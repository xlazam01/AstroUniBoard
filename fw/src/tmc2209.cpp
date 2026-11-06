// TMC2209 drivers on the shared single-wire USART2 bus (#24) and StallGuard (#27).
// Adapted from OpenWheel filterwheel.cpp (configureDriver / checkDriver / applyStallThreshold /
// watchMotion), vendored TMCStepper in lib/.
//
// All UART traffic runs in the main loop; the step ISR is independent, so polling during a move
// does not disturb stepping. A register read blocks ~5 ms (library reply delays), a write ~2 ms.
//   - moving: SG_RESULT every SG_SAMPLE_MS (stats, stall check), DRV_STATUS every FAULT_CHECK_MS
//   - idle:   one driver per IDLE_POLL_MS: GSTAT (reset -> re-apply config), DRV_STATUS (temperature)
#include <Arduino.h>
#include <TMCStepper.h>

#include "tmc2209.h"
#include "axis.h"
#include "config.h"
#include "focuser.h"
#include "status.h"

static const uint32_t TMC_FCLK          = 12000000UL;   // internal clock, TSTEP / TCOOLTHRS unit
static const uint32_t STALL_MIN_FULLSTEPS = 100;        // full steps/s: StallGuard minimum speed
static const uint32_t STALL_BLANK_MS    = 200;          // ignore StallGuard after a move starts
static const uint32_t SG_SAMPLE_MS      = 20;
static const uint32_t FAULT_CHECK_MS    = 100;
static const uint32_t IDLE_POLL_MS      = 500;          // alternating: each driver about every 1 s
static const uint8_t  MOVE_FAIL_LIMIT   = 3;            // consecutive failed reads during a move

static const uint32_t DRV_OTPW       = 1UL << 0;
static const uint32_t DRV_FAULT_MASK = 0x0000003EUL;    // ot, s2ga, s2gb, s2vsa, s2vsb
static const uint32_t DRV_T120       = 1UL << 8;
static const uint32_t DRV_T143       = 1UL << 9;
static const uint32_t DRV_T150       = 1UL << 10;
static const uint32_t DRV_T157       = 1UL << 11;
static const uint8_t  GSTAT_RESET    = 0x01;

static HardwareSerial tmcSerial(PIN_TMC_RX, PIN_TMC_TX);
static TMC2209Stepper drvFoc(&tmcSerial, TMC_RSENSE_OHM, TMC_ADDR_FOC);
static TMC2209Stepper drvRot(&tmcSerial, TMC_RSENSE_OHM, TMC_ADDR_ROT);

struct DrvState {
    bool     answering;
    int16_t  tempClass;
    bool     otpw;
    int16_t  sgLast, sgMin, sgMax;
    uint32_t moveStart;      // moveStartMs() of the move the stats belong to
    uint8_t  failCount;
};

static DrvState st[AXIS_COUNT] = {
    {false, -1, false, -1, -1, -1, 0xFFFFFFFFUL, 0},
    {false, -1, false, -1, -1, -1, 0xFFFFFFFFUL, 0},
};
static bool     resetLatched = false;
static bool     started = false;
static uint32_t lastSg = 0, lastFault = 0, lastIdle = 0;
static uint8_t  idleNext = 0;

static TMC2209Stepper& drv(AxisId a) { return a == AXIS_ROT ? drvRot : drvFoc; }

// IRUN / IHOLD from RMS mA with integer math (Rsense 0.11 + 0.02 ohm internal):
// I_rms = (CS + 1) / 32 * Vfs / (Rs + 0.02) / sqrt(2)  ->  CS + 1 = mA * 5.8831 / Vfs[mV]
static uint8_t csFor(uint16_t mA, uint32_t vfsMv) {
    uint32_t cs1 = ((uint32_t)mA * 58831UL) / (vfsMv * 10000UL);
    if (cs1 < 1) cs1 = 1;
    if (cs1 > 32) cs1 = 32;
    return (uint8_t)(cs1 - 1);
}

static uint32_t tcoolFor(const DriverConfig& c) {
    return c.stallThreshold ? TMC_FCLK / (256UL * STALL_MIN_FULLSTEPS) : 0;
}

