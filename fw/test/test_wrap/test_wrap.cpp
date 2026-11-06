// Native unit tests: cable-wrap planner (#30) and rotator arithmetic (#29, #32).
// Run: pio test -e native (needs a host gcc).
#include <unity.h>
#include "rotator_calc.h"
#include "rotator_wrap.h"

void setUp() {}
void tearDown() {}

static int32_t plan(int32_t cur, int32_t t, int32_t mn = -18000, int32_t mx = 18000) {
    int32_t out = 123456789;
    TEST_ASSERT_TRUE_MESSAGE(wrapPlanTarget(cur, t, mn, mx, &out), "expected a candidate");
    return out;
}

static void noPlan(int32_t cur, int32_t t, int32_t mn = -18000, int32_t mx = 18000) {
    int32_t out = 123456789;
    TEST_ASSERT_FALSE(wrapPlanTarget(cur, t, mn, mx, &out));
    TEST_ASSERT_EQUAL_INT32(123456789, out);   // untouched
}

// --- wrapPlanTarget: PROTOCOL.md §4.2 examples ---
void test_spec_0_to_270_goes_via_minus90() { TEST_ASSERT_EQUAL_INT32(-9000, plan(0, 27000)); }
void test_spec_170_to_190_goes_through_0() { TEST_ASSERT_EQUAL_INT32(-17000, plan(17000, 19000)); }
void test_go_home_always_unwinds() {
    TEST_ASSERT_EQUAL_INT32(0, plan(17000, 0));
    TEST_ASSERT_EQUAL_INT32(0, plan(-17000, 0));
}
void test_plain_targets() {
    TEST_ASSERT_EQUAL_INT32(9000, plan(0, 9000));
    TEST_ASSERT_EQUAL_INT32(0, plan(0, 0));
    TEST_ASSERT_EQUAL_INT32(-100, plan(0, 35900));
}

// --- boundaries of the default range ---
void test_boundary_18000_tie_from_0_prefers_positive() {
    // +18000 and -18000 are both inside and both 18000 away: equal |angle| -> the positive one
    TEST_ASSERT_EQUAL_INT32(18000, plan(0, 18000));
}
void test_boundary_18000_nearest_wins() {
    TEST_ASSERT_EQUAL_INT32(-18000, plan(-17000, 18000));
    TEST_ASSERT_EQUAL_INT32(18000, plan(17000, 18000));
}
void test_target_range_checked() {
    noPlan(0, -1);
    noPlan(0, 36000);
    noPlan(0, 99999);
    TEST_ASSERT_EQUAL_INT32(-1, plan(0, 35999));
}

// --- asymmetric ranges ---
void test_range_minus360_to_0() {
    TEST_ASSERT_EQUAL_INT32(-27000, plan(0, 9000, -36000, 0));
    TEST_ASSERT_EQUAL_INT32(-9000, plan(-1000, 27000, -36000, 0));
    // target 0: candidates 0 and -36000, nearest to the current angle
    TEST_ASSERT_EQUAL_INT32(0, plan(-1000, 0, -36000, 0));
    TEST_ASSERT_EQUAL_INT32(-36000, plan(-35000, 0, -36000, 0));
    // current -18000: 0 and -36000 equally far -> the less wound one (0)
    TEST_ASSERT_EQUAL_INT32(0, plan(-18000, 0, -36000, 0));
}
void test_range_0_to_360() {
    TEST_ASSERT_EQUAL_INT32(27000, plan(0, 27000, 0, 36000));
    TEST_ASSERT_EQUAL_INT32(36000, plan(35000, 0, 0, 36000));
    TEST_ASSERT_EQUAL_INT32(0, plan(1000, 0, 0, 36000));
    // tie at current 18000: 0 and 36000 -> the less wound one (0)
    TEST_ASSERT_EQUAL_INT32(0, plan(18000, 0, 0, 36000));
}
void test_full_range_three_candidates() {
    // -36000..36000 holds -36000, 0 and 36000 for target 0
    TEST_ASSERT_EQUAL_INT32(36000, plan(30000, 0, -36000, 36000));
    TEST_ASSERT_EQUAL_INT32(-36000, plan(-30000, 0, -36000, 36000));
    TEST_ASSERT_EQUAL_INT32(0, plan(5000, 0, -36000, 36000));
    TEST_ASSERT_EQUAL_INT32(-35000, plan(-20000, 1000, -36000, 36000));
}

