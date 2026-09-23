# QUIC test fixtures

`quic-test.p12` and `quic-test-cert.der` are a self-signed P-256 credential
used only to open a loopback MsQuic listener in tests
(`realtime-quic-native.test.ts`, `realtime-quic-consumer.test.ts`). Not a
production credential; no private key outside this pair signs anything real.

`quic-test-cert.der`'s subject is `CN=cultmesh-quic-native-tests`, and its
SHA-256 is what the tests pin as the expected certificate pin
(`cert-sha256`).

Generated with (mirrors the recipe the cut map records at P13):

```sh
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes \
  -keyout quic-test-key.pem -out quic-test-cert.pem \
  -subj "/CN=cultmesh-quic-native-tests" -days 3650

openssl x509 -in quic-test-cert.pem -outform der -out quic-test-cert.der

openssl pkcs12 -export -inkey quic-test-key.pem -in quic-test-cert.pem \
  -out quic-test.p12 -passout pass:
```

Regenerate the same way if the pair ever needs replacing; nothing here
depends on today's exact bytes past what the tests read back out (the DER
cert and its SHA-256, and a PKCS12 the native bridge can load).
