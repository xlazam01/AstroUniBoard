# STM32G0B1 Port - Task Tracking & TODO

## PHASE 1: Hardware Abstraction Layer [COMPLETE]

### Task 1.1: STM32G0B1 HAL Header
- [x] Create `src/src/HAL/stm32/STM32G0B1.h`
- [x] NV driver set to NV_2464 (24AA64 I2C EEPROM)
- [x] DFU bootloader magic packet support (end of 144KB SRAM)
- [x] I2C pin config delegated to pinmap (I2C_SDA_PIN/I2C_SCL_PIN)

### Task 1.2: HAL Detection & Constants
- [x] STM32G0B1xx detection in `src/src/HAL/HAL.h`
- [x] AUB_FOCROT constant (model 25) in `src/src/Constants.h`
- [x] AUB_FOCROT entry in `src/src/pinmaps/Models.h`

### Task 1.3: Pinmap Configuration
- [x] `src/src/pinmaps/Pins.AUB_FocRot.h` created
- [x] AXIS3 (Rotator): STEP=PB2, DIR=PB1, EN=PB0
- [x] AXIS4 (Focuser): STEP=PB9, DIR=PB8, EN=PB7
- [x] TMC2209 shared HW UART: TX=PA2, RX=PA3, 460800 baud
- [x] I2C: SDA=PA10, SCL=PA9
- [x] Status LED: PB12
- [x] Serial A: USB virtual COM

### Task 1.4: Build Configuration
- [x] `platformio.ini` configured (btt_ebb42_v1_1, DFU, -O2, gnu++17)
- [x] `src/Config.h` updated: AXIS3/AXIS4 = TMC2209, 16 microsteps
- [x] Secondary UARTs disabled (SERIAL_B/C/D/E = OFF)

---

## PHASE 2: Compilation & Library Verification [IN PROGRESS]

### Task 2.1: Compile with TMC2209 Enabled
- [ ] Clean build with TMC2209 driver models
- [ ] Verify HardSerial TMC UART mode compiles on STM32G0
- [ ] Verify NV_2464 I2C driver compiles
- [ ] Check Flash/RAM usage (512KB Flash / 144KB SRAM available)
- [ ] Fix any compilation errors

### Task 2.2: Verify Task Scheduler
- [ ] Review hardware timer compatibility on STM32G0
- [ ] Check HAL_HWTIMER files for G0B1 support

### Task 2.3: Verify Serial/Commands
- [ ] Confirm USB Serial works as SERIAL_A
- [ ] Check command buffer sizes

---

## PHASE 3: SHT31 Sensor Implementation [PENDING]

### Task 3.1: SHT31 Driver
- [ ] Implement SHT31 I2C driver in weather/temperature library
- [ ] Add SHT31 constant to Config.defaults.h / Constants.h
- [ ] Integrate with existing temperature reporting framework
- [ ] Add Config.h option for SHT31

---

## PHASE 4: DFU Bootloader Command [PENDING]

### Task 4.1: DFU Command Handler
- [ ] Implement `:BOOTDFU#` command in command processing
- [ ] Add startup check: call `HAL_bootloader_check()` and jump to system bootloader
- [ ] Test DFU mode activation via command
- [ ] Test firmware upload via `dfu-util`

---

## PHASE 5: Hardware Testing [PENDING]

### Task 5.1: Basic Communication
- [ ] USB serial communication at 9600 baud
- [ ] Command echo test
- [ ] `:GVN#` firmware version response

### Task 5.2: TMC2209 Communication
- [ ] UART register read/write to both TMC2209 chips
- [ ] Verify driver addresses (0 and 1)
- [ ] Microstepping configuration

### Task 5.3: Motor Control
- [ ] Rotator (AXIS3) movement commands
- [ ] Focuser (AXIS4) movement commands
- [ ] Speed and direction control
- [ ] Position limits

### Task 5.4: I2C Devices
- [ ] 24AA64 EEPROM read/write (NV storage)
- [ ] SHT31 sensor temperature/humidity reading

### Task 5.5: Timing Verification
- [ ] Step pulse timing with oscilloscope
- [ ] Microsecond precision check
- [ ] Jitter measurement

---

## PHASE 6: Documentation & Optimization [PENDING]

- [ ] Create `STM32G0B1_PORTING.md`
- [ ] Performance tuning
- [ ] Final code review

---

## Current Status
- **Overall Progress:** Phase 1 complete, Phase 2 starting
- **Build Status:** Needs rebuild after pinmap/config changes
- **Next Action:** Compile and fix any errors from TMC2209 + new pinmap changes
- **Hardware:** Custom PCB with STM32G0B1RE, 512KB Flash, 144KB SRAM

## Notes
- Memory is NOT constrained: 512KB Flash + 144KB SRAM is plenty
- OnStepX convention: AXIS3=Rotator, AXIS4=Focuser (hardcoded, cannot swap)
- AXIS1/AXIS2 (mount) are OFF — this is a focuser/rotator-only build
- SHT31 is a new feature not in upstream OnStepX
