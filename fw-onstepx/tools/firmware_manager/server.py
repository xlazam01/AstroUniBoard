"""
Firmware Manager MCP Server
============================

MCP (Model Context Protocol) server for updating the firmware of AstroUniBoard
FocuserRotator boards from the PC, without touching the board (no BOOT0 button).

It handles both firmware generations:

  V1  OnStepX firmware (fw-onstepx/)   jump to DFU with  :EDFU#
  V2  PlatformIO firmware (fw/)        identify :32#, then :31#  (docs/PROTOCOL.md)

V2 boards enumerate as USB 1209:0001 (shared pid.codes test PID, also used by
OpenWheel), so a port only counts as V2 when it also answers :32# with
"ASTROUNIBOARD,...". The V2 identify gate re-locks every time the port is
opened, so :32# and :31# are sent over one open port, without changing its
settings in between.

Both generations jump to the STM32 system bootloader (USB 0483:df11); the image
is written with dfu-util at 0x08000000.

Tools:
    list_ports()                     ports with VID:PID, serial number, generation guess
    detect_board(port)               V1 / V2 (+ identify fields for V2)
    enter_dfu(port, generation)      send the DFU command for the detected generation
    upload_firmware(firmware_path)   dfu-util download to 0x08000000 (:leave)
    enter_dfu_and_upload(port, firmware_path, ...)
                                     full cycle; for V2 also waits for the board to come
                                     back and checks with :32# that the new firmware answers
    build_firmware(project_dir, environment)
    serial_read(port, baudrate, duration_seconds)

Prerequisites: Python 3.10+, pyserial, mcp[cli], dfu-util (PlatformIO's tool-dfuutil works).
Run: python server.py   (or: mcp run server.py)
"""

import logging
import os
import re
import shutil
import subprocess
import time
from dataclasses import dataclass
from typing import Optional

import serial
import serial.tools.list_ports

try:
    from mcp.server.fastmcp import FastMCP
except ImportError:  # unit tests run without the MCP SDK
    FastMCP = None

# Tool locations: PATH first, then the PlatformIO install in the user home
PIO_HOME = os.path.join(os.path.expanduser("~"), ".platformio")
_EXE = ".exe" if os.name == "nt" else ""
DFU_UTIL = shutil.which("dfu-util") or os.path.join(PIO_HOME, "packages", "tool-dfuutil", "bin", "dfu-util" + _EXE)
PIO = shutil.which("pio") or os.path.join(PIO_HOME, "penv", "Scripts" if os.name == "nt" else "bin", "pio" + _EXE)

# Repository layout: <repo>/fw-onstepx/tools/firmware_manager/server.py
REPO_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
V1_PROJECT_DIR = os.path.join(REPO_DIR, "fw-onstepx")
V1_ENV = "nucleo_g0b1re"
V2_PROJECT_DIR = os.path.join(REPO_DIR, "fw")
V2_ENV = "rev2"  # default_envs in fw/platformio.ini
V2_CONFIG_H = os.path.join(V2_PROJECT_DIR, "src", "config.h")

V2_VID, V2_PID = 0x1209, 0x0001
DFU_VID_PID = "0483:df11"      # STM32 system bootloader
FLASH_ADDRESS = "0x08000000"
V2_FLASH_SIZE = 128 * 1024     # STM32G0B1CBT6
V2_PROTOCOLS = {2}             # protocol versions this tool understands

BAUD = 115200                  # nominal, ignored over USB CDC
REPLY_TIMEOUT = 1.0

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


def _tool(fn):
    """Register fn as an MCP tool when the SDK is available; return fn unchanged."""
    if mcp is not None:
        mcp.tool()(fn)
    return fn


mcp = FastMCP("FirmwareManager") if FastMCP is not None else None


# ---------------------------------------------------------------- helpers

@dataclass
class Identity:
    model: str
    firmware: str
    protocol: int
    serial: str
    caps: int

    def __str__(self) -> str:
        return (f"{self.model} {self.firmware} protocol {self.protocol} "
                f"serial {self.serial} caps 0x{self.caps:X}")


def parse_identify(reply: str) -> Optional[Identity]:
    """Parse an identify reply 'ASTROUNIBOARD,<model>,<fw>,<protocol>,<serial>,<caps>#'."""
    m = re.fullmatch(r"ASTROUNIBOARD,([^,#]+),([^,#]+),(\d+),([0-9A-Fa-f]+),([0-9A-Fa-f]+)#?",
                     reply.strip())
    if not m:
        return None
    return Identity(m.group(1), m.group(2), int(m.group(3)), m.group(4).upper(), int(m.group(5), 16))