// --- narrow ranges ---
void test_narrow_range_no_candidate() {
    noPlan(0, 18000, -9000, 9000);
    noPlan(0, 27000 - 1, -9000, 9000);
    TEST_ASSERT_EQUAL_INT32(-9000, plan(0, 27000, -9000, 9000));
    TEST_ASSERT_EQUAL_INT32(9000, plan(0, 9000, -9000, 9000));
}
void test_zero_width_range() {
    TEST_ASSERT_EQUAL_INT32(0, plan(0, 0, 0, 0));
    noPlan(0, 1, 0, 0);
    noPlan(0, 35999, 0, 0);
}
void test_null_out_pointer() { TEST_ASSERT_TRUE(wrapPlanTarget(0, 100, -18000, 18000, nullptr)); }

// --- wrapReported ---
void test_wrap_reported() {
    TEST_ASSERT_EQUAL_INT32(0, wrapReported(0));
    TEST_ASSERT_EQUAL_INT32(27000, wrapReported(-9000));
    TEST_ASSERT_EQUAL_INT32(18000, wrapReported(-18000));
    TEST_ASSERT_EQUAL_INT32(18000, wrapReported(18000));
    TEST_ASSERT_EQUAL_INT32(0, wrapReported(36000));
    TEST_ASSERT_EQUAL_INT32(0, wrapReported(-36000));
    TEST_ASSERT_EQUAL_INT32(35999, wrapReported(-1));
}

// --- steps <-> cdeg (default 88889 microsteps per 1000 deg) ---
void test_cdeg_to_steps() {
    TEST_ASSERT_EQUAL_INT32(8000, rotCdegToSteps(9000, 88889));      // 8000.01
    TEST_ASSERT_EQUAL_INT32(-8000, rotCdegToSteps(-9000, 88889));
    TEST_ASSERT_EQUAL_INT32(16000, rotCdegToSteps(18000, 88889));
    TEST_ASSERT_EQUAL_INT32(3600000, rotCdegToSteps(36000, 10000000));   // max spk, no overflow
    TEST_ASSERT_EQUAL_INT32(0, rotCdegToSteps(0, 88889));
}
void test_steps_to_cdeg() {
    TEST_ASSERT_EQUAL_INT32(9000, rotStepsToCdeg(8000, 88889));
    TEST_ASSERT_EQUAL_INT32(-9000, rotStepsToCdeg(-8000, 88889));
    TEST_ASSERT_EQUAL_INT32(0, rotStepsToCdeg(5, 0));
}
void test_round_trip_is_stable() {
    // cdeg -> steps -> cdeg -> steps gives the same step position (no drift over many moves)
    for (int32_t c = -36000; c <= 36000; c += 7) {
        const int32_t s = rotCdegToSteps(c, 88889);
        TEST_ASSERT_EQUAL_INT32(s, rotCdegToSteps(rotStepsToCdeg(s, 88889), 88889));
    }
}
void test_rates() {
    TEST_ASSERT_EQUAL_UINT32(1067, rotRateToSteps(1200, 88889));   // 12 deg/s
    TEST_ASSERT_EQUAL_UINT32(1, rotRateToSteps(10, 1000));         // never 0
}
void test_rescale_spk() {
    TEST_ASSERT_EQUAL_UINT32(177778, rotRescaleSpk(88889, 16, 32));
    TEST_ASSERT_EQUAL_UINT32(44445, rotRescaleSpk(88889, 16, 8));  // 44444.5 rounds up
    TEST_ASSERT_EQUAL_UINT32(88889, rotRescaleSpk(88889, 0, 8));
    TEST_ASSERT_EQUAL_UINT32(1, rotRescaleSpk(1, 256, 1));
}

