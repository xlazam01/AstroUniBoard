"""
Firmware Manager MCP Server
============================

MCP (Model Context Protocol) server for managing firmware on AstroUniBoard
FocuserRotator devices over serial/DFU interface.

Description:
    This server exposes tools that allow an MCP-compatible client (e.g. Claude)
    to interact with STM32-based AstroUniBoard hardware:
      - List available serial ports
      - Send a "jump to DFU" command (:EDFU#) over a serial connection
      - Upload a .bin firmware image via dfu-util
      - Perform the full update cycle (DFU command + wait + upload) in one step

Prerequisites:
    - Python 3.10+
    - pyserial          (pip install pyserial)
    - mcp[cli]          (pip install mcp[cli])
    - dfu-util          (bundled with PlatformIO or installed separately)

Usage:
    Run directly:
        python server.py

    Or via the MCP CLI:
        mcp run server.py

    Once running, the server exposes the following tools to any MCP client:

    list_ports()
        Returns a newline-separated list of available serial (COM) ports.

    enter_dfu(port)
        Opens <port> at 115200 baud and sends the :EDFU# command to make the
        device jump into DFU bootloader mode.

    upload_firmware(firmware_path)
        Calls dfu-util to flash the given .bin file at address 0x08000000.

    enter_dfu_and_upload(port, firmware_path, wait_seconds=3)
        Convenience wrapper: sends the DFU command, waits for the bootloader
        to become ready, then uploads the firmware — all in a single call.

    build_firmware(project_dir, environment)
        Runs `pio run` to compile the firmware using PlatformIO.  Returns
        the full build output (stdout + stderr).  The default environment
        is "nucleo_g0b1re" as defined in platformio.ini.

    serial_read(port, baudrate, duration_seconds)
        Opens a serial port and captures all incoming data for the given
        duration.  Useful for reading device logs, debug output, or
        verifying that the device booted correctly after a firmware update.

Example (inside an MCP client session):
    >>> list_ports()
    COM3
    COM6

    >>> build_firmware()
    Build successful (environment: nucleo_g0b1re): ...

    >>> enter_dfu_and_upload("COM6", "C:/path/to/firmware.bin")
    Success! DFU command sent, waited 3s, upload completed: ...

    >>> serial_read("COM6", duration_seconds=5)
    [serial output captured over 5 seconds]
"""

import asyncio
import logging
import os
import subprocess
import time
from typing import Optional

from mcp.server.fastmcp import FastMCP
import serial
import serial.tools.list_ports

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Initialize FastMCP server
mcp = FastMCP("FirmwareManager")

@mcp.tool()
def list_ports() -> str:
    """List available serial ports."""
    ports = serial.tools.list_ports.comports()
    if not ports:
        return "No serial ports found."

    result = []
    for p in ports:
        result.append(f"{p.device}")
    return "\n".join(result)

@mcp.tool()
def enter_dfu(port: str) -> str:
    """Send jump to DFU command (:EDFU#) to the specified port."""
    try:
        with serial.Serial(port, 115200, timeout=1) as ser:
            ser.write(b':EDFU#')
            ser.flush()
            time.sleep(0.1)
        return f"Sent jump to DFU command to {port}. Device should now be in DFU mode."
    except Exception as e:
        return f"Error sending DFU command: {e}"

@mcp.tool()
def upload_firmware(firmware_path: str) -> str:
    """Upload firmware using dfu-util.

    Args:
        firmware_path: Absolute path to the .bin firmware file.
    """
    cmd = [
        r"c:\Users\marti\.platformio\packages\tool-dfuutil\bin\dfu-util.exe",
        "-a", "0",
        "-s", "0x08000000:leave",
        "-D", firmware_path
    ]

    try:
        logger.info(f"Running: {' '.join(cmd)}")
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return f"Upload successful:\n{result.stdout}"
    except subprocess.CalledProcessError as e:
        return f"Upload failed:\n{e.stdout}\n{e.stderr}"
    except FileNotFoundError:
        return "Error: dfu-util not found. Please verify it is installed and in PATH."

@mcp.tool()
def enter_dfu_and_upload(port: str, firmware_path: str, wait_seconds: int = 3) -> str:
    """Send DFU command, wait for device to enter DFU mode, then upload firmware.

    Args:
        port: Serial port (e.g., "COM6")
        firmware_path: Absolute path to .bin firmware file
        wait_seconds: Seconds to wait after DFU command (default: 3)
    """
    # Step 1: Send DFU command
    try:
        logger.info(f"Sending DFU command to {port}...")
        with serial.Serial(port, 115200, timeout=1) as ser:
            ser.write(b':EDFU#')
            ser.flush()
            time.sleep(0.1)
        logger.info(f"DFU command sent successfully")
    except Exception as e:
        return f"Error sending DFU command: {e}"

    # Step 2: Wait for device to enter DFU mode
    logger.info(f"Waiting {wait_seconds} seconds for device to enter DFU mode...")
    time.sleep(wait_seconds)

    # Step 3: Upload firmware
    logger.info(f"Starting firmware upload...")
    cmd = [
        r"c:\Users\marti\.platformio\packages\tool-dfuutil\bin\dfu-util.exe",
        "-a", "0",
        "-s", "0x08000000:leave",
        "-D", firmware_path
    ]

    try:
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return f"Success! DFU command sent, waited {wait_seconds}s, upload completed:\n{result.stdout}"
    except subprocess.CalledProcessError as e:
        return f"Upload failed after DFU command:\n{e.stdout}\n{e.stderr}"
    except FileNotFoundError:
        return "Error: dfu-util not found."

@mcp.tool()
def build_firmware(
    project_dir: str = r"f:\Projekty\AstroUniBoard\FW - FocuserRotator",
    environment: str = "nucleo_g0b1re",
) -> str:
    """Build firmware using PlatformIO.

    Args:
        project_dir: Path to the PlatformIO project root (contains platformio.ini).
        environment: PlatformIO environment name to build (default: nucleo_g0b1re).
    """
    if not os.path.isfile(os.path.join(project_dir, "platformio.ini")):
        return f"Error: platformio.ini not found in {project_dir}"

    cmd = ["pio", "run", "-d", project_dir, "-e", environment]

    try:
        logger.info(f"Running: {' '.join(cmd)}")
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return f"Build successful (environment: {environment}):\n{result.stdout}"
    except subprocess.CalledProcessError as e:
        return f"Build failed:\n{e.stdout}\n{e.stderr}"
    except FileNotFoundError:
        return "Error: pio (PlatformIO CLI) not found. Please verify it is installed and in PATH."

@mcp.tool()
def serial_read(
    port: str,
    baudrate: int = 9600,
    duration_seconds: int = 5,
) -> str:
    """Read data from a serial port for a specified duration.

    Opens the serial port, captures all incoming data until the duration
    elapses, and returns the collected output.  Useful for reading device
    logs or verifying post-flash behaviour.

    Args:
        port: Serial port to read from (e.g., "COM6").
        baudrate: Baud rate (default: 9600, matching monitor_speed in platformio.ini).
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
    mcp.run()
