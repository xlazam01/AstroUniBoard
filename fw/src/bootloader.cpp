#include <Arduino.h>

#include "bootloader.h"

static const uint32_t DFU_MAGIC     = 0xDF0B007Bu;
static const uint32_t SYSTEM_MEMORY = 0x1FFF0000u;   // STM32G0 system bootloader (AN2606)

static void backupAccess() {
    __HAL_RCC_PWR_CLK_ENABLE();
    __HAL_RCC_RTCAPB_CLK_ENABLE();   // TAMP registers are on the RTC APB clock
    HAL_PWR_EnableBkUpAccess();
}

void rebootToDfu() {
    backupAccess();
    TAMP->BKP0R = DFU_MAGIC;
    __DSB();
    NVIC_SystemReset();
}

void rebootNormal() {
    NVIC_SystemReset();
}

extern "C" void dfuCheckAtBoot() {
    backupAccess();
    if (TAMP->BKP0R != DFU_MAGIC) return;
    TAMP->BKP0R = 0;   // one shot: the next reset (after the upload) starts the firmware again

    // Only HAL_Init() has run: undo SysTick and pending interrupts, then hand over.
    __disable_irq();
    SysTick->CTRL = 0;
    SysTick->LOAD = 0;
    SysTick->VAL = 0;
    NVIC->ICER[0] = 0xFFFFFFFF;
    NVIC->ICPR[0] = 0xFFFFFFFF;

    __HAL_RCC_SYSCFG_CLK_ENABLE();   // map system flash at 0x00000000
    SYSCFG->CFGR1 = (SYSCFG->CFGR1 & ~SYSCFG_CFGR1_MEM_MODE) | (0x01u << SYSCFG_CFGR1_MEM_MODE_Pos);
    __DSB();
    __ISB();

    typedef void (*Entry)(void);
    const Entry entry = (Entry)(*(volatile uint32_t*)(SYSTEM_MEMORY + 4));
    __set_MSP(*(volatile uint32_t*)SYSTEM_MEMORY);
    entry();
}
