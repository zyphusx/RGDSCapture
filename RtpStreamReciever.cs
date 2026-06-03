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
    /// Receives H.264-over-RTP, decodes via FFmpeg, fires FrameReady.
    /// Now also exposes live FPS, last-frame timestamp, and freeze detection.
    /// Audio removed entirely — use 3.5mm jack to host machine + OBS.
    /// </summary>
    public sealed class RtpStreamReceiver : IDisposable
    {
        // ── Public API ────────────────────────────────────────────────
        public event Action<byte[], int, int>? FrameReady;
        public bool   IsRunning   { get; private set; }
        public float  CurrentFps  { get; private set; }
        public DateTime LastFrameTime { get; private set; } = DateTime.MinValue;

        /// <summary>True if no frame received for FreezeThreshold seconds.</summary>
        public bool IsFrozen =>
            IsRunning &&
            LastFrameTime != DateTime.MinValue &&
            (DateTime.Now - LastFrameTime).TotalSeconds >= FreezeThresholdSeconds;

        public double FreezeThresholdSeconds { get; set; } = 5.0;

        // ── Private state ─────────────────────────────────────────────
        private readonly int _port;
        private UdpClient?   _udp;
        private CancellationTokenSource? _cts;
        private Task?        _receiveTask;

        private readonly List<byte> _fuaBuffer  = new();
        private bool  _fuaStarted  = false;
        private byte  _fuaNalType  = 0;

        private unsafe AVCodecContext* _codecCtx  = null;
        private unsafe AVFrame*        _frame     = null;
        private unsafe AVFrame*        _frameRgb  = null;
        private unsafe SwsContext*     _swsCtx    = null;
        private unsafe AVPacket*       _packet    = null;
        private bool   _ffmpegInitialised = false;

        private int  _frameRgbWidth  = 0;
        private int  _frameRgbHeight = 0;

        // FPS tracking
        private long     _fpsFrameCount = 0;
        private DateTime _fpsTimer      = DateTime.Now;

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

            _cts      = new CancellationTokenSource();
            IsRunning = true;
            CurrentFps = 0f;
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
            _udp?.Close();
            _receiveTask?.Wait(2000);
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

                    int  cc        = data[0] & 0x0F;
                    bool hasExt    = (data[0] & 0x10) != 0;
                    int  headerLen = 12 + (cc * 4);

                    if (hasExt && data.Length > headerLen + 4)
                    {
                        int extLen  = ((data[headerLen + 2] << 8) | data[headerLen + 3]);
                        headerLen  += 4 + (extLen * 4);
                    }
                    if (data.Length <= headerLen) continue;

                    byte nalHeader = data[headerLen];
                    int  nalType   = nalHeader & 0x1F;

                    if (nalType >= 1 && nalType <= 23)
                    {
                        int    payLen = data.Length - headerLen;
                        byte[] nal    = new byte[payLen + 4];
                        WriteStartCode(nal, 0);
                        Buffer.BlockCopy(data, headerLen, nal, 4, payLen);
                        DecodeNal(nal);
                    }
                    else if (nalType == 24)
                    {
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
                            byte reconstitutedNalHdr = (byte)((nalHeader & 0x60) | fuNalType);
                            _fuaBuffer.Clear();
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x00);
                            _fuaBuffer.Add(0x01);
                            _fuaBuffer.Add(reconstitutedNalHdr);
                            _fuaStarted = true;
                            _fuaNalType = fuNalType;
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
                        $"RTP recv error port {_port}: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // DECODE
        // ─────────────────────────────────────────────────────────────
        // Consecutive decode errors before we flush and request a keyframe
        private const int MaxDecodeErrors = 3;
        private int _decodeErrorCount = 0;

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
                    _decodeErrorCount++;
                    if (_decodeErrorCount >= MaxDecodeErrors)
                    {
                        // Flush the decoder — forces it to discard damaged reference
                        // frames and wait cleanly for the next IDR keyframe.
                        ffmpeg.avcodec_flush_buffers(_codecCtx);
                        _decodeErrorCount = 0;
                        System.Diagnostics.Debug.WriteLine(
                            $"[Port {_port}] Decoder flushed after {MaxDecodeErrors} errors — waiting for IDR");
                    }
                    return;
                }

                // Reset error counter on successful send
                _decodeErrorCount = 0;

                while (true)
                {
                    int recvResult = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (recvResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                        recvResult == ffmpeg.AVERROR_EOF)
                        break;
                    if (recvResult < 0)
                    {
                        // Receive error — flush and recover
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

                    // Update FPS + last-frame timestamp
                    _fpsFrameCount++;
                    LastFrameTime = DateTime.Now;
                    var elapsed = (DateTime.Now - _fpsTimer).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        CurrentFps     = (float)(_fpsFrameCount / elapsed);
                        _fpsFrameCount = 0;
                        _fpsTimer      = DateTime.Now;
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
                throw new Exception("H.264 decoder not found in FFmpeg binaries.");

            _codecCtx = ffmpeg.avcodec_alloc_context3(codec);

            // Low-delay decode
            _codecCtx->flags  |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

            // Error concealment — fills damaged macroblocks with interpolated data
            // instead of leaving visible green/grey corruption blocks.
            // GUESS_MVS:  estimate motion vectors for damaged areas
            // DEBLOCK:    apply deblocking filter over concealed regions
            _codecCtx->error_concealment = ffmpeg.FF_EC_GUESS_MVS | ffmpeg.FF_EC_DEBLOCK;

            // Skip corrupted frames entirely rather than displaying garbage.
            // AVDISCARD_DEFAULT skips frames marked as corrupted by the decoder.
            _codecCtx->skip_frame = AVDiscard.AVDISCARD_DEFAULT;

            _codecCtx->thread_count = 2;

            if (ffmpeg.avcodec_open2(_codecCtx, codec, null) < 0)
                throw new Exception("Failed to open H.264 codec context.");

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
