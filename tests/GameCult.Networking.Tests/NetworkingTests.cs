using System.Text;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    public class NetworkingTests
    {
        [Test]
        public void EncryptDecrypt_Roundtrip()
        {
            var plaintext = Encoding.UTF8.GetBytes("test");
            var nonce = Secret.NewNonce;
            var encrypted = Secret.EncryptBytes(plaintext, nonce);
            var decrypted = Secret.DecryptBytes(encrypted, nonce);
            Assert.That(plaintext, Is.EqualTo(decrypted));
        }
    }
}