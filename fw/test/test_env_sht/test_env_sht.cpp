// Native tests: SHT3x / SHT4x detection and recovery in env_sht31.cpp (#90), against fake sensors on a
// mock Wire bus. On-board U8 (0x45) runs from PB9 (SHT_PWR); J9 (0x44) from the plain 3V3 rail.
// Run: pio test -e native (needs a host gcc).
#include <stdio.h>
#include <unity.h>

#include "../../src/env_sht31.cpp"   // the driver itself, with this folder's Arduino.h / Wire.h

uint32_t g_ms = 0;
int      g_pwr = 0;
Settings settings;
void diagMark(DiagMark) {}
void diagNoteOr(uint32_t) {}
void i2cRestart() {}

enum Kind { NONE, SHT3X, SHT4X };
struct Dev {
    Kind     kind = NONE;
    bool     switched = false;     // powered from PB9 (on-board) or the plain 3V3 rail (J9)
    bool     hung = false;         // ACKs writes, never has data, until a soft reset or power loss
    bool     ackInvalid = false;   // SHT4x: ACK an unknown command instead of NACKing it
    uint16_t rawT = 26214;         // 25.0 C
    uint16_t rawRH = 0;
    int      pending = 0;          // 0 none, 1 measurement, 2 serial number
    int      resets = 0;
    bool powered() const { return kind != NONE && (!switched || g_pwr); }
};
static Dev onb, ext;
static Dev* devAt(uint8_t a) { return a == 0x45 ? &onb : a == 0x44 ? &ext : nullptr; }

TwoWire Wire;
static uint8_t txAddr, txBuf[4], txLen, rxBuf[6], rxLen, rxPos;

void TwoWire::beginTransmission(uint8_t a) { txAddr = a; txLen = 0; }
void TwoWire::write(uint8_t b) { if (txLen < 4) txBuf[txLen++] = b; }

uint8_t TwoWire::endTransmission() {
    Dev* d = devAt(txAddr);
    if (!d || !d->powered()) return 2;   // address NACK
    if (d->kind == SHT3X) {              // two-byte commands; anything else is ACKed and ignored
        if (txLen == 2 && txBuf[0] == 0x24 && txBuf[1] == 0x00) { if (!d->hung) d->pending = 1; }
        else if (txLen == 2 && txBuf[0] == 0x30 && txBuf[1] == 0xA2) { d->hung = false; d->pending = 0; d->resets++; }
        else d->pending = 0;
        return 0;
    }
    if (txLen == 1 && txBuf[0] == 0xFD) { if (!d->hung) d->pending = 1; return 0; }
    if (txLen == 1 && txBuf[0] == 0x89) { if (!d->hung) d->pending = 2; return 0; }
    if (txLen == 1 && txBuf[0] == 0x94) { d->hung = false; d->pending = 0; d->resets++; return 0; }
    d->pending = 0;
    return d->ackInvalid ? 0 : 3;   // data NACK on an unknown command
}

uint8_t TwoWire::requestFrom(uint8_t a, uint8_t n) {
    Dev* d = devAt(a);
    rxLen = rxPos = 0;
    if (!d || !d->powered() || d->pending == 0 || n != 6) return 0;   // no data: read header NACKed
    const uint16_t w0 = d->pending == 2 ? 0x1234 : d->rawT;
    const uint16_t w1 = d->pending == 2 ? 0x5678 : d->rawRH;
    d->pending = 0;
    rxBuf[0] = (uint8_t)(w0 >> 8); rxBuf[1] = (uint8_t)w0; rxBuf[2] = crc8(rxBuf, 2);
    rxBuf[3] = (uint8_t)(w1 >> 8); rxBuf[4] = (uint8_t)w1; rxBuf[5] = crc8(rxBuf + 3, 2);
    rxLen = 6;
    return 6;
}
int TwoWire::read() { return rxPos < rxLen ? rxBuf[rxPos++] : -1; }

static void run(uint32_t ms) {
    for (uint32_t i = 0; i < ms; i++) {
        g_ms++;
        envUpdate();
        if (!g_pwr && onb.switched) { onb.hung = false; onb.pending = 0; }   // power loss clears a hang
    }
}

static const uint16_t RH50[3] = { 0, 32768, 29360 };   // 50 %RH raw: none, SHT3x, SHT4x
static uint16_t pc0;
static uint16_t cycles() { return (uint16_t)(envPowerCycles() - pc0); }

static void boot(Kind o, Kind x) {
    onb = Dev(); ext = Dev();
    onb.kind = o; onb.switched = true; onb.rawRH = RH50[o];
    ext.kind = x; ext.rawRH = RH50[x];
    for (uint8_t i = 0; i < ENV_SENSOR_COUNT; i++) { seen[i] = false; fails[i] = 0; }   // as after a reset
    settings.envSelect = ENV_SEL_AUTO;
    pc0 = envPowerCycles();
    envBegin();
}

