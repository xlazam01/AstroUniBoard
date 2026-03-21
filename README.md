# AstroUniBoard

A custom STM32G0B1-based telescope controller board focused on **focuser and rotator control**. Firmware is a port of [OnStepX](https://groups.io/g/onstep/wiki/home) to a custom PCB, with added SHT31 weather sensor support and DFU bootloader management.

## Repository Structure

```
AstroUniBoard/
├── FW/                     # Firmware (PlatformIO / Arduino / OnStepX)
│   ├── src/                # OnStepX source tree + custom config
│   ├── platformio.ini      # Build configuration
│   └── tools/              # Firmware manager MCP server (DFU upload)
├── HW/                     # Hardware (PCB designs, schematics — KiCad)
└── docs/
    ├── HARDWARE.md         # Pin assignments and hardware overview
    └── FIRMWARE.md         # Build and upload guide
```

## Hardware

| | |
|---|---|
| **MCU** | STM32G0B1CBT6 (ARM Cortex-M0+ @ 64 MHz) |
| **Flash / RAM** | 128 KB / 144 KB SRAM |
| **Package** | 48-pin LQFP |
| **Stepper Drivers** | 2× TMC2209 (UART, shared bus) |
| **NV Storage** | 24AA64 I2C EEPROM (8 KB) |
| **Weather Sensor** | SHT31 (temperature + humidity, I2C) |
| **Interface** | USB CDC (native USB PA11/PA12) |
| **Upload** | DFU bootloader (ROM) |

### Axes

| Axis | Function | Driver | UART Address |
|------|----------|--------|--------------|
| AXIS3 | Rotator | TMC2209 | 1 |
| AXIS4 | Focuser | TMC2209 | 0 |

Mount axes (AXIS1/AXIS2) are disabled — this board is focuser/rotator only.

See [docs/HARDWARE.md](docs/HARDWARE.md) for full pin assignments.

## Firmware

Based on **OnStepX** — advanced telescope controller firmware with LX200 protocol compatibility, ASCOM support, and stepper motor control.

### Custom additions in this port

- `STM32G0B1.h` — HAL implementation for STM32G0B1CBT6
- `Pins.AUB_FocRot.h` — Custom pinmap (model AUB_FOCROT = 25)
- SHT31 weather sensor support (not in upstream OnStepX)
- `:EDFU#` command — jump to DFU ROM bootloader for firmware updates
- Activity LED (PB4) driven by focuser/rotator movement

### Build status

- Flash: **93.3%** (122,244 / 131,072 bytes)
- RAM: **8.2%**
- Compiler: ARM GCC, `-Os -std=gnu++17`
- Hardware tested and functional

### Quick start

1. Install [PlatformIO](https://platformio.org/)
2. Open the `FW/` folder in VS Code
3. Build: `Ctrl+Alt+B` (environment: `nucleo_g0b1re`)
4. First upload: put board in DFU via BOOT0 switch, then `Ctrl+Alt+U`
5. Subsequent uploads: send `:EDFU#` via serial to enter DFU, then upload

See [docs/FIRMWARE.md](docs/FIRMWARE.md) for full build and upload instructions.

## Serial Commands

| Command | Action |
|---------|--------|
| `:EDFU#` | Jump to DFU ROM bootloader (firmware upload) |
| `:ERESET#` | Reset MCU |
| `:ENVRESET#` | Wipe NV memory (takes effect on next boot) |

Plus the full [OnStepX LX200 command set](https://groups.io/g/onstep/wiki/OnStep-Command-Reference).

## Communication

- **Port:** USB CDC (virtual COM port, no driver needed on Linux/Mac; WinUSB on Windows)
- **Baud rate:** 115200
- **Protocol:** LX200 / OnStepX command set
- **Compatible clients:** ASCOM OnStep driver, INDI OnStep driver, Android OnStep apps, Sky Planetarium

## Tools

### Firmware Manager MCP Server

`FW/tools/firmware_manager/server.py` — MCP server that automates DFU firmware updates from Claude Code or any MCP client.

```bash
pip install -r FW/tools/firmware_manager/requirements.txt
python FW/tools/firmware_manager/server.py
```

Key tool: `enter_dfu_and_upload(port, firmware_path)` — sends `:EDFU#`, waits for re-enumeration, uploads via dfu-util.

## License

Firmware is licensed under the GPL (inherited from OnStepX). See [FW/src/LICENSE](FW/src/LICENSE).

## Credits

- OnStepX firmware by [Howard Dutton](http://www.stellarjourney.com) and contributors
- STM32G0B1 HAL port and AstroUniBoard hardware by the project author
