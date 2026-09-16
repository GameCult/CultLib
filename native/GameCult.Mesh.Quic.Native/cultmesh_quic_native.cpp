// The CultMesh MsQuic bridge: one MsQuic body, two roles, two platforms.
//
// This file owns MsQuic registration, configuration, listener, connection and
// stream handles, TLS credential loading, the 1-byte-kind plus 4-byte-LE-length
// QUIC stream framing, and a single event queue per runtime. It owns transport
// only. It never decodes a CultMesh frame, never decides delivery semantics,
// never coalesces, never reconnects, and in the v2 path never accepts a
// certificate on its own: the host answers, through
// `cultmesh_quic_connection_certificate_complete`.
//
// No host callbacks. Every crossing is the host calling in, including the one
// blocking wait (`cultmesh_quic_next_event`). MsQuic's worker threads only ever
// push onto the queue and wake that wait, so the bridge is runtime-neutral: it
// includes no Node headers, no Unity headers, and nothing platform-specific
// outside the `_WIN32` block at the bottom of this file.
//
// Two ABIs live here. v2 is the queue API above, for any host. v1 is the five
// exports the Unity managed connector already imports by name
// (`CultMeshNativeQuicRealtimeTransport.cs`, `NativeMethods`), kept Windows-only
// and implemented on top of a private v2 runtime. v1 is the only place a
// certificate pin is compared in C, because that is the contract Unity was
// shipped with; it delegates everything else and decides nothing new.

// The host-facing contract lives in include/cultmesh_quic_native.h and is
// included here so this file cannot drift from it: the event layout, the
// exports, the return codes, the lifetime rules and the threading rules are
// stated there once and asserted at compile time.
#define CULTMESH_QUIC_BUILD 1
#include <cultmesh_quic_native.h>

#include <msquic.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

#define CULTMESH_API extern "C" CULTMESH_QUIC_API

// Development builds only, and never in a shipped binary: after
// `cultmesh_quic_runtime_close` has quiesced, no host call may still be inside
// the library. The wait is what makes that true, and its absence is otherwise
// visible only as a use-after-free a sanitizer catches by luck — a poller has to
// be preempted in the right window. With pollers blocked, this states the
// invariant directly, so a build without the wait dies on the first close rather
// than on the run that happens to be unlucky.
#if defined(CULTMESH_QUIC_DEBUG_ASSERTS)
#define CULTMESH_QUIC_ASSERT_QUIESCED(count)                                          \
    do {                                                                              \
        const int cultmesh_quic_remaining = (count);                                   \
        if (cultmesh_quic_remaining != 0) {                                            \
            std::fprintf(stderr,                                                       \
                "cultmesh_quic_runtime_close: %d host call(s) still inside the "        \
                "library after the quiesce\n", cultmesh_quic_remaining);                \
            std::fflush(stderr);                                                       \
            std::abort();                                                              \
        }                                                                              \
    } while (false)
#else
#define CULTMESH_QUIC_ASSERT_QUIESCED(count) ((void)0)
#endif

namespace {

constexpr uint32_t kApiVersion = 2;
constexpr uint64_t kConnectionCloseCode = 0x43554c54;
constexpr uint64_t kStreamAbortCode = 0x53544154;
constexpr uint8_t kReliableStream = CULTMESH_QUIC_STREAM_RELIABLE;
constexpr uint8_t kLatestOnlyStream = CULTMESH_QUIC_STREAM_LATEST_ONLY;
constexpr uint32_t kMaximumEncodedFrameBytes = (64u * 1024u * 1024u) + 37u + (3u * 65535u);
constexpr char kAlpn[] = "cultmesh-state-v1";

// Mirrors the C# server and connector: CultMeshQuicRealtimeTransport.cs:77,:202
// for the stream ceiling, and the idle/keep-alive pair the v1 bridge already
// used (:335-338).
constexpr uint32_t kIdleTimeoutMs = 30000;
constexpr uint32_t kKeepAliveIntervalMs = 5000;
constexpr uint16_t kPeerUnidiStreamCount = 1024;
// If the host never answers the certificate event, this is what closes the
// connection instead of leaking it: MsQuic's own handshake timeout fires and
// the shutdown arrives as event 4.
constexpr uint32_t kHandshakeIdleTimeoutMs = 10000;

// Short names for the header's event codes; the header is the contract.
constexpr uint32_t kEventListenerNewConnection = CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION;
constexpr uint32_t kEventConnectionConnected = CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED;
constexpr uint32_t kEventConnectionCertificateReceived = CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED;
constexpr uint32_t kEventConnectionShutdown = CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN;
constexpr uint32_t kEventStreamStarted = CULTMESH_QUIC_EVENT_STREAM_STARTED;
constexpr uint32_t kEventStreamFrame = CULTMESH_QUIC_EVENT_STREAM_FRAME;
constexpr uint32_t kEventStreamSendComplete = CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE;
constexpr uint32_t kEventStreamShutdown = CULTMESH_QUIC_EVENT_STREAM_SHUTDOWN;
constexpr uint32_t kEventListenerStopped = CULTMESH_QUIC_EVENT_LISTENER_STOPPED;

struct Runtime;
struct Connection;

struct QueuedEvent {
    cultmesh_quic_event header{};
    std::vector<uint8_t> payload;
};

// Objects are shared-owned, and that is the whole lifetime model. The runtime's
// maps hold one reference each; an export that names an id takes its own
// reference under the lock and holds it for the entire MsQuic call. MsQuic's
// worker, on shutdown-complete, drops the map's reference only. The handle
// follows the object: it is closed exactly once, in the destructor, by whichever
// holder drops last, under a one-shot exchange guard. See section 4 of
// include/cultmesh_quic_native.h, which is where this is promised to hosts.

struct Stream {
    Runtime* runtime = nullptr;
    // Shared, not raw: this is what keeps a connection's handle open for as long
    // as any of its streams still exist, so stream handles always close first.
    std::shared_ptr<Connection> owner;
    HQUIC handle = nullptr;
    uint64_t id = 0;
    uint8_t kind = 0;
    bool inbound = false;
    bool has_kind = false;
    std::atomic<bool> handle_closed{false};
    // Inbound reassembly only. MsQuic delivers stream bytes in arbitrary
    // chunks, so the length prefix and the frame it introduces can arrive in
    // separate receives.
    std::vector<uint8_t> buffered;

    ~Stream();
};

struct Connection : std::enable_shared_from_this<Connection> {
    Runtime* runtime = nullptr;
    HQUIC handle = nullptr;
    uint64_t id = 0;
    uint64_t listener_id = 0;
    bool inbound = false;
    // Written by the host thread in `cultmesh_quic_connection_shutdown`, read on
    // an MsQuic worker when the shutdown completes: the code the host passed is
    // what its own event 4 has to carry.
    std::atomic<bool> closing{false};
    std::atomic<uint64_t> shutdown_code{0};
    // Why it ended, recorded by whichever initiated-by callback saw it and read
    // by shutdown-complete, which is the single emitter of event 4. Both run on
    // this connection's own worker, serialized.
    std::string shutdown_reason;
    int32_t shutdown_status = 0;
    // Set when MsQuic is closing the handle itself (an app close already in
    // progress), so the destructor's one-shot close stands down.
    std::atomic<bool> handle_closed{false};
    // v1 only. A connection carrying a pin has its certificate answered in C
    // against that pin; a v2 connection never sets this and is always answered
    // by the host. Nothing outside the `_WIN32` block below ever sets it.
    bool has_pin = false;
    std::array<uint8_t, 32> pin{};
    // v1 diagnostics: the MsQuic event sequence, which the v1 error string
    // reports and nothing in v2 reads.
    std::string trace;

