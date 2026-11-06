// Entry to the STM32 system bootloader (USB DFU) through a magic word and a reset.
// :31# stores the word in a TAMP backup register (survives a system reset) and resets; the next boot
// sees it in SystemClock_Config(), before clocks, USB or interrupts are set up, and jumps from that
// clean state. More robust than jumping from the running firmware (no peripherals to undo).
#pragma once

void rebootToDfu();      // does not return
void rebootNormal();     // does not return

// Called first thing in SystemClock_Config(). Jumps to the bootloader if the magic word is set.
extern "C" void dfuCheckAtBoot();
