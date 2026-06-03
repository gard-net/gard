# Native AOT Notes

Status for `v0.2.5`: Gard release packaging attempts Native AOT first and falls
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
- Link failed with `ld: library 'ssl' not found`.
- The self-contained fallback publish passed and produced a working `gard`
  binary.

## AOT warning backlog

The main remaining AOT warnings are:

- `ControlMessageCodec` uses dynamic `System.Text.Json` node serialization and
  generic deserialization for LSP control bodies.
- `ControlStream` deserializes `ErrorBody` through reflection-based JSON APIs.
- `PreferencesStore` and `HistoryStore` use reflection-based JSON APIs and
  non-generic `JsonStringEnumConverter`.
- `Makaretu.Dns.Multicast.New` brings `Common.Logging`, which currently emits
  trim warnings during Native AOT analysis.

These warnings do not affect the self-contained release path. A future AOT hard
pass should add source-generated JSON metadata for protocol and persistence
types, then re-audit mDNS dependencies under trimming.
