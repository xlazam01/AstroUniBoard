# Firmware Manager MCP Server

Manages firmware updates and DFU mode entry for the AstroUniBoard FocuserRotator (OnStepX).

## Prerequisite
- `dfu-util` must be installed (bundled with PlatformIO)
- `pip install -r requirements.txt`

## Tools

### `list_ports()`
List available COM ports.

**Returns:** List of port names

---

### `enter_dfu(port)`
Send `:EDFU#` command to jump to bootloader.

**Parameters:**
- `port` (str): Serial port (e.g., "COM6")

**Returns:** Status message

**Note:** Device will reboot into DFU mode. Wait 2-3 seconds before uploading.

---

### `upload_firmware(firmware_path)`
Upload firmware using dfu-util.

**Parameters:**
- `firmware_path` (str): Absolute path to .bin firmware file

**Returns:** Upload status and output

**Example:**
```python
upload_firmware("f:/Projekty/AstroUniBoard/FW - FocuserRotator/.pio/build/nucleo_g0b1re/firmware.bin")
```

---

### `enter_dfu_and_upload(port, firmware_path, wait_seconds=3)` - **RECOMMENDED**
Complete workflow: Send DFU command -> Wait -> Upload firmware.

**Parameters:**
- `port` (str): Serial port (e.g., "COM6")
- `firmware_path` (str): Absolute path to .bin firmware file
- `wait_seconds` (int): Seconds to wait after DFU command (default: 3)

**Returns:** Complete workflow status

**Example:**
```python
enter_dfu_and_upload(
    "COM6",
    "f:/Projekty/AstroUniBoard/FW - FocuserRotator/.pio/build/nucleo_g0b1re/firmware.bin",
    3
)
```

---

## Usage

### Starting the Server
```bash
python tools/firmware_manager/server.py
```

### Typical Workflow

**Option 1: All-in-one (Recommended)**
```python
enter_dfu_and_upload(
    "COM6",
    "f:/Projekty/AstroUniBoard/FW - FocuserRotator/.pio/build/nucleo_g0b1re/firmware.bin"
)
```

**Option 2: Manual steps**
```python
# Step 1: Send DFU command
enter_dfu("COM6")

# Step 2: Wait manually (3 seconds)

# Step 3: Upload
upload_firmware("f:/Projekty/AstroUniBoard/FW - FocuserRotator/.pio/build/nucleo_g0b1re/firmware.bin")
```

---

## Troubleshooting

### "No DFU capable USB device available"
**Cause:** Device not in DFU mode yet
**Solution:**
- Increase `wait_seconds` parameter (try 4-5 seconds)
- Manually reset device before upload

### "could not open port 'COMX'"
**Cause:** Port is in use
**Solution:**
- Close any serial monitors
- Close VSCode serial monitor
- Wait for port to be released

### "dfu-util not found"
**Cause:** dfu-util not in PATH
**Solution:**
- Verify path: `c:\Users\marti\.platformio\packages\tool-dfuutil\bin\dfu-util.exe`
- Server uses hardcoded path, should work automatically

---

## Configuration

The server is pre-configured for your environment:
- **DFU command:** `:EDFU#` (OnStepX custom command)
- **Serial baud:** 115200
- **dfu-util path:** `c:\Users\marti\.platformio\packages\tool-dfuutil\bin\dfu-util.exe`
- **Upload address:** `0x08000000` (STM32 flash start)
- **Upload mode:** `:leave` (device auto-reboots after upload)
- **Build output:** `.pio/build/nucleo_g0b1re/firmware.bin`
