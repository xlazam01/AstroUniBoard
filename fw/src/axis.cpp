// STUB — session A (#23) implements the ramp and ISR stepping.
#include <Arduino.h>
#include "axis.h"
#include "config.h"
#include "step_timer.h"

Axis axisFoc(AXIS_FOC, PIN_FOC_STEP, PIN_FOC_DIR, PIN_FOC_EN);
Axis axisRot(AXIS_ROT, PIN_ROT_STEP, PIN_ROT_DIR, PIN_ROT_EN);

Axis& axisById(AxisId id) { return id == AXIS_ROT ? axisRot : axisFoc; }
bool axisAnyBusy() { return axisFoc.busy() || axisRot.busy(); }

Axis::Axis(AxisId id, uint32_t s, uint32_t d, uint32_t e) : axisId(id), pinStep(s), pinDir(d), pinEn(e) {}

void Axis::begin() {
    pinMode(pinEn, OUTPUT);
    digitalWrite(pinEn, HIGH);   // driver off (active LOW)
    pinMode(pinStep, OUTPUT);
    pinMode(pinDir, OUTPUT);
}

bool Axis::moveTo(int32_t target, uint32_t leadSteps) {
    (void)leadSteps;
    return target == pos;
}
void Axis::halt() {}
void Axis::stopNow() {}
void Axis::update() {}
int32_t Axis::position() const { return pos; }
void Axis::setPosition(int32_t p) { if (!busy()) pos = p; }
int32_t Axis::target() const { return tgt; }
bool Axis::busy() const { return mot != AXIS_IDLE; }
bool Axis::halting() const { return mot == AXIS_HALTING; }
AxisMotion Axis::motion() const { return (AxisMotion)mot; }
uint32_t Axis::speedNow() const { return 0; }
uint32_t Axis::moveStartMs() const { return startMs; }
bool Axis::justStopped() { bool f = stoppedFlag; stoppedFlag = false; return f; }
void Axis::setSpeed(uint32_t v) { speed = v; }
void Axis::setAccel(uint32_t v) { accel = v; }
void Axis::setReverse(bool r) { reverse = r; }
void Axis::setIdleOff(uint16_t s) { idleOffS = s; }
void Axis::isrStep() {}
