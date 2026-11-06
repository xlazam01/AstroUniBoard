// Motors tab: TMC2209 config (:47/:48) and StallGuard readings (:41) with the suggested threshold
// and setting it (:42), per axis.
using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private sealed class MotorPanel
        {
            public Axis Axis;
            public NumericUpDown Run, Hold, Idle, Threshold;
            public ComboBox Microsteps, Mode;
            public Label Config, Stall;
            public int? Suggested;
        }

        private TabPage tabMotors;
        private MotorPanel motorFoc, motorRot;

        private void BuildMotorsTab()
        {
            TabPage p = tabMotors = AddTab("Motors / StallGuard");
            motorFoc = BuildMotorGroup(p, Axis.Focuser, 10);
            motorRot = BuildMotorGroup(p, Axis.Rotator, 480);
            var help = Lbl(p,
                "StallGuard (§3.4): SG_RESULT 0..510, lower = more load; only readings after the first 200 ms of a move and above 100 full steps/s count. " +
                "With a threshold > 0, a reading ≤ 2 × threshold stops the move with error 13. Tuning: run a few normal moves (jog on the axis tabs), " +
                "press Read, then use the suggestion (a third of the lowest reading). Needs StealthChop; on SpreadCycle the threshold has no effect. " +
                "Rev 1 measurement: a hand-held focuser did not pull the reading below 46 at 900 steps/s, so light blocks are not reliably detected; " +
                "the rotator needs ≥ 18°/s (default 12°/s) for StallGuard. Changing microsteps rescales every step value of that axis (position, max, speed…).",
                16, 470, 930, 80);
            help.ForeColor = Color.DimGray;
        }

        private MotorPanel BuildMotorGroup(TabPage p, Axis axis, int gx)
        {
            var m = new MotorPanel { Axis = axis };
            var g = new GroupBox { Text = axis == Axis.Focuser ? "Focuser motor" : "Rotator motor", Left = gx, Top = 8, Width = 460, Height = 450 };
            p.Controls.Add(g);
            int y = 24;
            Lbl(g, "Run current (mA RMS):", 12, y + 3, 150); m.Run = Num(g, 170, y, 90, 0, 3000, 0); y += 30;
            Lbl(g, "Hold current (mA RMS):", 12, y + 3, 150); m.Hold = Num(g, 170, y, 90, 0, 3000, 0); y += 30;
            Lbl(g, "Microsteps:", 12, y + 3, 150);
            m.Microsteps = new ComboBox { Left = 170, Top = y, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int u in new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256 }) m.Microsteps.Items.Add(I(u));
            g.Controls.Add(m.Microsteps); y += 30;
            Lbl(g, "Mode:", 12, y + 3, 150);
            m.Mode = new ComboBox { Left = 170, Top = y, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            m.Mode.Items.AddRange(new object[] { "0 StealthChop (StallGuard works)", "1 SpreadCycle" });
            g.Controls.Add(m.Mode); y += 30;
            Lbl(g, "Off after idle (s, 0 = hold):", 12, y + 3, 155); m.Idle = Num(g, 170, y, 90, 0, 100000, 0); y += 32;
            Btn(g, "Read (:47)", 12, y, 100, async (s, e) => await ReadMotor(m));
            Btn(g, "Apply (:48)", 118, y, 100, async (s, e) => await ApplyMotor(m));
            y += 32;
            m.Config = Lbl(g, "", 12, y, 436, 36);
            m.Config.ForeColor = Color.DimGray;
            y += 44;

            Lbl(g, "StallGuard", 12, y, 200).Font = new Font(Font, FontStyle.Bold);
            y += 22;
            m.Stall = Lbl(g, "(press Read after a few moves)", 12, y, 436, 54);
            m.Stall.Font = new Font(FontFamily.GenericMonospace, 9f);
            y += 58;
            Btn(g, "Read (:41)", 12, y, 100, async (s, e) => await ReadStall(m));
            Btn(g, "Use suggestion", 118, y, 110, (s, e) => { if (m.Suggested.HasValue) m.Threshold.Value = m.Suggested.Value; });
            y += 34;
            Lbl(g, "Threshold (0 = off):", 12, y + 3, 150);
            m.Threshold = Num(g, 170, y, 70, 0, 255, 0);
            Btn(g, "Set (:42)", 250, y - 1, 90, async (s, e) =>
            {
                string r = await Send(Commands.SetStallThreshold(m.Axis, (int)m.Threshold.Value), "StallGuard threshold");
                if (!Replies.IsFailure(r)) await ReadStall(m);
            });
            Lbl(g, "(saved with :23#)", 346, y + 3, 110).ForeColor = Color.DimGray;
            return m;
        }

        private async Task ReadMotor(MotorPanel m)
        {
            string r = await Send(Commands.GetDriverConfig(m.Axis), "driver config");
            DriverConfig c;
            if (!DriverConfig.TryParse(r, out c)) { m.Config.Text = Replies.Explain(r); return; }
            m.Run.Value = Math.Min(m.Run.Maximum, c.RunCurrentMa);
            m.Hold.Value = Math.Min(m.Hold.Maximum, c.HoldCurrentMa);
            m.Microsteps.SelectedItem = I(c.Microsteps);
            m.Mode.SelectedIndex = c.Mode;
            m.Idle.Value = Math.Min(m.Idle.Maximum, c.IdleOffSeconds);
            m.Config.Text = "Board: " + c.RunCurrentMa + "/" + c.HoldCurrentMa + " mA, " + c.Microsteps + " microsteps, " +
                            (c.Mode == 0 ? "StealthChop" : "SpreadCycle") + ", off after " + c.IdleOffSeconds + " s";
        }

        private async Task ApplyMotor(MotorPanel m)
        {
            if (m.Microsteps.SelectedItem == null || m.Mode.SelectedIndex < 0) { MessageBox.Show(this, "Read the config first (:47).", "AstroUniBoard"); return; }
            if (m.Hold.Value > m.Run.Value) { MessageBox.Show(this, "Hold current must not be above the run current.", "AstroUniBoard"); return; }
            var c = new DriverConfig
            {
                RunCurrentMa = (int)m.Run.Value, HoldCurrentMa = (int)m.Hold.Value,
                Microsteps = int.Parse((string)m.Microsteps.SelectedItem, CultureInfo.InvariantCulture), Mode = m.Mode.SelectedIndex, IdleOffSeconds = (int)m.Idle.Value
            };
            if (!Confirm("Set the " + m.Axis.ToString().ToLowerInvariant() + " driver to " + c.RunCurrentMa + "/" + c.HoldCurrentMa + " mA, " + c.Microsteps +
                         " microsteps, " + (c.Mode == 0 ? "StealthChop" : "SpreadCycle") + ", off after " + c.IdleOffSeconds + " s?\n\n" +
                         "A microstep change rescales all step values of this axis. Too much current overheats the motor.")) return;
            string r = await Send(Commands.SetDriverConfig(m.Axis, c), "driver config");
            if (!Replies.IsFailure(r)) { await ReadMotor(m); settingsLoaded = false; await PollAsync(); }
        }

        private async Task ReadStall(MotorPanel m)
        {
            string r = await Send(Commands.GetStallGuard(m.Axis), "StallGuard");
            StallGuardInfo g;
            if (!StallGuardInfo.TryParse(r, out g)) { m.Stall.Text = Replies.Explain(r); return; }
            m.Suggested = g.SuggestedThreshold;
            m.Threshold.Value = g.Threshold;
            m.Stall.Text = "threshold " + g.Threshold + (g.Threshold == 0 ? " (off)" : " (stops at ≤ " + (2 * g.Threshold) + ")") + "\n" +
                           "last move: last " + N(g.Last) + "  min " + N(g.Min) + "  max " + N(g.Max) + "\n" +
                           "suggested threshold: " + (g.SuggestedThreshold.HasValue ? I(g.SuggestedThreshold.Value) : "— (no readings; move faster/longer)");
        }

        private static string N(int? v) { return v.HasValue ? I(v.Value) : "-"; }
    }
}
