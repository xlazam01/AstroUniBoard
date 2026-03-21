# AstroUniBoard — Firmware Build & Upload Guide

## Prerequisites

- [VS Code](https://code.visualstudio.com/) with [PlatformIO extension](https://platformio.org/install/ide?install=vscode)
- Open the `FW/` folder as the PlatformIO project root

## Build

Open `FW/` in VS Code and use PlatformIO:

- **GUI:** PlatformIO sidebar → `nucleo_g0b1re` environment → Build
- **Keyboard:** `Ctrl+Alt+B`
- **CLI:** `pio run -e nucleo_g0b1re`

Build output: `FW/.pio/build/nucleo_g0b1re/firmware.bin`

### Build configuration summary

| Setting | Value |
|---------|-------|
| Platform | ststm32 |
| Board | btt_ebb42_v1_1 |
| Framework | Arduino |
| Upload protocol | DFU |
| Optimization | `-Os -std=gnu++17` |
| USB CDC flags | `-DUSBCON -DUSBD_USE_CDC -DHAL_PCD_MODULE_ENABLED` |

### Flash budget

| Metric | Value |
|--------|-------|
| Flash used | 122,244 / 131,072 bytes (93.3%) |
| RAM used | ~8.2% |

> Debug level must stay at `ON`, not `VERBOSE`. VERBOSE adds ~15 KB of string literals and overflows the 128 KB flash.

## Upload

### Via DFU (normal workflow)

1. Connect the board via USB
2. Send `:EDFU#` via any serial terminal at 115200 baud
   - Board disconnects from CDC and re-enumerates as STM32 DFU device
3. Upload: `Ctrl+Alt+U` in VS Code, or the Firmware Manager tool (see below)
4. Board auto-resets and runs new firmware
5. **Note:** "Error 74" at end of DFU upload is harmless (DFU leave status quirk)

### First upload (no firmware yet)

Board has no `:EDFU#` command, so enter DFU manually:

1. Hold BOOT0 switch, press RESET (or power-cycle with BOOT0 held)
2. Release BOOT0 — board enumerates as STM32 DFU device
3. Upload with `Ctrl+Alt+U`

### Firmware Manager MCP Server (automated)

`FW/tools/firmware_manager/server.py` provides an MCP server for one-step DFU upload from Claude Code.

```bash
pip install -r FW/tools/firmware_manager/requirements.txt
python FW/tools/firmware_manager/server.py
```

**Recommended:** `enter_dfu_and_upload(port, firmware_path)` — handles the full workflow.

```python
enter_dfu_and_upload(
    "COM6",
    "f:/Projekty/AstroUniBoard/FW/.pio/build/nucleo_g0b1re/firmware.bin"
)
```

Available tools:

| Tool | Description |
|------|-------------|
| `list_ports()` | List available COM ports |
| `enter_dfu(port)` | Send `:EDFU#` command |
| `upload_firmware(firmware_path)` | Upload .bin via dfu-util (device must already be in DFU) |
| `enter_dfu_and_upload(port, path, wait_seconds=3)` | Complete workflow (recommended) |
| `build_firmware(project_dir, environment)` | Build via PlatformIO CLI |
| `serial_read(port, baudrate, duration_seconds)` | Read serial output |

## Configuration

### Primary: `FW/src/Config.h`

```c
#define PINMAP          AUB_FOCROT    // Custom board
#define SERIAL_A_BAUD   115200        // USB CDC
#define WEATHER         SHT31         // I2C 0x44
#define STATUS_LED      ON
#define AXIS1_DRIVER_MODEL  OFF       // No mount
#define AXIS2_DRIVER_MODEL  OFF       // No mount
#define AXIS3_DRIVER_MODEL  TMC2209   // Rotator
#define AXIS4_DRIVER_MODEL  TMC2209   // Focuser
```

### Extended: `FW/src/Extended.config.h`

```c
#define DEBUG           ON            // Use ON, never VERBOSE
#define SERIAL_DEBUG    SerialUSB
#define NV_WIPE         OFF
```

## Serial Commands

| Command | Action |
|---------|--------|
| `:EDFU#` | Jump to DFU ROM bootloader |
| `:ERESET#` | Reset MCU |
| `:ENVRESET#` | Wipe NV memory on next boot |

Full OnStepX LX200 command set applies for focuser and rotator control.

## Troubleshooting

### "No DFU capable USB device available"
Device not in DFU mode yet. Increase `wait_seconds` to 4–5, or check BOOT0.

### "could not open port COMX"
Port is in use. Close any serial monitors before uploading.

### Board not responding after upload
Check that `SerialUSB` (not `Serial`) is used for USB CDC. The firmware uses `SerialUSB` explicitly.

### dfu-util not found
PlatformIO bundles dfu-util. Path: `C:\Users\<user>\.platformio\packages\tool-dfuutil\bin\dfu-util.exe`