    ~Connection();
};

struct Listener {
    Runtime* runtime = nullptr;
    HQUIC handle = nullptr;
    HQUIC configuration = nullptr;
    uint64_t id = 0;
    std::atomic<bool> handle_closed{false};

    ~Listener();
};

struct Runtime {
    const QUIC_API_TABLE* api = nullptr;
    HQUIC registration = nullptr;
    // One client configuration serves every outbound connection: the ALPN,
    // settings and credential flags are identical for all of them.
    HQUIC client_configuration = nullptr;

    std::mutex gate;
    std::condition_variable signal;
    std::deque<QueuedEvent> events;
    std::unordered_map<uint64_t, std::shared_ptr<Listener>> listeners;
    std::unordered_map<uint64_t, std::shared_ptr<Connection>> connections;
    std::unordered_map<uint64_t, std::shared_ptr<Stream>> streams;
    uint64_t next_id = 1;
    std::string error;
    // The raw platform status behind the last MsQuic refusal, kept because the
    // portable return code deliberately throws its bits away.
    int32_t last_status = 0;
    bool closing = false;
    // Host calls currently inside the library. `cultmesh_quic_runtime_close`
    // waits for this to reach zero before it touches a handle.
    int active_calls = 0;
};

// Every host-facing export enters through this. It refuses once
// `cultmesh_quic_runtime_close` has started, and otherwise holds the runtime
// open for the length of the call.
class CallScope {
public:
    explicit CallScope(Runtime* runtime) : runtime_(runtime) {
        if (runtime_ == nullptr) return;
        std::lock_guard<std::mutex> lock(runtime_->gate);
        if (runtime_->closing) return;
        ++runtime_->active_calls;
        entered_ = true;
    }
    // The notify stays under `gate`, and that is the whole point of it. Released
    // first, the closer's predicate is already true, so it can return from its
    // wait, run the teardown and destroy the runtime — condition variable
    // included — before this thread reaches the notify. Holding the lock across
    // both means the closer cannot leave `signal.wait` until this scope has let
    // go, so the condition variable is still alive when it is signalled.
    ~CallScope() {
        if (!entered_) return;
        std::lock_guard<std::mutex> lock(runtime_->gate);
        --runtime_->active_calls;
        runtime_->signal.notify_all();
    }
    CallScope(const CallScope&) = delete;
    CallScope& operator=(const CallScope&) = delete;
    bool entered() const { return entered_; }

private:
    Runtime* runtime_ = nullptr;
    bool entered_ = false;
};

Stream::~Stream() {
    if (handle != nullptr && runtime != nullptr && runtime->api != nullptr &&
        !handle_closed.exchange(true))
        runtime->api->StreamClose(handle);
}

Connection::~Connection() {
    if (handle != nullptr && runtime != nullptr && runtime->api != nullptr &&
        !handle_closed.exchange(true))
        runtime->api->ConnectionClose(handle);
}

Listener::~Listener() {
    if (runtime == nullptr || runtime->api == nullptr) return;
    if (handle != nullptr && !handle_closed.exchange(true)) runtime->api->ListenerClose(handle);
    if (configuration != nullptr) runtime->api->ConfigurationClose(configuration);
}

// Every send buffer outlives its `StreamSend` call: MsQuic reads the bytes
// asynchronously and only releases them at SEND_COMPLETE, where this is freed.
struct SendRequest {
    QUIC_BUFFER buffer{};
    std::vector<uint8_t> bytes;
    // Which send this was. The bridge writes the stream's kind byte itself, and
    // that completion is not the host's to hear about; event 7 is one per
    // `cultmesh_quic_stream_send_frame` and nothing else.
    bool is_frame = false;
};

// A `QUIC_STATUS` reaches this through `uint32_t`, never straight to `uint64_t`:
// it is a signed HRESULT on Windows, and widening it signed printed every
// Windows failure as `0xFFFFFFFF80072B18`. The status is 32 bits on both
// platforms and is read as 32 bits here.
std::string Hex(uint64_t value) {
    std::ostringstream text;
    text << "0x" << std::hex << std::uppercase << value;
    return text.str();
}

void SetError(Runtime* runtime, const std::string& message) {
    if (runtime == nullptr) return;
    std::lock_guard<std::mutex> lock(runtime->gate);
    runtime->error = message;
}

// `QUIC_STATUS` never reaches a host. It is a negative HRESULT on Windows and a
// positive errno on POSIX, so a host that checked for a negative value saw every
// Linux failure as success. One negative space on both platforms instead; the
// raw bits go to `cultmesh_quic_last_status`. See section 3 of the header.
int32_t MsQuicCode(QUIC_STATUS status) {
    return CULTMESH_QUIC_RESULT_MSQUIC_BASE -
        static_cast<int32_t>(static_cast<uint32_t>(status) & 0xffffu);
}

int32_t Refuse(Runtime* runtime, QUIC_STATUS status, const std::string& message) {
    if (runtime != nullptr) {
        std::lock_guard<std::mutex> lock(runtime->gate);
        runtime->error = message;
        runtime->last_status = static_cast<int32_t>(status);
    }
    return MsQuicCode(status);
}

// The only way anything reaches the host. Callers must not hold `gate`.
void Publish(Runtime* runtime, const cultmesh_quic_event& header, const uint8_t* payload, size_t payload_length) {
    QueuedEvent queued;
    queued.header = header;
    queued.header.payload_length = static_cast<int32_t>(payload_length);
    if (payload != nullptr && payload_length > 0)
        queued.payload.assign(payload, payload + payload_length);
    {
        std::lock_guard<std::mutex> lock(runtime->gate);
        if (runtime->closing) return;
        runtime->events.push_back(std::move(queued));
    }
    runtime->signal.notify_all();
}

void PublishSimple(Runtime* runtime, uint32_t type, uint64_t listener_id, uint64_t connection_id,
                   uint64_t stream_id, uint64_t code, int32_t status, uint32_t stream_kind = 0) {
    cultmesh_quic_event header{};
    header.type = type;
    header.stream_kind = stream_kind;
    header.listener_id = listener_id;
    header.connection_id = connection_id;
    header.stream_id = stream_id;
    header.code = code;
    header.status = status;
    Publish(runtime, header, nullptr, 0);
}

// v1 diagnostics only, and bounded. A v2 connection has no reader for this, and
// a long-lived one appended to it once per MsQuic event for as long as it lived.
constexpr size_t kMaximumTraceBytes = 512;

void AppendTrace(Connection* connection, const std::string& event) {
    if (connection == nullptr || !connection->has_pin) return;
    std::lock_guard<std::mutex> lock(connection->runtime->gate);
    if (connection->trace.size() >= kMaximumTraceBytes) return;
    if (!connection->trace.empty()) connection->trace += ",";
    connection->trace += event;
}

uint32_t ReadUInt32LittleEndian(const uint8_t* value) {
    return static_cast<uint32_t>(value[0]) |
        (static_cast<uint32_t>(value[1]) << 8) |
        (static_cast<uint32_t>(value[2]) << 16) |
        (static_cast<uint32_t>(value[3]) << 24);
}

void WriteUInt32LittleEndian(uint8_t* destination, uint32_t value) {
    destination[0] = static_cast<uint8_t>(value & 0xff);
    destination[1] = static_cast<uint8_t>((value >> 8) & 0xff);
    destination[2] = static_cast<uint8_t>((value >> 16) & 0xff);
    destination[3] = static_cast<uint8_t>((value >> 24) & 0xff);
}

uint64_t NextId(Runtime* runtime) {
    std::lock_guard<std::mutex> lock(runtime->gate);
    return runtime->next_id++;
}

// The three lookups. Each returns a reference the caller owns for as long as it
// holds it, taken under the lock, so nothing the caller then names can be freed
// under it. A raw pointer here was the use-after-free.
std::shared_ptr<Connection> FindConnection(Runtime* runtime, uint64_t id) {
    std::lock_guard<std::mutex> lock(runtime->gate);
    const auto found = runtime->connections.find(id);
    return found == runtime->connections.end() ? nullptr : found->second;
}

std::shared_ptr<Stream> FindStream(Runtime* runtime, uint64_t id) {
    std::lock_guard<std::mutex> lock(runtime->gate);
    const auto found = runtime->streams.find(id);
    return found == runtime->streams.end() ? nullptr : found->second;
}

// Takes the object out of its map and returns it, so the caller's reference —
// and any destructor it ends up running — lives outside the lock.
template <typename Map>
auto Detach(std::mutex& gate, Map& map, uint64_t id) {
    typename Map::mapped_type detached;
    std::lock_guard<std::mutex> lock(gate);
    const auto found = map.find(id);
    if (found == map.end()) return detached;
    detached = std::move(found->second);
    map.erase(found);
    return detached;
}

void ShutdownConnectionForProtocolFault(const std::shared_ptr<Connection>& connection, const std::string& message) {
    if (connection == nullptr) return;
    SetError(connection->runtime, message);
    if (connection->runtime->api != nullptr && connection->handle != nullptr)
        connection->runtime->api->ConnectionShutdown(
            connection->handle, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, kStreamAbortCode);
}

// Reassembles whole encoded frames out of the stream byte sequence and
// publishes one event 6 per frame, length prefix stripped. The first byte of an
// inbound stream is its kind, which becomes event 5.
bool ConsumeFrames(Stream* stream) {
    Runtime* runtime = stream->runtime;
    auto& bytes = stream->buffered;
    if (!stream->has_kind) {
        if (bytes.empty()) return true;
        stream->kind = bytes.front();
        bytes.erase(bytes.begin());
        stream->has_kind = true;
        if (stream->kind != kReliableStream && stream->kind != kLatestOnlyStream) {
            ShutdownConnectionForProtocolFault(stream->owner, "CultMesh QUIC stream kind is invalid.");
            return false;
        }
        PublishSimple(runtime, kEventStreamStarted, stream->owner->listener_id, stream->owner->id,
                      stream->id, 0, 0, stream->kind);
    }

    while (bytes.size() >= sizeof(uint32_t)) {
        const uint32_t length = ReadUInt32LittleEndian(bytes.data());
        if (length == 0 || length > kMaximumEncodedFrameBytes) {
            ShutdownConnectionForProtocolFault(stream->owner, "CultMesh QUIC frame length is invalid.");
            return false;
        }
        const size_t framed_length = sizeof(uint32_t) + static_cast<size_t>(length);
        if (bytes.size() < framed_length) return true;

        cultmesh_quic_event header{};
        header.type = kEventStreamFrame;
        header.stream_kind = stream->kind;
        header.listener_id = stream->owner->listener_id;
        header.connection_id = stream->owner->id;
        header.stream_id = stream->id;
        Publish(runtime, header, bytes.data() + sizeof(uint32_t), length);

        bytes.erase(bytes.begin(), bytes.begin() + static_cast<std::ptrdiff_t>(framed_length));
    }
    return true;
}

// Drops the map's reference and nothing else. If the map was the last holder the
// object dies here, outside the lock, and its destructor closes the handle; if a
// host call is still inside, the object outlives this by exactly that long.
void DestroyStream(Runtime* runtime, uint64_t stream_id) {
    [[maybe_unused]] const auto detached = Detach(runtime->gate, runtime->streams, stream_id);
}

QUIC_STATUS QUIC_API StreamCallback(HQUIC, void* context, QUIC_STREAM_EVENT* event) {
    auto* stream = static_cast<Stream*>(context);
    Runtime* runtime = stream->runtime;
    switch (event->Type) {
    case QUIC_STREAM_EVENT_RECEIVE:
        for (uint32_t index = 0; index < event->RECEIVE.BufferCount; ++index) {
            const auto& buffer = event->RECEIVE.Buffers[index];
            stream->buffered.insert(stream->buffered.end(), buffer.Buffer, buffer.Buffer + buffer.Length);
        }
        ConsumeFrames(stream);
        break;
    case QUIC_STREAM_EVENT_SEND_COMPLETE: {
        // Which send finished is the client context's job to say. Without it the
        // bridge's own kind byte completed as an anonymous event 7 and the host
        // counted six completions for five frames.
        auto* request = static_cast<SendRequest*>(event->SEND_COMPLETE.ClientContext);
        const bool is_frame = request != nullptr && request->is_frame;
        delete request;
        if (is_frame)
            PublishSimple(runtime, kEventStreamSendComplete, stream->owner->listener_id, stream->owner->id,
                          stream->id,
                          event->SEND_COMPLETE.Canceled
                              ? CULTMESH_QUIC_SEND_CANCELED
                              : CULTMESH_QUIC_SEND_COMPLETED,
                          0, stream->kind);
        break;
    }
    case QUIC_STREAM_EVENT_PEER_SEND_SHUTDOWN:
        if (!stream->buffered.empty())
            ShutdownConnectionForProtocolFault(stream->owner, "CultMesh QUIC stream ended with a truncated frame.");
        break;
    case QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE: {
        // Everything this event reports is read before the map's reference goes,
        // because that reference may be the last one and `stream` may not
        // survive `DestroyStream`.
        const uint64_t stream_id = stream->id;
        const uint64_t connection_id = stream->owner->id;
        const uint64_t listener_id = stream->owner->listener_id;
        const uint32_t kind = stream->kind;
        // An app close already in progress means MsQuic closes this handle
        // itself; anything else leaves it to the last holder's destructor.
        if (event->SHUTDOWN_COMPLETE.AppCloseInProgress) stream->handle_closed.store(true);
        DestroyStream(runtime, stream_id);
        PublishSimple(runtime, kEventStreamShutdown, listener_id, connection_id, stream_id, 0, 0, kind);
        break;
    }
    default:
        break;
    }
    return QUIC_STATUS_SUCCESS;
}

void DestroyConnection(Runtime* runtime, uint64_t connection_id) {
    // Declared first so it dies last: whatever order these drop in, a stream
    // holds its connection, so every stream handle closes before the connection
    // handle does.
    std::shared_ptr<Connection> connection;
    std::vector<std::shared_ptr<Stream>> streams;
    {
        std::lock_guard<std::mutex> lock(runtime->gate);
        for (auto it = runtime->streams.begin(); it != runtime->streams.end();) {
            if (it->second->owner != nullptr && it->second->owner->id == connection_id) {
                streams.push_back(std::move(it->second));
                it = runtime->streams.erase(it);
            } else {
                ++it;
            }
        }
        const auto found = runtime->connections.find(connection_id);
        if (found != runtime->connections.end()) {
            connection = std::move(found->second);
            runtime->connections.erase(found);
        }
    }
}

#if defined(_WIN32)
// Declared here, defined in the v1 block at the bottom: the pin comparison is
// the one piece of the certificate path that is Windows-only and v1-only.
bool CertificateMatchesPin(Connection* connection, QUIC_CERTIFICATE* certificate);
#endif

QUIC_STATUS QUIC_API ConnectionCallback(HQUIC handle, void* context, QUIC_CONNECTION_EVENT* event) {
    auto* connection = static_cast<Connection*>(context);
    Runtime* runtime = connection->runtime;
    AppendTrace(connection, "event=" + std::to_string(static_cast<int>(event->Type)));
    switch (event->Type) {
    case QUIC_CONNECTION_EVENT_CONNECTED:
        PublishSimple(runtime, kEventConnectionConnected, connection->listener_id, connection->id, 0, 0, 0);
        break;
    case QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED: {
        auto stream = std::make_shared<Stream>();
        stream->runtime = runtime;
        stream->owner = connection->shared_from_this();
        stream->handle = event->PEER_STREAM_STARTED.Stream;
        stream->inbound = true;
        stream->id = NextId(runtime);
        Stream* raw = stream.get();
        {
            std::lock_guard<std::mutex> lock(runtime->gate);
            runtime->streams.emplace(raw->id, std::move(stream));
        }
        runtime->api->SetCallbackHandler(
            event->PEER_STREAM_STARTED.Stream, reinterpret_cast<void*>(StreamCallback), raw);
        break;
    }
    case QUIC_CONNECTION_EVENT_PEER_CERTIFICATE_RECEIVED: {
        const auto* encoded = reinterpret_cast<const QUIC_BUFFER*>(event->PEER_CERTIFICATE_RECEIVED.Certificate);
        AppendTrace(connection, "certificate-status=" +
            Hex(static_cast<uint32_t>(event->PEER_CERTIFICATE_RECEIVED.DeferredStatus)));
#if defined(_WIN32)
        if (connection->has_pin) {
            // The v1 contract: the pin decision is made here, in C, and the host
            // is never asked. Only `cultmesh_quic_open` sets `has_pin`.
            const bool matches = CertificateMatchesPin(
                connection, event->PEER_CERTIFICATE_RECEIVED.Certificate);
            if (!matches)
                SetError(runtime,
                    "CultMesh QUIC provider certificate does not match the advertised SHA-256 pin.");
            const auto completion = runtime->api->ConnectionCertificateValidationComplete(
                handle,
                matches ? static_cast<BOOLEAN>(1) : static_cast<BOOLEAN>(0),
                matches ? QUIC_TLS_ALERT_CODE_SUCCESS : QUIC_TLS_ALERT_CODE_BAD_CERTIFICATE);
            if (QUIC_FAILED(completion)) {
                SetError(runtime, "CultMesh QUIC certificate validation completion failed (status=" +
                    Hex(static_cast<uint32_t>(completion)) + ").");
                return completion;
            }
            return QUIC_STATUS_PENDING;
        }
#endif
        // v2: the DER goes to the host and the handshake waits. Nothing here
        // decides trust. If the host never answers, MsQuic's handshake timeout
        // closes the connection and event 4 reports it.
        cultmesh_quic_event header{};
        header.type = kEventConnectionCertificateReceived;
        header.listener_id = connection->listener_id;
        header.connection_id = connection->id;
        header.status = static_cast<int32_t>(event->PEER_CERTIFICATE_RECEIVED.DeferredStatus);
        const uint8_t* der = (encoded != nullptr) ? encoded->Buffer : nullptr;
        const size_t der_length = (encoded != nullptr) ? encoded->Length : 0;
        Publish(runtime, header, der, der_length);
        return QUIC_STATUS_PENDING;
    }
    // The two initiated-by events say why a connection is ending, not that it
    // has ended; the object and its id are still live here. They record the
    // reason and the code, and shutdown-complete below is the one place event 4
    // is emitted, once per connection id, after the id has stopped resolving.
    // All three of these run on the connection's own MsQuic worker, serialized,
    // which is what lets these fields be plain members.
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT:
        connection->shutdown_reason = "CultMesh QUIC connection was shut down by the transport (status=" +
            Hex(static_cast<uint32_t>(event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status)) + ", error=" +
            Hex(event->SHUTDOWN_INITIATED_BY_TRANSPORT.ErrorCode) + ").";
        connection->shutdown_code.store(event->SHUTDOWN_INITIATED_BY_TRANSPORT.ErrorCode);
        connection->shutdown_status = static_cast<int32_t>(event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status);
        break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER:
        connection->shutdown_reason = "CultMesh QUIC connection was shut down by the peer.";
        connection->shutdown_code.store(event->SHUTDOWN_INITIATED_BY_PEER.ErrorCode);
        break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE: {
        const uint64_t connection_id = connection->id;
        if (event->SHUTDOWN_COMPLETE.AppCloseInProgress) connection->handle_closed.store(true);
        // A host that ended the connection itself gets nothing else from MsQuic
        // — no initiated-by-peer, no initiated-by-transport — so without this
        // its own connection id simply stopped existing and the end of a
        // connection was observable only from the other side.
        cultmesh_quic_event header{};
        header.type = kEventConnectionShutdown;
        header.listener_id = connection->listener_id;
        header.connection_id = connection_id;
        header.code = connection->shutdown_code.load();
        header.status = connection->shutdown_status;
        const std::string reason = connection->shutdown_reason.empty()
            ? std::string("CultMesh QUIC connection was shut down by the host.")
            : connection->shutdown_reason;
        DestroyConnection(runtime, connection_id);
        Publish(runtime, header, reinterpret_cast<const uint8_t*>(reason.data()), reason.size());
        break;
    }
    default:
        break;
    }
    return QUIC_STATUS_SUCCESS;
}

QUIC_STATUS QUIC_API ListenerCallback(HQUIC, void* context, QUIC_LISTENER_EVENT* event) {
    auto* listener = static_cast<Listener*>(context);
    Runtime* runtime = listener->runtime;
    switch (event->Type) {
    case QUIC_LISTENER_EVENT_NEW_CONNECTION: {
        auto connection = std::make_shared<Connection>();
        connection->runtime = runtime;
        connection->handle = event->NEW_CONNECTION.Connection;
        connection->inbound = true;
        connection->listener_id = listener->id;
        connection->id = NextId(runtime);
        Connection* raw = connection.get();
        {
            std::lock_guard<std::mutex> lock(runtime->gate);
            runtime->connections.emplace(raw->id, std::move(connection));
        }
        runtime->api->SetCallbackHandler(
            event->NEW_CONNECTION.Connection, reinterpret_cast<void*>(ConnectionCallback), raw);
        const auto status = runtime->api->ConnectionSetConfiguration(
            event->NEW_CONNECTION.Connection, listener->configuration);
        if (QUIC_FAILED(status)) {
            SetError(runtime, "CultMesh QUIC inbound connection configuration failed (status=" +
                Hex(static_cast<uint32_t>(status)) + ").");
            // Returning a failure here is MsQuic's signal to reject and close
            // this connection itself, so the entry made a moment ago has to go
            // with it rather than sitting in the map naming a dead handle.
            raw->handle_closed.store(true);
            DestroyConnection(runtime, raw->id);
            return status;
        }
        PublishSimple(runtime, kEventListenerNewConnection, listener->id, raw->id, 0, 0, 0);
        break;
    }
    case QUIC_LISTENER_EVENT_STOP_COMPLETE:
        PublishSimple(runtime, kEventListenerStopped, listener->id, 0, 0, 0, 0);
        break;
    default:
        break;
    }
    return QUIC_STATUS_SUCCESS;
}

void FillSettings(QUIC_SETTINGS& settings) {
    settings.IdleTimeoutMs = kIdleTimeoutMs;
    settings.IsSet.IdleTimeoutMs = 1;
    settings.KeepAliveIntervalMs = kKeepAliveIntervalMs;
    settings.IsSet.KeepAliveIntervalMs = 1;
    settings.PeerUnidiStreamCount = kPeerUnidiStreamCount;
    settings.IsSet.PeerUnidiStreamCount = 1;
    settings.HandshakeIdleTimeoutMs = kHandshakeIdleTimeoutMs;
    settings.IsSet.HandshakeIdleTimeoutMs = 1;
}

QUIC_BUFFER AlpnBuffer() {
    return QUIC_BUFFER{
        static_cast<uint32_t>(sizeof(kAlpn) - 1),
        reinterpret_cast<uint8_t*>(const_cast<char*>(kAlpn))
    };
}

// Opened once per runtime, on first outbound connection.
QUIC_STATUS EnsureClientConfiguration(Runtime* runtime) {
    if (runtime->client_configuration != nullptr) return QUIC_STATUS_SUCCESS;

    QUIC_SETTINGS settings{};
    FillSettings(settings);
    QUIC_BUFFER alpn = AlpnBuffer();
    HQUIC configuration = nullptr;
    auto status = runtime->api->ConfigurationOpen(
        runtime->registration, &alpn, 1, &settings, sizeof(settings), nullptr, &configuration);
    if (QUIC_FAILED(status)) return status;

    QUIC_CREDENTIAL_CONFIG credentials{};
    credentials.Type = QUIC_CREDENTIAL_TYPE_NONE;
    credentials.Flags = static_cast<QUIC_CREDENTIAL_FLAGS>(
        QUIC_CREDENTIAL_FLAG_CLIENT |
        QUIC_CREDENTIAL_FLAG_INDICATE_CERTIFICATE_RECEIVED |
        QUIC_CREDENTIAL_FLAG_DEFER_CERTIFICATE_VALIDATION |
        QUIC_CREDENTIAL_FLAG_USE_PORTABLE_CERTIFICATES);
    status = runtime->api->ConfigurationLoadCredential(configuration, &credentials);
    if (QUIC_FAILED(status)) {
        runtime->api->ConfigurationClose(configuration);
        return status;
    }
    runtime->client_configuration = configuration;
    return QUIC_STATUS_SUCCESS;
}

// `pin` is the v1 shim's and nothing else passes it. It must be attached before
// `ConnectionStart`, not after it returns: the handshake runs on an MsQuic
// worker thread and reaches the certificate event immediately on loopback, so a
// pin applied afterwards loses the race, the connection takes the v2 path, and
// the handshake stalls waiting for an answer from a host that was never told to
// give one.
int32_t OpenConnection(Runtime* runtime, const char* host, uint16_t port, uint64_t* out_connection_id,
                       const std::array<uint8_t, 32>* pin) {
    if (runtime == nullptr || host == nullptr || *host == '\0' || port == 0 || out_connection_id == nullptr)
        return -1;
    *out_connection_id = 0;

    auto status = EnsureClientConfiguration(runtime);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC client configuration failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    auto connection = std::make_shared<Connection>();
    connection->runtime = runtime;
    connection->id = NextId(runtime);
    if (pin != nullptr) {
        connection->has_pin = true;
        connection->pin = *pin;
    }
    Connection* raw = connection.get();

    status = runtime->api->ConnectionOpen(runtime->registration, ConnectionCallback, raw, &raw->handle);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC connection open failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }
    {
        std::lock_guard<std::mutex> lock(runtime->gate);
        runtime->connections.emplace(raw->id, connection);
    }

