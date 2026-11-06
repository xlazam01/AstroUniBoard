// Rotator (#29, #30, #32 sequencing, PROTOCOL.md §4.2) on axisRot.
//
// Angle bookkeeping: while idle the exact signed angle in cdeg (angleIdle) is the truth and the axis
// step position is derived from it (rotCdegToSteps). A move targets the step position of an absolute
// cdeg target, so rounding never accumulates; when the move ends at that step position the angle
// becomes the exact target, otherwise (halt, stall) it is derived from the steps. Changing
// steps/degree or microsteps keeps the angle and recomputes the step position.
//
// IMU home (:77) and IMU zero calibration (:95) are state machines run from rotatorUpdate(); they
// never block the loop. Timeouts and fault handling are documented at the constants below.
//
// "Suspect" angle (#75, status flag 9): the saved angle is known to be untrustworthy - a power loss
// cut a move, a move ended early without a halt (stall, fault), nothing was ever saved, or safe mode
// (positions not loaded). It is saved with the angle, so it survives reboots, until :76 sync or a
// good IMU home. Flag 1 ("not homed since boot") is wider and is set at every boot. While suspect and
// settings.rotGuard is on, :71 / :72 / :95 are refused with FW_ERR_POS_UNVERIFIED; in safe mode only
// jogs up to SAFE_JOG_ROT_CDEG are allowed (#77, move_guard.h). IMU home stays allowed: it clears it.
//
// Every move that needs motion first checks the driver as the focuser does (#74): not answering
// after one re-apply (or +12 V low on rev 2.1) -> sticky FW_ERR_MOTOR_DRIVER, no move. This covers
// IMU home and calibration moves (the sequence then aborts; the sticky code stays the driver error).
#include <Arduino.h>

#include "rotator.h"
#include "axis.h"
#include "config.h"
#include "housekeeping.h"
#include "imu_mpu6000.h"
#include "move_guard.h"
#include "nv_store.h"
#include "rotator_calc.h"
#include "rotator_wrap.h"
#include "settings.h"
#include "status.h"
#include "tmc2209.h"

// IMU home: at most this many moves (passes) before FW_ERR_HOME_TIMEOUT
#define HOME_MAX_PASSES          3
// After imuRestartFilter() the IMU must report imuSettled() within this time, else the sequence
// aborts (home: FW_ERR_HOME_TIMEOUT, calibration: FW_ERR_CALIB_FAILED). An IMU that stops answering
// aborts the same way at once.
#define IMU_SETTLE_TIMEOUT_MS    5000
// Boot home (Q20) starts this long after rotatorBegin(), so the IMU filter has samples
#define BOOT_HOME_DELAY_MS       3000
// :95 captures gravity at the start angle, +90 deg and +180 deg (imu_math.h), then returns
#define CAL_STEP_CDEG            9000

enum Seq : uint8_t {
    SEQ_NONE = 0,
    SEQ_HOME_SETTLE,     // waiting for imuSettled(), then read the roll and move (or finish)
    SEQ_HOME_MOVE,       // waiting for the homing move to end
    SEQ_CAL_SETTLE0,     // settle at the start orientation, capture g0, move +90 deg
    SEQ_CAL_MOVE90,
    SEQ_CAL_SETTLE90,    // settle at +90 deg, capture g90, move to +180 deg
    SEQ_CAL_MOVE180,
    SEQ_CAL_SETTLE180,   // settle at +180 deg, capture g180, move back
    SEQ_CAL_MOVEBACK     // back at the start: imuCalFinish()
};

static int32_t  angleIdle = 0;         // exact signed angle while no move runs
static bool     moving = false;        // a move started here is running
static int32_t  moveTarget = 0;        // signed cdeg target of that move
static bool     haltRequested = false; // the running move was stopped on purpose (position exact)
static bool     lastMoveComplete = true;
static bool     unverified = true;     // status flag 1 (Q20): set at boot until homed / synced
static bool     suspect = true;        // status flag 9 (#75): saved angle known to be untrustworthy
static bool     savePending = false;
static uint32_t idleSinceMs = 0;
static Seq      seq = SEQ_NONE;
static uint32_t seqMs = 0;
static uint8_t  homeMoves = 0;
static int32_t  calStart = 0;
static bool     bootHomePending = false;
static uint32_t bootMs = 0;

