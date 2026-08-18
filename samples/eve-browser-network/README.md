# Eve Browser + Headless Counter

This is the real-process continuation of `samples/eve-two-runtime`:

- one C# provider owns a durable `.cc` counter, the Eve surface, command
  idempotency, and canonical receipts;
- one real Chromium page leases both documents through `cultmesh-browser` and
  lowers the provider-owned Eve surface;
- one retained C# headless client discovers the provider through the same Odin
  identity, observes the typed counter and canonical receipt ids, and survives
  the provider route replacement without application reconnect code;
- one separate local Odin fixture answers the canonical CultMesh Verse catalog
  over binary CultNet WebSocket messages;
- the browser and C# clients speak the same binary CultNet schema-v0 WebSocket
  messages through the authenticated host adapter.

Run the proof from the CultLib repository:

```powershell
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

The wrapper first proves clean package consumption, then runs this real-process
checkpoint. Run `node scripts/verify-eve-browser-network.mjs --eve-root ../Eve`
directly only to diagnose the network layer. Browser discovery is portable:
`CHROME_PATH` wins when configured, then common Chrome/Chromium/Edge locations
and Playwright's installed Chromium are checked.

The verifier clicks the Eve button twice with one idempotency key, requires one
state transition, restarts the provider on a different physical port, updates
the local Odin fixture, and requires the still-open browser to rediscover the
route, resubscribe, execute a second command, and preserve the durable count.
The retained C# lease independently rediscovers the same replacement route and
observes both canonical receipt ids.
The fixture speaks the production Verse-catalog contract; a deployment smoke
against the full Odin daemon remains a separate infrastructure gate.

The `odin` fixture defaults to this sample's Verse, but its `--verse-id`,
`--verse-name`, `--authority-runtime-id`, `--transport-version`, and
`--rules-hash` arguments can advertise another real provider during a product
route-rotation witness. The provider endpoint remains data owned by Odin; the
consumer still connects only by stable Verse and provider identity.