    status = runtime->api->ConnectionStart(
        raw->handle, runtime->client_configuration, QUIC_ADDRESS_FAMILY_UNSPEC, host, port);
    if (QUIC_FAILED(status)) {
        // The map's reference goes and this local one goes with the scope; the
        // handle is closed by whichever of the two drops last, exactly once.
        DestroyConnection(runtime, raw->id);
        return Refuse(runtime, status, "CultMesh QUIC connection start failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    *out_connection_id = raw->id;
    return 0;
}

} // namespace

// ---------------------------------------------------------------------------
// C ABI v2: the runtime-neutral bridge.
// ---------------------------------------------------------------------------

CULTMESH_API int32_t cultmesh_quic_runtime_open(const char* app_name, void** out_runtime) {
    if (out_runtime == nullptr) return -1;
    *out_runtime = nullptr;
    auto runtime = std::make_unique<Runtime>();

    // No runtime yet to hang `last_status` on, so these two refusals lose the
    // raw bits. Everything after this point keeps them.
    auto status = MsQuicOpenVersion(kApiVersion, reinterpret_cast<const void**>(&runtime->api));
    if (QUIC_FAILED(status)) return MsQuicCode(status);

    const QUIC_REGISTRATION_CONFIG registration_config = {
        (app_name != nullptr && *app_name != '\0') ? app_name : "GameCult.Mesh.Quic.Native",
        QUIC_EXECUTION_PROFILE_TYPE_REAL_TIME
    };
    status = runtime->api->RegistrationOpen(&registration_config, &runtime->registration);
    if (QUIC_FAILED(status)) {
        MsQuicClose(runtime->api);
        return MsQuicCode(status);
    }

    *out_runtime = runtime.release();
    return 0;
}

CULTMESH_API void cultmesh_quic_runtime_close(void* handle) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return;

