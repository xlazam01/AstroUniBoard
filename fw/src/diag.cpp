#include <Arduino.h>

#include "diag.h"

// TAMP->BKP0R is the DFU magic word (bootloader.cpp); backup access is enabled by dfuCheckAtBoot().
static uint32_t resetFlags;
static uint8_t  lastMark;
static uint32_t lastNote;

void diagBoot() {
    resetFlags = RCC->CSR & 0xFF000000u;
    RCC->CSR |= RCC_CSR_RMVF;            // clear the flags for the next reset
    lastMark = (uint8_t)TAMP->BKP1R;
    TAMP->BKP1R = DIAG_NONE;
    lastNote = TAMP->BKP2R;
    TAMP->BKP2R = 0;

    // IWDG: LSI 32 kHz / 64 = 500 Hz, reload 2000 -> 4 s. A software-started IWDG stops on reset,
    // so the system bootloader entered through :31# is not reset by it.
    IWDG->KR = 0xCCCC;
    IWDG->KR = 0x5555;
    IWDG->PR = 4;
    IWDG->RLR = 2000;
    while (IWDG->SR != 0) { }
    IWDG->KR = 0xAAAA;
}

void diagFeed() { IWDG->KR = 0xAAAA; }

void diagMark(DiagMark m) { TAMP->BKP1R = m; }

bool diagWatchdogReset() { return (resetFlags & RCC_CSR_IWDGRSTF) != 0; }

uint32_t diagResetFlags() { return resetFlags >> 24; }

uint8_t diagLastMark() { return lastMark; }

void diagNote(uint32_t v) { TAMP->BKP2R = v; }

void diagNoteOr(uint32_t v) { TAMP->BKP2R |= v; }

uint32_t diagLastNote() { return lastNote; }
