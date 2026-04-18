# GameCult.Networking.Tests

`GameCult.Networking.Tests` contains NUnit tests for the networking library.

## Scope

The tests currently cover:

- AES-GCM encrypt/decrypt round-trips in `Secret`
- signed session-token validation
- signed session-token tamper detection
- signed session-token expiry handling

## Run

```powershell
dotnet test tests\GameCult.Networking.Tests\GameCult.Networking.Tests.csproj
```

## Adding New Tests

Typical future areas to expand include:

- client reconnect and verify flows
- login and registration integration tests
- malformed message handling
- rate-limiting behavior under concurrency
