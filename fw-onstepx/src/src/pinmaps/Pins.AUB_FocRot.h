// -------------------------------------------------------------------------------------------------
// Pin map for AstroUniBoard Focuser/Rotator on STM32G0B1CBT6
#pragma once

// Custom PCB with STM32G0B1CBT6 (48-pin LQFP)
// 128 KB Flash, 144 KB SRAM, ARM Cortex-M0+ @ 64 MHz
// Two TMC2209 drivers on shared UART2, 24AA64 I2C EEPROM, SHT31 I2C sensor
// SHT31 supply switching is configured separately via SHT31_POWER_PIN in Config.h
//
// OnStepX axis convention: AXIS3 = Rotator, AXIS4 = Focuser

#if defined(STM32G0B1xx)

// Serial ports ------------------------------------------------------------------------------------
// SerialUSB: USB CDC virtual COM port (primary command channel)
#if SERIAL_A_BAUD_DEFAULT != OFF
  #define SERIAL_A              SerialUSB
#endif

// I2C bus (shared) --------------------------------------------------------------------------------
// PA9  = SCL, PA10 = SDA  (Wire / HAL_WIRE)
// Devices on bus:
//   0x45 - on-board SHT31 (U8), rev 2.0 (ADDR tied to its PB9 supply; 0x44 on rev 1);
//          powered via SHT31_POWER_PIN = PB9, answers only while powered
//   0x44 - external SHT31 on J9 (rev 2.0 only)
//   0x50 - 24AA64T EEPROM (NV storage); SOT-23 package has no address pins
//   0x68 - MPU6000 accelerometer (AXIS3 rotator IMU homing)
//            Chip is mounted PCB-side up, connector side down (flipped vs. gravity).
//            Effective gravity vector lands on +Z axis of the chip.
//            angle() = atan2(ax, az)  → 0° when rotator is at mechanical home.
//            Home angle stored in NV at NV_ROTATOR_IMU_HOME (addr 1056, float).
//            Enable via AXIS3_SENSE_HOME MPU6000 in Config.h.
//            Auto-home on power-up via AXIS3_HOME_AUTO ON in Config.h.
//            Homing commands:
//              :rC#   - go to stored home position
//              :rHA#  - query current IMU angle (degrees, 1 decimal)
//              :rHS#  - save current orientation as home (10-sample average)
//              :rHG#  - save gravity reference (0.0°) as home
#define I2C_SDA_PIN             PA10
#define I2C_SCL_PIN             PA9

// TMC2209 UART (shared hardware UART for both drivers) -------------------------------------------
#if defined(STEP_DIR_TMC_UART_PRESENT) || defined(SERVO_TMC2209_PRESENT)
  #define DRIVER_TMC_STEPPER                       // Use TMCStepper library (not legacy)
  #define SERIAL_TMC_HARDWARE_UART
  #define SERIAL_TMC              HardSerial       // Create HardwareSerial from RX/TX pins
  #define SERIAL_TMC_BAUD         460800
  #define SERIAL_TMC_RX           PA3              // USART2 RX
  #define SERIAL_TMC_TX           PA2              // USART2 TX
  // Map axis index to TMC2209 hardware address (hardwired via MS1/MS2)
  // x = axisNumber - 1: AXIS3 -> x=2 -> addr 1 (Rotator: MS1=3V3, MS2=GND)
  //                      AXIS4 -> x=3 -> addr 0 (Focuser: MS1=GND, MS2=GND)
  #define SERIAL_TMC_ADDRESS_MAP(x) ((x) >= 2 ? 3 - (x) : 0)
#endif

#if PINMAP == AUB_FOCROT

// AXIS3 - Rotator (TMC2209) ----------------------------------------------------------------------
#define AXIS3_STEP_PIN          PB15
#define AXIS3_DIR_PIN           PB14
#define AXIS3_ENABLE_PIN        PB13
#define AXIS3_M0_PIN            OFF              // MS1 hardwired HIGH (TMC UART addr 1)
#define AXIS3_M1_PIN            OFF              // MS2 hardwired LOW  (TMC UART addr 1)

// AXIS4 - Focuser (TMC2209) ----------------------------------------------------------------------
#define AXIS4_STEP_PIN          PB2
#define AXIS4_DIR_PIN           PB1
#define AXIS4_ENABLE_PIN        PB0
#define AXIS4_M0_PIN            OFF              // MS1 hardwired LOW  (TMC UART addr 0)
#define AXIS4_M1_PIN            OFF              // MS2 hardwired LOW  (TMC UART addr 0)

// Status LEDs -------------------------------------------------------------------------------------
#define STATUS_LED_PIN          PB12
#define MOUNT_LED_PIN           PB4

// AUX pins (active but optional) ------------------------------------------------------------------
#define AUX1_PIN                PC13
#define AUX2_PIN                PC14

// Reserved/unused pins (active but optional) ------------------------------------------------------
// AXIS3 DIAG:    PA7
// LIMIT:         PA1
// BUTTON LEFT:   PA14
// BUTTON RIGHT:  PB6
// UART6 TX:      PA4
// UART6 RX:      PA5

#endif // PINMAP == AUB_FOCROT

#endif // STM32G0B1xx
