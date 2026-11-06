// IMU zero calibration math (#32). See imu_math.h.
//
// Roll sign (header contract: roll(g90) = +9000).
// The board turns with the camera. Turning the camera by +theta about the optical axis a (rotator
// direction) turns the chip frame by +theta, so gravity seen in chip axes turns by -theta about a:
//     g(theta) = Rot(a, -theta) g0.
// For g0 perpendicular to a and theta = +90 deg this gives g90 = -(a x g0), hence
//     g0 x g90 = -g0 x (a x g0) = -a |g0|^2      ->  n = g0 x g90 / |..| = -a.
// So g(theta) = Rot(-n, -theta) g0 = Rot(n, +theta) g0: measured about n (right hand), the angle from
// g0 to the current gravity is +theta. With both vectors projected onto the plane normal to n,
//     roll = atan2(n . (g0p x gp), g0p . gp)     (n . (g0p x gp) = n . (g0 x g), the n parts drop out)
// which is +90 deg at the +90 deg capture by construction (n . (g0 x g90) = |g0 x g90| > 0). The
// rotator therefore reaches the IMU zero by moving -roll. The sign does not depend on how the board
// is mounted (Triton: upside down) nor on the accelerometer sign convention (both g0 and g are read
// the same way).
//
// Limitation: n = g0 x g90 is the true rotation axis only when the optical axis is horizontal during
// :95. With the axis at elevation e, cos(g0, g90) = sin^2 e, so the 70..110 deg check accepts
// e <= ~36 deg, and n is off the true axis by atan(sqrt(2) tan e) (e = 10 deg: 14 deg, 20 deg: 27 deg;
// checked numerically). Roll is 0 at the g0 orientation at any e, but at +90 deg it reads the angle
// between g0 and g90 (e = 10: 88.3 deg, e = 20: 83.3 deg). Calibrate with the scope close to horizontal.
// A third capture at +180 deg would give the exact axis for any e: n ~ (g90 - g0) x (g180 - g90).
#include <math.h>

#include "imu_math.h"

static const float RAD_TO_CDEG = 18000.0f / 3.14159265f;

static float dot(const ImuV3& a, const ImuV3& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static ImuV3 cross(const ImuV3& a, const ImuV3& b) {
    return ImuV3{ a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x };
}
static float norm(const ImuV3& a) { return sqrtf(dot(a, a)); }

static ImuV3 calN(const ImuCalibration& c) {
    ImuV3 n{ (float)c.n[0], (float)c.n[1], (float)c.n[2] };
    const float l = norm(n);
    if (l > 0.0f) { n.x /= l; n.y /= l; n.z /= l; }
    return n;
}
static ImuV3 calG0(const ImuCalibration& c) { return ImuV3{ (float)c.g0[0], (float)c.g0[1], (float)c.g0[2] }; }

static int16_t toI16(float v) {
    if (v > 32767.0f) return 32767;
    if (v < -32768.0f) return -32768;
    return (int16_t)lroundf(v);
}

FwError imuMathCalibrate(const ImuV3& g0, const ImuV3& g90, ImuCalibration& out) {
    const float l0 = norm(g0), l90 = norm(g90);
    if (l0 < IMU_CAL_G_MIN_MG || l0 > IMU_CAL_G_MAX_MG || l90 < IMU_CAL_G_MIN_MG || l90 > IMU_CAL_G_MAX_MG)
        return FW_ERR_CALIB_FAILED;

    // Optical axis near vertical: turning the camera hardly moves gravity in chip axes.
    const float c = dot(g0, g90) / (l0 * l90);
    if (c >= IMU_CAL_COS_VERTICAL) return FW_ERR_IMU_VERTICAL;
    if (fabsf(c) > IMU_CAL_COS_MAX) return FW_ERR_CALIB_FAILED;   // rotator slipped / IMU moved

    ImuV3 n = cross(g0, g90);
    const float ln = norm(n);
    if (ln <= 0.0f) return FW_ERR_CALIB_FAILED;
    out.valid = 1;
    out.g0[0] = toI16(g0.x);
    out.g0[1] = toI16(g0.y);
    out.g0[2] = toI16(g0.z);
    out.n[0] = toI16(n.x / ln * 10000.0f);
    out.n[1] = toI16(n.y / ln * 10000.0f);
    out.n[2] = toI16(n.z / ln * 10000.0f);
    return FW_OK;
}

bool imuMathCalValid(const ImuCalibration& c) {
    if (c.valid != 1) return false;
    const ImuV3 n{ (float)c.n[0], (float)c.n[1], (float)c.n[2] };
    const float ln = norm(n), lg = norm(calG0(c));
    return ln >= 9000.0f && ln <= 11000.0f && lg >= IMU_CAL_G_MIN_MG && lg <= IMU_CAL_G_MAX_MG;
}

bool imuMathNearVertical(const ImuCalibration& c, const ImuV3& g) {
    const float lg = norm(g);
    if (lg <= 0.0f) return true;
    return fabsf(dot(calN(c), g)) / lg >= IMU_VERTICAL_COS;
}

int32_t imuMathRollCdeg(const ImuCalibration& c, const ImuV3& g) {
    if (imuMathNearVertical(c, g)) return IMU_MATH_INVALID;
    const ImuV3 n = calN(c);
    const ImuV3 g0 = calG0(c);
    const float a0 = dot(g0, n), a = dot(g, n);
    const ImuV3 g0p{ g0.x - a0 * n.x, g0.y - a0 * n.y, g0.z - a0 * n.z };
    const ImuV3 gp{ g.x - a * n.x, g.y - a * n.y, g.z - a * n.z };
    const float s = dot(n, cross(g0p, gp));
    const float k = dot(g0p, gp);
    int32_t r = (int32_t)lroundf(atan2f(s, k) * RAD_TO_CDEG);
    if (r > 18000) r = 18000;
    if (r < -18000) r = -18000;
    return r;
}

int32_t imuMathTiltCdeg(const ImuCalibration& c, const ImuV3& g) {
    const ImuV3 n = calN(c);
    if (norm(g) <= 0.0f) return IMU_MATH_INVALID;
    const float t = atan2f(norm(cross(n, g)), dot(n, g));   // 0..pi
    return (int32_t)lroundf(t * RAD_TO_CDEG);
}
