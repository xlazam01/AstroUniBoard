// Hardware step timer (Q11: timer + STEP pin, exact step count). One timer serves both axes because
// only one axis moves at a time (Q12). The ISR only toggles pins and counts; ramps are computed
// in Axis::isrStep() with integer math and no allocation.
// Owner: session A (#23). FROZEN interface.
// Timer allocation: the step timer uses TIM3 (as OpenWheel), the LED soft PWM uses TIM16.
#pragma once
#include <stdint.h>

class Axis;

void stepTimerBegin();               // set up the timer, not running
bool stepTimerStart(Axis* axis);     // start generating steps for this axis; false if another axis runs
void stepTimerStop();                // stop at once (no ramp); the axis keeps its counted position
bool stepTimerRunning();
Axis* stepTimerAxis();               // axis being stepped, nullptr when stopped
