// COM LocalServer for the AstroUniBoard ASCOM drivers (copied and adapted from OpenWheel.Ascom).
//
// One process serves every client (NINA, the Secondary Focuser plugin, the debug app, ...) and all
// three device types, so they share one BoardController and one serial connection. The mechanics
// (class factories, registration, shut-down when the last client has gone) follow the ASCOM
// LocalServer template, written compactly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using AstroUniBoard.Core;

namespace AstroUniBoard.Ascom
{
    /// <summary>One COM class served by this exe.</summary>
    internal sealed class ServedClass
    {
        public string ProgId;
        public string Description;
        public string DeviceType;
        public Guid Clsid;
        public Func<object> Create;
    }

    internal static class Server
    {
        public const string FocuserProgId = "ASCOM.AstroUniBoard.Focuser";
        public const string RotatorProgId = "ASCOM.AstroUniBoard.Rotator";
        public const string ObservingConditionsProgId = "ASCOM.AstroUniBoard.ObservingConditions";
        public const string FocuserClsid = "f6e22b09-e12f-46fb-ac89-26b2c8a63438";
        public const string RotatorClsid = "90a0b732-d5d9-434d-856b-3f1656b1ad5d";
        public const string ObservingConditionsClsid = "c2ef9ee5-4fdb-42cd-883d-67e80ddfb367";
        public static readonly Guid AppId = new Guid("7dd32772-ad1e-4128-ae85-b7fa07968f2d");
        public const string AppName = "AstroUniBoard";

        public static readonly ServedClass[] Classes =
        {
            new ServedClass { ProgId = FocuserProgId, Description = "AstroUniBoard Focuser", DeviceType = "Focuser", Clsid = new Guid(FocuserClsid), Create = () => new Focuser() },
            new ServedClass { ProgId = RotatorProgId, Description = "AstroUniBoard Rotator", DeviceType = "Rotator", Clsid = new Guid(RotatorClsid), Create = () => new Rotator() },
            new ServedClass { ProgId = ObservingConditionsProgId, Description = "AstroUniBoard ObservingConditions", DeviceType = "ObservingConditions", Clsid = new Guid(ObservingConditionsClsid), Create = () => new ObservingConditions() },
        };

        // ------------------------------------------------------------------ shared state

        private static readonly object gate = new object();
        private static int objectCount;
        private static int lockCount;
        private static int connectionClients;
        private static BoardController controller;
        private static readonly ManualResetEvent exitEvent = new ManualResetEvent(false);
        public static bool StartedByCom { get; private set; }

        /// <summary>Ring buffer of the controller's log (and the driver's own messages) for the debug app.</summary>
        public static readonly LogBuffer LogRing = new LogBuffer(1000);

        /// <summary>Host-side temperature compensation (one for all focuser clients).</summary>
        public static readonly TempCompensator TempComp = new TempCompensator();

        /// <summary>The single controller shared by all driver instances in this process.</summary>
        public static BoardController Controller
        {
            get
            {
                lock (gate)
                {
                    if (controller == null)
                    {
                        controller = new BoardController(Settings.LoadOptions());
                        controller.Log += Log;
                        controller.Start();
                    }
                    return controller;
                }
            }
        }

        /// <summary>Number of connected clients (all device types, setup dialog included).</summary>
        public static int ConnectionClients { get { lock (gate) return connectionClients; } }

        /// <summary>A client wants the board connected (reference counted over all device types).</summary>
        public static void AcquireConnection()
        {
            BoardController c;
            lock (gate)
            {
                connectionClients++;
                if (connectionClients == 1)
                {
                    // Settings may have changed in the setup dialog since the last connection: the
                    // controller reads its options at construction, so rebuild it while nobody uses it.
                    BoardController old = controller;
                    controller = null;
                    if (old != null) old.Dispose();
                    TempComp.Reset();
                }
                c = Controller;
            }
            c.Connect();
        }

        public static void ReleaseConnection()
        {
            lock (gate)
            {
                if (connectionClients > 0) connectionClients--;
                if (connectionClients == 0 && controller != null)
                {
                    TempComp.Reset();
                    controller.Disconnect();
                }
            }
        }

        public static void Log(string message)
        {
            LogRing.Add(message);
            DriverLog.Write(message);
        }

        public static void CountObject() { Interlocked.Increment(ref objectCount); }
        public static void UncountObject() { Interlocked.Decrement(ref objectCount); }
        public static void Lock() { Interlocked.Increment(ref lockCount); }
        public static void Unlock() { Interlocked.Decrement(ref lockCount); }

