"""Serves a simulated AstroUniBoard on a byte link: framing as fw/src/protocol.cpp, link faults, reboot /
DFU / unplug, and three transports:

  PtyTransport     Linux/macOS pseudo terminal: hosts open /dev/pts/N (or a --link symlink) like /dev/ttyACM0
  TcpTransport     raw TCP, one client at a time: a connection is a port open (any OS, CI, Windows tests)
  SerialTransport  a real COM port via pyserial, e.g. one end of a com0com pair on Windows

A host "opening the port" re-locks the identify gate (#58): a new pty open or termios change, a new TCP
connection, or a rising DSR on a serial port (com0com: map DSR to the peer's open state).
"""
from __future__ import annotations

import json
import os
import select
import socket
import sys
import threading
import time
from typing import Optional

from .device import (E_BAD_FORMAT, PROTO_MAX_LINE, RESET_POWER_ON, RESET_SOFTWARE, RESET_WATCHDOG, Config, Device,
                     Nv)

JUNK = b"\x01\xfe\x7f junk \x02#"


class Framer:
    """`:NN...#` framing of fw/src/protocol.cpp Protocol::process(). Yields bodies; None = overflow."""

    def __init__(self):
        self.buf = bytearray()
        self.in_cmd = False
        self.overflow = False

    def feed(self, data: bytes):
        out = []
        for c in data:
            if c == 0x3A:  # ':'
                self.in_cmd, self.overflow = True, False
                self.buf.clear()
            elif c == 0x23:  # '#'
                if not self.in_cmd:
                    continue
                out.append(None if self.overflow else self.buf.decode("latin-1"))
                self.in_cmd = False
            elif self.in_cmd:
                if len(self.buf) < PROTO_MAX_LINE:
                    self.buf.append(c)
                else:
                    self.overflow = True
        return out


class Faults:
    """Link misbehaviour, set with control commands (see README)."""

    def __init__(self):
        self.silent = False      # read commands, never answer
        self.garbage = False     # answer binary junk ending in '#'
        self.nohash = False      # answer without the closing '#'
        self.partial = 0         # answer only the first N bytes (0 = off)
        self.delay_ms = 0        # answer late
        self.drop = 0            # leave the next N commands unanswered


# ------------------------------------------------------------------------------------- transports
class Disconnected(Exception):
    pass


class TcpTransport:
    def __init__(self, host: str = "127.0.0.1", port: int = 0):
        self.host, self.port = host, port
        self.listener: Optional[socket.socket] = None
        self.client: Optional[socket.socket] = None
        self.events = 0

    @property
    def name(self) -> str:
        return f"tcp://{self.host}:{self.port}"

    def open(self):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if os.name != "nt":
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind((self.host, self.port))
        s.listen(2)
        s.setblocking(False)
        self.port = s.getsockname()[1]
        self.listener = s

    def read(self, timeout: float) -> bytes:
        socks = [x for x in (self.listener, self.client) if x is not None]
        if not socks:
            time.sleep(timeout)
            return b""
        r, _, _ = select.select(socks, [], [], timeout)
        if self.listener is not None and self.listener in r:
            try:
                c, _ = self.listener.accept()
            except OSError:
                c = None
            if c is not None:
                if self.client is not None:  # the port is open already (COM ports are exclusive)
                    c.close()
                else:
                    c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                    c.setblocking(False)
                    self.client = c
                    self.events += 1
        if self.client is not None and self.client in r:
            try:
                d = self.client.recv(4096)
            except (BlockingIOError, InterruptedError):
                return b""
            except OSError:
                d = b""
            if not d:
                self._drop_client()
            return d
        return b""

    def host_opened(self) -> bool:
        e, self.events = self.events, 0
        return e > 0

    def write(self, data: bytes):
        if self.client is None:
            return
        try:
            self.client.setblocking(True)
            self.client.sendall(data)
            self.client.setblocking(False)
        except OSError:
            self._drop_client()

    def _drop_client(self):
        if self.client is not None:
            try:
                self.client.close()
            except OSError:
                pass
            self.client = None

    def unplug(self):
        self._drop_client()
        if self.listener is not None:
            if os.name != "nt":
                # close() alone leaves the listener accepting while the serving thread is still in select()
                # on it (Linux): a host that reconnects at once is accepted and then reset
                try:
                    self.listener.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
            self.listener.close()
            self.listener = None

    def replug(self):
        if self.listener is None:
            self.open()

    def close(self):
        self.unplug()


