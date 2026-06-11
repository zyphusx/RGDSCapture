using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Saves the contents of the two replay ring buffers as one MP4 with two
    /// video tracks. Pure remux (-c copy), so a 30-second save completes in
    /// well under a second.
    /// </summary>
    public static class ReplayService
    {
        private const string VideoInputArgs =
            "-thread_queue_size 1024 -fflags +genpts -framerate 30 -f h264";
        private const string AudioInputArgs =
            "-thread_queue_size 1024 -f s16le -ar 48000 -ac 2";

        public static async Task<string?> SaveAsync(
            ReplayBuffer top, ReplayBuffer bottom, AudioReplayBuffer? audio,
            int seconds, Action<string, bool> log)
        {
            if (!File.Exists(AppPaths.FfmpegExe))
            {
                log($"[REPLAY] ffmpeg.exe not found at {AppPaths.FfmpegExe}", true);
                return null;
            }

            var topClip = TrimToWindow(top.Snapshot(), seconds);
            var bottomClip = TrimToWindow(bottom.Snapshot(), seconds);

            if (topClip.Count == 0 || bottomClip.Count == 0)
            {
                log("[REPLAY] Not enough buffered video yet — wait a few seconds after connecting.", true);
                return null;
            }

            var baseUtc = topClip[0].TsUtc < bottomClip[0].TsUtc
                ? topClip[0].TsUtc : bottomClip[0].TsUtc;
            double topOffset = (topClip[0].TsUtc - baseUtc).TotalSeconds;
            double bottomOffset = (bottomClip[0].TsUtc - baseUtc).TotalSeconds;

            var lastTop = topClip[^1].TsUtc;
            var lastBottom = bottomClip[^1].TsUtc;
            var videoEndUtc = lastTop > lastBottom ? lastTop : lastBottom;
            double duration = (videoEndUtc - baseUtc).TotalSeconds;

            var (audioClip, audioOffset) = TrimAudio(audio, baseUtc, videoEndUtc);
            bool hasAudio = audioClip.Count > 0;

            Directory.CreateDirectory(AppPaths.RecordingsDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string outFile = Path.Combine(AppPaths.RecordingsDir, $"rg_replay_{ts}.mp4");

            // Video offsets ride in the setts expressions (setts overrides
            // -itsoffset); the audio input is encoded, so -itsoffset works.
            string outputArgs =
                "-map 0:v:0 -map 1:v:0 " +
                (hasAudio ? "-map 2:a:0 " : "") +
                "-c:v copy " +
                (hasAudio ? "-c:a aac -b:a 192k " : "") +
                FfmpegArgs.CfrSetts(0, topOffset) +
                FfmpegArgs.CfrSetts(1, bottomOffset) +
                "-metadata:s:v:0 handler_name=\"Top Screen\" " +
                "-metadata:s:v:1 handler_name=\"Bottom Screen\" " +
                "-movflags +faststart";

            var inputs = new List<MuxInput>
            {
                new(VideoInputArgs, 0),
                new(VideoInputArgs, 0)
            };
            if (hasAudio) inputs.Add(new MuxInput(AudioInputArgs, audioOffset));

            try
            {
                using var mux = new FfmpegPipeMuxer(outFile, inputs, outputArgs, log);

                // One task per input: ffmpeg interleaves packets by timestamp,
                // so feeding the streams sequentially could deadlock against
                // its muxing queue.
                var feeds = new List<Task>
                {
                    Task.Run(() => { foreach (var e in topClip) mux.Write(0, e.Nal); }),
                    Task.Run(() => { foreach (var e in bottomClip) mux.Write(1, e.Nal); })
                };
                if (hasAudio)
                    feeds.Add(Task.Run(() => { foreach (var c in audioClip) mux.Write(2, c); }));
                await Task.WhenAll(feeds);

                bool ok = await mux.CompleteAsync(timeoutMs: 30_000);
                if (!ok)
                {
                    log("[REPLAY] Save failed (ffmpeg did not exit cleanly).", true);
                    return null;
                }

                log($"[REPLAY] Saved last {duration:F0}s{(hasAudio ? " + audio" : "")} → {outFile}", false);
                return outFile;
            }
            catch (Exception ex)
            {
                log($"[REPLAY] Save failed: {ex.Message}", true);
                return null;
            }
        }

        /// <summary>
        /// Selects buffered PCM covering [baseUtc, videoEnd], trimming the
        /// first chunk to the sample so audio starts exactly at the video base.
        /// Returns the chunks plus the start offset (0 unless audio capture
        /// began after the video window started).
        /// </summary>
        private static (List<byte[]> Chunks, double Offset) TrimAudio(
            AudioReplayBuffer? audio, DateTime baseUtc, DateTime videoEndUtc)
        {
            var result = new List<byte[]>();
            if (audio == null) return (result, 0);

            double offset = 0;
            foreach (var chunk in audio.Snapshot())
            {
                double chunkDur = (double)chunk.Pcm.Length / AudioReplayBuffer.BytesPerSecond;
                if (chunk.TsUtc.AddSeconds(chunkDur) <= baseUtc) continue;
                if (chunk.TsUtc >= videoEndUtc) break;

                if (result.Count == 0)
                {
                    if (chunk.TsUtc < baseUtc)
                    {
                        long skip = (long)((baseUtc - chunk.TsUtc).TotalSeconds
                                           * AudioReplayBuffer.BytesPerSecond);
                        skip -= skip % 4;   // whole stereo frames
                        if (skip >= chunk.Pcm.Length) continue;
                        var rest = new byte[chunk.Pcm.Length - skip];
                        Buffer.BlockCopy(chunk.Pcm, (int)skip, rest, 0, rest.Length);
                        result.Add(rest);
                        continue;
                    }
                    offset = (chunk.TsUtc - baseUtc).TotalSeconds;
                }
                result.Add(chunk.Pcm);
            }
            return (result, offset);
        }

        /// <summary>
        /// Saves the last few seconds of both screens as a shareable animated
        /// GIF (stacked top over bottom, 15 fps, DS-native 256 px wide).
        /// Uses ffmpeg's palettegen/paletteuse for high-quality colors.
        /// </summary>
        public static async Task<string?> SaveGifAsync(
            ReplayBuffer top, ReplayBuffer bottom, int seconds,
            Action<string, bool> log)
        {
            if (!File.Exists(AppPaths.FfmpegExe))
            {
                log($"[GIF] ffmpeg.exe not found at {AppPaths.FfmpegExe}", true);
                return null;
            }

            var topClip = TrimToWindow(top.Snapshot(), seconds);
            var bottomClip = TrimToWindow(bottom.Snapshot(), seconds);

            if (topClip.Count == 0 || bottomClip.Count == 0)
            {
                log("[GIF] Not enough buffered video yet — wait a few seconds after connecting.", true);
                return null;
            }

            var baseUtc = topClip[0].TsUtc < bottomClip[0].TsUtc
                ? topClip[0].TsUtc : bottomClip[0].TsUtc;
            double topOffset = (topClip[0].TsUtc - baseUtc).TotalSeconds;
            double bottomOffset = (bottomClip[0].TsUtc - baseUtc).TotalSeconds;

            int shiftFrames = (int)Math.Round(Math.Max(topOffset, bottomOffset) * 30);
            string topPts = shiftFrames > 0 && topOffset > bottomOffset
                ? $"(N+{shiftFrames})/(30*TB)" : "N/(30*TB)";
            string bottomPts = shiftFrames > 0 && bottomOffset > topOffset
                ? $"(N+{shiftFrames})/(30*TB)" : "N/(30*TB)";

            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string outFile = Path.Combine(AppPaths.ScreenshotsDir, $"rg_clip_{ts}.gif");

            string graph =
                $"[0:v]setpts={topPts}[v0];" +
                $"[1:v]setpts={bottomPts}[v1];" +
                "[v0][v1]vstack=inputs=2,fps=15,scale=256:-1:flags=lanczos,split[s0][s1];" +
                "[s0]palettegen=stats_mode=diff[p];" +
                "[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[v]";

            string outputArgs = $"-filter_complex \"{graph}\" -map \"[v]\" -f gif";

            try
            {
                using var mux = new FfmpegPipeMuxer(
                    outFile,
                    new[] { new MuxInput(VideoInputArgs, 0), new MuxInput(VideoInputArgs, 0) },
                    outputArgs, log);

                var feedTop = Task.Run(() =>
                {
                    foreach (var e in topClip) mux.Write(0, e.Nal);
                });
                var feedBottom = Task.Run(() =>
                {
                    foreach (var e in bottomClip) mux.Write(1, e.Nal);
                });
                await Task.WhenAll(feedTop, feedBottom);

                bool ok = await mux.CompleteAsync(timeoutMs: 60_000);
                if (!ok)
                {
                    log("[GIF] Save failed (ffmpeg did not exit cleanly).", true);
                    return null;
                }

                long sizeKb = new FileInfo(outFile).Length / 1024;
                log($"[GIF] Saved ({sizeKb:N0} KB) → {outFile}", false);
                return outFile;
            }
            catch (Exception ex)
            {
                log($"[GIF] Save failed: {ex.Message}", true);
                return null;
            }
        }

        /// <summary>
        /// Returns the buffered NALs covering the last <paramref name="seconds"/>,
        /// starting at the first keyframe (SPS) inside the window so the clip
        /// is decodable from its first frame.
        /// </summary>
        private static List<ReplayBuffer.ReplayEntry> TrimToWindow(
            ReplayBuffer.ReplayEntry[] entries, int seconds)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-seconds);
            var result = new List<ReplayBuffer.ReplayEntry>();
            bool started = false;

            foreach (var entry in entries)
            {
                if (!started)
                {
                    if (entry.TsUtc < cutoff) continue;
                    if (entry.Nal.Length <= 4 || (entry.Nal[4] & 0x1F) != 7) continue;
                    started = true;
                }
                result.Add(entry);
            }
            return result;
        }
    }
}
