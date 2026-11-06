// Cable-wrap planner (#30, PROTOCOL.md §4.2), pure integer code without Arduino so it runs in the
// native unit tests (fw/test/test_wrap). All angles in centidegrees.
// Owner: session B. FROZEN interface.
#pragma once
#include <stdint.h>

// ":71" rule: target is 0..35999. Picks t, t-36000 or t+36000 inside [wrapMin, wrapMax]; when more
// than one fits, the one nearest `current`. Returns false (ERR,7) when none fits or the target is
// outside 0..35999.
bool wrapPlanTarget(int32_t current, int32_t target, int32_t wrapMin, int32_t wrapMax, int32_t* out);

// Signed mechanical angle -> reported 0..35999
int32_t wrapReported(int32_t signedCdeg);
