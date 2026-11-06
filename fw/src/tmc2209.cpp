// STUB — session A (#24, #27) implements the TMC2209 UART driver and StallGuard.
#include "tmc2209.h"

bool tmcBegin() { return false; }
bool tmcApply(AxisId axis) { (void)axis; return false; }
bool tmcAnswering(AxisId axis) { (void)axis; return false; }
void tmcUpdate() {}
int16_t tmcTempClass(AxisId axis) { (void)axis; return -1; }
uint8_t tmcWarnings() { return 0; }
void tmcClearLatched() {}
void tmcStallStats(AxisId axis, StallStats& out) {
    out.threshold = settings.drv[axis].stallThreshold;
    out.last = out.min = out.max = -1;
}
FwError tmcSetStallThreshold(AxisId axis, uint8_t thr) {
    settings.drv[axis].stallThreshold = thr;
    return FW_OK;
}