def read_reply(ser, timeout: float = REPLY_TIMEOUT) -> str:
    """Read one reply line ending in '#'. Returns what arrived ('' on timeout)."""
    buf = bytearray()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        chunk = ser.read(1)
        if chunk:
            buf += chunk
            if chunk == b"#":
                break
    return buf.decode(errors="replace")


def command(ser, cmd: str, timeout: float = REPLY_TIMEOUT) -> str:
    """Send one command and return its reply. The port's settings are never touched."""
    ser.reset_input_buffer()
    ser.write(cmd.encode())
    ser.flush()
    return read_reply(ser, timeout)


def identify(ser) -> Optional[Identity]:
    """Send :32# (also unlocks the V2 identify gate) and parse the reply."""
    return parse_identify(command(ser, ":32#"))


def port_info(port: str):
    for p in serial.tools.list_ports.comports():
        if p.device.lower() == port.lower():
            return p
    return None


def is_v2_usb_id(info) -> bool:
    return info is not None and info.vid == V2_VID and info.pid == V2_PID


def find_port_by_serial(serial_number: str) -> Optional[str]:
    for p in serial.tools.list_ports.comports():
        if is_v2_usb_id(p) and (p.serial_number or "").upper() == serial_number.upper():
            return p.device
    return None


def expected_v2_firmware() -> Optional[str]:
    """FIRMWARE_ID from fw/src/config.h, e.g. 'FW2.0.0' (what :32# reports after the update)."""
    try:
        with open(V2_CONFIG_H, encoding="utf-8") as f:
            m = re.search(r'#define\s+FIRMWARE_ID\s+"([^"]+)"', f.read())
        return m.group(1) if m else None
    except OSError:
        return None


def default_firmware_path(generation: str) -> str:
    if generation == "v2":
        return os.path.join(V2_PROJECT_DIR, ".pio", "build", V2_ENV, "firmware.bin")
    return os.path.join(V1_PROJECT_DIR, ".pio", "build", V1_ENV, "firmware.bin")


def detect(port: str, generation: str = "auto"):
    """Return (generation, Identity or None, message).

    generation 'auto': a 1209:0001 port must answer :32# with ASTROUNIBOARD to be V2
    (the PID is shared with other devices); any other port is V1 (OnStepX) and is not
    probed, so nothing unknown is ever sent to an OnStepX board.
    """
    if generation not in ("auto", "v1", "v2"):
        return None, None, f"unknown generation '{generation}' (use auto, v1 or v2)"
    if generation == "v1":
        return "v1", None, "V1 (OnStepX) as requested"
    info = port_info(port)
    if generation == "auto" and not is_v2_usb_id(info):
        vidpid = f"{info.vid:04x}:{info.pid:04x}" if info is not None and info.vid is not None else "unknown"
        return "v1", None, f"V1 (OnStepX): USB {vidpid} is not the V2 id 1209:0001"
    try:
        with serial.Serial(port, BAUD, timeout=0.05) as ser:
            ident = identify(ser)
    except Exception as e:
        return None, None, f"could not open {port}: {e}"
    if ident is None:
        return None, None, f"{port} is 1209:0001 but does not answer :32# as an AstroUniBoard (OpenWheel or another device?)"
    if ident.protocol not in V2_PROTOCOLS:
        return None, ident, f"{port}: AstroUniBoard protocol {ident.protocol} is not supported by this tool"
    return "v2", ident, f"V2: {ident}"


def send_dfu_command(port: str, generation: str) -> str:
    """Send the jump-to-bootloader command. Raises RuntimeError on failure."""
    with serial.Serial(port, BAUD, timeout=0.05) as ser:
        if generation == "v1":
            ser.write(b":EDFU#")
            ser.flush()
            time.sleep(0.1)
            return "sent :EDFU#"
        # V2: identify and :31# over the same open port (the gate re-locks on every open)
        ident = identify(ser)
        if ident is None:
            raise RuntimeError("no identify reply to :32#")
        reply = command(ser, ":31#")
        if reply == "ERR,16#" or reply == "ERR,16":
            # gate re-locked (e.g. another program touched the port): identify again, same board?
            again = identify(ser)
            if again is None or again.serial != ident.serial:
                raise RuntimeError("identify gate re-locked and the board did not identify again")
            reply = command(ser, ":31#")
        if not reply.startswith("OK"):
            raise RuntimeError(f":31# answered {reply!r} instead of OK#")
        return f"sent :32# ({ident.firmware}) and :31# (OK#)"


def dfu_devices() -> list:
    """Lines of `dfu-util -l` that list the STM32 system bootloader (one per alt setting)."""
    try:
        result = subprocess.run([DFU_UTIL, "-l"], capture_output=True, text=True, timeout=10)
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return []
    return [line for line in (result.stdout + result.stderr).splitlines()
            if f"[{DFU_VID_PID}]" in line.lower() and "alt=0" in line]


