using System;
using System.Security.Cryptography;
using System.Text;

namespace GameCult.Networking
{
    public static class Secret
    {
        public const string ConnectionKey = "cultpong-843ctnsw";
        public static readonly byte[] EncryptionKey = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(ConnectionKey)); // PSK
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] NewNonce
        {
            get
            {
                var bytes = new byte[AesGcm.NonceByteSizes.MaxSize];
                Rng.GetBytes(bytes);
                return bytes;
            }
        }

        public static byte[] EncryptString(string input, byte[] nonce)
        {
            if (string.IsNullOrEmpty(input)) return null;
            return EncryptBytes(Encoding.UTF8.GetBytes(input), nonce);
        }

        public static byte[] EncryptBytes(byte[] input, byte[] nonce)
        {
            if (input == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid input or nonce");

            using var aes = new AesGcm(EncryptionKey);
            var ciphertext = new byte[input.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            aes.Encrypt(nonce, input, ciphertext, tag);
            var result = new byte[tag.Length + ciphertext.Length];
            Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, result, tag.Length, ciphertext.Length);
            return result;
        }

        public static string DecryptString(byte[] encrypted, byte[] nonce)
        {
            if (encrypted == null || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize) return null;
            var decrypted = DecryptBytes(encrypted, nonce);
            return Encoding.UTF8.GetString(decrypted);
        }

        public static byte[] DecryptBytes(byte[] encrypted, byte[] nonce)
        {
            if (encrypted == null || encrypted.Length < AesGcm.TagByteSizes.MaxSize || nonce == null || nonce.Length != AesGcm.NonceByteSizes.MaxSize)
                throw new CryptographicException("Invalid encrypted data or nonce");

            using var aes = new AesGcm(EncryptionKey);
            var decrypted = new byte[encrypted.Length - AesGcm.TagByteSizes.MaxSize];
            var tag = encrypted[..AesGcm.TagByteSizes.MaxSize];
            var ciphertext = encrypted[AesGcm.TagByteSizes.MaxSize..];
            aes.Decrypt(nonce, ciphertext, tag, decrypted);
            return decrypted;
        }
    }
}