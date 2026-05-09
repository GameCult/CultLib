using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Logging;
using Isopoh.Cryptography.Argon2;
using LiteNetLib;
using MessagePack;
using R3;

namespace GameCult.Networking
{
    /// <summary>
    /// Hosts the server-side authentication, session, and message dispatch pipeline.
    /// </summary>
    public class Server : IDisposable
    {
        private const string EmailPattern =
            @"^([0-9a-zA-Z]([\+\-_\.][0-9a-zA-Z]+)*)+@(([0-9a-zA-Z][-\w]*[0-9a-zA-Z]*\.)+[a-zA-Z0-9]{2,17})$";
        private const string UsernamePattern = @"^[A-Za-z0-9]+(?:[ _-][A-Za-z0-9]+)*$";
        private const int ServerPort = 3075;
        private const float SessionTimeoutSeconds = 1800; // 30 minutes
        private const float SessionRefreshThresholdSeconds = 300; // 5 minutes
        private const int MaxConnectionAttemptsPerMinute = 30;
        private const int MaxLoginAttemptsPerMinute = 5;

        private readonly ConcurrentDictionary<Type, Delegate> _messageDelegates = new();
        private readonly ConcurrentDictionary<long, User> _users = new();
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _connectionAttempts = new();
        private readonly ConcurrentDictionary<string, object> _connectionAttemptLocks = new();
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _authAttempts = new();
        private readonly ConcurrentDictionary<string, object> _authAttemptLocks = new();
        private readonly IDisposable _cleanupSubscription;
        private readonly CultCache _database;
        private readonly ServerSecurityOptions _security;
        private NetManager? _netManager;
        private Stopwatch? _timer;
        private ILogger _logger = new NullLogger();
        private bool _disposed;

        /// <summary>
        /// Gets or sets the logger used by the server.
        /// </summary>
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        /// <summary>
        /// Gets or sets whether raw payload bodies may be logged for diagnostics.
        /// </summary>
        public bool LogSensitivePayloads { get; set; }

        private float Time => (float)(_timer?.Elapsed.TotalSeconds ?? 0d);

        /// <summary>
        /// Initializes a new server instance over the supplied cache.
        /// </summary>
        /// <param name="cache">The backing cache used for player persistence.</param>
        /// <param name="security">Optional validated server security options. When omitted, strict environment-based configuration is used.</param>
        public Server(CultCache cache, ServerSecurityOptions? security = null)
        {
            _database = cache;
            _security = security ?? ServerSecurityOptions.FromEnvironment();
            LogSensitivePayloads = _security.IsDevelopment;
            _cleanupSubscription = Observable.Timer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
                .Subscribe(_ => CleanupExpiredSessions());
        }

        /// <summary>
        /// Validates an email address against the server's accepted pattern.
        /// </summary>
        /// <param name="email">The email address to validate.</param>
        /// <returns><c>true</c> when the address matches the expected format.</returns>
        public bool IsValidEmail(string email) => Regex.IsMatch(email, EmailPattern);

        /// <summary>
        /// Validates a username against the server's accepted pattern.
        /// </summary>
        /// <param name="name">The username to validate.</param>
        /// <returns><c>true</c> when the username matches the expected format.</returns>
        public bool IsValidUsername(string name) => Regex.IsMatch(name, UsernamePattern);

        /// <summary>
        /// Removes all registered message listeners.
        /// </summary>
        public void ClearMessageListeners()
        {
            _messageDelegates.Clear();
        }

        /// <summary>
        /// Adds a listener for a specific authenticated message type.
        /// </summary>
        /// <typeparam name="T">The message type to subscribe to.</typeparam>
        /// <param name="callback">The callback to invoke when the message is received.</param>
        public void AddMessageListener<T>(Action<T> callback) where T : Message
        {
            var type = typeof(T);
            _messageDelegates.AddOrUpdate(type,
                _ => callback,
                (t, current) =>
                {
                    var combined = Delegate.Combine(current, callback) as Action<T>;
                    return combined ?? throw new InvalidOperationException($"Failed to combine delegates for {t.Name}");
                });
        }

        /// <summary>
        /// Adds a listener for a specific authenticated message type.
        /// </summary>
        public void On<T>(Action<T> callback) where T : Message
        {
            AddMessageListener(callback);
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
                _messageDelegates[typeof(T)] = newDelegate!;
            }
        }

