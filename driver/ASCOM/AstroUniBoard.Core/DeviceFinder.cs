// Stage 1 of detection: find COM ports whose USB descriptor (VID/PID) matches an AstroUniBoard.
// Stage 2 (the :32# identify handshake) is done by BoardController via DeviceFinder.Identify.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using Microsoft.Win32;

namespace AstroUniBoard.Core
{
    public sealed class UsbSerialDevice
    {
        public string PortName { get; set; }
        public int Vid { get; set; }
        public int Pid { get; set; }
        /// <summary>USB serial number string (stable across COM renumbering). May be a generated id if the device has none.</summary>
        public string UsbSerial { get; set; }
        public override string ToString()
        {
            return PortName + " (USB " + Vid.ToString("X4") + ":" + Pid.ToString("X4") + ", serial " + UsbSerial + ")";
        }
    }

    public interface IUsbSerialEnumerator
    {
        /// <summary>All USB serial devices currently present.</summary>
        IList<UsbSerialDevice> Enumerate();
    }

    /// <summary>
    /// Enumerates present USB COM ports from the Windows registry (HKLM\SYSTEM\CurrentControlSet\Enum\USB)
    /// intersected with the ports that currently exist, so entries of unplugged devices are ignored.
    /// </summary>
    public sealed class RegistryUsbEnumerator : IUsbSerialEnumerator
    {
        public IList<UsbSerialDevice> Enumerate()
        {
            var result = new List<UsbSerialDevice>();
            HashSet<string> present;
            try { present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase); }
            catch { return result; }

            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb == null) return result;
                    foreach (string hw in usb.GetSubKeyNames())
                    {
                        int vid, pid;
                        if (!TryParseVidPid(hw, out vid, out pid)) continue;
                        using (RegistryKey hwKey = usb.OpenSubKey(hw))
                        {
                            if (hwKey == null) continue;
                            foreach (string instance in hwKey.GetSubKeyNames())
                            {
                                using (RegistryKey dp = hwKey.OpenSubKey(instance + @"\Device Parameters"))
                                {
                                    string port = dp == null ? null : dp.GetValue("PortName") as string;
                                    if (string.IsNullOrEmpty(port) || !present.Contains(port)) continue;
                                    result.Add(new UsbSerialDevice { PortName = port, Vid = vid, Pid = pid, UsbSerial = instance });
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // registry unreadable: report what we have (nothing); callers fall back to manual selection
            }
            return result;
        }

        /// <summary>Parses "VID_1209&amp;PID_2180" (MI_xx composite suffixes are rejected: not a plain CDC device).</summary>
        public static bool TryParseVidPid(string key, out int vid, out int pid)
        {
            vid = pid = 0;
            if (key == null || !key.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) return false;
            string[] p = key.Split('&');
            if (p.Length != 2 || !p[1].StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(p[0].Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                   int.TryParse(p[1].Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }
    }

    /// <summary>Known USB IDs of AstroUniBoard devices (docs/USB_IDS.md).</summary>
    public static class BoardUsb
    {
        public const int Vid = 0x1209;          // pid.codes
        /// <summary>Assigned PID, 0 until pid.codes has merged the request (#36, #37).</summary>
        public const int PidAstroUniBoard = 0;
        public const int PidTest = 0x0001;      // shared pid.codes test PID (also used by OpenWheel): identify decides

        public static bool IsCandidate(UsbSerialDevice d)
        {
            return d.Vid == Vid && ((PidAstroUniBoard != 0 && d.Pid == PidAstroUniBoard) || d.Pid == PidTest);
        }
    }

    public static class DeviceFinder
    {
        /// <summary>Stage 1: candidate ports by USB descriptor, preferred serial first.</summary>
        public static List<UsbSerialDevice> FindCandidates(IUsbSerialEnumerator enumerator, string preferredUsbSerial)
        {
            var list = new List<UsbSerialDevice>();
            foreach (UsbSerialDevice d in enumerator.Enumerate())
                if (BoardUsb.IsCandidate(d)) list.Add(d);

            list.Sort((a, b) =>
            {
                bool pa = !string.IsNullOrEmpty(preferredUsbSerial) && string.Equals(a.UsbSerial, preferredUsbSerial, StringComparison.OrdinalIgnoreCase);
                bool pb = !string.IsNullOrEmpty(preferredUsbSerial) && string.Equals(b.UsbSerial, preferredUsbSerial, StringComparison.OrdinalIgnoreCase);
                if (pa != pb) return pa ? -1 : 1;
                // production PID before the shared test PID
                if (a.Pid != b.Pid) return a.Pid == BoardUsb.PidAstroUniBoard ? -1 : 1;
                return string.CompareOrdinal(a.PortName, b.PortName);
            });
            return list;
        }

        /// <summary>
        /// Stage 2: open the port and run :25# / :32#. Returns the identity or null when the port is
        /// not an AstroUniBoard (silent, wrong answer, an OpenWheel on the shared test PID, unsupported
        /// protocol). :32# also unlocks the firmware's identify gate. Never throws, never blocks longer
        /// than a few command timeouts.
        /// </summary>
        public static BoardIdentity Identify(ILink link, int commandTimeoutMs, out string failure)
        {
            failure = null;
            try
            {
                if (!link.IsOpen) link.Open();
                var t = new Transport(link);

                // The board may still be booting (USB enumerates before the firmware loop runs): retry the ping.
                bool alive = false;
                for (int i = 0; i < 3 && !alive; i++)
                {
                    Reply r = t.Execute(Commands.Ping, commandTimeoutMs);
                    alive = r.IsOk;
                }
                if (!alive) { failure = "no answer to ping"; return null; }

                Reply id = t.Execute(Commands.Identify, commandTimeoutMs);
                if (id.Kind != ReplyKind.Value) { failure = "bad identify reply: " + id; return null; }

                BoardIdentity identity;
                if (!BoardIdentity.TryParse(id.Value, out identity)) { failure = "not an AstroUniBoard: " + id.Value; return null; }
                if (!identity.IsSupported)
                {
                    failure = "protocol version " + identity.ProtocolVersion + " is not supported (need " +
                              BoardIdentity.MinProtocolVersion + ".." + BoardIdentity.MaxProtocolVersion + ")";
                    return null;
                }
                return identity;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return null;
            }
        }
    }
}
