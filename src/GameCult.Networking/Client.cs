using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameCult.Logging;
using LiteNetLib;
using MessagePack;
using R3;

namespace GameCult.Networking
{
    /// <summary>
    /// Describes the client's reconnect posture.
    /// </summary>
    public enum ClientReconnectState
    {
        /// <summary>
        /// The client is connected or otherwise not trying to reconnect.
        /// </summary>
        Idle,

        /// <summary>
        /// The client is waiting for the next reconnect attempt.
        /// </summary>
        WaitingToReconnect,

        /// <summary>
        /// The client is actively attempting a reconnect.
        /// </summary>
        Reconnecting
    }

    /// <summary>
    /// Provides client-side connection, authentication, and message dispatch logic.
    /// </summary>
    public class Client : IDisposable
    {
        /// <summary>
        /// Raised when the client encounters a user-visible error.
        /// </summary>
        public event Action<string>? OnError; // Hook for important user-facing feedback (e.g. via modal UI element)
        /// <summary>
        /// Raised when reconnect state changes.
        /// </summary>
        public event Action<ClientReconnectState>? OnReconnectStateChanged;
        private NetManager? _client;
        private NetPeer? _peer;
        private LiteNetLibTransportConnection? _transportConnection;
        private IDisposable? _pollSubscription;
        private IDisposable? _reconnectSubscription;
        private bool _disposed;
        private bool _manualDisconnect;
        private bool _isReconnecting;
        private readonly CultNetReconnectController _reconnectController;

        private readonly ConcurrentDictionary<Type, Delegate> _messageDelegates = new();
        private readonly ConcurrentDictionary<Type, Delegate> _cultNetMessageDelegates = new();

        private string? _sessionToken;
        private string _lastHost = "localhost";
        private int _lastPort;
        private readonly ClientSecurityOptions _security;
        private ILogger _logger = new NullLogger();
        
        /// <summary>
        /// Gets or sets the logger used by the client.
        /// </summary>
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        /// <summary>
        /// Gets the last reported round-trip latency in milliseconds.
        /// </summary>
        public int Ping { get; private set; }

        /// <summary>
        /// Gets the current reconnect posture exposed to consumers.
        /// </summary>
        public ClientReconnectState ReconnectState { get; private set; } = ClientReconnectState.Idle;

        /// <summary>
        /// Gets the portable reconnect policy used by this client.
        /// </summary>
        public CultNetReconnectPolicy ReconnectPolicy => _reconnectController.Policy;

        /// <summary>
        /// Gets a transport profile describing the client's current LiteNetLib production lane.
        /// </summary>
        public CultNetTransportProfile TransportProfile => CultNetTransportProfiles.CreateLiteNetLib(
            "csharp-client",
            new LiteNetLibTransportProfileOptions
            {
                TransportId = "litenetlib-client",
                Host = _lastHost,
                Port = _lastPort > 0 ? _lastPort : null,
                ReconnectPolicy = ReconnectPolicy
            });

        /// <summary>
        /// Gets the last scheduled reconnect attempt number.
        /// </summary>
        public int ReconnectAttempt => _reconnectController.Attempt;

        /// <summary>
        /// Gets the absolute scheduler time for the next reconnect attempt.
        /// </summary>
        public long? NextReconnectAttemptAtMs => _reconnectController.NextAttemptAtMs;

        /// <summary>
        /// Gets or sets whether raw payload bodies may be logged for diagnostics.
        /// </summary>
        public bool LogSensitivePayloads { get; set; }

        /// <summary>
        /// Gets or sets whether CultNet schema-v0 messages may be sent before user session verification.
        /// </summary>
        public bool AllowUnverifiedCultNetMessages { get; set; }

        /// <summary>
        /// Gets a value indicating whether the client currently has a verified session.
        /// </summary>
        public bool Verified => !string.IsNullOrWhiteSpace(_sessionToken);

        /// <summary>
        /// Gets a value indicating whether the network manager has been created.
        /// </summary>
        public bool Connected => _client is { IsRunning: true } && _peer != null;

        /// <summary>
        /// Initializes a new client instance.
        /// </summary>
        /// <param name="security">Validated client security configuration. This must be provided explicitly by the caller.</param>
        /// <param name="reconnectPolicy">Optional portable reconnect policy for the LiteNetLib client lane.</param>
        public Client(ClientSecurityOptions security, CultNetReconnectPolicy? reconnectPolicy = null)
        {
            _security = security ?? throw new ArgumentNullException(nameof(security));
            _reconnectController = new CultNetReconnectController(reconnectPolicy);
            OnError += s => Logger.LogError(s);
        }

