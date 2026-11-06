#include <string.h>
#include "settings.h"

Settings settings;

void settingsDefaults(Settings& s) {
    memset(&s, 0, sizeof(s));

    // Focuser (PROTOCOL.md §4.1)
    s.drv[AXIS_FOC] = DriverConfig{900, 450, 4, DRV_STEALTHCHOP, 0, 30};
    s.foc.maxPos   = 8100;
    s.foc.speed    = 900;
    s.foc.accel    = 2000;
    s.foc.backlash = 0;
    s.foc.reverse  = 0;
    s.foc.stepSize = 111;

    // Rotator (PROTOCOL.md §4.2)
    s.drv[AXIS_ROT] = DriverConfig{650, 325, 16, DRV_STEALTHCHOP, 0, 0};
    s.rot.wrapMin = -18000;
    s.rot.wrapMax = 18000;
    s.rot.speed   = 1200;
    s.rot.accel   = 2400;
    s.rot.reverse = 0;
    s.rot.stepsPer1000Deg = 88889;
    s.rot.bootHome = 0;
    s.rot.homeTolCdeg = 50;

    s.envSelect = ENV_SEL_AUTO;
    s.ledBrightness = 100;
}
