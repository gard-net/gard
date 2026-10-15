# gard

**Local network quality, measured like a product you can trust.**

Gard is an open-source CLI for measuring the real quality of a link between
devices on your LAN: throughput, latency, jitter, packet loss and RTT under
load. It is built for engineers who need more than a speed number and less than
a lab full of custom tooling.

Gard speaks the open **Landspeed Protocol (LSP/1.2)**, discovers peers over
mDNS, supports TCP and UDP data planes, ships as a single native binary with no
runtime to install, and emits clean human, JSON and CSV output.

```sh
gard host                       # on one machine
gard test 192.168.1.50          # on another
```

```text
╭─ gard ─ 192.168.1.50 ─────────────────────────────────────── ✓ excellent ─╮
│ down · tcp · 2 streams · 3.0s · LSP/1.2 · linux                            │
│                                                                            │
│ Throughput   11.47 Gb/s mean   12.46 Gb/s peak                             │
│              ▅█▇▁▄▄▇▇▆██▇▅▅                                                │
│                                                                            │
│ Latency      0.22 min  1.56 avg  3.91 p95  9.04 max ms                     │
│ Jitter       1.51 ms                                                       │
│ Loss         0.00%  of 59 probes                                           │
│ Under load   p95 12.96 ms · median 8.97 ms · 0 spikes                      │
│                                                                            │
│ ✓ Excellent · ready for demanding local workloads                          │
╰────────────────────────────────────────────────────────────────────────────╯
```

> Status: `v0.3.0`, LSP/1.2. A redesigned CLI (live progress, adaptive
> dashboard, completions, versioned JSON), pairing, and one-line installers.
> LSP wire compatibility with 0.2.x and the Apple app is unchanged.

## Install

**macOS and Linux (Homebrew)**

```sh
brew install gard-net/gard/gard
```

**macOS and Linux (script)** — detects OS/arch, verifies the SHA256 and installs
to `/usr/local/bin` if writable, otherwise `~/.local/bin` (never uses sudo):

```sh
curl -fsSL https://raw.githubusercontent.com/gard-net/gard/main/scripts/install.sh | sh
```

**Windows (PowerShell)** — installs to `%LOCALAPPDATA%\Programs\gard` and adds it
to your user PATH:

```powershell
irm https://raw.githubusercontent.com/gard-net/gard/main/scripts/install.ps1 | iex
```

