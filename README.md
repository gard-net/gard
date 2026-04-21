# gard

Open-source command-line tool for measuring the quality of a **local network** link between two peers: **throughput, latency, jitter and packet loss**.

Think of it as a LAN-focused cousin of `iperf3`. Where `iperf3` measures raw bandwidth between arbitrary hosts over any network, `gard` is designed specifically for the link between devices the user owns — inside their own Wi-Fi or wired LAN — and speaks the open [**LSP/1.2 protocol**](docs/LSP_PROTOCOL_v1.md) so implementations can interoperate across platforms.

> **Status:** v0.2 — LSP/1.2 with UDP data-plane is implemented and validated end-to-end (MBP ↔ Windows PC, cabled gigabit: gard matches iperf3 within normal TCP variance). Self-contained single-file binaries are published per tagged release for linux-x64, osx-arm64, osx-x64 and win-x64, plus a `.deb`. See [CHANGELOG.md](CHANGELOG.md).

## Why another tool?

- **LAN-native.** Discovery via mDNS (`_landspeed._tcp`), no central server, no Internet egress.
- **Honest numbers.** Reports throughput, RTT under load, jitter, packet loss and per-window intervals (median / p95 / stdev). Control and data travel on separate frames so the control parser never bottlenecks the measurement.
- **Portable.** The protocol spec is the source of truth (CC-BY 4.0). Today there are reference implementations in Swift (Apple) and .NET 10 (cross-platform). Ports to Rust, Go or any TCP+mDNS stack are welcome.
- **Open.** Apache-2.0 for the code, CC-BY 4.0 for the spec. Patent grant included.

## Installation

Packaged binaries (`.tar.gz`, `.zip`, `.deb`) are attached to each [GitHub Release](https://github.com/gard-net/gard/releases) with SHA256 checksums. Or build from source:

```sh
git clone https://github.com/gard-net/gard.git
cd gard
dotnet publish src/Gard.Cli -c Release -r <rid> \
  --self-contained -p:PublishSingleFile=true -o ./out
./out/gard --help
```

Where `<rid>` is `linux-x64`, `osx-arm64`, `osx-x64` or `win-x64`.

> AOT-compiled binaries (~15 MB) are planned for v0.2; v0.1 ships
> self-contained single-file builds (~60 MB) so the protocol core can
> keep its reflection-based JSON path without risk.

## Usage (preview)

```sh
# Discover peers on the local network
gard scan

# Run as a host so other peers can test against this machine
gard host

# Measure against a peer
gard test 192.168.1.50

# Machine-readable output for scripts / monitoring
gard test 192.168.1.50 --json
```

Full command reference will live in `docs/man/gard.1`.

## Protocol

gard implements [**Landspeed Protocol 1.2 (LSP/1.2)**](docs/LSP_PROTOCOL_v1.md) — a small, auditable TCP control channel with JSON messages, a TCP or UDP data-plane (negotiated via `caps`), discovery over mDNS, and optional pairing / TLS. LSP/1.2 is backwards compatible with 1.1 and 1.0 peers: UDP is only used when both sides advertise the `udp_data_plane` capability, otherwise the test falls back to TCP.

The spec is the source of truth. Implementations are expected to validate against it, not against any one reference implementation. Section §11 of the spec lists the exact 1.1 → 1.2 diff for implementers.

## Relationship to Landspeed

[Landspeed](https://github.com/marcorojasb/Landspeed) is the reference Apple app (iOS / iPadOS / macOS) that originated the protocol. [triuque](https://github.com/marcorojasb/triuque) is the reference Windows app on .NET 10. `gard` extracts the platform-neutral core of the .NET implementation into a cross-platform CLI so the protocol can live on servers and headless machines — where apps cannot reach.

## License

- **Code** (`src/`, `packaging/`, build scripts): [Apache License 2.0](LICENSE).
- **Protocol specification and docs** (`docs/`): [Creative Commons Attribution 4.0 International](LICENSE-docs).

Copyright © 2026 Marco Rojas.