        // ------------------------------------------------------------------ entry point

        [MTAThread]
        private static int Main(string[] args)
        {
            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            bool silent = args.Length > 1 && args[1].ToLowerInvariant() == "/silent";   // used by the installer: no message boxes
            try
            {
                switch (arg)
                {
                    case "-embedding":
                    case "/embedding":
                        return RunComServer();

                    case "/regserver":
                    case "-regserver":
                    case "/register":
                    case "-register":
                        return Elevated(arg, silent, () => { Register(); return 0; }, "registered");

                    case "/unregserver":
                    case "-unregserver":
                    case "/unregister":
                    case "-unregister":
                        return Elevated(arg, silent, () => { Unregister(); return 0; }, "unregistered");

                    case "":
                    case "/setup":
                        ShowSetupOnStaThread();
                        BoardController standalone;
                        lock (gate) { standalone = controller; controller = null; }
                        if (standalone != null) standalone.Dispose();
                        return 0;

                    default:
                        Message("Unknown argument: " + arg + "\r\n\r\nUse /regserver or /unregserver (administrator rights are requested automatically), or no argument to open the setup dialog.");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                DriverLog.Write("fatal: " + ex);
                Message("AstroUniBoard driver error: " + ex.Message);
                return 1;
            }
        }

        private static int RunComServer()
        {
            StartedByCom = true;
            Log("COM server starting (driver " + Identity.DriverVersion + ")");
            var cookies = new List<uint>();
            foreach (ServedClass sc in Classes)
            {
                uint cookie;
                Guid clsid = sc.Clsid;
                int hr = CoRegisterClassObject(ref clsid, new ClassFactory(sc), 4 /*LOCAL_SERVER*/, 1 | 4 /*MULTIPLEUSE|SUSPENDED*/, out cookie);
                if (hr != 0) { Log("CoRegisterClassObject " + sc.ProgId + " failed: 0x" + hr.ToString("X8")); continue; }
                cookies.Add(cookie);
            }
            if (cookies.Count == 0) return 3;
            CoResumeClassObjects();

            // Periodically collect so that released COM objects get finalized and counted down,
            // then leave when no client uses the server any more.
            var idleSince = Stopwatch.StartNew();
            while (!exitEvent.WaitOne(2000))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Volatile.Read(ref objectCount) > 0 || Volatile.Read(ref lockCount) > 0) idleSince.Restart();
                else if (idleSince.ElapsedMilliseconds > 4000) break;   // idle for 4 s: leave
            }

            foreach (uint c in cookies) CoRevokeClassObject(c);
            TempComp.Dispose();
            BoardController bc;
            lock (gate) { bc = controller; controller = null; }
            if (bc != null) bc.Dispose();
            Log("COM server stopped");
            return 0;
        }

