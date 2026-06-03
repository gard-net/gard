# gard

**Local network quality, measured like a product you can trust.**

Gard is an open-source CLI for measuring the real quality of a link between
devices on your LAN: throughput, latency, jitter, packet loss and RTT under
load. It is built for engineers who need more than a speed number and less than
a lab full of custom tooling.

Gard speaks the open **Landspeed Protocol (LSP/1.2)**, discovers peers over
mDNS, supports TCP and UDP data planes, ships as SDK-free native/self-contained
binaries, and emits clean human, JSON and CSV output.

```sh
# Terminal A: listen on one machine
gard host

# Terminal B: measure from another machine
gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
```

> Status: `v0.2.5`, LSP/1.2. This release focuses on native distribution,
> interactive pixel-art CLI polish and operational diagnostics. LSP wire
> compatibility is unchanged.

## Why Gard

`iperf3` is excellent at raw throughput. Gard is shaped around local devices and
repeatable product-grade diagnostics:

- Discover peers on your LAN with no central service.
- Measure throughput, latency, jitter, packet loss and RTT under load together.
- Use TCP for baseline tests or UDP for paced, loss-aware measurements.
- Run a polished interactive terminal UI, or force plain output for logs.
- Script results as JSON or CSV without scraping terminal output.
- Interoperate through the documented LSP/1.2 protocol.

## Quick Start

Install a release binary from:

```text
https://github.com/gard-net/gard/releases
```

Then run:

```sh
gard host --name office-mac
```

On another device:

```sh
gard scan --seconds 5
gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
gard watch 192.168.1.50 --interval 5
gard info 192.168.1.50
gard doctor
```

For UDP:

```sh
gard test 192.168.1.50 \
  --transport udp \
  --direction up \
  --payload 1200 \
  --bitrate 200M
```

For automation:

```sh
gard test 192.168.1.50 --format json
gard test 192.168.1.50 --format csv --label nightly-lab
gard test --csv-header
```

## Install

Release assets do not require a system .NET runtime or .NET SDK. Gard attempts
Native AOT release binaries first; if a platform cannot publish AOT cleanly, the
release workflow falls back to a self-contained single-file executable.

You need the .NET SDK only when building Gard from source.

| Platform | Asset |
| --- | --- |
| Linux x64 | `.tar.gz`, `.deb` |
| macOS Apple Silicon | `osx-arm64.tar.gz` |
| macOS Intel | `osx-x64.tar.gz` |
| Windows x64 | `.zip` |

Each release includes SHA256 checksums and a `BUILD_KIND` file in the archive
that says `native-aot` or `self-contained`.

## Build From Source

Requirements:

- .NET SDK 10.0.x
- Git

```sh
git clone https://github.com/gard-net/gard.git
cd gard
dotnet restore gard.slnx
dotnet build gard.slnx -c Release --no-restore
dotnet run --project src/Gard.Cli -c Release -- --help
```

Publish a Native AOT binary:

```sh
dotnet publish src/Gard.Cli -c Release -r osx-arm64 \
  -p:PublishAot=true \
  -o ./out/osx-arm64-aot
```

Publish a self-contained fallback binary:

```sh
dotnet publish src/Gard.Cli -c Release -r osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./out/osx-arm64
```

Common runtime IDs: `linux-x64`, `osx-arm64`, `osx-x64`, `win-x64`.
See [docs/native-aot-notes.md](docs/native-aot-notes.md) for the current AOT
audit and fallback policy.

## CLI

```text
gard <command> [options]
gard help <command>
gard --version
```

Commands:

- `gard scan`: discover LSP peers on the LAN.
- `gard host`: listen for incoming tests and advertise over mDNS.
- `gard test`: measure a peer over TCP or UDP.
- `gard watch`: run repeated short measurements until `Ctrl+C`.
- `gard info`: show peer protocol and negotiated capabilities.
- `gard doctor`: diagnose local interfaces, install path and firewall hints.

Help is available globally and per command:

```sh
gard --help
gard help scan
gard help host
gard help test
gard help watch
gard help info
gard help doctor
gard test --help
```

Human output uses a rich ASCII/ANSI terminal style when stdout is interactive.
Use `--plain`, `--no-color` or `--style plain` for logs, narrow terminals or
automation. JSON and CSV never include banners, ANSI color or progress text.

### `gard scan`

```sh
gard scan --seconds 5
```

Scans `_landspeed._tcp` over mDNS and prints active peers.

### `gard host`

```sh
gard host --name lab-mac --port 7737
gard host --dynamic-port
```

Starts a host and prints its platform, service name, TCP control port and local
interfaces. Stop with `Ctrl+C`.

### `gard test`

```sh
gard test --host 192.168.1.50 --direction down --duration 10 --streams 4
gard test 192.168.1.50 --transport udp --direction up --payload 1200 --bitrate 200M
gard test 192.168.1.50 --direction bidir --bidir-mode simultaneous
```

Important options:

