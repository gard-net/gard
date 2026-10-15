#!/bin/sh
# Actualiza Formula/gard.rb en el tap gard-net/homebrew-gard desde un release
# YA PUBLICADO, usando las credenciales git/gh de esta máquina.
#
#   packaging/homebrew/publish-tap.sh v0.3.0            # render + commit + push
#   packaging/homebrew/publish-tap.sh v0.3.0 --dry-run  # sólo muestra el diff
#
# Es el método vigente mientras no exista el secret HOMEBREW_TAP_TOKEN (sin él,
# .github/workflows/homebrew.yml sólo deja la fórmula como artefacto).
#
# GH=<ruta a gh> si el `gh` del PATH es un shim que se cuelga (p. ej. mise).
# TAP_COMMIT_TRAILER="Co-Authored-By: ..." agrega esa línea al commit del tap.
set -eu

if [ $# -lt 1 ]; then
  echo "uso: $0 vX.Y.Z [--dry-run]" >&2
  exit 2
fi
tag=$1
dry_run=${2:-}
case "$tag" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "error: '$tag' no es un tag vX.Y.Z" >&2; exit 2 ;;
esac
version=${tag#v}
here=$(cd "$(dirname "$0")" && pwd)
gh_bin=${GH:-gh}

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

if [ "$("$gh_bin" release view "$tag" --repo gard-net/gard --json isDraft --jq .isDraft)" = true ]; then
  echo "error: $tag sigue en draft; publícalo primero (las URLs de un draft no son descargables)" >&2
  exit 1
fi

"$gh_bin" release download "$tag" --repo gard-net/gard --pattern SHA256SUMS --dir "$tmp"
"$here/render-formula.sh" "$version" "$tmp/SHA256SUMS" > "$tmp/gard.rb"
if command -v ruby >/dev/null 2>&1; then ruby -c "$tmp/gard.rb" >/dev/null; fi

git clone --quiet --depth 1 https://github.com/gard-net/homebrew-gard "$tmp/tap"
mkdir -p "$tmp/tap/Formula"
cp "$tmp/gard.rb" "$tmp/tap/Formula/gard.rb"

if git -C "$tmp/tap" diff --quiet -- Formula/gard.rb; then
  echo "el tap ya tiene la fórmula de $tag; nada que hacer"
  exit 0
fi
git -C "$tmp/tap" --no-pager diff --stat -- Formula/gard.rb

if [ "$dry_run" = --dry-run ]; then
  git -C "$tmp/tap" --no-pager diff -- Formula/gard.rb
  echo "(dry-run: sin commit ni push)"
  exit 0
fi

git -C "$tmp/tap" add Formula/gard.rb
# TAP_COMMIT_TRAILER: línea opcional al final del mensaje (p. ej. Co-Authored-By).
git -C "$tmp/tap" commit --quiet -m "gard $tag" ${TAP_COMMIT_TRAILER:+-m "$TAP_COMMIT_TRAILER"}
git -C "$tmp/tap" push --quiet origin HEAD
echo "tap actualizado: brew install gard-net/gard/gard  ($tag)"
