#include <Arduino.h>
#include <stdio.h>

#include "bootloader.h"
#include "config.h"
#include "diag.h"
#include "error_codes.h"
#include "env_sht31.h"
#include "protocol.h"

// One write per reply, so the host never sees half a reply.
static void reply(const char* s, int n) { PROTOCOL_SERIAL.write((const uint8_t*)s, (size_t)n); }

static void replyError(FwError code) {
    char out[12];
    reply(out, snprintf(out, sizeof(out), "ERR,%d#", (int)code));
}

void Protocol::process() {
    while (PROTOCOL_SERIAL.available() > 0) {
        const char c = (char)PROTOCOL_SERIAL.read();
        if (c == ':') {
            inCommand = true;
            overflow = false;
            len = 0;
        } else if (c == '#') {
            if (!inCommand) continue;
            buf[len] = '\0';
            if (overflow) replyError(FW_ERR_BAD_FORMAT);
            else handleCommand();
            inCommand = false;
        } else if (inCommand) {
            if (len < sizeof(buf) - 1) buf[len++] = c;
            else overflow = true;
        }
        // bytes outside ":...#" are noise and are ignored without a reply
    }
}

void Protocol::handleCommand() {
    if (len < 2 || !isDigit(buf[0]) || !isDigit(buf[1])) { replyError(FW_ERR_BAD_FORMAT); return; }
    const int cmd = (buf[0] - '0') * 10 + (buf[1] - '0');
    const char* params = &buf[2];

    switch (cmd) {
        case 22:  // reboot
        case 31:  // reboot to the USB DFU bootloader (magic word + reset)
            reply("OK#", 3);
            PROTOCOL_SERIAL.flush();
            delay(50);   // let the USB host fetch the reply before the device drops off the bus
            if (cmd == 31) rebootToDfu();
            rebootNormal();
            break;

        case 25:  // ping
            reply("OK#", 3);
            break;

        case 90: {  // TEMPORARY (until the env block 90-99 is specified, issue #10): both SHT31 sensors
                    // "W,<st>,<t 0.1C>,<rh 0.1%>,<dew 0.1C>,<st>,<t>,<rh>,<dew>,<power cycles>#"
                    // first group on-board (0x45), second external J9 (0x44); st = ShtStatus,
                    // values are the last good reading and valid only while st == 0.
            if (params[0] != '\0') { replyError(FW_ERR_BAD_PARAM); break; }
            const ShtReading& a = envReading(ENV_ONBOARD);
            const ShtReading& b = envReading(ENV_EXTERNAL);
            char out[64];
            reply(out, snprintf(out, sizeof(out), "W,%u,%d,%u,%d,%u,%d,%u,%d,%u#",
                                (unsigned)a.status, (int)a.tDeciC, (unsigned)a.rhDeci, (int)a.dewDeciC,
                                (unsigned)b.status, (int)b.tDeciC, (unsigned)b.rhDeci, (int)b.dewDeciC,
                                (unsigned)envPowerCycles()));
            break;
        }

        case 99: {  // TEMPORARY bring-up diagnostics:
                    // "D,<reset flags RCC_CSR>>24 hex>,<last marker>,<safe mode>,<last note hex>#"
            char out[40];
            reply(out, snprintf(out, sizeof(out), "D,%02lX,%u,%u,%lX#", (unsigned long)diagResetFlags(),
                                (unsigned)diagLastMark(), diagWatchdogReset() ? 1u : 0u,
                                (unsigned long)diagLastNote()));
            break;
        }

        default:
            replyError(FW_ERR_UNKNOWN_CMD);
            break;
    }
}