    // Quiesce before anything is closed or freed. Marking the runtime closing
    // refuses every call that has not started; waking the pollers gets the
    // blocked ones out of `next_event`; waiting for `active_calls` to reach zero
    // is what makes the teardown below safe at all. A wake without the wait is
    // how a poller returns into freed memory, so the wait is the contract, not
    // the wake. Objects are taken out of the maps here and released after the
    // lock, so their destructors run on this thread and close their handles.
    std::vector<std::shared_ptr<Stream>> streams;
    std::vector<std::shared_ptr<Connection>> connections;
    std::vector<std::shared_ptr<Listener>> listeners;
    {
        std::unique_lock<std::mutex> lock(runtime->gate);
        if (runtime->closing) return;
        runtime->closing = true;
        runtime->signal.notify_all();
        runtime->signal.wait(lock, [runtime] { return runtime->active_calls == 0; });
        CULTMESH_QUIC_ASSERT_QUIESCED(runtime->active_calls);
        for (auto& entry : runtime->streams) streams.push_back(std::move(entry.second));
        for (auto& entry : runtime->connections) connections.push_back(std::move(entry.second));
        for (auto& entry : runtime->listeners) listeners.push_back(std::move(entry.second));
        runtime->streams.clear();
        runtime->connections.clear();
        runtime->listeners.clear();
    }

