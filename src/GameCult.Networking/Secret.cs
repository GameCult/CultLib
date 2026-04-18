using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameCult.Networking
{
    /// <summary>
    /// Provides shared encryption helpers and connection secrets for the networking layer.
    /// </summary>
    public static class Secret
    {
        private static readonly Lazy<ServerSecurityOptions> DefaultOptions =
            new(() => ServerSecurityOptions.FromEnvironment());

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>
        /// Configures the default server security options used by overloads that do not take an explicit options instance.
        /// </summary>
        public static void ConfigureDefault(ServerSecurityOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _configuredOptions = options;
        }

        private static ServerSecurityOptions? _configuredOptions;

        /// <summary>
        /// Shared LiteNetLib connection key from the configured default options.
        /// </summary>
        public static string ConnectionKey => GetDefaultOptions().ConnectionKey;

        /// <summary>
        /// Gets a newly generated nonce suitable for AES-GCM operations.
        /// </summary>
        public static byte[] NewNonce
        {
            get
            {
                var bytes = new byte[AesGcm.NonceByteSizes.MaxSize];
                Rng.GetBytes(bytes);
                return bytes;
            }
        }

        /// <summary>
        /// Encrypts a string using AES-GCM.
        /// </summary>
        /// <param name="input">The plaintext string to encrypt.</param>
        /// <param name="nonce">The nonce to use for the operation.</param>
        /// <returns>The encrypted payload, or <c>null</c> when <paramref name="input"/> is null or empty.</returns>
        public static byte[]? EncryptString(string? input, byte[] nonce) =>
            EncryptString(input, nonce, GetDefaultOptions());

        /// <summary>
        /// Encrypts a string using AES-GCM.
        /// </summary>
        public static byte[]? EncryptString(string? input, byte[] nonce, ClientSecurityOptions options)
        {
            if (string.IsNullOrEmpty(input)) return null;
            return EncryptBytes(Encoding.UTF8.GetBytes(input), nonce, options);
        }

        /// <summary>
        /// Encrypts a string using AES-GCM.
        /// </summary>
        public static byte[]? EncryptString(string? input, byte[] nonce, ServerSecurityOptions options)
        {
            if (string.IsNullOrEmpty(input)) return null;
            return EncryptBytes(Encoding.UTF8.GetBytes(input), nonce, options);
        }

        /// <summary>
        /// Encrypts a byte array using AES-GCM.
        /// </summary>
        /// <param name="input">The plaintext bytes to encrypt.</param>
        /// <param name="nonce">The nonce to use for the operation.</param>
        /// <returns>The authentication tag followed by the ciphertext.</returns>
        public static byte[] EncryptBytes(byte[] input, byte[] nonce) =>
            EncryptBytes(input, nonce, GetDefaultOptions());

        /// <summary>
        /// Encrypts a byte array using AES-GCM.
        /// </summary>
        public static byte[] EncryptBytes(byte[] input, byte[] nonce, ClientSecurityOptions options)
        {
            if (input == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid input or nonce");

            using var aes = new AesGcm(options.GetEncryptionKey());
            var ciphertext = new byte[input.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            aes.Encrypt(nonce, input, ciphertext, tag);
            var result = new byte[tag.Length + ciphertext.Length];
            Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, result, tag.Length, ciphertext.Length);
            return result;
        }

        /// <summary>
        /// Encrypts a byte array using AES-GCM.
        /// </summary>
        public static byte[] EncryptBytes(byte[] input, byte[] nonce, ServerSecurityOptions options)
        {
            if (input == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid input or nonce");

            using var aes = new AesGcm(options.GetEncryptionKey());
            var ciphertext = new byte[input.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            aes.Encrypt(nonce, input, ciphertext, tag);
            var result = new byte[tag.Length + ciphertext.Length];
            Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, result, tag.Length, ciphertext.Length);
            return result;
        }

        /// <summary>
        /// Decrypts an encrypted UTF-8 string payload.
        /// </summary>
        /// <param name="encrypted">The encrypted bytes produced by the corresponding string or byte encryption overloads.</param>
        /// <param name="nonce">The nonce used during encryption.</param>
        /// <returns>The decrypted string, or <c>null</c> when the inputs are invalid.</returns>
        public static string? DecryptString(byte[]? encrypted, byte[]? nonce) =>
            DecryptString(encrypted, nonce, GetDefaultOptions());

        /// <summary>
        /// Decrypts an encrypted UTF-8 string payload.
        /// </summary>
        public static string? DecryptString(byte[]? encrypted, byte[]? nonce, ClientSecurityOptions options)
        {
            if (encrypted == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize) return null;
            var decrypted = DecryptBytes(encrypted, nonce, options);
            return Encoding.UTF8.GetString(decrypted);
        }

        /// <summary>
        /// Decrypts an encrypted UTF-8 string payload.
        /// </summary>
        public static string? DecryptString(byte[]? encrypted, byte[]? nonce, ServerSecurityOptions options)
        {
            if (encrypted == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize) return null;
            var decrypted = DecryptBytes(encrypted, nonce, options);
            return Encoding.UTF8.GetString(decrypted);
        }

        /// <summary>
        /// Decrypts an AES-GCM payload into raw bytes.
        /// </summary>
        /// <param name="encrypted">The encrypted bytes containing the authentication tag prefix.</param>
        /// <param name="nonce">The nonce used during encryption.</param>
        /// <returns>The decrypted plaintext bytes.</returns>
        public static byte[] DecryptBytes(byte[] encrypted, byte[] nonce) =>
            DecryptBytes(encrypted, nonce, GetDefaultOptions());

        /// <summary>
        /// Decrypts an AES-GCM payload into raw bytes.
        /// </summary>
        public static byte[] DecryptBytes(byte[] encrypted, byte[] nonce, ClientSecurityOptions options)
        {
            if (encrypted == null || encrypted.Length < AesGcm.TagByteSizes.MaxSize || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid encrypted data or nonce");

            using var aes = new AesGcm(options.GetEncryptionKey());
            var decrypted = new byte[encrypted.Length - AesGcm.TagByteSizes.MaxSize];
            var tag = encrypted[..AesGcm.TagByteSizes.MaxSize];
            var ciphertext = encrypted[AesGcm.TagByteSizes.MaxSize..];
            aes.Decrypt(nonce, ciphertext, tag, decrypted);
            return decrypted;
        }

        /// <summary>
        /// Decrypts an AES-GCM payload into raw bytes.
        /// </summary>
        public static byte[] DecryptBytes(byte[] encrypted, byte[] nonce, ServerSecurityOptions options)
        {
            if (encrypted == null || encrypted.Length < AesGcm.TagByteSizes.MaxSize || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid encrypted data or nonce");

            using var aes = new AesGcm(options.GetEncryptionKey());
            var decrypted = new byte[encrypted.Length - AesGcm.TagByteSizes.MaxSize];
            var tag = encrypted[..AesGcm.TagByteSizes.MaxSize];
            var ciphertext = encrypted[AesGcm.TagByteSizes.MaxSize..];
            aes.Decrypt(nonce, ciphertext, tag, decrypted);
            return decrypted;
        }

        /// <summary>
        /// Creates a signed session token for the supplied user.
        /// </summary>
        /// <param name="userId">The authenticated user identifier.</param>
        /// <param name="expiresAtUtc">The UTC expiration time for the token.</param>
        /// <returns>A tamper-evident session token.</returns>
        public static string CreateSessionToken(Guid userId, DateTimeOffset expiresAtUtc) =>
            CreateSessionToken(userId, expiresAtUtc, GetDefaultOptions());

        /// <summary>
        /// Creates a signed session token for the supplied user.
        /// </summary>
        public static string CreateSessionToken(Guid userId, DateTimeOffset expiresAtUtc, ServerSecurityOptions options)
        {
            var payload = $"{userId:N}|{expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var signatureBytes = ComputeHmacSha256(options.GetSessionSigningKey(), payloadBytes);
            return $"{ToBase64Url(payloadBytes)}.{ToBase64Url(signatureBytes)}";
        }

        /// <summary>
        /// Validates and parses a signed session token.
        /// </summary>
        /// <param name="token">The token to validate.</param>
        /// <param name="userId">The extracted user identifier.</param>
        /// <param name="expiresAtUtc">The extracted UTC expiration time.</param>
        /// <returns><c>true</c> when the token is valid and unexpired.</returns>
        public static bool TryValidateSessionToken(string? token, out Guid userId, out DateTimeOffset expiresAtUtc) =>
            TryValidateSessionToken(token, GetDefaultOptions(), out userId, out expiresAtUtc);

        /// <summary>
        /// Validates and parses a signed session token.
        /// </summary>
        public static bool TryValidateSessionToken(
            string? token,
            ServerSecurityOptions options,
            out Guid userId,
            out DateTimeOffset expiresAtUtc)
        {
            userId = Guid.Empty;
            expiresAtUtc = DateTimeOffset.MinValue;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            var parts = token.Split('.');
            if (parts.Length != 2)
                return false;

            try
            {
                var payloadBytes = FromBase64Url(parts[0]);
                var signatureBytes = FromBase64Url(parts[1]);
                var expectedSignature = ComputeHmacSha256(options.GetSessionSigningKey(), payloadBytes);
                if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature))
                    return false;

                var payload = Encoding.UTF8.GetString(payloadBytes);
                var payloadParts = payload.Split('|');
                if (payloadParts.Length != 2 ||
                    !Guid.TryParseExact(payloadParts[0], "N", out userId) ||
                    !long.TryParse(payloadParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiryUnixSeconds))
                {
                    userId = Guid.Empty;
                    return false;
                }

                expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiryUnixSeconds);
                return expiresAtUtc > DateTimeOffset.UtcNow;
            }
            catch (FormatException)
            {
                userId = Guid.Empty;
                expiresAtUtc = DateTimeOffset.MinValue;
                return false;
            }
        }

        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] FromBase64Url(string input)
        {
            var padded = input
                .Replace('-', '+')
                .Replace('_', '/');

            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(data);
        }

        private static byte[] ComputeHmacSha256(byte[] key, byte[] data)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        private static ServerSecurityOptions GetDefaultOptions()
        {
            return _configuredOptions ?? DefaultOptions.Value;
        }
    }
}
