# Native AOT Notes

Status for `v0.2.6`: Gard release packaging attempts Native AOT first and falls
back to self-contained single-file binaries when AOT is not available on a
platform or runner.

Users do not need the .NET SDK or a system .NET runtime for release assets. The
SDK is only required when building from source.

## Local macOS arm64 audit

Command:

```sh
dotnet publish src/Gard.Cli/Gard.Cli.csproj -c Release -r osx-arm64 \
  -p:PublishAot=true -o ./out/osx-arm64-aot
```

Observed result on the Patagua Mac:

- C# compile reached native code generation.
- Gard-owned JSON trim/AOT warnings for the LSP codec and persistence stores
  were removed by source-generated metadata.
- Link still failed locally with `ld: library 'ssl' not found`, which appears
  to be a local macOS toolchain/OpenSSL link issue rather than C# analysis.
- The self-contained fallback publish passed and produced a working `gard`
  binary.

## Local Linux x64 run (2026-10-15)

On Arch Linux with SDK 10.0.111 and clang, `dotnet publish -r linux-x64
-p:PublishAot=true` links cleanly into a ~6 MB native `gard`. The only warning
is IL2104 from `Common.Logging` (below). The binary passed `--version`,
`doctor`, `info`, and TCP and UDP loopback tests against a native `gard host`.

CI now runs the same publish plus a smoke test (`aot-smoke` job in
`.github/workflows/ci.yml`), so AOT regressions surface on every PR instead
of only as a release-time fallback warning.

## AOT warning backlog

The remaining AOT issue is:

- `Makaretu.Dns.Multicast.New` brings `Common.Logging`, which currently emits
  trim warnings during Native AOT analysis.

This does not affect the self-contained release path. A future AOT hard pass
should re-audit mDNS dependencies under trimming or replace the mDNS layer with
a leaner AOT-friendly implementation.
