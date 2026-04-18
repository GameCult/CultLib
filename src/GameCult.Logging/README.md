# GameCult.Logging

`GameCult.Logging` provides a common logging contract for cache, networking, and Unity-facing code.

## Scope

This package provides a minimal logging abstraction. It does not provide structured logging, external sinks, scopes, or framework integration.

It is designed for:

- simple runtime diagnostics
- lightweight library dependencies
- swapping logging behavior by environment
- avoiding a hard dependency on a specific logging ecosystem

## Included Types

- `ILogger`: interface with `LogInfo`, `LogWarning`, `LogError`, and `LogDebug`
- `NullLogger`: no-op implementation
- `ConsoleLogger`: writes to `System.Console`
- `FileLogger`: appends timestamped log lines to a file
- `UnityLogger`: Unity-specific implementation in `src/GameCult.Logging.Unity`

## Behavior Notes

- `NullLogger` is the safe default used by other libraries when no logger is supplied.
- `ConsoleLogger` colors warnings and errors.
- `FileLogger` writes plain text entries with timestamps.
- `UnityLogger` schedules logging to the next Unity frame instead of writing immediately.

## Typical Usage

```csharp
using GameCult.Logging;

ILogger logger = new ConsoleLogger();

logger.LogInfo("Server starting");
logger.LogWarning("High latency detected");
logger.LogError("Connection failed");
```

## Swapping Implementations

Consumers can choose the implementation that fits the runtime.

```csharp
using GameCult.Logging;
using GameCult.Networking;

var client = new Client
{
    Logger = new FileLogger("client.log")
};

client.Connect("127.0.0.1", 3075);
```

## When To Use Something Else

If you need:

- structured events
- sinks to external observability platforms
- log scopes
- sampling and filtering pipelines
- JSON log output

use a logging framework at the application boundary.