static uint32_t spk() { return settings.rot.stepsPer1000Deg; }

static void scheduleSave() {
    savePending = true;
    idleSinceMs = millis();
}

// Driver answering (one re-apply) and, on rev 2.1, +12 V present. Raises the sticky error on failure.
static bool driverReady() {
    if ((!tmcAnswering(AXIS_ROT) && !tmcApply(AXIS_ROT)) || !hkMotorSupplyOk()) {
        statusRaise(FW_ERR_MOTOR_DRIVER);
        return false;
    }
    return true;
}

// Starts a move to an absolute signed angle (range already checked). FW_ERR_BUSY: the axis refused
// (another axis busy); FW_ERR_MOTOR_DRIVER: driver check failed (sticky raised). A target within the
// same microstep finishes at once without motion.
static FwError startMove(int32_t target) {
    const int32_t steps = rotCdegToSteps(target, spk());
    if (axisRot.busy()) return FW_ERR_BUSY;
    if (steps == axisRot.position()) {
        if (target != angleIdle) {
            angleIdle = target;
            scheduleSave();
        }
        lastMoveComplete = true;
        return FW_OK;
    }
    if (!driverReady()) return FW_ERR_MOTOR_DRIVER;
    nvMarkMoving(AXIS_ROT);
    if (!axisRot.moveTo(steps)) return FW_ERR_BUSY;
    moving = true;
    moveTarget = target;
    haltRequested = false;
    savePending = false;
    return FW_OK;
}

static FwError guard(bool absolute, int32_t delta) {
    return moveGuard(MoveGuardIn{statusSafeMode(), suspect, settings.rotGuard != 0, absolute, delta,
                                 SAFE_JOG_ROT_CDEG});
}

// End-of-move bookkeeping, every loop pass.
static void checkMoveEnd() {
    if (!moving || axisRot.busy()) return;
    moving = false;
    const int32_t pos = axisRot.position();
    lastMoveComplete = (pos == rotCdegToSteps(moveTarget, spk()));
    if (lastMoveComplete) {
        angleIdle = moveTarget;
    } else {
        angleIdle = rotStepsToCdeg(pos, spk());
        // stopped early without a halt (StallGuard, driver fault): the angle can't be trusted
        if (!haltRequested) unverified = suspect = true;
    }
    haltRequested = false;
    scheduleSave();
}

static void setZeroHere() {
    angleIdle = 0;
    axisRot.setPosition(0);
    unverified = suspect = false;
    scheduleSave();
}

static void abortSeq(FwError err) {
    seq = SEQ_NONE;
    if (axisRot.busy()) {   // a sticky fault stops the axis first
        haltRequested = true;
        axisRot.stopNow();
    }
    statusRaise(err);
}

static FwError seqError() {
    return (seq == SEQ_HOME_SETTLE || seq == SEQ_HOME_MOVE) ? FW_ERR_HOME_TIMEOUT : FW_ERR_CALIB_FAILED;
}

// Common settle step: true once the IMU is settled; aborts on no answer / timeout.
static bool settled() {
    if (!imuAnswering() || (!imuSettled() && millis() - seqMs > IMU_SETTLE_TIMEOUT_MS)) {
        abortSeq(seqError());
        return false;
    }
    return imuSettled();
}

static void beginSettle(Seq next) {
    imuRestartFilter();
    seqMs = millis();
    seq = next;
}

static void homeStep() {
    const int32_t roll = imuRollCdeg();
    if (roll == IMU_INVALID) { abortSeq(FW_ERR_HOME_TIMEOUT); return; }
    const int32_t tol = settings.rot.homeTolCdeg;
    int32_t target;
    if (homeMoves == 0) {
        // first pass: the IMU zero candidate nearest 0 inside the wrap range (unwinds the cable)
        if (!rotPlanHome(angleIdle, roll, settings.rot.wrapMin, settings.rot.wrapMax, &target)) {
            abortSeq(FW_ERR_HOME_TIMEOUT);
            return;
        }
        const int32_t d = target - angleIdle;
        if (d >= -tol && d <= tol) { seq = SEQ_NONE; setZeroHere(); return; }
    } else {
        if (roll >= -tol && roll <= tol) { seq = SEQ_NONE; setZeroHere(); return; }
        if (homeMoves >= HOME_MAX_PASSES) { abortSeq(FW_ERR_HOME_TIMEOUT); return; }
        target = rotPlanCorrection(angleIdle, roll, settings.rot.wrapMin, settings.rot.wrapMax);
        if (target == angleIdle) { abortSeq(FW_ERR_HOME_TIMEOUT); return; }   // clamped at a wrap limit
    }
    if (startMove(target) != FW_OK) { abortSeq(FW_ERR_HOME_TIMEOUT); return; }
    homeMoves++;
    seq = SEQ_HOME_MOVE;
}

