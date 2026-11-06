// Move guard (#75, #77): which host moves are allowed while a position is not trustworthy.
// Pure code without Arduino so it runs in the native unit tests (fw/test/test_move_guard).
#pragma once
#include <stdint.h>
#include "error_codes.h"

// Safe mode (watchdog reset, positions not loaded): only relative jogs up to these sizes until
// the axis is synced (:56 / :76). Focuser in steps, rotator in cdeg.
#define SAFE_JOG_FOC_STEPS  200
#define SAFE_JOG_ROT_CDEG   1000

struct MoveGuardIn {
    bool    safeMode;     // statusSafeMode()
    bool    suspect;      // the axis position is known to be untrustworthy (see focuser / rotator)
    bool    guardOn;      // #75 setting: refuse every move while suspect (rotator only; false for the focuser)
    bool    absolute;     // :51 / :71 (true) or :52 / :72 (false)
    int32_t delta;        // relative moves: the requested change
    int32_t jogLimit;     // SAFE_JOG_FOC_STEPS / SAFE_JOG_ROT_CDEG
};

// FW_OK or FW_ERR_POS_UNVERIFIED. Rules (PROTOCOL.md §4):
//  - safe mode and suspect: absolute moves refused, relative moves only up to +-jogLimit;
//  - otherwise guard on and suspect: every move refused;
//  - otherwise allowed.
FwError moveGuard(const MoveGuardIn& in);
