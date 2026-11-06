// Cable-wrap planner (#30, PROTOCOL.md §4.2). Pure integer code, no Arduino: runs in the native
// unit tests (fw/test/test_wrap).
#include "rotator_wrap.h"

static int64_t absDist(int64_t a, int64_t b) { return a > b ? a - b : b - a; }
static int64_t abs64(int64_t a) { return a < 0 ? -a : a; }

// Candidates t - 36000, t, t + 36000 inside [wrapMin, wrapMax]; the one nearest `current`.
// Tie-break (two candidates at the same distance, e.g. current 0, target 18000 with the range
// -18000..18000): the less wound one (smaller |angle|); if that ties too, the larger (positive) one.
// The widest range -36000..36000 can hold all three candidates (target 0: -36000, 0, 36000).
bool wrapPlanTarget(int32_t current, int32_t target, int32_t wrapMin, int32_t wrapMax, int32_t* out) {
    if (target < 0 || target > 35999 || wrapMin > wrapMax) return false;
    bool found = false;
    int64_t best = 0;
    for (int k = -1; k <= 1; k++) {
        const int64_t c = (int64_t)target + (int64_t)k * 36000;
        if (c < wrapMin || c > wrapMax) continue;
        if (!found) { best = c; found = true; continue; }
        const int64_t dc = absDist(c, current), db = absDist(best, current);
        if (dc < db || (dc == db && (abs64(c) < abs64(best) || (abs64(c) == abs64(best) && c > best))))
            best = c;
    }
    if (found && out) *out = (int32_t)best;
    return found;
}

// Signed mechanical angle -> reported 0..35999
int32_t wrapReported(int32_t a) {
    a %= 36000;
    return a < 0 ? a + 36000 : a;
}