        /// <summary>
        /// Removes a previously registered listener for a specific authenticated message type.
        /// </summary>
        public void Off<T>(Action<T> callback) where T : Message
        {
            RemoveMessageListener(callback);
        }

        /// <summary>
        /// Stops the underlying LiteNetLib server.
        /// </summary>
        public void Stop()
        {
            _netManager?.Stop();
            _netManager = null;
        }

        /// <summary>
        /// Starts listening for connections and configures message handlers.
        /// </summary>
        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Server));
            }

            Stop();
            _timer = Stopwatch.StartNew();

            var listener = new EventBasedNetListener();
            _netManager = new NetManager(listener)
            {
                UnsyncedEvents = true,
                NatPunchEnabled = true
            };
            _netManager.Start(ServerPort);

            listener.NetworkErrorEvent += (point, code) => Logger.LogInfo($"{point.Address}: Error {code}");

            listener.ConnectionRequestEvent += request =>
            {
                if (CheckConnectionRateLimit(request.RemoteEndPoint.Address.ToString()))
                {
                    request.AcceptIfKey(_security.ConnectionKey);
                }
                else
                {
                    request.Reject();
                }
            };

            listener.PeerConnectedEvent += peer =>
            {
                Logger.LogInfo($"User Connected: {peer.Address}");
                _users.TryAdd(peer.Id, new User { Peer = peer });
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                Logger.LogInfo($"User Disconnected: {peer.Address}");
                _users.TryRemove(peer.Id, out _);
            };

            listener.NetworkLatencyUpdateEvent += (peer, latency) =>
            {
                if (_users.TryGetValue(peer.Id, out var user))
                {
                    user.Latency = latency;
                }
            };

            listener.NetworkReceiveEvent += async (peer, reader, channel, method) =>
            {
                try
                {
                    var bytes = reader.GetRemainingBytes();
                    var message = MessageSerialization.Deserialize<Message>(bytes);
                    if (LogSensitivePayloads)
                    {
                        Logger.LogDebug($"Received message: {MessagePackSerializer.ConvertToJson(new ReadOnlyMemory<byte>(bytes))}");
                    }
                    else
                    {
                        Logger.LogDebug($"Received message {message?.GetType().Name ?? "unknown"}");
                    }
                    if (message == null)
                    {
                        return;
                    }

                    message.Peer = peer;
                    var user = _users.GetOrAdd(peer.Id, _ => new User { Peer = peer });

                    if (message is LoginMessage or RegisterMessage or VerifyMessage)
                    {
                        if (IsVerified(user))
                        {
                            SendSessionToken(peer, user.PlayerId);
                            return;
                        }

                        if (message is LoginMessage or RegisterMessage && !CheckAuthRateLimit(peer.Address.ToString()))
                        {
                            peer.Send(new ErrorMessage { Error = "Too Many Attempts" });
                            return;
                        }

                        switch (message)
                        {
                            case RegisterMessage register:
                                await HandleRegisterAsync(peer, user, register);
                                break;
                            case VerifyMessage verify:
                                HandleVerify(peer, user, verify);
                                break;
                            case LoginMessage login:
                                HandleLogin(peer, user, login);
                                break;
                        }
                    }
                    else if (IsVerified(user))
                    {
                        if (_messageDelegates.TryGetValue(message.GetType(), out var del) && del != null)
                        {
                            del.DynamicInvoke(message);
                        }
                        else
                        {
                            Logger.LogWarning($"No listener for {message.GetType().Name}");
                        }

                        user.SessionExpiresAt = DateTimeOffset.UtcNow.AddSeconds(SessionTimeoutSeconds);
                        RefreshSessionIfNeeded(peer, user);
                    }
                    else
                    {
                        peer.Send(new ErrorMessage { Error = "User Not Verified" });
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Message processing failed: {ex.Message}");
                }
            };

            AddMessageListener<ChangeNameMessage>(async message =>
            {
                if (!IsValidUsername(message.Name))
                {
                    message.Peer?.Send(new ErrorMessage { Error = "Username Invalid" });
                    return;
                }

                if (_database.GetByName<PlayerData>(message.Name) != null)
                {
                    message.Peer?.Send(new ErrorMessage { Error = "Username Taken" });
                    return;
                }

                if (message.Peer != null && _users.TryGetValue(message.Peer.Id, out var user))
                {
                    var data = SessionData(user);
                    if (data == null)
                    {
                        message.Peer.Send(new ErrorMessage { Error = "User Not Verified" });
                        return;
                    }

                    data.Username = message.Name;
                    await _database.AddAsync(data);
                }
            });

            Logger.LogInfo($"Server started on port {ServerPort}.");
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cleanupSubscription.Dispose();
            Stop();
        }

        private async Task HandleRegisterAsync(NetPeer peer, User user, RegisterMessage register)
        {
            var name = Secret.DecryptString(register.Name, register.Nonce, _security);
            var email = Secret.DecryptString(register.Email, register.Nonce, _security);
            var password = Secret.DecryptString(register.Password, register.Nonce, _security);

            if (string.IsNullOrWhiteSpace(name) || !IsValidUsername(name))
            {
                peer.Send(new ErrorMessage { Error = "Username Invalid" });
                return;
            }

            if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
            {
                peer.Send(new ErrorMessage { Error = "Email Invalid" });
                return;
            }

            if (_database.GetByIndex<PlayerData>("Email", email) != null)
            {
                peer.Send(new ErrorMessage { Error = "Email Taken" });
                return;
            }

            if (_database.GetByName<PlayerData>(name) != null)
            {
                peer.Send(new ErrorMessage { Error = "Username Taken" });
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                peer.Send(new ErrorMessage { Error = "Password Invalid" });
                return;
            }

            var newUserData = new PlayerData
            {
                PlayerId = Guid.NewGuid(),
                Email = email,
                PasswordHash = Argon2.Hash(password, memoryCost: 16384),
                Username = name
            };

            await _database.AddAsync(newUserData);
            AttachUser(user, newUserData.PlayerId);
            SendSessionToken(peer, newUserData.PlayerId);
        }

        private void HandleVerify(NetPeer peer, User user, VerifyMessage verify)
        {
            var token = Secret.DecryptString(verify.Session, verify.Nonce, _security);
            if (!Secret.TryValidateSessionToken(token, _security, out var playerId, out _, out var sessionVersion))
            {
                peer.Send(new ErrorMessage { Error = "Session Invalid" });
                return;
            }

            var player = _database.GetByIndex<PlayerData>("PlayerId", playerId.ToString("D"));
            if (player == null)
            {
                peer.Send(new ErrorMessage { Error = "Session Not Found" });
                return;
            }

            if (player.SessionVersion != sessionVersion)
            {
                peer.Send(new ErrorMessage { Error = "Session Superseded" });
                return;
            }

            AttachUser(user, playerId);
            SendSessionToken(peer, player);
        }

        private void HandleLogin(NetPeer peer, User user, LoginMessage login)
        {
            var auth = Secret.DecryptString(login.Auth, login.Nonce, _security);
            var password = Secret.DecryptString(login.Password, login.Nonce, _security);
            if (string.IsNullOrWhiteSpace(auth) || string.IsNullOrWhiteSpace(password))
            {
                peer.Send(new ErrorMessage { Error = "Credentials Invalid" });
                return;
            }

            var isEmail = IsValidEmail(auth);
            var userData = isEmail
                ? _database.GetByIndex<PlayerData>("Email", auth)
                : _database.GetByName<PlayerData>(auth);

            if (userData == null)
            {
                peer.Send(new ErrorMessage { Error = isEmail ? "Email Not Found" : "Username Not Found" });
                return;
            }

            if (!Argon2.Verify(userData.PasswordHash, password))
            {
                peer.Send(new ErrorMessage { Error = "Password Incorrect" });
                return;
            }

            AttachUser(user, userData.PlayerId);
            SendSessionToken(peer, userData);
        }

        private bool IsVerified(User? user) =>
            user != null &&
            user.PlayerId != Guid.Empty &&
            user.SessionExpiresAt > DateTimeOffset.UtcNow &&
            _database.GetByIndex<PlayerData>("PlayerId", user.PlayerId.ToString("D")) != null;

        private PlayerData? SessionData(User user) =>
            IsVerified(user) ? _database.GetByIndex<PlayerData>("PlayerId", user.PlayerId.ToString("D")) : null;

        internal bool CheckConnectionRateLimit(string ip)
        {
            return CheckRateLimit(ip, MaxConnectionAttemptsPerMinute, _connectionAttempts, _connectionAttemptLocks);
        }

        internal bool CheckAuthRateLimit(string ip)
        {
            return CheckRateLimit(ip, MaxLoginAttemptsPerMinute, _authAttempts, _authAttemptLocks);
        }

        private void CleanupExpiredSessions()
        {
            foreach (var entry in _users.ToArray())
            {
                if (entry.Value.SessionExpiresAt != default && entry.Value.SessionExpiresAt <= DateTimeOffset.UtcNow)
                {
                    if (_users.TryRemove(entry.Key, out var user))
                    {
                        user.Peer.Disconnect();
                    }
                }
            }
        }

        private void AttachUser(User user, Guid playerId)
        {
            user.PlayerId = playerId;
            user.SessionExpiresAt = DateTimeOffset.UtcNow.AddSeconds(SessionTimeoutSeconds);
        }

        private void SendSessionToken(NetPeer peer, Guid playerId)
        {
            var player = _database.GetByIndex<PlayerData>("PlayerId", playerId.ToString("D"));
            if (player == null)
            {
                peer.Send(new ErrorMessage { Error = "Session Not Found" });
                return;
            }

            SendSessionToken(peer, player);
        }

        private void SendSessionToken(NetPeer peer, PlayerData player)
        {
            var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(SessionTimeoutSeconds);
            player.SessionVersion++;
            var token = Secret.CreateSessionToken(player.PlayerId, expiresAtUtc, player.SessionVersion, _security);
            _database.AddAsync(player).GetAwaiter().GetResult();

            if (_users.TryGetValue(peer.Id, out var user))
            {
                user.PlayerId = player.PlayerId;
                user.SessionExpiresAt = expiresAtUtc;
                user.SessionVersion = player.SessionVersion;
                user.SessionToken = token;
            }

            var nonce = Secret.NewNonce;
            peer.Send(new LoginSuccessMessage
            {
                Nonce = nonce,
                Session = Secret.EncryptString(token, nonce, _security) ?? Array.Empty<byte>()
            });
        }

        private void RefreshSessionIfNeeded(NetPeer peer, User user)
        {
            if ((user.SessionExpiresAt - DateTimeOffset.UtcNow).TotalSeconds > SessionRefreshThresholdSeconds)
            {
                return;
            }

            SendSessionToken(peer, user.PlayerId);
        }

        private static bool CheckRateLimit(
            string ip,
            int maxAttemptsPerMinute,
            ConcurrentDictionary<string, Queue<DateTimeOffset>> attemptBuckets,
            ConcurrentDictionary<string, object> attemptLocks)
        {
            var now = DateTimeOffset.UtcNow;
            var windowStart = now.AddMinutes(-1);
            var queue = attemptBuckets.GetOrAdd(ip, _ => new Queue<DateTimeOffset>());
            var gate = attemptLocks.GetOrAdd(ip, _ => new object());

            lock (gate)
            {
                while (queue.Count > 0 && queue.Peek() < windowStart)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(now);
                return queue.Count <= maxAttemptsPerMinute;
            }
        }
    }

    /// <summary>
    /// Represents an active authenticated session.
    /// </summary>
    public class Session
    {
        /// <summary>
        /// The last time the session was observed as active.
        /// </summary>
        public DateTime LastUpdate;

        /// <summary>
        /// Player data associated with the session.
        /// </summary>
        public PlayerData Data = null!;
    }

    /// <summary>
    /// Tracks connection state for a connected peer.
    /// </summary>
    public class User
    {
        /// <summary>
        /// The connected network peer.
        /// </summary>
        public NetPeer Peer = null!;

        /// <summary>
        /// The last reported latency for the peer.
        /// </summary>
        public int Latency;

        /// <summary>
        /// The authenticated player identifier.
        /// </summary>
        public Guid PlayerId;

        /// <summary>
        /// The current session expiration timestamp in UTC.
        /// </summary>
        public DateTimeOffset SessionExpiresAt;

        /// <summary>
        /// The latest signed session token issued to the peer.
        /// </summary>
        public string SessionToken = string.Empty;

        /// <summary>
        /// The session version associated with the latest issued token.
        /// </summary>
        public long SessionVersion;
    }
}
