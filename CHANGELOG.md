# Changelog

All notable changes to this project are documented here. The format is loosely based on [Keep a Changelog](https://keepachangelog.com/).

`gard` versions the CLI independently from the LSP protocol. The protocol
version is documented inside [docs/LSP_PROTOCOL_v1.md](docs/LSP_PROTOCOL_v1.md);
protocol-level changes are summarised in that file's §11.

## [Unreleased]

## [0.2.2] — 2026-04-24

Fix de throughput del emisor UDP detectado tras 0.2.1 contra `landspeed
1.5.6`: el host gard capeaba a ~12 Mbps en loopback Windows con
`--bitrate 200M`, mientras `iperf3` mantenía ~800 Mbps en la misma red.
Diagnóstico: el scheduler de Windows tiene granularidad por defecto
~15.6 ms, y cada `await socket.SendAsync(...)` costaba ~135 µs por el
round-trip IOCP/ThreadPool — el loop quedaba techado muy por debajo del
target aunque el pacing deadline-based fuera correcto.

### Fixed
- **Windows timer resolution** — nuevo `WindowsTimerResolution.RaiseToOneMs()`
  que llama `timeBeginPeriod(1)` vía P/Invoke a `winmm.dll` al iniciar
  `gard host`, y `timeEndPeriod(1)` al shutdown. En macOS/Linux es no-op.
  Baja la granularidad del scheduler del proceso a 1 ms, desbloqueando
  pacing sub-ms.
- **UDP send sincrónico en el hot loop** — reemplazado
  `await socket.SendAsync(...)` por `socket.Client.Send(...)` /
  `SendTo(...)` dentro del loop del `UdpSender`. El `await Task.Yield()`
  inicial sigue garantizando que no corremos sync en el thread del
  caller. El send UDP real al kernel es microsegundos; hacerlo sync
  evita el round-trip async por paquete.

### Verified
- Loopback macOS arm64: 940 Mb/s @ `--bitrate 1000M`, 470 Mb/s @ 500M,
  188 Mb/s @ 200M, 0 % loss, miss ≈ 6 % (warmup).
- 115/115 tests Gard.Core.Tests pasan.

## [0.2.1] — 2026-04-22

Fix puntual sobre el emisor UDP del host (y por consistencia también del
cliente) detectado contra `landspeed 1.5.5` en Windows + 1 GbE: con un
`test_start` pidiendo 200 Mbps/stream × 4 streams, gard entregaba ~4 Mbps
agregados en vez de ~800 Mbps.

### Fixed
- **UDP host sender pacing** — el token bucket viejo dormía vía
  `Task.Delay(1)` (granularidad ~15.6 ms en Windows) y capeaba tokens a
  10 ms worth, descartando tokens cada tick del scheduler. Reemplazado
  por scheduling deadline-based (`nextDueNs = start + seq × nsPerPacket`)
  con `Task.Delay` sólo para huecos ≥ 2 ms y busy-wait contra
  `MonotonicClock` para la precisión sub-ms. Se limita la deuda a 50 ms
  para evitar ráfagas tras pausas del socket.
- **`target_bitrate_bps` ahora es per-stream** (no agregado) — coincide
  con la referencia Swift (`LandspeedCore.UdpSender`). Antes gard dividía
  por `streams`, lo que hacía que un cliente pidiendo 200 Mbps/stream
  obtuviera 200 Mbps/`streams` por stream (ej. 50 Mbps con 4 streams).
  Ver §12.3 del spec, clarificado en el mismo commit.

### Added
- `CHANGELOG.md` (entrada retroactiva para 0.2.0 ya existente).

## [0.2.0] — 2026-04-21

First publishable tagged release. LSP/1.2 is implemented end-to-end and
validated against `iperf3` on a real MBP ↔ Windows PC cabled link.

### Added
- **LSP/1.2 UDP data-plane** — TCP control + UDP data. Negotiated via
  the `udp_data_plane` capability (`0x0100`); falls back to TCP for
  peers that don't advertise it.
  - `HELLO_UDP` (seq=0) for pinhole / 5-tuple learning.
  - `udp_stats` control message so the sender reports its counters
    after `test_end` (needed for loss computation in `up` direction).
  - `up`, `down` and `bidir` all supported over UDP.
  - New error codes: `3003` (UDP not negotiated), `3004` (payload out
    of range), `3005` (bitrate not honored, informational).
- **Release pipeline**: self-contained single-file binaries for
  `linux-x64`, `osx-arm64`, `osx-x64`, `win-x64`, plus a `.deb`.
  SHA256 per artifact + consolidated `SHA256SUMS` on each release.
- **Benchmark suite** (`scripts/bench/run_suite.sh`) that runs a matrix
  of gard vs iperf3 cells, including UDP, and emits a summary with
  `rtt_p95` under load and loss %.

### Changed
- Banner now reads the protocol version dynamically from
  `ProtocolVersion.Current` — no more hand-edited `LSP/1.x` strings.
- CI in Spanish with NuGet cache, concurrency, TRX test summary and
  timeouts.
- mDNS browser: buffers SRV/TXT/A/AAAA across messages (fixes a race
  on noisy networks).

### Fixed
- `UdpDataPlane.HostDrainHelloAsync`: on timeout, the host now reports
  which streams failed to send `HELLO_UDP` instead of throwing an
  opaque `OperationCanceledException`.
- Bench script: `parse_iperf3` was reading non-existent JSON fields
  for bidir (`sum_bidir_recv` / `sum_bidir_send` → real fields are
  `sum_received` / `sum_received_bidir_reverse`).
- Bench script: `parse_gard` column indices were off-by-one for every
  metric except throughput (jitter was actually loss_pct, etc.).
- Bench preflight uses TCP connect instead of ICMP (Windows Firewall
  blocks ICMP by default).

### Protocol
- Bumped reference version to **LSP/1.2**. See §11 of
  [docs/LSP_PROTOCOL_v1.md](docs/LSP_PROTOCOL_v1.md) for the full
  1.1 → 1.2 diff: `test_start.transport`, `test_start.target_bitrate_bps`,
  `result.udp{…}`, §12 (data-plane UDP spec), new error codes.

### For implementers migrating from LSP/1.1
- No wire-level breaking changes. A 1.1 client talking to a 1.2 host
  works unchanged — the host simply won't advertise `udp_data_plane`
  as active.
- To add UDP support, implement §12 of the spec and advertise the
  `udp_data_plane` capability in `caps`. Start with `direction=down`
  (simpler: client receives, reports); then `up` (requires
  `udp_stats` from sender); then `bidir`.

## [0.1.0]

Initial extraction of the .NET 10 reference CLI. LSP/1.1 (TCP-only).
