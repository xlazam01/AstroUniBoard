// Byte link abstraction, serial implementation and the framing/timeout layer.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;

namespace AstroUniBoard.Core
{
    /// <summary>Thrown by a link when the device is gone (unplugged, port closed). Not a timeout.</summary>
    public sealed class LinkException : IOException
    {
        public LinkException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A byte pipe to the board. Implementations must never block longer than the given timeout.</summary>
    public interface ILink : IDisposable
    {
        string Name { get; }
        bool IsOpen { get; }
        void Open();
        void Close();
        void Write(byte[] data);
        /// <summary>Waits up to timeoutMs for data; returns the number of bytes copied (0 on timeout).
        /// Throws <see cref="LinkException"/> when the link is broken.</summary>
        int Read(byte[] buffer, int timeoutMs);
        void DiscardInput();
    }

    /// <summary>ILink over a Windows COM port (USB CDC), 115200 8N1, DTR/RTS not asserted.</summary>
    public sealed class SerialLink : ILink
    {
        private readonly string portName;
        private SerialPort port;

        public SerialLink(string portName) { this.portName = portName; }

        public string Name { get { return portName; } }
        public bool IsOpen { get { return port != null && port.IsOpen; } }

        public void Open()
        {
            Close();
            try
            {
                var p = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
                {
                    DtrEnable = false,   // asserting DTR can reset some USB CDC devices
                    RtsEnable = false,
                    ReadTimeout = 100,
                    WriteTimeout = 500,
                    ReadBufferSize = 4096
                };
                p.Open();
                // The SerialStream finalizer throws ObjectDisposedException (and kills the process) when
                // the port was closed on another thread or the USB device vanished; Close() disposes it.
                try { GC.SuppressFinalize(p.BaseStream); } catch { }
                port = p;
            }
            catch (Exception ex)
            {
                throw new LinkException("cannot open " + portName + ": " + ex.Message, ex);
            }
        }

        public void Close()
        {
            SerialPort p = port;
            port = null;
            if (p == null) return;
            try
            {
                // Closing a USB serial port that was just unplugged can block for seconds on
                // some drivers: do it on a throw-away thread so callers never wait for it.
                var t = new System.Threading.Thread(() => { try { p.Close(); p.Dispose(); } catch { } })
                { IsBackground = true, Name = "AstroUniBoard serial close" };
                t.Start();
                t.Join(300);
            }
            catch { }
        }

        public void Write(byte[] data)
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) throw new LinkException(portName + " is not open");
            try { p.Write(data, 0, data.Length); }
            catch (TimeoutException ex) { throw new LinkException("write timeout on " + portName, ex); }
            catch (Exception ex) { throw new LinkException("write failed on " + portName + ": " + ex.Message, ex); }
        }

        public int Read(byte[] buffer, int timeoutMs)
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) throw new LinkException(portName + " is not open");
            var sw = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (p.BytesToRead > 0)
                        return p.Read(buffer, 0, Math.Min(buffer.Length, p.BytesToRead));
                    if (sw.ElapsedMilliseconds >= timeoutMs) return 0;
                    System.Threading.Thread.Sleep(2);
                    if (!p.IsOpen) throw new LinkException(portName + " closed");
                }
            }
            catch (LinkException) { throw; }
            catch (TimeoutException) { return 0; }
            catch (Exception ex) { throw new LinkException("read failed on " + portName + ": " + ex.Message, ex); }
        }

        public void DiscardInput()
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) return;
            try { p.DiscardInBuffer(); } catch { }
        }

        public void Dispose() { Close(); }
    }

    /// <summary>
    /// Sends one command and returns one parsed reply, always within the given time.
    /// Not thread-safe: owned by a single worker.
    /// </summary>
    public sealed class Transport
    {
        /// <summary>Longest plausible reply line. Longer input without '#' is treated as garbage.</summary>
        public const int MaxLineLength = 64;

        private readonly ILink link;
        private readonly byte[] rx = new byte[256];

        public Transport(ILink link) { this.link = link; }

        public ILink Link { get { return link; } }

        /// <summary>
        /// Sends <paramref name="command"/> (":NN...#") and waits up to <paramref name="timeoutMs"/>
        /// for one reply. Stale input is discarded first. Returns Timeout/Garbage instead of throwing;
        /// only a broken link throws <see cref="LinkException"/>.
        /// </summary>
        public Reply Execute(string command, int timeoutMs)
        {
            if (command == null || command.Length < 4 || command[0] != ':' || command[command.Length - 1] != '#' ||
                command.Length - 2 > Commands.MaxCommandBody)
                throw new ArgumentException("not a valid command: " + command, "command");

            link.DiscardInput();
            link.Write(Encoding.ASCII.GetBytes(command));

            var line = new StringBuilder(MaxLineLength);
            bool overflowed = false;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                long left = timeoutMs - sw.ElapsedMilliseconds;
                if (left <= 0) return overflowed ? Reply.GarbageReply : Reply.TimeoutReply;

                int n = link.Read(rx, (int)Math.Min(left, 50));
                for (int i = 0; i < n; i++)
                {
                    char c = (char)rx[i];
                    if (c == '#')
                    {
                        if (overflowed) return Reply.GarbageReply;
                        return Reply.Parse(line.ToString());
                    }
                    if (line.Length < MaxLineLength) line.Append(c);
                    else overflowed = true;
                }
            }
        }
    }
}
