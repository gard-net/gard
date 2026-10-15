#!/bin/sh
# Renderiza Formula/gard.rb desde la plantilla y un SHA256SUMS de release.
#
#   packaging/homebrew/render-formula.sh 0.3.0 dist/SHA256SUMS > gard.rb
#
# Falla si falta el hash de alguna plataforma: una fórmula con un placeholder
# sin reemplazar rompería `brew install` sólo en esa plataforma.
set -eu

if [ $# -ne 2 ]; then
  echo "uso: $0 <version sin v> <SHA256SUMS>" >&2
  exit 2
fi

version=$1
sums=$2
template="$(dirname "$0")/gard.rb.tmpl"

hash_for() {
  asset="gard-v${version}-$1.tar.gz"
  h=$(awk -v f="$asset" '$2 == f || $2 == "*"f { print $1 }' "$sums")
  if [ -z "$h" ]; then
    echo "error: falta $asset en $sums" >&2
    exit 1
  fi
  printf '%s' "$h"
}

osx_arm64=$(hash_for osx-arm64)
osx_x64=$(hash_for osx-x64)
linux_x64=$(hash_for linux-x64)
linux_arm64=$(hash_for linux-arm64)

sed \
  -e "s/__VERSION__/${version}/" \
  -e "s/__SHA256_OSX_ARM64__/${osx_arm64}/" \
  -e "s/__SHA256_OSX_X64__/${osx_x64}/" \
  -e "s/__SHA256_LINUX_X64__/${linux_x64}/" \
  -e "s/__SHA256_LINUX_ARM64__/${linux_arm64}/" \
  "$template"
