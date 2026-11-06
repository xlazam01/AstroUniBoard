// Reference implementation of the driver's AstroUniBoard:* Action contract over a BoardController,
// in process. Used by the tests and by "--loopback" to exercise the whole ASCOM code path of the
// app before the LocalServer is installed. It mirrors what the LocalServer is expected to do:
//   AstroUniBoard:Raw       ":NN...#" → reply without '#' or "ERROR: message"
//   AstroUniBoard:Snapshot  → JSON (SnapshotJson.Build)
//   AstroUniBoard:Log       last seen seq or "" → JSON [{seq,time,text}]
//   AstroUniBoard:HaltAll | ClearError | SaveSettings | ImuHome | ImuCalibrate | Reboot | EnterDfu → "OK" / "ERROR: message"
using System;
using System.Collections.Generic;
using System.Globalization;
using AstroUniBoard.Core;

namespace AstroUniBoard.DebugApp
{
    internal sealed class LoopbackDriver : IDriverChannel
    {
        private readonly DirectSession board;
        private bool connected;

        public LoopbackDriver(DirectSession board) { this.board = board; }

        public string Name { get { return "loopback (" + board.Description + ")"; } }
        public int ActionCount { get; private set; }

        public void Connect()
        {
            if (connected) { board.Reconnect(); return; }
            board.Open();
            connected = true;
        }

        public void Disconnect() { }

        public string Action(string name, string parameter)
        {
            ActionCount++;
            if (!connected) return Replies.ErrorPrefix + "not connected";
            if (string.Equals(name, AscomSession.ActionRaw, StringComparison.OrdinalIgnoreCase))
                return board.Raw(parameter);
            if (string.Equals(name, AscomSession.ActionSnapshot, StringComparison.OrdinalIgnoreCase))
                return SnapshotJson.ToJson(SnapshotJson.Build(board.Controller.Snapshot));
            if (string.Equals(name, AscomSession.ActionLog, StringComparison.OrdinalIgnoreCase))
            {
                long after = 0;
                if (!string.IsNullOrEmpty(parameter) && !long.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out after))
                    return Replies.ErrorPrefix + "log parameter must be the last seen sequence number";
                var list = new List<object>();
                foreach (LogEntry e in board.ReadLog(after))
                    list.Add(new Dictionary<string, object> { ["seq"] = e.Seq, ["time"] = e.Time.ToString("o", CultureInfo.InvariantCulture), ["text"] = e.Text });
                return SnapshotJson.ToJson(list);
            }
            const string prefix = "AstroUniBoard:";
            BoardAction a;
            if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse(name.Substring(prefix.Length), true, out a))
                return board.Do(a);
            // ASCOM drivers throw ActionNotImplementedException here
            throw new NotSupportedException("action " + name + " is not implemented");
        }

        public void Dispose() { board.Dispose(); }
    }
}
