# Test fixtures

`content-https-test-cert.pem` and `content-https-test-key.pem` are a loopback-only
test credential. They sign nothing real: the certificate is self-signed P-256, valid
for `127.0.0.1` and `localhost`, `CA:FALSE`, `serverAuth` only, and the key is public
on purpose.

They exist for `interop/content-https.test.ts`. The TypeScript provider serves HTTPS
with this pair. The C# suite (`CultMeshHttpsContentInteropTests`) anchors the
certificate with `CustomRootTrust`, so chain and name validation stay on and no
other certificate is accepted. The TypeScript reader trusts it through
`NODE_EXTRA_CA_CERTS`, set on the reader's child process only.

Regenerate (Git Bash needs the `MSYS_NO_PATHCONV=1` prefix); no test pins the bytes:

    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes \
      -keyout content-https-test-key.pem -out content-https-test-cert.pem -days 36500 \
      -subj "/CN=cultmesh-content-https-tests" \
      -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
      -addext "basicConstraints=critical,CA:FALSE" \
      -addext "keyUsage=critical,digitalSignature" \
      -addext "extendedKeyUsage=serverAuth"
