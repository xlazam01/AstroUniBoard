// Status and activity LEDs (#20), active LOW on PB12 / PB4.
// Neither pin has a usable PWM channel (PB4 is on TIM3, the step timer), so dimming is a software PWM
// from TIM16 as on OpenWheel: 64 steps at 12.8 kHz = 200 Hz, gamma-2 brightness. At 100 % and 0 % the
// timer is stopped and the pins are driven directly. The tick ISR only writes GPIOB->BSRR.
//
// Patterns (ledsUpdate):
//   status LED    on; blinks at 5 Hz while a sticky error is set
//   activity LED  heartbeat (60 ms flash) at 1 Hz when idle, 4 Hz in safe mode;
//                 blinks at 10 Hz while an axis moves, halts, homes or calibrates
#include <Arduino.h>
#include <HardwareTimer.h>

#include "config.h"
#include "leds.h"
#include "settings.h"
#include "status.h"

static const uint32_t PWM_TICK_HZ = 12800;
static const uint8_t  PWM_STEPS   = 64;

static HardwareTimer* pwmTimer = nullptr;
static volatile uint32_t onMask = 0;          // GPIOB bits of the LEDs that should be lit
static volatile uint8_t  duty = PWM_STEPS;    // lit ticks per period
static volatile uint8_t  phase = 0;
static uint32_t bitStatus = 0, bitAct = 0;    // GPIOB pin masks

static void pwmTick() {
    const uint32_t all = bitStatus | bitAct;
    const uint32_t lit = (phase < duty) ? onMask : 0;
    // active low: reset (BR, upper half) = on, set (BS, lower half) = off
    GPIOB->BSRR = (lit << 16) | (all & ~lit);
    phase = (uint8_t)((phase + 1) % PWM_STEPS);
}

// Pins follow onMask directly (timer stopped): 100 % = on as requested, 0 % = always off.
static void driveDirect() {
    const bool off = settings.ledBrightness == 0;
    digitalWrite(PIN_STATUS_LED, (!off && (onMask & bitStatus)) ? LOW : HIGH);
    digitalWrite(PIN_ACT_LED, (!off && (onMask & bitAct)) ? LOW : HIGH);
}

static void ledSet(uint32_t bit, bool on) {
    const uint32_t m = on ? (onMask | bit) : (onMask & ~bit);
    if (m == onMask) return;
    onMask = m;
    if (settings.ledBrightness == 0 || settings.ledBrightness >= 100) driveDirect();
}

void ledsBegin() {
    bitStatus = digitalPinToBitMask(PIN_STATUS_LED);
    bitAct = digitalPinToBitMask(PIN_ACT_LED);
    pinMode(PIN_STATUS_LED, OUTPUT);
    pinMode(PIN_ACT_LED, OUTPUT);
    onMask = bitStatus;   // status LED on from power-up
    driveDirect();
    pwmTimer = new HardwareTimer(TIM16);
    pwmTimer->setOverflow(PWM_TICK_HZ, HERTZ_FORMAT);
    pwmTimer->attachInterrupt(pwmTick);
    ledsSetBrightness(settings.ledBrightness);
}

void ledsUpdate() {
    const uint32_t now = millis();
    // statusError() / statusAnyBusy() instead of statusGet(): the same information (state 2..5 =
    // busy) without assembling the whole status record on every loop pass
    const bool error = statusError() != FW_OK;
    ledSet(bitStatus, error ? ((now / 100) & 1) == 0 : true);

    bool act;
    if (statusAnyBusy()) {
        act = ((now / 50) & 1) == 0;                              // 10 Hz
    } else {
        const uint32_t period = statusSafeMode() ? 250 : 1000;    // 4 Hz / 1 Hz heartbeat
        act = (now % period) < 60;
    }
    ledSet(bitAct, act);
}

void ledsSetBrightness(uint8_t percent) {
    if (percent > 100) percent = 100;
    settings.ledBrightness = percent;
    // gamma 2: equal steps look equally bright; anything above 0 % gets at least one tick
    uint32_t d = ((uint32_t)percent * percent * PWM_STEPS + 5000) / 10000;
    if (percent > 0 && d == 0) d = 1;
    duty = (uint8_t)d;
    if (pwmTimer == nullptr) return;
    if (percent == 0 || percent == 100) {
        pwmTimer->pause();
        driveDirect();
    } else {
        pwmTimer->resume();
    }
}

uint8_t ledsBrightness() { return settings.ledBrightness; }
