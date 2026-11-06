// MPU-6000 accelerometer on I2C 0x68 (#31) and the IMU zero calibration math (#32, Q19 / Q19b).
// The board rotates with the camera, so gravity in chip axes gives the camera roll.
// Rev 1: /CS floats (rev 2: tied high), so I2C mode must be checked on the rev 1 board.
// Owner: session C. FROZEN interface. The rotator (session B) sequences homing and calibration.
#pragma once
#include <stdint.h>
#include "error_codes.h"
#include "nv_store.h"

#define IMU_INVALID  (-99999)

struct ImuVec { int16_t x, y, z; };      // mg, chip axes

bool imuBegin();                          // probe + configure; loads the calibration (nvLoadImuCal)
void imuUpdate();                         // main loop: sample + low-pass filter
bool imuAnswering();                      // status flag 6
bool imuCalibrated();                     // status flag 7 = !imuCalibrated()
bool imuAccel(ImuVec& mg);                // filtered acceleration; false when not answering

// Roll = signed angle from g0 to the current gravity, both projected onto the plane normal to n,
// sign by n. With n = g0 x g90 (g90 taken after the rotator moved +90 deg), roll has the sign of
// the mechanical angle: at the +90 deg calibration position roll is about +9000, so the rotator
// reaches the IMU zero by moving -roll. IMU_INVALID when not calibrated or near vertical.
int32_t imuRollCdeg();
int32_t imuTiltCdeg();                    // angle between the optical axis (n) and vertical; IMU_INVALID if not calibrated
bool    imuNearVertical();                // calibrated and the optical axis is within ~20 deg of vertical -> ERR,15

// Settling: after the rotator stops, call imuRestartFilter(); imuSettled() turns true once enough
// fresh samples are averaged for a stable roll or calibration capture.
void imuRestartFilter();
bool imuSettled();

// Calibration (:95), sequenced by the rotator: capture g0 (landscape), move +90 deg, capture g90,
// move +180 deg, capture g180, move back, finish. imuCalFinish() computes the rotation axis
// n = (g90 - g0) x (g180 - g90) (exact at any elevation, imu_math.h), checks the steps are about
// 90 deg and stores g0 + n with nvSaveImuCal():
// FW_OK, FW_ERR_IMU_VERTICAL (gravity nearly parallel to n), FW_ERR_CALIB_FAILED or FW_ERR_EEPROM.
bool    imuCalCaptureG0();                // false when not answering or not settled
bool    imuCalCaptureG90();
bool    imuCalCaptureG180();
FwError imuCalFinish();
const ImuCalibration& imuCalData();       // :96
