using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace RGDSCapture
{
    /// <summary>
    /// Receives raw PCM audio over RTP (rtpL16pay, port 5002) and plays
    /// it through the Windows audio device via NAudio.
    ///
    /// rtpL16pay (RFC 2586) sends big-endian signed-16-bit interleaved
    /// stereo PCM directly in the RTP payload — no codec required.
    /// We just strip the RTP header, swap each sample from big-endian to
    /// little-endian (which Windows / NAudio expects), and push to the
    /// BufferedWaveProvider.
    ///
    /// GStreamer pipeline on device:
    ///   alsasrc device=hw:0,0 !
    ///   audio/x-raw,format=S16BE,rate=48000,channels=2 !
    ///   rtpL16pay pt=96 !
    ///   udpsink host={HOST} port=5002 sync=false
    /// </summary>
    public sealed class AudioStreamReceiver : IDisposable
    {
        private readonly int _port;
        private UdpClient?  _udp;
        private CancellationTokenSource? _cts;
        private Task?       _receiveTask;

        private IWavePlayer?          _waveOut;
        private BufferedWaveProvider? _waveBuffer;

        // S16 stereo @ 48 kHz — matches the GStreamer caps and rtpL16pay output
        private static readonly WaveFormat AudioFormat = new WaveFormat(48000, 16, 2);

        public bool IsRunning    { get; private set; }
        public bool AudioWorking { get; private set; }

        private float _volume = 1.0f;
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_waveOut != null) _waveOut.Volume = _volume;
            }
        }

        public AudioStreamReceiver(int port) => _port = port;

        // ─────────────────────────────────────────────────────────────
        // START
        // ─────────────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;

            // Audio output init — failure is non-fatal; video must still work.
            TryInitAudio();

            _udp = new UdpClient(_port);
            _udp.Client.ReceiveBufferSize = 1 * 1024 * 1024;

            _cts      = new CancellationTokenSource();
            IsRunning = true;

            _receiveTask = Task.Factory.StartNew(
                ReceiveLoop,
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        // ─────────────────────────────────────────────────────────────
        // STOP
        // ─────────────────────────────────────────────────────────────
        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();
            _udp?.Close();
            _receiveTask?.Wait(2000);

            try { _waveOut?.Stop();    } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut    = null;
            _waveBuffer = null;
        }

        // ─────────────────────────────────────────────────────────────
        // RECEIVE LOOP
        // ─────────────────────────────────────────────────────────────
        private void ReceiveLoop()
        {
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (IsRunning)
            {
                try
                {
                    byte[] data = _udp!.Receive(ref remoteEp);
                    if (data.Length < 12) continue;

                    // ── Strip RTP header ──────────────────────────
                    int  cc        = data[0] & 0x0F;
                    int  headerLen = 12 + (cc * 4);
                    bool hasExt    = (data[0] & 0x10) != 0;
                    if (hasExt && data.Length > headerLen + 4)
                    {
                        int extLen  = (data[headerLen + 2] << 8) | data[headerLen + 3];
                        headerLen  += 4 + (extLen * 4);
                    }
                    if (data.Length <= headerLen) continue;

                    int payloadLen = data.Length - headerLen;
                    if (payloadLen < 2) continue;

                    // ── Swap big-endian → little-endian ───────────
                    // rtpL16pay sends S16BE; NAudio / Windows expects S16LE.
                    // Swap every pair of bytes in-place.
                    // payloadLen is always even (16-bit samples), but guard anyway.
                    int swapEnd = headerLen + (payloadLen & ~1);   // round down to even
                    for (int i = headerLen; i < swapEnd; i += 2)
                    {
                        byte hi   = data[i];
                        data[i]   = data[i + 1];
                        data[i + 1] = hi;
                    }

                    if (AudioWorking && _waveBuffer != null)
                        _waveBuffer.AddSamples(data, headerLen, payloadLen & ~1);
                }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio recv error: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // NAUDIO INIT
        // ─────────────────────────────────────────────────────────────
        private void TryInitAudio()
        {
            // Attempt 1: WasapiOut — lowest latency on modern Windows
            try
            {
                _waveBuffer = new BufferedWaveProvider(AudioFormat)
                {
                    BufferDuration          = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true
                };

                var wasapi = new NAudio.Wave.WasapiOut(
    NAudio.CoreAudioApi.AudioClientShareMode.Shared, 100);
                wasapi.Init(_waveBuffer);
                wasapi.Play();

                _waveOut     = wasapi;
                AudioWorking = true;
                System.Diagnostics.Debug.WriteLine("Audio: WasapiOut initialised (L16 PCM, 48 kHz stereo).");
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio: WasapiOut failed: {ex.Message}");
                _waveOut    = null;
                _waveBuffer = null;
            }

            // Attempt 2: WaveOutEvent — legacy Windows audio, very compatible
            try
            {
                _waveBuffer = new BufferedWaveProvider(AudioFormat)
                {
                    BufferDuration          = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true
                };

                var waveOut = new WaveOutEvent { DesiredLatency = 150 };
                waveOut.Init(_waveBuffer);
                waveOut.Play();

                _waveOut     = waveOut;
                AudioWorking = true;
                System.Diagnostics.Debug.WriteLine("Audio: WaveOutEvent initialised (L16 PCM, 48 kHz stereo).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Audio: WaveOutEvent failed: {ex.Message}");
                _waveOut     = null;
                _waveBuffer  = null;
                AudioWorking = false;
            }
        }

        public void Dispose() => Stop();
    }
}