        /// <summary>
        /// Sends a message when the client is connected and authorized to do so.
        /// </summary>
        /// <typeparam name="T">The message type.</typeparam>
        /// <param name="m">The message to send.</param>
        public void Send<T>(T m) where T : Message
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Client));
            }

            if (Connected && _peer != null)
            {
                if (Verified || m is LoginMessage || m is RegisterMessage || m is VerifyMessage)
                {
                    if (LogSensitivePayloads)
                    {
                        Logger.LogDebug($"Sending message {MessagePackSerializer.SerializeToJson(m)}");
                    }
                    else
                    {
                        Logger.LogDebug($"Sending message {typeof(T).Name}");
                    }
                    (_transportConnection ??= new LiteNetLibTransportConnection(_peer)).SendLegacy(m);
                }
                else Logger.LogError("Cannot send, client is not verified!");
            }
            else Logger.LogError("Cannot send, client is not connected!");
        }

        /// <summary>
        /// Sends a modern CultNet schema-v0 message when the client is connected and authorized.
        /// </summary>
        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Client));
            }

            if (message == null) throw new ArgumentNullException(nameof(message));

            if (Connected && _peer != null)
            {
                if (AllowUnverifiedCultNetMessages || Verified || message is CultNetLoginMessage or CultNetRegisterMessage or CultNetVerifyMessage)
                {
                    Logger.LogDebug($"Sending CultNet schema message {message.SchemaVersion}");
                    (_transportConnection ??= new LiteNetLibTransportConnection(_peer)).SendSchema(message);
                }
                else
                {
                    Logger.LogError("Cannot send CultNet schema message, client is not verified!");
                }
            }
            else
            {
                Logger.LogError("Cannot send CultNet schema message, client is not connected!");
            }
        }

        /// <summary>
        /// Removes all registered message listeners.
        /// </summary>
        public void ClearMessageListeners()
        {
            _messageDelegates.Clear();
            _cultNetMessageDelegates.Clear();
        }

        /// <summary>
        /// Adds a listener for a specific message type.
        /// </summary>
        /// <typeparam name="T">The message type to subscribe to.</typeparam>
        /// <param name="callback">The callback to invoke when the message is received.</param>
        public void AddMessageListener<T>(Action<T> callback) where T : Message
        {
            var type = typeof(T);
            // Atomically aggregate into multicast delegate (thread-safe via CAS)
            _messageDelegates.AddOrUpdate(type,
                _ => callback,  // First callback
                (t, current) =>
                {
                    // Combine with existing (supports multiple callbacks)
                    var combined = Delegate.Combine(current, callback) as Action<T>;
                    return combined ?? throw new InvalidOperationException($"Failed to combine delegates for {t.Name}");
                });
        }

        /// <summary>
        /// Adds a listener for a specific message type.
        /// </summary>
        public void On<T>(Action<T> callback) where T : Message
        {
            AddMessageListener(callback);
        }

        /// <summary>
        /// Adds a listener for a modern CultNet schema-v0 message type.
        /// </summary>
        public void AddCultNetMessageListener<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            var type = typeof(T);
            _cultNetMessageDelegates.AddOrUpdate(type,
                _ => callback,
                (t, current) =>
                {
                    var combined = Delegate.Combine(current, callback) as Action<T>;
                    return combined ?? throw new InvalidOperationException($"Failed to combine delegates for {t.Name}");
                });
        }

        /// <summary>
        /// Adds a listener for a modern CultNet schema-v0 message type.
        /// </summary>
        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            AddCultNetMessageListener(callback);
        }

        /// <summary>
        /// Removes a previously registered listener for a specific message type.
        /// </summary>
        /// <typeparam name="T">The message type to unsubscribe from.</typeparam>
        /// <param name="callback">The callback to remove.</param>
        public void RemoveMessageListener<T>(Action<T> callback) where T : Message
        {
            if (_messageDelegates.TryGetValue(typeof(T), out var currentDelegate))
            {
                var newDelegate = Delegate.Remove(currentDelegate, callback) as Action<T>;
                _messageDelegates[typeof(T)] = newDelegate ?? (_ => { });  // Null if empty
            }
        }

        /// <summary>
        /// Removes a previously registered listener for a specific message type.
        /// </summary>
        public void Off<T>(Action<T> callback) where T : Message
        {
            RemoveMessageListener(callback);
        }

        /// <summary>
        /// Removes a previously registered modern CultNet schema-v0 listener.
        /// </summary>
        public void RemoveCultNetMessageListener<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (_cultNetMessageDelegates.TryGetValue(typeof(T), out var currentDelegate))
            {
                var newDelegate = Delegate.Remove(currentDelegate, callback) as Action<T>;
                _cultNetMessageDelegates[typeof(T)] = newDelegate ?? (_ => { });
            }
        }

        /// <summary>
        /// Removes a previously registered modern CultNet schema-v0 listener.
        /// </summary>
        public void OffCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            RemoveCultNetMessageListener(callback);
        }

        /// <summary>
        /// Sends a login request.
        /// </summary>
        /// <param name="auth">The email address or username.</param>
        /// <param name="password">The plaintext password.</param>
        public void Login(string auth, string password)
        {
            if (auth.Length > 0)
            {
                if (password.Length is >= 8 and < 32)
                {
                    var nonce = Secret.NewNonce;
                    Send(new LoginMessage
                    {
                        Nonce = nonce,
                        Auth = Secret.EncryptString(auth, nonce, _security) ?? Array.Empty<byte>(),
                        Password = Secret.EncryptString(password, nonce, _security) ?? Array.Empty<byte>()
                    });
                }
                else OnError?.Invoke("Password Invalid: needs to be at least 8 characters");
            }
            else OnError?.Invoke("Email/Username Empty");
        }

        /// <summary>
        /// Sends a registration request.
        /// </summary>
        /// <param name="email">The email address for the new account.</param>
        /// <param name="username">The requested username.</param>
        /// <param name="password">The plaintext password.</param>
        public void Register(string email, string username, string password)
        {
            if (email.Length > 0)
            {
                if (username.Length > 0)
                {
                    if (password.Length is >= 8 and < 32)
                    {
                        var nonce = Secret.NewNonce;
                        Send(new RegisterMessage
                        {
                            Nonce = nonce,
                            Email = Secret.EncryptString(email, nonce, _security) ?? Array.Empty<byte>(),
                            Name = Secret.EncryptString(username, nonce, _security) ?? Array.Empty<byte>(),
                            Password = Secret.EncryptString(password, nonce, _security) ?? Array.Empty<byte>()
                        });
                    }
                    else OnError?.Invoke("Password Invalid: needs to be at least 8 characters");
                }
                else OnError?.Invoke("Username Empty");
            }
            else OnError?.Invoke("Email Empty");
        }

        /// <summary>
        /// Connects the client to a server and starts polling LiteNetLib events.
        /// </summary>
        /// <param name="host">The remote host name or IP address.</param>
        /// <param name="port">The remote server port.</param>
        public void Connect(string host = "localhost", int port = 3075)
        {
            ConnectCore(host, port, resetReconnectBackoff: true);
        }

        private void ConnectCore(string host, int port, bool resetReconnectBackoff)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Client));
            }

            _manualDisconnect = false;
            _isReconnecting = false;
            if (resetReconnectBackoff)
            {
                _reconnectController.Reset();
            }
            SetReconnectState(ClientReconnectState.Idle);
            _reconnectSubscription?.Dispose();
            _reconnectSubscription = null;
            _lastHost = host;
            _lastPort = port;

            DisposeTransport();

            var listener = new EventBasedNetListener();
            _client = new NetManager(listener)
            {
                NatPunchEnabled = true
            };
            _client.Start(3074);
            _peer = _client.Connect(host, port, _security.ConnectionKey);
            _pollSubscription = Observable.EveryUpdate().Subscribe(_ => _client?.PollEvents());
            listener.NetworkErrorEvent += (point, code) => Logger.LogInfo($"{point.Address}: Error {code}");
            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try
                {
                    var bytes = reader.GetRemainingBytes();
                    var transport = _transportConnection ??= new LiteNetLibTransportConnection(peer);
                    var frame = transport.Receive(bytes);
                    if (string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal))
                    {
                        var cultNetMessage = LiteNetLibTransportConnection.DecodeSchema(frame);
                        Logger.LogDebug($"Received CultNet schema message {cultNetMessage.SchemaVersion}");
                        var cultNetType = cultNetMessage.GetType();
                        if (_cultNetMessageDelegates.TryGetValue(cultNetType, out var cultNetDelegate) &&
                            cultNetDelegate != null)
                        {
                            cultNetDelegate.DynamicInvoke(cultNetMessage);
                        }
                        else
                        {
                            Logger.LogWarning($"No listener for CultNet schema message {cultNetMessage.SchemaVersion}");
                        }

                        return;
                    }

                    var message = LiteNetLibTransportConnection.DecodeLegacy(frame);
                    if (LogSensitivePayloads)
                    {
                        Logger.LogDebug($"Received message: {MessagePackSerializer.ConvertToJson(new ReadOnlyMemory<byte>(frame.Payload))}");
                    }
                    else
                    {
                        Logger.LogDebug($"Received message {message.GetType().Name}");
                    }
                    var type = message.GetType();

                    switch (message)
                    {
                        case LoginSuccessMessage loginSuccess:
                            _sessionToken = Secret.DecryptString(loginSuccess.Session, loginSuccess.Nonce, _security);
                            break;
                        case ErrorMessage error:
                            OnError?.Invoke(error.Error);
                            break;
                    }


                    if (_messageDelegates.TryGetValue(type, out var del) && del != null)
                        del.DynamicInvoke(message);
                    else
                        Logger.LogWarning($"No listener for {type.Name}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Message processing failed: {ex.Message}");
                }

            };

            listener.PeerConnectedEvent += peer =>
            {
                Logger.LogInfo($"Peer {peer.Address}:{peer.Port} connected.");
                _peer = peer;
                _transportConnection = new LiteNetLibTransportConnection(peer);
                _reconnectController.Reset();
                SetReconnectState(ClientReconnectState.Idle);
                if (Verified)
                {
                    var nonce = Secret.NewNonce;
                    _transportConnection.SendLegacy(new VerifyMessage
                    {
                        Nonce = nonce,
                        Session = Secret.EncryptString(_sessionToken, nonce, _security) ?? Array.Empty<byte>()
                    });
                }
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                Logger.LogInfo($"Peer {peer.Address}:{peer.Port} disconnected: {info.Reason}.");
                DisposeTransport();
                ScheduleReconnect();
            };

            listener.NetworkLatencyUpdateEvent +=
                (peer, latency) => Ping = latency; //Logger($"Ping received: {latency} ms");
        }

        /// <summary>
        /// Disconnects the client and suppresses automatic reconnect.
        /// </summary>
        public void Disconnect()
        {
            _manualDisconnect = true;
            _reconnectSubscription?.Dispose();
            _reconnectSubscription = null;
            _reconnectController.Reset();
            SetReconnectState(ClientReconnectState.Idle);
            DisposeTransport();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _manualDisconnect = true;
            _reconnectSubscription?.Dispose();
            _reconnectSubscription = null;
            _reconnectController.Reset();
            SetReconnectState(ClientReconnectState.Idle);
            DisposeTransport();
        }

        private void ScheduleReconnect()
        {
            if (_disposed || _manualDisconnect || string.IsNullOrWhiteSpace(_lastHost) || _isReconnecting)
            {
                return;
            }

            _isReconnecting = true;
            var decision = _reconnectController.RecordFailure(
                CurrentSchedulerTimeMs(),
                RandomNumberGenerator.GetInt32(ReconnectPolicy.MaxJitterMs + 1));
            if (!decision.ShouldRetry)
            {
                _isReconnecting = false;
                Logger.LogWarning("Reconnect attempts exhausted.");
                SetReconnectState(ClientReconnectState.Idle);
                return;
            }

            var delay = TimeSpan.FromMilliseconds(decision.DelayMs);
            SetReconnectState(ClientReconnectState.WaitingToReconnect);
            _reconnectSubscription = Observable.Timer(delay).Subscribe(_ =>
            {
                _isReconnecting = false;
                _reconnectSubscription?.Dispose();
                _reconnectSubscription = null;
                SetReconnectState(ClientReconnectState.Reconnecting);

                try
                {
                    ConnectCore(_lastHost, _lastPort, resetReconnectBackoff: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Reconnect failed: {ex.Message}");
                    ScheduleReconnect();
                }
            });
        }

        internal static TimeSpan GetReconnectDelayForAttempt(int attempt)
        {
            var jitterMilliseconds = RandomNumberGenerator.GetInt32(CultNetReconnectPolicies.CreateDefault().MaxJitterMs + 1);
            return GetReconnectDelayForAttempt(attempt, jitterMilliseconds, CultNetReconnectPolicies.CreateDefault());
        }

        internal static TimeSpan GetReconnectDelayForAttempt(
            int attempt,
            int jitterMs,
            CultNetReconnectPolicy? policy = null)
        {
            var reconnectPolicy = policy ?? CultNetReconnectPolicies.CreateDefault();
            return TimeSpan.FromMilliseconds(
                CultNetReconnectPolicies.ComputeDelayMs(reconnectPolicy, attempt, jitterMs));
        }

        private static long CurrentSchedulerTimeMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private void DisposeTransport()
        {
            _pollSubscription?.Dispose();
            _pollSubscription = null;
            _transportConnection = null;

            if (_peer != null)
            {
                try
                {
                    _peer.Disconnect();
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }

            if (_client != null)
            {
                try
                {
                    _client.Stop();
                }
                finally
                {
                    _client = null;
                    _peer = null;
                }
            }
        }

        private void SetReconnectState(ClientReconnectState state)
        {
            if (ReconnectState == state)
            {
                return;
            }

            ReconnectState = state;
            OnReconnectStateChanged?.Invoke(state);
        }
    }
}
