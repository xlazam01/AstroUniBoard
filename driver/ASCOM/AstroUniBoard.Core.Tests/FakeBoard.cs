// An in-memory AstroUniBoard (protocol 2.0) that can misbehave the way real devices do.
// Only what the Core needs; the full simulator for driver tests is driver/sim (Python).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AstroUniBoard.Core;

namespace AstroUniBoard.Core.Tests
{
    public enum Misbehavior
    {
        None,
        /// <summary>Never answers anything.</summary>
        Silent,
        /// <summary>Answers with binary junk.</summary>
        Garbage,
        /// <summary>Answers but never sends the terminating '#' (partial line).</summary>
        NoTerminator,
        /// <summary>Sends a long stream of characters without '#'.</summary>
        Flood,
        /// <summary>Answers "OK#" to every command.</summary>
        AlwaysOk,
        /// <summary>Replies arrive after ReplyDelayMs.</summary>
        Slow
    }

    public sealed class FakeBoard : ILink
    {
        private readonly object gate = new object();
        private readonly Queue<byte> rx = new Queue<byte>();
        private readonly StringBuilder cmd = new StringBuilder();
        private bool inCmd;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long releaseAtMs;

        // identity
        public string Serial = "2076349A4B31";
        public string Model = "REV2";
        public string Firmware = "FW2.0.0";
        public int ProtocolVersion = 2;
        public int Caps = 0xF;
        /// <summary>Answer identify like an OpenWheel (same shared test PID).</summary>
        public bool IsOpenWheel;

        // device model
        public int FocPos = 4000, FocMax = 8100, FocSpeed = 900;
        public int RotCdeg = 9000;           // signed mechanical angle
        public int WrapMin = -18000, WrapMax = 18000;
        public int MoveMs = 300;
        public int ErrorCode;
        public int Flags = 0x30;             // both SHT31 valid
        public bool StuckMoving;
        public bool DropReplyToMove;
        public bool NoSensor;                // :90# answers ERR,14
        /// <summary>:22# / :31# answer ERR,8 while an axis moves (#76); false = firmware before that fix.</summary>
        public bool RefuseResetWhileBusy = true;
        /// <summary>Halts answer OK but the move goes on.</summary>
        public bool HaltIgnored;
        /// <summary>Non-zero: rotator moves are refused with this code while flag 2 (angle unverified) is set (#75).</summary>
        public int RefuseUnverifiedRotatorCode;
        public int Warnings;
        public Misbehavior Mode = Misbehavior.None;
        public int ReplyDelayMs;
        public bool Identified;

        // observation
        public readonly List<string> Received = new List<string>();
        public int HaltCount;
        public int OpenCount;

        private int state = 1, focTarget = -1, rotTarget = -1, rotTargetSigned;
        private long moveEndMs;

        private bool open, unplugged;
        public string PortName = "COMFAKE";
        public string Name { get { return PortName; } }
        public bool IsOpen { get { return open && !unplugged; } }

        public void Unplug() { lock (gate) { unplugged = true; } }
        public void Replug() { lock (gate) { unplugged = false; open = false; rx.Clear(); Identified = false; } }
        /// <summary>Another program opened the port: the identify gate locks again.</summary>
        public void Relock() { lock (gate) { Identified = false; } }

        public void Open()
        {
            lock (gate)
            {
                if (unplugged) throw new LinkException("device gone");
                open = true;
                Identified = false;          // the firmware re-locks on every port open
                OpenCount++;
            }
        }
        public void Close() { lock (gate) { open = false; } }
        public void Dispose() { Close(); }
        public void DiscardInput() { lock (gate) { rx.Clear(); } }

        public void Write(byte[] data)
        {
            lock (gate)
            {
                if (unplugged || !open) throw new LinkException("device gone");
                foreach (byte b in data)
                {
                    char c = (char)b;
                    if (c == ':') { cmd.Clear(); inCmd = true; }
                    else if (c == '#' && inCmd) { inCmd = false; Handle(cmd.ToString()); }
                    else if (inCmd) cmd.Append(c);
                }
            }
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (gate)
                {
                    if (unplugged || !open) throw new LinkException("device gone");
                    if (rx.Count > 0 && clock.ElapsedMilliseconds >= releaseAtMs)
                    {
                        int n = 0;
                        while (n < buffer.Length && rx.Count > 0) buffer[n++] = rx.Dequeue();
                        return n;
                    }
                }
                if (sw.ElapsedMilliseconds >= timeoutMs) return 0;
                System.Threading.Thread.Sleep(1);
            }
        }

        // ---------------------------------------------------------------- device logic

        private void Reply(string text)
        {
            if (Mode == Misbehavior.Silent) return;
            if (Mode == Misbehavior.Garbage) text = "\u0001\u0002ÿ\u0003";
            if (Mode == Misbehavior.AlwaysOk) text = "OK";
            if (Mode == Misbehavior.Flood) text = new string('x', 500);
            string wire = (Mode == Misbehavior.NoTerminator || Mode == Misbehavior.Flood) ? text : text + "#";
            releaseAtMs = clock.ElapsedMilliseconds + (Mode == Misbehavior.Slow ? ReplyDelayMs : 0);
            foreach (char ch in wire) rx.Enqueue((byte)ch);
        }

        private static int Norm(int cdeg) { return ((cdeg % 36000) + 36000) % 36000; }

