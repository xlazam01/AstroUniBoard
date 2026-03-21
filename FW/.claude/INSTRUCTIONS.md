# OnStepX STM32G0B1 Port - Implementation Instructions

## Build Status: Phase 5 COMPLETE, Phase 6 pending
**Configuration:** Custom PCB with STM32G0B1CBT6, AUB_FOCROT pinmap, TMC2209 drivers
**Compilation:** Successful - Flash 93.3% (122,244 / 131,072 bytes), RAM 8.2%
**Status:** Firmware running on hardware, USB CDC working, DFU command working

## Project Goal
Port the OnStepX telescope controller firmware to the STM32G0B1CBT6 microcontroller (custom AstroUniBoard PCB). Focus exclusively on focuser and rotator control (no mount axes). Implement SHT31 temperature/humidity sensor support (not in upstream OnStepX).

## Hardware Target
- **Board:** Custom PCB (AstroUniBoard Focuser/Rotator)
- **Microcontroller:** STM32G0B1CBT6 (48-pin LQFP)
- **Architecture:** ARM Cortex-M0+ @ 64 MHz
- **Flash:** 128 KB
- **RAM:** 144 KB SRAM
- **Stepper Drivers:** 2x TMC2209 on shared UART2 (PA2 TX, PA3 RX)
- **NV Storage:** 24AA64 I2C EEPROM (8 KB, address 0x50)
- **Sensors:** SHT31 I2C temperature/humidity (address 0x44)
- **I2C Bus:** SCL=PA9, SDA=PA10 (24AA64 + SHT31)
- **USB:** Native USB CDC on PA11/PA12 (SerialUSB)
- **LEDs:** PB12 (status, active LOW), PB4 (activity, active LOW)
- **Build Tool:** PlatformIO with STMicroelectronics platform
- **PlatformIO Board:** btt_ebb42_v1_1 (same STM32G0B1 chip, proper STM32duino support)
- **Reference project:** C:\Users\marti\Documents\PlatformIO\Projects\251205-184509-genericSTM32F103CB (FilterWheel STM32G0B1 with proven DFU bootloader jump)

## Axis Configuration (OnStepX Convention - HARDCODED, cannot be swapped)
- **AXIS1/AXIS2 (Mount):** OFF - not used on this board
- **AXIS3 (Rotator):** TMC2209, STEP=PB9, DIR=PB8, EN=PB7, UART addr 1
- **AXIS4 (Focuser):** TMC2209, STEP=PB2, DIR=PB1, EN=PB0, UART addr 0

## Critical Constraints
1. **Flash Size:** Only 128KB available - use `-Os` optimization, avoid VERBOSE debug
2. **Real-time Timing:** Stepper control requires microsecond-level precision for pulse generation
3. **No Core Changes:** Mount control logic and telescope functionality remain unchanged
4. **Focuser/Rotator Only:** AXIS1/AXIS2 (mount) are OFF; only AXIS3 (Rotator) and AXIS4 (Focuser) are active
5. **OnStepX Axis Convention:** AXIS3 = Rotator, AXIS4 = Focuser (hardcoded in OnStepX)
6. **Preserve Compatibility:** Maintain protocol compatibility (LX200, ASCOM-compatible behavior)
7. **USB CDC:** Must use `SerialUSB` explicitly (not `Serial`) for USB CDC communication
8. **DEBUG level:** Use `ON` not `VERBOSE` - VERBOSE adds ~15KB of string literals, overflows 128KB

## Project Phases

### PHASE 1: Hardware Abstraction Layer (HAL) Implementation [COMPLETE]
**Objective:** Create G0B1-specific hardware drivers

Files created/modified:
- `src/src/HAL/stm32/STM32G0B1.h` - HAL header with NV_2464, DFU bootloader direct jump
- `src/src/HAL/HAL.h` - G0B1 detection added
- `src/src/Constants.h` - AUB_FOCROT constant + SHT31 weather sensor constants
- `src/src/pinmaps/Pins.AUB_FocRot.h` - Complete pinmap with TMC2209 UART, LEDs
- `src/src/pinmaps/Models.h` - AUB_FOCROT model (25) added
- `platformio.ini` - Build configuration with DFU upload, USB CDC flags

