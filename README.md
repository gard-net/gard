# garc

Open-source command-line tool for measuring the quality of a **local network** link between two peers: **throughput, latency, jitter and packet loss**.

Think of it as a LAN-focused cousin of `iperf3`. Where `iperf3` measures raw bandwidth between arbitrary hosts over any network, `garc` is designed specifically for the link between devices the user owns — inside their own Wi-Fi or wired LAN — and speaks the open [**LSP/1.1 protocol**](docs/LSP_PROTOCOL_v1.md) so implementations can interoperate across platforms.

> **Status:** v0.1 in progress. Protocol is stable (LSP/1.1); the CLI is being extracted from the reference .NET 10 implementation. First packaged release: coming soon.

## Why another tool?

- **LAN-native.** Discovery via mDNS (`_landspeed._tcp`), no central server, no Internet egress.
- **Honest numbers.** Reports throughput, RTT under load, jitter, packet loss and per-window intervals (median / p95 / stdev). Control and data travel on separate frames so the control parser never bottlenecks the measurement.
- **Portable.** The protocol spec is the source of truth (CC-BY 4.0). Today there are reference implementations in Swift (Apple) and .NET 10 (cross-platform). Ports to Rust, Go or any TCP+mDNS stack are welcome.
- **Open.** Apache-2.0 for the code, CC-BY 4.0 for the spec. Patent grant included.

## Installation

Packaged binaries (`.deb`, Homebrew tap, Windows zip) will be published with the first tagged release. Until then, build from source:

```sh
git clone https://github.com/garcnet/garc.git
cd garc
dotnet publish src/Garc.Cli -c Release -r <rid> -p:PublishAot=true -o ./out
./out/garc --help
```

Where `<rid>` is `linux-x64`, `osx-arm64`, `osx-x64` or `win-x64`.

## Usage (preview)

```sh
# Discover peers on the local network
garc scan

# Run as a host so other peers can test against this machine
garc host

# Measure against a peer
garc test 192.168.1.50

# Machine-readable output for scripts / monitoring
garc test 192.168.1.50 --json
```

Full command reference will live in `docs/man/garc.1`.

## Protocol

garc implements [**Landspeed Protocol 1.1 (LSP/1.1)**](docs/LSP_PROTOCOL_v1.md) — a small, auditable TCP framing with JSON control and binary data channels, discovery over mDNS, and optional pairing / TLS.

The spec is the source of truth. Implementations are expected to validate against it, not against any one reference implementation.

## Relationship to Landspeed

[Landspeed](https://github.com/marcorojasb/Landspeed) is the reference Apple app (iOS / iPadOS / macOS) that originated the protocol. [triuque](https://github.com/marcorojasb/triuque) is the reference Windows app on .NET 10. `garc` extracts the platform-neutral core of the .NET implementation into a cross-platform CLI so the protocol can live on servers and headless machines — where apps cannot reach.

## License

- **Code** (`src/`, `packaging/`, build scripts): [Apache License 2.0](LICENSE).
- **Protocol specification and docs** (`docs/`): [Creative Commons Attribution 4.0 International](LICENSE-docs).

Copyright © 2026 Marco Rojas.
