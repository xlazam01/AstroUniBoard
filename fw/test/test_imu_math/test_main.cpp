// Host tests for imu_math.cpp (#32): pio test -e native -f test_imu_math
// Needs `+<imu_math.cpp>` in [env:native] build_src_filter and a host gcc.
//
// Model: optical axis a = +x (horizontal). Turning the camera by +theta turns gravity in chip axes by
// -theta about a: g(theta) = 1000 * (0, sin theta, cos theta) mg, g0 = g(0) = (0, 0, 1000).
#include <math.h>
#include <unity.h>

#include "imu_math.h"

static ImuV3 g(float deg) {
    const float r = deg * 3.14159265f / 180.0f;
    return ImuV3{ 0.0f, 1000.0f * sinf(r), 1000.0f * cosf(r) };
}

static ImuCalibration horizontalCal() {
    ImuCalibration c{};
    TEST_ASSERT_EQUAL(FW_OK, imuMathCalibrate(g(0), g(90), c));
    return c;
}

void setUp() {}
void tearDown() {}

static void test_calibrate_axis_and_record() {
    const ImuCalibration c = horizontalCal();
    TEST_ASSERT_EQUAL_UINT8(1, c.valid);
    TEST_ASSERT_EQUAL_INT16(0, c.g0[0]);
    TEST_ASSERT_EQUAL_INT16(0, c.g0[1]);
    TEST_ASSERT_EQUAL_INT16(1000, c.g0[2]);
    TEST_ASSERT_EQUAL_INT16(-10000, c.n[0]);   // n = g0 x g90 = -a
    TEST_ASSERT_INT_WITHIN(1, 0, c.n[1]);
    TEST_ASSERT_INT_WITHIN(1, 0, c.n[2]);
    TEST_ASSERT_TRUE(imuMathCalValid(c));
}

static void test_roll_sign_matches_mechanical_angle() {
    const ImuCalibration c = horizontalCal();
    TEST_ASSERT_INT_WITHIN(2, 0, imuMathRollCdeg(c, g(0)));
    TEST_ASSERT_INT_WITHIN(2, 9000, imuMathRollCdeg(c, g(90)));     // header contract
    TEST_ASSERT_INT_WITHIN(2, 4500, imuMathRollCdeg(c, g(45)));
    TEST_ASSERT_INT_WITHIN(2, -9000, imuMathRollCdeg(c, g(-90)));
    TEST_ASSERT_INT_WITHIN(2, 17000, imuMathRollCdeg(c, g(170)));
    TEST_ASSERT_INT_WITHIN(2, -17000, imuMathRollCdeg(c, g(-170)));
}

static void test_roll_ignores_component_along_axis() {
    const ImuCalibration c = horizontalCal();
    ImuV3 v = g(30);                       // scope raised: gravity gets a part along the axis
    v.x = 500.0f;
    TEST_ASSERT_INT_WITHIN(2, 3000, imuMathRollCdeg(c, v));
}

static void test_tilt() {
    const ImuCalibration c = horizontalCal();
    TEST_ASSERT_INT_WITHIN(2, 9000, imuMathTiltCdeg(c, g(37)));      // axis horizontal
    const ImuV3 up{ -1000.0f, 0.0f, 0.0f };                          // gravity along n
    TEST_ASSERT_INT_WITHIN(2, 0, imuMathTiltCdeg(c, up));
    const ImuV3 down{ 1000.0f, 0.0f, 0.0f };
    TEST_ASSERT_INT_WITHIN(2, 18000, imuMathTiltCdeg(c, down));
}

static void test_near_vertical() {
    const ImuCalibration c = horizontalCal();
    const ImuV3 steep{ 1000.0f, 0.0f, 100.0f };                      // ~6 deg from the axis
    TEST_ASSERT_TRUE(imuMathNearVertical(c, steep));
    TEST_ASSERT_EQUAL_INT32(IMU_MATH_INVALID, imuMathRollCdeg(c, steep));
    const ImuV3 ok{ 500.0f, 0.0f, 866.0f };                          // 60 deg from the axis
    TEST_ASSERT_FALSE(imuMathNearVertical(c, ok));
}

static void test_calibrate_rejects() {
    ImuCalibration c{};
    // optical axis vertical: g barely moves
    TEST_ASSERT_EQUAL(FW_ERR_IMU_VERTICAL,
                      imuMathCalibrate(ImuV3{ 1000, 0, 0 }, ImuV3{ 990, 0, 140 }, c));
    TEST_ASSERT_EQUAL_UINT8(0, c.valid);
    // only 45 / 135 deg apart: rotator slipped
    TEST_ASSERT_EQUAL(FW_ERR_CALIB_FAILED, imuMathCalibrate(g(0), g(45), c));
    TEST_ASSERT_EQUAL(FW_ERR_CALIB_FAILED, imuMathCalibrate(g(0), g(135), c));
    // not 1 g (moving / bad data)
    TEST_ASSERT_EQUAL(FW_ERR_CALIB_FAILED, imuMathCalibrate(ImuV3{ 0, 0, 500 }, ImuV3{ 0, 500, 0 }, c));
    TEST_ASSERT_EQUAL_UINT8(0, c.valid);
    // 75 and 105 deg are accepted
    TEST_ASSERT_EQUAL(FW_OK, imuMathCalibrate(g(0), g(75), c));
    TEST_ASSERT_EQUAL(FW_OK, imuMathCalibrate(g(0), g(105), c));
}

static void test_cal_valid() {
    ImuCalibration c{};
    TEST_ASSERT_FALSE(imuMathCalValid(c));
    c = horizontalCal();
    c.n[0] = -5000;
    TEST_ASSERT_FALSE(imuMathCalValid(c));
}

int main() {
    UNITY_BEGIN();
    RUN_TEST(test_calibrate_axis_and_record);
    RUN_TEST(test_roll_sign_matches_mechanical_angle);
    RUN_TEST(test_roll_ignores_component_along_axis);
    RUN_TEST(test_tilt);
    RUN_TEST(test_near_vertical);
    RUN_TEST(test_calibrate_rejects);
    RUN_TEST(test_cal_valid);
    return UNITY_END();
}
