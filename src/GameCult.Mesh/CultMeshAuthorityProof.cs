using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Networking;

namespace GameCult.Mesh
{
    public enum CultMeshAuthorityTrustMode
    {
        AuthenticatedRemote,
        LocalDevelopment
    }

    public sealed class CultMeshEcdsaP256PublicKey
    {
        public CultMeshEcdsaP256PublicKey(string keyId, string x, string y)
        {
            KeyId = Require(keyId, nameof(keyId));
            X = RequireCoordinate(x, nameof(x));
            Y = RequireCoordinate(y, nameof(y));
        }

        public string KeyId { get; }
        public string X { get; }
        public string Y { get; }

        internal ECDsa CreateVerifier()
        {
            var verifier = ECDsa.Create();
            verifier.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Convert.FromBase64String(X), Y = Convert.FromBase64String(Y) }
            });
            return verifier;
        }

        public static CultMeshEcdsaP256PublicKey From(string keyId, ECDsa key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var parameters = key.ExportParameters(false);
            return new CultMeshEcdsaP256PublicKey(
                keyId,
                Convert.ToBase64String(parameters.Q.X ?? throw new InvalidOperationException("P-256 key has no X coordinate.")),
                Convert.ToBase64String(parameters.Q.Y ?? throw new InvalidOperationException("P-256 key has no Y coordinate.")));
        }

        private static string RequireCoordinate(string value, string name)
        {
            var required = Require(value, name);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(required); }
            catch (FormatException error) { throw new ArgumentException("P-256 coordinates must be base64.", name, error); }
            if (bytes.Length != 32) throw new ArgumentException("P-256 coordinates must contain 32 bytes.", name);
            return required;
        }

        private static string Require(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must be non-empty.", name) : value.Trim();
    }

    public sealed class CultMeshRouteCertificate
    {
        public CultMeshRouteCertificate(
            CultMeshEcdsaP256PublicKey providerKey,
            string odinKeyId,
            long issuedAtUnixMilliseconds,
            long expiresAtUnixMilliseconds,
            string signature)
        {
            ProviderKey = providerKey ?? throw new ArgumentNullException(nameof(providerKey));
            OdinKeyId = string.IsNullOrWhiteSpace(odinKeyId)
                ? throw new ArgumentException("Odin key identity is required.", nameof(odinKeyId))
                : odinKeyId.Trim();
            if (issuedAtUnixMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(issuedAtUnixMilliseconds));
            if (expiresAtUnixMilliseconds <= issuedAtUnixMilliseconds) throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMilliseconds));
            IssuedAtUnixMilliseconds = issuedAtUnixMilliseconds;
            ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
            Signature = signature?.Trim() ?? string.Empty;
        }

        public CultMeshEcdsaP256PublicKey ProviderKey { get; }
        public string OdinKeyId { get; }
        public long IssuedAtUnixMilliseconds { get; }
        public long ExpiresAtUnixMilliseconds { get; }
        public string Signature { get; }
    }

    public sealed class CultMeshAuthorityTrustPolicy
    {
        private readonly Dictionary<string, CultMeshEcdsaP256PublicKey> _odinRoots;

        public CultMeshAuthorityTrustPolicy(
            CultMeshAuthorityTrustMode mode,
            IEnumerable<CultMeshEcdsaP256PublicKey>? odinRoots = null)
        {
            Mode = mode;
            _odinRoots = (odinRoots ?? Array.Empty<CultMeshEcdsaP256PublicKey>())
                .ToDictionary(key => key.KeyId, StringComparer.Ordinal);
        }

        public CultMeshAuthorityTrustMode Mode { get; }
        public IReadOnlyCollection<CultMeshEcdsaP256PublicKey> OdinRoots => _odinRoots.Values;

        /// <summary>Fails closed unless the route is valid under this consumer-owned policy.</summary>
        public void Validate(string verseId, CultMeshAuthorityRoute route, DateTimeOffset now) =>
            Verify(verseId, route, now);

        internal CultMeshVerifiedAuthorityRoute Verify(
            string verseId,
            CultMeshAuthorityRoute route,
            DateTimeOffset now)
        {
            if (route == null) throw new ArgumentNullException(nameof(route));
            var certificate = route.Certificate;
            if (certificate == null || string.IsNullOrWhiteSpace(certificate.Signature))
            {
                if (Mode == CultMeshAuthorityTrustMode.LocalDevelopment && IsLoopback(route.Endpoint))
                    return CultMeshVerifiedAuthorityRoute.Local(route);
                throw Failure(route, "Remote CultMesh routes require an Odin-signed authority certificate.");
            }
            if (!IsProtected(route.Endpoint) &&
                !(Mode == CultMeshAuthorityTrustMode.LocalDevelopment && IsLoopback(route.Endpoint)))
                throw Failure(route, "Authenticated remote CultMesh routes require TLS or QUIC channel protection.");
            if (!_odinRoots.TryGetValue(certificate.OdinKeyId, out var root))
                throw Failure(route, $"Odin key '{certificate.OdinKeyId}' is not trusted by this consumer.");
            var nowMs = now.ToUnixTimeMilliseconds();
            if (nowMs < certificate.IssuedAtUnixMilliseconds || nowMs >= certificate.ExpiresAtUnixMilliseconds)
                throw Failure(route, "The Odin route certificate is not currently valid.");
            byte[] signature;
            try { signature = Convert.FromBase64String(certificate.Signature); }
            catch (FormatException error) { throw Failure(route, "The Odin route signature is not base64.", error); }
            if (signature.Length != 64) throw Failure(route, "The Odin route signature is not IEEE P1363 P-256.");
            using var verifier = root.CreateVerifier();
            if (!verifier.VerifyData(
                    CultMeshAuthorityProof.CanonicalRoute(verseId, route),
                    signature,
                    HashAlgorithmName.SHA256))
                throw Failure(route, "The Odin route certificate signature is invalid.");
            return CultMeshVerifiedAuthorityRoute.Authenticated(route, certificate.ProviderKey);
        }

        private static bool IsLoopback(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            return uri.IsLoopback ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
                string.Equals(uri.Host, "::1", StringComparison.Ordinal);
        }

        private static bool IsProtected(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            return string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.IndexOf("quic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static CultMeshSessionException Failure(CultMeshAuthorityRoute route, string message, Exception? inner = null) =>
            new CultMeshSessionException(new CultMeshSessionFailure(
                CultMeshSessionFailureReason.Authentication,
                message,
                route.Endpoint), inner);
    }

    internal sealed class CultMeshVerifiedAuthorityRoute
    {
        private CultMeshVerifiedAuthorityRoute(
            CultMeshAuthorityRoute route,
            CultMeshEcdsaP256PublicKey? providerKey,
            bool localDevelopment)
        {
            Route = route;
            ProviderKey = providerKey;
            IsLocalDevelopment = localDevelopment;
        }

        public CultMeshAuthorityRoute Route { get; }
        public CultMeshEcdsaP256PublicKey? ProviderKey { get; }
        public bool IsLocalDevelopment { get; }
        public static CultMeshVerifiedAuthorityRoute Authenticated(CultMeshAuthorityRoute route, CultMeshEcdsaP256PublicKey key) => new(route, key, false);
        public static CultMeshVerifiedAuthorityRoute Local(CultMeshAuthorityRoute route) => new(route, null, true);
    }

    public sealed class CultMeshSessionProofSigner
    {
        private readonly ECDsa _key;

        public CultMeshSessionProofSigner(CultMeshAuthorityRoute route, ECDsa providerPrivateKey)
        {
            Route = route ?? throw new ArgumentNullException(nameof(route));
            _key = providerPrivateKey ?? throw new ArgumentNullException(nameof(providerPrivateKey));
            var certificate = route.Certificate ?? throw new ArgumentException("A session proof signer requires a certified route.", nameof(route));
            ProviderKeyId = certificate.ProviderKey.KeyId;
            var exported = CultMeshEcdsaP256PublicKey.From(ProviderKeyId, _key);
            if (!string.Equals(exported.X, certificate.ProviderKey.X, StringComparison.Ordinal) ||
                !string.Equals(exported.Y, certificate.ProviderKey.Y, StringComparison.Ordinal))
                throw new ArgumentException("Provider private key does not match the certified route key.", nameof(providerPrivateKey));
        }

        public CultMeshAuthorityRoute Route { get; }
        public string ProviderKeyId { get; }

        public string Sign(CultMeshSessionOpenMessage request)
        {
            var bytes = CultMeshAuthorityProof.CanonicalSession(request, Route.Endpoint);
            return Convert.ToBase64String(CultMeshAuthorityProof.SignP256(_key, bytes));
        }
    }

    public static class CultMeshAuthorityProof
    {
        private const string RouteDomain = "gamecult.cultmesh.route-certificate.v1";
        private const string SessionDomain = "gamecult.cultmesh.session-proof.v1";

        public static CultMeshAuthorityRoute CreateSignedRoute(
            string verseId,
            string authorityRuntimeId,
            string endpoint,
            IEnumerable<string> protocolIds,
            int priority,
            string generation,
            CultMeshEcdsaP256PublicKey providerKey,
            string odinKeyId,
            DateTimeOffset issuedAt,
            DateTimeOffset expiresAt,
            ECDsa odinPrivateKey)
        {
            if (odinPrivateKey == null) throw new ArgumentNullException(nameof(odinPrivateKey));
            var unsignedCertificate = new CultMeshRouteCertificate(
                providerKey,
                odinKeyId,
                issuedAt.ToUnixTimeMilliseconds(),
                expiresAt.ToUnixTimeMilliseconds(),
                string.Empty);
            var unsignedRoute = new CultMeshAuthorityRoute(
                authorityRuntimeId, endpoint, protocolIds, priority, generation, unsignedCertificate);
            var signature = Convert.ToBase64String(SignP256(
                odinPrivateKey,
                CanonicalRoute(verseId, unsignedRoute)));
            return new CultMeshAuthorityRoute(
                authorityRuntimeId,
                endpoint,
                protocolIds,
                priority,
                generation,
                new CultMeshRouteCertificate(
                    providerKey,
                    odinKeyId,
                    issuedAt.ToUnixTimeMilliseconds(),
                    expiresAt.ToUnixTimeMilliseconds(),
                    signature));
        }

        /// <summary>Verifies both the Odin route certificate and provider nonce proof.</summary>
        public static bool VerifySessionProof(
            CultMeshSessionOpenMessage request,
            CultMeshSessionAcceptedMessage response,
            string verseId,
            CultMeshAuthorityRoute route,
            CultMeshAuthorityTrustPolicy trust,
            DateTimeOffset now)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (trust == null) throw new ArgumentNullException(nameof(trust));
            return VerifySession(request, response, trust.Verify(verseId, route, now));
        }

        internal static byte[] CanonicalRoute(string verseId, CultMeshAuthorityRoute route)
        {
            var certificate = route.Certificate ?? throw new InvalidOperationException("Route certificate is required.");
            return Canonical(
                RouteDomain,
                verseId,
                route.AuthorityRuntimeId,
                route.Endpoint,
                string.Join("\u001f", route.ProtocolIds.OrderBy(value => value, StringComparer.Ordinal)),
                route.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                route.Generation,
                certificate.ProviderKey.KeyId,
                certificate.ProviderKey.X,
                certificate.ProviderKey.Y,
                certificate.OdinKeyId,
                certificate.IssuedAtUnixMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                certificate.ExpiresAtUnixMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        internal static byte[] CanonicalSession(CultMeshSessionOpenMessage request, string endpoint) => Canonical(
            SessionDomain,
            request.ClientNonce ?? string.Empty,
            request.MessageId ?? string.Empty,
            request.SourceRuntimeId ?? string.Empty,
            request.VerseId ?? string.Empty,
            request.AuthorityRuntimeId ?? string.Empty,
            request.ProtocolId ?? string.Empty,
            endpoint ?? string.Empty,
            request.RouteGeneration ?? string.Empty);

        internal static bool VerifySession(
            CultMeshSessionOpenMessage request,
            CultMeshSessionAcceptedMessage response,
            CultMeshVerifiedAuthorityRoute route)
        {
            if (route.IsLocalDevelopment) return true;
            if (route.ProviderKey == null ||
                !string.Equals(response.ProviderKeyId, route.ProviderKey.KeyId, StringComparison.Ordinal) ||
                !string.Equals(response.ClientNonce, request.ClientNonce, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(response.ProviderSignature))
                return false;
            byte[] signature;
            try { signature = Convert.FromBase64String(response.ProviderSignature); }
            catch (FormatException) { return false; }
            if (signature.Length != 64) return false;
            using var verifier = route.ProviderKey.CreateVerifier();
            return verifier.VerifyData(
                CanonicalSession(request, route.Route.Endpoint),
                signature,
                HashAlgorithmName.SHA256);
        }

        internal static byte[] SignP256(ECDsa key, byte[] payload)
        {
            var signature = key.SignData(payload, HashAlgorithmName.SHA256);
            if (signature.Length != 64)
                throw new CryptographicException("ECDSA provider did not emit IEEE P1363 P-256 signature bytes.");
            return signature;
        }

        private static byte[] Canonical(params string[] values)
        {
            using var stream = new MemoryStream();
            var length = new byte[4];
            foreach (var value in values)
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                stream.Write(length, 0, length.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            return stream.ToArray();
        }
    }
}
