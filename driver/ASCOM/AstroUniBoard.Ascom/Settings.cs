// Driver settings, stored in the ASCOM Profile store (readable by every client bitness).
// The board choice and the temperature compensation live under the Focuser ProgID, the rotator's
// sky offset (Q18: host only) under the Rotator ProgID. Board settings (speeds, limits, reverse, ...)
// are not stored here: they live in the board's EEPROM (:23#).
using System;
using System.Globalization;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    internal static class Settings
    {
        private const string UsbSerialKey = "PreferredUsbSerial";
        private const string ManualPortKey = "ManualPort";
        private const string SkyOffsetKey = "SkyOffsetDegrees";
        private const string TempCoefficientKey = "TempCompStepsPerDegree";
        private const string TempThresholdKey = "TempCompThresholdDegrees";

        private static string Get(string deviceType, string progId, string key, string def)
        {
            try
            {
                using (var p = new ASCOM.Utilities.Profile { DeviceType = deviceType })
                {
                    string v = p.GetValue(progId, key, string.Empty, def);
                    return v ?? def;
                }
            }
            catch { return def; }
        }

        private static void Set(string deviceType, string progId, string key, string value)
        {
            try
            {
                using (var p = new ASCOM.Utilities.Profile { DeviceType = deviceType })
                {
                    p.WriteValue(progId, key, value ?? string.Empty);
                }
            }
            catch (ASCOM.Utilities.Exceptions.DriverNotRegisteredException)
            {
                // only when the classes run without registration (AstroUniBoard.Ascom.Check before /regserver)
                Server.Log("setting " + key + " not saved: " + progId + " is not registered (run the server with /regserver)");
            }
        }

        private static double GetDouble(string deviceType, string progId, string key, double def)
        {
            double v;
            return double.TryParse(Get(deviceType, progId, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : def;
        }

        private static string D(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public static string PreferredUsbSerial
        {
            get { return Get("Focuser", Server.FocuserProgId, UsbSerialKey, string.Empty); }
            set { Set("Focuser", Server.FocuserProgId, UsbSerialKey, value); }
        }

        public static string ManualPort
        {
            get { return Get("Focuser", Server.FocuserProgId, ManualPortKey, string.Empty); }
            set { Set("Focuser", Server.FocuserProgId, ManualPortKey, value); }
        }

        /// <summary>Rotator sky angle = mechanical angle + this, degrees (0..360).</summary>
        public static double SkyOffsetDegrees
        {
            get { return GetDouble("Rotator", Server.RotatorProgId, SkyOffsetKey, 0); }
            set { Set("Rotator", Server.RotatorProgId, SkyOffsetKey, D(value)); }
        }

        /// <summary>Temperature compensation: focuser steps per °C of temperature rise (signed).</summary>
        public static double TempCompStepsPerDegree
        {
            get { return GetDouble("Focuser", Server.FocuserProgId, TempCoefficientKey, 0); }
            set { Set("Focuser", Server.FocuserProgId, TempCoefficientKey, D(value)); }
        }

        /// <summary>Temperature compensation moves only after the temperature changed by at least this, °C.</summary>
        public static double TempCompThresholdDegrees
        {
            get { return Math.Max(0.1, GetDouble("Focuser", Server.FocuserProgId, TempThresholdKey, 0.5)); }
            set { Set("Focuser", Server.FocuserProgId, TempThresholdKey, D(value)); }
        }

        public static BoardOptions LoadOptions()
        {
            var o = new BoardOptions();
            string serial = PreferredUsbSerial;
            string port = ManualPort;
            if (!string.IsNullOrWhiteSpace(serial)) o.PreferredUsbSerial = serial.Trim();
            if (!string.IsNullOrWhiteSpace(port)) o.ManualPort = port.Trim();
            return o;
        }
    }
}
