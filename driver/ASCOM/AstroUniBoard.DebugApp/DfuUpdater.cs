// Firmware update over USB DFU, the same flow as fw-onstepx/tools/firmware_manager (PR #62):
//  1. check the image (#79): a .bin that fits the 128 KB flash of the STM32G0B1CB, a plausible
//     vector table (initial SP in SRAM, odd reset vector inside the image) and the image marker
//     "ASTROUNIBOARD-IMG:<model>;" matching the model of the identify reply. A missing marker
//     (older images) or another model needs the user's explicit OK (overrideChecks).
//  2. refuse when a 0483:df11 bootloader is already present (it could be another board)
//  3. :31# through the session (the board answers OK and jumps to the STM32 system bootloader;
//     ERR,8 while an axis moves, #76 — the app halts and waits for idle first)
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
    internal enum ImageVerdict
    {
        /// <summary>Vector table plausible, marker matches the board.</summary>
        Ok,
        /// <summary>No marker (images built before it existed): flash only after the user confirms.</summary>
        MissingMarker,
        /// <summary>The marker names another model: flash only when the user forces it.</summary>
        ModelMismatch,
        /// <summary>Never flashable (missing, too big, not a vector table for this MCU).</summary>
        Invalid
    }

    internal sealed class ImageCheck
    {
        public ImageCheck(ImageVerdict verdict, string markerModel, string message)
        {
            Verdict = verdict;
            MarkerModel = markerModel;
            Message = message;
        }

        public static ImageCheck Invalid(string message) { return new ImageCheck(ImageVerdict.Invalid, null, message); }

        public ImageVerdict Verdict { get; private set; }
        /// <summary>The model named by the image marker, or null.</summary>
        public string MarkerModel { get; private set; }
        public string Message { get; private set; }

        /// <summary>Ok, or a warning the user has accepted (<paramref name="userAccepted"/>). Invalid never.</summary>
        public bool MayFlash(bool userAccepted)
        {
            return Verdict == ImageVerdict.Ok || (userAccepted && Verdict != ImageVerdict.Invalid);
        }
    }

    internal enum DfuUtilSource { AppFolder, PathVariable, PlatformIO }

    internal sealed class DfuUtilLocation
    {
        public DfuUtilLocation(string path, DfuUtilSource source) { Path = path; Source = source; }
        public string Path { get; private set; }
        public DfuUtilSource Source { get; private set; }

        public string SourceText
        {
            get
            {
                switch (Source)
                {
                    case DfuUtilSource.AppFolder: return "installed next to this app";
                    case DfuUtilSource.PathVariable: return "found on PATH";
                    default: return "found in PlatformIO (tool-dfuutil)";
                }
            }
        }
    }

    internal sealed class DfuUpdater
    {
        public const string DfuVidPid = "0483:df11";
        public const string FlashAddress = "0x08000000";
        public const int FlashSize = 128 * 1024;
        // STM32G0B1CB: 144 KB SRAM, 128 KB flash
        public const uint SramStart = 0x20000000, SramEnd = 0x20024000;
        public const uint FlashStart = 0x08000000, FlashEnd = 0x08020000;
        /// <summary>The firmware embeds "ASTROUNIBOARD-IMG:&lt;model&gt;;", for example "ASTROUNIBOARD-IMG:REV2;".</summary>
        public const string MarkerPrefix = "ASTROUNIBOARD-IMG:";
        private const int MaxMarkerModel = 32;

        public string DfuUtilPath { get; set; }
        public int WaitSeconds { get; set; } = 10;

        /// <summary>dfu-util next to the app, else on PATH, else PlatformIO's tool-dfuutil; null if none.</summary>
        public static DfuUtilLocation FindDfuUtil()
        {
            return FindDfuUtil(AppDomain.CurrentDomain.BaseDirectory, Environment.GetEnvironmentVariable("PATH"),
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        internal static DfuUtilLocation FindDfuUtil(string appDir, string pathVariable, string home)
        {
            var places = new List<DfuUtilLocation>();
            if (!string.IsNullOrEmpty(appDir)) places.Add(new DfuUtilLocation(Path.Combine(appDir, "dfu-util.exe"), DfuUtilSource.AppFolder));
            foreach (string raw in (pathVariable ?? "").Split(Path.PathSeparator))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    // a relative entry (".", "bin") resolves against the current folder: never use it
                    if (!Path.IsPathRooted(dir)) continue;
                    places.Add(new DfuUtilLocation(Path.Combine(dir, "dfu-util.exe"), DfuUtilSource.PathVariable));
                }
                catch (ArgumentException) { }
            }
            if (!string.IsNullOrEmpty(home))
                places.Add(new DfuUtilLocation(Path.Combine(home, ".platformio", "packages", "tool-dfuutil", "bin", "dfu-util.exe"), DfuUtilSource.PlatformIO));
            DfuUtilLocation found = places.FirstOrDefault(l => File.Exists(l.Path));
            return found == null ? null : new DfuUtilLocation(Path.GetFullPath(found.Path), found.Source);
        }

        /// <summary>True for the dfu-util.exe installed next to this app, the only copy used without asking.</summary>
        public static bool IsNextToApp(string path, string appDir = null)
        {
            try
            {
                string mine = Path.GetFullPath(Path.Combine(appDir ?? AppDomain.CurrentDomain.BaseDirectory, "dfu-util.exe"));
                return string.Equals(Path.GetFullPath(path), mine, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }

        /// <summary>Checks the image file for a board whose identify reply names <paramref name="expectedModel"/>.</summary>
        public static ImageCheck CheckImage(string path, string expectedModel)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ImageCheck.Invalid("firmware file not found: " + path);
            if (!path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return ImageCheck.Invalid("expected a .bin image (raw binary for 0x08000000)");
            long size = new FileInfo(path).Length;
            if (size == 0) return ImageCheck.Invalid("firmware file is empty");
            if (size > FlashSize) return ImageCheck.Invalid("firmware is " + size + " bytes, more than the " + FlashSize + " bytes of flash");
            return CheckImage(File.ReadAllBytes(path), expectedModel);
        }

        internal static ImageCheck CheckImage(byte[] image, string expectedModel)
        {
            if (image.Length < 8) return ImageCheck.Invalid("firmware is only " + image.Length + " bytes: no vector table");
            if (image.Length > FlashSize) return ImageCheck.Invalid("firmware is " + image.Length + " bytes, more than the " + FlashSize + " bytes of flash");
            uint sp = BitConverter.ToUInt32(image, 0);
            uint reset = BitConverter.ToUInt32(image, 4);
            // the initial SP is usually the end of SRAM itself (the stack grows down from there)
            if (sp <= SramStart || sp > SramEnd)
                return ImageCheck.Invalid(string.Format("not a firmware for this board: the initial stack pointer 0x{0:X8} is outside the SRAM 0x{1:X8}–0x{2:X8}", sp, SramStart, SramEnd));
            uint entry = reset & ~1u;
            if ((reset & 1) == 0 || entry < FlashStart || entry >= FlashEnd)
                return ImageCheck.Invalid(string.Format("not a firmware for this board: the reset vector 0x{0:X8} is not an odd (Thumb) address inside the flash 0x{1:X8}–0x{2:X8}", reset, FlashStart, FlashEnd));
            if (entry - FlashStart >= (uint)image.Length)
                return ImageCheck.Invalid(string.Format("the reset vector 0x{0:X8} points past the end of the {1}-byte image (truncated download?)", reset, image.Length));

            string model = FindMarker(image);
            string board = string.IsNullOrEmpty(expectedModel) ? null : expectedModel.Trim();
            if (model == null)
                return new ImageCheck(ImageVerdict.MissingMarker, null,
                    "The image has no \"" + MarkerPrefix + "<model>;\" marker, so it cannot be checked against the board (" + (board ?? "model unknown") + "). " +
                    "Firmware built before the marker was added has none; anything else may be firmware for another device.");
            if (board == null || !string.Equals(model, board, StringComparison.OrdinalIgnoreCase))
                return new ImageCheck(ImageVerdict.ModelMismatch, model,
                    "The image is built for the board model " + model + ", but the connected board reports " + (board ?? "no model") + ".");
            return new ImageCheck(ImageVerdict.Ok, model, "image for " + model + ", " + image.Length + " bytes, vector table OK");
        }

        /// <summary>The model of the first "ASTROUNIBOARD-IMG:&lt;model&gt;;" in the image, or null.</summary>
        internal static string FindMarker(byte[] image)
        {
            byte[] prefix = Encoding.ASCII.GetBytes(MarkerPrefix);
            for (int i = 0; i + prefix.Length < image.Length; i++)
            {
                int k = 0;
                while (k < prefix.Length && image[i + k] == prefix[k]) k++;
                if (k < prefix.Length) continue;
                int start = i + prefix.Length, end = start;
                while (end < image.Length && end - start < MaxMarkerModel && IsModelChar(image[end])) end++;
                if (end > start && end < image.Length && image[end] == (byte)';') return Encoding.ASCII.GetString(image, start, end - start);
            }
            return null;
        }

        private static bool IsModelChar(byte b)
        {
            return (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '_' || b == '-' || b == '.';
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
        /// <paramref name="overrideChecks"/>: the user accepted a missing marker or forced another model.
        /// The board must be idle (the app halts both axes first, #76).
        /// </summary>
        public bool Update(IBoardSession session, string image, Action<string> log, bool overrideChecks = false)
        {
            BoardView before = session.Snapshot();
            string model = before.Identity != null ? before.Identity.Model : null;
            ImageCheck check = CheckImage(image, model);
            if (!check.MayFlash(overrideChecks)) { log("not flashing: " + check.Message); return false; }
            if (check.Verdict != ImageVerdict.Ok) log("WARNING (accepted by the user): " + check.Message);
            if (before.Status != null && before.Status.Busy) { log("not flashing: the board is busy (an axis is moving) — halt both axes and wait until it is idle"); return false; }
            string oldFw = before.Identity != null ? before.Identity.Firmware : "?";
            string serial = before.Identity != null ? before.Identity.Serial : null;
            log("image " + image + ", " + new FileInfo(image).Length + " bytes, marker " + (check.MarkerModel ?? "none") + "; board " + (model ?? "?") + " " + (serial ?? "?") + " running " + oldFw);
            log("dfu-util: " + DfuUtilPath);

            if (CountBootloaders() > 0) { log("a DFU bootloader is already present — finish or unplug it first, so the right board gets flashed"); return false; }
            string r = session.Do(BoardAction.EnterDfu);
            log(":31# → " + Replies.Explain(r));
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
