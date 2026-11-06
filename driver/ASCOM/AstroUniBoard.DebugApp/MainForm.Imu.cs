// IMU tab: live :94# readout, stored calibration :96#, IMU home :77#, and the guided IMU zero
// calibration :95# (PROTOCOL.md §4.3): camera landscape upright, scope away from the zenith, 180° of
// cable wrap above the current angle; the rotator turns +90°, +180° and back (about 35 s).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private TabPage tabImu;
        private CheckBox chkImuLive;
        private Label lblImu, lblImuCal, lblCalChecks, lblCalProgress;
        private CheckBox chkCal1, chkCal2, chkCal3;
        private Button btnCalStart;
        private int? imuTiltCdeg;
        private Stopwatch calClock;
        private bool calSawBusy;

        private void BuildImuTab()
        {
            TabPage p = tabImu = AddTab("IMU");
            chkImuLive = new CheckBox { Text = "Live readout (:94# every 0.5 s)", Left = 16, Top = 10, Width = 260, Checked = true };
            p.Controls.Add(chkImuLive);
            Btn(p, "Read calibration (:96#)", 290, 7, 170, async (s, e) => await ReadImuCal());
            Btn(p, "IMU home (:77#)", 470, 7, 130, async (s, e) => await DoAction(BoardAction.ImuHome));
            lblImu = Lbl(p, "—", 16, 40, 920, 66);
            lblImu.Font = new Font(FontFamily.GenericMonospace, 10f);
            lblImuCal = Lbl(p, "", 16, 108, 920, 36);
            lblImuCal.Font = new Font(FontFamily.GenericMonospace, 9f);
            lblImuCal.ForeColor = Color.DimGray;

            var g = new GroupBox { Text = "Guided IMU zero calibration (:95#)", Left = 10, Top = 150, Width = 930, Height = 400 };
            p.Controls.Add(g);
            var intro = Lbl(g,
                "Defines the IMU zero (camera landscape, upright) and the rotation axis. The firmware records gravity, turns the rotator +90° and records it, " +
                "then +180° and records it, and returns to the start. It works at any elevation except near the zenith. On success the calibration is written " +
                "to the EEPROM at once and the current angle becomes 0°. Failure (a step not about 90°: rotator slipped or IMU moved) is sticky error 3.",
                12, 22, 900, 52);
            intro.ForeColor = Color.DimGray;
            chkCal1 = new CheckBox { Text = "1. The camera is landscape and upright (this becomes 0°).", Left = 12, Top = 80, Width = 880 };
            chkCal2 = new CheckBox { Text = "2. The scope points well away from the zenith (more than ~20° from vertical) and is not tracking into a limit.", Left = 12, Top = 104, Width = 880 };
            chkCal3 = new CheckBox { Text = "3. Cables and camera are free to turn +180° and back (about 35 s); nothing else is moving the rotator (N.I.N.A. idle).", Left = 12, Top = 128, Width = 880 };
            g.Controls.AddRange(new Control[] { chkCal1, chkCal2, chkCal3 });
            lblCalChecks = Lbl(g, "", 12, 156, 900, 70);
            lblCalChecks.Font = new Font(FontFamily.GenericMonospace, 9f);
            btnCalStart = Btn(g, "Start calibration", 12, 232, 160, async (s, e) =>
            {
                if (!Confirm("Start the IMU zero calibration now? The rotator turns +90°, +180° and back.")) return;
                string r = await DoAction(BoardAction.ImuCalibrate);
                if (Replies.IsFailure(r)) { lblCalProgress.Text = "Not started: " + Replies.Explain(r); lblCalProgress.ForeColor = Color.Firebrick; return; }
                calClock = Stopwatch.StartNew();
                calSawBusy = false;
                lblCalProgress.ForeColor = SystemColors.ControlText;
            }, false);
            Btn(g, "Abort (halt all)", 180, 232, 130, async (s, e) => await DoAction(BoardAction.HaltAll)).ForeColor = Color.Firebrick;
            Btn(g, "Sync angle to 0° first (:76,0#)", 318, 232, 210, async (s, e) =>
            {
                if (Confirm("Set the current rotator angle to 0° (no motion)? Use this when the cable-wrap check fails because the angle is wrong."))
                    await Send(Commands.RotatorSync(0), "rotator set home");
            });
            lblCalProgress = Lbl(g, "", 12, 268, 900, 120);
            lblCalProgress.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            foreach (var c in new[] { chkCal1, chkCal2, chkCal3 }) c.CheckedChanged += (s, e) => UpdateImuTab(view);
        }

        private void ShowImu(string reply)
        {
            ImuReading imu;
            if (!ImuReading.TryParse(reply, out imu))
            {
                lblImu.Text = "IMU: " + Replies.Explain(reply);
                imuTiltCdeg = null;
                return;
            }
            imuTiltCdeg = imu.TiltCdeg;
            lblImu.Text = "roll  " + (imu.RollCdeg.HasValue ? Deg(imu.RollCdeg.Value) : "undefined (not calibrated or too steep)") + "\n" +
                          "tilt  " + (imu.TiltCdeg.HasValue ? Deg(imu.TiltCdeg.Value) + " from vertical" : "undefined (not calibrated)") + "\n" +
                          "accel " + imu.Ax + " / " + imu.Ay + " / " + imu.Az + " mg (chip axes), |g| = " +
                          Math.Sqrt((double)imu.Ax * imu.Ax + (double)imu.Ay * imu.Ay + (double)imu.Az * imu.Az).ToString("0", CultureInfo.InvariantCulture) + " mg";
        }

        private async System.Threading.Tasks.Task ReadImuCal()
        {
            string r = await Send(Commands.ImuCalibrationData, "IMU calibration");
            string[] f = r.Split(',');
            if (f.Length == 11 && f[0] == "C")
            {
                lblImuCal.Text = "stored calibration: " + (f[1] == "1" ? "VALID" : "none") + "   g0 = (" + f[2] + ", " + f[3] + ", " + f[4] + ") mg   axis n = (" +
                                 f[5] + ", " + f[6] + ", " + f[7] + ") /10000   circle centre c = (" + f[8] + ", " + f[9] + ", " + f[10] + ") mg";
            }
            else lblImuCal.Text = "calibration: " + Replies.Explain(r);
        }

        private void UpdateImuTab(BoardView v)
        {
            StatusView st = v.Status;
            var sb = new StringBuilder();
            bool ok = true;
            if (st == null) { lblCalChecks.Text = "not connected"; btnCalStart.Enabled = false; return; }
            bool answering = st.Has(StatusFlags.ImuAnswering);
            sb.AppendLine((answering ? "[ok] " : "[!!] ") + "IMU answering" + (answering ? "" : " — check the IMU board and its cable"));
            ok &= answering;
            sb.AppendLine("[--] IMU zero " + (st.Has(StatusFlags.ImuNotCalibrated) ? "not calibrated yet" : "calibrated (this run replaces it)"));
            if (imuTiltCdeg.HasValue)
            {
                bool steep = imuTiltCdeg.Value < 2000;
                sb.AppendLine((steep ? "[!!] " : "[ok] ") + "tilt " + Deg(imuTiltCdeg.Value) + " from vertical" + (steep ? " — too close to the zenith" : ""));
                ok &= !steep;
            }
            else sb.AppendLine("[--] tilt unknown until calibrated: judge it yourself (step 2)");
            if (v.Rotator != null && rotSignedCdeg.HasValue)
            {
                int room = v.Rotator.WrapMaxCdeg - rotSignedCdeg.Value;
                bool enough = room >= 18000;
                sb.AppendLine((enough ? "[ok] " : "[!!] ") + "cable wrap above the current angle: " + Deg(room) + " (needs 180°)" +
                              (enough ? "" : " — sync to 0° first if the angle is wrong, else ERR,7"));
                ok &= enough;
            }
            else sb.AppendLine("[--] cable wrap: waiting for the signed angle");
            bool idle = st.State == (int)BoardState.Idle;
            if (!idle) sb.AppendLine("[!!] board busy: " + StateName(st.State));
            ok &= idle;
            string text = sb.ToString().TrimEnd();
            if (lblCalChecks.Text != text) lblCalChecks.Text = text;
            btnCalStart.Enabled = session != null && v.IsConnected && ok && chkCal1.Checked && chkCal2.Checked && chkCal3.Checked && calClock == null;

            if (calClock != null)
            {
                double s = calClock.Elapsed.TotalSeconds;
                if (st.State == (int)BoardState.ImuBusy) calSawBusy = true;
                if (st.Busy || (!calSawBusy && s < 3))
                {
                    lblCalProgress.Text = "Calibrating… " + s.ToString("0", CultureInfo.InvariantCulture) + " s (about 35 s), rotator at " + Deg(st.RotatorAngle);
                    lblCalProgress.ForeColor = Color.DarkOrange;
                }
                else
                {
                    calClock = null;
                    if (st.Error == (int)BoardError.CalibrationFailed || st.Error != 0)
                    {
                        lblCalProgress.Text = "FAILED after " + s.ToString("0", CultureInfo.InvariantCulture) + " s: error " + st.Error + " — " + BoardErrors.Describe(st.Error) +
                                              "\nClear the error, check that the IMU board is fixed and the rotator does not slip, and try again.";
                        lblCalProgress.ForeColor = Color.Firebrick;
                    }
                    else if (st.Has(StatusFlags.ImuNotCalibrated))
                    {
                        lblCalProgress.Text = "Finished, but the board still reports \"IMU not calibrated\" — was it halted? Check the log.";
                        lblCalProgress.ForeColor = Color.Firebrick;
                    }
                    else
                    {
                        lblCalProgress.Text = "Calibration done in " + s.ToString("0", CultureInfo.InvariantCulture) + " s. Stored in the EEPROM; the current angle is 0°.\n" +
                                              "Check: roll should read about 0.00° now. IMU home (:77#) works from now on.";
                        lblCalProgress.ForeColor = Color.DarkGreen;
                        var _ = ReadImuCal();
                    }
                }
            }
        }
    }
}
