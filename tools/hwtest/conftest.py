"""Hardware tests for the AstroUniBoard firmware V2 (docs/PROTOCOL.md 1.0) over USB CDC.

The board is found by USB VID 0x1209 / PID 0x0001 unless --port is given. That is the shared
pid.codes TEST pid (OpenWheel and other devices use it too), so a port is used only after it
identifies as ASTROUNIBOARD, and after a reboot the suite reconnects to the same USB serial number,
never to another device. Without a board every test is skipped, so
`python -m pytest --collect-only tools/hwtest` works anywhere.
"""
import re
import time

import pytest

try:
    import serial
    from serial.tools import list_ports
except ImportError:  # collection must still work; the board fixture skips
    serial = None
    list_ports = None

USB_VID = 0x1209
USB_PID = 0x0001
CMD_TIMEOUT = 1.0
SAVE_TIMEOUT = 2.0      # :23# writes the EEPROM
READ_POLL_S = 0.05      # serial read timeout, set once before open (never changed while open)

# values the firmware enforces (fw/src/tmc2209.h, protocol_common.cpp)
TMC_MAX_RUN_MA = 1500
IDLE_OFF_MAX_S = 3600


def pytest_addoption(parser):
    parser.addoption("--port", default=None, help="serial port of the board (default: find by VID/PID)")
    parser.addoption("--move-timeout", type=float, default=30.0, help="max seconds for one move")


IDENTIFY_PREFIX = "ASTROUNIBOARD,"


def identify(port):
    """:32# reply of the device on port, '' if it does not answer. Sends nothing else."""
    try:
        s = serial.Serial(port, 115200, timeout=CMD_TIMEOUT, write_timeout=1.0)
    except (serial.SerialException, OSError):
        return ""
    try:
        s.reset_input_buffer()
        s.write(b":32#")
        return s.read_until(b"#").decode("ascii", errors="replace")
    except (serial.SerialException, OSError):
        return ""
    finally:
        s.close()


def find_port(usb_serial=None):
    """First 1209:0001 port that identifies as an AstroUniBoard. With usb_serial (a board this
    session already identified) the USB serial number alone selects it and nothing is sent."""
    if list_ports is None:
        return None
    for p in list_ports.comports():
        if p.vid != USB_VID or p.pid != USB_PID:
            continue
        if usb_serial:
            if (p.serial_number or "").upper() == usb_serial.upper():
                return p.device
            continue
        if identify(p.device).startswith(IDENTIFY_PREFIX):
            return p.device
    return None


def usb_serial_number(port):
    if list_ports is None:
        return None
    for p in list_ports.comports():
        if p.device == port:
            return p.serial_number
    return None