    // Bottom-up, and by the app's own hand. `RegistrationClose` blocks until
    // every child handle the app opened has been closed *by the app*, and a
    // silent shutdown delivers no shutdown events at all, so leaning on the
    // callbacks to do the closing deadlocks the caller forever.
    streams.clear();
    for (auto& connection : connections)
        if (connection->handle != nullptr && !connection->handle_closed.load())
            runtime->api->ConnectionShutdown(
                connection->handle, QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT, kConnectionCloseCode);
    connections.clear();
    listeners.clear();
    if (runtime->client_configuration != nullptr) {
        runtime->api->ConfigurationClose(runtime->client_configuration);
        runtime->client_configuration = nullptr;
    }

    // No callback can be running past this point.
    if (runtime->registration != nullptr) {
        runtime->api->RegistrationClose(runtime->registration);
        runtime->registration = nullptr;
    }
    if (runtime->api != nullptr) {
        MsQuicClose(runtime->api);
        runtime->api = nullptr;
    }
    delete runtime;
}

CULTMESH_API int32_t cultmesh_quic_listener_open(
    void* handle, const char* host, uint16_t port,
    const uint8_t* pkcs12, int32_t pkcs12_len, const char* password,
    uint64_t* out_listener_id, uint16_t* out_bound_port) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr || pkcs12 == nullptr || pkcs12_len <= 0 || out_listener_id == nullptr)
        return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    *out_listener_id = 0;
    if (out_bound_port != nullptr) *out_bound_port = 0;

    // Held here until the listener is fully started. Every failure path below
    // simply returns: the destructor closes whatever was opened, and a partial
    // open therefore leaves neither a handle nor a map entry behind.
    auto listener = std::make_shared<Listener>();
    listener->runtime = runtime;
    listener->id = NextId(runtime);

    QUIC_SETTINGS settings{};
    FillSettings(settings);
    QUIC_BUFFER alpn = AlpnBuffer();
    auto status = runtime->api->ConfigurationOpen(
        runtime->registration, &alpn, 1, &settings, sizeof(settings), nullptr, &listener->configuration);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC listener configuration failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    // PKCS12 is the one credential type Schannel and OpenSSL both take, which
    // is why the provider ships a PKCS12 rather than a platform store handle.
    QUIC_CERTIFICATE_PKCS12 pkcs12_certificate{};
    pkcs12_certificate.Asn1Blob = pkcs12;
    pkcs12_certificate.Asn1BlobLength = static_cast<uint32_t>(pkcs12_len);
    pkcs12_certificate.PrivateKeyPassword = (password != nullptr && *password != '\0') ? password : nullptr;

    QUIC_CREDENTIAL_CONFIG credentials{};
    credentials.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12;
    credentials.CertificatePkcs12 = &pkcs12_certificate;
    credentials.Flags = QUIC_CREDENTIAL_FLAG_NONE;
    status = runtime->api->ConfigurationLoadCredential(listener->configuration, &credentials);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC listener certificate could not be loaded (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    Listener* raw = listener.get();
    status = runtime->api->ListenerOpen(runtime->registration, ListenerCallback, raw, &raw->handle);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC listener open failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    QUIC_ADDR address{};
    QuicAddrSetFamily(&address, QUIC_ADDRESS_FAMILY_UNSPEC);
    QuicAddrSetPort(&address, port);
    if (host != nullptr && *host != '\0' && !QuicAddrFromString(host, port, &address)) {
        SetError(runtime, "CultMesh QUIC listener host is not an IP address.");
        return -2;
    }

    // In the map before it starts accepting, because the callback that accepts
    // an inbound connection reads the listener through it.
    {
        std::lock_guard<std::mutex> lock(runtime->gate);
        runtime->listeners.emplace(raw->id, listener);
    }

    QUIC_BUFFER listener_alpn = AlpnBuffer();
    status = runtime->api->ListenerStart(raw->handle, &listener_alpn, 1, &address);
    if (QUIC_FAILED(status)) {
        Detach(runtime->gate, runtime->listeners, raw->id);
        return Refuse(runtime, status, "CultMesh QUIC listener start failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    // Port 0 means "pick one"; the host needs the chosen port to advertise it.
    QUIC_ADDR bound{};
    uint32_t bound_size = sizeof(bound);
    status = runtime->api->GetParam(raw->handle, QUIC_PARAM_LISTENER_LOCAL_ADDRESS, &bound_size, &bound);
    if (QUIC_FAILED(status)) {
        Detach(runtime->gate, runtime->listeners, raw->id);
        return Refuse(runtime, status, "CultMesh QUIC listener address could not be read (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }
    if (out_bound_port != nullptr) *out_bound_port = QuicAddrGetPort(&bound);
    *out_listener_id = raw->id;
    return 0;
}

CULTMESH_API void cultmesh_quic_listener_close(void* handle, uint64_t listener_id) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return;
    CallScope scope(runtime);
    if (!scope.entered()) return;
    // Taken out of the map, then released here: ~Listener closes the handle
    // (which is what produces event 9) and its configuration, exactly once.
    [[maybe_unused]] const auto detached = Detach(runtime->gate, runtime->listeners, listener_id);
}

CULTMESH_API int32_t cultmesh_quic_connection_open(
    void* handle, const char* host, uint16_t port, uint64_t* out_connection_id) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    return OpenConnection(runtime, host, port, out_connection_id, nullptr);
}

CULTMESH_API int32_t cultmesh_quic_connection_certificate_complete(
    void* handle, uint64_t connection_id, int32_t accept) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    const auto connection = FindConnection(runtime, connection_id);
    if (connection == nullptr) return -1;
    const auto status = runtime->api->ConnectionCertificateValidationComplete(
        connection->handle,
        accept != 0 ? static_cast<BOOLEAN>(1) : static_cast<BOOLEAN>(0),
        accept != 0 ? QUIC_TLS_ALERT_CODE_SUCCESS : QUIC_TLS_ALERT_CODE_BAD_CERTIFICATE);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC certificate validation completion failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }
    return 0;
}

