using System;
using System.Security.Cryptography;
using System.Text;

namespace GameCult.Networking
{
    /// <summary>
    /// Carries the client-side networking configuration required to talk to a server.
    /// </summary>
    public sealed class ClientSecurityOptions
    {
        private readonly byte[] _encryptionKey;

        /// <summary>
        /// Shared LiteNetLib connection key and encryption seed.
        /// </summary>
        public string ConnectionKey { get; }

        /// <summary>
        /// Initializes a new client security configuration.
        /// </summary>
        /// <param name="connectionKey">The shared connection key used by both client and server.</param>
        public ClientSecurityOptions(string connectionKey)
        {
            if (string.IsNullOrWhiteSpace(connectionKey))
                throw new ArgumentException("Connection key must be provided.", nameof(connectionKey));

            ConnectionKey = connectionKey;
            _encryptionKey = ComputeSha256(Encoding.UTF8.GetBytes(connectionKey));
        }

        /// <summary>
        /// Creates a deterministic development-only configuration.
        /// </summary>
        public static ClientSecurityOptions Development()
        {
            return new ClientSecurityOptions("gamecult-dev-connection-key");
        }

        internal byte[] GetEncryptionKey()
        {
            return _encryptionKey;
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(data);
        }
    }
}