### PHASE 2: Core Library Verification & Configuration [COMPLETE]
- [x] Compile with TMC2209 driver models enabled
- [x] Verify TMC2209 UART driver code compiles with HardSerial mode
- [x] Verify I2C NV storage (NV_2464) driver compiles
- [x] Assess memory footprint (Flash 93.3% + RAM 8.2%)

### PHASE 3: SHT31 Sensor Implementation [COMPLETE]
- [x] Implement SHT31 support using Adafruit SHT31 Library (for consistency with BME280)
- [x] Add SHT31/SHT31_0x45 as WEATHER options in Constants.h
- [x] Add WS_SHT31 to WeatherSensor enum in Weather.h
- [x] Implement init + poll in Weather.cpp

### PHASE 4: DFU Bootloader Command [COMPLETE]
- [x] Implement `:EDFU#` command handler in Telescope.command.cpp
- [x] Direct jump to ROM bootloader (HAL_bootloader_jump) with USB reset + HAL deinit
- [x] Tested and working on hardware

### PHASE 5: Hardware Testing & Verification [COMPLETE]
- [x] Serial communication via USB CDC (SerialUSB)
- [x] DFU bootloader entry via `:EDFU#` command
- [x] Status LED (PB12) operational
- [x] Activity LED (PB4) operational
- [x] I2C: SHT31 sensor reading (temperature + humidity confirmed)
- [x] TMC2209 UART communication (both drivers, addr 0 + addr 1)
- [x] Rotator movement (AXIS3) - CW/CCW verified, position tracking
- [x] Focuser movement (AXIS4) - inward/outward verified, position tracking
- [x] I2C: NV storage read/write - focuser position persists across reset
- [ ] Timing accuracy verification

### PHASE 6: Documentation & Optimization
- [ ] Create `STM32G0B1_PORTING.md`
- [ ] Performance tuning (timer latency, prescalers)
- [ ] Final code review

## Implementation Rules

### DO:
- Follow existing code patterns from other STM32 implementations
- Use ARM CMSIS abstractions where available
- Keep interrupt handlers minimal and fast
- Test incrementally after each phase
- Preserve all existing APIs and function signatures
- Document hardware-specific choices
- Use direct jump for DFU bootloader (USB reset + HAL_DeInit + remap system memory)

### DON'T:
- Modify core mount control algorithms
- Change telescope control command handling
- Remove existing peripheral abstractions
- Add unnecessary features to G0B1 config
- Use floating-point math in interrupt handlers
- Implement polling where interrupts are used elsewhere
- Change the public API of any library
- Use VERBOSE debug mode (overflows 128KB flash)

## File Structure Reference
```
Files created/modified:
├── src/src/HAL/stm32/STM32G0B1.h          [DONE] HAL: NV_2464, DFU direct jump, 64MHz M0+
├── src/src/HAL/HAL.h                       [DONE] G0B1 detection
├── src/src/Constants.h                     [DONE] AUB_FOCROT + SHT31 constants
├── src/src/pinmaps/Pins.AUB_FocRot.h      [DONE] Pinmap: TMC2209 UART, LEDs, SerialUSB
├── src/src/pinmaps/Models.h                [DONE] AUB_FOCROT model 25
├── src/src/libApp/weather/Weather.h        [DONE] WS_SHT31 enum added
├── src/src/libApp/weather/Weather.cpp      [DONE] SHT31 init + poll (Adafruit library)
├── src/src/telescope/Telescope.cpp         [DONE] Focuser/rotator activity LED task
├── src/src/telescope/Telescope.command.cpp [DONE] :EDFU# command handler
├── src/Config.h                            [DONE] TMC2209, SHT31, LEDs, DEBUG ON
├── src/Extended.config.h                   [DONE] DEBUG=ON, SERIAL_DEBUG=SerialUSB
├── src/OnStepX.ino                         [DONE] (no startup check needed - direct jump)
├── platformio.ini                          [DONE] btt_ebb42_v1_1, DFU, -Os, USB CDC
├── .claude/PINOUT.md                       [DONE] Hardware pin reference
├── tools/firmware_manager/server.py        [DONE] MCP server for DFU upload
├── tools/firmware_manager/requirements.txt [DONE] MCP dependencies (mcp, pyserial)
├── tools/firmware_manager/README.md        [DONE] MCP server documentation
└── STM32G0B1_PORTING.md                    [TODO] Final documentation
```