def wait_for_dfu(timeout: float) -> int:
    """Wait until a 0483:df11 device shows up. Returns how many (0 on timeout)."""
    deadline = time.monotonic() + timeout
    while True:
        n = len(dfu_devices())
        if n or time.monotonic() >= deadline:
            return n
        time.sleep(0.5)


def run_dfu_util(firmware_path: str):
    """Download the image. Returns (ok, output)."""
    cmd = [DFU_UTIL, "-d", DFU_VID_PID, "-a", "0", "-s", f"{FLASH_ADDRESS}:leave", "-D", firmware_path]
    logger.info(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)
    output = result.stdout + result.stderr
    # dfu-util exits non-zero with "get_status" on STM32G0 after a successful flash
    # (device reboots before dfu-util can read the status — this is a known false positive)
    return "File downloaded successfully" in output, f"(exit {result.returncode})\n{output}"


def wait_for_v2_board(serial_number: str, timeout: float) -> Optional[Identity]:
    """Wait for the board with this USB serial number to come back and answer :32#."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        port = find_port_by_serial(serial_number)
        if port:
            try:
                with serial.Serial(port, BAUD, timeout=0.05) as ser:
                    ident = identify(ser)
                if ident is not None:
                    return ident
            except Exception:
                pass  # port still enumerating
        time.sleep(0.5)
    return None


def check_image(firmware_path: str, generation: str) -> Optional[str]:
    """Return an error message, or None when the image looks flashable."""
    if not os.path.isfile(firmware_path):
        return f"firmware file not found: {firmware_path}"
    size = os.path.getsize(firmware_path)
    if size == 0:
        return f"firmware file is empty: {firmware_path}"
    if generation == "v2" and size > V2_FLASH_SIZE:
        return f"firmware is {size} bytes, more than the {V2_FLASH_SIZE} bytes of flash"
    return None


# ---------------------------------------------------------------- tools

@_tool
def list_ports() -> str:
    """List serial ports with USB VID:PID, serial number and a firmware generation guess."""
    ports = serial.tools.list_ports.comports()
    if not ports:
        return "No serial ports found."
    lines = []
    for p in ports:
        vidpid = f"{p.vid:04x}:{p.pid:04x}" if p.vid is not None else "----:----"
        guess = "V2? (1209:0001, confirm with detect_board)" if is_v2_usb_id(p) else ""
        lines.append(f"{p.device}  {vidpid}  SER={p.serial_number or '-'}  {p.description}  {guess}".rstrip())
    dfu = dfu_devices()
    if dfu:
        lines.append(f"DFU bootloader ({DFU_VID_PID}) present: {len(dfu)} device(s)")
    return "\n".join(lines)


@_tool
def detect_board(port: str) -> str:
    """Tell whether <port> runs the V1 (OnStepX) or the V2 firmware (identify :32#)."""
    gen, _, msg = detect(port)
    return f"{port}: {msg}" if gen else f"{port}: not usable — {msg}"


@_tool
def enter_dfu(port: str, generation: str = "auto") -> str:
    """Jump to the USB DFU bootloader: :EDFU# for V1 (OnStepX), :32# + :31# for V2.

    Args:
        port: Serial port (e.g. "COM6")
        generation: "auto" (detect), "v1" or "v2"
    """
    gen, _, msg = detect(port, generation)
    if gen is None:
        return f"Error: {msg}"
    try:
        sent = send_dfu_command(port, gen)
    except Exception as e:
        return f"Error sending DFU command ({gen}): {e}"
    return f"{msg}. {sent} to {port}; the board re-enumerates as {DFU_VID_PID}."


@_tool
def upload_firmware(firmware_path: str) -> str:
    """Upload a .bin with dfu-util to 0x08000000 of a board already in DFU mode (0483:df11).

    Args:
        firmware_path: Absolute path to the .bin firmware file.
    """
    err = check_image(firmware_path, "v1")
    if err:
        return f"Error: {err}"
    n = len(dfu_devices())
    if n > 1:
        return f"Error: {n} DFU bootloaders found — leave only the board to update in DFU mode."
    try:
        ok, output = run_dfu_util(firmware_path)
    except FileNotFoundError:
        return "Error: dfu-util not found. Please verify it is installed and in PATH."
    return f"Upload successful {output}" if ok else f"Upload failed {output}"


@_tool
def enter_dfu_and_upload(
    port: str,
    firmware_path: str = "",
    wait_seconds: int = 10,
    generation: str = "auto",
    expected_firmware: str = "",
) -> str:
    """Full update: detect V1/V2, jump to DFU, flash, and (V2) verify the new firmware answers.

    Args:
        port: Serial port of the running board (e.g. "COM6")
        firmware_path: .bin to flash; default is the build output of the detected generation
            (V2: fw/.pio/build/rev2/firmware.bin, V1: fw-onstepx/.pio/build/nucleo_g0b1re/firmware.bin)
        wait_seconds: max seconds to wait for the bootloader, and again for the board to come back
        generation: "auto" (detect), "v1" or "v2"
        expected_firmware: V2 only, firmware field :32# must report after the update
            (default: FIRMWARE_ID from fw/src/config.h, e.g. "FW2.0.0"; "-" skips the check)
    """
    log = []
    gen, before, msg = detect(port, generation)
    if gen is None:
        return f"Error: {msg}"
    log.append(f"Board: {msg}")

    path = firmware_path or default_firmware_path(gen)
    err = check_image(path, gen)
    if err:
        return f"Error: {err}" + ("" if firmware_path else " (build it first with build_firmware)")
    log.append(f"Image: {path} ({os.path.getsize(path)} bytes)")

    if dfu_devices():
        return "Error: a DFU bootloader is already present — finish or unplug it first, so the right board gets flashed."

    try:
        log.append(send_dfu_command(port, gen))
    except Exception as e:
        return "\n".join(log + [f"Error sending DFU command: {e}"])

    n = wait_for_dfu(wait_seconds)
    if n == 0:
        return "\n".join(log + [f"Error: no {DFU_VID_PID} bootloader within {wait_seconds}s."])
    if n > 1:
        return "\n".join(log + [f"Error: {n} DFU bootloaders present, not flashing."])
    log.append("Bootloader is up.")

    try:
        ok, output = run_dfu_util(path)
    except FileNotFoundError:
        return "\n".join(log + ["Error: dfu-util not found."])
    if not ok:
        return "\n".join(log + [f"Upload failed {output}"])
    log.append("Upload successful.")

    if gen == "v1":
        log.append("V1 has no identify command: check the board with serial_read or the OnStepX app.")
        return "\n".join(log)

    after = wait_for_v2_board(before.serial, wait_seconds)
    if after is None:
        return "\n".join(log + [f"Error: board {before.serial} did not answer :32# within {wait_seconds}s after the update."])
    log.append(f"Back on {find_port_by_serial(before.serial)}: {after}")

    expected = expected_firmware or expected_v2_firmware()
    if not expected or expected == "-":
        log.append(f"Version check skipped: {before.firmware} -> {after.firmware}.")
    elif after.firmware != expected:
        return "\n".join(log + [f"Error: board reports {after.firmware}, expected {expected}."])
    else:
        log.append(f"Verified: {before.firmware} -> {after.firmware} (expected {expected}).")
    return "\n".join(log)


@_tool
def build_firmware(project_dir: str = V2_PROJECT_DIR, environment: str = V2_ENV) -> str:
    """Build firmware using PlatformIO.

    Args:
        project_dir: PlatformIO project root (default: the V2 firmware, fw/; V1 is fw-onstepx/).
        environment: PlatformIO environment (default: rev2; the V1 one is nucleo_g0b1re).
    """
    if not os.path.isfile(os.path.join(project_dir, "platformio.ini")):
        return f"Error: platformio.ini not found in {project_dir}"
    cmd = [PIO, "run", "-d", project_dir, "-e", environment]
    try:
        logger.info(f"Running: {' '.join(cmd)}")
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0:
            return f"Build successful (environment: {environment}):\n{result.stdout}"
        return f"Build failed:\n{result.stdout}\n{result.stderr}"
    except FileNotFoundError:
        return f"Error: pio not found at {PIO}"


@_tool
def serial_read(port: str, baudrate: int = 9600, duration_seconds: int = 5) -> str:
    """Read data from a serial port for a specified duration.

    The V2 firmware never sends anything unsolicited; this is for V1 logs.

    Args:
        port: Serial port to read from (e.g., "COM6").
        baudrate: Baud rate (default: 9600).
        duration_seconds: How long to read, in seconds (default: 5).
    """
    try:
        with serial.Serial(port, baudrate, timeout=1) as ser:
            logger.info(f"Reading from {port} at {baudrate} baud for {duration_seconds}s...")
            collected = []
            deadline = time.time() + duration_seconds
            while time.time() < deadline:
                raw = ser.read(ser.in_waiting or 1)
                if raw:
                    collected.append(raw.decode(errors="replace"))
            output = "".join(collected)
            if not output:
                return f"No data received from {port} within {duration_seconds}s."
            return f"Serial output from {port} ({duration_seconds}s):\n{output}"
    except Exception as e:
        return f"Error reading from {port}: {e}"


if __name__ == "__main__":
    if mcp is None:
        raise SystemExit("mcp is not installed: pip install -r requirements.txt")
    mcp.run()