class PtyTransport:
    """Pseudo terminal (POSIX). The sim keeps only the master side open, so it sees when a host opens
    the slave (POLLHUP clears) and when the host changes the termios (tcgetattr on the master returns
    the slave's settings on Linux)."""

    def __init__(self, link: Optional[str] = None):
        self.link = link
        self.master: Optional[int] = None
        self.path = ""
        self.hup = True
        self.attrs = None
        self.events = 0

    @property
    def name(self) -> str:
        return self.link or self.path

    def open(self):
        import termios  # noqa: F401  (POSIX only)
        import tty
        m, s = os.openpty()
        tty.setraw(s)
        self.path = os.ttyname(s)
        os.close(s)
        os.set_blocking(m, False)
        self.master = m
        self.hup = True
        self.attrs = self._attrs()
        if self.link:
            try:
                os.unlink(self.link)
            except FileNotFoundError:
                pass
            os.symlink(self.path, self.link)

    def _attrs(self):
        import termios
        try:
            return termios.tcgetattr(self.master)
        except Exception:
            return None

    def read(self, timeout: float) -> bytes:
        if self.master is None:
            time.sleep(timeout)
            return b""
        p = select.poll()
        p.register(self.master, select.POLLIN)
        ev = p.poll(timeout * 1000)
        rev = ev[0][1] if ev else 0
        if rev & select.POLLHUP and not rev & select.POLLIN:
            self.hup = True
            time.sleep(timeout)
            return b""
        if self.hup and not rev & select.POLLHUP:
            self.hup = False
            self.events += 1
        a = self._attrs()
        if a != self.attrs:
            self.attrs = a
            self.events += 1
        if not rev & select.POLLIN:
            return b""
        try:
            return os.read(self.master, 4096)
        except (BlockingIOError, InterruptedError):
            return b""
        except OSError:  # EIO: the host closed the slave
            self.hup = True
            return b""

    def host_opened(self) -> bool:
        e, self.events = self.events, 0
        return e > 0

    def write(self, data: bytes):
        if self.master is None or self.hup:
            return
        try:
            os.write(self.master, data)
        except OSError:
            pass

    def unplug(self):
        if self.master is not None:
            os.close(self.master)
            self.master = None
        if self.link:
            try:
                os.unlink(self.link)
            except FileNotFoundError:
                pass

    def replug(self):
        if self.master is None:
            self.open()

    def close(self):
        self.unplug()


class SerialTransport:
    """A COM port through pyserial (Windows: one end of a com0com pair, the host opens the other)."""

    def __init__(self, port: str, open_signal: str = "dsr"):
        self.port, self.open_signal = port, open_signal
        self.ser = None
        self.line_prev = False
        self.events = 0

    @property
    def name(self) -> str:
        return self.port

    def open(self):
        import serial  # pyserial
        self.ser = serial.Serial(self.port, 115200, timeout=0)
        self.line_prev = self._line()

    def _line(self) -> bool:
        if self.ser is None or self.open_signal == "none":
            return False
        return bool(self.ser.dsr if self.open_signal == "dsr" else self.ser.cd)

    def read(self, timeout: float) -> bytes:
        if self.ser is None:
            time.sleep(timeout)
            return b""
        cur = self._line()
        if cur and not self.line_prev:
            self.events += 1
        self.line_prev = cur
        self.ser.timeout = timeout
        return self.ser.read(max(1, self.ser.in_waiting))

    def host_opened(self) -> bool:
        e, self.events = self.events, 0
        return e > 0

    def write(self, data: bytes):
        if self.ser is not None:
            self.ser.write(data)

    def unplug(self):
        if self.ser is not None:
            self.ser.close()
            self.ser = None

    def replug(self):
        if self.ser is None:
            self.open()

    def close(self):
        self.unplug()


