// Focuser (#25, #26) on axisFoc: soft limits 0..max (never clamped), backlash take-up on a
// direction change, position persistence (Q15).
//
// Decisions:
//  - Backlash: the first move after boot takes up no backlash (last direction unknown). The
//    direction of a move counts as "last direction" only once its take-up (lead steps) has run in
//    full: a halt inside the take-up keeps the previous direction, so the next move repeats it (#77).
//  - Safe mode (#77): until :56 sync, absolute moves are refused and relative moves are limited to
//    +-SAFE_JOG_FOC_STEPS (FW_ERR_POS_UNVERIFIED, move_guard.h).
//  - rev 2.1: a move is refused with FW_ERR_MOTOR_DRIVER when +12 V is low (#74).
//  - Driver not answering when a move is requested: the config is re-applied once; if the driver
//    still does not answer, the move is refused with FW_ERR_MOTOR_DRIVER (also raised sticky),
//    so the position never counts steps that a dead driver did not make.
//  - The position is saved POSITION_SAVE_DELAY_MS after every stop, also after a stall (best
//    estimate); "unverified" is saved with it, so it survives a reboot until :56 sync.
//  - The saved position carries the microstep setting it is counted in. A :48 microstep change
//    that was not saved with :23# is undone at boot: the position is scaled back to the loaded setting.
#include <Arduino.h>
#include "focuser.h"
#include "axis.h"
#include "config.h"
#include "housekeeping.h"
#include "move_guard.h"
#include "nv_store.h"
#include "settings.h"
#include "status.h"
#include "tmc2209.h"

static bool     unverified = true;
static int8_t   lastDir = 0;          // +1 / -1 direction of the last move, 0 unknown
static int8_t   moveDir = 0;          // direction of the running move, 0 none
static bool     savePending = false;
static uint32_t idleSince = 0;

void focuserBegin() {
    SavedPositions sp;
    nvLoadPositions(sp);
    const uint16_t now = settings.drv[AXIS_FOC].microsteps;
    int32_t p = sp.focPos;
    if (sp.focUsteps && now && sp.focUsteps != now)
        p = (int32_t)((int64_t)p * now / sp.focUsteps);
    axisFoc.setPosition(p);
    unverified = !sp.focValid;
    lastDir = 0;
    moveDir = 0;
    savePending = false;
    focuserApplySettings();
}

void focuserUpdate() {
    const uint32_t now = millis();
    if (axisFoc.justStopped()) {
        if (moveDir && axisFoc.leadLeft() == 0) lastDir = moveDir;   // take-up done
        moveDir = 0;
        savePending = true;
        idleSince = now;
    }
    if (savePending && !axisFoc.busy() && now - idleSince >= POSITION_SAVE_DELAY_MS) {
        savePending = false;
        nvSavePosition(AXIS_FOC, axisFoc.position(), !unverified);
    }
}

static FwError moveTo(int32_t target, bool absolute) {
    if (target < 0 || target > settings.foc.maxPos) return FW_ERR_BAD_PARAM;
    if (statusAnyBusy() || axisAnyBusy()) return FW_ERR_BUSY;
    const int32_t pos = axisFoc.position();
    const FwError g = moveGuard(MoveGuardIn{statusSafeMode(), unverified, false, absolute, target - pos,
                                            SAFE_JOG_FOC_STEPS});
    if (g != FW_OK) return g;
    if (target == pos) return FW_OK;

    if ((!tmcAnswering(AXIS_FOC) && !tmcApply(AXIS_FOC)) || !hkMotorSupplyOk()) {
        statusRaise(FW_ERR_MOTOR_DRIVER);
        return FW_ERR_MOTOR_DRIVER;
    }

    const int8_t dir = target > pos ? 1 : -1;
    const uint32_t lead = (lastDir != 0 && dir != lastDir) ? settings.foc.backlash : 0;
    nvMarkMoving(AXIS_FOC);
    if (!axisFoc.moveTo(target, lead)) return FW_ERR_BUSY;
    moveDir = dir;
    savePending = false;
    return FW_OK;
}

FwError focuserMoveTo(int32_t target) { return moveTo(target, true); }

FwError focuserMoveBy(int32_t delta) {
    const int64_t t = (int64_t)axisFoc.position() + delta;
    if (t < 0 || t > (int64_t)settings.foc.maxPos) return FW_ERR_BAD_PARAM;
    return moveTo((int32_t)t, false);
}

void focuserHalt() { axisFoc.halt(); }

FwError focuserSync(int32_t pos) {
    if (pos < 0 || pos > settings.foc.maxPos) return FW_ERR_BAD_PARAM;
    if (axisFoc.busy()) return FW_ERR_BUSY;
    axisFoc.setPosition(pos);
    unverified = false;
    savePending = false;
    nvSavePosition(AXIS_FOC, pos);
    return FW_OK;
}

int32_t focuserPosition() { return axisFoc.position(); }
int32_t focuserTarget() { return axisFoc.busy() ? axisFoc.target() : -1; }
bool focuserBusy() { return axisFoc.busy(); }
bool focuserHalting() { return axisFoc.halting(); }
bool focuserUnverified() { return unverified; }
void focuserSetUnverified() { unverified = true; }

void focuserApplySettings() {
    axisFoc.setSpeed(settings.foc.speed);
    axisFoc.setAccel(settings.foc.accel);
    axisFoc.setReverse(settings.foc.reverse != 0);
    axisFoc.setIdleOff(settings.drv[AXIS_FOC].idleOffS);
}

static int64_t scale(int64_t v, uint16_t o, uint16_t n) { return v * n / o; }

static int64_t clamp64(int64_t v, int64_t lo, int64_t hi) { return v < lo ? lo : v > hi ? hi : v; }

void focuserRescale(uint16_t oldUsteps, uint16_t newUsteps) {
    if (!oldUsteps || !newUsteps || oldUsteps == newUsteps || axisFoc.busy()) return;
    FocuserSettings& f = settings.foc;
    f.maxPos = (int32_t)clamp64(scale(f.maxPos, oldUsteps, newUsteps), 1, 10000000);
    f.speed = (uint32_t)clamp64(scale(f.speed, oldUsteps, newUsteps), 1, 20000);
    f.accel = (uint32_t)clamp64(scale(f.accel, oldUsteps, newUsteps), 1, 100000);
    f.backlash = (uint32_t)clamp64(scale(f.backlash, oldUsteps, newUsteps), 0, 10000);
    const int32_t p = (int32_t)clamp64(scale(axisFoc.position(), oldUsteps, newUsteps), 0, f.maxPos);
    axisFoc.setPosition(p);
    focuserApplySettings();
    // Save on the next loop pass, after the caller (:48 / :24) has stored the new microstep value:
    // the record carries it, so a reboot without :23# scales the position back (focuserBegin).
    savePending = true;
    idleSince = millis() - POSITION_SAVE_DELAY_MS;
}