        private void Settle()
        {
            if (state != 1 && !StuckMoving && clock.ElapsedMilliseconds >= moveEndMs)
            {
                if (state == 2 && focTarget >= 0) FocPos = focTarget;
                if (state == 3) RotCdeg = rotTargetSigned;
                state = 1; focTarget = -1; rotTarget = -1;
            }
        }

        private string Status()
        {
            Settle();
            int flags = Flags | (Warnings != 0 ? 8 : 0);
            return string.Format(CultureInfo.InvariantCulture, "S,{0},{1},{2},{3},{4},{5},{6:X}", state, FocPos, focTarget, Norm(RotCdeg), rotTarget, ErrorCode, flags);
        }

        private static bool Arg(string body, out int v)
        {
            return int.TryParse(body.Substring(2).Trim(' ', ','), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
        }

        private void StartMove(int st)
        {
            state = st;
            moveEndMs = clock.ElapsedMilliseconds + MoveMs;
        }

        private void Handle(string body)
        {
            Received.Add(body);
            Settle();
            if (body == "25") { Reply("OK"); return; }
            if (body == "32")
            {
                Identified = true;
                if (IsOpenWheel) { Reply("OPENWHEEL,G0B1,FW1.3.0,2," + Serial); return; }
                Reply(string.Format(CultureInfo.InvariantCulture, "ASTROUNIBOARD,{0},{1},{2},{3},{4:X}", Model, Firmware, ProtocolVersion, Serial, Caps));
                return;
            }
            if (!Identified) { Reply("ERR,16"); return; }
            string num = body.Length >= 2 ? body.Substring(0, 2) : body;
            int v;
            switch (num)
            {
                case "33": Reply(Status()); return;
                case "30": case "53": case "73":
                    HaltCount++;
                    if (!HaltIgnored) { state = 1; focTarget = -1; rotTarget = -1; }
                    Reply("OK"); return;
                case "34":
                    if (body == "34") Reply("E," + ErrorCode); else { ErrorCode = 0; Reply("OK"); }
                    return;
                case "40": Reply(string.Format(CultureInfo.InvariantCulture, "H,312,3297,-1,-1,0,{0:X},0,0", Warnings)); return;
                case "22": case "31":
                    Reply(RefuseResetWhileBusy && state != 1 ? "ERR,8" : "OK"); return;
                case "23": Reply("OK"); return;
                case "50": Reply(FocPos.ToString(CultureInfo.InvariantCulture)); return;
                case "54": Reply(FocMax.ToString(CultureInfo.InvariantCulture)); return;
                case "58": Reply(FocSpeed.ToString(CultureInfo.InvariantCulture)); return;
                case "60": Reply("2000"); return;
                case "62": Reply("0"); return;
                case "64": Reply("0"); return;
                case "66": Reply("111"); return;
                case "59":
                    if (!Arg(body, out v) || v < 1 || v > 20000) { Reply("ERR,7"); return; }
                    FocSpeed = v; Reply("OK"); return;
                case "51": case "52":
                    if (!Arg(body, out v)) { Reply("ERR,7"); return; }
                    if (state != 1) { Reply("ERR,8"); return; }
                    int t = num == "51" ? v : FocPos + v;
                    if (t < 0 || t > FocMax) { Reply("ERR,7"); return; }
                    if (t != FocPos) { focTarget = t; StartMove(2); }
                    if (!DropReplyToMove) Reply("OK");
                    return;
                case "56":
                    if (!Arg(body, out v) || v < 0 || v > FocMax) { Reply("ERR,7"); return; }
                    FocPos = v; Flags &= ~1; Reply("OK"); return;
                case "70": Reply(RotCdeg.ToString(CultureInfo.InvariantCulture)); return;
                case "74": Reply(WrapMin + "," + WrapMax); return;
                case "78": Reply("1200"); return;
                case "80": Reply("2400"); return;
                case "82": Reply("0"); return;
                case "84": Reply("88889"); return;
                case "86": Reply("0"); return;
                case "88": Reply("50"); return;
                case "71":
                    if (!Arg(body, out v) || v < 0 || v > 35999) { Reply("ERR,7"); return; }
                    if (state != 1) { Reply("ERR,8"); return; }
                    if (RefuseUnverifiedRotatorCode != 0 && (Flags & 2) != 0) { Reply("ERR," + RefuseUnverifiedRotatorCode); return; }
                    int best = int.MinValue;
                    foreach (int c in new[] { v - 36000, v, v + 36000 })
                        if (c >= WrapMin && c <= WrapMax && (best == int.MinValue || Math.Abs(c - RotCdeg) < Math.Abs(best - RotCdeg))) best = c;
                    if (best == int.MinValue) { Reply("ERR,7"); return; }
                    rotTargetSigned = best; rotTarget = v; StartMove(3);
                    if (!DropReplyToMove) Reply("OK");
                    return;
                case "90":
                    if (NoSensor) { Reply("ERR,14"); return; }
                    Reply("W,2,85,812,54"); return;
                case "94": Reply("I,-120,6230,12,-998,41"); return;
            }
            Reply("ERR,10");
        }
    }

    public sealed class FakeEnumerator : IUsbSerialEnumerator
    {
        public readonly List<UsbSerialDevice> Devices = new List<UsbSerialDevice>();
        public IList<UsbSerialDevice> Enumerate() { lock (Devices) return new List<UsbSerialDevice>(Devices); }
    }
}
