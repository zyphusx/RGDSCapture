using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace RGDSCapture
{
    /// <summary>
    /// Receives H.264-over-RTP on a UDP port, decodes each NAL unit via
    /// FFmpeg, and fires FrameReady with a raw BGRA pixel buffer.
    /// Also tracks live FPS and exposes freeze detection.
    /// </summary>
    public sealed class RtpStreamReceiver : IDisposable
    {
        // ── Public API ────────────────────────────────────────────────
        public event Action<byte[], int, int>? FrameReady;

        public bool     IsRunning  { get; private set; }
        public float    CurrentFps { get; private set; }
        public DateTime LastFrameTime { get; private set; } = DateTime.MinValue;

        public bool IsFrozen =>
            IsRunning &&
            LastFrameTime != DateTime.MinValue &&
            (DateTime.Now - LastFrameTime).TotalSeconds >= FreezeThresholdSeconds;

        public double FreezeThresholdSeconds { get; set; } = 5.0;

        // ── Private state ─────────────────────────────────────────────
        private readonly int  _port;
        private UdpClient?    _udp;
        private CancellationTokenSource? _cts;
        private Task?         _receiveTask;

        // FU-A reassembly buffer
        private readonly List<byte> _fuaBuffer = new();
        private bool _fuaStarted = false;

        // FFmpeg context (all unsafe pointers)
        private unsafe AVCodecContext* _codecCtx  = null;
        private unsafe AVFrame*        _frame     = null;
        private unsafe AVFrame*        _frameRgb  = null;
        private unsafe SwsContext*     _swsCtx    = null;
        private unsafe AVPacket*       _packet    = null;
        private bool _ffmpegInitialised = false;
        private int  _frameRgbWidth  = 0;
        private int  _frameRgbHeight = 0;

        // FPS tracking
        private long     _fpsFrameCount = 0;
        private DateTime _fpsWindowStart = DateTime.UtcNow;

        // Decoder error recovery
        private const int MaxDecodeErrors = 3;
        private int _decodeErrorCount = 0;

        public RtpStreamReceiver(int port) => _port = port;

        // ─────────────────────────────────────────────────────────────
        // START / STOP
        // ─────────────────────────────────────────────────────────────
        public void Start()
        {
            if (IsRunning) return;

            FFmpegBinariesHelper.RegisterFFmpegBinaries();
            InitialiseFFmpeg();

            _udp = new UdpClient(_port);
            _udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;

            _cts          = new CancellationTokenSource();
            IsRunning     = true;
            CurrentFps    = 0f;
            LastFrameTime = DateTime.MinValue;

            _receiveTask = Task.Factory.StartNew(
                ReceiveLoop,
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _udp?.Close();
            _receiveTask?.Wait(2000);
            _receiveTask = null;
            FreeFFmpeg();
            CurrentFps = 0f;
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

                    // Parse RTP fixed header
                    int  cc        = data[0] & 0x0F;
                    bool hasExt    = (data[0] & 0x10) != 0;
                    int  headerLen = 12 + (cc * 4);

                    // Skip optional extension header
                    if (hasExt && data.Length > headerLen + 4)
                    {
                        int extLen  = (data[headerLen + 2] << 8) | data[headerLen + 3];
                        headerLen  += 4 + (extLen * 4);
                    }
                    if (data.Length <= headerLen) continue;

                    byte nalHeader = data[headerLen];
                    int  nalType   = nalHeader & 0x1F;

                    if (nalType >= 1 && nalType <= 23)
                    {
                        // Single NAL unit packet
                        int    payLen = data.Length - headerLen;
                        byte[] nal    = new byte[payLen + 4];
                        WriteStartCode(nal, 0);
                        Buffer.BlockCopy(data, headerLen, nal, 4, payLen);
                        DecodeNal(nal);
                    }
                    else if (nalType == 24)
                    {
                        // STAP-A: multiple NALs in one packet
                        int offset = headerLen + 1;
                        while (offset + 2 < data.Length)
                        {
                            int size = (data[offset] << 8) | data[offset + 1];
                            offset += 2;
                            if (size <= 0 || offset + size > data.Length) break;
                            byte[] nal = new byte[size + 4];
                            WriteStartCode(nal, 0);
                            Buffer.BlockCopy(data, offset, nal, 4, size);
                            DecodeNal(nal);
                            offset += size;
                        }
                    }
                    else if (nalType == 28 || nalType == 29)
                    {
                        // FU-A / FU-B: fragmented NAL unit
                        if (data.Length < headerLen + 2) continue;

                        byte fuHeader  = data[headerLen + 1];
                        bool fuStart   = (fuHeader & 0x80) != 0;
                        bool fuEnd     = (fuHeader & 0x40) != 0;
                        byte fuNalType = (byte)(fuHeader & 0x1F);

                        int payloadStart = headerLen + 2;
                        int payloadLen   = data.Length - payloadStart;
                        if (payloadLen <= 0) continue;

                        if (fuStart)
                        {
                            // Reconstruct the original NAL header from the FU indicator
                            byte reconstitutedNalHdr = (byte)((nalHeader & 0x60) | fuNalType);
                            _fuaBuffer.Clear();
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x01);
                            _fuaBuffer.Add(reconstitutedNalHdr);
                            _fuaStarted = true;
                        }

                        if (!_fuaStarted) continue;

                        for (int i = payloadStart; i < data.Length; i++)
                            _fuaBuffer.Add(data[i]);

                        if (fuEnd)
                        {
                            DecodeNal(_fuaBuffer.ToArray());
                            _fuaBuffer.Clear();
                            _fuaStarted = false;
                        }
                    }
                }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[RTP:{_port}] Receive error: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // DECODE
        // ─────────────────────────────────────────────────────────────
        private unsafe void DecodeNal(byte[] annexBData)
        {
            if (!_ffmpegInitialised) return;

            fixed (byte* pData = annexBData)
            {
                _packet->data = pData;
                _packet->size = annexBData.Length;

                int sendResult = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
                if (sendResult < 0)
                {
                    if (++_decodeErrorCount >= MaxDecodeErrors)
                    {
                        ffmpeg.avcodec_flush_buffers(_codecCtx);
                        _decodeErrorCount = 0;
                        System.Diagnostics.Debug.WriteLine(
                            $"[RTP:{_port}] Decoder flushed after {MaxDecodeErrors} send errors.");
                    }
                    return;
                }

                _decodeErrorCount = 0;

                while (true)
                {
                    int recvResult = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (recvResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                        recvResult == ffmpeg.AVERROR_EOF)
                        break;

                    if (recvResult < 0)
                    {
                        ffmpeg.avcodec_flush_buffers(_codecCtx);
                        break;
                    }

                    int w = _frame->width;
                    int h = _frame->height;
                    if (w <= 0 || h <= 0) { ffmpeg.av_frame_unref(_frame); break; }

                    _swsCtx = ffmpeg.sws_getCachedContext(
                        _swsCtx,
                        w, h, (AVPixelFormat)_frame->format,
                        w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                        ffmpeg.SWS_BILINEAR,
                        null, null, null);

                    if (w != _frameRgbWidth || h != _frameRgbHeight)
                    {
                        ffmpeg.av_frame_unref(_frameRgb);
                        _frameRgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                        _frameRgb->width  = w;
                        _frameRgb->height = h;
                        ffmpeg.av_frame_get_buffer(_frameRgb, 1);
                        _frameRgbWidth  = w;
                        _frameRgbHeight = h;
                    }

                    ffmpeg.sws_scale(
                        _swsCtx,
                        _frame->data, _frame->linesize, 0, h,
                        _frameRgb->data, _frameRgb->linesize);

                    int    stride  = _frameRgb->linesize[0];
                    int    bufSize = stride * h;
                    byte[] buffer  = new byte[bufSize];

                    fixed (byte* pDst = buffer)
                        Buffer.MemoryCopy(_frameRgb->data[0], pDst, bufSize, bufSize);

                    // FPS tracking — use a single UtcNow snapshot per frame
                    // to avoid the subtle drift from calling DateTime.Now twice.
                    var now = DateTime.UtcNow;
                    _fpsFrameCount++;
                    LastFrameTime = now;

                    double elapsed = (now - _fpsWindowStart).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        CurrentFps      = (float)(_fpsFrameCount / elapsed);
                        _fpsFrameCount  = 0;
                        _fpsWindowStart = now;
                    }

                    FrameReady?.Invoke(buffer, w, h);
                    ffmpeg.av_frame_unref(_frame);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // FFMPEG INIT / FREE
        // ─────────────────────────────────────────────────────────────
        private unsafe void InitialiseFFmpeg()
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException(
                    "H.264 decoder not found in FFmpeg binaries.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            _codecCtx->flags  |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            _codecCtx->error_concealment = ffmpeg.FF_EC_GUESS_MVS | ffmpeg.FF_EC_DEBLOCK;
            _codecCtx->skip_frame        = AVDiscard.AVDISCARD_DEFAULT;
            _codecCtx->thread_count      = 2;

            if (ffmpeg.avcodec_open2(_codecCtx, codec, null) < 0)
                throw new InvalidOperationException("Failed to open H.264 codec context.");

            _frame    = ffmpeg.av_frame_alloc();
            _frameRgb = ffmpeg.av_frame_alloc();
            _packet   = ffmpeg.av_packet_alloc();

            _ffmpegInitialised = true;
        }

        private unsafe void FreeFFmpeg()
        {
            if (!_ffmpegInitialised) return;
            _ffmpegInitialised = false;

            fixed (AVCodecContext** pp = &_codecCtx) ffmpeg.avcodec_free_context(pp);
            fixed (AVFrame**        pp = &_frame)    ffmpeg.av_frame_free(pp);
            fixed (AVFrame**        pp = &_frameRgb) ffmpeg.av_frame_free(pp);
            fixed (AVPacket**       pp = &_packet)   ffmpeg.av_packet_free(pp);

            if (_swsCtx != null)
            {
                ffmpeg.sws_freeContext(_swsCtx);
                _swsCtx = null;
            }
        }

        private static void WriteStartCode(byte[] buf, int offset)
        {
            buf[offset]     = 0x00;
            buf[offset + 1] = 0x00;
            buf[offset + 2] = 0x00;
            buf[offset + 3] = 0x01;
        }

        public void Dispose() => Stop();
    }
}
