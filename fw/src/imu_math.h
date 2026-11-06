// Pure IMU zero-calibration math (#32, Q19 / Q19b): no Arduino, no I/O, host-testable (test/test_imu_math).
// Vectors are accelerometer readings in mg, chip axes. Single-precision float; never call from an ISR.
// Owner: session C. Used only by imu_mpu6000.cpp.
#pragma once
#include <stdint.h>
#include "error_codes.h"
#include "nv_store.h"

#define IMU_MATH_INVALID  (-99999)

struct ImuV3 { float x, y, z; };

// Limits (also documented in docs/PROTOCOL.md §4.3)
#define IMU_CAL_G_MIN_MG      700.0f    // a capture must look like 1 g at rest
#define IMU_CAL_G_MAX_MG      1300.0f
#define IMU_CAL_COS_MAX       0.3420f   // each step, measured about n, is 70..110 deg (|cos| <= cos 70 deg)
#define IMU_VERTICAL_COS      0.9397f   // |cos(n, g)| >= cos 20 deg: roll undefined
#define IMU_VERTICAL_SIN      0.3420f
#define IMU_CAL_OFFSET_MAX_MG 300.0f    // larger circle-centre offset: not a sensor offset, reject   // sin 20 deg: gravity circle radius below this * |g| = near vertical

// Three captures at the rotator angles 0, +90 and +180 deg. Gravity seen by the chip moves on a
// circle around the rotation axis, so the axis is the normal of the plane through the three points,
// n = (g90 - g0) x (g180 - g90) normalised. Unlike g0 x g90 this is exact at any elevation of the
// optical axis, and its sign makes the roll positive in the rotator's + direction.
// The circle centre is stored too (its part in the plane normal to n = the accelerometer offset in
// that plane, up to IMU_CAL_OFFSET_MAX_MG): roll and tilt are measured from it, so an offset of a few
// tens of mg no longer bends the roll away from zero (measured: -6.4 deg at -120 deg without it).
// Checks: each capture 700..1300 mg; optical axis not within ~20 deg of vertical (FW_ERR_IMU_VERTICAL);
// both 90 deg steps measured about n are 70..110 deg (else FW_ERR_CALIB_FAILED: rotator slipped, IMU
// moved). FW_OK fills `out` (valid = 1, g0 in mg, n * 10000); errors leave `out` untouched.
FwError imuMathCalibrate(const ImuV3& g0, const ImuV3& g90, const ImuV3& g180, ImuCalibration& out);

// Plausibility of a stored record (valid flag, |n| about 10000, |g0| about 1 g).
bool imuMathCalValid(const ImuCalibration& c);

// Signed roll in cdeg (-18000..18000) from g0 to g about n; IMU_MATH_INVALID when near vertical.
int32_t imuMathRollCdeg(const ImuCalibration& c, const ImuV3& g);
// Angle between n and the measured gravity, cdeg 0..18000 (0 / 18000 = optical axis vertical).
int32_t imuMathTiltCdeg(const ImuCalibration& c, const ImuV3& g);
bool    imuMathNearVertical(const ImuCalibration& c, const ImuV3& g);