CULTMESH_API void cultmesh_quic_connection_shutdown(void* handle, uint64_t connection_id, uint64_t code) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return;
    CallScope scope(runtime);
    if (!scope.entered()) return;
    const auto connection = FindConnection(runtime, connection_id);
    if (connection == nullptr || connection->closing.exchange(true)) return;
    // Kept for the shutdown-complete callback: this is the code the initiator's
    // own event 4 carries, matching the one the peer sees.
    connection->shutdown_code.store(code);
    if (connection->handle != nullptr)
        runtime->api->ConnectionShutdown(connection->handle, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, code);
}

CULTMESH_API int32_t cultmesh_quic_stream_open(
    void* handle, uint64_t connection_id, uint8_t kind, uint64_t* out_stream_id) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr || out_stream_id == nullptr) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    *out_stream_id = 0;
    if (kind != kReliableStream && kind != kLatestOnlyStream) return -2;
    const auto connection = FindConnection(runtime, connection_id);
    if (connection == nullptr) return -1;

    auto stream = std::make_shared<Stream>();
    stream->runtime = runtime;
    stream->owner = connection;
    stream->kind = kind;
    stream->has_kind = true;
    stream->id = NextId(runtime);
    Stream* raw = stream.get();

    auto status = runtime->api->StreamOpen(
        connection->handle, QUIC_STREAM_OPEN_FLAG_UNIDIRECTIONAL,
        StreamCallback, raw, &raw->handle);
    if (QUIC_FAILED(status)) {
        return Refuse(runtime, status, "CultMesh QUIC stream open failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }
    // In the map before it starts. `StreamStart` can reach shutdown-complete on
    // an MsQuic worker before it returns here, and that callback has to find the
    // stream to report it and to drop the map's reference.
    {
        std::lock_guard<std::mutex> lock(runtime->gate);
        runtime->streams.emplace(raw->id, stream);
    }
    status = runtime->api->StreamStart(raw->handle, QUIC_STREAM_START_FLAG_IMMEDIATE);
    if (QUIC_FAILED(status)) {
        DestroyStream(runtime, raw->id);
        return Refuse(runtime, status, "CultMesh QUIC stream start failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    // The kind byte is the first thing on the stream, which is what the peer's
    // reassembly reads before any length prefix.
    auto* request = new SendRequest();
    request->bytes.assign(1, kind);
    request->buffer.Buffer = request->bytes.data();
    request->buffer.Length = 1;
    status = runtime->api->StreamSend(raw->handle, &request->buffer, 1, QUIC_SEND_FLAG_NONE, request);
    if (QUIC_FAILED(status)) {
        delete request;
        return Refuse(runtime, status, "CultMesh QUIC stream kind could not be sent (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }

    *out_stream_id = raw->id;
    return 0;
}

CULTMESH_API int32_t cultmesh_quic_stream_send_frame(
    void* handle, uint64_t stream_id, const uint8_t* encoded_frame, int32_t length, int32_t fin) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr || encoded_frame == nullptr || length <= 0) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    if (static_cast<uint32_t>(length) > kMaximumEncodedFrameBytes) return -2;
    // The reference is held for the whole `StreamSend` below, so the worker
    // cannot free the stream or close its handle underneath this call.
    const auto stream = FindStream(runtime, stream_id);
    if (stream == nullptr || stream->handle == nullptr) return -1;

    // The bytes are copied because MsQuic reads them after this returns; the
    // 4-byte LE length prefix is the QUIC stream framing, and it is added here
    // so that framing lives in one place for both directions.
    auto* request = new SendRequest();
    request->is_frame = true;
    request->bytes.resize(sizeof(uint32_t) + static_cast<size_t>(length));
    WriteUInt32LittleEndian(request->bytes.data(), static_cast<uint32_t>(length));
    std::memcpy(request->bytes.data() + sizeof(uint32_t), encoded_frame, static_cast<size_t>(length));
    request->buffer.Buffer = request->bytes.data();
    request->buffer.Length = static_cast<uint32_t>(request->bytes.size());

    const auto flags = (fin != 0) ? QUIC_SEND_FLAG_FIN : QUIC_SEND_FLAG_NONE;
    const auto status = runtime->api->StreamSend(stream->handle, &request->buffer, 1, flags, request);
    if (QUIC_FAILED(status)) {
        delete request;
        return Refuse(runtime, status, "CultMesh QUIC frame send failed (status=" +
            Hex(static_cast<uint32_t>(status)) + ").");
    }
    return 0;
}

CULTMESH_API void cultmesh_quic_stream_shutdown(void* handle, uint64_t stream_id, uint64_t code) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return;
    CallScope scope(runtime);
    if (!scope.entered()) return;
    const auto stream = FindStream(runtime, stream_id);
    if (stream == nullptr || stream->handle == nullptr) return;
    runtime->api->StreamShutdown(stream->handle, QUIC_STREAM_SHUTDOWN_FLAG_ABORT, code);
}

// The one blocking crossing; see section 6 of the header for its returns. The
// call scope is what `cultmesh_quic_runtime_close` waits on: a poller blocked
// here is woken by `closing` and counted out before anything is freed.
CULTMESH_API int32_t cultmesh_quic_next_event(
    void* handle, int32_t timeout_ms, cultmesh_quic_event* out_event,
    uint8_t* payload, int32_t payload_capacity, int32_t* out_required) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr || out_event == nullptr || out_required == nullptr || payload_capacity < 0)
        return -1;
    // A null buffer with a non-zero capacity is a lie about the buffer. Taking
    // it meant the event was consumed and its payload silently dropped.
    if (payload == nullptr && payload_capacity > 0) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    *out_required = 0;

    std::unique_lock<std::mutex> lock(runtime->gate);
    if (runtime->events.empty() && timeout_ms > 0) {
        runtime->signal.wait_for(lock, std::chrono::milliseconds(timeout_ms),
            [runtime] { return !runtime->events.empty() || runtime->closing; });
    }
    if (runtime->events.empty()) return 0;

    const QueuedEvent& queued = runtime->events.front();
    const auto required = static_cast<int32_t>(queued.payload.size());
    if (required > payload_capacity) {
        *out_required = required;
        return 2;
    }
    *out_event = queued.header;
    if (required > 0 && payload != nullptr)
        std::memcpy(payload, queued.payload.data(), static_cast<size_t>(required));
    *out_required = required;
    runtime->events.pop_front();
    return 1;
}

