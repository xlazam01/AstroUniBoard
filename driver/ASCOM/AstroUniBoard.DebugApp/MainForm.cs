// Main window: connection bar (ASCOM driver or direct), tabs, and the poll loop. Every board call
// runs off the UI thread (Bg); results come back to the UI thread through await. Layout is done in
// code with fixed coordinates, as in the OpenWheel setup dialog.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm : Form
    {
        private sealed class ConnectChoice
        {
            public string Text;
            public Func<IBoardSession> Create;
            public bool Direct;
            public override string ToString() { return Text; }
        }

        private readonly ComboBox cmbConnect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button btnRescan = new Button { Text = "Rescan" };
        private readonly Button btnConnect = new Button { Text = "Connect" };
        private readonly Button btnDisconnect = new Button { Text = "Disconnect", Enabled = false };
        private readonly Button btnHaltAll = new Button { Text = "HALT ALL", BackColor = Color.Firebrick, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Label lblConn = new Label { AutoSize = false };
        private readonly Label lblWarn = new Label { AutoSize = false, ForeColor = Color.DarkOrange };
        private readonly TabControl tabs = new TabControl();
        private readonly StatusStrip strip = new StatusStrip();
        private readonly ToolStripStatusLabel stripText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Timer timer = new Timer { Interval = 500 };

        private readonly bool loopbackOption;
        private readonly string startPort;
        private IBoardSession session;
        private BoardView view = BoardView.Disconnected;
        private bool polling;
        private bool pausePoll;
        private int tick;
        private long logSeq;

        public MainForm(bool loopback, string startPort)
        {
            loopbackOption = loopback;
            this.startPort = startPort;
            Text = "AstroUniBoard control & debug";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(900, 640);

            int y = 10;
            Controls.Add(new Label { Text = "Connection:", Left = 12, Top = y + 4, Width = 80 });
            cmbConnect.SetBounds(95, y, 470, 24);
            btnRescan.SetBounds(570, y - 1, 70, 27);
            btnConnect.SetBounds(645, y - 1, 80, 27);
            btnDisconnect.SetBounds(730, y - 1, 85, 27);
            btnHaltAll.SetBounds(860, y - 4, 108, 33);
            btnHaltAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnHaltAll.Font = new Font(Font, FontStyle.Bold);
            Controls.AddRange(new Control[] { cmbConnect, btnRescan, btnConnect, btnDisconnect, btnHaltAll });
            y += 32;
            lblConn.SetBounds(12, y, 956, 18);
            lblConn.Font = new Font(Font, FontStyle.Bold);
            lblConn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            y += 18;
            lblWarn.SetBounds(12, y, 956, 18);
            lblWarn.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.AddRange(new Control[] { lblConn, lblWarn });
            y += 22;

            tabs.SetBounds(8, y, 964, ClientSize.Height - y - 26);
            tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(tabs);
            strip.Items.Add(stripText);
            Controls.Add(strip);

            BuildStatusTab();
            BuildFocuserTab();
            BuildRotatorTab();
            BuildMotorsTab();
            BuildImuTab();
            BuildEnvironmentTab();
            BuildConsoleTab();
            BuildLogTab();
            BuildFirmwareTab();

            btnRescan.Click += (s, e) => LoadChoices();
            btnConnect.Click += async (s, e) => await ConnectAsync();
            btnDisconnect.Click += (s, e) => DisconnectSession();
            btnHaltAll.Click += async (s, e) => await DoAction(BoardAction.HaltAll);
            cmbConnect.SelectedIndexChanged += (s, e) => ShowChoiceWarning();
            timer.Tick += async (s, e) => await PollAsync();
            FormClosing += (s, e) => { timer.Stop(); DisconnectSession(); };
            Load += (s, e) => { LoadChoices(); UpdateAll(); timer.Start(); };
        }

        // ------------------------------------------------------------------ connection

        private void LoadChoices()
        {
            cmbConnect.Items.Clear();
            cmbConnect.Items.Add(new ConnectChoice
            {
                Text = "ASCOM driver " + AscomSession.FocuserProgId + " (works while N.I.N.A. is connected)",
                Create = () => new AscomSession()
            });
            cmbConnect.Items.Add(new ConnectChoice { Text = "Direct — automatic (first AstroUniBoard on USB)", Create = () => new DirectSession(null), Direct = true });
            int select = 0;
            foreach (UsbSerialDevice d in DeviceFinder.FindCandidates(new RegistryUsbEnumerator(), null))
            {
                string port = d.PortName;
                cmbConnect.Items.Add(new ConnectChoice
                {
                    Text = "Direct — " + port + "  (USB " + d.Vid.ToString("X4") + ":" + d.Pid.ToString("X4") + ", serial " + d.UsbSerial + ")",
                    Create = () => new DirectSession(port), Direct = true
                });
                if (string.Equals(port, startPort, StringComparison.OrdinalIgnoreCase)) select = cmbConnect.Items.Count - 1;
            }
            if (!string.IsNullOrEmpty(startPort) && select == 0)
            {
                string port = startPort;
                cmbConnect.Items.Add(new ConnectChoice { Text = "Direct — " + port, Create = () => new DirectSession(port), Direct = true });
                select = cmbConnect.Items.Count - 1;
            }
            if (loopbackOption)
            {
                string port = startPort;
                cmbConnect.Items.Add(new ConnectChoice
                {
                    Text = "Loopback: ASCOM code path over an in-process copy of the driver contract (" + (port ?? "automatic") + ", development)",
                    Create = () => new AscomSession(() => new LoopbackDriver(new DirectSession(port)))
                });
                select = cmbConnect.Items.Count - 1;
            }
            cmbConnect.SelectedIndex = select;
            ShowChoiceWarning();
        }

        private void ShowChoiceWarning()
        {
            var c = cmbConnect.SelectedItem as ConnectChoice;
            lblWarn.Text = c != null && c.Direct
                ? "Direct takes the COM port: N.I.N.A. (or the ASCOM driver) cannot connect to the board while this is connected. Use it on the bench or with N.I.N.A. disconnected."
                : "Goes through the installed ASCOM driver: safe while N.I.N.A. is connected — but every move here also moves the rig N.I.N.A. is using.";
        }

        private async Task ConnectAsync()
        {
            var c = cmbConnect.SelectedItem as ConnectChoice;
            if (c == null) return;
            DisconnectSession();
            btnConnect.Enabled = false;
            cmbConnect.Enabled = false;
            lblConn.Text = "Connecting…";
            IBoardSession s = null;
            string error = null;
            await Task.Run(() =>
            {
                try { s = c.Create(); s.Open(); }
                catch (Exception ex)
                {
                    error = ex.Message;
                    try { if (s != null) s.Dispose(); } catch { }
                    s = null;
                }
            });
            if (s == null)
            {
                lblConn.Text = "Not connected: " + error;
                lblConn.ForeColor = Color.Firebrick;
                btnConnect.Enabled = true;
                cmbConnect.Enabled = true;
                AppLog("connect failed: " + error);
                return;
            }
            session = s;
            logSeq = 0;
            btnDisconnect.Enabled = true;
            Text = "AstroUniBoard control & debug — " + s.Description;
            AppLog("connected: " + s.Description);
            settingsLoaded = false;
            await PollAsync();
        }

        private void DisconnectSession()
        {
            IBoardSession s = session;
            session = null;
            view = BoardView.Disconnected;
            if (s != null)
            {
                AppLog("disconnected");
                // the direct session halts nothing; it just closes the port
                Task.Run(() => { try { s.Dispose(); } catch { } });
            }
            btnConnect.Enabled = true;
            cmbConnect.Enabled = true;
            btnDisconnect.Enabled = false;
            Text = "AstroUniBoard control & debug";
            UpdateAll();
        }

        // ------------------------------------------------------------------ polling

        private async Task PollAsync()
        {
            IBoardSession s = session;
            if (s == null || polling || pausePoll) return;
            polling = true;
            try
            {
                tick++;
                TabPage page = tabs.SelectedTab;
                bool wantSigned = page == tabRotator || page == tabImu;
                bool wantImu = page == tabImu && chkImuLive.Checked;
                bool wantEnv = page == tabEnv && tick % 6 == 1;
                bool wantEnvSource = page == tabEnv && cmbEnvSource.SelectedIndex < 0;
                BoardView v = null;
                string err = null, signed = null, imu = null, env = null, envSource = null;
                IList<LogEntry> logs = null;
                await Task.Run(() =>
                {
                    try
                    {
                        v = s.Snapshot();
                        if (v.IsConnected)
                        {
                            if (wantSigned) signed = s.Raw(":70#");
                            if (wantImu) imu = s.Raw(Commands.Imu);
                            if (wantEnv) env = s.Raw(Commands.EnvironmentDetail);
                            if (wantEnvSource) envSource = s.Raw(Commands.GetEnvironmentSource);
                        }
                        logs = s.ReadLog(logSeq);
                    }
                    catch (Exception ex) { err = ex.Message; }
                });
                if (s != session) return;
                if (err != null)
                {
                    stripText.Text = "Snapshot failed: " + err;
                    return;
                }
                view = v;
                if (logs != null) foreach (LogEntry e in logs) { DriverLog(e); if (e.Seq > logSeq) logSeq = e.Seq; }
                if (signed != null) ShowSignedAngle(signed);
                if (imu != null) ShowImu(imu);
                if (env != null) ShowEnvDetail(env);
                if (envSource != null) ShowEnvSource(envSource);
                UpdateAll();
            }
            finally { polling = false; }
        }

        private void UpdateAll()
        {
            BoardView v = view;
            if (session == null)
            {
                if (btnConnect.Enabled && !lblConn.Text.StartsWith("Not connected", StringComparison.Ordinal))
                {
                    lblConn.Text = "Not connected";
                    lblConn.ForeColor = Color.DimGray;
                }
            }
            else
            {
                string id = v.Identity != null ? v.Identity.Model + " " + v.Identity.Firmware + " (protocol " + v.Identity.Protocol + ", serial " + v.Identity.Serial + ")" : "";
                lblConn.Text = (session.IsDirect ? "Direct" : "ASCOM") + ": " + v.Connection + (v.Port != null ? " on " + v.Port : "") + "  " + id +
                               (v.Problem != null ? "  —  " + v.Problem : "");
                lblConn.ForeColor = !v.IsConnected ? Color.Firebrick : (v.Problem != null ? Color.DarkOrange : Color.DarkGreen);
            }
            bool up = session != null && v.IsConnected;
            btnHaltAll.Enabled = up;
            UpdateStatusTab(v);
            UpdateFocuserTab(v);
            UpdateRotatorTab(v);
            UpdateImuTab(v);
            UpdateEnvironmentTab(v);
            UpdateFirmwareTab(v);
            EnableBoardControls(tabs, up);
        }

        /// <summary>Controls tagged "needs-board" are enabled only while a board is connected.</summary>
        private static void EnableBoardControls(Control parent, bool up)
        {
            foreach (Control c in parent.Controls)
            {
                if (c.Tag as string == "needs-board") c.Enabled = up;
                else if (c.HasChildren) EnableBoardControls(c, up);
            }
        }

        // ------------------------------------------------------------------ helpers for the tabs

        /// <summary>Sends a raw command off the UI thread, logs it, shows errors in the status bar. Returns the reply.</summary>
        private async Task<string> Send(string command, string what = null)
        {
            IBoardSession s = session;
            if (s == null) { stripText.Text = "Not connected"; return Replies.ErrorPrefix + "not connected"; }
            string reply = await Task.Run(() => { try { return s.Raw(command); } catch (Exception ex) { return Replies.ErrorPrefix + ex.Message; } });
            string text = (what != null ? what + ": " : "") + command + " → " + Replies.Explain(reply);
            AppLog(text);
            stripText.Text = text;
            stripText.ForeColor = Replies.IsFailure(reply) ? Color.Firebrick : SystemColors.ControlText;
            ConsoleEcho(command, reply);
            return reply;
        }

        private async Task<string> DoAction(BoardAction a)
        {
            IBoardSession s = session;
            if (s == null) return Replies.ErrorPrefix + "not connected";
            string reply = await Task.Run(() => { try { return s.Do(a); } catch (Exception ex) { return Replies.ErrorPrefix + ex.Message; } });
            string text = a + " → " + reply;
            AppLog(text);
            stripText.Text = text;
            stripText.ForeColor = Replies.IsFailure(reply) ? Color.Firebrick : SystemColors.ControlText;
            await PollAsync();
            return reply;
        }

        private bool Confirm(string text)
        {
            return MessageBox.Show(this, text, "AstroUniBoard", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        private TabPage AddTab(string title)
        {
            // size the page now: anchors are measured against it before the TabControl has a handle
            var p = new TabPage(title) { UseVisualStyleBackColor = true, Size = new Size(tabs.Width - 8, tabs.Height - 26) };
            tabs.TabPages.Add(p);
            return p;
        }

        private static Button Btn(Control parent, string text, int x, int y, int w, EventHandler click, bool needsBoard = true)
        {
            var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 27 };
            if (needsBoard) b.Tag = "needs-board";
            b.Click += click;
            parent.Controls.Add(b);
            return b;
        }

        private static Label Lbl(Control parent, string text, int x, int y, int w, int h = 18)
        {
            var l = new Label { Text = text, Left = x, Top = y, Width = w, Height = h, AutoSize = false };
            parent.Controls.Add(l);
            return l;
        }

        private static NumericUpDown Num(Control parent, int x, int y, int w, decimal min, decimal max, decimal value, int decimals = 0)
        {
            var n = new NumericUpDown { Left = x, Top = y, Width = w, Minimum = min, Maximum = max, DecimalPlaces = decimals, Value = value, ThousandsSeparator = false };
            parent.Controls.Add(n);
            return n;
        }

        private static TextBox Mono(Control parent, int x, int y, int w, int h)
        {
            var t = new TextBox
            {
                Left = x, Top = y, Width = w, Height = h, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window, WordWrap = false
            };
            parent.Controls.Add(t);
            return t;
        }

        private static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
        private static string Deg(double cdeg) { return (cdeg / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "°"; }

        private static int Cdeg(decimal degrees) { return (int)Math.Round(degrees * 100m, MidpointRounding.AwayFromZero); }

        private static string StateName(int state)
        {
            switch ((BoardState)state)
            {
                case BoardState.Init: return "init";
                case BoardState.Idle: return "idle";
                case BoardState.FocuserMoving: return "focuser moving";
                case BoardState.RotatorMoving: return "rotator moving";
                case BoardState.ImuBusy: return "IMU homing / calibration";
                case BoardState.Halting: return "halting";
                default: return "state " + state;
            }
        }
    }
}
