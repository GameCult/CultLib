using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Mesh;

namespace GameCult.Mesh.Quic.Native
{
    public sealed class CultMeshNativeQuicRealtimeConnectorOptions
    {
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(2);
    }

    /// <summary>
    /// Creates receive-capable MsQuic sessions on Unity-compatible managed runtimes.
    /// Commands and receipts remain on their typed control plane.
    /// </summary>
    public sealed class CultMeshNativeQuicRealtimeTransportConnector : ICultMeshRealtimeTransportConnector
    {
        public const string Scheme = "cultmesh-state+quic";
        private readonly CultMeshNativeQuicRealtimeConnectorOptions _options;

        public CultMeshNativeQuicRealtimeTransportConnector(
            CultMeshNativeQuicRealtimeConnectorOptions? options = null)
        {
            _options = options ?? new CultMeshNativeQuicRealtimeConnectorOptions();
            if (_options.HandshakeTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "QUIC handshake timeout must be positive.");
            if (_options.PollInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "QUIC poll interval must be positive.");
        }

        public string ConnectorId => "msquic-native-realtime";
        public int Priority => 0;

        public bool CanConnect(CultMeshTransportCandidate candidate) =>
            candidate != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            TryParseEndpoint(candidate.Endpoint, out _, out _, out _);

        public async Task<ICultMeshRealtimeTransport> ConnectAsync(
            CultMeshTransportCandidate candidate,
            CultMeshEndpointId endpointId,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (endpointId == null) throw new ArgumentNullException(nameof(endpointId));
            if (!TryParseEndpoint(candidate.Endpoint, out var host, out var port, out var pin))
                throw new NotSupportedException($"Native QUIC connector does not support '{candidate.Endpoint}'.");

            IntPtr handle;
            int opened;
            try
            {
                opened = NativeMethods.Open(host, checked((ushort)port), pin, out handle);
            }
            catch (DllNotFoundException error)
            {
                throw new PlatformNotSupportedException(
                    "CultMesh native QUIC requires gamecult_mesh_quic_native and msquic beside the managed runtime.",
                    error);
            }
            catch (EntryPointNotFoundException error)
            {
                throw new PlatformNotSupportedException("The installed CultMesh native QUIC bridge is incompatible.", error);
            }
            if (opened != 0 || handle == IntPtr.Zero)
                throw new IOException($"CultMesh native QUIC initialization failed with status 0x{opened:X8}.");

            var transport = new CultMeshNativeQuicRealtimeTransport(
                candidate.Endpoint,
                handle,
                _options.PollInterval);
            try
            {
                await transport.WaitUntilConnectedAsync(_options.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
                return transport;
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }

        private static bool TryParseEndpoint(
            string? endpoint,
            out string host,
            out int port,
            out string certificatePin)
        {
            host = string.Empty;
            port = 0;
            certificatePin = string.Empty;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
                return false;
            var pin = ParseQueryValue(uri.Query, "cert-sha256");
            if (pin == null || pin.Length != 64) return false;
            for (var index = 0; index < pin.Length; index++)
                if (!Uri.IsHexDigit(pin[index])) return false;
            host = uri.Host;
            port = uri.Port;
            certificatePin = pin.ToUpperInvariant();
            return true;
        }

        private static string? ParseQueryValue(string query, string key)
        {
            foreach (var component in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = component.IndexOf('=');
                var candidateKey = separator < 0 ? component : component.Substring(0, separator);
                if (!string.Equals(Uri.UnescapeDataString(candidateKey), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(component.Substring(separator + 1));
            }
            return null;
        }
    }

    internal sealed class CultMeshNativeQuicRealtimeTransport : ICultMeshRealtimeTransport
    {
        private readonly object _gate = new object();
        private readonly TimeSpan _pollInterval;
        private IntPtr _handle;
        private bool _disposed;

        public CultMeshNativeQuicRealtimeTransport(string endpoint, IntPtr handle, TimeSpan pollInterval)
        {
            Endpoint = endpoint;
            _handle = handle;
            _pollInterval = pollInterval;
        }

        public string TransportId => "msquic-native-realtime";
        public string Endpoint { get; }

        public Task SendAsync(CultMeshRealtimeFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromException(new NotSupportedException(
                "The Unity-compatible native QUIC connector currently owns provider-to-client state only."));

        public async Task<CultMeshRealtimeFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? bytes = null;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var state = NativeMethods.State(_handle);
                    if (state == 2) throw new IOException(ReadNativeError(_handle));
                    var first = NativeMethods.Poll(_handle, IntPtr.Zero, 0, out var required);
                    if (first < 0) throw new IOException(ReadNativeError(_handle));
                    if (first == 2 && required > 0)
                    {
                        bytes = new byte[required];
                        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        try
                        {
                            var second = NativeMethods.Poll(
                                _handle,
                                pinned.AddrOfPinnedObject(),
                                bytes.Length,
                                out var received);
                            if (second != 1 || received != bytes.Length)
                                throw new IOException("CultMesh native QUIC frame changed while it was acquired.");
                        }
                        finally
                        {
                            pinned.Free();
                        }
                    }
                }
                if (bytes != null) return CultMeshRealtimeWireProtocol.DecodeFrame(bytes);
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task WaitUntilConnectedAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ThrowIfDisposed();
                    var state = NativeMethods.State(_handle);
                    if (state == 1) return;
                    if (state == 2) throw new IOException(ReadNativeError(_handle));
                }
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException($"CultMesh native QUIC handshake with '{Endpoint}' timed out.");
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_handle != IntPtr.Zero) NativeMethods.Close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed || _handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(CultMeshNativeQuicRealtimeTransport));
        }

        private static string ReadNativeError(IntPtr handle)
        {
            var bytes = new byte[1024];
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var count = NativeMethods.Error(handle, pinned.AddrOfPinnedObject(), bytes.Length);
                return count > 0
                    ? Encoding.UTF8.GetString(bytes, 0, count)
                    : "CultMesh native QUIC transport failed.";
            }
            finally
            {
                pinned.Free();
            }
        }
    }

    internal static class NativeMethods
    {
        private const string Library = "gamecult_mesh_quic_native";

        [DllImport(Library, EntryPoint = "cultmesh_quic_open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int Open(string host, ushort port, string certificateSha256, out IntPtr client);

        [DllImport(Library, EntryPoint = "cultmesh_quic_state", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int State(IntPtr client);

        [DllImport(Library, EntryPoint = "cultmesh_quic_poll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Poll(IntPtr client, IntPtr destination, int destinationLength, out int requiredLength);

        [DllImport(Library, EntryPoint = "cultmesh_quic_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Error(IntPtr client, IntPtr destination, int destinationLength);

        [DllImport(Library, EntryPoint = "cultmesh_quic_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr client);
    }
}