static void expectOk(EnvSensor s, const char* what) {
    char msg[96];
    const ShtReading& r = envReading(s);
    snprintf(msg, sizeof msg, "%s: status %d, t %d, rh %u", what, r.status, r.tDeciC, r.rhDeci);
    TEST_ASSERT_EQUAL_MESSAGE(SHT_OK, r.status, msg);
    TEST_ASSERT_EQUAL_MESSAGE(250, r.tDeciC, msg);
    TEST_ASSERT_INT_WITHIN_MESSAGE(1, 500, r.rhDeci, msg);
}

void setUp() {}
void tearDown() {}

// Every on-board / J9 combination, with SHT4x parts that NACK or ACK an SHT3x command.
void test_detect_all_combinations() {
    const char* names[3] = { "none", "SHT3x", "SHT4x" };
    for (int a = 0; a < 3; a++)
        for (int b = 0; b < 3; b++)
            for (int inv = 0; inv < 2; inv++) {
                char what[64];
                snprintf(what, sizeof what, "onb %s, ext %s%s", names[a], names[b], inv ? ", ackInvalid" : "");
                boot((Kind)a, (Kind)b);
                onb.ackInvalid = ext.ackInvalid = inv;
                run(10000);
                if (a != NONE) expectOk(ENV_ONBOARD, what);
                else TEST_ASSERT_EQUAL_MESSAGE(SHT_NO_ACK, envReading(ENV_ONBOARD).status, what);
                if (b != NONE) expectOk(ENV_EXTERNAL, what);
                else TEST_ASSERT_EQUAL_MESSAGE(SHT_NO_ACK, envReading(ENV_EXTERNAL).status, what);
                TEST_ASSERT_EQUAL_MESSAGE(0, cycles(), what);
            }
}

void test_first_reading_in_first_cycle() {
    boot(SHT4X, SHT4X);
    run(100);   // power-up 2 ms + probe 10 ms + measurement 20 ms
    expectOk(ENV_ONBOARD, "onb");
    expectOk(ENV_EXTERNAL, "ext");
}

void test_swap_on_j9_detected_within_two_cycles() {
    boot(SHT4X, SHT3X);
    run(5000);
    expectOk(ENV_EXTERNAL, "SHT31 module");
    ext.kind = SHT4X; ext.rawRH = RH50[SHT4X];
    run(4100);
    expectOk(ENV_EXTERNAL, "swapped to SHT40 tab");
    ext.kind = SHT3X; ext.rawRH = RH50[SHT3X];
    run(4100);
    expectOk(ENV_EXTERNAL, "swapped back");
}

// J9 is not on SHT_VDD: a hung external sensor gets a soft reset, U8 is not power-cycled.
void test_hung_external_soft_reset_no_power_cycle() {
    for (int k = SHT3X; k <= SHT4X; k++) {
        boot(SHT4X, (Kind)k);
        run(5000);
        ext.hung = true;
        run(8000);
        TEST_ASSERT_EQUAL(1, ext.resets);
        TEST_ASSERT_EQUAL(0, cycles());
        run(4100);
        expectOk(ENV_EXTERNAL, "ext recovered");
        expectOk(ENV_ONBOARD, "onb untouched");
    }
}

void test_hung_onboard_power_cycle() {
    for (int k = SHT3X; k <= SHT4X; k++) {
        boot((Kind)k, SHT4X);
        run(5000);
        onb.hung = true;
        run(8000);
        TEST_ASSERT_EQUAL(1, cycles());
        TEST_ASSERT_EQUAL(0, ext.resets);
        run(4100);
        expectOk(ENV_ONBOARD, "onb recovered");
    }
}

// A J9 sensor unplugged after it answered must not power-cycle U8 over and over.
void test_unplugged_external_no_power_cycles() {
    boot(SHT4X, SHT4X);
    run(5000);
    ext.kind = NONE;
    run(60000);
    TEST_ASSERT_EQUAL(0, cycles());
    TEST_ASSERT_EQUAL(SHT_NO_ACK, envReading(ENV_EXTERNAL).status);
    expectOk(ENV_ONBOARD, "onb");
}

void test_sht4x_rh_clamped() {
    boot(SHT4X, SHT4X);
    ext.rawRH = 65535;   // 119 %RH before the clamp
    onb.rawRH = 0;       // -6 %RH before the clamp
    run(3000);
    TEST_ASSERT_EQUAL(1000, envReading(ENV_EXTERNAL).rhDeci);
    TEST_ASSERT_EQUAL(0, envReading(ENV_ONBOARD).rhDeci);
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_detect_all_combinations);
    RUN_TEST(test_first_reading_in_first_cycle);
    RUN_TEST(test_swap_on_j9_detected_within_two_cycles);
    RUN_TEST(test_hung_external_soft_reset_no_power_cycle);
    RUN_TEST(test_hung_onboard_power_cycle);
    RUN_TEST(test_unplugged_external_no_power_cycles);
    RUN_TEST(test_sht4x_rh_clamped);
    return UNITY_END();
}
