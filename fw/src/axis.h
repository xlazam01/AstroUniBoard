// Generic stepper axis: counted position, target, trapezoid ramp, halt, busy state (#23).
// Units are microsteps at the axis' current microstep setting. The axis knows no limits and no
// angles: focuser.cpp / rotator.cpp check soft limits and the cable wrap before calling moveTo().
// Only one axis moves at a time (Q12): moveTo() fails while any axis is busy.
// Owner: session A. FROZEN public interface; private members are A's to choose.
#pragma once
#include <stdint.h>
#include "settings.h"

enum AxisMotion : uint8_t {
    AXIS_IDLE = 0,
    AXIS_ACCEL,
    AXIS_CRUISE,
    AXIS_DECEL,
    AXIS_HALTING        // decelerating after halt()
};

class Axis {
public:
    Axis(AxisId id, uint32_t pinStep, uint32_t pinDir, uint32_t pinEn);

    void begin();                       // pins; driver stays disabled until the first move

    // Main loop only.
    // Start a move to target. leadSteps extra steps are run first in the direction of the move and
    // are not counted in the position (focuser backlash take-up). Returns false (nothing started)
    // when any axis is busy. target == position and leadSteps == 0 returns true without motion.
    bool moveTo(int32_t target, uint32_t leadSteps = 0);
    void halt();                        // decelerate to a stop; position stays exact
    void stopNow();                     // immediate stop (faults); position stays counted
    void update();                      // every loop pass: idle-off timer, end-of-move bookkeeping

    int32_t position() const;
    void    setPosition(int32_t pos);   // sync, no motion; ignored while busy
    int32_t target() const;             // target of the running move (valid while busy())
    bool    busy() const;               // moving or halting
    bool    halting() const;
    AxisMotion motion() const;
    uint32_t speedNow() const;          // current step rate, steps/s (StallGuard minimum speed check)
    uint32_t moveStartMs() const;       // millis() when the running / last move started
    bool    justStopped();              // true once after a move ended (normally or halted)

    void setSpeed(uint32_t stepsPerSec);    // takes effect at the next move
    void setAccel(uint32_t stepsPerSec2);
    void setReverse(bool rev);              // motor direction only; position keeps its meaning
    void setIdleOff(uint16_t seconds);      // 0 = driver stays enabled after the move

    AxisId id() const { return axisId; }

    // Called from the step timer ISR only.
    void isrStep();

private:
    AxisId   axisId;
    uint32_t pinStep, pinDir, pinEn;

    volatile int32_t  pos = 0;
    volatile int32_t  tgt = 0;
    volatile uint8_t  mot = AXIS_IDLE;
    volatile uint32_t lead = 0;
    volatile bool     stoppedFlag = false;
    uint32_t speed = 1, accel = 1;
    uint16_t idleOffS = 0;
    bool     reverse = false;
    uint32_t startMs = 0;

    // Ramp state (AVR446-style per-step delay recursion, timer ticks). Written by moveTo() before
    // the timer starts, then owned by the ISR.
    enum Phase : uint8_t { PH_ACCEL, PH_RUN, PH_DECEL };
    volatile uint8_t  phase = PH_ACCEL;
    volatile uint32_t remain = 0;       // steps left including lead steps
    volatile uint32_t total = 0;        // steps of this move including lead steps
    volatile uint32_t delayTicks = 0;   // period to the next step
    volatile uint32_t restTicks = 0;    // remainder carried by the recursion
    volatile uint32_t rampN = 0;        // accel steps done (= steps needed to stop from this speed)
    volatile uint32_t decelLeft = 0;    // decel steps left
    volatile bool     haltReq = false;
    volatile bool     settle = false;   // first ISR waits for the driver enable settle time
    uint32_t minDelay = 0;              // cruise period
    int8_t   dirStep = 1;               // +1 / -1 position change per counted step
    bool     enabled = false;           // driver EN active
    bool     wasBusy = false;
    uint32_t stopMs = 0;                // millis() when the last move ended
    volatile uint32_t* stepSet = nullptr;   // GPIO BSRR / BRR of the STEP pin
    volatile uint32_t* stepClr = nullptr;
    uint32_t stepMask = 0;

    void finishIsr();
    void enableDriver(bool on);
};

extern Axis axisFoc;
extern Axis axisRot;
Axis& axisById(AxisId id);
bool axisAnyBusy();
