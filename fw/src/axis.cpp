// Generic stepper axis (#23): exact step count, trapezoid ramp, halt, driver enable / idle-off.
//
// Ramp: per-step delay recursion from Atmel AVR446 ("Linear speed control of stepper motor"):
//   c0 = 0.676 * F * sqrt(2 / a),   c(n) = c(n-1) - (2 c(n-1) + rest) / (4n + 1)
// with the remainder carried, so it stays accurate in integer math. One 32-bit division per ramp
// step (none while cruising). Deceleration mirrors it with (4k - 1), k = steps left to stop.
// The axis decelerates when the steps left reach the steps it used to accelerate, so short moves
// become triangles without any precomputation, and halt() decelerates over the same number of
// steps from wherever the ramp is. Every step is counted in the ISR, so the position stays exact.
#include <Arduino.h>
#include "axis.h"
#include "config.h"
#include "step_timer.h"

// Internal to the motion module (step_timer.cpp).
extern const uint32_t STEP_TIMER_HZ;
void stepTimerSchedule(uint32_t ticks);

static const uint32_t MAX_STEP_HZ   = 50000;     // ISR budget: step rate is clamped to this
static const uint32_t SETTLE_TICKS  = 16000;     // 2 ms after EN goes active before the first step

Axis axisFoc(AXIS_FOC, PIN_FOC_STEP, PIN_FOC_DIR, PIN_FOC_EN);
Axis axisRot(AXIS_ROT, PIN_ROT_STEP, PIN_ROT_DIR, PIN_ROT_EN);

Axis& axisById(AxisId id) { return id == AXIS_ROT ? axisRot : axisFoc; }
bool axisAnyBusy() { return axisFoc.busy() || axisRot.busy(); }

Axis::Axis(AxisId id, uint32_t s, uint32_t d, uint32_t e) : axisId(id), pinStep(s), pinDir(d), pinEn(e) {}

void Axis::begin() {
    pinMode(pinEn, OUTPUT);
    digitalWrite(pinEn, HIGH);   // driver off (active LOW)
    enabled = false;
    pinMode(pinStep, OUTPUT);
    digitalWrite(pinStep, LOW);
    pinMode(pinDir, OUTPUT);
    GPIO_TypeDef* port = digitalPinToPort(pinStep);
    stepSet = &port->BSRR;
    stepClr = &port->BRR;
    stepMask = digitalPinToBitMask(pinStep);
}

void Axis::enableDriver(bool on) {
    digitalWrite(pinEn, on ? LOW : HIGH);
    enabled = on;
}

bool Axis::moveTo(int32_t target, uint32_t leadSteps) {
    if (axisAnyBusy() || stepTimerRunning() || !stepSet) return false;
    const int32_t p = pos;
    const int64_t diff = (int64_t)target - p;
    const uint32_t dist = (uint32_t)(diff < 0 ? -diff : diff);
    if (dist == 0 && leadSteps == 0) return true;

    const bool fwd = diff >= 0;
    dirStep = fwd ? 1 : -1;
    digitalWrite(pinDir, (fwd != reverse) ? HIGH : LOW);

    uint32_t v = speed ? speed : 1;
    if (v > MAX_STEP_HZ) v = MAX_STEP_HZ;
    minDelay = STEP_TIMER_HZ / v;
    const uint32_t a = accel ? accel : 1;
    float c0f = 0.676f * (float)STEP_TIMER_HZ * sqrtf(2.0f / (float)a);
    uint32_t c0 = (c0f > 16000000.0f) ? 16000000UL : (uint32_t)c0f;
    if (c0 < minDelay) c0 = minDelay;

    tgt = target;
    lead = leadSteps;
    total = dist + leadSteps;
    remain = dist + leadSteps;
    rampN = 0;
    decelLeft = 0;
    restTicks = 0;
    delayTicks = c0;
    phase = PH_ACCEL;
    haltReq = false;
    settle = !enabled;
    enableDriver(true);
    stoppedFlag = false;
    startMs = millis();
    mot = AXIS_ACCEL;
    wasBusy = true;
    if (!stepTimerStart(this)) {
        mot = AXIS_IDLE;
        remain = 0;
        return false;
    }
    return true;
}

void Axis::halt() {
    noInterrupts();
    if (mot != AXIS_IDLE && mot != AXIS_HALTING) {
        if (remain == total) {          // no step made yet: nothing to decelerate
            if (stepTimerAxis() == this) stepTimerStop();
            remain = 0;
            mot = AXIS_IDLE;
            stoppedFlag = true;
        } else {
            haltReq = true;
            mot = AXIS_HALTING;
        }
    }
    interrupts();
}

