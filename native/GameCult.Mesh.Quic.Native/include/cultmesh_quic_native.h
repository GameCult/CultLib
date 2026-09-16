/* GameCult.Mesh.Quic.Native — C ABI v2, the runtime-neutral CultMesh QUIC bridge.
 *
 * This header is the contract. It is the single place the event layout, the
 * exports, the id and return-code conventions, the object lifetime rules and the
 * threading rules are stated; `cultmesh_quic_native.cpp` includes it, the README
 * points at it, and a host that cannot include it (koffi, P/Invoke) declares the
 * same thing in its own FFI and is checked against this file by a test.
 *
 * It is C. It includes nothing of MsQuic, nothing of Node, nothing of Unity, and
 * nothing platform-specific: a host binds these twelve queue exports plus
 * `cultmesh_quic_last_status` and needs no other build input.
 *
 * The v1 Unity exports (`cultmesh_quic_open`, `_state`, `_poll`, `_error`,
 * `_close`) are deliberately not here. They are a Windows-only shim for a
 * shipped managed connector, not a contract offered to new hosts.
 */

#ifndef CULTMESH_QUIC_NATIVE_H
#define CULTMESH_QUIC_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#if defined(__cplusplus)
#define CULTMESH_QUIC_STATIC_ASSERT(condition, message) static_assert(condition, message)
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
#define CULTMESH_QUIC_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#else
#define CULTMESH_QUIC_STATIC_ASSERT(condition, message)
#endif

/* `CULTMESH_QUIC_BUILD` is defined by the bridge's own translation unit and by
 * nothing else. A consumer gets plain declarations and links by name, which is
 * what every host here does: all three load the library at runtime. */
#if defined(CULTMESH_QUIC_BUILD)
#  if defined(_WIN32)
#    define CULTMESH_QUIC_API __declspec(dllexport)
#  else
#    define CULTMESH_QUIC_API __attribute__((visibility("default")))
#  endif
#else
#  define CULTMESH_QUIC_API
#endif