static bool readOk(TMC2209Stepper& d) { return !d.CRCerror; }

bool tmcBegin() {
    tmcSerial.begin(TMC_BAUD);
    started = true;
    bool ok = true;
    for (uint8_t a = 0; a < AXIS_COUNT; a++) ok &= tmcApply((AxisId)a);
    lastIdle = millis();
    return ok;
}

bool tmcApply(AxisId axis) {
    if (!started || axis >= AXIS_COUNT) return false;
    TMC2209Stepper& d = drv(axis);
    const DriverConfig& c = settings.drv[axis];

    d.begin();                      // pdn_disable (UART), mstep_reg_select (MRES from UART)
    d.I_scale_analog(false);        // VREF is open on rev 2.0: internal reference
    d.multistep_filt(true);
    d.toff(4);
    d.blank_time(24);
    d.hstrt(5);
    d.hend(0);
    d.intpol(true);

    // Sense range: high sensitivity (vsense = 1, 180 mV) when the run current fits it.
    const uint16_t run = c.runMa > TMC_MAX_RUN_MA ? TMC_MAX_RUN_MA : c.runMa;
    const uint16_t hold = c.holdMa > run ? run : c.holdMa;
    const bool vs = csFor(run, 325) < 16;
    const uint32_t vfs = vs ? 180 : 325;
    d.vsense(vs);
    d.irun(csFor(run, vfs));
    d.ihold(hold ? csFor(hold, vfs) : 0);
    d.iholddelay(6);
    d.TPOWERDOWN(20);               // ~0.44 s standstill before the hold current

    d.microsteps(c.microsteps ? c.microsteps : 1);
    d.en_spreadCycle(c.mode == DRV_SPREADCYCLE);
    d.pwm_autoscale(true);
    d.pwm_autograd(true);
    d.TPWMTHRS(0);                  // StealthChop at every speed (unless SpreadCycle is selected)
    d.SGTHRS(c.stallThreshold);
    d.TCOOLTHRS(tcoolFor(c));
    d.GSTAT(0x07);                  // clear reset / drv_err / uv_cp

    // Verify: IOIN version 0x21 means the driver answered with a good CRC.
    const uint32_t ioin = d.IOIN();
    const bool ok = readOk(d) && (ioin >> 24) == 0x21;
    st[axis].answering = ok;
    if (!ok) st[axis].tempClass = -1;
    st[axis].failCount = 0;
    return ok;
}

bool tmcAnswering(AxisId axis) { return axis < AXIS_COUNT && st[axis].answering; }

static int16_t tempClassOf(uint32_t ds) {
    if (ds & DRV_T157) return 157;
    if (ds & DRV_T150) return 150;
    if (ds & DRV_T143) return 143;
    if (ds & DRV_T120) return 120;
    return 0;
}

// Idle health check of one driver: answering, reset (re-init), temperature, faults.
static void pollIdle(AxisId a) {
    TMC2209Stepper& d = drv(a);
    DrvState& s = st[a];
    const uint8_t g = d.GSTAT();
    if (!readOk(d)) {
        s.answering = false;
        s.tempClass = -1;
        s.otpw = false;
        return;
    }
    if (!s.answering || (g & GSTAT_RESET)) {
        // Driver came back (motor supply on) or restarted after a supply dip: config is lost.
        if (g & GSTAT_RESET) resetLatched = true;
        if (!tmcApply(a)) return;
    }
    const uint32_t ds = d.DRV_STATUS();
    if (!readOk(d)) {
        s.answering = false;
        s.tempClass = -1;
        s.otpw = false;
        return;
    }
    s.answering = true;
    s.tempClass = tempClassOf(ds);
    s.otpw = (ds & DRV_OTPW) != 0;
    if (ds & DRV_FAULT_MASK) statusRaise(FW_ERR_MOTOR_DRIVER);
}

static void faultStop(Axis& ax, FwError code) {
    ax.stopNow();
    if (ax.id() == AXIS_FOC) focuserSetUnverified();
    statusRaise(code);
}

