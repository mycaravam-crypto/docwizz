#!/usr/bin/env bash
# Install the scanner payload published for the action's immutable release, verified by its SHA-256 checksum.
# With no repository (a local `uses: ./`, e.g. this repo's own docwizz workflow) the source checkout is packed instead.
# DOCWIZZ_SCANNER_RELEASE_BASE overrides the download location (scanner-package.yml tests with a file:// URL).
set -euo pipefail
repo="${1-}"
ref="${2-}"
target="${3:?target directory required}"
temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT

if [[ -z "$repo" ]]; then
  root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
  npm ci --prefix "$root/scanner-vue" --silent
  bash "$root/.github/scripts/pack-scanner-release.sh" "$temp"
  source="local checkout"
else
  if [[ ! "$repo" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
    echo "::error::invalid action repository: $repo" >&2; exit 1
  fi
  if [[ "$ref" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    tag="$ref"
  elif [[ "$ref" =~ ^[a-fA-F0-9]{40}$ ]]; then
    # The release workflow creates lightweight version tags at the merge commit. Never substitute 'latest', which could
    # mismatch the pinned action.
    sha="${ref,,}"
    tag=$(git ls-remote --tags "https://github.com/$repo.git" 'refs/tags/v*' |
      awk -v sha="$sha" '$1==sha && $2 ~ /^refs\/tags\/v[0-9]+\.[0-9]+\.[0-9]+$/ {sub(/^refs\/tags\//, "", $2); print $2}' |
      sort -V | tail -n 1)
    [[ -n "$tag" ]] || { echo "::error::no release tag points at action commit $ref" >&2; exit 1; }
  else
    echo "::error::pin the action to a release tag (vX.Y.Z) or a release commit SHA, not '$ref'" >&2; exit 1
  fi
  base="${DOCWIZZ_SCANNER_RELEASE_BASE:-https://github.com/$repo/releases/download/$tag}"
  for name in scanner-vue.tar.gz scanner-vue.tar.gz.sha256; do
    curl --fail --location --silent --show-error --retry 3 "$base/$name" -o "$temp/$name"
  done
  (cd "$temp" && sha256sum --check --strict --quiet scanner-vue.tar.gz.sha256)
  source="$repo release $tag"
fi

mkdir -p "$target"
tar -xzf "$temp/scanner-vue.tar.gz" -C "$target" --no-same-owner
for file in index.mjs java.mjs node_modules/typescript/package.json node_modules/@vue/compiler-sfc/package.json \
  node_modules/web-tree-sitter/package.json node_modules/tree-sitter-java/tree-sitter-java.wasm; do
  [[ -f "$target/$file" ]] || { echo "::error::incomplete scanner payload: $file" >&2; exit 1; }
done
echo "Installed scanner payload from $source"
