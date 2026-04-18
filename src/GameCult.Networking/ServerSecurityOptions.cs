using System;
using System.Security.Cryptography;
using System.Text;

namespace GameCult.Networking
{
    /// <summary>
    /// Carries the server-side networking configuration and server-only signing secret.
    /// </summary>
    public sealed class ServerSecurityOptions
    {
        /// <summary>
        /// Environment variable name for the LiteNetLib connection key.
        /// </summary>
        public const string ConnectionKeyEnvironmentVariable = "GAMECULT_CONNECTION_KEY";

        /// <summary>
        /// Environment variable name for the session-token signing secret.
        /// </summary>
        public const string SessionSigningSecretEnvironmentVariable = "GAMECULT_SESSION_SIGNING_SECRET";

        private readonly byte[] _encryptionKey;
        private readonly byte[] _sessionSigningKey;

        /// <summary>
        /// Shared LiteNetLib connection key and encryption seed.
        /// </summary>
        public string ConnectionKey { get; }

        /// <summary>
        /// Gets a value indicating whether the options were created for explicit development use.
        /// </summary>
        public bool IsDevelopment { get; }

        /// <summary>
        /// Initializes a new server security configuration.
        /// </summary>
        public ServerSecurityOptions(string connectionKey, string sessionSigningSecret, bool isDevelopment = false)
        {
            if (string.IsNullOrWhiteSpace(connectionKey))
                throw new ArgumentException("Connection key must be provided.", nameof(connectionKey));
            if (string.IsNullOrWhiteSpace(sessionSigningSecret))
                throw new ArgumentException("Session signing secret must be provided.", nameof(sessionSigningSecret));

            ConnectionKey = connectionKey;
            IsDevelopment = isDevelopment;
            _encryptionKey = ComputeSha256(Encoding.UTF8.GetBytes(connectionKey));
            _sessionSigningKey = ComputeSha256(Encoding.UTF8.GetBytes(sessionSigningSecret));
        }

        /// <summary>
        /// Creates a strict production-style configuration from environment variables.
        /// </summary>
        /// <param name="allowDevelopmentDefaults">
        /// When <c>true</c>, returns <see cref="Development"/> if both environment variables are absent.
        /// </param>
        public static ServerSecurityOptions FromEnvironment(bool allowDevelopmentDefaults = false)
        {
            var connectionKey = Environment.GetEnvironmentVariable(ConnectionKeyEnvironmentVariable);
            var sessionSigningSecret = Environment.GetEnvironmentVariable(SessionSigningSecretEnvironmentVariable);

            var missingConnectionKey = string.IsNullOrWhiteSpace(connectionKey);
            var missingSessionSigningSecret = string.IsNullOrWhiteSpace(sessionSigningSecret);

            if (missingConnectionKey && missingSessionSigningSecret)
            {
                if (allowDevelopmentDefaults)
                    return Development();

                throw new InvalidOperationException(
                    "Server security configuration is not configured. Set " +
                    $"{ConnectionKeyEnvironmentVariable} and {SessionSigningSecretEnvironmentVariable}, " +
                    "or explicitly use ServerSecurityOptions.Development() for local development.");
            }

            if (missingConnectionKey || missingSessionSigningSecret)
            {
                throw new InvalidOperationException(
                    "Server security configuration is partially configured. Missing: " +
                    string.Join(", ", GetMissingVariables(missingConnectionKey, missingSessionSigningSecret)) + ".");
            }

            return new ServerSecurityOptions(connectionKey!, sessionSigningSecret!);
        }

        /// <summary>
        /// Creates a deterministic development-only configuration.
        /// </summary>
        public static ServerSecurityOptions Development()
        {
            return new ServerSecurityOptions(
                "gamecult-dev-connection-key",
                "gamecult-dev-session-signing-secret",
                true);
        }

        internal byte[] GetEncryptionKey()
        {
            return _encryptionKey;
        }

        internal byte[] GetSessionSigningKey()
        {
            return _sessionSigningKey;
        }

        internal ClientSecurityOptions ToClientOptions()
        {
            return new ClientSecurityOptions(ConnectionKey);
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(data);
        }

        private static string[] GetMissingVariables(bool missingConnectionKey, bool missingSessionSigningSecret)
        {
            if (missingConnectionKey && missingSessionSigningSecret)
                return [ConnectionKeyEnvironmentVariable, SessionSigningSecretEnvironmentVariable];
            if (missingConnectionKey)
                return [ConnectionKeyEnvironmentVariable];
            return [SessionSigningSecretEnvironmentVariable];
        }
    }
}
