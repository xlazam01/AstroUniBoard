// Hang diagnostics: independent watchdog (4 s) + a progress marker in a TAMP backup register.
// After a watchdog reset the firmware starts in safe mode (no sensors) so USB and :31# DFU stay
// reachable, and :46# reports where the previous run stopped.
#pragma once
#include <stdint.h>

enum DiagMark : uint8_t {
    DIAG_NONE = 0,
    DIAG_SETUP = 1,
    DIAG_WIRE_BEGIN = 2,
    DIAG_ENV_BEGIN = 3,
    DIAG_NV_BEGIN = 4,
    DIAG_IMU_BEGIN = 5,
    DIAG_TMC_BEGIN = 6,
    DIAG_AXES_BEGIN = 7,
    DIAG_PROTOCOL = 10,
    DIAG_ENV_UPDATE = 11,
    DIAG_ENV_START0 = 12,   // + sensor index: trigger measurement
    DIAG_ENV_READ0 = 14,    // + sensor index: read result
    DIAG_ENV_PROBE = 16,    // sensor family probe (SHT4x serial number read)
    DIAG_FOC_UPDATE = 20,
    DIAG_ROT_UPDATE = 21,
    DIAG_TMC_UPDATE = 22,
    DIAG_IMU_UPDATE = 23,
    DIAG_NV_UPDATE = 24,
    DIAG_HK_UPDATE = 25,
    DIAG_LED_UPDATE = 26
};

void     diagBoot();            // first call in setup()
void     diagFeed();            // every loop pass
void     diagMark(DiagMark m);
bool     diagWatchdogReset();   // the last reset came from the watchdog
uint32_t diagResetFlags();      // RCC_CSR of the last reset (bits 24..31)
uint8_t  diagLastMark();        // marker the previous run left behind
void     diagNote(uint32_t v);  // free value kept across a reset (TAMP BKP2R)
void     diagNoteOr(uint32_t v);
uint32_t diagLastNote();        // note the previous run left behind
