// STUB — session B (#29, #30, #32) implements the rotator.
#include "rotator.h"
#include "axis.h"

void rotatorBegin() {}
void rotatorUpdate() {}
FwError rotatorMoveTo(int32_t t) { (void)t; return FW_ERR_UNKNOWN_CMD; }
FwError rotatorMoveBy(int32_t d) { (void)d; return FW_ERR_UNKNOWN_CMD; }
void rotatorHalt() { axisRot.halt(); }
FwError rotatorSync(int32_t a) { (void)a; return FW_ERR_UNKNOWN_CMD; }
FwError rotatorImuHome() { return FW_ERR_UNKNOWN_CMD; }
FwError rotatorImuCalibrate() { return FW_ERR_UNKNOWN_CMD; }
int32_t rotatorAngle() { return 0; }
int32_t rotatorTarget() { return ROT_NO_TARGET; }
bool rotatorBusy() { return axisRot.busy(); }
bool rotatorHalting() { return axisRot.halting(); }
bool rotatorHoming() { return false; }
bool rotatorUnverified() { return true; }
void rotatorApplySettings() {}
void rotatorRescale(uint16_t o, uint16_t n) { (void)o; (void)n; }