## Claude Code Workflow

### PlatformIO CLI
PlatformIO CLI is not on PATH. Use the full path:
```
C:\Users\marti\.platformio\penv\Scripts\pio.exe
```
Example build command:
```
"C:\Users\marti\.platformio\penv\Scripts\pio.exe" run -e nucleo_g0b1re -d "f:/Projekty/AstroUniBoard/FW - FocuserRotator"
```

## Build & Upload Instructions

### Building in VS Code
1. Open PlatformIO in the sidebar
2. Select `nucleo_g0b1re` environment
3. Build: `Ctrl+Alt+B`

### Uploading Firmware (MCP Server - Recommended)
Use the Firmware Manager MCP server for automated DFU upload:
1. MCP server location: `tools/firmware_manager/server.py`
2. Use `enter_dfu_and_upload(port, firmware_path)` for one-step upload:
   - Sends `:EDFU#` command to enter DFU bootloader
   - Waits for device to re-enumerate as DFU
   - Uploads firmware via dfu-util
3. Firmware binary path: `.pio/build/nucleo_g0b1re/firmware.bin`
4. Example: `enter_dfu_and_upload("COM6", "f:/Projekty/AstroUniBoard/FW - FocuserRotator/.pio/build/nucleo_g0b1re/firmware.bin")`

Available MCP tools:
- `list_ports()` - List available COM ports
- `enter_dfu(port)` - Send `:EDFU#` to enter DFU mode
- `upload_firmware(firmware_path)` - Upload .bin via dfu-util (device must be in DFU already)
- `enter_dfu_and_upload(port, firmware_path, wait_seconds=3)` - Complete workflow (recommended)

### Uploading Firmware (Manual)
1. Send `:EDFU#` command via serial terminal to enter DFU bootloader
   - Device disconnects from CDC and re-enumerates as STM32 DFU device
2. Upload: `Ctrl+Alt+U` (PlatformIO upload)
3. Device automatically resets and runs new firmware after upload
4. **Note:** "Error 74" at end of DFU upload is harmless (DFU leave status)
5. **First upload** (no firmware or firmware without `:EDFU#`): manually enter DFU via BOOT0 switch

### Build Configuration
- **Platform:** ststm32
- **Board:** btt_ebb42_v1_1 (STM32G0B1CBT6 compatible)
- **Framework:** Arduino
- **Upload Protocol:** DFU
- **Optimization:** `-Os -std=gnu++17`
- **USB CDC Flags:** `-DUSBCON -DUSBD_USE_CDC -DHAL_PCD_MODULE_ENABLED`
- **Serial Baud:** 115200
- **Libraries:** TMCStepper, Adafruit SHT31 Library

## Custom Commands
- `:EDFU#` - Jump to DFU bootloader for firmware upload (direct jump, USB re-enumeration)
- `:ERESET#` - Reset MCU (standard OnStepX command)
- `:ENVRESET#` - Wipe NV memory (cleared on next boot)

## Success Criteria
- [x] HAL layer for G0B1 implemented
- [x] Pinmap configured for AUB_FocRot
- [x] DFU bootloader direct jump (`:EDFU#`)
- [x] TMC2209 drivers configured in Config.h
- [x] NV storage set to NV_2464 (24AA64)
- [x] Code compiles without errors (93.3% flash)
- [x] USB CDC serial communication working
- [x] SHT31 sensor support implemented
- [x] Status LED (PB12) working
- [x] Activity LED (PB4) for focuser/rotator movement
- [x] I2C: SHT31 temperature/humidity sensor working
- [x] TMC2209 UART communication verified (both addr 0 + addr 1)
- [x] Focuser movements functional (AXIS4, position tracking + NV persistence)
- [x] Rotator movements functional (AXIS3, CW/CCW + position tracking)
- [ ] Full documentation complete

## References
- STM32G0B1CBT6 Datasheet (128KB Flash, 144KB SRAM, 48-pin LQFP)
- Existing implementations: STM32F407.h, STM32F446.h
- FilterWheel STM32G0B1 project (DFU bootloader reference): `C:\Users\marti\Documents\PlatformIO\Projects\251205-184509-genericSTM32F103CB`
- OnStep documentation: https://groups.io/g/onstep/wiki/home
- Pin assignment: `.claude/PINOUT.md`
