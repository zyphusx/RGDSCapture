using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Receives H.264-over-RTP on a UDP port, reassembles NAL units
    /// (single, STAP-A and FU-A), decodes them with FFmpeg and raises
    /// <see cref="FrameReady"/> with a BGRA pixel buffer.
    ///
    /// Reliability notes:
    ///  - RTP sequence numbers are tracked; a gap during FU-A reassembly
    ///    discards the partial fragment instead of feeding a corrupt NAL
    ///    to the decoder.
    ///  - The BGRA output buffer is reused between frames, so steady-state
    ///    decoding allocates nothing. <see cref="FrameReady"/> handlers must
    ///    copy the data before returning.
    ///  - <see cref="NalUnitReceived"/> exposes every reassembled Annex-B NAL
    ///    (a fresh array per NAL) so a recorder can remux the stream without
    ///    re-encoding and without fighting over the UDP port.
    /// </summary>
    public sealed class RtpStreamReceiver : IDisposable
    {
        /// <summary>BGRA buffer (reused — copy before returning), width, height.</summary>
        public event Action<byte[], int, int>? FrameReady;

        /// <summary>A complete Annex-B NAL unit including its 4-byte start code.</summary>
        public event Action<byte[]>? NalUnitReceived;

        public int Port { get; }
        public bool IsRunning { get; private set; }
        public float CurrentFps { get; private set; }

        private long _lastFrameTicksUtc;

        /// <summary>UTC time of the most recently decoded frame, or MinValue if none yet.</summary>
        public DateTime LastFrameUtc => new(Interlocked.Read(ref _lastFrameTicksUtc), DateTimeKind.Utc);

        public bool HasReceivedFrame => Interlocked.Read(ref _lastFrameTicksUtc) != 0;

        private UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;

        // FU-A reassembly
        private readonly MemoryStream _fua = new(64 * 1024);
        private bool _fuaActive;
        private ushort _lastSeq;
        private bool _haveLastSeq;

        // FFmpeg decode contexts (unsafe interop pointers)
        private unsafe AVCodecContext* _codecCtx = null;
        private unsafe AVFrame* _frame = null;
        private unsafe AVFrame* _frameBgra = null;
        private unsafe SwsContext* _swsCtx = null;
        private unsafe AVPacket* _packet = null;
        private bool _ffmpegReady;
        private int _bgraWidth, _bgraHeight;
        private byte[]? _bgraBuffer;

        // FPS window
        private long _fpsFrameCount;
        private DateTime _fpsWindowStart = DateTime.UtcNow;

        // Network stats (written on the receive thread, read from the UI)
        private long _statPackets;
        private long _statLostPackets;
        private long _statBytes;

        /// <summary>Cumulative packet/loss/byte counters since Start().</summary>
        public (long Packets, long Lost, long Bytes) GetStats() => (
            Interlocked.Read(ref _statPackets),
            Interlocked.Read(ref _statLostPackets),
            Interlocked.Read(ref _statBytes));

        private const int MaxConsecutiveSendErrors = 3;
        private int _sendErrorCount;

        public RtpStreamReceiver(int port) => Port = port;

        // ─────────────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;

            FFmpegLoader.EnsureRegistered();
            InitDecoder();

            _udp = new UdpClient(Port);
            _udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;

            Interlocked.Exchange(ref _lastFrameTicksUtc, 0);
            CurrentFps = 0f;
            _haveLastSeq = false;
            _fuaActive = false;
            _sendErrorCount = 0;
            Interlocked.Exchange(ref _statPackets, 0);
            Interlocked.Exchange(ref _statLostPackets, 0);
            Interlocked.Exchange(ref _statBytes, 0);

            _cts = new CancellationTokenSource();
            IsRunning = true;

            _receiveTask = Task.Factory.StartNew(
                ReceiveLoop, _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // Closing the socket unblocks the synchronous Receive call.
            _udp?.Close();
            _udp = null;

            _receiveTask?.Wait(2000);
            _receiveTask = null;

            FreeDecoder();
            CurrentFps = 0f;
        }

        public void Dispose() => Stop();

        // ─────────────────────────────────────────────────────────────
        // RECEIVE / DEPACKETIZE
        // ─────────────────────────────────────────────────────────────
        private void ReceiveLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);

            while (IsRunning)
            {
                byte[] data;
                try
                {
                    data = _udp!.Receive(ref remote);
                }
                catch (SocketException) { break; }   // socket closed by Stop()
                catch (ObjectDisposedException) { break; }
                catch (NullReferenceException) { break; }

                try
                {
                    ProcessRtpPacket(data);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RTP:{Port}] {ex.Message}");
                }
            }
        }

        private void ProcessRtpPacket(byte[] data)
        {
            if (data.Length < 12) return;

            Interlocked.Increment(ref _statPackets);
            Interlocked.Add(ref _statBytes, data.Length);

            int cc = data[0] & 0x0F;
            bool hasExt = (data[0] & 0x10) != 0;
            int headerLen = 12 + cc * 4;

            ushort seq = (ushort)((data[2] << 8) | data[3]);
            if (_haveLastSeq)
            {
                int delta = unchecked((ushort)(seq - _lastSeq));
                if (delta != 1)
                {
                    // Packet loss or reorder: any in-flight fragment is now corrupt.
                    _fuaActive = false;

                    // Small forward gaps are genuine loss; huge jumps are a
                    // sequence reset (device pipeline restart), not loss.
                    int lost = delta - 1;
                    if (lost > 0 && lost < 200)
                        Interlocked.Add(ref _statLostPackets, lost);
                }
            }
            _lastSeq = seq;
            _haveLastSeq = true;

            if (hasExt && data.Length > headerLen + 4)
            {
                int extWords = (data[headerLen + 2] << 8) | data[headerLen + 3];
                headerLen += 4 + extWords * 4;
            }
            if (data.Length <= headerLen) return;

            byte nalHeader = data[headerLen];
            int nalType = nalHeader & 0x1F;

            if (nalType >= 1 && nalType <= 23)
            {
                // Single NAL unit packet
                int payLen = data.Length - headerLen;
                var nal = new byte[payLen + 4];
                WriteStartCode(nal);
                Buffer.BlockCopy(data, headerLen, nal, 4, payLen);
                EmitNal(nal);
            }
            else if (nalType == 24)
            {
                // STAP-A: several NALs packed in one RTP packet
                int offset = headerLen + 1;
                while (offset + 2 < data.Length)
                {
                    int size = (data[offset] << 8) | data[offset + 1];
                    offset += 2;
                    if (size <= 0 || offset + size > data.Length) break;

                    var nal = new byte[size + 4];
                    WriteStartCode(nal);
                    Buffer.BlockCopy(data, offset, nal, 4, size);
                    EmitNal(nal);
                    offset += size;
                }
            }
            else if (nalType == 28 || nalType == 29)
            {
                // FU-A / FU-B fragmented NAL unit
                if (data.Length < headerLen + 2) return;

                byte fuHeader = data[headerLen + 1];
                bool fuStart = (fuHeader & 0x80) != 0;
                bool fuEnd = (fuHeader & 0x40) != 0;
                byte fuNalType = (byte)(fuHeader & 0x1F);

                int payloadStart = headerLen + 2;
                int payloadLen = data.Length - payloadStart;
                if (payloadLen <= 0) return;

                if (fuStart)
                {
                    byte reconstructed = (byte)((nalHeader & 0x60) | fuNalType);
                    _fua.SetLength(0);
                    _fua.WriteByte(0x00);
                    _fua.WriteByte(0x00);
                    _fua.WriteByte(0x00);
                    _fua.WriteByte(0x01);
                    _fua.WriteByte(reconstructed);
                    _fuaActive = true;
                }

                if (!_fuaActive) return;

                _fua.Write(data, payloadStart, payloadLen);

                if (fuEnd)
                {
                    EmitNal(_fua.ToArray());
                    _fuaActive = false;
                }
            }
        }

        private void EmitNal(byte[] annexBNal)
        {
            NalUnitReceived?.Invoke(annexBNal);
            DecodeNal(annexBNal);
        }

        private static void WriteStartCode(byte[] buf)
        {
            buf[0] = 0x00;
            buf[1] = 0x00;
            buf[2] = 0x00;
            buf[3] = 0x01;
        }

        // ─────────────────────────────────────────────────────────────
        // DECODE
        // ─────────────────────────────────────────────────────────────
        private unsafe void DecodeNal(byte[] annexBNal)
        {
            if (!_ffmpegReady) return;

            fixed (byte* pData = annexBNal)
            {
                _packet->data = pData;
                _packet->size = annexBNal.Length;

                if (ffmpeg.avcodec_send_packet(_codecCtx, _packet) < 0)
                {
                    if (++_sendErrorCount >= MaxConsecutiveSendErrors)
                    {
                        ffmpeg.avcodec_flush_buffers(_codecCtx);
                        _sendErrorCount = 0;
                    }
                    return;
                }
                _sendErrorCount = 0;

                while (true)
                {
                    int rc = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rc == ffmpeg.AVERROR_EOF)
                        break;
                    if (rc < 0)
                    {
                        ffmpeg.avcodec_flush_buffers(_codecCtx);
                        break;
                    }

                    int w = _frame->width;
                    int h = _frame->height;
                    if (w <= 0 || h <= 0)
                    {
                        ffmpeg.av_frame_unref(_frame);
                        break;
                    }

                    _swsCtx = ffmpeg.sws_getCachedContext(
                        _swsCtx,
                        w, h, (AVPixelFormat)_frame->format,
                        w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                        ffmpeg.SWS_BILINEAR, null, null, null);

                    if (w != _bgraWidth || h != _bgraHeight)
                    {
                        ffmpeg.av_frame_unref(_frameBgra);
                        _frameBgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                        _frameBgra->width = w;
                        _frameBgra->height = h;
                        // align=1 → linesize is exactly w*4, matching WriteableBitmap stride
                        ffmpeg.av_frame_get_buffer(_frameBgra, 1);
                        _bgraWidth = w;
                        _bgraHeight = h;
                        _bgraBuffer = new byte[w * 4 * h];
                    }

                    ffmpeg.sws_scale(
                        _swsCtx,
                        _frame->data, _frame->linesize, 0, h,
                        _frameBgra->data, _frameBgra->linesize);

                    int byteCount = w * 4 * h;
                    fixed (byte* pDst = _bgraBuffer)
                        Buffer.MemoryCopy(_frameBgra->data[0], pDst, byteCount, byteCount);

                    var now = DateTime.UtcNow;
                    Interlocked.Exchange(ref _lastFrameTicksUtc, now.Ticks);

                    _fpsFrameCount++;
                    double elapsed = (now - _fpsWindowStart).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        CurrentFps = (float)(_fpsFrameCount / elapsed);
                        _fpsFrameCount = 0;
                        _fpsWindowStart = now;
                    }

                    FrameReady?.Invoke(_bgraBuffer!, w, h);
                    ffmpeg.av_frame_unref(_frame);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // DECODER LIFECYCLE
        // ─────────────────────────────────────────────────────────────
        private unsafe void InitDecoder()
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException("H.264 decoder not found in FFmpeg binaries.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            _codecCtx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            _codecCtx->error_concealment = ffmpeg.FF_EC_GUESS_MVS | ffmpeg.FF_EC_DEBLOCK;
            _codecCtx->thread_count = 2;

            if (ffmpeg.avcodec_open2(_codecCtx, codec, null) < 0)
                throw new InvalidOperationException("Failed to open H.264 codec context.");

            _frame = ffmpeg.av_frame_alloc();
            _frameBgra = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            _bgraWidth = _bgraHeight = 0;

            _ffmpegReady = true;
        }

        private unsafe void FreeDecoder()
        {
            if (!_ffmpegReady) return;
            _ffmpegReady = false;

            fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
            fixed (AVFrame** pp = &_frame) ffmpeg.av_frame_free(pp);
            fixed (AVFrame** pp = &_frameBgra) ffmpeg.av_frame_free(pp);
            fixed (AVPacket** pp = &_packet) ffmpeg.av_packet_free(pp);

            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }
        }
    }
}
