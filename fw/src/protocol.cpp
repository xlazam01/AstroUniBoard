#include <Arduino.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "config.h"
#include "protocol.h"
#include "settings.h"

// One write per reply, so the host never sees half a reply.
void protoReply(const char* s, int n) {
    if (n <= 0) return;
    PROTOCOL_SERIAL.write((const uint8_t*)s, (size_t)n);
}

static bool identified = false;   // cleared at reset and when a host opens the port (#58)

void protoSetIdentified() { identified = true; }

// ---- port-open detection (identify gate, #58; same code as OpenWheel#16) ----
// A host's CDC driver sends SET_LINE_CODING / SET_CONTROL_LINE_STATE when a program opens the port
// (Windows usbser also with DTR = false) and when it changes the port settings. The stm32duino CDC
// code has no hook for them, so the USB core's setup-stage entry is wrapped at link time
// (-Wl,--wrap=USBD_LL_SetupStage in platformio.ini). Runs in the USB interrupt: only sets a flag;
// Protocol::process() locks again before it reads the next bytes.
#include "usbd_def.h"
static volatile bool usbPortOpened = false;

extern "C" USBD_StatusTypeDef __real_USBD_LL_SetupStage(USBD_HandleTypeDef* pdev, uint8_t* psetup);
extern "C" USBD_StatusTypeDef __wrap_USBD_LL_SetupStage(USBD_HandleTypeDef* pdev, uint8_t* psetup) {
    // bmRequestType 0x21 = host-to-device, class, interface
    // bRequest 0x20 = SET_LINE_CODING, 0x22 = SET_CONTROL_LINE_STATE
    if (psetup[0] == 0x21 && (psetup[1] == 0x20 || psetup[1] == 0x22)) usbPortOpened = true;
    return __real_USBD_LL_SetupStage(pdev, psetup);
}

void protoOk() { protoReply("OK#", 3); }

void protoError(FwError code) {
    char out[12];
    protoReply(out, snprintf(out, sizeof(out), "ERR,%d#", (int)code));
}

void protoInt(int32_t v) {
    char out[16];
    protoReply(out, snprintf(out, sizeof(out), "%ld#", (long)v));
}

void protoPrintf(const char* fmt, ...) {
    char out[96];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(out, sizeof(out), fmt, ap);
    va_end(ap);
    if (n >= (int)sizeof(out)) n = sizeof(out) - 1;
    protoReply(out, n);
}

bool protoSplit(char* p, ProtoArgs& a) {
    a.n = 0;
    if (*p == ' ' || *p == ',') p++;
    if (*p == '\0') return true;
    for (;;) {
        if (a.n >= PROTO_MAX_ARGS) return false;
        a.v[a.n++] = p;
        char* c = strchr(p, ',');
        if (!c) break;
        *c = '\0';
        p = c + 1;
    }
    for (uint8_t i = 0; i < a.n; i++)
        if (a.v[i][0] == '\0') return false;
    return true;
}

bool protoInt32(const char* s, int32_t& out) {
    const char* p = s;
    bool neg = false;
    if (*p == '+' || *p == '-') { neg = (*p == '-'); p++; }
    if (*p == '\0') return false;
    int64_t v = 0;
    for (; *p; p++) {
        if (*p < '0' || *p > '9') return false;
        v = v * 10 + (*p - '0');
        if (v > 2147483648LL) return false;
    }
    if (neg) v = -v;
    if (v > INT32_MAX || v < INT32_MIN) return false;
    out = (int32_t)v;
    return true;
}

int protoAxis(const char* s) {
    if (s[0] == '\0' || s[1] != '\0') return -1;
    if (s[0] == 'F' || s[0] == 'f') return AXIS_FOC;
    if (s[0] == 'R' || s[0] == 'r') return AXIS_ROT;
    return -1;
}

void Protocol::process() {
    // A host (re)opened or reconfigured the port: lock until it identifies again. Checked before
    // reading the bytes, because the host sends the class requests before its first command.
    if (usbPortOpened) {
        usbPortOpened = false;
        identified = false;
    }
    while (PROTOCOL_SERIAL.available() > 0) {
        const char c = (char)PROTOCOL_SERIAL.read();
        if (c == ':') {
            inCommand = true;
            overflow = false;
            len = 0;
        } else if (c == '#') {
            if (!inCommand) continue;
            buf[len] = '\0';
            if (overflow) protoError(FW_ERR_BAD_FORMAT);
            else handleCommand();
            inCommand = false;
        } else if (inCommand) {
            if (len < PROTO_MAX_LINE) buf[len++] = c;
            else overflow = true;
        }
        // bytes outside ":...#" are noise and are ignored without a reply
    }
}

void Protocol::handleCommand() {
    if (len < 2 || !isDigit(buf[0]) || !isDigit(buf[1])) { protoError(FW_ERR_BAD_FORMAT); return; }
    const uint8_t cmd = (uint8_t)((buf[0] - '0') * 10 + (buf[1] - '0'));
    char* params = &buf[2];

    // A device on the shared test PID 1209:0001 must be identified before it acts on anything.
    if (!identified && cmd != 25 && cmd != 32) { protoError(FW_ERR_NOT_IDENTIFIED); return; }

    if (cmd >= 90)      protocolEnv(cmd, params);
    else if (cmd >= 70) protocolRotator(cmd, params);
    else if (cmd >= 50) protocolFocuser(cmd, params);
    else if (cmd >= 20) protocolCommon(cmd, params);
    else                protoError(FW_ERR_UNKNOWN_CMD);
}
