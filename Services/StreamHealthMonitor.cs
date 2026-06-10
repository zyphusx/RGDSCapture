using System;
using System.Threading.Tasks;
using RGDSCapture.Core;

namespace RGDSCapture.Services
{
    /// <summary>
    /// Tracks the health of one video stream and drives auto-recovery.
    ///
    /// State machine per tick:
    ///   no frames yet            → Waiting
    ///   frames flowing           → Live   (retry counter resets)
    ///   frames stopped ≥ 5 s     → Frozen → issue restart (max 3), then
    ///                              Recovering during a grace window
    ///
    /// The grace window is the critical fix over naive freeze polling:
    /// after a restart is issued, the pipeline needs several seconds before
    /// frames can possibly arrive, so the monitor must not count that gap
    /// as a new freeze — otherwise all retries burn in seconds.
    /// </summary>
    public sealed class StreamHealthTracker
    {
        private const double FreezeThresholdSec = 5.0;
        private const double RestartGraceSec = 10.0;
        public const int MaxAutoRetries = 3;

        public StreamHealth Health { get; private set; } = StreamHealth.Waiting;
        public int RetriesUsed { get; private set; }

        private readonly ScreenId _screen;
        private readonly RtpStreamReceiver _receiver;
        private readonly Func<ScreenId, Task> _requestRestart;
        private readonly Action<string, bool> _log;

        private DateTime _graceUntilUtc = DateTime.MinValue;
        private DateTime _lastRestartUtc = DateTime.MinValue;
        private bool _restartInFlight;

        public StreamHealthTracker(
            ScreenId screen,
            RtpStreamReceiver receiver,
            Func<ScreenId, Task> requestRestart,
            Action<string, bool> log)
        {
            _screen = screen;
            _receiver = receiver;
            _requestRestart = requestRestart;
            _log = log;
        }

        /// <summary>Call when a manual restart is issued so retries reset and grace applies.</summary>
        public void NotifyManualRestart()
        {
            RetriesUsed = 0;
            _lastRestartUtc = DateTime.UtcNow;
            _graceUntilUtc = DateTime.UtcNow.AddSeconds(RestartGraceSec);
            Health = StreamHealth.Recovering;
        }

        public void Reset()
        {
            RetriesUsed = 0;
            _graceUntilUtc = DateTime.MinValue;
            _lastRestartUtc = DateTime.MinValue;
            _restartInFlight = false;
            Health = StreamHealth.Waiting;
        }

        /// <summary>Evaluates current health; called once per second while connected.</summary>
        public void Tick()
        {
            var now = DateTime.UtcNow;

            if (!_receiver.HasReceivedFrame)
            {
                // Never had a frame: stay Waiting (or Recovering inside grace).
                Health = now < _graceUntilUtc ? StreamHealth.Recovering : StreamHealth.Waiting;
                return;
            }

            double sinceFrame = (now - _receiver.LastFrameUtc).TotalSeconds;
            bool framesFlowing = sinceFrame < FreezeThresholdSec;

            if (framesFlowing && _receiver.LastFrameUtc > _lastRestartUtc)
            {
                if (Health is StreamHealth.Frozen or StreamHealth.Recovering)
                    _log($"[FREEZE] {Label} stream recovered.", false);
                Health = StreamHealth.Live;
                RetriesUsed = 0;
                return;
            }

            // Frames have stopped. Inside the grace window we just wait.
            if (now < _graceUntilUtc)
            {
                Health = StreamHealth.Recovering;
                return;
            }

            if (RetriesUsed >= MaxAutoRetries)
            {
                // Out of retries — show FROZEN, no log spam, manual restart required.
                Health = StreamHealth.Frozen;
                return;
            }

            if (_restartInFlight) return;

            RetriesUsed++;
            _restartInFlight = true;
            Health = StreamHealth.Recovering;
            _graceUntilUtc = now.AddSeconds(RestartGraceSec);
            _lastRestartUtc = now;
            _log($"[FREEZE] {Label} frozen — recovery attempt {RetriesUsed}/{MaxAutoRetries}", true);

            _ = RestartAsync();
        }

        private async Task RestartAsync()
        {
            try
            {
                await _requestRestart(_screen);
            }
            catch (Exception ex)
            {
                _log($"[FREEZE] {Label} restart failed: {ex.Message}", true);
            }
            finally
            {
                _restartInFlight = false;
            }
        }

        private string Label => _screen == ScreenId.Top ? "Top" : "Bottom";
    }
}