static void seqUpdate() {
    switch (seq) {
        case SEQ_NONE:
            break;

        case SEQ_HOME_SETTLE:
            if (settled()) homeStep();
            break;

        case SEQ_CAL_SETTLE0:
            if (!settled()) break;
            if (!imuCalCaptureG0() || startMove(calStart + CAL_STEP_CDEG) != FW_OK) { abortSeq(FW_ERR_CALIB_FAILED); break; }
            seq = SEQ_CAL_MOVE90;
            break;

        case SEQ_CAL_SETTLE90:
            if (!settled()) break;
            if (!imuCalCaptureG90() || startMove(calStart + 2 * CAL_STEP_CDEG) != FW_OK) { abortSeq(FW_ERR_CALIB_FAILED); break; }
            seq = SEQ_CAL_MOVE180;
            break;

        case SEQ_CAL_SETTLE180:
            if (!settled()) break;
            if (!imuCalCaptureG180() || startMove(calStart) != FW_OK) { abortSeq(FW_ERR_CALIB_FAILED); break; }
            seq = SEQ_CAL_MOVEBACK;
            break;

        case SEQ_HOME_MOVE:
        case SEQ_CAL_MOVE90:
        case SEQ_CAL_MOVE180:
        case SEQ_CAL_MOVEBACK: {
            if (moving) break;
            if (!lastMoveComplete) { abortSeq(seqError()); break; }   // stalled / stopped by a fault
            if (seq == SEQ_HOME_MOVE) { beginSettle(SEQ_HOME_SETTLE); break; }
            if (seq == SEQ_CAL_MOVE90) { beginSettle(SEQ_CAL_SETTLE90); break; }
            if (seq == SEQ_CAL_MOVE180) { beginSettle(SEQ_CAL_SETTLE180); break; }
            seq = SEQ_NONE;
            const FwError err = imuCalFinish();
            // FW_ERR_EEPROM: the new calibration is in use (only not saved), so zero the angle too,
            // or :70 and the IMU roll would refer to different zeros until the next reset.
            if (err == FW_OK || err == FW_ERR_EEPROM) setZeroHere();
            if (err != FW_OK) statusRaise(err == FW_ERR_IMU_VERTICAL ? FW_ERR_CALIB_FAILED : err);
            break;
        }
    }
}

// ---------------------------------------------------------------------------------------------

void rotatorBegin() {
    SavedPositions sp;
    nvLoadPositions(sp);
    angleIdle = sp.rotCdeg;          // last saved value, also when a move was cut by a power loss
    unverified = true;               // Q20: flag 1 until homed or synced
    suspect = !sp.rotValid;          // #75: cut by a power loss, saved as suspect, never saved, safe mode
    moving = false;
    seq = SEQ_NONE;
    savePending = false;
    rotatorApplySettings();          // speed/accel/reverse/idle-off + step position from the angle
    bootHomePending = settings.rot.bootHome != 0;
    bootMs = millis();
}

void rotatorUpdate() {
    checkMoveEnd();
    seqUpdate();

    if (bootHomePending && millis() - bootMs >= BOOT_HOME_DELAY_MS) {
        bootHomePending = false;
        // only when calibrated, answering and not near vertical (rotatorImuHome checks all of it);
        // a host command that is already running wins (busy -> skipped)
        (void)rotatorImuHome();
    }

    if (savePending && !rotatorBusy() && millis() - idleSinceMs >= POSITION_SAVE_DELAY_MS) {
        savePending = false;
        nvSavePosition(AXIS_ROT, angleIdle, !suspect);
    }
}

FwError rotatorMoveTo(int32_t target0to35999) {
    if (target0to35999 < 0 || target0to35999 > 35999) return FW_ERR_BAD_PARAM;
    if (statusAnyBusy()) return FW_ERR_BUSY;
    const FwError g = guard(true, 0);
    if (g != FW_OK) return g;
    int32_t t;
    if (!wrapPlanTarget(angleIdle, target0to35999, settings.rot.wrapMin, settings.rot.wrapMax, &t))
        return FW_ERR_BAD_PARAM;
    return startMove(t);
}