// A move is running on ax: StallGuard sampling and stall stop, driver fault check.
static void watchMove(Axis& ax, uint32_t now) {
    const AxisId a = ax.id();
    TMC2209Stepper& d = drv(a);
    DrvState& s = st[a];
    const DriverConfig& c = settings.drv[a];

    if (s.moveStart != ax.moveStartMs()) {      // new move: fresh statistics
        s.moveStart = ax.moveStartMs();
        s.sgLast = s.sgMin = s.sgMax = -1;
        s.failCount = 0;
        lastSg = lastFault = now;
    }

    if (now - lastFault >= FAULT_CHECK_MS) {
        lastFault = now;
        const uint32_t ds = d.DRV_STATUS();
        if (!readOk(d)) {
            if (++s.failCount >= MOVE_FAIL_LIMIT) {
                s.answering = false;
                s.tempClass = -1;
                faultStop(ax, FW_ERR_MOTOR_DRIVER);
            }
            return;
        }
        s.failCount = 0;
        s.otpw = (ds & DRV_OTPW) != 0;
        if (ds & DRV_FAULT_MASK) {
            faultStop(ax, FW_ERR_MOTOR_DRIVER);
            return;
        }
    }

    if (now - lastSg >= SG_SAMPLE_MS) {
        lastSg = now;
        const uint16_t sgr = d.SG_RESULT();
        if (!readOk(d)) {
            if (++s.failCount >= MOVE_FAIL_LIMIT) {
                s.answering = false;
                s.tempClass = -1;
                faultStop(ax, FW_ERR_MOTOR_DRIVER);
            }
            return;
        }
        s.failCount = 0;
        if (!ax.busy()) return;                 // ended while we were reading
        const int16_t sg = (int16_t)(sgr & 0x3FF);
        s.sgLast = sg;
        if (s.sgMin < 0 || sg < s.sgMin) s.sgMin = sg;
        if (sg > s.sgMax) s.sgMax = sg;

        const uint32_t usteps = c.microsteps ? c.microsteps : 1;
        if (c.stallThreshold && c.mode == DRV_STEALTHCHOP &&
            now - ax.moveStartMs() >= STALL_BLANK_MS &&
            ax.speedNow() >= STALL_MIN_FULLSTEPS * usteps &&
            sg <= 2 * (int16_t)c.stallThreshold) {
            faultStop(ax, FW_ERR_MOTOR_STALL);
        }
    }
}

void tmcUpdate() {
    if (!started) return;
    const uint32_t now = millis();
    Axis* moving = axisFoc.busy() ? &axisFoc : axisRot.busy() ? &axisRot : nullptr;
    if (moving) {
        watchMove(*moving, now);
        lastIdle = now;                         // first idle poll one period after the move
        return;
    }
    if (now - lastIdle >= IDLE_POLL_MS) {
        lastIdle = now;
        pollIdle((AxisId)idleNext);
        idleNext = (idleNext + 1) % AXIS_COUNT;
    }
}

int16_t tmcTempClass(AxisId axis) { return axis < AXIS_COUNT && st[axis].answering ? st[axis].tempClass : -1; }

uint8_t tmcWarnings() {
    uint8_t w = 0;
    if (st[AXIS_FOC].otpw || st[AXIS_ROT].otpw) w |= TMC_WARN_OTPW;
    if (resetLatched) w |= TMC_WARN_RESET;
    return w;
}

void tmcClearLatched() { resetLatched = false; }

void tmcStallStats(AxisId axis, StallStats& out) {
    if (axis >= AXIS_COUNT) axis = AXIS_FOC;
    out.threshold = settings.drv[axis].stallThreshold;
    out.last = st[axis].sgLast;
    out.min = st[axis].sgMin;
    out.max = st[axis].sgMax;
}

FwError tmcSetStallThreshold(AxisId axis, uint8_t thr) {
    if (axis >= AXIS_COUNT) return FW_ERR_BAD_PARAM;
    if (axisAnyBusy()) return FW_ERR_BUSY;
    DriverConfig& c = settings.drv[axis];
    c.stallThreshold = thr;
    if (started) {
        TMC2209Stepper& d = drv(axis);
        d.SGTHRS(thr);
        d.TCOOLTHRS(tcoolFor(c));
    }
    return FW_OK;
}
