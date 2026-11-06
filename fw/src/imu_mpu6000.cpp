// STUB — session C (#31, #32) implements the MPU-6000 driver and the calibration math.
#include "imu_mpu6000.h"

static ImuCalibration cal;

bool imuBegin() { return false; }
void imuUpdate() {}
bool imuAnswering() { return false; }
bool imuCalibrated() { return false; }
bool imuAccel(ImuVec& mg) { mg = ImuVec{0, 0, 0}; return false; }
int32_t imuRollCdeg() { return IMU_INVALID; }
int32_t imuTiltCdeg() { return IMU_INVALID; }
bool imuNearVertical() { return false; }
void imuRestartFilter() {}
bool imuSettled() { return false; }
bool imuCalCaptureG0() { return false; }
bool imuCalCaptureG90() { return false; }
FwError imuCalFinish() { return FW_ERR_CALIB_FAILED; }
const ImuCalibration& imuCalData() { return cal; }