#if defined(__cplusplus)
extern "C" {
#endif

/* ---------------------------------------------------------------------------
 * 1. The event
 *
 * Exactly 64 bytes, no implicit padding, field order below is the ABI. A host
 * that reorders a field reads a different struct; the offsets are asserted here
 * so a compiler that pads differently fails the build rather than the wire.
 * --------------------------------------------------------------------------- */

struct cultmesh_quic_event {
    uint32_t type;           /* offset  0 — one of the cultmesh_quic_event_type values */
    uint32_t stream_kind;    /* offset  4 — 1 reliable, 2 latest-only; 0 when not a stream event */
    uint64_t listener_id;    /* offset  8 — 0 for an outbound connection */
    uint64_t connection_id;  /* offset 16 */
    uint64_t stream_id;      /* offset 24 */
    uint64_t code;           /* offset 32 — meaning depends on `type`; see below */
    int32_t  status;         /* offset 40 — the raw platform QUIC status, diagnostics only */
    int32_t  payload_length; /* offset 44 — bytes written into the caller's payload buffer */
    uint8_t  reserved[16];   /* offset 48 — zero today; do not read meaning into it */
};

CULTMESH_QUIC_STATIC_ASSERT(sizeof(struct cultmesh_quic_event) == 64,
    "the event struct is a 64-byte ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, type) == 0,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, stream_kind) == 4,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, listener_id) == 8,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, connection_id) == 16,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, stream_id) == 24,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, code) == 32,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, status) == 40,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, payload_length) == 44,
    "event field offsets are an ABI contract");
CULTMESH_QUIC_STATIC_ASSERT(offsetof(struct cultmesh_quic_event, reserved) == 48,
    "event field offsets are an ABI contract");

enum cultmesh_quic_event_type {
    /* A listener accepted an inbound connection. `listener_id`, `connection_id`. */
    CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION = 1,
    /* The handshake completed. `connection_id`. */
    CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED = 2,
    /* Payload: the peer's DER certificate. The handshake is blocked until the
     * host answers with `cultmesh_quic_connection_certificate_complete`. */
    CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED = 3,
    /* The connection is gone, whoever ended it, and its id no longer resolves.
     * Payload: a UTF-8 reason. `code` is the QUIC application error code — the
     * peer's when the peer ended it, and the code the host passed to
     * `cultmesh_quic_connection_shutdown` when the host did. Emitted exactly
     * once per connection id, including for the initiator of a local shutdown:
     * every connection id's end is observable from the host that owns it. */
    CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN = 4,
    /* An inbound stream's first byte named its kind. `stream_id`, `stream_kind`. */
    CULTMESH_QUIC_EVENT_STREAM_STARTED = 5,
    /* Payload: one whole encoded frame, the 4-byte length prefix stripped. */
    CULTMESH_QUIC_EVENT_STREAM_FRAME = 6,
    /* One per `cultmesh_quic_stream_send_frame` call that returned 0, and never
     * for the bridge's own kind byte. `code` is a cultmesh_quic_send_result. */
    CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE = 7,
    /* The stream is gone. `stream_id`, `stream_kind`. */
    CULTMESH_QUIC_EVENT_STREAM_SHUTDOWN = 8,
    /* The listener stopped. `listener_id`. */
    CULTMESH_QUIC_EVENT_LISTENER_STOPPED = 9
};

/* The `code` of event 7. A canceled send is a send MsQuic released without
 * putting on the wire — the stream was aborted or the connection ended under it.
 * It is reported, not silently dropped. */
enum cultmesh_quic_send_result {
    CULTMESH_QUIC_SEND_COMPLETED = 0,
    CULTMESH_QUIC_SEND_CANCELED = 1
};

/* The first byte of every CultMesh QUIC stream. */
enum cultmesh_quic_stream_kind {
    CULTMESH_QUIC_STREAM_RELIABLE = 1,
    CULTMESH_QUIC_STREAM_LATEST_ONLY = 2
};

/* ---------------------------------------------------------------------------
 * 2. Ids
 *
 * Listeners, connections and streams are named by `uint64_t` ids drawn from one
 * per-runtime counter that starts at 1 and never repeats. 0 is never a valid id
 * and is what an out parameter holds when a call fails. An id is the only thing
 * a host ever holds: there is no host-visible pointer to a connection or stream,
 * so a host cannot outlive an object it is naming. An id whose object is gone is
 * a bad call (-1), not undefined behaviour.
 * --------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------
 * 3. Return codes — negative on a bad call, on every platform
 *
 * Every int32_t-returning export returns:
 *
 *    0                        the call succeeded
 *    1, 2                     `cultmesh_quic_next_event` only; see its comment
 *   -1                        a bad call: a null handle, a null out parameter,
 *                             or an id whose object is gone
 *   -2                        an argument the bridge rejects on its own terms:
 *                             an unknown stream kind, an oversized frame, a
 *                             listener host that is not an IP literal
 *   <= -1000                  MsQuic refused. The bridge code is
 *                             -(1000 + (raw_status & 0xffff)); the raw
 *                             platform status is available from
 *                             `cultmesh_quic_last_status` and a sentence about
 *                             it from `cultmesh_quic_last_error`.
 *
 * This mapping exists because `QUIC_STATUS` itself is not portable: it is a
 * negative HRESULT on Windows and a positive errno on POSIX, so a host that
 * checked for a negative value saw every Linux failure as success.
 *
 * What the mapping makes portable is the sign, and only the sign. A failure is
 * negative everywhere, and that is what a host may branch on. The magnitude of a
 * <= -1000 code is diagnostic and platform-specific: the same MsQuic failure
 * carries different numbers on different platforms (a TLS error is -12032 on
 * win32-x64 and -1126 on linux-x64; an ALPN already in use is -1009 against
 * -1091), and the 16-bit mask can give two distinct statuses the same code
 * (`QUIC_STATUS_USER_CANCELED` and `QUIC_STATUS_FILE_NOT_FOUND` are both -1002
 * on Windows). Switching on a particular code is therefore not portable and not
 * a reliable identification of the failure. Report it, log it, and read
 * `cultmesh_quic_last_status` and `cultmesh_quic_last_error` beside it.
 * --------------------------------------------------------------------------- */

#define CULTMESH_QUIC_RESULT_BAD_CALL (-1)
#define CULTMESH_QUIC_RESULT_BAD_ARGUMENT (-2)
#define CULTMESH_QUIC_RESULT_MSQUIC_BASE (-1000)

/* ---------------------------------------------------------------------------
 * 4. Object lifetime — resolved from an id, kept alive across the call
 *
 * The bridge's objects are shared-owned. The runtime's maps hold one reference
 * each; an export that names an id takes its own reference under the lock and
 * holds it for the whole MsQuic call, so an object cannot be freed under a host
 * thread that is using it. MsQuic's worker, on shutdown-complete, removes the
 * map entry and drops the map's reference only — it never frees an object a host
 * call is inside.
 *
 * The MsQuic handle follows the object: it is closed exactly once, by whichever
 * holder drops the last reference (or by `cultmesh_quic_runtime_close`), under a
 * one-shot exchange guard. A handle is therefore valid for as long as any holder
 * has a reference to its object, which is the property the host thread needs. A
 * stream holds a reference to its connection, so stream handles are always
 * closed before the connection handle they belong to.
 *
 * `cultmesh_quic_runtime_close` quiesces before it tears anything down. It marks
 * the runtime closing, wakes every blocked `cultmesh_quic_next_event`, and waits
 * for every in-flight export to return before it closes a handle or frees a
 * byte. No host call may begin after `cultmesh_quic_runtime_close` starts: a
 * call that arrives during or after it is refused with -1 and touches nothing.
 * The host is responsible for that ordering; the refusal is a guard rail, not a
 * licence to race.
 * --------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------
 * 5. Threading
 *
 * There are no host callbacks. Every crossing is the host calling in.
 *
 *  - MsQuic's worker threads run inside this library only. They push events onto
 *    the runtime's queue and wake the one blocking wait. No host code is ever
 *    called from a thread the host does not own.
 *  - `cultmesh_quic_next_event` is called from host threads, one or many. It is
 *    the only blocking export. Concurrent callers each get a distinct event.
 *  - Every other export may be called from any host thread at any time, except
 *    during or after `cultmesh_quic_runtime_close`.
 *  - `cultmesh_quic_runtime_close` is called once, from one thread, and
 *    quiesces as described above.
 * --------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------
 * 6. The exports
 * --------------------------------------------------------------------------- */

/* Opens a runtime: one MsQuic registration, one event queue. `app_name` may be
 * null. `*out_runtime` is the opaque handle every other export takes. */
CULTMESH_QUIC_API int32_t cultmesh_quic_runtime_open(
    const char* app_name, void** out_runtime);

/* Quiesces, tears down and frees the runtime. Safe to call with null. After it
 * returns, the handle is dead; see section 4 for the ordering rule. */
CULTMESH_QUIC_API void cultmesh_quic_runtime_close(void* runtime);

/* Opens a provider listener on `host`:`port` with a PKCS12 credential. `host`
 * may be null or empty for any address, otherwise it must be an IP literal.
 * `port` 0 binds an ephemeral port, which `*out_bound_port` reports. A failure
 * leaves no listener behind and no id allocated to the host. */
CULTMESH_QUIC_API int32_t cultmesh_quic_listener_open(
    void* runtime, const char* host, uint16_t port,
    const uint8_t* pkcs12, int32_t pkcs12_length, const char* password,
    uint64_t* out_listener_id, uint16_t* out_bound_port);

/* Stops and closes a listener. Its accepted connections are not affected.
 * Emits event 9. Unknown ids are ignored. */
CULTMESH_QUIC_API void cultmesh_quic_listener_close(
    void* runtime, uint64_t listener_id);

/* Starts an outbound connection. Certificate validation is deferred: the host
 * gets event 3 and must answer. */
CULTMESH_QUIC_API int32_t cultmesh_quic_connection_open(
    void* runtime, const char* host, uint16_t port, uint64_t* out_connection_id);

/* Answers event 3. `accept` non-zero continues the handshake; zero fails it with
 * a bad-certificate alert. */
CULTMESH_QUIC_API int32_t cultmesh_quic_connection_certificate_complete(
    void* runtime, uint64_t connection_id, int32_t accept);

/* Ends a connection with a QUIC application error code. The initiator gets its
 * own event 4 carrying `code`, as the peer does. Unknown ids are ignored. */
CULTMESH_QUIC_API void cultmesh_quic_connection_shutdown(
    void* runtime, uint64_t connection_id, uint64_t code);

/* Opens an outbound unidirectional stream and puts `kind` on the wire as its
 * first byte. That byte is the bridge's own and never produces an event 7. */
CULTMESH_QUIC_API int32_t cultmesh_quic_stream_open(
    void* runtime, uint64_t connection_id, uint8_t kind, uint64_t* out_stream_id);

/* Queues one whole encoded frame, length-prefixed by the bridge. A call that
 * returns 0 produces exactly one event 7 later, completed or canceled. */
CULTMESH_QUIC_API int32_t cultmesh_quic_stream_send_frame(
    void* runtime, uint64_t stream_id,
    const uint8_t* encoded_frame, int32_t length, int32_t fin);

/* Aborts a stream with a QUIC application error code. Unknown ids are ignored. */
CULTMESH_QUIC_API void cultmesh_quic_stream_shutdown(
    void* runtime, uint64_t stream_id, uint64_t code);

/* The one blocking crossing.
 *
 *   0   nothing within `timeout_ms`. A poller already blocked here when
 *       `cultmesh_quic_runtime_close` starts is woken and returns 0; one that
 *       arrives after it starts is refused with -1 like any other call.
 *   1   `*out_event` filled and up to `payload_capacity` payload bytes copied;
 *       `*out_required` is the payload size
 *   2   `payload_capacity` is too small. Nothing is consumed and nothing is
 *       written to `*out_event`; `*out_required` is the size to allocate. Ask
 *       again with a big enough buffer and the same event is delivered.
 *  -1   a bad call, including a null `payload` with a non-zero
 *       `payload_capacity`, which is a lie about the buffer and is refused
 *       rather than treated as a discard.
 *
 * `payload` may be null when `payload_capacity` is 0: that is the two-phase
 * poll's first ask. */
CULTMESH_QUIC_API int32_t cultmesh_quic_next_event(
    void* runtime, int32_t timeout_ms, struct cultmesh_quic_event* out_event,
    uint8_t* payload, int32_t payload_capacity, int32_t* out_required);

/* The last error sentence, NUL-terminated, truncated to `capacity`. Returns the
 * number of bytes written, or -1 on a bad call. */
CULTMESH_QUIC_API int32_t cultmesh_quic_last_error(
    void* runtime, char* destination, int32_t capacity);

/* The raw platform `QUIC_STATUS` behind the last <= -1000 return, as its own
 * bits: a negative HRESULT on Windows, a positive errno on POSIX. Diagnostics
 * only; what is portable about the return code is its sign, not its value. 0
 * when nothing has failed. */
CULTMESH_QUIC_API int32_t cultmesh_quic_last_status(void* runtime);

/* ---------------------------------------------------------------------------
 * 7. The development seam
 *
 * Present only in a library configured with `CULTMESH_QUIC_DEBUG_ASSERTS`, which
 * a shipped build never is. It exists for the runtime-lifetime scenarios: the
 * quiesce is a promise about host calls that are inside the library, and a
 * scenario that can only sleep and hope cannot tell a bridge that counts them
 * from one that does not.
 *
 * It is process-wide, not per-runtime, because the numbers outlive the runtime
 * they describe: `cultmesh_quic_runtime_close` frees the runtime, and the count
 * it waited on is read afterwards. The scenarios open one runtime at a time.
 * --------------------------------------------------------------------------- */
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)

/* Non-zero arms the hold and resets both counters: every call that reaches the
 * blocking wait in `cultmesh_quic_next_event` parks inside the library instead
 * of returning. Zero releases every held call and disarms it. A held call
 * returns 0 and touches the runtime no further. */
CULTMESH_QUIC_API void cultmesh_quic_debug_hold_calls(int32_t hold);

/* The high-water mark of host calls counted inside the library since the hold
 * was armed. Zero means no call was ever counted. */
CULTMESH_QUIC_API int32_t cultmesh_quic_debug_peak_calls(void);

/* How many host calls were counted inside the library when the last
 * `cultmesh_quic_runtime_close` began its wait, or -1 if no close has begun one
 * since the hold was armed. This is the number the wait exists for. */
CULTMESH_QUIC_API int32_t cultmesh_quic_debug_calls_at_close(void);

#endif /* CULTMESH_QUIC_DEBUG_ASSERTS */

#if defined(__cplusplus)
}
#endif

#endif /* CULTMESH_QUIC_NATIVE_H */
