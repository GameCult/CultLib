# Eve Browser + Headless Counter

This is the real-process continuation of `samples/eve-two-runtime`:

- one C# provider owns a durable `.cc` counter, the Eve surface, command
  idempotency, and canonical receipts;
- one real Chromium page leases both documents through `cultmesh-browser` and
  lowers the provider-owned Eve surface;
- one independent C# headless client observes the same typed counter;
- the browser and C# clients speak the same binary CultNet schema-v0 WebSocket
  messages through the authenticated host adapter.

Run the proof from the CultLib repository:

```powershell
node scripts/verify-eve-browser-network.mjs --eve-root ..\Eve
```

The verifier clicks the Eve button twice with one idempotency key, requires one
state transition, restarts the provider, and requires the browser to rehydrate
the persisted count. Odin route discovery is represented by the browser
client's rendezvous port; a live Odin process remains the next discovery gate.
