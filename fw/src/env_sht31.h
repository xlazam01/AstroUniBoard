// SHT31 temperature / humidity: on-board sensor (0x45) and optional external sensor on J9 (0x44).
// Non-blocking: envUpdate() runs from the superloop, both sensors are measured every ENV_PERIOD_MS.
// The firmware only reports; temperature compensation is host-side (V2_PLAN Q21).
#pragma once
#include <stdint.h>

enum ShtStatus : uint8_t {
    SHT_OK      = 0,   // last measurement good
    SHT_NO_ACK  = 1,   // sensor did not answer (absent, unpowered or bus fault)
    SHT_BAD_CRC = 2,   // answered, but the data failed the CRC check
    SHT_NO_DATA = 3    // no measurement finished yet since boot
};

struct ShtReading {
    ShtStatus status;   // result of the last measurement attempt
    int16_t  tDeciC;    // last good values; meaningful only while status == SHT_OK
    uint16_t rhDeci;    // 0.1 %RH
    int16_t  dewDeciC;  // dew point (Magnus formula, over water)
};

enum EnvSensor : uint8_t { ENV_ONBOARD = 0, ENV_EXTERNAL = 1, ENV_SENSOR_COUNT = 2 };

void envBegin();
void envUpdate();
const ShtReading& envReading(EnvSensor s);
uint16_t envPowerCycles();   // supply power cycles done to recover the on-board sensor

// :90 — the sensor chosen by settings.envSelect (auto prefers the external J9 sensor, PROTOCOL.md §4.3).
// Returns ENV_ONBOARD / ENV_EXTERNAL, or -1 when the chosen sensor has no valid reading (ERR,14).
// Owner of this file: session C (#34).
int8_t envSelected();
