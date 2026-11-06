// Firmware tab safety (#79 image and dfu-util checks, #76 halt before :22#/:31#, #75 rotator refusal
// text) against crafted .bin files and the in-memory FakeBoard.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AstroUniBoard.Core;
using AstroUniBoard.Core.Tests;
using Xunit;

namespace AstroUniBoard.DebugApp.Tests
{
    public class ImageChecks : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "aub-fw-" + Guid.NewGuid().ToString("N"));

        public ImageChecks() { Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

        /// <summary>A plausible image: SP = end of SRAM, reset handler at 0x080000C1, marker in the middle.</summary>
        private static byte[] Image(uint sp = 0x20024000, uint reset = 0x080000C1, string marker = "ASTROUNIBOARD-IMG:REV2;", int size = 4096)
        {
            var b = new byte[size];
            BitConverter.GetBytes(sp).CopyTo(b, 0);
            BitConverter.GetBytes(reset).CopyTo(b, 4);
            if (marker != null) Encoding.ASCII.GetBytes(marker).CopyTo(b, size / 2);
            return b;
        }

        private string Write(byte[] data, string name = "firmware.bin")
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, data);
            return p;
        }

        [Fact]
        public void GoodImageForThisModelPasses()
        {
            ImageCheck c = DfuUpdater.CheckImage(Write(Image()), "REV2");
            Assert.Equal(ImageVerdict.Ok, c.Verdict);
            Assert.Equal("REV2", c.MarkerModel);
            Assert.True(c.MayFlash(false));
        }

        [Fact]
        public void ModelIsComparedCaseInsensitively()
        {
            Assert.Equal(ImageVerdict.Ok, DfuUpdater.CheckImage(Image(), "rev2").Verdict);
        }

        [Fact]
        public void MissingMarkerNeedsTheUsersOk()
        {
            ImageCheck c = DfuUpdater.CheckImage(Write(Image(marker: null)), "REV2");
            Assert.Equal(ImageVerdict.MissingMarker, c.Verdict);
            Assert.False(c.MayFlash(false));
            Assert.True(c.MayFlash(true));
            Assert.Contains("marker", c.Message);
        }

        [Fact]
        public void OtherModelBlocksUnlessForced()
        {
            ImageCheck c = DfuUpdater.CheckImage(Image(marker: "ASTROUNIBOARD-IMG:REV1;"), "REV2");
            Assert.Equal(ImageVerdict.ModelMismatch, c.Verdict);
            Assert.Equal("REV1", c.MarkerModel);
            Assert.False(c.MayFlash(false));
            Assert.True(c.MayFlash(true));
            Assert.Contains("REV1", c.Message);
            Assert.Contains("REV2", c.Message);
        }

        [Fact]
        public void UnknownBoardModelIsAMismatch()
        {
            Assert.Equal(ImageVerdict.ModelMismatch, DfuUpdater.CheckImage(Image(), null).Verdict);
        }

        [Theory]
        [InlineData("ASTROUNIBOARD-IMG:REV2")]          // no terminating ';'
        [InlineData("ASTROUNIBOARD-IMG:;")]             // empty model
        [InlineData("ASTROUNIBOARD-IMG:RE V2;")]        // not a model name
        [InlineData("OPENWHEEL-IMG:REV2;")]
        public void MalformedMarkersCountAsMissing(string marker)
        {
            Assert.Equal(ImageVerdict.MissingMarker, DfuUpdater.CheckImage(Image(marker: marker), "REV2").Verdict);
        }

        [Fact]
        public void MarkerAtTheVeryEndIsFound()
        {
            byte[] b = Image(marker: null);
            byte[] m = Encoding.ASCII.GetBytes("ASTROUNIBOARD-IMG:REV2;");
            m.CopyTo(b, b.Length - m.Length);
            Assert.Equal("REV2", DfuUpdater.FindMarker(b));
        }

        [Theory]
        [InlineData(0x20000000u)]   // the SRAM start itself: nothing below it for a stack
        [InlineData(0x20024004u)]   // past the 144 KB SRAM
        [InlineData(0x10000000u)]
        [InlineData(0xFFFFFFFFu)]   // erased flash
        [InlineData(0u)]
        public void StackPointerOutsideSramIsInvalid(uint sp)
        {
            ImageCheck c = DfuUpdater.CheckImage(Image(sp: sp), "REV2");
            Assert.Equal(ImageVerdict.Invalid, c.Verdict);
            Assert.False(c.MayFlash(true));
            Assert.Contains("stack pointer", c.Message);
        }

        [Theory]
        [InlineData(0x080000C0u)]   // even: not a Thumb address
        [InlineData(0x08020001u)]   // past the 128 KB flash
        [InlineData(0x00000101u)]
        [InlineData(0x20000101u)]   // in SRAM
        public void ResetVectorOutsideFlashOrEvenIsInvalid(uint reset)
        {
            ImageCheck c = DfuUpdater.CheckImage(Image(reset: reset), "REV2");
            Assert.Equal(ImageVerdict.Invalid, c.Verdict);
            Assert.False(c.MayFlash(true));
            Assert.Contains("reset vector", c.Message);
        }

        [Fact]
        public void TruncatedImageWhoseResetHandlerIsMissingIsInvalid()
        {
            ImageCheck c = DfuUpdater.CheckImage(Image(reset: 0x08001001, size: 1024), "REV2");
            Assert.Equal(ImageVerdict.Invalid, c.Verdict);
            Assert.Contains("truncated", c.Message);
        }

        [Fact]
        public void TooShortTooBigEmptyMissingAndNotBinAreInvalid()
        {
            Assert.Equal(ImageVerdict.Invalid, DfuUpdater.CheckImage(Write(new byte[4], "short.bin"), "REV2").Verdict);
            Assert.Equal(ImageVerdict.Invalid, DfuUpdater.CheckImage(Write(new byte[0], "empty.bin"), "REV2").Verdict);
            Assert.Equal(ImageVerdict.Invalid, DfuUpdater.CheckImage(Write(Image(size: DfuUpdater.FlashSize + 4), "big.bin"), "REV2").Verdict);
            Assert.Equal(ImageVerdict.Invalid, DfuUpdater.CheckImage(Write(Image(), "firmware.hex"), "REV2").Verdict);
            Assert.Equal(ImageVerdict.Invalid, DfuUpdater.CheckImage(Path.Combine(dir, "none.bin"), "REV2").Verdict);
            Assert.Equal(ImageVerdict.Ok, DfuUpdater.CheckImage(Write(Image(size: DfuUpdater.FlashSize), "full.bin"), "REV2").Verdict);
        }
    }

    public class DfuUtilLookup : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "aub-dfu-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

        private string Dir(string name, bool withExe)
        {
            string d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            if (withExe) File.WriteAllText(Path.Combine(d, "dfu-util.exe"), "");
            return d;
        }

        [Fact]
        public void TheCopyNextToTheAppWins()
        {
            string app = Dir("app", true), onPath = Dir("path", true);
            DfuUtilLocation l = DfuUpdater.FindDfuUtil(app, onPath, null);
            Assert.Equal(DfuUtilSource.AppFolder, l.Source);
            Assert.Equal(Path.Combine(app, "dfu-util.exe"), l.Path);
            Assert.True(DfuUpdater.IsNextToApp(l.Path, app));
        }

        [Fact]
        public void PathThenPlatformIOAreMarkedAsSuch()
        {
            string app = Dir("app", false), empty = Dir("empty", false), onPath = Dir("path", true);
            string home = Path.Combine(root, "home");
            Directory.CreateDirectory(Path.Combine(home, ".platformio", "packages", "tool-dfuutil", "bin"));
            File.WriteAllText(Path.Combine(home, ".platformio", "packages", "tool-dfuutil", "bin", "dfu-util.exe"), "");

            DfuUtilLocation l = DfuUpdater.FindDfuUtil(app, empty + ";\"" + onPath + "\"", home);
            Assert.Equal(DfuUtilSource.PathVariable, l.Source);
            Assert.Equal(Path.Combine(onPath, "dfu-util.exe"), l.Path);
            Assert.False(DfuUpdater.IsNextToApp(l.Path, app));

            l = DfuUpdater.FindDfuUtil(app, empty, home);
            Assert.Equal(DfuUtilSource.PlatformIO, l.Source);
            Assert.Null(DfuUpdater.FindDfuUtil(app, empty, Path.Combine(root, "nohome")));
        }

        [Fact]
        public void RelativePathEntriesAreIgnored()
        {
            string app = Dir("app", false);
            string cwd = Dir("cwd", true);
            string before = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = cwd;
                Assert.Null(DfuUpdater.FindDfuUtil(app, ".;bin;" + Path.Combine(root, "missing"), null));
            }
            finally { Environment.CurrentDirectory = before; }
        }
    }

    /// <summary>#76 on the host: no reset while an axis moves; the app halts and waits first.</summary>
    public class ResetWhileMoving : IDisposable
    {
        private readonly FakeBoard board = new FakeBoard { PortName = "COM9", MoveMs = 1500 };
        private readonly AscomSession session;

        public ResetWhileMoving()
        {
            var en = new FakeEnumerator();
            en.Devices.Add(new UsbSerialDevice { PortName = "COM9", Vid = 0x1209, Pid = 0x0001, UsbSerial = board.Serial });
            var boards = new Dictionary<string, FakeBoard> { ["COM9"] = board };
            var opt = new BoardOptions { CommandTimeoutMs = 150, PollIdleMs = 50, PollBusyMs = 20, HousekeepingMs = 200, EnvironmentMs = 200, ReconnectMinMs = 50, ReconnectMaxMs = 200 };
            session = new AscomSession(() => new LoopbackDriver(new DirectSession(null, n => boards[n], en, opt)));
            session.Open();
        }

        public void Dispose() { session.Dispose(); }

        private int Resets() { return board.Received.FindAll(c => c == "22" || c == "31").Count; }

        [Theory]
        [InlineData("Reboot")]
        [InlineData("EnterDfu")]
        public void ActionsRefuseWhileMovingEvenOnOldFirmware(string name)
        {
            var action = (BoardAction)Enum.Parse(typeof(BoardAction), name);
            board.RefuseResetWhileBusy = false;          // firmware before #76 would reset at once
            Assert.Equal("OK", session.Raw(Commands.FocuserMoveTo(6000)));
            string r = session.Do(action);
            Assert.StartsWith("ERROR: ", r);
            Assert.Contains("halt both axes first", r);
            Assert.Equal(0, Resets());
            Assert.True(session.Snapshot().IsConnected);
        }

        [Fact]
        public void RawResetIsRefusedWhileMoving()
        {
            Assert.Equal("OK", session.Raw(Commands.RotatorMoveTo(0)));
            Assert.StartsWith("ERROR: an axis is moving", session.Raw(Commands.Reboot));
            Assert.Equal(0, Resets());
            Assert.True(session.Snapshot().IsConnected);
        }

        [Fact]
        public void HaltAndWaitIdleThenEnterDfu()
        {
            Assert.Equal("OK", session.Raw(Commands.FocuserMoveTo(6000)));
            Assert.Null(session.HaltAndWaitIdle(3000));
            Assert.False(session.Snapshot().Status.Busy);
            Assert.Equal("OK", session.Do(BoardAction.EnterDfu));
            Assert.Equal(1, Resets());
        }

        [Fact]
        public void HaltAndWaitIdleReportsABoardThatStaysBusy()
        {
            board.StuckMoving = true;
            board.HaltIgnored = true;
            Assert.Equal("OK", session.Raw(Commands.FocuserMoveTo(6000)));
            string problem = session.HaltAndWaitIdle(500);
            Assert.NotNull(problem);
            Assert.Contains("still busy", problem);
        }
    }

    /// <summary>#75 on the host: a refused rotator move while the angle is unverified is explained.</summary>
    public class RotatorUnverified
    {
        private static StatusView Status(int flags) { return new StatusView { State = 1, Flags = flags }; }

        [Fact]
        public void RefusedMoveWithTheUnverifiedFlagIsExplained()
        {
            Assert.Equal(BoardErrors.RotatorUnverifiedHint, Replies.RotatorRefusalHint(":71,9000#", "ERR,7", Status((int)StatusFlags.RotatorUnverified)));
            Assert.Equal(BoardErrors.RotatorUnverifiedHint, Replies.RotatorRefusalHint(":72,-500#", "ERR,17", Status((int)StatusFlags.RotatorUnverified | 0x30)));
        }

        [Fact]
        public void OtherCasesGiveNoHint()
        {
            StatusView unverified = Status((int)StatusFlags.RotatorUnverified);
            Assert.Null(Replies.RotatorRefusalHint(":71,9000#", "OK", unverified));
            Assert.Null(Replies.RotatorRefusalHint(":71,9000#", "ERR,8", unverified));      // busy is its own message
            Assert.Null(Replies.RotatorRefusalHint(":71,9000#", "ERR,7", Status(0x30)));    // angle verified: a real limit
            Assert.Null(Replies.RotatorRefusalHint(":51,100#", "ERR,7", unverified));       // focuser
            Assert.Null(Replies.RotatorRefusalHint(":71,9000#", "ERR,7", null));
        }
    }
}
