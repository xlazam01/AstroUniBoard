// Focuser and rotator tabs: jog, absolute moves, halt, sync / set home, and the settings of both
// axes (get/set pairs of PROTOCOL.md 4.1 and 4.2) with save to the EEPROM.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed partial class MainForm
    {
        private sealed class SettingRow
        {
            public string Name;
            /// <summary>Set command number; 75 (cable wrap) is sent as one command for min and max.</summary>
            public int SetCommand;
            public Func<BoardView, int?> Current;
            public string Unit;
        }

        private TabPage tabFocuser, tabRotator;
        private Label lblFocPos, lblRotPos, lblRotSigned;
        private NumericUpDown numFocJog, numFocAbs, numFocSync, numRotJog, numRotAbs, numRotSync;
        private DataGridView gridFoc, gridRot;
        private bool settingsLoaded;
        private int? rotSignedCdeg;

        private static readonly SettingRow[] FocRows =
        {
            new SettingRow { Name = "Max position", SetCommand = 55, Current = v => v.Focuser?.MaxPosition, Unit = "steps, 1..10000000, ≥ position" },
            new SettingRow { Name = "Speed", SetCommand = 59, Current = v => v.Focuser?.Speed, Unit = "steps/s, 1..20000" },
            new SettingRow { Name = "Acceleration", SetCommand = 61, Current = v => v.Focuser?.Acceleration, Unit = "steps/s², 1..100000" },
            new SettingRow { Name = "Backlash", SetCommand = 63, Current = v => v.Focuser?.Backlash, Unit = "steps, 0..10000 (0 off; 0 if N.I.N.A. compensates)" },
            new SettingRow { Name = "Reverse", SetCommand = 65, Current = v => v.Focuser == null ? (int?)null : (v.Focuser.Reverse ? 1 : 0), Unit = "0 / 1 (direction only)" },
            new SettingRow { Name = "Step size", SetCommand = 67, Current = v => v.Focuser?.StepSizeCentiMicrons, Unit = "0.01 µm per step, 0 = unknown" }
        };

        private static readonly SettingRow[] RotRows =
        {
            new SettingRow { Name = "Cable wrap min", SetCommand = 75, Current = v => v.Rotator?.WrapMinCdeg, Unit = "cdeg, -36000..0" },
            new SettingRow { Name = "Cable wrap max", SetCommand = 75, Current = v => v.Rotator?.WrapMaxCdeg, Unit = "cdeg, 0..36000 (current angle must be inside)" },
            new SettingRow { Name = "Speed", SetCommand = 79, Current = v => v.Rotator?.SpeedCdegPerS, Unit = "cdeg/s, 10..3600 (StallGuard needs ≥ 1800)" },
            new SettingRow { Name = "Acceleration", SetCommand = 81, Current = v => v.Rotator?.AccelerationCdegPerS2, Unit = "cdeg/s², 10..36000" },
            new SettingRow { Name = "Reverse", SetCommand = 83, Current = v => v.Rotator == null ? (int?)null : (v.Rotator.Reverse ? 1 : 0), Unit = "0 / 1" },
            new SettingRow { Name = "Steps per 1000°", SetCommand = 85, Current = v => v.Rotator?.StepsPerKiloDegree, Unit = "microsteps, 1000..10000000" },
            new SettingRow { Name = "Boot home", SetCommand = 87, Current = v => v.Rotator == null ? (int?)null : (v.Rotator.BootHome ? 1 : 0), Unit = "0 / 1 (IMU home after power-on)" },
            new SettingRow { Name = "Home tolerance", SetCommand = 89, Current = v => v.Rotator?.HomeToleranceCdeg, Unit = "cdeg, 5..500" }
        };

        // ------------------------------------------------------------------ focuser

        private void BuildFocuserTab()
        {
            TabPage p = tabFocuser = AddTab("Focuser");
            lblFocPos = Lbl(p, "—", 16, 10, 600, 40);
            lblFocPos.Font = new Font("Segoe UI", 18f, FontStyle.Bold);

            int y = 60;
            Lbl(p, "Jog (relative, :52):", 16, y + 5, 130);
            int x = 150;
            foreach (int d in new[] { -1000, -100, -10, 10, 100, 1000 })
            {
                int delta = d;
                Btn(p, (d > 0 ? "+" : "") + I(d), x, y, 62, async (s, e) => await Send(Commands.FocuserMoveBy(delta), "focuser jog"));
                x += 66;
            }
            numFocJog = Num(p, x + 10, y + 2, 80, -10000000, 10000000, 500);
            Btn(p, "Move by", x + 95, y, 80, async (s, e) => await Send(Commands.FocuserMoveBy((int)numFocJog.Value), "focuser move by"));
            y += 36;
            Lbl(p, "Absolute (:51):", 16, y + 5, 130);
            numFocAbs = Num(p, 150, y + 2, 100, 0, 10000000, 0);
            Btn(p, "Move to", 256, y, 80, async (s, e) => await Send(Commands.FocuserMoveTo((int)numFocAbs.Value), "focuser move to"));
            Btn(p, "HALT focuser (:53#)", 360, y, 150, async (s, e) => await Send(Commands.FocuserHalt, "focuser halt")).ForeColor = Color.Firebrick;
            y += 36;
            Lbl(p, "Sync (:56, no motion):", 16, y + 5, 130);
            numFocSync = Num(p, 150, y + 2, 100, 0, 10000000, 0);
            Btn(p, "Sync position", 256, y, 104, async (s, e) =>
            {
                int v = (int)numFocSync.Value;
                if (Confirm("Set the focuser position counter to " + v + " without moving? (Clears \"position unverified\".)"))
                    await Send(Commands.FocuserSync(v), "focuser sync");
            });
            y += 44;

            Lbl(p, "Settings (running values; Save writes them to the EEPROM):", 16, y, 600);
            y += 20;
            gridFoc = SettingsGrid(p, 16, y, 760, 190, FocRows);
            int by = y;
            Btn(p, "Apply changes", 790, by, 150, async (s, e) => await ApplySettings(gridFoc, FocRows)); by += 32;
            Btn(p, "Re-read values", 790, by, 150, (s, e) => FillGrid(gridFoc, FocRows, view, true)); by += 32;
            Btn(p, "Save to EEPROM (:23#)", 790, by, 150, async (s, e) => await DoAction(BoardAction.SaveSettings)); by += 32;
            Btn(p, "Defaults (:24#)…", 790, by, 150, async (s, e) =>
            {
                if (Confirm("Reset the running settings of BOTH axes to the firmware defaults (:24#)? They stay in the EEPROM until you save.")) await Send(Commands.ResetSettings, "defaults");
            });
        }

        private void UpdateFocuserTab(BoardView v)
        {
            StatusView st = v.Status;
            if (st == null) { lblFocPos.Text = "—"; return; }
            string max = v.Focuser != null ? " / max " + I(v.Focuser.MaxPosition) : "";
            lblFocPos.Text = "Position " + I(st.FocuserPosition) + max + (st.FocuserTarget >= 0 ? "   → " + I(st.FocuserTarget) : "") +
                             (st.Has(StatusFlags.FocuserUnverified) ? "   (unverified)" : "");
            lblFocPos.ForeColor = st.State == (int)BoardState.FocuserMoving ? Color.DarkOrange : SystemColors.ControlText;
            if (!settingsLoaded && v.Focuser != null && v.Rotator != null)
            {
                settingsLoaded = true;
                FillGrid(gridFoc, FocRows, v, true);
                FillGrid(gridRot, RotRows, v, true);
                numFocAbs.Value = Math.Min(numFocAbs.Maximum, st.FocuserPosition);
                numFocSync.Value = Math.Min(numFocSync.Maximum, Math.Max(0, st.FocuserPosition));
            }
            else
            {
                FillGrid(gridFoc, FocRows, v, false);
                FillGrid(gridRot, RotRows, v, false);
            }
        }

        // ------------------------------------------------------------------ rotator

        private void BuildRotatorTab()
        {
            TabPage p = tabRotator = AddTab("Rotator");
            lblRotPos = Lbl(p, "—", 16, 10, 700, 36);
            lblRotPos.Font = new Font("Segoe UI", 18f, FontStyle.Bold);
            lblRotSigned = Lbl(p, "", 16, 46, 900, 18);
            lblRotSigned.ForeColor = Color.DimGray;

            int y = 70;
            Lbl(p, "Jog (relative, :72):", 16, y + 5, 130);
            int x = 150;
            foreach (decimal d in new[] { -90m, -10m, -1m, -0.1m, 0.1m, 1m, 10m, 90m })
            {
                decimal delta = d;
                Btn(p, (d > 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture) + "°", x, y, 58, async (s, e) => await Send(Commands.RotatorMoveBy(Cdeg(delta)), "rotator jog"));
                x += 62;
            }
            numRotJog = Num(p, x + 10, y + 2, 70, -360, 360, 45, 2);
            Btn(p, "Move by °", x + 85, y, 80, async (s, e) => await Send(Commands.RotatorMoveBy(Cdeg(numRotJog.Value)), "rotator move by"));
            y += 36;
            Lbl(p, "Mechanical angle (:71):", 16, y + 5, 130);
            numRotAbs = Num(p, 150, y + 2, 80, 0, 359.99m, 0, 2);
            Btn(p, "Move to °", 236, y, 80, async (s, e) => await Send(Commands.RotatorMoveTo(Cdeg(numRotAbs.Value) % 36000), "rotator move to"));
            Btn(p, "Go home (:71,0#)", 330, y, 120, async (s, e) => await Send(Commands.RotatorMoveTo(0), "rotator go home"));
            Btn(p, "IMU home (:77#)", 456, y, 120, async (s, e) => await DoAction(BoardAction.ImuHome));
            Btn(p, "HALT rotator (:73#)", 600, y, 150, async (s, e) => await Send(Commands.RotatorHalt, "rotator halt")).ForeColor = Color.Firebrick;
            y += 36;
            Lbl(p, "Sync signed angle (:76):", 16, y + 5, 130);
            numRotSync = Num(p, 150, y + 2, 80, -360, 360, 0, 2);
            Btn(p, "Sync °", 236, y, 80, async (s, e) =>
            {
                int c = Cdeg(numRotSync.Value);
                if (Confirm("Set the rotator's signed mechanical angle to " + Deg(c) + " without moving?")) await Send(Commands.RotatorSync(c), "rotator sync");
            });
            Btn(p, "Set home here (:76,0#)", 330, y, 160, async (s, e) =>
            {
                if (Confirm("Make the current orientation the rotator's 0° (home)? Clears \"angle unverified\".")) await Send(Commands.RotatorSync(0), "rotator set home");
            });
            y += 44;

            Lbl(p, "Settings (running values; Save writes them to the EEPROM). Angles in centidegrees (cdeg, 1/100°):", 16, y, 760);
            y += 20;
            gridRot = SettingsGrid(p, 16, y, 760, 240, RotRows);
            int by = y;
            Btn(p, "Apply changes", 790, by, 150, async (s, e) => await ApplySettings(gridRot, RotRows)); by += 32;
            Btn(p, "Re-read values", 790, by, 150, (s, e) => FillGrid(gridRot, RotRows, view, true)); by += 32;
            Btn(p, "Save to EEPROM (:23#)", 790, by, 150, async (s, e) => await DoAction(BoardAction.SaveSettings));
        }

        private void UpdateRotatorTab(BoardView v)
        {
            StatusView st = v.Status;
            if (st == null) { lblRotPos.Text = "—"; lblRotSigned.Text = ""; return; }
            lblRotPos.Text = "Mechanical " + Deg(st.RotatorAngle) + (st.RotatorTarget >= 0 ? "   → " + Deg(st.RotatorTarget) : "") +
                             (st.Has(StatusFlags.RotatorUnverified) ? "   (unverified)" : "");
            lblRotPos.ForeColor = st.State == (int)BoardState.RotatorMoving || st.State == (int)BoardState.ImuBusy ? Color.DarkOrange : SystemColors.ControlText;
            string wrap = v.Rotator != null ? "   cable wrap " + Deg(v.Rotator.WrapMinCdeg) + " .. " + Deg(v.Rotator.WrapMaxCdeg) : "";
            lblRotSigned.Text = "Signed angle (:70#) " + (rotSignedCdeg.HasValue ? Deg(rotSignedCdeg.Value) : "?") + wrap +
                                "   — 0° landscape upright (home), +90° portrait CCW, ±180° upside down, −90° portrait CW";
        }

        private void ShowSignedAngle(string reply)
        {
            int c;
            rotSignedCdeg = int.TryParse(reply, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out c) ? c : (int?)null;
        }

        // ------------------------------------------------------------------ settings grid

        private static DataGridView SettingsGrid(Control parent, int x, int y, int w, int h, SettingRow[] rows)
        {
            var g = new DataGridView
            {
                Left = x, Top = y, Width = w, Height = h, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AllowUserToResizeRows = false, BackgroundColor = SystemColors.Window
            };
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Setting", ReadOnly = true, Width = 140 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Board", ReadOnly = true, Width = 90 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "New value", Width = 100 });
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Unit / range", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            foreach (SettingRow r in rows) g.Rows.Add(r.Name, "", "", r.Unit);
            g.CellValueChanged += (s, e) => MarkChanged(g, rows);
            parent.Controls.Add(g);
            return g;
        }

        private static void FillGrid(DataGridView g, SettingRow[] rows, BoardView v, bool overwriteNew)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                int? cur = rows[i].Current(v);
                string text = cur.HasValue ? cur.Value.ToString(CultureInfo.InvariantCulture) : "?";
                if ((string)g.Rows[i].Cells[1].Value != text) g.Rows[i].Cells[1].Value = text;
                if (overwriteNew && cur.HasValue && !g.IsCurrentCellInEditMode) g.Rows[i].Cells[2].Value = text;
            }
            MarkChanged(g, rows);
        }

        private static void MarkChanged(DataGridView g, SettingRow[] rows)
        {
            for (int i = 0; i < rows.Length && i < g.Rows.Count; i++)
            {
                bool changed = Convert.ToString(g.Rows[i].Cells[2].Value) != Convert.ToString(g.Rows[i].Cells[1].Value);
                g.Rows[i].Cells[2].Style.BackColor = changed ? Color.LightYellow : SystemColors.Window;
            }
        }

        private async Task ApplySettings(DataGridView g, SettingRow[] rows)
        {
            g.EndEdit();
            var commands = new List<string>();
            int? wrapMin = null, wrapMax = null;
            bool wrapChanged = false;
            for (int i = 0; i < rows.Length; i++)
            {
                string cur = Convert.ToString(g.Rows[i].Cells[1].Value);
                string nw = Convert.ToString(g.Rows[i].Cells[2].Value).Trim();
                int n;
                if (!int.TryParse(nw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                {
                    if (nw.Length > 0) { MessageBox.Show(this, rows[i].Name + ": \"" + nw + "\" is not a whole number.", "AstroUniBoard"); return; }
                    continue;
                }
                if (rows[i].SetCommand == 75)
                {
                    if (rows[i].Name.EndsWith("min", StringComparison.Ordinal)) wrapMin = n; else wrapMax = n;
                    if (nw != cur) wrapChanged = true;
                    continue;
                }
                if (nw != cur) commands.Add(Commands.Set(rows[i].SetCommand, n));
            }
            if (wrapChanged && wrapMin.HasValue && wrapMax.HasValue) commands.Add(Commands.RotatorSetWrap(wrapMin.Value, wrapMax.Value));
            if (commands.Count == 0) { stripText.Text = "Nothing changed"; return; }
            foreach (string c in commands)
            {
                string r = await Send(c, "setting");
                if (Replies.IsFailure(r)) break;
            }
            await PollAsync();
            FillGrid(g, rows, view, true);
            stripText.Text += "   (not saved yet — press Save to EEPROM to keep them)";
        }
    }
}
