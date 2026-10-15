#!/bin/sh
# Instalador de gard para macOS y Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/gard-net/gard/main/scripts/install.sh | sh
#
# Variables opcionales:
#   GARD_VERSION=0.3.0        versión a instalar (default: último release)
#   GARD_INSTALL_DIR=~/bin    destino (default: /usr/local/bin si es escribible,
#                             si no ~/.local/bin; nunca usa sudo por su cuenta)
#   GARD_DOWNLOAD_BASE=<url>  mirror de los assets (<url>/<tag>/<asset>); requiere
#                             GARD_VERSION
#
# Baja el tarball del release de GitHub, verifica su SHA256 contra SHA256SUMS
# y copia el binario. No requiere .NET.
set -eu

REPO="gard-net/gard"

say() { printf '%s\n' "$*"; }
fail() { printf 'gard-install: %s\n' "$*" >&2; exit 1; }
has() { command -v "$1" >/dev/null 2>&1; }

download() { # url destino
  if has curl; then
    curl -fsSL --retry 3 -o "$2" "$1" || fail "download failed: $1"
  elif has wget; then
    wget -q -O "$2" "$1" || fail "download failed: $1"
  else
    fail "curl or wget is required"
  fi
}

latest_tag() {
  # /releases/latest redirige a /releases/tag/vX.Y.Z: sin API ni rate limit.
  if has curl; then
    url=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest") \
      || fail "could not look up the latest release"
  else
    url=$(wget -q --max-redirect=5 -S -O /dev/null "https://github.com/$REPO/releases/latest" 2>&1 \
      | awk '/^  Location: /{loc=$2} END{print loc}')
  fi
  tag=${url##*/}
  case "$tag" in
    v[0-9]*) printf '%s' "$tag" ;;
    *) fail "no published releases found at github.com/$REPO" ;;
  esac
}

sha256_of() {
  if has sha256sum; then sha256sum "$1" | awk '{print $1}'
  elif has shasum; then shasum -a 256 "$1" | awk '{print $1}'
  else fail "sha256sum or shasum is required to verify the download"
  fi
}

case "$(uname -s)" in
  Darwin) os=osx ;;
  Linux) os=linux ;;
  MINGW* | MSYS* | CYGWIN*) fail "on Windows use: irm https://raw.githubusercontent.com/$REPO/main/scripts/install.ps1 | iex" ;;
  *) fail "unsupported OS: $(uname -s)" ;;
esac

case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) fail "unsupported architecture: $(uname -m)" ;;
esac

# Rosetta: una terminal x64 en un Mac Apple Silicon reporta x86_64; el binario
# nativo arm64 es lo correcto.
if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then
  arch=arm64
fi

if [ -n "${GARD_VERSION:-}" ]; then
  tag="v${GARD_VERSION#v}"
elif [ -n "${GARD_DOWNLOAD_BASE:-}" ]; then
  fail "GARD_DOWNLOAD_BASE requires GARD_VERSION"
else
  tag=$(latest_tag)
fi

asset="gard-${tag}-${os}-${arch}.tar.gz"
base="${GARD_DOWNLOAD_BASE:-https://github.com/$REPO/releases/download}/$tag"

tmp=$(mktemp -d 2>/dev/null || mktemp -d -t gard)
trap 'rm -rf "$tmp"' EXIT INT TERM

say "gard: downloading $asset"
download "$base/$asset" "$tmp/$asset"
download "$base/SHA256SUMS" "$tmp/SHA256SUMS"

expected=$(awk -v f="$asset" '$2 == f || $2 == "*"f { print $1 }' "$tmp/SHA256SUMS")
[ -n "$expected" ] || fail "$asset is not listed in SHA256SUMS for $tag"
actual=$(sha256_of "$tmp/$asset")
[ "$expected" = "$actual" ] || fail "SHA256 mismatch for $asset (expected $expected, got $actual)"

tar -xzf "$tmp/$asset" -C "$tmp"
bin="$tmp/gard-${tag}-${os}-${arch}/gard"
[ -f "$bin" ] || fail "archive does not contain the gard binary"

if [ -n "${GARD_INSTALL_DIR:-}" ]; then
  dir=$GARD_INSTALL_DIR
elif [ -d /usr/local/bin ] && [ -w /usr/local/bin ]; then
  dir=/usr/local/bin
else
  dir="$HOME/.local/bin"
fi
mkdir -p "$dir" || fail "cannot create $dir"
[ -w "$dir" ] || fail "$dir is not writable; set GARD_INSTALL_DIR=<dir> or run with permissions"

# Copia atómica: un gard en uso no queda a medio escribir.
cp "$bin" "$dir/.gard.tmp.$$"
chmod 0755 "$dir/.gard.tmp.$$"
mv -f "$dir/.gard.tmp.$$" "$dir/gard"

say "gard: installed to $dir/gard"
"$dir/gard" --version || fail "the installed binary does not start"

case ":$PATH:" in
  *":$dir:"*) ;;
  *)
    say ""
    say "  $dir is not on your PATH. Add this to your shell profile:"
    say "    export PATH=\"$dir:\$PATH\""
    ;;
esac
say ""
say "  Completions: gard completions bash|zsh|fish    Get started: gard help"
