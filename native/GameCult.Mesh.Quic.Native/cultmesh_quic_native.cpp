#include <windows.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <msquic.h>

#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if defined(GAMECULT_MESH_QUIC_NATIVE_EXPORTS)
#define CULTMESH_API extern "C" __declspec(dllexport)
#else
#define CULTMESH_API extern "C" __declspec(dllimport)
#endif

namespace {

constexpr uint32_t kApiVersion = 2;
constexpr uint64_t kConnectionCloseCode = 0x43554c54;
constexpr uint64_t kStreamAbortCode = 0x53544154;
constexpr uint8_t kReliableStream = 1;
constexpr uint8_t kLatestOnlyStream = 2;
constexpr uint32_t kMaximumEncodedFrameBytes = (64u * 1024u * 1024u) + 37u + (3u * 65535u);
constexpr char kAlpn[] = "cultmesh-state-v1";

enum class ClientState : int32_t {
    Connecting = 0,
    Connected = 1,
    Failed = 2,
    Closed = 3,
};

struct Client;

struct StreamContext {
    explicit StreamContext(Client* client) : owner(client) {}
    Client* owner;
    std::vector<uint8_t> buffered;
    uint8_t kind = 0;
    bool has_kind = false;
};

struct Client {
    const QUIC_API_TABLE* api = nullptr;
    HQUIC registration = nullptr;
    HQUIC configuration = nullptr;
    HQUIC connection = nullptr;
    std::array<uint8_t, 32> certificate_pin{};
    std::atomic<ClientState> state{ClientState::Connecting};
    std::atomic<bool> closing{false};
    std::mutex gate;
    std::deque<std::vector<uint8_t>> received;
    std::string error;
};

void SetFailure(Client* client, const std::string& message) {
    if (client == nullptr || client->closing.load()) return;
    {
        std::lock_guard<std::mutex> lock(client->gate);
        if (client->error.empty()) client->error = message;
    }
    client->state.store(ClientState::Failed);
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

uint32_t ReadUInt32LittleEndian(const uint8_t* value) {
    return static_cast<uint32_t>(value[0]) |
        (static_cast<uint32_t>(value[1]) << 8) |
        (static_cast<uint32_t>(value[2]) << 16) |
        (static_cast<uint32_t>(value[3]) << 24);
}

void FailStream(StreamContext* stream, const char* message) {
    SetFailure(stream->owner, message);
    if (stream->owner != nullptr && stream->owner->api != nullptr && stream->owner->connection != nullptr)
        stream->owner->api->ConnectionShutdown(
            stream->owner->connection,
            QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT,
            kStreamAbortCode);
}

bool ConsumeFrames(StreamContext* stream) {
    auto& bytes = stream->buffered;
    if (!stream->has_kind) {
        if (bytes.empty()) return true;
        stream->kind = bytes.front();
        bytes.erase(bytes.begin());
        stream->has_kind = true;
        if (stream->kind != kReliableStream && stream->kind != kLatestOnlyStream) {
            FailStream(stream, "CultMesh QUIC stream kind is invalid.");
            return false;
        }
    }

    while (bytes.size() >= sizeof(uint32_t)) {
        const uint32_t length = ReadUInt32LittleEndian(bytes.data());
        if (length == 0 || length > kMaximumEncodedFrameBytes) {
            FailStream(stream, "CultMesh QUIC frame length is invalid.");
            return false;
        }
        const size_t framed_length = sizeof(uint32_t) + static_cast<size_t>(length);
        if (bytes.size() < framed_length) return true;
        std::vector<uint8_t> frame(length);
        std::memcpy(frame.data(), bytes.data() + sizeof(uint32_t), length);
        {
            std::lock_guard<std::mutex> lock(stream->owner->gate);
            stream->owner->received.emplace_back(std::move(frame));
        }
        bytes.erase(bytes.begin(), bytes.begin() + static_cast<std::ptrdiff_t>(framed_length));
        if (stream->kind == kLatestOnlyStream && !bytes.empty()) {
            FailStream(stream, "CultMesh latest-only QUIC stream carried multiple frames.");
            return false;
        }
    }
    return true;
}

QUIC_STATUS QUIC_API StreamCallback(HQUIC stream_handle, void* context, QUIC_STREAM_EVENT* event) {
    auto* stream = static_cast<StreamContext*>(context);
    switch (event->Type) {
    case QUIC_STREAM_EVENT_RECEIVE:
        for (uint32_t index = 0; index < event->RECEIVE.BufferCount; ++index) {
            const auto& buffer = event->RECEIVE.Buffers[index];
            stream->buffered.insert(stream->buffered.end(), buffer.Buffer, buffer.Buffer + buffer.Length);
        }
        ConsumeFrames(stream);
        break;
    case QUIC_STREAM_EVENT_PEER_SEND_SHUTDOWN:
        if (!stream->buffered.empty())
            FailStream(stream, "CultMesh QUIC stream ended with a truncated frame.");
        break;
    case QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE:
        if (!event->SHUTDOWN_COMPLETE.AppCloseInProgress)
            stream->owner->api->StreamClose(stream_handle);
        delete stream;
        break;
    default:
        break;
    }
    return QUIC_STATUS_SUCCESS;
}

bool CertificateMatchesPin(Client* client, QUIC_CERTIFICATE* certificate) {
    if (certificate == nullptr) return false;
    const auto* encoded = reinterpret_cast<const QUIC_BUFFER*>(certificate);
    if (encoded->Buffer == nullptr || encoded->Length == 0) return false;
    BYTE digest[32]{};
    DWORD digest_length = sizeof(digest);
    if (!CryptHashCertificate2(
            BCRYPT_SHA256_ALGORITHM,
            0,
            nullptr,
            encoded->Buffer,
            encoded->Length,
            digest,
            &digest_length) || digest_length != sizeof(digest))
        return false;
    return std::memcmp(digest, client->certificate_pin.data(), sizeof(digest)) == 0;
}

QUIC_STATUS QUIC_API ConnectionCallback(HQUIC connection, void* context, QUIC_CONNECTION_EVENT* event) {
    auto* client = static_cast<Client*>(context);
    switch (event->Type) {
    case QUIC_CONNECTION_EVENT_CONNECTED:
        client->state.store(ClientState::Connected);
        break;
    case QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED: {
        auto* stream = new (std::nothrow) StreamContext(client);
        if (stream == nullptr) return QUIC_STATUS_OUT_OF_MEMORY;
        client->api->SetCallbackHandler(
            event->PEER_STREAM_STARTED.Stream,
            reinterpret_cast<void*>(StreamCallback),
            stream);
        break;
    }
    case QUIC_CONNECTION_EVENT_PEER_CERTIFICATE_RECEIVED:
        if (!CertificateMatchesPin(client, event->PEER_CERTIFICATE_RECEIVED.Certificate)) {
            SetFailure(client, "CultMesh QUIC provider certificate does not match the advertised SHA-256 pin.");
            return QUIC_STATUS_BAD_CERTIFICATE;
        }
        return QUIC_STATUS_SUCCESS;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT:
        if (!client->closing.load())
            SetFailure(client, "CultMesh QUIC connection was shut down by the transport.");
        break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER:
        if (!client->closing.load())
            SetFailure(client, "CultMesh QUIC connection was shut down by the provider.");
        break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE:
        if (client->closing.load()) client->state.store(ClientState::Closed);
        break;
    default:
        break;
    }
    return QUIC_STATUS_SUCCESS;
}

void CloseHandles(Client* client) {
    if (client->connection != nullptr) {
        client->api->ConnectionClose(client->connection);
        client->connection = nullptr;
    }
    if (client->configuration != nullptr) {
        client->api->ConfigurationClose(client->configuration);
        client->configuration = nullptr;
    }
    if (client->registration != nullptr) {
        client->api->RegistrationClose(client->registration);
        client->registration = nullptr;
    }
    if (client->api != nullptr) {
        MsQuicClose(client->api);
        client->api = nullptr;
    }
}

} // namespace

CULTMESH_API int32_t cultmesh_quic_open(
    const char* host,
    uint16_t port,
    const char* certificate_sha256,
    Client** result) {
    if (host == nullptr || *host == '\0' || port == 0 || result == nullptr) return -1;
    *result = nullptr;
    auto client = std::make_unique<Client>();
    if (!ParsePin(certificate_sha256, client->certificate_pin)) return -2;

    QUIC_STATUS status = MsQuicOpenVersion(
        kApiVersion,
        reinterpret_cast<const void**>(&client->api));
    if (QUIC_FAILED(status)) return status;

    const QUIC_REGISTRATION_CONFIG registration_config = {
        "GameCult.Mesh.Quic.Native",
        QUIC_EXECUTION_PROFILE_TYPE_REAL_TIME
    };
    status = client->api->RegistrationOpen(&registration_config, &client->registration);
    if (QUIC_FAILED(status)) {
        CloseHandles(client.get());
        return status;
    }

    QUIC_SETTINGS settings{};
    settings.IdleTimeoutMs = 30000;
    settings.IsSet.IdleTimeoutMs = TRUE;
    settings.KeepAliveIntervalMs = 5000;
    settings.IsSet.KeepAliveIntervalMs = TRUE;
    settings.PeerUnidiStreamCount = 1024;
    settings.IsSet.PeerUnidiStreamCount = TRUE;
    const QUIC_BUFFER alpn = {
        static_cast<uint32_t>(sizeof(kAlpn) - 1),
        reinterpret_cast<uint8_t*>(const_cast<char*>(kAlpn))
    };
    status = client->api->ConfigurationOpen(
        client->registration,
        &alpn,
        1,
        &settings,
        sizeof(settings),
        nullptr,
        &client->configuration);
    if (QUIC_FAILED(status)) {
        CloseHandles(client.get());
        return status;
    }

    QUIC_CREDENTIAL_CONFIG credentials{};
    credentials.Type = QUIC_CREDENTIAL_TYPE_NONE;
    credentials.Flags = static_cast<QUIC_CREDENTIAL_FLAGS>(
        QUIC_CREDENTIAL_FLAG_CLIENT |
        QUIC_CREDENTIAL_FLAG_INDICATE_CERTIFICATE_RECEIVED |
        QUIC_CREDENTIAL_FLAG_DEFER_CERTIFICATE_VALIDATION |
        QUIC_CREDENTIAL_FLAG_USE_PORTABLE_CERTIFICATES);
    status = client->api->ConfigurationLoadCredential(client->configuration, &credentials);
    if (QUIC_FAILED(status)) {
        CloseHandles(client.get());
        return status;
    }

    status = client->api->ConnectionOpen(
        client->registration,
        ConnectionCallback,
        client.get(),
        &client->connection);
    if (QUIC_FAILED(status)) {
        CloseHandles(client.get());
        return status;
    }
    status = client->api->ConnectionStart(
        client->connection,
        client->configuration,
        QUIC_ADDRESS_FAMILY_UNSPEC,
        host,
        port);
    if (QUIC_FAILED(status)) {
        CloseHandles(client.get());
        return status;
    }

    *result = client.release();
    return 0;
}

CULTMESH_API int32_t cultmesh_quic_state(Client* client) {
    if (client == nullptr) return static_cast<int32_t>(ClientState::Failed);
    return static_cast<int32_t>(client->state.load());
}

CULTMESH_API int32_t cultmesh_quic_poll(
    Client* client,
    uint8_t* destination,
    int32_t destination_length,
    int32_t* required_length) {
    if (client == nullptr || required_length == nullptr || destination_length < 0) return -1;
    std::lock_guard<std::mutex> lock(client->gate);
    if (client->received.empty()) {
        *required_length = 0;
        return client->state.load() == ClientState::Failed ? -2 : 0;
    }
    const auto& frame = client->received.front();
    if (frame.size() > static_cast<size_t>(INT32_MAX)) return -3;
    *required_length = static_cast<int32_t>(frame.size());
    if (destination == nullptr || destination_length < *required_length) return 2;
    std::memcpy(destination, frame.data(), frame.size());
    client->received.pop_front();
    return 1;
}

CULTMESH_API int32_t cultmesh_quic_error(
    Client* client,
    char* destination,
    int32_t destination_length) {
    if (client == nullptr || destination == nullptr || destination_length <= 0) return -1;
    std::lock_guard<std::mutex> lock(client->gate);
    const size_t count = (std::min)(client->error.size(), static_cast<size_t>(destination_length - 1));
    std::memcpy(destination, client->error.data(), count);
    destination[count] = '\0';
    return static_cast<int32_t>(count);
}

CULTMESH_API void cultmesh_quic_close(Client* client) {
    if (client == nullptr) return;
    client->closing.store(true);
    if (client->connection != nullptr && client->api != nullptr)
        client->api->ConnectionShutdown(client->connection, QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT, kConnectionCloseCode);
    CloseHandles(client);
    client->state.store(ClientState::Closed);
    delete client;
}
