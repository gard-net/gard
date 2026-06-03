# Gard Reactivation Preflight 2026-06-03

This note records the controlled restart pass for `gard` after the Patagua
filesystem migration. It is intentionally local-first and does not include
builds, tests, installs, network tests, release scripts, commits or pushes.

## Repository State

- Path: `/Users/marcorojasbelmar/Developer/patagua/50-network-tools/landspeed/gard`
- Branch: `main`, aligned with `origin/main`
- Remote: `https://github.com/gard-net/gard.git`
- Stack: .NET 10 via `global.json`, solution `gard.slnx`
- Current code/package version observed in project files: `0.2.3`

Initial Git status:

```text
## main...origin/main
?? AGENTS.md
?? CLAUDE.md
```

The untracked `AGENTS.md` and `CLAUDE.md` are local Patagua agent contracts from
the migration. They are not cleaned, staged or committed in this pass.

## Local Residue

Ignored local outputs are present and covered by `.gitignore`:

- `.DS_Store`
- `.claude/`
- `artifacts/`
- `out/`
- `src/*/bin/`
- `src/*/obj/`

These folders are preserved for now. Their presence should not be interpreted
as product changes.

## Superficial Findings

- Local Claude branches/worktrees still reference old Desktop paths such as
  `/Users/.../Desktop/garc/.claude/worktrees/...`. This is a local hygiene risk
  and should be handled separately; do not delete or rewrite those worktrees as
  part of product reactivation.
- README status text lagged the code and changelog version; it now names
  `v0.2.3`.
- CLI numeric option parsing used to fall back to defaults on invalid values.
  It now reports invalid integers/floats instead of silently changing the
  user's request.
- CSV output now escapes cells with commas, quotes or line breaks so `--label`
  cannot corrupt machine-readable rows.
- `RunTestAsync` no longer hardcodes the local client platform as `macos`; it
  uses `DeviceIdentity.CurrentPlatform`.
- `UdpReceiverStats` still uses exact duplicate tracking with an O(N)
  `HashSet<ulong>`. That is documented in code as acceptable for the current
  reference durations and should be revisited before long-duration UDP testing.

## Deferred Verification

Run only after explicit approval for the next phase:

- `dotnet --info`
- `dotnet test gard.slnx --no-restore` if local restore state is already valid
- `dotnet restore` only if explicitly approved
- CLI smoke checks for `--help`, `--version`, invalid arguments and CSV output
- Loopback TCP/UDP and cross-device network tests only after local checks pass