class Board:
    """Minimal protocol client: one command at a time, every command gets exactly one reply."""

    def __init__(self, port, move_timeout=30.0):
        self.port = port
        self.usb_serial = usb_serial_number(port)   # reconnect only to this device
        self.move_timeout = move_timeout
        self.ser = None

    # --- transport ---
    def open(self):
        s = serial.Serial()
        s.port, s.baudrate, s.timeout, s.write_timeout = self.port, 115200, READ_POLL_S, 1.0
        s.dtr = False
        s.open()
        s.reset_input_buffer()
        self.ser = s

    def close(self):
        if self.ser and self.ser.is_open:
            self.ser.close()

    def reconnect(self, timeout=20.0, identify=True):
        """After a reboot: wait for the same board (same USB serial number, identifies as
        ASTROUNIBOARD) to come back. Never switches to another device with the same VID/PID.
        identify=True sends :32# so the firmware accepts other commands again (gate, #58);
        identify=False leaves the board locked (only :25# was sent)."""
        self.close()
        end = time.time() + timeout
        while time.time() < end:
            port = find_port(self.usb_serial) if self.usb_serial else None
            if port is None and not self.usb_serial and identify(self.port).startswith(IDENTIFY_PREFIX):
                port = self.port
            try:
                if port is None:
                    raise OSError("board not back yet")
                self.port = port
                self.open()
                if self.raw(":25#") == "OK#":
                    if identify:
                        ident = self.raw(":32#")
                        assert ident.startswith(IDENTIFY_PREFIX), f"{self.port}: {ident!r}"
                    return
            except (serial.SerialException, OSError):
                pass
            self.close()
            time.sleep(0.5)
        raise AssertionError(f"board did not come back within {timeout}s")

    def send(self, text):
        """Write without reading a reply (noise, partial commands)."""
        self.ser.write(text.encode("latin-1"))
        self.ser.flush()

    def raw(self, text, timeout=CMD_TIMEOUT):
        """Send text, return the reply up to and including '#', or '' on timeout.
        Never changes the port settings while open: any reconfiguration (timeout, baud) sends
        CDC class requests that lock the board again (identify gate, #58)."""
        self.ser.reset_input_buffer()
        self.send(text)
        out = b""
        end = time.time() + timeout
        while time.time() < end:
            c = self.ser.read(1)            # port read timeout is short (READ_POLL_S)
            if not c:
                continue
            out += c
            if c == b"#":
                break
        return out.decode("ascii", errors="replace")

    def cmd(self, text, timeout=None):
        """cmd(":32#") -> "ASTROUNIBOARD,...#". 1 s timeout, 2 s for :23#."""
        if timeout is None:
            timeout = SAVE_TIMEOUT if text.startswith(":23") else CMD_TIMEOUT
        r = self.raw(text, timeout)
        assert r.endswith("#"), f"{text!r}: no or partial reply {r!r}"
        return r

    # --- helpers ---
    def ok(self, text):
        r = self.cmd(text)
        assert r == "OK#", f"{text!r}: expected OK#, got {r!r}"

    def err(self, text, code):
        r = self.cmd(text)
        assert r == f"ERR,{code}#", f"{text!r}: expected ERR,{code}#, got {r!r}"

    def int(self, text):
        r = self.cmd(text)
        assert re.fullmatch(r"-?\d+#", r), f"{text!r}: expected a number, got {r!r}"
        return int(r[:-1])

    def fields(self, text, tag):
        r = self.cmd(text)
        parts = r[:-1].split(",")
        assert parts[0] == tag, f"{text!r}: expected tag {tag}, got {r!r}"
        return parts[1:]

    def status(self):
        f = self.fields(":33#", "S")
        assert len(f) == 7, f
        keys = ("state", "foc_pos", "foc_target", "rot_cdeg", "rot_target", "error")
        st = dict(zip(keys, map(int, f[:6])))
        st["flags"] = int(f[6], 16)
        return st

    def driver(self, axis):
        f = self.fields(f":47,{axis}#", "D")
        assert len(f) == 5, f
        return tuple(int(x) for x in f)

    def set_driver(self, axis, cfg):
        self.ok(f":48,{axis}," + ",".join(str(v) for v in cfg) + "#")

    def wait_idle(self, timeout=None):
        """Poll :33# until state 1 (idle). Returns the last status."""
        end = time.time() + (timeout or self.move_timeout)
        while time.time() < end:
            st = self.status()
            if st["state"] == 1:
                return st
            time.sleep(0.1)
        raise AssertionError(f"still busy after {timeout or self.move_timeout}s: {self.status()}")

    # --- every setting a test may change, so the session can put them back ---
    def snapshot(self):
        lo, hi = (int(x) for x in self.cmd(":74#")[:-1].split(","))
        return {
            "drv_F": self.driver("F"), "drv_R": self.driver("R"),
            "stall_F": int(self.fields(":41,F#", "G")[0]), "stall_R": int(self.fields(":41,R#", "G")[0]),
            "led": self.int(":43#"),
            "foc_max": self.int(":54#"), "foc_speed": self.int(":58#"), "foc_accel": self.int(":60#"),
            "foc_backlash": self.int(":62#"), "foc_reverse": self.int(":64#"), "foc_stepsize": self.int(":66#"),
            "rot_range": (lo, hi), "rot_speed": self.int(":78#"), "rot_accel": self.int(":80#"),
            "rot_reverse": self.int(":82#"), "rot_spd": self.int(":84#"), "rot_boothome": self.int(":86#"),
            "rot_tol": self.int(":88#"), "env_sel": self.int(":92#"),
        }

    def restore(self, s):
        # driver config first: a microstep change rescales the step-based values set after it
        self.set_driver("F", s["drv_F"])
        self.set_driver("R", s["drv_R"])
        self.ok(f":42,F,{s['stall_F']}#")
        self.ok(f":42,R,{s['stall_R']}#")
        self.ok(f":44,{s['led']}#")
        self.ok(f":55,{s['foc_max']}#")
        self.ok(f":59,{s['foc_speed']}#")
        self.ok(f":61,{s['foc_accel']}#")
        self.ok(f":63,{s['foc_backlash']}#")
        self.ok(f":65,{s['foc_reverse']}#")
        self.ok(f":67,{s['foc_stepsize']}#")
        self.ok(f":75,{s['rot_range'][0]},{s['rot_range'][1]}#")
        self.ok(f":79,{s['rot_speed']}#")
        self.ok(f":81,{s['rot_accel']}#")
        self.ok(f":83,{s['rot_reverse']}#")
        self.ok(f":85,{s['rot_spd']}#")
        self.ok(f":87,{s['rot_boothome']}#")
        self.ok(f":89,{s['rot_tol']}#")
        self.ok(f":93,{s['env_sel']}#")


@pytest.fixture(scope="session")
def board(request):
    if serial is None:
        pytest.skip("pyserial not installed (pip install -r tools/hwtest/requirements.txt)")
    port = request.config.getoption("--port") or find_port()
    if not port:
        pytest.skip(f"no AstroUniBoard found (USB {USB_VID:04X}:{USB_PID:04X}); use --port")
    b = Board(port, request.config.getoption("--move-timeout"))
    try:
        b.open()
    except (serial.SerialException, OSError) as e:
        pytest.skip(f"cannot open {port}: {e}")
    ident = b.raw(":32#")
    if not ident.startswith(IDENTIFY_PREFIX):
        b.close()
        pytest.exit(f"{port} is not an AstroUniBoard (:32# -> {ident!r}); nothing was sent but :32#", 2)
    b.wait_idle()
    b.initial = b.snapshot()
    yield b
    try:  # leave the board the way we found it
        b.wait_idle()
        if b.snapshot() != b.initial:
            b.restore(b.initial)
    finally:
        b.close()


@pytest.fixture
def idle(board):
    """The board, idle and with no sticky error left over from an earlier test."""
    board.wait_idle()
    yield board
    board.wait_idle()


@pytest.fixture
def restore_settings(board):
    """Snapshot before the test, restore afterwards (running values only, no :23#)."""
    snap = board.snapshot()
    yield board
    board.wait_idle()
    if board.snapshot() != snap:
        board.restore(snap)
