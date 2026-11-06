// TMC2209 drivers on the shared USART2 single-wire bus (PA2/PA3, 460800 baud), addresses
// focuser 0 / rotator 1 (#24), and StallGuard stall detection for both axes (#27).
// Owner: session A. FROZEN interface.
#pragma once
#include <stdint.h>
#include "error_codes.h"
#include "settings.h"

// Sense resistors on the board: 0.11 ohm (HW/astrouniboard.kicad_sch) -> about 1.77 A RMS full scale.
#define TMC_RSENSE_OHM   0.11f
#define TMC_MAX_RUN_MA   1500      // upper limit accepted by :48 (run and hold), conservative for board cooling

// Housekeeping warning bits reported by the drivers (PROTOCOL.md §3.3, same bits as OpenWheel)
#define TMC_WARN_OTPW   0x02   // overtemperature prewarning
#define TMC_WARN_RESET  0x04   // a driver was reset by a supply dip and re-initialised (latched)

struct StallStats {
    uint8_t threshold;         // SGTHRS, 0 = off
    int16_t last, min, max;    // SG_RESULT of the last move (0..510), -1 if none
};

bool tmcBegin();                                   // UART + write the config of both drivers from settings
bool tmcApply(AxisId axis);                        // write settings.drv[axis] to the driver (:48, :24, load)
bool tmcAnswering(AxisId axis);

// Main loop: StallGuard sampling and stall stop while an axis runs (sticky FW_ERR_MOTOR_STALL,
// focuser also gets FLAG_FOC_UNVERIFIED); driver health and temperature polling while idle;
// re-initialisation after a driver reset.
void tmcUpdate();

int16_t tmcTempClass(AxisId axis);                 // 0 below 120 C, else 120/143/150/157; -1 not answering
uint8_t tmcWarnings();                             // TMC_WARN_* bits
void    tmcClearLatched();                         // clears TMC_WARN_RESET (":34,0#")

void tmcStallStats(AxisId axis, StallStats& out);  // :41
FwError tmcSetStallThreshold(AxisId axis, uint8_t thr);   // :42 — updates settings.drv[axis] and the driver
