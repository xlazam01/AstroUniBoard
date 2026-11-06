// Hang diagnostics: independent watchdog (4 s) + a progress marker in a TAMP backup register.
// After a watchdog reset the firmware starts in safe mode (no sensors) so USB and :31# DFU stay
// reachable, and :99# reports where the previous run stopped.
#pragma once
#include <stdint.h>

enum DiagMark : uint8_t {
    DIAG_NONE = 0,
    DIAG_SETUP = 1,
    DIAG_WIRE_BEGIN = 2,
    DIAG_ENV_BEGIN = 3,
    DIAG_PROTOCOL = 10,
    DIAG_ENV_UPDATE = 11,
    DIAG_ENV_START0 = 12,   // + sensor index: trigger measurement
    DIAG_ENV_READ0 = 14     // + sensor index: read result
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
