// Device status (PROTOCOL.md §3.2): the state and flags reported by ":33#", plus the sticky error.
// Owner: orchestrator. Assembled from the module interfaces; no module writes the struct directly.
// FROZEN interface (fw V2 first version) — change only together with every user.
#pragma once
#include <stdint.h>
#include "error_codes.h"

enum DevState : uint8_t {
    DEV_INIT        = 0,
    DEV_IDLE        = 1,
    DEV_FOC_MOVING  = 2,
    DEV_ROT_MOVING  = 3,
    DEV_ROT_HOMING  = 4,   // rotator IMU homing (:77) or IMU zero calibration (:95)
    DEV_HALTING     = 5    // an axis decelerates after :30 / :53 / :73
};

enum StatusFlag : uint16_t {
    FLAG_FOC_UNVERIFIED = 1u << 0,   // focuser position unverified (power loss mid-move, never saved, stall)
    FLAG_ROT_UNVERIFIED = 1u << 1,   // rotator angle unverified (not homed since boot)
    FLAG_EEPROM_FAULT   = 1u << 2,   // EEPROM missing or bad CRC: defaults in use
    FLAG_HK_WARNING     = 1u << 3,   // housekeeping warning, details in :40#
    FLAG_SHT_ONBOARD    = 1u << 4,   // on-board SHT31 has a valid reading
    FLAG_SHT_EXTERNAL   = 1u << 5,   // external SHT31 (J9) has a valid reading
    FLAG_IMU_OK         = 1u << 6,   // IMU answering
    FLAG_IMU_UNCAL      = 1u << 7,   // IMU zero not calibrated
    FLAG_SAFE_MODE      = 1u << 8,   // started after a watchdog reset with sensors disabled
    FLAG_ROT_SUSPECT    = 1u << 9    // rotator angle known untrustworthy (#75): moves refused while the :98 guard is on
};

struct DeviceStatus {
    DevState state;
    int32_t  focPos;        // steps
    int32_t  focTarget;     // steps, -1 when the focuser is not moving
    int32_t  rotCdeg;       // mechanical angle reported 0..35999
    int32_t  rotTarget;     // 0..35999, -1 when the rotator is not moving
    uint8_t  error;         // sticky error (FwError), 0 none
    uint16_t flags;         // StatusFlag bits
};

void statusBegin(bool safeMode);     // once in setup(), before any module runs
void statusSetReady();               // end of setup(): state leaves DEV_INIT
DeviceStatus statusGet();            // one consistent snapshot for :33#

// Sticky faults (PROTOCOL.md §6). Raising one does not stop motion by itself: the module that
// detects the fault stops its axis first, then calls statusRaise().
void    statusRaise(FwError code);   // keeps the first code until cleared
FwError statusError();
void    statusClearError();          // ":34,0#" — also clears housekeeping latches (hkClearLatched)

bool statusSafeMode();
bool statusAnyBusy();                // any axis moving, halting, homing or calibrating (Q12)
