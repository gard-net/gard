# Packaging

Templates and automation for publishing `gard` to end-user channels. Release
assets are SDK-free: Native AOT when available, with a self-contained
single-file fallback.

## Flow (current method)

1. A `vX.Y.Z` tag runs `.github/workflows/release.yml`: it checks the tag
   against `Directory.Build.props`, builds `osx-arm64`, `osx-x64`, `linux-x64`,
   `linux-arm64` and `win-x64`, packs `.tar.gz`/`.zip` and `.deb`, and creates a
   **draft** GitHub Release with a consolidated `SHA256SUMS`.
2. Publish the draft (web UI, or
   `gh release edit vX.Y.Z --draft=false --latest --notes-file notes.md`).
3. Update the Homebrew tap from a machine with push access to
   `gard-net/homebrew-gard`:

   ```sh
   packaging/homebrew/publish-tap.sh vX.Y.Z --dry-run   # review the diff
   packaging/homebrew/publish-tap.sh vX.Y.Z
   ```

   It refuses drafts, downloads `SHA256SUMS`, renders the formula, and commits and
   pushes `Formula/gard.rb` with the local git credentials. This is the method in
   use because the `HOMEBREW_TAP_TOKEN` secret is not configured;
   `homebrew.yml` still runs on publish and leaves the rendered formula as an
   artifact. If the `gh` on `PATH` hangs (a mise shim does), pass the real
   binary: `GH=/path/to/gh packaging/homebrew/publish-tap.sh vX.Y.Z`.
4. `scripts/install.sh` and `scripts/install.ps1` always resolve the latest
   published release, so they need no update. Smoke-test with
   `curl -fsSL https://raw.githubusercontent.com/gard-net/gard/main/scripts/install.sh | GARD_INSTALL_DIR=/tmp/g sh`.

## Homebrew

Tap: [`gard-net/homebrew-gard`](https://github.com/gard-net/homebrew-gard).
Users install with:

```sh
brew install gard-net/gard/gard
```

The formula installs the binary, bash/zsh/fish completions
(`generate_completions_from_executable`) and docs, and its `test` block checks
`--version`, `doctor --json` and the completion output.

### Optional: automate it with `HOMEBREW_TAP_TOKEN`

To make step 3 automatic, give the workflow a token that can push to the tap:

1. GitHub → Settings → Developer settings → Fine-grained personal access tokens
   → Generate new token.
2. Resource owner `gard-net`, repository access **Only select repositories** →
   `gard-net/homebrew-gard`.
3. Permissions → Repository → **Contents: Read and write**. Nothing else.
4. In `gard-net/gard` → Settings → Secrets and variables → Actions → New
   repository secret named `HOMEBREW_TAP_TOKEN` with that token.

With the secret in place, publishing a release updates the tap by itself (and
the workflow can be re-run for a tag with `workflow_dispatch`). Fine-grained
tokens expire (max. 1 year): renew it or the tap silently stops updating; until
then `publish-tap.sh` keeps working.

## Debian / Ubuntu (`.deb`)

`debian/control.tmpl` is expanded with the version and architecture
(`amd64`, `arm64`) and wrapped around the Linux binary with `dpkg-deb`. The
package also ships bash, zsh and fish completions. It has no .NET dependency.

## Windows

The `win-x64` zip is the distribution artifact; `scripts/install.ps1` installs
it per user. Windows on ARM runs it under x64 emulation.
