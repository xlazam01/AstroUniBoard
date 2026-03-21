// Platform setup ------------------------------------------------------------------------------------
#pragma once

// We define a more generic symbol, in case more STM32 boards based on different lines are supported
#define __ARM_STM32__

// AXIS1/2 (mount) are OFF on this board, so all hardware timers are free.
// Allow AXIS3/4 (focuser/rotator) to use them for jitter-free step generation.
#define AXIS_ALLOW_ALL_HWTIMERS

// Base rate for critical task timing (0.0038s = 0.06", 0.2 sec/day)
// G0B1 runs at 64 MHz (Cortex-M0+)
#define HAL_FRACTIONAL_SEC 263.1578947F

// Analog read and write
#ifndef HAL_VCC
  #define HAL_VCC 3.3F
#endif
#ifndef ANALOG_READ_RANGE
  #define ANALOG_READ_RANGE 1023
#else
  #error "Configuration (Config.h): ANALOG_READ_RANGE can't be changed on this platform"
#endif
#ifndef ANALOG_WRITE_RANGE
  #define ANALOG_WRITE_RANGE 255
#endif

// STM32 Arduino core (G0 is Cortex-M0+)
#define HAL_HAS_GLOBAL_PWM_RESOLUTION 1
#define HAL_HAS_GLOBAL_ADC_RESOLUTION 1

#define HAL_PWM_BITS_MAX 16

// G0B1 ADC is 12-bit
#define HAL_ADC_BITS_MAX 12

// Lower limit (fastest) step rate in uS for this platform (in SQW mode) and width of step pulse
// G0B1 is Cortex-M0+ at 64 MHz - conservative for M0+ (no branch prediction, single-issue)
#define HAL_FAST_PROCESSOR
#define HAL_MAXRATE_LOWER_LIMIT 32
#define HAL_PULSE_WIDTH 500          // in ns

#include <HardwareTimer.h>

// Interrupts
#define cli() noInterrupts()
#define sei() interrupts()

// New symbol for the default I2C port ---------------------------------------------------------------
#include <Wire.h>
#ifndef HAL_WIRE
  #define HAL_WIRE Wire
#endif
// no HAL_WIRE_CLOCK, setClock() can cause issues on STM32G0 core

// Non-volatile storage ------------------------------------------------------------------------------
#if NV_DRIVER == NV_DEFAULT
  #undef NV_DRIVER
  #define NV_DRIVER NV_2464          // 24AA64 8KB I2C EEPROM at address 0x50
#endif

//--------------------------------------------------------------------------------------------------
// Internal MCU temperature (in degrees C)
#define HAL_TEMP() ( NAN )

//--------------------------------------------------------------------------------------------------
// General purpose initialize for HAL
// I2C pins are configured via I2C_SDA_PIN / I2C_SCL_PIN in the pinmap (handled by WIRE_INIT)
#define HAL_INIT() { \
}

//---------------------------------------------------------------------------------------------------
// Misc. includes to support this processor's operation

// always bring in the software serial library early as strange things happen otherwise
#include <SoftwareSerial.h>

// MCU reset
#define HAL_RESET() NVIC_SystemReset()

// DFU Bootloader support ---------------------------------------------------------------------------
// Direct jump to STM32G0 ROM system bootloader (includes USB DFU)
// Based on proven implementation from FilterWheel STM32G0B1 project
typedef void (*pFunction)(void);

inline void HAL_bootloader_jump() {
  // 1. Disable all interrupts
  __disable_irq();

  // 2. Disable SysTick
  SysTick->CTRL = 0;
  SysTick->LOAD = 0;
  SysTick->VAL  = 0;

  // 3. Force reset USB so host detects the bootloader as a new device
  #ifdef __HAL_RCC_USB_FORCE_RESET
    __HAL_RCC_USB_FORCE_RESET();
    __HAL_RCC_USB_RELEASE_RESET();
  #endif

  // 4. DeInit HAL to reset clocks to HSI (critical for bootloader)
  HAL_DeInit();
  HAL_RCC_DeInit();

  // 5. Clear all NVIC interrupts (Cortex-M0+ has 1 register)
  NVIC->ICER[0] = 0xFFFFFFFF;
  NVIC->ICPR[0] = 0xFFFFFFFF;

  // 6. Memory barrier
  __DSB();
  __ISB();

  // 7. Remap system memory to 0x00000000
  __HAL_RCC_SYSCFG_CLK_ENABLE();
  SYSCFG->CFGR1 &= ~(SYSCFG_CFGR1_MEM_MODE);
  SYSCFG->CFGR1 |= (0x01 << SYSCFG_CFGR1_MEM_MODE_Pos);

  // 8. Set MSP and jump to system bootloader
  const uint32_t sysBootAddr = 0x1FFF0000;
  __set_MSP(*(__IO uint32_t *)sysBootAddr);
  ((pFunction)(*(__IO uint32_t *)(sysBootAddr + 4)))();

  while (1);
}
