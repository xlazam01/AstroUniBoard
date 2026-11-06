// ASCOM ObservingConditions (IObservingConditionsV2): temperature, humidity and dew point from the
// SHT31 the board selected (:90#, external J9 sensor preferred). Everything else is not implemented.
using System;
using System.Runtime.InteropServices;
using ASCOM;
using ASCOM.DeviceInterface;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    [ComVisible(true)]
    [Guid(Server.ObservingConditionsClsid)]
    [ProgId(Server.ObservingConditionsProgId)]
    [ServedClassName("AstroUniBoard ObservingConditions")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ObservingConditions : DeviceBase, IObservingConditionsV2
    {
        protected override string DeviceKind { get { return "ObservingConditions"; } }
        protected override BoardCaps RequiredCaps { get { return BoardCaps.Environment; } }

        public string Description { get { return "AstroUniBoard environment sensor (SHT31: temperature, humidity, dew point)"; } }
        public short InterfaceVersion { get { return 2; } }

        private EnvironmentReading Reading()
        {
            BoardSnapshot s = RequireConnected();
            if (s.Environment == null) throw new DriverException("The environment sensor (SHT31) has no valid reading");
            return s.Environment;
        }

        private static bool Implemented(string name, out string canonical)
        {
            canonical = null;
            foreach (string n in new[] { "Temperature", "Humidity", "DewPoint" })
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { canonical = n; return true; }
            return false;
        }

        private static readonly string[] AllProperties =
        {
            "CloudCover", "DewPoint", "Humidity", "Pressure", "RainRate", "SkyBrightness", "SkyQuality",
            "SkyTemperature", "StarFWHM", "Temperature", "WindDirection", "WindGust", "WindSpeed"
        };

        private static bool Known(string name)
        {
            foreach (string n in AllProperties) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- implemented

        public double Temperature { get { return Reading().Temperature; } }
        public double Humidity { get { return Reading().Humidity; } }
        public double DewPoint { get { return Reading().DewPoint; } }

        public double AveragePeriod
        {
            get { RequireConnected(); return 0; }
            set
            {
                RequireConnected();
                if (value != 0) throw new InvalidValueException("AveragePeriod", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "0 (only instantaneous readings)");
            }
        }

        public void Refresh() { RequireConnected(); }   // the board refreshes every 2 s, the driver reads every 5 s

        public string SensorDescription(string propertyName)
        {
            RequireConnected();
            string n;
            if (Implemented(propertyName, out n))
            {
                BoardSnapshot s = Server.Controller.Snapshot;
                string src = s.Environment == null ? "selected" : s.Environment.Source == 2 ? "external (J9)" : "on-board";
                return "SHT31 " + src + " sensor: " + n;
            }
            if (Known(propertyName)) throw new MethodNotImplementedException("SensorDescription(" + propertyName + ")");
            throw new InvalidValueException("SensorDescription", propertyName ?? "", "a valid ObservingConditions property name");
        }

        public double TimeSinceLastUpdate(string propertyName)
        {
            RequireConnected();
            string n;
            if (propertyName == null) propertyName = string.Empty;
            if (propertyName.Length == 0 || Implemented(propertyName, out n))
            {
                EnvironmentReading e = Reading();
                return Math.Max(0, (DateTime.UtcNow - e.TimeUtc).TotalSeconds);
            }
            if (Known(propertyName)) throw new MethodNotImplementedException("TimeSinceLastUpdate(" + propertyName + ")");
            throw new InvalidValueException("TimeSinceLastUpdate", propertyName, "a valid ObservingConditions property name");
        }

        public IStateValueCollection DeviceState
        {
            get
            {
                BoardSnapshot s = RequireConnected();
                var c = new StateValueCollection();
                if (s.Environment != null)
                {
                    c.Add("DewPoint", s.Environment.DewPoint);
                    c.Add("Humidity", s.Environment.Humidity);
                    c.Add("Temperature", s.Environment.Temperature);
                }
                c.AddUtcDateTime();
                return c;
            }
        }

        // ---------------------------------------------------------------- not implemented

        public double CloudCover { get { throw new PropertyNotImplementedException("CloudCover", false); } }
        public double Pressure { get { throw new PropertyNotImplementedException("Pressure", false); } }
        public double RainRate { get { throw new PropertyNotImplementedException("RainRate", false); } }
        public double SkyBrightness { get { throw new PropertyNotImplementedException("SkyBrightness", false); } }
        public double SkyQuality { get { throw new PropertyNotImplementedException("SkyQuality", false); } }
        public double SkyTemperature { get { throw new PropertyNotImplementedException("SkyTemperature", false); } }
        public double StarFWHM { get { throw new PropertyNotImplementedException("StarFWHM", false); } }
        public double WindDirection { get { throw new PropertyNotImplementedException("WindDirection", false); } }
        public double WindGust { get { throw new PropertyNotImplementedException("WindGust", false); } }
        public double WindSpeed { get { throw new PropertyNotImplementedException("WindSpeed", false); } }
    }
}
