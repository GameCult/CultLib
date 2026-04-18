using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using GameCult.Logging;
using LiteNetLib;
using MessagePack;
using MessagePack.Resolvers;
using R3;

namespace GameCult.Networking
{
    public class Client
    {
        public event Action<string> OnError; // Hook for important user-facing feedback (e.g. via modal UI element)
        private NetManager _client;
        private NetPeer _peer;

        private readonly ConcurrentDictionary<Type, Delegate> _messageDelegates = new();
        

        private Guid _sessionToken;
        private string _lastHost;
        private int _lastPort;
        private ILogger _logger = new NullLogger();
        
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        public int Ping { get; private set; }
        public bool Verified => _sessionToken != Guid.Empty;
        public bool Connected => _client != null;

        public Client()
        {
            OnError += s => Logger.LogError(s);
        }

        public void Send<T>(T m) where T : Message
        {
            if (Connected)
            {
                if (Verified || m is LoginMessage || m is RegisterMessage || m is VerifyMessage)
                {
                    Logger.LogDebug($"Sending message {MessagePackSerializer.SerializeToJson(m as Message)}");
                    _peer.Send(m);
                }
                else Logger.LogError("Cannot send, client is not verified!");
            }
            else Logger.LogError("Cannot send, client is not connected!");
        }

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
                        Auth = Secret.EncryptString(auth, nonce),
                        Password = Secret.EncryptString(password, nonce)
                    });
                }
                else OnError?.Invoke("Password Invalid: needs to be at least 8 characters");
            }
            else OnError?.Invoke("Email/Username Empty");
        }

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
                            Email = Secret.EncryptString(email, nonce),
                            Name = Secret.EncryptString(username, nonce),
                            Password = Secret.EncryptString(password, nonce)
                        });
                    }
                    else OnError?.Invoke("Password Invalid: needs to be at least 8 characters");
                }
                else OnError?.Invoke("Username Empty");
            }
            else OnError?.Invoke("Email Empty");
        }

        public void Connect(string host = "localhost", int port = 3075)
        {
            _lastHost = host;
            _lastPort = port;

            EventBasedNetListener listener = new EventBasedNetListener();
            _client = new NetManager(listener)
            {
//            UnsyncedEvents = true,
                NatPunchEnabled = true
            };
            _client.Start(3074);
            _peer = _client.Connect(host, port, Secret.ConnectionKey);
            Observable.EveryUpdate().Subscribe(_ => _client.PollEvents());
            listener.NetworkErrorEvent += (point, code) => Logger.LogInfo($"{point.Address}: Error {code}");
            listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                try
                {
                    var bytes = reader.GetRemainingBytes();
                    Logger.LogDebug($"Received message: {MessagePackSerializer.ConvertToJson(new ReadOnlyMemory<byte>(bytes))}");
                    var message = MessagePackSerializer.Deserialize<Message>(bytes);
                    var type = message.GetType();

                    if (!Verified)
                    {
                        switch (message)
                        {
                            case LoginSuccessMessage loginSuccess:
                                _sessionToken = new Guid(Secret.DecryptBytes(loginSuccess.Session, loginSuccess.Nonce));
                                break;
                            case ErrorMessage error:
                                OnError?.Invoke(error.Error);
                                break;
                        }
                    }


                    if (_messageDelegates.TryGetValue(message.GetType(), out var del) && del != null)
                        del.DynamicInvoke(message);
                    else
                        Logger.LogWarning($"No listener for {message.GetType().Name}");
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
                if (Verified)
                {
                    var nonce = Secret.NewNonce;
                    peer.Send(new VerifyMessage { Nonce = nonce, Session = Secret.EncryptBytes(_sessionToken.ToByteArray(), nonce) });
                }
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                Logger.LogInfo($"Peer {peer.Address}:{peer.Port} disconnected: {info.Reason}.");
                _peer = null;
                Connect(_lastHost, _lastPort);
            };

            listener.NetworkLatencyUpdateEvent +=
                (peer, latency) => Ping = latency; //Logger($"Ping received: {latency} ms");
        }
    }
}