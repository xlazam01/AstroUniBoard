// Housekeeping (#18, PROTOCOL.md §3.3): MCU die temperature and VDDA from the internal ADC channels
// (factory calibration), driver temperature classes from tmc2209.h, warning bits.
// Rev 2.0 has no voltage-sense divider (Q24): v5 and v5_min are always -1.
// Owner: session D. FROZEN interface.
#pragma once
#include <stdint.h>

#define HK_WARN_V5_LOW      0x01   // never set on rev 2.0
#define HK_WARN_DRV_OTPW    0x02
#define HK_WARN_DRV_RESET   0x04
#define HK_WARN_NO_CRYSTAL  0x08

void     hkBegin();
void     hkUpdate();                // main loop; samples while no axis moves
int16_t  hkMcuTempDeciC();
uint16_t hkVddaMv();
uint8_t  hkWarnings();              // HK_WARN_* (includes tmcWarnings() and the crystal state)