CULTMESH_API int32_t cultmesh_quic_last_error(void* handle, char* destination, int32_t capacity) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr || destination == nullptr || capacity <= 0) return -1;
    CallScope scope(runtime);
    if (!scope.entered()) return -1;
    std::lock_guard<std::mutex> lock(runtime->gate);
    const size_t count = (std::min)(runtime->error.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(destination, runtime->error.data(), count);
    destination[count] = '\0';
    return static_cast<int32_t>(count);
}

CULTMESH_API int32_t cultmesh_quic_last_status(void* handle) {
    auto* runtime = static_cast<Runtime*>(handle);
    if (runtime == nullptr) return 0;
    CallScope scope(runtime);
    if (!scope.entered()) return 0;
    std::lock_guard<std::mutex> lock(runtime->gate);
    return runtime->last_status;
}

// ---------------------------------------------------------------------------
// C ABI v1: the Unity shim. Windows only, by the same construction as its only
// caller (`CultMeshNativeQuicRealtimeTransport.CanConnect` is Windows-gated).
//
// These five exports are the contract the shipped Unity package imports by
// name. They are preserved byte for byte in signature and behaviour, and they
// decide nothing the v2 path does not: a v1 client is a private runtime with one
// connection. The single exception is the pin, which v1 compares here in C
// because that is what the managed connector was built against.
// ---------------------------------------------------------------------------

#if defined(_WIN32)

#include <windows.h>
#include <wincrypt.h>
#include <bcrypt.h>