- `--direction up|down|bidir`
- `--streams N`
- `--duration S`
- `--warmup S`
- `--payload BYTES`
- `--transport tcp|udp`
- `--bitrate BPS` for UDP, per stream (`200M`, `1.5G`, `500k`)
- `--format human|json|csv`

UDP supports bidirectional simultaneous mode. Sequential bidirectional mode is
TCP-only.

### `gard watch`

```sh
gard watch 192.168.1.50 --interval 5
gard watch --host 192.168.1.50 --direction down --duration 2
```

Runs repeated short tests and prints a live table with throughput, latency, loss
and quality. Stop with `Ctrl+C`.

### `gard info`

```sh
gard info 192.168.1.50
```

Performs a control handshake and prints LSP version, negotiated capabilities and
available transports.

### `gard doctor`

```sh
gard doctor
gard doctor --plain
```

Prints local OS, architecture, install path, network interfaces, mDNS service
name and firewall hints.

## Output

Human output is designed for terminals:

```text
Gard test result

Session:   ...
Peer:      192.168.1.50 (macos)
Protocol:  LSP/1.2
Direction: down
Streams:   4
Duration:  10.00s

Throughput
  Mean:    943.20 Mb/s
  Peak:    981.45 Mb/s

Latency
  Ping:    min 0.42 ms, avg 0.71 ms, p95 1.12 ms, max 3.11 ms
  Jitter:  0.23 ms
  Loss:    0.00% (234 samples)
```

Interactive terminals use the richer layout by default:

```text
gard v0.2.5
  #####    ###    ####   ####
 ##       ## ##   ##  ## ##  ##
 ##  ###  #####   ####   ##  ##

> gard test 192.168.1.50 --duration 10s --streams 4

+------------------------------- RESULTS -------------------------------+
| THROUGHPUT                                                           |
| [####################........]  mean 720.09 Mb/s  peak 893.91 Mb/s   |
| LATENCY                                                              |
| Min    3.69 ms   Avg   43.33 ms   P95   73.35 ms   Max  103.90 ms   |
| QUALITY                                                              |
| Grade: degraded                                                      |
+-----------------------------------------------------------------------+
```

JSON and CSV are stable machine-readable modes. Progress logs go to stderr;
the result stays on stdout.

## Protocol

Gard implements **Landspeed Protocol 1.2**. The protocol spec lives in
[docs/LSP_PROTOCOL_v1.md](docs/LSP_PROTOCOL_v1.md).

Compatibility model:

- LSP/1.0 and LSP/1.1 peers continue to use TCP.
- UDP is negotiated only when both peers advertise `udp_data_plane`.
- Unknown JSON fields are tolerated.
- Wire shape, frame layout and capability semantics are stable in this release.

## Relationship To LandSpeed

[LandSpeed](https://github.com/marcorojasb/Landspeed) is the Apple reference
app that originated LSP. Gard is the cross-platform CLI and .NET reference core
for servers, headless machines, CI benches and interop testing.

## Repository Map

- `src/Gard.Cli`: CLI entrypoint, help, parsing and output formatting.
- `src/Gard.Core`: protocol, discovery, transport, persistence and measurement.
- `src/Gard.Core.Tests`: unit, protocol and loopback tests.
- `docs/`: protocol and benchmark documentation.
- `scripts/bench/`: gard vs iperf3 benchmark runner.
- `packaging/`: Debian and Homebrew templates.

## Development

Run the local quality loop:

```sh
dotnet restore gard.slnx
dotnet build gard.slnx -c Release --no-restore
dotnet test gard.slnx -c Release --no-build
```

Smoke test the CLI:

```sh
dotnet run --project src/Gard.Cli -c Release -- --version
dotnet run --project src/Gard.Cli -c Release -- test --csv-header
```

The CI workflow builds and tests on Ubuntu, macOS and Windows. The release
workflow creates self-contained binaries and a draft GitHub Release for `v*`
tags.

## Benchmarks

The benchmark runner compares Gard against `iperf3` and writes results under
`docs/benchmarks/runs/`:

```sh
scripts/bench/run_suite.sh --host 192.168.1.50 --tag lab-a
scripts/bench/run_suite.sh --self-host --quick --tag smoke
```

Benchmarks require `iperf3`, `jq`, `awk` and `python3`.

## Troubleshooting

- `scan` finds no peers: confirm both devices are on the same LAN/VLAN and mDNS
  is not blocked.
- `test` cannot connect: open the host TCP control port in the firewall.
- UDP results are zero or lossy: ensure local firewalls allow the ephemeral UDP
  data ports opened after the TCP handshake.
- Windows UDP pacing is timer-sensitive. Gard raises timer resolution while
  running and releases it on shutdown.
- Long high-rate UDP runs still use exact duplicate tracking in memory. A
  bounded bitmap tracker is planned for longer soak testing.

## License

- Code: [Apache License 2.0](LICENSE)
- Protocol specification and docs: [Creative Commons Attribution 4.0](LICENSE-docs)

Copyright (c) 2026 Marco Rojas.