FwError rotatorMoveBy(int32_t deltaCdeg) {
    if (statusAnyBusy()) return FW_ERR_BUSY;
    const FwError g = guard(false, deltaCdeg);
    if (g != FW_OK) return g;
    const int64_t t = (int64_t)angleIdle + deltaCdeg;
    if (t < settings.rot.wrapMin || t > settings.rot.wrapMax) return FW_ERR_BAD_PARAM;
    return startMove((int32_t)t);
}

void rotatorHalt() {
    seq = SEQ_NONE;                  // aborts homing / calibration without a sticky error
    if (moving) haltRequested = true;
    axisRot.halt();
}

FwError rotatorSync(int32_t signedCdeg) {
    if (signedCdeg < settings.rot.wrapMin || signedCdeg > settings.rot.wrapMax) return FW_ERR_BAD_PARAM;
    if (statusAnyBusy()) return FW_ERR_BUSY;
    angleIdle = signedCdeg;
    axisRot.setPosition(rotCdegToSteps(signedCdeg, spk()));
    unverified = suspect = false;
    scheduleSave();
    return FW_OK;
}

FwError rotatorImuHome() {
    if (statusAnyBusy()) return FW_ERR_BUSY;
    if (statusSafeMode() || !imuAnswering()) return FW_ERR_SENSOR;
    if (!imuCalibrated()) return FW_ERR_NOT_CALIBRATED;
    if (imuNearVertical()) return FW_ERR_IMU_VERTICAL;
    if (!driverReady()) return FW_ERR_MOTOR_DRIVER;
    homeMoves = 0;
    beginSettle(SEQ_HOME_SETTLE);
    return FW_OK;
}

FwError rotatorImuCalibrate() {
    if (statusAnyBusy()) return FW_ERR_BUSY;
    if (statusSafeMode() || !imuAnswering()) return FW_ERR_SENSOR;
    // ERR,15 up front is possible only with an existing calibration (imuNearVertical() uses it);
    // without one, a vertical optical axis is found by imuCalFinish() (sticky FW_ERR_CALIB_FAILED).
    if (imuNearVertical()) return FW_ERR_IMU_VERTICAL;
    const FwError g = guard(true, 0);
    if (g != FW_OK) return g;
    // The moves up to +180 deg must stay inside the cable wrap (sync with :76,0# first when the
    // saved angle is wrong): rejected like any move outside the range.
    if ((int64_t)angleIdle + 2 * CAL_STEP_CDEG > settings.rot.wrapMax) return FW_ERR_BAD_PARAM;
    if (!driverReady()) return FW_ERR_MOTOR_DRIVER;
    calStart = angleIdle;
    beginSettle(SEQ_CAL_SETTLE0);
    return FW_OK;
}

int32_t rotatorAngle() {
    return moving ? rotStepsToCdeg(axisRot.position(), spk()) : angleIdle;
}

int32_t rotatorTarget() { return moving ? moveTarget : ROT_NO_TARGET; }

bool rotatorBusy() { return moving || axisRot.busy() || seq != SEQ_NONE; }
bool rotatorHalting() { return axisRot.halting(); }
bool rotatorHoming() { return seq != SEQ_NONE; }
bool rotatorUnverified() { return unverified; }
bool rotatorSuspect() { return suspect; }

void rotatorApplySettings() {
    const uint32_t s = spk();
    axisRot.setSpeed(rotRateToSteps(settings.rot.speed, s));
    axisRot.setAccel(rotRateToSteps(settings.rot.accel, s));
    axisRot.setReverse(settings.rot.reverse != 0);
    axisRot.setIdleOff(settings.drv[AXIS_ROT].idleOffS);
    // the angle stays; the step position follows steps/degree (ignored by the axis while busy)
    if (!rotatorBusy()) axisRot.setPosition(rotCdegToSteps(angleIdle, s));
}

void rotatorRescale(uint16_t oldUsteps, uint16_t newUsteps) {
    settings.rot.stepsPer1000Deg = rotRescaleSpk(settings.rot.stepsPer1000Deg, oldUsteps, newUsteps);
    rotatorApplySettings();
}
