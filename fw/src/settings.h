// Running settings (PROTOCOL.md "Settings and EEPROM"): setters change these values only;
// ":23#" writes the whole struct to the EEPROM (nv_store.h), ":24#" restores settingsDefaults().
// Owner: orchestrator. Defaults: PROTOCOL.md §4.1 / §4.2 (from fw-onstepx/src/Config.h).
// FROZEN layout (fw V2 first version). Adding a field: append it, raise SETTINGS_VERSION.
#pragma once
#include <stdint.h>

enum AxisId : uint8_t { AXIS_FOC = 0, AXIS_ROT = 1, AXIS_COUNT = 2 };

enum DriverMode : uint8_t { DRV_STEALTHCHOP = 0, DRV_SPREADCYCLE = 1 };

// Per-axis TMC2209 configuration (:47 / :48, Q13)
struct DriverConfig {
    uint16_t runMa;        // RMS run current, mA
    uint16_t holdMa;       // RMS standstill current, mA (<= runMa)
    uint16_t microsteps;   // 1, 2, 4, ..., 256
    uint8_t  mode;         // DriverMode
    uint8_t  stallThreshold;  // SGTHRS 0..255, 0 = stall detection off (:42)
    uint16_t idleOffS;     // driver disabled this many seconds after a move; 0 = stays at hold current
};

struct FocuserSettings {
    int32_t  maxPos;       // steps, 1..10000000
    uint32_t speed;        // steps/s, 1..20000
    uint32_t accel;        // steps/s^2, 1..100000
    uint32_t backlash;     // steps, 0..10000
    uint8_t  reverse;      // 0 / 1
    uint32_t stepSize;     // 0.01 um per step, 0 = unknown
};

struct RotatorSettings {
    int32_t  wrapMin;      // cdeg, -36000..0
    int32_t  wrapMax;      // cdeg, 0..36000
    uint32_t speed;        // cdeg/s, 10..3600
    uint32_t accel;        // cdeg/s^2, 10..36000
    uint8_t  reverse;      // 0 / 1
    uint32_t stepsPer1000Deg;  // microsteps per 1000 degrees, 1000..10000000
    uint8_t  bootHome;     // 0 / 1 (Q20)
    uint16_t homeTolCdeg;  // 5..500
};

enum EnvSelect : uint8_t { ENV_SEL_AUTO = 0, ENV_SEL_ONBOARD = 1, ENV_SEL_EXTERNAL = 2 };

// 2: + rotGuard (#75). Version 1 records are 76 bytes; their last two bytes are struct padding (0),
// so the new field starts after them and keeps its default when a version 1 record is loaded.
#define SETTINGS_VERSION 2
#define SETTINGS_V1_SIZE 76

struct Settings {
    DriverConfig    drv[AXIS_COUNT];
    FocuserSettings foc;
    RotatorSettings rot;
    uint8_t         envSelect;       // EnvSelect (:92 / :93)
    uint8_t         ledBrightness;   // 0..100 % (:43 / :44)
    uint8_t         padV1[2];        // padding of the version 1 layout: never use (old records hold 0 here)
    uint8_t         rotGuard;        // 0 / 1: refuse rotator moves while the angle is suspect (#75, :97 / :98)
};

#include <stddef.h>
static_assert(offsetof(Settings, padV1) + 2 == SETTINGS_V1_SIZE, "version 1 layout changed");
static_assert(offsetof(Settings, rotGuard) >= SETTINGS_V1_SIZE, "new fields go after the version 1 record");

extern Settings settings;            // the running values (defined in settings.cpp)

void settingsDefaults(Settings& s);  // compiled-in defaults
