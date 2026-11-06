// System clock setup (see clock_config.cpp)
#pragma once

// true if the system and USB clocks run from the 8 MHz crystal, false if the
// crystal did not start and the internal oscillators (HSI, HSI48 + CRS) are used
bool clockFromCrystal();
