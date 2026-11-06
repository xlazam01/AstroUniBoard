// TIM3 step generator (#23). One timer for both axes (Q12). Each update interrupt is one step of
// the active axis: Axis::isrStep() makes the pulse and schedules the next one with
// stepTimerSchedule(). The counter runs at STEP_TIMER_HZ without ARR preload, so a new period
// written in the ISR applies to the period that has just started (no accumulated ISR latency).
// TIM3 is 16 bit: periods longer than 65536 ticks (< 122 steps/s) are split into silent chunks.
#include <Arduino.h>
#include <HardwareTimer.h>

#include "step_timer.h"
#include "axis.h"

// Shared with axis.cpp (internal to the motion module, not part of the frozen header).
extern const uint32_t STEP_TIMER_HZ = 8000000UL;
void stepTimerSchedule(uint32_t ticks);

static HardwareTimer* tim = nullptr;
static Axis* volatile active = nullptr;
static volatile uint32_t waitTicks = 0;     // silent ticks still to wait before the next step

static inline void loadPeriod(uint32_t ticks) {
    if (ticks < 2) ticks = 2;
    if (ticks <= 65536UL) {
        TIM3->ARR = ticks - 1;
        waitTicks = 0;
    } else {
        TIM3->ARR = 32768UL - 1;
        waitTicks = ticks - 32768UL;
    }
}

// Called from Axis::isrStep() (ISR context) to set the time to the next step.
void stepTimerSchedule(uint32_t ticks) { loadPeriod(ticks); }

static void onUpdate() {
    if (waitTicks) {                // middle of a long period: no step yet
        loadPeriod(waitTicks);
        return;
    }
    Axis* a = active;
    if (a) a->isrStep();
}

void stepTimerBegin() {
    if (tim) return;
    tim = new HardwareTimer(TIM3);
    uint32_t clk = tim->getTimerClkFreq();      // 64 MHz
    uint32_t pre = clk / STEP_TIMER_HZ;
    if (pre < 1) pre = 1;
    tim->setPrescaleFactor(pre);
    tim->setOverflow(0xFFFF, TICK_FORMAT);
    tim->attachInterrupt(onUpdate);
    tim->setInterruptPriority(0, 0);
    tim->resume();                              // HAL start: clock, UIE, NVIC
    // From here on the counter is driven by registers only (CEN), never through HAL again.
    TIM3->CR1 &= ~(TIM_CR1_CEN | TIM_CR1_ARPE);
    TIM3->CR1 |= TIM_CR1_URS;                   // only counter overflow raises an update IRQ
    TIM3->SR = ~TIM_SR_UIF;
    active = nullptr;
}

bool stepTimerStart(Axis* axis) {
    if (!tim || !axis) return false;
    if (active && active != axis) return false;
    noInterrupts();
    TIM3->CR1 &= ~TIM_CR1_CEN;
    TIM3->CNT = 0;
    loadPeriod(160);                            // first interrupt after 20 us
    TIM3->SR = ~TIM_SR_UIF;
    active = axis;
    TIM3->CR1 |= TIM_CR1_CEN;
    interrupts();
    return true;
}

// Safe from the ISR and from the main loop.
void stepTimerStop() {
    TIM3->CR1 &= ~TIM_CR1_CEN;
    TIM3->SR = ~TIM_SR_UIF;
    waitTicks = 0;
    active = nullptr;
}

bool stepTimerRunning() { return active != nullptr; }
Axis* stepTimerAxis() { return active; }
