#!/usr/bin/env bash
# Pack the npm ci'd scanner-vue as the release payload: <out>/scanner-vue.tar.gz and its SHA-256 checksum.
# Shared by release.yml (publishing) and scanner-package.yml (testing), so the tested archive is the published one.
set -euo pipefail
out="${1:?output directory required}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
node "$root/scanner-vue/package-distribution.mjs"
mkdir -p "$out"
tar -C "$root/scanner-vue/dist" -czf "$out/scanner-vue.tar.gz" .
(cd "$out" && sha256sum scanner-vue.tar.gz > scanner-vue.tar.gz.sha256)