Pin a version with `GARD_VERSION=0.3.0` (or `$env:GARD_VERSION`); choose the
directory with `GARD_INSTALL_DIR`. Debian/Ubuntu `.deb` packages (amd64, arm64)
and all archives are on the [releases page](https://github.com/gard-net/gard/releases).

| Platform | Assets |
| --- | --- |
| macOS Apple Silicon / Intel | `osx-arm64.tar.gz`, `osx-x64.tar.gz` |
| Linux x64 / arm64 | `linux-x64.tar.gz`, `linux-arm64.tar.gz`, `.deb` |
| Windows x64 (and ARM via emulation) | `win-x64.zip` |

Binaries are Native AOT when the platform supports it, otherwise self-contained;
each archive has a `BUILD_KIND` file saying which. Every release ships a
`SHA256SUMS` file.

### Shell completions

Homebrew and the `.deb` install them automatically. Otherwise:

```sh
gard completions bash > ~/.local/share/bash-completion/completions/gard
gard completions zsh  > "${fpath[1]}/_gard"
gard completions fish > ~/.config/fish/completions/gard.fish
gard completions powershell | Out-String | Invoke-Expression   # add to $PROFILE
```

## Why Gard

`iperf3` is excellent at raw throughput. Gard is shaped around local devices and
repeatable, product-grade diagnostics:

- Discover peers on your LAN with no central service.
- Measure throughput, latency, jitter, packet loss and RTT under load together.
- Use TCP for baseline tests or UDP for paced, loss-aware measurements.
- Watch progress live, read a dashboard that fits your terminal, or force plain
  output for logs.
- Script results as versioned JSON or CSV without scraping terminal output.
- Interoperate through the documented LSP/1.2 protocol.

## CLI

```text
gard <command> [options]
gard help <command>
```

| Command | What it does |
| --- | --- |
| `gard scan` | Discover LSP peers on the LAN (mDNS). |
| `gard host` | Listen for tests and advertise over mDNS. |
| `gard test` | Measure a peer over TCP or UDP. |
| `gard watch` | Repeated short measurements until Ctrl+C (or `--count`). |
| `gard info` | Show a peer's protocol version and capabilities. |
| `gard doctor` | Diagnose the local environment and terminal. |
| `gard completions` | Print a bash, zsh, fish or PowerShell completion script. |
| `gard version` | Show the gard and protocol versions. |

Options accept `--name value` or `--name=value`. Unknown options are an error
with a suggestion (`did you mean '--duration'?`). Measurement flags have
iperf3-style short forms:

| Short | Long | Meaning |
| --- | --- | --- |
| `-t S` | `--duration S` | Measurement seconds (default 10). |
| `-P N` | `--streams N` | Parallel streams, 1..32. |
| `-l BYTES` | `--payload BYTES` | Payload size. |
| `-b BPS` | `--bitrate BPS` | UDP target per stream (`200M`, `1.5G`). |
| `-u` | `--udp` | Shortcut for `--transport udp`. |
| `-O S` | `--warmup S` | Seconds excluded at the start. |
| `-p N` | `--port N` | TCP control port (default 7737). |
| `-J` | `--json` | JSON output. |

Global options: `--plain` (plain lines), `--no-color` (also `NO_COLOR=1`),
`--ascii` (no Unicode glyphs), `--style rich|plain`. Gard detects terminal width,
Unicode support (UTF-8 locale; Windows consoles are switched to UTF-8), color
support (VT sequences on Windows 10+, `TERM=dumb`, `CI`) and whether stdout is a
TTY, and degrades gracefully. `FORCE_COLOR=1` keeps color when piping.

### `gard scan`

```sh
gard scan            # 5 s
gard scan -t 3 --json
```

### `gard host`

```sh
gard host --name lab-mac --port 7737
gard host --dynamic-port
gard host --pair                 # require a 6-digit code, shown in the banner
gard host --pair-code 482913     # require a fixed code
```

Prints its name, port, pairing state and LAN addresses (with a ready-to-copy
`gard test` command), then one line per session. Stop with `Ctrl+C`.

Without `--pair` any peer on the LAN can request a test. With `--pair` the host
requires the LSP pairing code (spec §7) on every session. Clients pass it with
`--pair-code`, or are prompted once when running on a terminal:

```sh
gard test 192.168.1.50 --pair-code 482913
gard info 192.168.1.50 --pair-code 482913
```

Wrong codes are rate limited to 5 failures per minute per source (IPv6 grouped
by /64) plus 30 per minute overall, and codes are compared in constant time.
Gard does not remember paired peers: LSP/1 has no stable peer identity
(`device_name` and IP can be spoofed), so a remembered pairing could not be
verified.

Either way, the host bounds what a peer can ask for:

- At most 4 concurrent sessions; extra connections are closed on accept.
- `hello` must arrive within 10 s, and the whole handshake must finish within
  10 s (2 min with pairing, to leave time to type the code).
- `test_start` is validated before any socket is opened: `streams` 1..32,
  `duration_s` up to 3600, `warmup_s`/`gap_s` up to 60, TCP payload up to 4 MiB,
  UDP payload 64..65507. Out-of-range payloads get LSP error `3004`; UDP without
  a negotiated `udp_data_plane` gets `3003`; anything else gets a negative
  `test_start_ack` with a reason.
- A session ends as soon as the client disconnects.

### `gard test`

```sh
gard test 192.168.1.50
gard test 192.168.1.50 -t 10 -P 4 --direction bidir
gard test 192.168.1.50 -u -b 200M -l 1200 --direction up
gard test 192.168.1.50 --json | jq .throughput.mean_bps
gard test 192.168.1.50 --csv --label nightly
```

On a TTY, progress is drawn live on stderr and cleared when the result prints.
`--quiet` disables it. Run `gard help test` for every option.

UDP supports bidirectional simultaneous mode with `--streams 2` or more: per
LSP §12.6, even streams carry down and odd streams carry up, so `--streams 4`
means two streams each way. Sequential bidirectional mode is TCP-only.

The UDP host only accepts the `HELLO_UDP` and data from the IP of the control
connection, and after `HELLO_UDP` only from the learned source port. The client
resends `HELLO_UDP` a few times with backoff, so one lost datagram on Wi-Fi does
not fail the test. IPv6 peers are supported.

### `gard watch`

```sh
gard watch 192.168.1.50                 # every 5 s, 3 s samples
gard watch 192.168.1.50 -i 10 -n 6      # six samples, then exit
gard watch 192.168.1.50 --json          # NDJSON, one test report per line
```

Prints one table row per sample. With `--count`, exits `1` if every sample failed.

### `gard info` and `gard doctor`

```sh
gard info 192.168.1.50          # LSP version, capabilities, transports, clock RTT
gard doctor                     # version, platform, binary, terminal, interfaces
gard doctor --json
```

## Output

- **human**: dashboard on a TTY (adapts to terminal width, falls back to ASCII);
  stable `Key: value` lines when redirected or with `--plain`.
- **JSON** (`--json`): snake_case and versioned with a `schema` field
  (`gard.test/1`, `gard.scan/1`, `gard.info/1`, `gard.doctor/1`). Indented on a
  TTY, compact when piped. New fields may be added; renames or removals bump the
  schema version.
- **CSV** (`--csv`, `--csv-header`): one row per test, stable columns.

Progress, warnings and errors always go to stderr; stdout carries only the result.

```json
{
  "schema": "gard.test/1",
  "gard_version": "0.3.0",
  "lsp_version": "1.2",
  "peer": { "name": "192.168.1.50", "platform": "linux" },
  "test": { "direction": "down", "transport": "tcp", "streams": 4, "duration_s": 10.0 },
  "throughput": { "mean_bps": 943200000, "peak_bps": 981450000, "per_stream_bps": [] },
  "latency": { "min_ms": 0.42, "avg_ms": 0.71, "p95_ms": 1.12, "max_ms": 3.11, "jitter_ms": 0.23, "loss_pct": 0.0, "samples": 234 },
  "quality": "excellent"
}
```

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. |
| `1` | Runtime failure: connection refused, timeout, peer rejected the test, protocol error. |
| `2` | Usage error: unknown command, invalid or out-of-range option. |
| `130` | Interrupted with Ctrl+C. |

## Build From Source

Requirements: .NET SDK 10.0.x and Git.

```sh
git clone https://github.com/gard-net/gard.git
cd gard
dotnet build gard.slnx -c Release
dotnet run --project src/Gard.Cli -c Release -- --help
```

Native AOT binary (needs clang on Linux, Xcode on macOS):

```sh
dotnet publish src/Gard.Cli -c Release -r linux-x64 -p:PublishAot=true -o ./out
```

Common runtime IDs: `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `win-x64`.
See [docs/native-aot-notes.md](docs/native-aot-notes.md).

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

- `src/Gard.Cli/App`: entry point, command spec (help, validation and
  completions all come from `CliSpec`), options and errors.
- `src/Gard.Cli/Commands`: one file per command.
- `src/Gard.Cli/Ui`: terminal capabilities, glyphs, tables, live progress.
- `src/Gard.Cli/Output`: dashboard, JSON reports, CSV, quality grading.
- `src/Gard.Core`: protocol, discovery, transport, persistence and measurement.
- `src/Gard.Core.Tests`: unit, protocol, CLI and loopback end-to-end tests.
- `scripts/install.sh`, `scripts/install.ps1`: one-line installers.
- `scripts/bench/`: gard vs iperf3 benchmark runner.
- `packaging/`: Debian control and Homebrew formula templates.
- `docs/`: protocol and benchmark documentation.

## Development

```sh
dotnet build gard.slnx -c Release
dotnet test gard.slnx -c Release --no-build
dotnet run --project src/Gard.Cli -c Release -- doctor
```

CI builds and tests on Ubuntu, macOS and Windows, publishes a Native AOT binary
and smoke-tests it (including `scripts/install.sh` against a local mirror), and
checks that the Homebrew formula renders and `install.ps1` parses.

### Releasing

1. Bump `<Version>` in `Directory.Build.props` and move `[Unreleased]` in
   `CHANGELOG.md` to the new version.
2. Tag and push: `git tag v0.3.0 && git push origin v0.3.0`. `release.yml`
   verifies the tag matches the version, builds every platform and creates a
   **draft** release with archives, `.deb` packages and `SHA256SUMS`.
3. Review and publish the draft. `homebrew.yml` then renders the formula and
   commits it to `gard-net/homebrew-gard` (needs the `HOMEBREW_TAP_TOKEN`
   secret; see `packaging/README.md`). The install scripts pick up the new
   release automatically.

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
- `host requires pairing`: the host was started with `--pair`; pass the code
  from its banner with `--pair-code`. `too many failed attempts`: wait a minute.
- `host rechazó test_start` / `code=3004`: the requested parameters exceed the
  host limits listed under `gard host`. The CLI applies the same limits locally.
- Windows UDP pacing is timer-sensitive. Gard raises timer resolution while
  running and releases it on shutdown.
- UDP duplicate detection uses a fixed 64K-sequence sliding window (8 KiB per
  stream), so long high-rate runs use constant memory. A packet arriving more
  than 64K sequences late counts as reordered; a duplicate that late cannot be
  detected.

## License

- Code: [Apache License 2.0](LICENSE)
- Protocol specification and docs: [Creative Commons Attribution 4.0](LICENSE-docs)

Copyright (c) 2026 Marco Rojas.
