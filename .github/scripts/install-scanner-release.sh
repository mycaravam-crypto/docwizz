#!/usr/bin/env bash
# Install only a payload published for the action's immutable release commit.
set -euo pipefail
repo="${1:?action repository required}"
ref="${2:?action ref required}"
target="${3:?target directory required}"
if [[ ! "$repo" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo "::error::invalid action repository" >&2; exit 1
fi
if [[ "$ref" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  tag="$ref"
elif [[ "$ref" =~ ^[a-fA-F0-9]{40}$ ]]; then
  # The release workflow creates lightweight version tags at the merge commit.
  # Do not silently substitute 'latest', which could mismatch the pinned action.
  tag=$(git ls-remote --tags "https://github.com/$repo.git" 'refs/tags/v*' |
    awk -v sha="$ref" '$1==sha && $2 ~ /^refs\/tags\/v[0-9]+\.[0-9]+\.[0-9]+$/ {sub(/^refs\/tags\//, "", $2); print $2}' |
    sort -V | tail -n 1)
else
  echo "::error::G1 requires an immutable version tag or full commit SHA for the action (got $ref)" >&2
  exit 1
fi
if [[ -z "$tag" ]]; then
  echo "::error::no release tag found for action commit $ref" >&2; exit 1
fi
mkdir -p "$target"
temp=$(mktemp -d)
trap 'rm -rf "$temp"' EXIT
for name in scanner-vue.tar.gz scanner-vue.tar.gz.sha256; do
  curl --fail --location --silent --show-error --retry 3 \
    "https://github.com/$repo/releases/download/$tag/$name" -o "$temp/$name"
done
(cd "$temp" && sha256sum --check scanner-vue.tar.gz.sha256)
tar -xzf "$temp/scanner-vue.tar.gz" -C "$target" --no-same-owner
for file in index.mjs java.mjs node_modules/typescript/package.json \
  node_modules/@vue/compiler-sfc/package.json \
  node_modules/tree-sitter-java/tree-sitter-java.wasm; do
  [[ -f "$target/$file" ]] || { echo "::error::incomplete scanner payload: $file" >&2; exit 1; }
done
echo "Verified scanner payload from $repo release $tag"
