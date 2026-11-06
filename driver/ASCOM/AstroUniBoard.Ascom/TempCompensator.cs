// Host-side focuser temperature compensation (Q21: the firmware only reports the SHT31 data).
// While enabled, every 2 s: if the board is idle and the temperature moved by at least the
// threshold since the last compensation, the focuser moves by coefficient x change (steps/°C).
// Runs on its own timer thread, never on the controller's worker (commands from there would wait
// for themselves).
using System;
using System.Globalization;
using System.Threading;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    internal sealed class TempCompensator : IDisposable
    {
        private readonly object gate = new object();
        private Timer timer;
        private bool enabled;
        private double? referenceTemperature;   // temperature the current focus belongs to
        private int running;                    // re-entrancy guard for the timer callback

        public bool Enabled { get { lock (gate) return enabled; } }
        public double StepsPerDegree { get; private set; }
        public double ThresholdDegrees { get; private set; }

        public TempCompensator()
        {
            LoadSettings();
        }

        /// <summary>Re-reads coefficient and threshold from the profile (after the setup dialog).</summary>
        public void LoadSettings()
        {
            StepsPerDegree = Settings.TempCompStepsPerDegree;
            ThresholdDegrees = Settings.TempCompThresholdDegrees;
        }

        public void Enable(bool on)
        {
            lock (gate)
            {
                if (on == enabled) return;
                enabled = on;
                referenceTemperature = null;   // taken at the first tick: the focus is good now
                if (on && timer == null) timer = new Timer(_ => Tick(), null, 0, 2000);
                if (!on && timer != null) { timer.Dispose(); timer = null; }
            }
            Server.Log("temperature compensation " + (on ? "on (" + StepsPerDegree.ToString("0.###", CultureInfo.InvariantCulture) + " steps/°C, threshold " +
                                                      ThresholdDegrees.ToString("0.###", CultureInfo.InvariantCulture) + " °C)" : "off"));
        }

        /// <summary>Back to off (new connection or the last client left).</summary>
        public void Reset()
        {
            lock (gate)
            {
                enabled = false;
                referenceTemperature = null;
                if (timer != null) { timer.Dispose(); timer = null; }
            }
        }

        public void Dispose() { Reset(); }

        private void Tick()
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                BoardSnapshot s = Server.Controller.Snapshot;
                EnvironmentReading env = s.Environment;
                if (!s.HasStatus || env == null || s.Focuser == null) return;
                double t = env.Temperature;
                int steps;
                double previous;
                lock (gate)
                {
                    if (!enabled) return;
                    if (referenceTemperature == null) { referenceTemperature = t; return; }
                    previous = referenceTemperature.Value;
                    double delta = t - previous;
                    if (Math.Abs(delta) < ThresholdDegrees || s.IsBusy) return;
                    steps = (int)Math.Round(StepsPerDegree * delta);
                    if (steps == 0) { referenceTemperature = t; return; }
                    referenceTemperature = t;
                }

                int target = Math.Max(0, Math.Min(s.Focuser.MaxPosition, s.Status.FocuserPosition + steps));
                CommandResult r = Server.Controller.FocuserMoveTo(target, 3000);
                Server.Log("temperature compensation: " + env.Temperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C, " +
                           (steps > 0 ? "+" : "") + steps + " steps -> " + target + (r.Success ? "" : " FAILED: " + r.Message));
                if (!r.Success)
                {
                    // try again at the next tick (board was busy, e.g. a client started a move meanwhile)
                    lock (gate) { if (enabled) referenceTemperature = previous; }
                }
            }
            catch (Exception ex)
            {
                Server.Log("temperature compensation error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }
    }

    /// <summary>Rotator host state: sky offset (Q18, profile) and the last requested target (sky degrees).</summary>
    internal static class RotatorState
    {
        private static readonly object gate = new object();
        private static double? skyOffset;
        public static double? TargetSky;

        public static double SkyOffset
        {
            get
            {
                lock (gate)
                {
                    if (skyOffset == null) skyOffset = Normalize(Settings.SkyOffsetDegrees);
                    return skyOffset.Value;
                }
            }
            set
            {
                double v = Normalize(value);
                lock (gate)
                {
                    skyOffset = v;
                    Settings.SkyOffsetDegrees = v;
                }
            }
        }

        /// <summary>Angle in 0..360 (360 itself becomes 0).</summary>
        public static double Normalize(double deg)
        {
            double r = deg % 360.0;
            if (r < 0) r += 360.0;
            if (r >= 360.0) r -= 360.0;
            return r;
        }
    }
}
