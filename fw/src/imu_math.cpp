// IMU zero calibration math (#32). See imu_math.h.
//
// Roll sign (header contract: roll(g90) = +9000).
// The board turns with the camera. Turning the camera by +theta about the optical axis a (rotator
// direction) turns the chip frame by +theta, so gravity seen in chip axes turns by -theta about a:
//     g(theta) = Rot(a, -theta) g0 = Rot(-a, +theta) g0.
// g0, g90 and g180 lie on a circle about the axis and are passed in this order, so
//     n = (g90 - g0) x (g180 - g90) / |..|  =  -a,
// and measured about n (right hand) the angle from g0 to the current gravity is +theta. With both
// vectors projected onto the plane normal to n,
//     roll = atan2(n . (g0p x gp), g0p . gp)
// which is about +90 deg at the +90 deg capture. The rotator reaches the IMU zero by moving -roll.
// The sign does not depend on how the board is mounted (Triton: upside down), on the accelerometer
// sign convention, or on the elevation of the optical axis during :95 (the earlier n = g0 x g90 was
// exact only with the axis horizontal: 14 deg off at 10 deg elevation).
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
static ImuV3 calC(const ImuCalibration& c) { return ImuV3{ (float)c.c[0], (float)c.c[1], (float)c.c[2] }; }
static ImuV3 sub3(const ImuV3& a, const ImuV3& b) { return ImuV3{ a.x - b.x, a.y - b.y, a.z - b.z }; }

static int16_t toI16(float v) {
    if (v > 32767.0f) return 32767;
    if (v < -32768.0f) return -32768;
    return (int16_t)lroundf(v);
}

// Signed angle from a to b about the unit vector n, both projected onto the plane normal to n.
static float angleAbout(const ImuV3& n, const ImuV3& a, const ImuV3& b) {
    const float an = dot(a, n), bn = dot(b, n);
    const ImuV3 ap{ a.x - an * n.x, a.y - an * n.y, a.z - an * n.z };
    const ImuV3 bp{ b.x - bn * n.x, b.y - bn * n.y, b.z - bn * n.z };
    return atan2f(dot(n, cross(ap, bp)), dot(ap, bp));
}

static bool oneG(const ImuV3& v) {
    const float l = norm(v);
    return l >= IMU_CAL_G_MIN_MG && l <= IMU_CAL_G_MAX_MG;
}

FwError imuMathCalibrate(const ImuV3& g0, const ImuV3& g90, const ImuV3& g180, ImuCalibration& out) {
    if (!oneG(g0) || !oneG(g90) || !oneG(g180)) return FW_ERR_CALIB_FAILED;

    const ImuV3 d1{ g90.x - g0.x, g90.y - g0.y, g90.z - g0.z };
    const ImuV3 d2{ g180.x - g90.x, g180.y - g90.y, g180.z - g90.z };
    // Radius of the gravity circle from the two 90 deg chords (chord = r * sqrt 2). A small circle
    // means gravity is nearly along the axis: optical axis near vertical, roll undefined.
    const float r = (norm(d1) + norm(d2)) * 0.5f * 0.70710678f;
    if (r < IMU_VERTICAL_SIN * norm(g0)) return FW_ERR_IMU_VERTICAL;

    const ImuV3 nr = cross(d1, d2);
    const float ln = norm(nr);
    if (ln <= 0.0f) return FW_ERR_CALIB_FAILED;
    const ImuV3 n{ nr.x / ln, nr.y / ln, nr.z / ln };
    if (fabsf(dot(n, g0)) / norm(g0) >= IMU_VERTICAL_COS) return FW_ERR_IMU_VERTICAL;

    // Circle centre = circumcentre of the three points; keep its part in the plane normal to n
    // (the part along n cannot be told apart from the elevation and does not matter for roll).
    const ImuV3 e = sub3(g180, g0), w = cross(d1, e);
    const float ww = dot(w, w);
    if (ww <= 0.0f) return FW_ERR_CALIB_FAILED;
    const ImuV3 t1 = cross(e, w), t2 = cross(w, d1);
    const float k1 = dot(d1, d1) / (2.0f * ww), k2 = dot(e, e) / (2.0f * ww);
    const ImuV3 ctr{ g0.x + k1 * t1.x + k2 * t2.x, g0.y + k1 * t1.y + k2 * t2.y, g0.z + k1 * t1.z + k2 * t2.z };
    const float cn = dot(ctr, n);
    const ImuV3 cp{ ctr.x - cn * n.x, ctr.y - cn * n.y, ctr.z - cn * n.z };
    if (norm(cp) > IMU_CAL_OFFSET_MAX_MG) return FW_ERR_CALIB_FAILED;

    // Both steps must be about +90 deg about n, seen from the centre (rotator slipped, IMU moved).
    const float lo = acosf(IMU_CAL_COS_MAX), hi = 3.14159265f - lo;   // 70 .. 110 deg
    const float s1 = angleAbout(n, sub3(g0, cp), sub3(g90, cp)), s2 = angleAbout(n, sub3(g90, cp), sub3(g180, cp));
    if (s1 < lo || s1 > hi || s2 < lo || s2 > hi) return FW_ERR_CALIB_FAILED;

    out.valid = 1;
    out.g0[0] = toI16(g0.x);
    out.g0[1] = toI16(g0.y);
    out.g0[2] = toI16(g0.z);
    out.n[0] = toI16(n.x * 10000.0f);
    out.n[1] = toI16(n.y * 10000.0f);
    out.n[2] = toI16(n.z * 10000.0f);
    out.c[0] = toI16(cp.x);
    out.c[1] = toI16(cp.y);
    out.c[2] = toI16(cp.z);
    return FW_OK;
}

bool imuMathCalValid(const ImuCalibration& c) {
    if (c.valid != 1) return false;
    const ImuV3 n{ (float)c.n[0], (float)c.n[1], (float)c.n[2] };
    const float ln = norm(n), lg = norm(calG0(c));
    return ln >= 9000.0f && ln <= 11000.0f && lg >= IMU_CAL_G_MIN_MG && lg <= IMU_CAL_G_MAX_MG &&
           norm(calC(c)) <= IMU_CAL_OFFSET_MAX_MG;
}

bool imuMathNearVertical(const ImuCalibration& c, const ImuV3& graw) {
    const ImuV3 g = sub3(graw, calC(c));
    const float lg = norm(g);
    if (lg <= 0.0f) return true;
    return fabsf(dot(calN(c), g)) / lg >= IMU_VERTICAL_COS;
}

int32_t imuMathRollCdeg(const ImuCalibration& c, const ImuV3& graw) {
    if (imuMathNearVertical(c, graw)) return IMU_MATH_INVALID;
    const ImuV3 n = calN(c);
    const ImuV3 g0 = sub3(calG0(c), calC(c));     // both measured from the circle centre
    const ImuV3 g = sub3(graw, calC(c));
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

int32_t imuMathTiltCdeg(const ImuCalibration& c, const ImuV3& graw) {
    const ImuV3 g = sub3(graw, calC(c));
    const ImuV3 n = calN(c);
    if (norm(g) <= 0.0f) return IMU_MATH_INVALID;
    const float t = atan2f(norm(cross(n, g)), dot(n, g));   // 0..pi
    return (int32_t)lroundf(t * RAD_TO_CDEG);
}