        private static void ShowSetupOnStaThread()
        {
            var t = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var f = new SetupForm()) f.ShowDialog();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }

        /// <summary>Shows the setup dialog from a COM call (runs on its own STA thread, blocks until closed).</summary>
        public static void ShowSetupDialog()
        {
            ShowSetupOnStaThread();
        }

        private static void Message(string text)
        {
            MessageBox.Show(text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------------ registration

        private static bool IsAdministrator()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Runs the action as administrator, asking for elevation (UAC) if necessary.</summary>
        private static int Elevated(string arg, bool silent, Func<int> action, string doneWord)
        {
            if (IsAdministrator())
            {
                int rc = action();
                if (rc == 0 && !silent) Message("AstroUniBoard drivers (Focuser, Rotator, ObservingConditions) " + doneWord + ".");
                return rc;
            }
            var psi = new ProcessStartInfo(Application.ExecutablePath, silent ? arg + " /silent" : arg) { UseShellExecute = true, Verb = "runas" };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                if (!silent) Message("Administrator rights are required and were not granted.");
                return 5;
            }
        }

        private static string Braced(Guid g) { return "{" + g.ToString().ToUpperInvariant() + "}"; }

        private static void Register()
        {
            string exe = Application.ExecutablePath;
            string appid = Braced(AppId);

            // Write both registry views: 32-bit and 64-bit clients each look in their own.
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey classes = hklm.CreateSubKey(@"SOFTWARE\Classes"))
                {
                    using (RegistryKey k = classes.CreateSubKey("AppID\\" + appid)) k.SetValue(null, "AstroUniBoard ASCOM LocalServer");
                    using (RegistryKey k = classes.CreateSubKey("AppID\\" + Path.GetFileName(exe))) k.SetValue("AppID", appid);

                    foreach (ServedClass sc in Classes)
                    {
                        string clsid = Braced(sc.Clsid);
                        using (RegistryKey k = classes.CreateSubKey("CLSID\\" + clsid))
                        {
                            k.SetValue(null, sc.Description);
                            k.SetValue("AppID", appid);
                            using (RegistryKey s = k.CreateSubKey("LocalServer32")) s.SetValue(null, exe);
                            using (RegistryKey s = k.CreateSubKey("ProgId")) s.SetValue(null, sc.ProgId);
                            using (RegistryKey s = k.CreateSubKey("Programmable")) { }
                        }
                        using (RegistryKey k = classes.CreateSubKey(sc.ProgId))
                        {
                            k.SetValue(null, sc.Description);
                            using (RegistryKey s = k.CreateSubKey("CLSID")) s.SetValue(null, clsid);
                        }
                    }
                }
            }

            foreach (ServedClass sc in Classes)
            {
                using (var profile = new ASCOM.Utilities.Profile())
                {
                    profile.DeviceType = sc.DeviceType;
                    profile.Register(sc.ProgId, sc.Description);
                }
            }
        }

        private static void Unregister()
        {
            string appid = Braced(AppId);
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey classes = hklm.OpenSubKey(@"SOFTWARE\Classes", true))
                {
                    if (classes == null) continue;
                    foreach (ServedClass sc in Classes)
                    {
                        classes.DeleteSubKeyTree("CLSID\\" + Braced(sc.Clsid), false);
                        classes.DeleteSubKeyTree(sc.ProgId, false);
                    }
                    classes.DeleteSubKeyTree("AppID\\" + appid, false);
                    classes.DeleteSubKeyTree("AppID\\" + Path.GetFileName(Application.ExecutablePath), false);
                }
            }
            foreach (ServedClass sc in Classes)
            {
                using (var profile = new ASCOM.Utilities.Profile())
                {
                    profile.DeviceType = sc.DeviceType;
                    profile.Unregister(sc.ProgId);
                }
            }
        }

        // ------------------------------------------------------------------ COM imports

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.Interface)] IClassFactory pUnk,
                                                        uint dwClsContext, uint flags, out uint lpdwRegister);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint dwRegister);

        [DllImport("ole32.dll")]
        private static extern int CoResumeClassObjects();
    }

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ClassFactory : IClassFactory
    {
        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        private readonly ServedClass served;

        public ClassFactory(ServedClass served) { this.served = served; }

        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
            object obj = served.Create();
            IntPtr unk = Marshal.GetIUnknownForObject(obj);
            try { return Marshal.QueryInterface(unk, ref riid, out ppvObject); }
            finally { Marshal.Release(unk); }
        }

        public int LockServer(bool fLock)
        {
            if (fLock) Server.Lock(); else Server.Unlock();
            return 0;
        }
    }

    /// <summary>Thread-safe ring buffer of log lines with increasing sequence numbers (Action AstroUniBoard:Log).</summary>
    internal sealed class LogBuffer
    {
        public struct Entry
        {
            public long Seq;
            public DateTime Time;
            public string Text;
        }

        private readonly object gate = new object();
        private readonly Entry[] ring;
        private long next = 1;   // sequence number of the next entry; the first is 1

        public LogBuffer(int capacity) { ring = new Entry[capacity]; }

        public void Add(string text)
        {
            lock (gate)
            {
                ring[(int)(next % ring.Length)] = new Entry { Seq = next, Time = DateTime.Now, Text = text ?? string.Empty };
                next++;
            }
        }

        /// <summary>Entries with a sequence number above <paramref name="afterSeq"/>, oldest first.</summary>
        public List<Entry> After(long afterSeq)
        {
            var list = new List<Entry>();
            lock (gate)
            {
                long first = Math.Max(afterSeq + 1, Math.Max(1, next - ring.Length));
                for (long s = first; s < next; s++) list.Add(ring[(int)(s % ring.Length)]);
            }
            return list;
        }
    }

    /// <summary>Small rolling log in %LOCALAPPDATA%\AstroUniBoard\driver.log (helps when a client misbehaves).</summary>
    internal static class DriverLog
    {
        private static readonly object gate = new object();
        private static string path;

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    if (path == null)
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstroUniBoard");
                        Directory.CreateDirectory(dir);
                        path = Path.Combine(dir, "driver.log");
                    }
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
