// Native unit tests: move guard (#75 rotator angle suspect, #77 safe mode).
// Run: pio test -e native (needs a host gcc).
#include <unity.h>
#include "move_guard.h"

void setUp() {}
void tearDown() {}

static FwError g(bool safe, bool suspect, bool guard, bool absolute, int32_t delta = 0, int32_t lim = 1000) {
    return moveGuard(MoveGuardIn{safe, suspect, guard, absolute, delta, lim});
}

void test_trusted_position_always_allowed() {
    TEST_ASSERT_EQUAL(FW_OK, g(false, false, true, true));
    TEST_ASSERT_EQUAL(FW_OK, g(true, false, true, true));       // safe mode after a sync
    TEST_ASSERT_EQUAL(FW_OK, g(true, false, false, false, 99999));
}

void test_guard_on_refuses_every_move_while_suspect() {
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(false, true, true, true));
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(false, true, true, false, 1));
}

void test_guard_off_allows_moves_while_suspect() {
    TEST_ASSERT_EQUAL(FW_OK, g(false, true, false, true));
    TEST_ASSERT_EQUAL(FW_OK, g(false, true, false, false, 30000));
}

void test_safe_mode_refuses_absolute() {
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, false, true));
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, true, true));
}

void test_safe_mode_allows_small_jogs_both_ways() {
    TEST_ASSERT_EQUAL(FW_OK, g(true, true, true, false, 1000));
    TEST_ASSERT_EQUAL(FW_OK, g(true, true, true, false, -1000));
    TEST_ASSERT_EQUAL(FW_OK, g(true, true, false, false, 0));
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, true, false, 1001));
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, true, false, -1001));
}

void test_safe_mode_jog_no_overflow() {
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, false, false, INT32_MIN, SAFE_JOG_FOC_STEPS));
    TEST_ASSERT_EQUAL(FW_ERR_POS_UNVERIFIED, g(true, true, false, false, INT32_MAX, SAFE_JOG_FOC_STEPS));
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_trusted_position_always_allowed);
    RUN_TEST(test_guard_on_refuses_every_move_while_suspect);
    RUN_TEST(test_guard_off_allows_moves_while_suspect);
    RUN_TEST(test_safe_mode_refuses_absolute);
    RUN_TEST(test_safe_mode_allows_small_jogs_both_ways);
    RUN_TEST(test_safe_mode_jog_no_overflow);
    return UNITY_END();
}
