// Protocol 1.0 (docs/PROTOCOL.md), OpenWheel framing ":NN[params]#". Every command is answered with
// exactly one line: "<value>#", "OK#" or "ERR,<code>#" (error_codes.h).
// protocol.cpp owns framing and replies and routes by number range to the block handlers:
//   20-49 protocol_common.cpp (session D)    50-69 protocol_focuser.cpp (session A)
//   70-89 protocol_rotator.cpp (session B)   90-99 protocol_env.cpp (session C)
//   00-19 -> ERR,10
// Owner: orchestrator. FROZEN interface for the handler files.
#pragma once
#include <stdint.h>
#include "error_codes.h"

#define PROTO_MAX_LINE  46      // characters between ':' and '#'
#define PROTO_MAX_ARGS  6

class Protocol {
public:
    void process();   // read and answer everything waiting on PROTOCOL_SERIAL

private:
    char    buf[PROTO_MAX_LINE + 1];
    uint8_t len = 0;
    bool    inCommand = false;
    bool    overflow = false;

    void handleCommand();
};

// --- identify gate (#58): after power-on, and whenever a host opens or reconfigures the port, only
// :25# ping and :32# identify are accepted; everything else answers ERR,16 until :32# has been
// answered. Clients re-identify on ERR,16 (same USB serial) and repeat the command. ---
void protoSetIdentified();

// --- replies (exactly one per command; each is a single USB write) ---
void protoReply(const char* s, int n);
void protoOk();                                   // "OK#"
void protoError(FwError code);                    // "ERR,<code>#"
void protoInt(int32_t v);                         // "<v>#"
void protoPrintf(const char* fmt, ...) __attribute__((format(printf, 1, 2)));   // must end with '#'

// --- parameters ---
// Splits the text after "NN" into comma separated values; one leading ' ' or ',' is skipped.
// Returns false for more than PROTO_MAX_ARGS values or an empty value ("1,,2"); handlers answer
// that with ERR,7 (bad parameter). ERR,11 is only for framing: line too long, no command number.
struct ProtoArgs {
    uint8_t n;                     // 0 when there are no parameters
    char*   v[PROTO_MAX_ARGS];
};
bool protoSplit(char* params, ProtoArgs& out);
bool protoInt32(const char* s, int32_t& out);     // strict decimal with an optional sign
int  protoAxis(const char* s);                    // "F"/"f" -> AXIS_FOC, "R"/"r" -> AXIS_ROT, else -1

// --- block handlers: params is the writable text after "NN"; each must answer exactly once ---
void protocolCommon(uint8_t cmd, char* params);   // 20-49
void protocolFocuser(uint8_t cmd, char* params);  // 50-69
void protocolRotator(uint8_t cmd, char* params);  // 70-89
void protocolEnv(uint8_t cmd, char* params);      // 90-99
