using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    public class Server
    {
        private const string EmailPattern =
            @"^([0-9a-zA-Z]([\+\-_\.][0-9a-zA-Z]+)*)+@(([0-9a-zA-Z][-\w]*[0-9a-zA-Z]*\.)+[a-zA-Z0-9]{2,17})$";
        private const string UsernamePattern = @"^[A-Za-z0-9]+(?:[ _-][A-Za-z0-9]+)*$";
        private const int ServerPort = 3075;
        private const float SessionTimeoutSeconds = 1800; // 30 minutes
        private const int MaxLoginAttemptsPerMinute = 5;

        private readonly ConcurrentDictionary<Type, Delegate> _messageDelegates = new();  // Aggregated multicast Action<T> per type
        private readonly ConcurrentDictionary<long, User> _users = new();
        private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
        private readonly ConcurrentDictionary<Guid, long> _sessionToUserId = new();
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _loginAttempts = new(); // IP to attempt timestamps
        private NetManager _netManager;
        private Stopwatch _timer;
        private readonly CultCache _database;
        private ILogger _logger = new NullLogger();
        
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        private float Time => (float)_timer.Elapsed.TotalSeconds;

        public Server(CultCache cache)
        {
            _database = cache;
            _database.RegisterIndex<PlayerData>("Email");
            Observable.Timer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
                .Subscribe(_ => CleanupExpiredSessions());
        }

        public bool IsValidEmail(string email) => Regex.IsMatch(email, EmailPattern);
        public bool IsValidUsername(string name) => Regex.IsMatch(name, UsernamePattern);

        public void ClearMessageListeners()
        {
            _messageDelegates.Clear();
        }

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

        public void RemoveMessageListener<T>(Action<T> callback) where T : Message
        {
            if (_messageDelegates.TryGetValue(typeof(T), out var currentDelegate))
            {
                var newDelegate = Delegate.Remove(currentDelegate, callback) as Action<T>;
                _messageDelegates[typeof(T)] = newDelegate;  // Null if empty
            }
        }

        public void Stop()
        {
            _netManager.Stop();
        }

        public void Start()
        {
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
                if (CheckRateLimit(request.RemoteEndPoint.Address.ToString()))
                    request.AcceptIfKey(Secret.ConnectionKey);
                else
                    request.Reject();
            };

            listener.PeerConnectedEvent += peer =>
            {
                Logger.LogInfo($"User Connected: {peer.Address}");
                _users.TryAdd(peer.Id, new User { Peer = peer });
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                Logger.LogInfo($"User Disconnected: {peer.Address}");
                if (_users.TryRemove(peer.Id, out var user) && user.SessionGuid != Guid.Empty)
                {
                    _sessions.TryRemove(user.SessionGuid, out _);
                    _sessionToUserId.TryRemove(user.SessionGuid, out _);
                }
            };

            listener.NetworkLatencyUpdateEvent += (peer, latency) =>
            {
                if (_users.TryGetValue(peer.Id, out var user))
                    user.Latency = latency;
            };

            listener.NetworkReceiveEvent += async (peer, reader, channel, method) =>
            {
                try
                {
                    var bytes = reader.GetRemainingBytes();
                    var message = MessagePackSerializer.Deserialize<Message>(bytes);
                    Logger.LogDebug($"Received message: {MessagePackSerializer.ConvertToJson(new ReadOnlyMemory<byte>(bytes))}");
                    if (message == null)
                        return;
                    message.Peer = peer;
                    var user = _users.GetOrAdd(peer.Id, _ => new User { Peer = peer });

                    if (message is LoginMessage or RegisterMessage or VerifyMessage)
                    {
                        if (IsVerified(user))
                        {
                            var nonce = Secret.NewNonce;
                            peer.Send(new LoginSuccessMessage { Nonce = nonce, Session = Secret.EncryptBytes(user.SessionGuid.ToByteArray(), nonce) });
                            return;
                        }

                        Guid sessionGuid;
                        if (message is LoginMessage or RegisterMessage && !CheckRateLimit(peer.Address.ToString()))
                        {
                            peer.Send(new ErrorMessage { Error = "Too Many Attempts" });
                            return;
                        }
                        switch (message)
                        {
                            case RegisterMessage register:
                            {
                                var name = Secret.DecryptString(register.Name, register.Nonce);
                                var email = Secret.DecryptString(register.Email, register.Nonce);
                                if (!IsValidUsername(name))
                                {
                                    peer.Send(new ErrorMessage { Error = "Username Invalid" });
                                    return;
                                }

                                if (!IsValidEmail(email))
                                {
                                    peer.Send(new ErrorMessage { Error = "Email Invalid" });
                                    return;
                                }

                                if (_database.GetIdByIndex<PlayerData>("Email", email) != null)
                                {
                                    peer.Send(new ErrorMessage { Error = "Email Taken" });
                                    return;
                                }
                                
                                if (_database.GetIdByName<PlayerData>(name) != null)
                                {
                                    peer.Send(new ErrorMessage { Error = "Username Taken" });
                                    return;
                                }
                                sessionGuid = GenerateSessionGuid();
                                var password = Secret.DecryptString(register.Password, register.Nonce);
                                var newUserData = new PlayerData
                                {
                                    ID = Guid.NewGuid(),
                                    Email = email,
                                    PasswordHash = Argon2.Hash(password, memoryCost: 16384),
                                    Username = name
                                };
                                await _database.AddAsync(newUserData); // Immediate cache + async file write
                                _sessions.TryAdd(sessionGuid, new Session { Data = newUserData, LastUpdate = DateTime.Now });
                                _sessionToUserId[sessionGuid] = peer.Id;
                                var nonce = Secret.NewNonce;
                                peer.Send(new LoginSuccessMessage { Nonce = nonce, Session = Secret.EncryptBytes(sessionGuid.ToByteArray(), nonce) });
                                if (_users.TryGetValue(peer.Id, out var u1))
                                    u1.SessionGuid = sessionGuid;
                                break;
                            }
                            case VerifyMessage verify:
                                var session = new Guid(Secret.DecryptBytes(verify.Session, verify.Nonce));
                                if (!_sessions.ContainsKey(session))
                                {
                                    peer.Send(new ErrorMessage { Error = "Session Not Found" });
                                    return;
                                }
                                if (_users.TryGetValue(peer.Id, out var u2))
                                {
                                    u2.SessionGuid = session;
                                    var nonce = Secret.NewNonce;
                                    peer.Send(new LoginSuccessMessage { Nonce = nonce, Session = Secret.EncryptBytes(session.ToByteArray(), nonce) });
                                }
                                break;
                            case LoginMessage login:
                            {
                                var auth = Secret.DecryptString(login.Auth,  login.Nonce);
                                var isEmail = IsValidEmail(auth);
                                var userData = isEmail ? 
                                    _database.GetByIndex<PlayerData>("Email", auth) : 
                                    _database.GetByName<PlayerData>(auth);

                                if (userData == null)
                                {
                                    peer.Send(new ErrorMessage { Error = isEmail ? "Email Not Found" : "Username Not Found" });
                                    return;
                                }

                                var password = Secret.DecryptString(login.Password, login.Nonce);
                                if (!Argon2.Verify(userData.PasswordHash, password))
                                {
                                    peer.Send(new ErrorMessage { Error = "Password Incorrect" });
                                    return;
                                }

                                sessionGuid = GenerateSessionGuid();
                                _sessions.TryAdd(sessionGuid, new Session { Data = userData, LastUpdate = DateTime.Now });
                                _sessionToUserId[sessionGuid] = peer.Id;
                                var nonce = Secret.NewNonce;
                                peer.Send(new LoginSuccessMessage { Nonce = nonce, Session = Secret.EncryptBytes(sessionGuid.ToByteArray(), nonce) });
                                if (_users.TryGetValue(peer.Id, out var u3))
                                    u3.SessionGuid = sessionGuid;
                                break;
                            }
                        }
                    }
                    else if (IsVerified(user))
                    {
                        // Zero-reflection dispatch via cached multicast delegate
                        if (_messageDelegates.TryGetValue(message.GetType(), out var del) && del != null)
                            del.DynamicInvoke(message);
                        else
                            Logger.LogWarning($"No listener for {message.GetType().Name}");
                        
                        if (_sessions.TryGetValue(user.SessionGuid, out var session))
                            session.LastUpdate = DateTime.Now;
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

            AddMessageListener<ChangeNameMessage>(async (message) =>
            {
                if (!IsValidUsername(message.Name))
                {
                    message.Peer.Send(new ErrorMessage { Error = "Username Invalid" });
                    return;
                }
                if (_database.GetIdByName<PlayerData>(message.Name) != null)
                {
                    message.Peer.Send(new ErrorMessage { Error = "Username Taken" });
                    return;
                }
                if (_users.TryGetValue(message.Peer.Id, out var user))
                {
                    var data = SessionData(user);
                    data.Username = message.Name;
                    await _database.AddAsync(data);
                }
            });

            Logger.LogInfo($"Server started on port {ServerPort}.");
        }

        private bool IsVerified(User u) => u != null && _sessions.ContainsKey(u.SessionGuid);

        private PlayerData SessionData(User user) =>
            IsVerified(user) ? _sessions[user.SessionGuid].Data : null;

        private bool CheckRateLimit(string ip)
        {
            var now = DateTime.Now;
            var windowStart = now.AddMinutes(-1);
            _loginAttempts.AddOrUpdate(
                ip,
                _ => new Queue<DateTime>(new[] { now }),
                (_, queue) =>
                {
                    // Remove attempts older than 1 minute
                    while (queue.Count > 0 && queue.Peek() < windowStart)
                        queue.Dequeue();
                    queue.Enqueue(now);
                    return queue;
                });

            return _loginAttempts[ip].Count <= MaxLoginAttemptsPerMinute;
        }

        private Guid GenerateSessionGuid()
        {
            var guid = Guid.NewGuid();
            // TODO: Implement HMAC signing for session validation
            return guid;
        }

        private void CleanupExpiredSessions()
        {
            foreach (var session in _sessions.ToArray())
            {
                if ((DateTime.Now - session.Value.LastUpdate).TotalSeconds > SessionTimeoutSeconds)
                {
                    _sessions.TryRemove(session.Key, out _);
                    if (_sessionToUserId.TryRemove(session.Key, out var userId) && _users.TryGetValue(userId, out var user))
                    {
                        user.Peer.Disconnect();
                        _users.TryRemove(userId, out _);
                    }
                }
            }
        }
    }

    public class Session
    {
        public DateTime LastUpdate;
        public PlayerData Data;
    }

    public class User
    {
        public NetPeer Peer;
        public int Latency;
        public Guid SessionGuid;
    }
}