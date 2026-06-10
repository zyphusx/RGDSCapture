using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RGDSCapture.Services
{
    /// <summary>One ffmpeg input: its demuxer arguments and a start offset.</summary>
    /// <param name="FormatArgs">Input options placed before -i (e.g. "-f h264 -framerate 30").</param>
    /// <param name="OffsetSeconds">-itsoffset applied to this input for track alignment.</param>
    public sealed record MuxInput(string FormatArgs, double OffsetSeconds);

    /// <summary>Shared ffmpeg argument fragments.</summary>
    public static class FfmpegArgs
    {
        /// <summary>
        /// setts bitstream filter forcing exact 30 fps CFR timestamps on a
        /// video output stream, with an optional alignment offset folded in.
        /// Necessary because the raw h264 demuxer trusts SPS VUI timing when
        /// present, which can mis-time the stream regardless of -framerate
        /// (verified against the shipped ffmpeg). Note setts overrides
        /// -itsoffset, so the offset must live inside the expression.
        /// </summary>
        public static string CfrSetts(int videoStreamIndex, double offsetSeconds)
        {
            string expr = "N/(30*TB)";
            if (offsetSeconds > 0.0005)
                expr += "+" + offsetSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "/TB";
            return $"-bsf:v:{videoStreamIndex} setts=ts={expr} ";
        }
    }

    /// <summary>
    /// Runs one ffmpeg process with N inputs fed through Windows named pipes.
    /// stdin can only carry a single stream, so multi-track output (two video
    /// tracks + audio in one MP4) requires a pipe per input. Each input gets a
    /// bounded queue drained by its own writer task, so producers never block
    /// the receive/decode threads.
    /// </summary>
    public sealed class FfmpegPipeMuxer : IDisposable
    {
        /// <summary>Raised (background thread) if ffmpeg exits before Complete.</summary>
        public event Action? Failed;

        public string OutputFile { get; }

        private sealed class Feed
        {
            public NamedPipeServerStream Server = null!;
            public readonly BlockingCollection<byte[]> Queue = new(boundedCapacity: 8192);
            public Task Writer = Task.CompletedTask;
        }

        private readonly Process _proc;
        private readonly Feed[] _feeds;
        private readonly CancellationTokenSource _connectCts = new();
        private volatile bool _stopping;

        public FfmpegPipeMuxer(
            string outputFile,
            IReadOnlyList<MuxInput> inputs,
            string outputArgs,
            Action<string, bool> log)
        {
            OutputFile = outputFile;
            _feeds = new Feed[inputs.Count];

            var names = new string[inputs.Count];
            var args = new StringBuilder("-hide_banner -loglevel error ");
            for (int i = 0; i < inputs.Count; i++)
            {
                names[i] = $"rgds_{Guid.NewGuid():N}";
                if (inputs[i].OffsetSeconds > 0.0005)
                    args.Append("-itsoffset ")
                        .Append(inputs[i].OffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append(' ');
                args.Append(inputs[i].FormatArgs)
                    .Append(" -i \\\\.\\pipe\\").Append(names[i]).Append(' ');
            }
            args.Append(outputArgs).Append(" -y \"").Append(outputFile).Append('"');

            for (int i = 0; i < inputs.Count; i++)
                _feeds[i] = new Feed
                {
                    Server = new NamedPipeServerStream(
                        names[i], PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                        inBufferSize: 0, outBufferSize: 1 << 20)
                };

            var psi = new ProcessStartInfo
            {
                FileName = Core.AppPaths.FfmpegExe,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            _proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            _ = Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = _proc.StandardError.ReadLine()) != null)
                        log($"[MUX] ffmpeg: {line}", true);
                }
                catch { }
            });

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            {
                // Unblock writers still waiting for a pipe connection.
                try { _connectCts.Cancel(); } catch { }
                if (!_stopping) Failed?.Invoke();
            };

            foreach (var feed in _feeds)
            {
                var f = feed;
                f.Writer = Task.Run(async () =>
                {
                    try
                    {
                        await f.Server.WaitForConnectionAsync(_connectCts.Token);
                        foreach (var chunk in f.Queue.GetConsumingEnumerable())
                            f.Server.Write(chunk, 0, chunk.Length);
                        // Critical: disposing a pipe server discards anything the
                        // client hasn't read yet. Drain before closing or the tail
                        // of the recording is silently lost.
                        f.Server.WaitForPipeDrain();
                    }
                    catch
                    {
                        // Cancelled connection wait or broken pipe (ffmpeg died);
                        // the Exited handler reports the failure.
                    }
                    finally
                    {
                        try { f.Server.Dispose(); } catch { }
                    }
                });
            }
        }

        /// <summary>Non-blocking enqueue for live capture paths; drops under backpressure.</summary>
        public bool TryWrite(int input, byte[] data)
            => !_feeds[input].Queue.IsAddingCompleted && _feeds[input].Queue.TryAdd(data);

        /// <summary>Blocking enqueue for bulk writes from background tasks.</summary>
        public void Write(int input, byte[] data)
        {
            try { _feeds[input].Queue.Add(data); }
            catch (InvalidOperationException) { }   // completed during shutdown
        }

        /// <summary>
        /// Closes all inputs, lets ffmpeg finalize the MP4 and waits for exit.
        /// Returns true if ffmpeg exited cleanly.
        /// </summary>
        public async Task<bool> CompleteAsync(int timeoutMs)
        {
            _stopping = true;
            foreach (var f in _feeds) f.Queue.CompleteAdding();
            await Task.WhenAll(_feeds.Select(f => f.Writer));

            return await Task.Run(() =>
            {
                if (!_proc.WaitForExit(timeoutMs))
                {
                    try
                    {
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(2000);
                    }
                    catch { }
                    return false;
                }
                return _proc.ExitCode == 0;
            });
        }

        public void Dispose()
        {
            _stopping = true;
            try { _connectCts.Cancel(); } catch { }
            foreach (var f in _feeds)
            {
                try { f.Queue.CompleteAdding(); } catch { }
            }
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
            _proc.Dispose();
            foreach (var f in _feeds)
            {
                try { f.Server.Dispose(); } catch { }
                f.Queue.Dispose();
            }
            _connectCts.Dispose();
        }
    }
}
