#nullable enable
using System;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    public class NetworkingTests
    {
        private static readonly ServerSecurityOptions DevelopmentServerSecurity = ServerSecurityOptions.Development();
        private static readonly ClientSecurityOptions DevelopmentClientSecurity = ClientSecurityOptions.Development();

        [Test]
        public void EncryptDecrypt_Roundtrip()
        {
            var plaintext = Encoding.UTF8.GetBytes("test");
            var nonce = Secret.NewNonce;
            var encrypted = Secret.EncryptBytes(plaintext, nonce, DevelopmentClientSecurity);
            var decrypted = Secret.DecryptBytes(encrypted, nonce, DevelopmentClientSecurity);
            Assert.That(plaintext, Is.EqualTo(decrypted));
        }

        [Test]
        public void SessionToken_Validates_AndRejectsTampering()
        {
            var userId = Guid.NewGuid();
            var token = Secret.CreateSessionToken(userId, DateTimeOffset.UtcNow.AddMinutes(5), DevelopmentServerSecurity);

            Assert.That(Secret.TryValidateSessionToken(token, DevelopmentServerSecurity, out var parsedUserId, out var expiresAt), Is.True);
            Assert.That(parsedUserId, Is.EqualTo(userId));
            Assert.That(expiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));

            var tamperedToken = $"{token}tampered";
            Assert.That(Secret.TryValidateSessionToken(tamperedToken, DevelopmentServerSecurity, out _, out _), Is.False);
        }

        [Test]
        public void SessionToken_RejectsExpiredTokens()
        {
            var token = Secret.CreateSessionToken(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), DevelopmentServerSecurity);

            Assert.That(Secret.TryValidateSessionToken(token, DevelopmentServerSecurity, out _, out _), Is.False);
        }

        [Test]
        public void SecurityOptions_FromEnvironment_Rejects_MissingSecrets()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, null),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            Assert.That(
                () => ServerSecurityOptions.FromEnvironment(),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Server security configuration is not configured"));
        }

        [Test]
        public void SecurityOptions_FromEnvironment_Rejects_PartialConfiguration()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, "connection-key"),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            Assert.That(
                () => ServerSecurityOptions.FromEnvironment(),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains(ServerSecurityOptions.SessionSigningSecretEnvironmentVariable));
        }

        [Test]
        public void ServerSecurityOptions_FromEnvironment_Can_OptInto_DevelopmentDefaults()
        {
            using var _ = new EnvironmentVariableScope(
                (ServerSecurityOptions.ConnectionKeyEnvironmentVariable, null),
                (ServerSecurityOptions.SessionSigningSecretEnvironmentVariable, null));

            var options = ServerSecurityOptions.FromEnvironment(allowDevelopmentDefaults: true);

            Assert.That(options.IsDevelopment, Is.True);
            Assert.That(options.ConnectionKey, Is.Not.Empty);
        }

        [Test]
        public void MessageSerialization_RoundTrips_KnownMessageUnion()
        {
            var client = new Client(DevelopmentClientSecurity);
            var message = new LoginMessage
            {
                Nonce = Secret.NewNonce,
                Auth = Encoding.UTF8.GetBytes("auth"),
                Password = Encoding.UTF8.GetBytes("password")
            };

            var serializationType = client.GetType().Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var serialize = serializationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));

            var payload = (byte[])serialize.Invoke(null, [message])!;
            var roundTrip = (LoginMessage)deserialize.Invoke(null, [payload])!;

            Assert.That(roundTrip.Auth, Is.EqualTo(message.Auth));
            Assert.That(roundTrip.Password, Is.EqualTo(message.Password));
            Assert.That(roundTrip.Nonce, Is.EqualTo(message.Nonce));
        }

        [Test]
        public void MessageSerialization_Rejects_InvalidPayload()
        {
            var client = new Client(DevelopmentClientSecurity);
            var serializationType = client.GetType().Assembly
                .GetType("GameCult.Networking.MessageSerialization", throwOnError: true)!;
            var deserialize = serializationType.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeof(Message));

            Assert.That(
                () => deserialize.Invoke(null, [new byte[] { 0xC1 }]),
                Throws.TypeOf<TargetInvocationException>()
                    .With.InnerException.InstanceOf<MessagePack.MessagePackSerializationException>());
        }

        private sealed class EnvironmentVariableScope : IDisposable
        {
            private readonly (string Name, string? Value)[] _originalValues;

            public EnvironmentVariableScope(params (string Name, string? Value)[] values)
            {
                _originalValues = new (string Name, string? Value)[values.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    _originalValues[i] = (values[i].Name, Environment.GetEnvironmentVariable(values[i].Name));
                    Environment.SetEnvironmentVariable(values[i].Name, values[i].Value);
                }
            }

            public void Dispose()
            {
                foreach (var original in _originalValues)
                {
                    Environment.SetEnvironmentVariable(original.Name, original.Value);
                }
            }
        }
    }
}
