// Firmware update over USB DFU, the same flow as fw-onstepx/tools/firmware_manager (PR #62):
//  1. check the image (exists, not empty, fits the 128 KB flash of the STM32G0B1CB)
//  2. refuse when a 0483:df11 bootloader is already present (it could be another board)
//  3. :31# through the session (the board answers OK and jumps to the STM32 system bootloader)
//  4. wait for the bootloader, dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D firmware.bin
//  5. reconnect to the same board and compare the firmware field of the identify reply
// dfu-util on STM32G0 often exits non-zero after a good download (the device resets before the
// final get_status), so success is "File downloaded successfully" in its output.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AstroUniBoard.DebugApp
{
    internal sealed class DfuUpdater
    {
        public const string DfuVidPid = "0483:df11";
        public const string FlashAddress = "0x08000000";
        public const int FlashSize = 128 * 1024;

        public string DfuUtilPath { get; set; }
        public int WaitSeconds { get; set; } = 10;

        /// <summary>dfu-util next to the app, on PATH, or PlatformIO's tool-dfuutil; null if none.</summary>
        public static string FindDfuUtil()
        {
            var places = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dfu-util.exe") };
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                try { places.Add(Path.Combine(dir.Trim(), "dfu-util.exe")); } catch (ArgumentException) { }
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            places.Add(Path.Combine(home, ".platformio", "packages", "tool-dfuutil", "bin", "dfu-util.exe"));
            return places.FirstOrDefault(File.Exists);
        }

        /// <summary>Null when the image looks flashable, else why not.</summary>
        public static string CheckImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "firmware file not found: " + path;
            long size = new FileInfo(path).Length;
            if (size == 0) return "firmware file is empty";
            if (size > FlashSize) return "firmware is " + size + " bytes, more than the " + FlashSize + " bytes of flash";
            if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return "expected a .bin image (raw binary for 0x08000000)";
            return null;
        }

        /// <summary>Number of STM32 bootloaders dfu-util sees (alt=0 lines).</summary>
        public int CountBootloaders()
        {
            string output;
            Run("-l", 10000, out output);
            return output.Split('\n').Count(l => l.ToLowerInvariant().Contains("[" + DfuVidPid + "]") && l.Contains("alt=0"));
        }

        public bool WaitForBootloader(Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < WaitSeconds)
            {
                int n = CountBootloaders();
                if (n == 1) return true;
                if (n > 1) { log("more than one DFU bootloader present — not flashing"); return false; }
                Thread.Sleep(500);
            }
            log("no " + DfuVidPid + " bootloader within " + WaitSeconds + " s (on Windows it needs the WinUSB driver, see Zadig)");
            return false;
        }

        public bool Flash(string image, Action<string> log)
        {
            string output;
            // skip dfu-util's progress bars except the final 100 % line
            Action<string> quiet = l => { if (l.Contains("[") && l.Contains("%") && !l.Contains("100%")) return; log(l); };
            int exit = Run("-d " + DfuVidPid + " -a 0 -s " + FlashAddress + ":leave -D \"" + image + "\"", 120000, out output, quiet);
            bool ok = output.Contains("File downloaded successfully");
            log("dfu-util exit code " + exit + (ok ? " — download successful" : " — download FAILED"));
            return ok;
        }

        /// <summary>
        /// The whole update through a connected session (blocking, call it off the UI thread).
        /// Returns true when the board came back after a good download; <paramref name="log"/> gets every step.
        /// </summary>
        public bool Update(IBoardSession session, string image, Action<string> log)
        {
            string problem = CheckImage(image);
            if (problem != null) { log(problem); return false; }
            BoardView before = session.Snapshot();
            string oldFw = before.Identity != null ? before.Identity.Firmware : "?";
            string serial = before.Identity != null ? before.Identity.Serial : null;
            log("image " + image + ", " + new FileInfo(image).Length + " bytes; board " + (serial ?? "?") + " running " + oldFw);

            if (CountBootloaders() > 0) { log("a DFU bootloader is already present — finish or unplug it first, so the right board gets flashed"); return false; }
            string r = session.Do(BoardAction.EnterDfu);
            log(":31# → " + r);
            if (!Replies.IsOk(r)) return false;
            log("waiting for the bootloader (" + DfuVidPid + ")…");
            if (!WaitForBootloader(log)) { log("update NOT done; power-cycle the board if it stays in DFU mode (the old firmware starts)"); return false; }
            log("bootloader is up, flashing…");
            if (!Flash(image, log)) { log("update NOT done; the board stays in DFU mode — flash again, or power-cycle it"); return false; }

            log("waiting for the board to come back…");
            var sw = Stopwatch.StartNew();
            Thread.Sleep(1500);
            while (sw.Elapsed.TotalSeconds < 20)
            {
                try
                {
                    session.Reconnect();
                    BoardView v = session.Snapshot();
                    if (v.IsConnected && v.Identity != null)
                    {
                        bool same = serial == null || string.Equals(v.Identity.Serial, serial, StringComparison.OrdinalIgnoreCase);
                        log("back: " + v.Identity.Model + " " + v.Identity.Firmware + " protocol " + v.Identity.Protocol + " serial " + v.Identity.Serial +
                            "   (" + oldFw + " → " + v.Identity.Firmware + ")" + (same ? "" : "  — WARNING: not the serial flashed (" + serial + ")"));
                        return same;
                    }
                }
                catch (Exception ex) { log("not yet: " + ex.Message); }
                Thread.Sleep(1000);
            }
            log("the board did not come back within 20 s — check Device Manager; reconnect by hand (Disconnect, Connect)");
            return false;
        }

        private int Run(string args, int timeoutMs, out string output, Action<string> log = null)
        {
            if (string.IsNullOrEmpty(DfuUtilPath) || !File.Exists(DfuUtilPath)) throw new InvalidOperationException("dfu-util not found — choose dfu-util.exe");
            var psi = new ProcessStartInfo(DfuUtilPath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (sb) sb.AppendLine(e.Data);
                    if (log != null && e.Data.Trim().Length > 0) log(e.Data);
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    lock (sb) output = sb.ToString();
                    return -1;
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString();
                return p.ExitCode;
            }
        }
    }
}
