# Eve Browser + Headless Counter

This is the real-process continuation of `samples/eve-two-runtime`:

- one C# provider owns a durable `.cc` counter, the Eve surface, command
  idempotency, and canonical receipts;
- one real Chromium page leases both documents through `cultmesh-browser` and
  lowers the provider-owned Eve surface;
- one retained C# headless client discovers the provider through the same Odin
  identity, observes the typed counter and canonical receipt ids, survives the
  provider route replacement, and invokes through the generic CultMesh client
  without application transport or reconnect code;
- one separate local Odin fixture answers the canonical CultMesh Verse catalog
  over binary CultNet WebSocket messages;
- one wrong-authority provider is deliberately advertised on a better-priority
  route, and both clients must ignore it because the route is bound to another
  runtime;
- the browser and C# clients speak the same binary CultNet schema-v0 WebSocket
  messages through the authenticated host adapter.

The provider registers typed commands through `CultNetOperationServer`.
Application code receives `CultNetOperationContext<TRequest>` and returns a
typed receipt; it does not parse base64, switch on wire envelopes, or construct
correlation responses. The counter's durable transaction still owns
idempotency and state mutation. The provider opens one public `CultMeshNode`;
it does not assemble a parallel CultCache/CultNet database host by hand.

Run the proof from the CultLib repository:

```powershell
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

The wrapper first proves clean package consumption, then runs this real-process
checkpoint. Run `node scripts/verify-eve-browser-network.mjs --eve-root ../Eve`
directly only to diagnose the network layer. Browser discovery is portable:
`CHROME_PATH` wins when configured, then common Chrome/Chromium/Edge locations
and Playwright's installed Chromium are checked.

The verifier clicks the Eve button twice with one idempotency key and requires
one state transition. It then restarts the provider on a different physical
port, updates the local Odin fixture, and requires both retained clients to
rediscover and resubscribe. The C# client invokes the second command through
`CultMeshClient.InvokeAsync`; Chromium observes the resulting count and both
clients observe both canonical receipt ids.
The retained C# client then sends 10,000 typed no-op operations over the same
real WebSocket session. The gate records p99 latency, throughput, managed heap,
and private memory, and rejects more than 8 MiB of post-GC managed growth or a
250 ms p99. The no-op provider handler stores no receipt history, so persistence
growth cannot impersonate a client leak.
The fixture speaks the production Verse-catalog contract; a deployment smoke
against the full Odin daemon remains a separate infrastructure gate.

The `odin` fixture defaults to this sample's Verse, but its `--verse-id`,
`--verse-name`, `--authority-runtime-id`, `--transport-version`, and
`--rules-hash` arguments can advertise another real provider during a product
route-rotation witness. The provider endpoint remains data owned by Odin; the
consumer still connects only by stable Verse and provider identity.
