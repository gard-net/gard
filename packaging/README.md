# Packaging

Templates and notes for publishing `gard` to end-user package channels.
The release workflow (`.github/workflows/release.yml`) uses these on
every `v*` tag push.

## Debian / Ubuntu (`.deb`)

`debian/control.tmpl` is expanded with the current version and wrapped
around the `linux-x64` binary via `dpkg-deb --build` inside CI. The
resulting `gard_<version>_amd64.deb` is attached to the GitHub Release
next to the tarballs.

## Homebrew

`homebrew/gard.rb.tmpl` is the formula template. After the GitHub
Release is published, compute `sha256` of the three tarballs
(`osx-arm64`, `osx-x64`, `linux-x64`), fill in the placeholders, and
commit the result as `Formula/gard.rb` in the
`gard-net/homebrew-gard` tap repo.

## Windows

The `win-x64` zip produced by the release workflow is the distribution
artifact — no installer in v0.1. `gard.exe` is self-contained and has
no external runtime dependencies.
