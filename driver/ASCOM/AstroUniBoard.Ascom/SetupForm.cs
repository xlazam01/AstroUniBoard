// Setup dialog shared by the Focuser, Rotator and ObservingConditions drivers: choose the board
// (by USB serial, manual COM port as fallback), watch the live status, change the focuser and rotator
// settings stored in the board (written with :23#), the rotator sky offset (host only, Q18) and the
// temperature compensation coefficient (host side, Q21).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    internal sealed class SetupForm : Form
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        private readonly ComboBox cmbDevice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button btnIdentify = new Button { Text = "Identify" };
        private readonly TextBox txtManual = new TextBox();
        private readonly Label lblConnection = new Label { AutoSize = false };
        private readonly TextBox txtStatus = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9f) };

        // focuser (board settings)
        private readonly NumericUpDown numFocMax = Num(1, 10000000, 0);
        private readonly NumericUpDown numFocSpeed = Num(1, 20000, 0);
        private readonly NumericUpDown numFocAccel = Num(1, 100000, 0);
        private readonly NumericUpDown numFocBacklash = Num(0, 10000, 0);
        private readonly NumericUpDown numFocStep = Num(0, 1000, 2);
        private readonly CheckBox chkFocReverse = new CheckBox { Text = "Reverse direction" };
        private readonly Button btnFocWrite = new Button { Text = "Write to board" };
        // rotator (board settings)
        private readonly NumericUpDown numRotSpeed = Num(0.1m, 36, 2);
        private readonly NumericUpDown numRotAccel = Num(0.1m, 360, 2);
        private readonly NumericUpDown numRotSpd = Num(1, 10000, 3);
        private readonly NumericUpDown numRotWrapMin = Num(-360, 0, 2);
        private readonly NumericUpDown numRotWrapMax = Num(0, 360, 2);
        private readonly NumericUpDown numRotTol = Num(0.05m, 5, 2);
        private readonly CheckBox chkRotReverse = new CheckBox { Text = "Reverse direction" };
        private readonly CheckBox chkRotBootHome = new CheckBox { Text = "IMU home after power-on" };
        private readonly Button btnRotWrite = new Button { Text = "Write to board" };
        // host side
        private readonly NumericUpDown numSkyOffset = Num(0, 359.99m, 2);
        private readonly NumericUpDown numTcCoef = Num(-10000, 10000, 2);
        private readonly NumericUpDown numTcThreshold = Num(0.1m, 10, 2);

        private readonly Button btnHaltAll = new Button { Text = "Halt all" };
        private readonly Button btnClear = new Button { Text = "Clear error" };
        private readonly Button btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK };
        private readonly Button btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 400 };

        private List<UsbSerialDevice> candidates = new List<UsbSerialDevice>();
        private readonly Dictionary<string, string> probed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool acquired;
        private bool focLoaded, rotLoaded;

        private static NumericUpDown Num(decimal min, decimal max, int decimals)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : 0.1m, Width = 100, TextAlign = HorizontalAlignment.Right };
        }

        public SetupForm()
        {
            Text = "AstroUniBoard (Focuser, Rotator, ObservingConditions) - setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            const int W = 760;

            int y = 12;
            Controls.Add(new Label { Text = "Board:", Left = 12, Top = y + 3, Width = 110 });
            cmbDevice.SetBounds(125, y, 530, 24);
            btnIdentify.SetBounds(662, y - 1, 86, 26);
            Controls.AddRange(new Control[] { cmbDevice, btnIdentify });
            new ToolTip().SetToolTip(btnIdentify, "Asks every listed board that is not in use for its identity (:32#).");
            y += 32;
            Controls.Add(new Label { Text = "Manual COM port:", Left = 12, Top = y + 3, Width = 110 });
            txtManual.SetBounds(125, y, 100, 24);
            Controls.Add(txtManual);
            Controls.Add(new Label { Text = "(only if automatic detection does not work; leave empty)", Left = 235, Top = y + 3, Width = 420 });
            y += 30;
            lblConnection.SetBounds(12, y, W - 24, 36);
            lblConnection.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(lblConnection);
            y += 38;

            Controls.Add(new Label { Text = "Live status:", Left = 12, Top = y, Width = 200 });
            y += 18;
            txtStatus.SetBounds(12, y, W - 24, 150);
            Controls.Add(txtStatus);
            y += 158;

            // two columns of board settings
            var foc = new GroupBox { Text = "Focuser (stored in the board)", Left = 12, Top = y, Width = 362, Height = 248 };
            int fy = 22;
            Row(foc, ref fy, "Max position (steps):", numFocMax);
            Row(foc, ref fy, "Speed (steps/s):", numFocSpeed);
            Row(foc, ref fy, "Acceleration (steps/s²):", numFocAccel);
            Row(foc, ref fy, "Backlash (steps):", numFocBacklash);
            Row(foc, ref fy, "Step size (µm, 0 = unknown):", numFocStep);
            chkFocReverse.SetBounds(12, fy, 200, 22); foc.Controls.Add(chkFocReverse); fy += 28;
            btnFocWrite.SetBounds(12, fy, 120, 28); foc.Controls.Add(btnFocWrite);
            Controls.Add(foc);

            var rot = new GroupBox { Text = "Rotator (stored in the board)", Left = 386, Top = y, Width = 362, Height = 248 };
            int ry = 22;
            Row(rot, ref ry, "Speed (°/s):", numRotSpeed);
            Row(rot, ref ry, "Acceleration (°/s²):", numRotAccel);
            Row(rot, ref ry, "Steps per degree:", numRotSpd);
            Row(rot, ref ry, "Cable wrap min / max (°):", numRotWrapMin, numRotWrapMax);
            Row(rot, ref ry, "Home tolerance (°):", numRotTol);
            chkRotReverse.SetBounds(12, ry, 150, 22); chkRotBootHome.SetBounds(165, ry, 190, 22);
            rot.Controls.AddRange(new Control[] { chkRotReverse, chkRotBootHome }); ry += 28;
            btnRotWrite.SetBounds(12, ry, 120, 28); rot.Controls.Add(btnRotWrite);
            Controls.Add(rot);
            y += 256;

            var host = new GroupBox { Text = "This PC (ASCOM profile)", Left = 12, Top = y, Width = W - 24, Height = 86 };
            int hy = 22;
            host.Controls.Add(new Label { Text = "Rotator sky offset (°):", Left = 12, Top = hy + 3, Width = 170 });
            numSkyOffset.SetBounds(185, hy, 100, 24); host.Controls.Add(numSkyOffset);
            host.Controls.Add(new Label { Text = "sky angle = mechanical angle + offset; set by ASCOM Sync (plate solve)", Left = 295, Top = hy + 3, Width = 430, ForeColor = Color.DimGray });
            hy += 30;
            host.Controls.Add(new Label { Text = "TempComp (steps/°C):", Left = 12, Top = hy + 3, Width = 170 });
            numTcCoef.SetBounds(185, hy, 100, 24); host.Controls.Add(numTcCoef);
            host.Controls.Add(new Label { Text = "move after a change of (°C):", Left = 295, Top = hy + 3, Width = 170 });
            numTcThreshold.SetBounds(468, hy, 70, 24); host.Controls.Add(numTcThreshold);
            host.Controls.Add(new Label { Text = "positive = outward when warmer", Left = 545, Top = hy + 3, Width = 190, ForeColor = Color.DimGray });
            Controls.Add(host);
            y += 96;

            btnHaltAll.SetBounds(12, y, 90, 28);
            btnClear.SetBounds(108, y, 90, 28);
            btnOk.SetBounds(W - 170, y, 75, 28);
            btnCancel.SetBounds(W - 87, y, 75, 28);
            Controls.AddRange(new Control[] { btnHaltAll, btnClear, btnOk, btnCancel });
            ClientSize = new Size(W, y + 40);

            btnIdentify.Click += OnIdentify;
            btnFocWrite.Click += OnFocuserWrite;
            btnRotWrite.Click += OnRotatorWrite;
            btnHaltAll.Click += (s, e) => Run(Server.Controller.HaltAll(4000));
            btnClear.Click += (s, e) => Run(Server.Controller.ClearError(4000));
            btnOk.Click += OnOk;
            timer.Tick += (s, e) => RefreshStatus();

            Load += OnLoad;
            FormClosed += (s, e) =>
            {
                timer.Stop();
                if (acquired) { acquired = false; Server.ReleaseConnection(); }
            };
        }

        private static void Row(GroupBox g, ref int y, string label, params NumericUpDown[] nums)
        {
            g.Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, Width = 170 });
            int x = 185;
            foreach (NumericUpDown n in nums)
            {
                n.SetBounds(x, y, nums.Length > 1 ? 80 : 100, 24);
                g.Controls.Add(n);
                x += 86;
            }
            y += 30;
        }

        private static decimal Clamp(NumericUpDown n, double v)
        {
            decimal d = (decimal)v;
            return d < n.Minimum ? n.Minimum : d > n.Maximum ? n.Maximum : d;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            LoadDevices();
            txtManual.Text = Settings.ManualPort;
            numSkyOffset.Value = Clamp(numSkyOffset, RotatorState.SkyOffset);
            numTcCoef.Value = Clamp(numTcCoef, Settings.TempCompStepsPerDegree);
            numTcThreshold.Value = Clamp(numTcThreshold, Settings.TempCompThresholdDegrees);
            Server.AcquireConnection();
            acquired = true;
            timer.Start();
            RefreshStatus();
        }

        private void LoadDevices()
        {
            int keep = cmbDevice.SelectedIndex;
            string preferred = keep > 0 && keep - 1 < candidates.Count ? candidates[keep - 1].UsbSerial : Settings.PreferredUsbSerial;
            cmbDevice.Items.Clear();
            cmbDevice.Items.Add("Automatic (the AstroUniBoard found on USB)");
            candidates = DeviceFinder.FindCandidates(new RegistryUsbEnumerator(), null);
            int select = 0;
            bool found = string.IsNullOrEmpty(preferred);
            foreach (UsbSerialDevice d in candidates)
            {
                string info;
                probed.TryGetValue(d.PortName, out info);
                cmbDevice.Items.Add(d.PortName + "   USB serial " + d.UsbSerial + (info != null ? "   - " + info : ""));
                if (!string.IsNullOrEmpty(preferred) && string.Equals(d.UsbSerial, preferred, StringComparison.OrdinalIgnoreCase)) { select = cmbDevice.Items.Count - 1; found = true; }
            }
            if (!found)
            {
                // the remembered board is not plugged in: keep it selectable so OK does not forget it
                candidates.Add(new UsbSerialDevice { PortName = "?", UsbSerial = preferred });
                cmbDevice.Items.Add("(not present)   USB serial " + preferred);
                select = cmbDevice.Items.Count - 1;
            }
            cmbDevice.SelectedIndex = select;
        }

        private void OnIdentify(object sender, EventArgs e)
        {
            btnIdentify.Enabled = false;
            Cursor = Cursors.WaitCursor;
            BoardSnapshot s = Server.Controller.Snapshot;
            var list = new List<UsbSerialDevice>(DeviceFinder.FindCandidates(new RegistryUsbEnumerator(), null));
            var t = new Thread(() =>
            {
                var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (UsbSerialDevice d in list)
                {
                    if (s.IsConnected && string.Equals(d.PortName, s.PortName, StringComparison.OrdinalIgnoreCase))
                    {
                        results[d.PortName] = "in use by this driver: " + s.Identity.Model + " " + s.Identity.Firmware + ", board serial " + s.Identity.Serial;
                        continue;
                    }
                    if (!s.IsConnected && string.Equals(d.PortName, s.PortName, StringComparison.OrdinalIgnoreCase)) { results[d.PortName] = "this driver is connecting to it"; continue; }
                    string failure;
                    using (var link = new SerialLink(d.PortName))
                    {
                        BoardIdentity id = DeviceFinder.Identify(link, 800, out failure);
                        results[d.PortName] = id != null ? id.Model + " " + id.Firmware + ", board serial " + id.Serial : "not an AstroUniBoard or in use (" + failure + ")";
                    }
                }
                BeginInvoke((Action)(() =>
                {
                    foreach (var kv in results) probed[kv.Key] = kv.Value;
                    LoadDevices();
                    Cursor = Cursors.Default;
                    btnIdentify.Enabled = true;
                }));
            }) { IsBackground = true };
            t.Start();
        }

        private void RefreshStatus()
        {
            BoardSnapshot s = Server.Controller.Snapshot;
            BoardStatus st = s.Status;

            switch (s.Connection)
            {
                case ConnectionState.Connected:
                    lblConnection.Text = "Connected: " + s.Identity + " on " + s.PortName;
                    lblConnection.ForeColor = Color.DarkGreen;
                    break;
                case ConnectionState.Disconnected:
                    lblConnection.Text = "Not connected " + (s.Problem ?? "");
                    lblConnection.ForeColor = Color.Gray;
                    break;
                default:
                    lblConnection.Text = "Looking for the AstroUniBoard... " + (s.Problem ?? "");
                    lblConnection.ForeColor = Color.DarkOrange;
                    break;
            }

            var sb = new StringBuilder();
            if (s.HasStatus)
            {
                sb.AppendLine("State:       " + st.State + "   clients of this driver: " + Server.ConnectionClients);
                sb.AppendLine("Focuser:     " + st.FocuserPosition + (st.FocuserTarget >= 0 ? " -> " + st.FocuserTarget : "") +
                              (s.Focuser != null ? " of 0.." + s.Focuser.MaxPosition : "") + (st.Has(StatusFlags.FocuserUnverified) ? "   (position unverified)" : ""));
                double mech = st.RotatorAngle / 100.0;
                sb.AppendLine("Rotator:     mechanical " + mech.ToString("0.00", CI) + "°, sky " + RotatorState.Normalize(mech + RotatorState.SkyOffset).ToString("0.00", CI) + "°" +
                              (st.RotatorTarget >= 0 ? " -> mechanical " + (st.RotatorTarget / 100.0).ToString("0.00", CI) + "°" : "") +
                              (st.Has(StatusFlags.RotatorUnverified) ? "   (angle unverified)" : ""));
                EnvironmentReading env = s.Environment;
                sb.AppendLine("Environment: " + (env == null ? "no valid reading" : env.ToString()) +
                              "   TempComp " + (Server.TempComp.Enabled ? "ON" : "off"));
                Housekeeping h = s.Housekeeping;
                if (h != null)
                    sb.AppendLine("Board:       MCU " + h.McuTemperature.ToString("0.0", CI) + " °C, 3.3 V rail " + h.Vdda.ToString("0.00", CI) + " V, warnings: " + Housekeeping.Describe(h.Warnings));
                sb.AppendLine("IMU:         " + (st.Has(StatusFlags.ImuAnswering) ? "answering" : "not answering") + (st.Has(StatusFlags.ImuNotCalibrated) ? ", zero not calibrated" : ""));
                sb.AppendLine("Error:       " + (st.Error == 0 ? "none" : BoardErrors.Describe(st.Error) + " (code " + st.Error + ")"));
            }
            if (!string.IsNullOrEmpty(s.Problem)) sb.AppendLine("Note:        " + s.Problem);
            if (txtStatus.Text != sb.ToString()) txtStatus.Text = sb.ToString();

            bool ready = s.HasStatus;
            btnHaltAll.Enabled = ready;
            btnClear.Enabled = ready && st.Error != 0;

            if (!ready) { focLoaded = rotLoaded = false; }
            if (ready && !focLoaded && s.Focuser != null) { LoadFocuser(s.Focuser); focLoaded = true; }
            if (ready && !rotLoaded && s.Rotator != null) { LoadRotator(s.Rotator); rotLoaded = true; }
            btnFocWrite.Enabled = ready && focLoaded && !st.Busy;
            btnRotWrite.Enabled = ready && rotLoaded && !st.Busy;
        }

        private void LoadFocuser(FocuserSettings f)
        {
            numFocMax.Value = Clamp(numFocMax, f.MaxPosition);
            numFocSpeed.Value = Clamp(numFocSpeed, f.Speed);
            numFocAccel.Value = Clamp(numFocAccel, f.Acceleration);
            numFocBacklash.Value = Clamp(numFocBacklash, f.Backlash);
            numFocStep.Value = Clamp(numFocStep, f.StepSizeCentiMicrons / 100.0);
            chkFocReverse.Checked = f.Reverse;
        }

        private void LoadRotator(RotatorSettings r)
        {
            numRotSpeed.Value = Clamp(numRotSpeed, r.SpeedCdegPerS / 100.0);
            numRotAccel.Value = Clamp(numRotAccel, r.AccelerationCdegPerS2 / 100.0);
            numRotSpd.Value = Clamp(numRotSpd, r.StepsPerKiloDegree / 1000.0);
            numRotWrapMin.Value = Clamp(numRotWrapMin, r.WrapMinCdeg / 100.0);
            numRotWrapMax.Value = Clamp(numRotWrapMax, r.WrapMaxCdeg / 100.0);
            numRotTol.Value = Clamp(numRotTol, r.HomeToleranceCdeg / 100.0);
            chkRotReverse.Checked = r.Reverse;
            chkRotBootHome.Checked = r.BootHome;
        }

        private static int Scaled(NumericUpDown n, int factor) { return (int)Math.Round(n.Value * factor); }

        /// <summary>Writes the changed values one by one, then saves them in the EEPROM (:23#).</summary>
        private void WriteChanges(List<KeyValuePair<string, Func<CommandResult>>> changes, string what)
        {
            if (changes.Count == 0) { MessageBox.Show(this, "Nothing changed.", "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (var c in changes)
                {
                    CommandResult r = c.Value();
                    if (!r.Success)
                    {
                        MessageBox.Show(this, c.Key + ": " + r.Message, "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                CommandResult save = Server.Controller.SaveSettings(6000);
                if (!save.Success) MessageBox.Show(this, "Set, but saving to the EEPROM failed: " + save.Message, "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else Server.Log("setup dialog: " + what + " settings written and saved (" + changes.Count + " values)");
            }
            finally
            {
                Cursor = Cursors.Default;
                focLoaded = rotLoaded = false;   // show what the board really has now
                RefreshStatus();
            }
        }

        private void OnFocuserWrite(object sender, EventArgs e)
        {
            FocuserSettings f = Server.Controller.Snapshot.Focuser;
            if (f == null) return;
            BoardController c = Server.Controller;
            var ch = new List<KeyValuePair<string, Func<CommandResult>>>();
            int max = Scaled(numFocMax, 1), speed = Scaled(numFocSpeed, 1), accel = Scaled(numFocAccel, 1), bl = Scaled(numFocBacklash, 1), step = Scaled(numFocStep, 100);
            if (max != f.MaxPosition) ch.Add(Change("max position", () => c.SetFocuserSetting(FocuserSetting.MaxPosition, max)));
            if (speed != f.Speed) ch.Add(Change("speed", () => c.SetFocuserSetting(FocuserSetting.Speed, speed)));
            if (accel != f.Acceleration) ch.Add(Change("acceleration", () => c.SetFocuserSetting(FocuserSetting.Acceleration, accel)));
            if (bl != f.Backlash) ch.Add(Change("backlash", () => c.SetFocuserSetting(FocuserSetting.Backlash, bl)));
            if (step != f.StepSizeCentiMicrons) ch.Add(Change("step size", () => c.SetFocuserSetting(FocuserSetting.StepSizeCentiMicrons, step)));
            bool rev = chkFocReverse.Checked;
            if (rev != f.Reverse) ch.Add(Change("reverse", () => c.SetFocuserSetting(FocuserSetting.Reverse, rev ? 1 : 0)));
            WriteChanges(ch, "focuser");
        }

        private void OnRotatorWrite(object sender, EventArgs e)
        {
            RotatorSettings r = Server.Controller.Snapshot.Rotator;
            if (r == null) return;
            BoardController c = Server.Controller;
            var ch = new List<KeyValuePair<string, Func<CommandResult>>>();
            int speed = Scaled(numRotSpeed, 100), accel = Scaled(numRotAccel, 100), spk = Scaled(numRotSpd, 1000);
            int wmin = Scaled(numRotWrapMin, 100), wmax = Scaled(numRotWrapMax, 100), tol = Scaled(numRotTol, 100);
            if (speed != r.SpeedCdegPerS) ch.Add(Change("speed", () => c.SetRotatorSetting(RotatorSetting.Speed, speed)));
            if (accel != r.AccelerationCdegPerS2) ch.Add(Change("acceleration", () => c.SetRotatorSetting(RotatorSetting.Acceleration, accel)));
            if (spk != r.StepsPerKiloDegree) ch.Add(Change("steps per degree", () => c.SetRotatorSetting(RotatorSetting.StepsPerKiloDegree, spk)));
            if (wmin != r.WrapMinCdeg || wmax != r.WrapMaxCdeg) ch.Add(Change("cable wrap", () => c.SetRotatorWrap(wmin, wmax)));
            if (tol != r.HomeToleranceCdeg) ch.Add(Change("home tolerance", () => c.SetRotatorSetting(RotatorSetting.HomeToleranceCdeg, tol)));
            bool rev = chkRotReverse.Checked, boot = chkRotBootHome.Checked;
            if (rev != r.Reverse) ch.Add(Change("reverse", () => c.SetRotatorSetting(RotatorSetting.Reverse, rev ? 1 : 0)));
            if (boot != r.BootHome) ch.Add(Change("boot home", () => c.SetRotatorSetting(RotatorSetting.BootHome, boot ? 1 : 0)));
            WriteChanges(ch, "rotator");
        }

        private static KeyValuePair<string, Func<CommandResult>> Change(string name, Func<CommandResult> f)
        {
            return new KeyValuePair<string, Func<CommandResult>>(name, f);
        }

        private void Run(CommandResult r)
        {
            if (!r.Success) MessageBox.Show(this, r.Message, "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshStatus();
        }

        private void OnOk(object sender, EventArgs e)
        {
            int idx = cmbDevice.SelectedIndex;
            string serial = idx > 0 && idx - 1 < candidates.Count ? candidates[idx - 1].UsbSerial : string.Empty;
            string manual = txtManual.Text.Trim();
            bool boardChanged = !string.Equals(serial, Settings.PreferredUsbSerial, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(manual, Settings.ManualPort, StringComparison.OrdinalIgnoreCase);
            Settings.PreferredUsbSerial = serial;
            Settings.ManualPort = manual;

            double offset = (double)numSkyOffset.Value;
            if (Math.Abs(offset - RotatorState.SkyOffset) > 0.004) RotatorState.SkyOffset = offset;
            Settings.TempCompStepsPerDegree = (double)numTcCoef.Value;
            Settings.TempCompThresholdDegrees = (double)numTcThreshold.Value;
            Server.TempComp.LoadSettings();

            if (boardChanged && Server.ConnectionClients > 1)
                MessageBox.Show(this, "The new board choice is used the next time all programs have disconnected from the AstroUniBoard.",
                                "AstroUniBoard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
