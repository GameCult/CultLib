# Prior art: presence provider as a library, launcher as a consumer

Served: `cultlib-gaps:cut_spec:cut-cultnet-rs-presence-provider-a.r1` and the
Defaulted rulings `presence-provider-network-free-core`,
`presence-credentials-both-idunn-drivers` and `presence-provider-consumers-adopt`,
applying the operator's `gamecult-chat:ruling:presence-one-owner-library-launcher-consumer`.
Imagination pass, 2026-10-29, from recall of the cited manuals (not re-fetched).

- libsystemd `sd_notify(3)`: a service links a small library and reports its
  own state (READY, STATUS, WATCHDOG); programs that cannot link it use
  `systemd-notify(1)` or a wrapper. Same split as cultnet-rs provider plus
  idunn-launch as the sidecar consumer.
- `sd_listen_fds_with_names(3)`: checks LISTEN_PID against the process, sets
  close-on-exec on passed descriptors, finds them by name, and can unset the
  LISTEN_* environment. This set is the descriptor rule the provider adopts.
- systemd watchdog (`WATCHDOG=1` within `WatchdogSec`): the model for the
  warming heartbeat.
- Consul: TTL checks updated by the service through the client API, versus
  script/HTTP checks the agent runs for services that cannot. In-process
  versus sidecar again.
- Kubernetes: kubelet-run probes observe from outside; readiness gates are
  reported by the app. Idunn's route challenge is the outside observer, the
  signed presence the app's own report.

Existing copies compared: CodexConnector `src/idunn_health.rs` (two
descriptors, strict PID check), Idunn launcher (two descriptors, no PID check),
cultnet-ts `idunn-runtime-authority.ts` (descriptors by name, systemd private
PID case). The provider takes the strictest union.
