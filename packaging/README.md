# Packaging

Templates and automation for publishing `gard` to end-user channels. Release
assets are SDK-free: Native AOT when available, with a self-contained
single-file fallback.

## Flow

1. A `vX.Y.Z` tag runs `.github/workflows/release.yml`: it checks the tag
   against `Directory.Build.props`, builds `osx-arm64`, `osx-x64`, `linux-x64`,
   `linux-arm64` and `win-x64`, packs `.tar.gz`/`.zip` and `.deb`, and creates a
   **draft** GitHub Release with a consolidated `SHA256SUMS`.
2. Publishing the draft runs `.github/workflows/homebrew.yml`, which renders
   `homebrew/gard.rb.tmpl` with the release hashes and commits it to the tap.
3. `scripts/install.sh` and `scripts/install.ps1` always resolve the latest
   published release, so they need no update.

## Homebrew

Tap: [`gard-net/homebrew-gard`](https://github.com/gard-net/homebrew-gard).
Users install with:

```sh
brew install gard-net/gard/gard
```

The formula installs the binary, bash/zsh/fish completions
(`generate_completions_from_executable`) and docs, and its `test` block checks
`--version`, `doctor --json` and the completion output.

### One-time setup: `HOMEBREW_TAP_TOKEN`

The tap lives in another repository, so the workflow needs a token that can push
there:

1. GitHub → Settings → Developer settings → Fine-grained personal access tokens
   → Generate new token.
2. Resource owner `gard-net`, repository access **Only select repositories** →
   `gard-net/homebrew-gard`.
3. Permissions → Repository → **Contents: Read and write**. Nothing else.
4. In `gard-net/gard` → Settings → Secrets and variables → Actions → New
   repository secret named `HOMEBREW_TAP_TOKEN` with that token.

Without the secret the workflow still renders the formula and uploads it as the
`homebrew-formula` artifact, with a warning, so it can be committed by hand.

To regenerate the formula manually for an already published tag:

```sh
gh release download v0.3.0 --repo gard-net/gard --pattern SHA256SUMS --dir dist
packaging/homebrew/render-formula.sh 0.3.0 dist/SHA256SUMS > Formula/gard.rb
```

or run the **Homebrew** workflow with `workflow_dispatch` and the tag.

## Debian / Ubuntu (`.deb`)

`debian/control.tmpl` is expanded with the version and architecture
(`amd64`, `arm64`) and wrapped around the Linux binary with `dpkg-deb`. The
package also ships bash, zsh and fish completions. It has no .NET dependency.

## Windows

The `win-x64` zip is the distribution artifact; `scripts/install.ps1` installs
it per user. Windows on ARM runs it under x64 emulation.