# ------------------------------------------------------------------------------------- simulator
class Simulator:
    """Runs a Device on a transport in a background thread. `control(line)` takes the control
    commands of the README (also used by the CLI on stdin)."""

    def __init__(self, transport, config: Optional[Config] = None, nv: Optional[Nv] = None, speed: float = 1.0,
                 reboot_s: float = 1.0, dfu_return_s: Optional[float] = None, nv_file: Optional[str] = None):
        self.transport = transport
        self.speed = speed
        self.reboot_s = reboot_s
        self.dfu_return_s = dfu_return_s
        self.nv_file = nv_file
        if nv is None and nv_file and os.path.exists(nv_file):
            with open(nv_file, encoding="utf-8") as f:
                nv = Nv(**json.load(f))
        self.t0 = time.monotonic()
        self.device = Device(config, nv, now=0.0)
        self.faults = Faults()
        self.framer = Framer()
        self.lock = threading.RLock()
        self.outq = []               # (due real time, bytes)
        self.reset_at = None         # (real time, kind) after :22# / :31#
        self.offline = None          # (kind, real time it comes back; inf = at `replug`)
        self.received = []           # command bodies seen, newest last
        self.running = False
        self.thread: Optional[threading.Thread] = None

    # time
    def now(self) -> float:
        return (time.monotonic() - self.t0) * self.speed

    @property
    def port(self) -> str:
        return self.transport.name

    def start(self) -> "Simulator":
        self.transport.open()
        self.running = True
        self.thread = threading.Thread(target=self._run, name="astrouniboard-sim", daemon=True)
        self.thread.start()
        return self

    def stop(self):
        self.running = False
        if self.thread:
            self.thread.join(2)
        self.transport.close()
        self._save_nv()

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.stop()

    def _save_nv(self):
        if self.nv_file:
            from dataclasses import asdict
            with open(self.nv_file, "w", encoding="utf-8") as f:
                json.dump(asdict(self.device.nv), f, indent=1)

    # loop
    def _run(self):
        while self.running:
            try:
                data = self.transport.read(0.005)
            except Exception as e:  # keep serving; a transport error is a host problem
                print(f"sim: transport error: {e}", file=sys.stderr)
                time.sleep(0.05)
                data = b""
            with self.lock:
                self._step(data)

    def _step(self, data: bytes):
        real = time.monotonic()
        dev = self.device
        if self.offline is not None:
            kind, until = self.offline
            if kind == "unplug":        # USB cable out: the board keeps running on its own supply
                dev.advance(self.now())
            if real < until:
                return
            self.offline = None
            if kind != "unplug":
                dev.restart(self.now(), {"powercut": RESET_POWER_ON, "watchdog": RESET_WATCHDOG}.get(kind, RESET_SOFTWARE))
            self.framer = Framer()
            self.transport.replug()
            return
        # an open seen together with data happened before the host wrote that data
        if self.transport.host_opened():
            dev.port_opened()
            self.framer = Framer()
        for body in self.framer.feed(data):
            self.received.append(body if body is not None else "<overflow>")
            del self.received[:-200]
            reply = dev.handle(body, self.now()) if body is not None else f"ERR,{E_BAD_FORMAT}#"
            pending = dev.take_pending_reset()
            out = self._mangle(reply.encode("latin-1"))
            due = real + self.faults.delay_ms / 1000.0
            if self.outq:
                due = max(due, self.outq[-1][0])
            if out is not None:
                self.outq.append((due, out))
            if pending:
                self.reset_at = (due + 0.05, pending)
        while self.outq and self.outq[0][0] <= real:
            self.transport.write(self.outq.pop(0)[1])
        dev.advance(self.now())
        if self.reset_at and real >= self.reset_at[0]:
            kind = self.reset_at[1]
            self.reset_at = None
            self._go_offline(kind)

    def _mangle(self, reply: bytes) -> Optional[bytes]:
        f = self.faults
        if f.drop > 0:
            f.drop -= 1
            return None
        if f.silent:
            return None
        if f.garbage:
            reply = JUNK
        if f.nohash and reply.endswith(b"#"):
            reply = reply[:-1]
        if f.partial:
            reply = reply[:f.partial]
        return reply

    def _go_offline(self, kind: str, back_s: Optional[float] = None):
        """The device drops off the bus. kind: reboot / watchdog / powercut (back after reboot_s, firmware
        dead meanwhile), dfu (back after dfu_return_s or at `replug`), unplug (USB cable: back at `replug`,
        the firmware keeps running)."""
        self.outq.clear()
        self.reset_at = None
        self.transport.unplug()
        if back_s is None:
            back_s = {"dfu": self.dfu_return_s, "unplug": None}.get(kind, self.reboot_s)
        self.offline = (kind, time.monotonic() + back_s if back_s is not None else float("inf"))
        self._save_nv()

    # control
    def control(self, line: str) -> str:
        """Control command -> answer text ('' when nothing to say). Raises ValueError on bad input."""
        w = line.split()
        if not w:
            return ""
        c, args = w[0].lower(), w[1:]

        def num(i=0, default=None):
            if len(args) > i:
                return float(args[i]) if "." in args[i] else int(args[i], 0)
            if default is None:
                raise ValueError(f"{c}: value missing")
            return default

        with self.lock:
            dev, f = self.device, self.faults
            dev.advance(self.now())
            if c in ("silent", "garbage", "nohash"):
                setattr(f, c, bool(num(0, 1)))
            elif c == "partial":
                f.partial = num()
            elif c == "delay":
                f.delay_ms = num()
            elif c == "drop":
                f.drop = num(0, 1)
            elif c == "noise":
                self.transport.write(" ".join(args).encode("latin-1") if args else JUNK[:-1])
            elif c in ("unplug", "reboot", "powercut", "watchdog"):
                # powercut: RAM and pending position saves are lost; watchdog: comes back in safe mode
                self._go_offline(c, float(args[0]) if args and c != "unplug" else None)
            elif c == "replug":
                if self.offline is not None:
                    self.offline = (self.offline[0], time.monotonic())
            elif c == "fault":
                dev.inject_fault(num())
            elif c == "block":
                ax = 0 if args[0].upper() == "F" else 1
                dev.blocked[ax] = bool(num(1, 1))
            elif c == "drvtemp":
                ax = 0 if args[0].upper() == "F" else 1
                dev.drv_temp[ax] = num(1)
            elif c == "drvwarn":
                dev.drv_warn = num()
            elif c == "drvreset":
                dev.reset_latched = True
            elif c == "sht":
                which = {"onboard": "onboard", "o": "onboard", "external": "external", "x": "external"}[args[0]]
                dev.sht[which] = {"ok": 0, "absent": 1, "crc": 2}[args[1]]
                dev.env_next = dev.t
            elif c == "ambient":
                dev.ambient = num()
            elif c == "humidity":
                dev.humidity = num()
            elif c == "altitude":
                dev.altitude = num()
            elif c == "imu":
                dev.imu_present = bool(num(0, 1))
            elif c == "imuslip":
                dev.imu_slip = bool(num(0, 1))
            elif c == "camera":         # turn the camera by hand (cdeg): the IMU sees it, the counter not
                dev.camera_offset += num()
            elif c == "show":
                snap = dev.snapshot()
                snap.update(port=self.port, online=self.offline is None, received=len(self.received))
                return json.dumps(snap)
            elif c in ("quit", "exit"):
                self.running = False
            else:
                raise ValueError(f"unknown control command: {c}")
        return ""
