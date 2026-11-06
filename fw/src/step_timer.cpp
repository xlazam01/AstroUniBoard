// STUB — session A (#23) implements the TIM3 step generator.
#include "step_timer.h"
#include "axis.h"

static Axis* active = nullptr;

void stepTimerBegin() {}
bool stepTimerStart(Axis* axis) { (void)axis; return false; }
void stepTimerStop() { active = nullptr; }
bool stepTimerRunning() { return active != nullptr; }
Axis* stepTimerAxis() { return active; }