void Axis::stopNow() {
    noInterrupts();
    if (stepTimerAxis() == this) stepTimerStop();
    if (mot != AXIS_IDLE) {
        mot = AXIS_IDLE;
        stoppedFlag = true;
    }
    remain = 0;
    haltReq = false;
    interrupts();
}

void Axis::update() {
    const bool b = busy();
    const uint32_t now = millis();
    if (wasBusy && !b) stopMs = now;
    wasBusy = b;
    if (!b && enabled && idleOffS && now - stopMs >= (uint32_t)idleOffS * 1000UL) enableDriver(false);
}

int32_t Axis::position() const { return pos; }
void Axis::setPosition(int32_t p) { if (!busy()) pos = p; }
int32_t Axis::target() const { return tgt; }
bool Axis::busy() const { return mot != AXIS_IDLE; }
bool Axis::halting() const { return mot == AXIS_HALTING; }
AxisMotion Axis::motion() const { return (AxisMotion)mot; }
uint32_t Axis::speedNow() const {
    if (mot == AXIS_IDLE) return 0;
    const uint32_t d = delayTicks;
    return d ? STEP_TIMER_HZ / d : 0;
}
uint32_t Axis::moveStartMs() const { return startMs; }
bool Axis::justStopped() {
    noInterrupts();
    bool f = stoppedFlag;
    stoppedFlag = false;
    interrupts();
    return f;
}
void Axis::setSpeed(uint32_t v) { speed = v ? v : 1; }
void Axis::setAccel(uint32_t v) { accel = v ? v : 1; }
void Axis::setReverse(bool r) { reverse = r; }
void Axis::setIdleOff(uint16_t s) { idleOffS = s; }

void Axis::finishIsr() {
    stepTimerStop();
    remain = 0;
    haltReq = false;
    mot = AXIS_IDLE;
    stoppedFlag = true;
}

// Step timer ISR. Keep it short: one pulse, one counter update, at most one division.
void Axis::isrStep() {
    if (settle) {                       // driver just enabled: wait before the first step
        settle = false;
        stepTimerSchedule(SETTLE_TICKS);
        return;
    }
    *stepSet = stepMask;                // STEP high; the work below is the pulse width (> 1 us)
    if (lead) lead = lead - 1;
    else pos = pos + dirStep;
    const uint32_t r = remain - 1;
    remain = r;
    if (r == 0) {
        finishIsr();
        *stepClr = stepMask;
        return;
    }

    uint32_t d = delayTicks;
    uint32_t rest = restTicks;
    uint8_t ph = phase;
    const bool h = haltReq;
    haltReq = false;

    if (ph == PH_ACCEL) {
        const uint32_t n = rampN + 1;
        rampN = n;
        if (h || r <= n) {
            decelLeft = (r < n) ? r : n;   // halt: as many steps as the ramp up took
            ph = PH_DECEL;
        } else {
            const uint32_t x = 2 * d + rest;
            const uint32_t div = 4 * n + 1;
            const uint32_t q = x / div;
            rest = x - q * div;
            d -= q;
            if (d <= minDelay) {
                d = minDelay;
                rest = 0;
                ph = PH_RUN;
            }
        }
    } else if (ph == PH_RUN) {
        const uint32_t n = rampN;
        if (h || r <= n) {
            decelLeft = (r < n) ? r : n;
            ph = PH_DECEL;
            rest = 0;
        }
    } else {
        const uint32_t k = decelLeft - 1;
        decelLeft = k;
        if (k == 0) {                   // halt finished before the target
            finishIsr();
            *stepClr = stepMask;
            return;
        }
    }

    if (ph == PH_DECEL) {
        const uint32_t k = decelLeft;
        const uint32_t x = 2 * d + rest;
        const uint32_t div = 4 * k - 1;
        const uint32_t q = x / div;
        rest = x - q * div;
        d += q;
    }

    delayTicks = d;
    restTicks = rest;
    phase = ph;
    if (mot != AXIS_HALTING) mot = (ph == PH_ACCEL) ? AXIS_ACCEL : (ph == PH_RUN) ? AXIS_CRUISE : AXIS_DECEL;
    stepTimerSchedule(d);
    *stepClr = stepMask;
}
