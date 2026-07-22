# CultMesh transport parity harness

This harness compares the current public CultMesh content-session path with
dedicated byte streams under the same loopback conditions and payload size.
It is evidence for transport-plane selection, not a production transport.

The measured paths are:

- Authorized same-machine file mapping open and first/last-byte access. The
  provider body is created before the timer because it already exists in the
  real deployment.
- CultMesh content transfer over CultNet RUDP, including chunk verification,
  partial-file writes, final SHA-256 verification, and atomic promotion.
- TCP stream to a file, followed by SHA-256 verification.
- QUIC stream to a file, followed by SHA-256 verification. .NET delegates QUIC
  to MsQuic on supported platforms.

Run the Aetheria-sized comparison with:

```powershell
dotnet run --project tools/GameCult.TransportParity -c Release -- --bytes 56204750
```

The default payload is 56,204,750 bytes. Results report elapsed time,
application goodput, and RUDP wire bytes where available.