// --- IMU home planning ---
static int32_t home(int32_t cur, int32_t roll, int32_t mn = -18000, int32_t mx = 18000) {
    int32_t out = 123456789;
    TEST_ASSERT_TRUE(rotPlanHome(cur, roll, mn, mx, &out));
    return out;
}
void test_home_simple() {
    TEST_ASSERT_EQUAL_INT32(0, home(9000, 9000));          // at +90: roll +90 -> back to 0
    TEST_ASSERT_EQUAL_INT32(0, home(-9000, -9000));
    TEST_ASSERT_EQUAL_INT32(100, home(0, -100));           // saved angle slightly off
}
void test_home_saved_angle_wrong_by_a_turn() {
    // range 0..36000, saved 35000 but the camera is 10 deg past the IMU zero: zero at 34000 or -2000
    // (outside) -> 34000; the candidate nearest 0 inside the range is chosen
    TEST_ASSERT_EQUAL_INT32(34000, home(35000, 1000, 0, 36000));
    // saved 35900, roll -100: zero at 36000 or 0 -> 0 (unwinds a full turn)
    TEST_ASSERT_EQUAL_INT32(0, home(35900, -100, 0, 36000));
}
void test_home_unwinds_near_180() {
    // from +170 deg with roll +170 deg: the zero is at 0
    TEST_ASSERT_EQUAL_INT32(0, home(17000, 17000));
    // roll -17000 from +17000: zero at 34000 / -2000 -> -2000
    TEST_ASSERT_EQUAL_INT32(-2000, home(17000, -17000));
}
void test_home_no_candidate() {
    int32_t out = 5;
    TEST_ASSERT_FALSE(rotPlanHome(0, 18000, -9000, 9000, &out));
    TEST_ASSERT_EQUAL_INT32(5, out);
}
void test_correction_is_clamped() {
    TEST_ASSERT_EQUAL_INT32(-30, rotPlanCorrection(10, 40, -18000, 18000));
    TEST_ASSERT_EQUAL_INT32(0, rotPlanCorrection(10, 40, 0, 36000));       // no full turn
    TEST_ASSERT_EQUAL_INT32(18000, rotPlanCorrection(17990, -50, -18000, 18000));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_spec_0_to_270_goes_via_minus90);
    RUN_TEST(test_spec_170_to_190_goes_through_0);
    RUN_TEST(test_go_home_always_unwinds);
    RUN_TEST(test_plain_targets);
    RUN_TEST(test_boundary_18000_tie_from_0_prefers_positive);
    RUN_TEST(test_boundary_18000_nearest_wins);
    RUN_TEST(test_target_range_checked);
    RUN_TEST(test_range_minus360_to_0);
    RUN_TEST(test_range_0_to_360);
    RUN_TEST(test_full_range_three_candidates);
    RUN_TEST(test_narrow_range_no_candidate);
    RUN_TEST(test_zero_width_range);
    RUN_TEST(test_null_out_pointer);
    RUN_TEST(test_wrap_reported);
    RUN_TEST(test_cdeg_to_steps);
    RUN_TEST(test_steps_to_cdeg);
    RUN_TEST(test_round_trip_is_stable);
    RUN_TEST(test_rates);
    RUN_TEST(test_rescale_spk);
    RUN_TEST(test_home_simple);
    RUN_TEST(test_home_saved_angle_wrong_by_a_turn);
    RUN_TEST(test_home_unwinds_near_180);
    RUN_TEST(test_home_no_candidate);
    RUN_TEST(test_correction_is_clamped);
    return UNITY_END();
}