namespace {

enum class ClientState : int32_t {
    Connecting = 0,
    Connected = 1,
    Failed = 2,
    Closed = 3,
};

struct V1Client {
    Runtime* runtime = nullptr;
    uint64_t connection_id = 0;
    ClientState state = ClientState::Connecting;
    std::deque<std::vector<uint8_t>> frames;
    std::string error;
    bool closing = false;
    // The managed connector polls `state` and `poll` from its own loop and may
    // read `error` from the caller's thread, so the drain and everything it
    // mutates sit behind one lock. Held only around `Pump` and the fields; it is
    // always taken before the runtime's, never after.
    std::mutex gate;
};

std::string LoadedMsQuicPath() {
    const auto module = GetModuleHandleW(L"msquic.dll");
    if (module == nullptr) return "unresolved";
    std::array<wchar_t, 32768> path{};
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) return "unresolved";
    const auto required = WideCharToMultiByte(
        CP_UTF8, 0, path.data(), static_cast<int>(length), nullptr, 0, nullptr, nullptr);
    if (required <= 0) return "unresolved";
    std::string utf8(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, path.data(), static_cast<int>(length), utf8.data(), required, nullptr, nullptr);
    return utf8;
}

bool ParseHexNibble(char value, uint8_t& nibble) {
    if (value >= '0' && value <= '9') nibble = static_cast<uint8_t>(value - '0');
    else if (value >= 'a' && value <= 'f') nibble = static_cast<uint8_t>(value - 'a' + 10);
    else if (value >= 'A' && value <= 'F') nibble = static_cast<uint8_t>(value - 'A' + 10);
    else return false;
    return true;
}

bool ParsePin(const char* value, std::array<uint8_t, 32>& pin) {
    if (value == nullptr || std::strlen(value) != 64) return false;
    for (size_t index = 0; index < pin.size(); ++index) {
        uint8_t high = 0;
        uint8_t low = 0;
        if (!ParseHexNibble(value[index * 2], high) || !ParseHexNibble(value[(index * 2) + 1], low))
            return false;
        pin[index] = static_cast<uint8_t>((high << 4) | low);
    }
    return true;
}

// Drains the runtime's event queue into the v1 client's own shape: frames for
// `poll`, a state for `state`, an error string for `error`. This is where the
// v1 contract is reconstructed from v2 events, and the only place it is.
void Pump(V1Client* client) {
    cultmesh_quic_event header{};
    std::vector<uint8_t> payload;
    for (;;) {
        int32_t required = 0;
        auto result = cultmesh_quic_next_event(
            client->runtime, 0, &header,
            payload.empty() ? nullptr : payload.data(),
            static_cast<int32_t>(payload.size()), &required);
        if (result == 2) {
            payload.resize(static_cast<size_t>(required));
            continue;
        }
        if (result != 1) break;

        switch (header.type) {
        case kEventConnectionConnected:
            if (client->state == ClientState::Connecting) client->state = ClientState::Connected;
            break;
        case kEventStreamFrame:
            client->frames.emplace_back(payload.begin(), payload.begin() + header.payload_length);
            break;
        case kEventConnectionShutdown:
            if (client->closing) {
                client->state = ClientState::Closed;
            } else if (client->state != ClientState::Failed) {
                client->state = ClientState::Failed;
                if (client->error.empty()) {
                    char runtime_error[1024]{};
                    cultmesh_quic_last_error(client->runtime, runtime_error, sizeof(runtime_error));
                    // The pin refusal is the runtime's, and the managed
                    // connector matches on its wording; anything else is the
                    // transport's own reason plus where msquic came from.
                    client->error = runtime_error[0] != '\0'
                        ? std::string(runtime_error)
                        : std::string(reinterpret_cast<const char*>(payload.data()),
                                      static_cast<size_t>(header.payload_length));
                    client->error += " (msquic=" + LoadedMsQuicPath() + ")";
                }
            }
            break;
        default:
            break;
        }
    }
}

bool CertificateMatchesPin(Connection* connection, QUIC_CERTIFICATE* certificate) {
    if (certificate == nullptr) return false;
    const auto* encoded = reinterpret_cast<const QUIC_BUFFER*>(certificate);
    if (encoded->Buffer == nullptr || encoded->Length == 0) return false;
    BYTE digest[32]{};
    DWORD digest_length = sizeof(digest);
    if (!CryptHashCertificate2(
            BCRYPT_SHA256_ALGORITHM, 0, nullptr,
            encoded->Buffer, encoded->Length, digest, &digest_length) ||
        digest_length != sizeof(digest))
        return false;
    return std::memcmp(digest, connection->pin.data(), sizeof(digest)) == 0;
}

} // namespace

CULTMESH_API int32_t cultmesh_quic_open(
    const char* host, uint16_t port, const char* certificate_sha256, V1Client** result) {
    if (host == nullptr || *host == '\0' || port == 0 || result == nullptr) return -1;
    *result = nullptr;

    std::array<uint8_t, 32> pin{};
    if (!ParsePin(certificate_sha256, pin)) return -2;

    auto client = std::make_unique<V1Client>();
    void* runtime_handle = nullptr;
    const auto opened = cultmesh_quic_runtime_open("GameCult.Mesh.Quic.Native", &runtime_handle);
    if (opened != 0) return opened;
    client->runtime = static_cast<Runtime*>(runtime_handle);

    // The pin goes in before the connection starts; see OpenConnection.
    const auto status = OpenConnection(client->runtime, host, port, &client->connection_id, &pin);
    if (status != 0) {
        cultmesh_quic_runtime_close(client->runtime);
        return status;
    }

    *result = client.release();
    return 0;
}

CULTMESH_API int32_t cultmesh_quic_state(V1Client* client) {
    if (client == nullptr) return static_cast<int32_t>(ClientState::Failed);
    std::lock_guard<std::mutex> lock(client->gate);
    Pump(client);
    return static_cast<int32_t>(client->state);
}

CULTMESH_API int32_t cultmesh_quic_poll(
    V1Client* client, uint8_t* destination, int32_t destination_length, int32_t* required_length) {
    if (client == nullptr || required_length == nullptr || destination_length < 0) return -1;
    std::lock_guard<std::mutex> lock(client->gate);
    Pump(client);
    if (client->frames.empty()) {
        *required_length = 0;
        return client->state == ClientState::Failed ? -2 : 0;
    }
    const auto& frame = client->frames.front();
    if (frame.size() > static_cast<size_t>(INT32_MAX)) return -3;
    *required_length = static_cast<int32_t>(frame.size());
    if (destination == nullptr || destination_length < *required_length) return 2;
    std::memcpy(destination, frame.data(), frame.size());
    client->frames.pop_front();
    return 1;
}

CULTMESH_API int32_t cultmesh_quic_error(V1Client* client, char* destination, int32_t destination_length) {
    if (client == nullptr || destination == nullptr || destination_length <= 0) return -1;
    std::lock_guard<std::mutex> lock(client->gate);
    Pump(client);
    const size_t count = (std::min)(client->error.size(), static_cast<size_t>(destination_length - 1));
    std::memcpy(destination, client->error.data(), count);
    destination[count] = '\0';
    return static_cast<int32_t>(count);
}

CULTMESH_API void cultmesh_quic_close(V1Client* client) {
    if (client == nullptr) return;
    {
        std::lock_guard<std::mutex> lock(client->gate);
        client->closing = true;
    }
    // `runtime_close` already shuts the connection down silently with the same
    // CULT close code and then closes the handle, which is what v1 did before
    // it delegated. Shutting it down here as well would only race that.
    cultmesh_quic_runtime_close(client->runtime);
    client->state = ClientState::Closed;
    delete client;
}

#endif // _WIN32
