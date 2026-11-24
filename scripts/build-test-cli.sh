#!/usr/bin/env bash
# The docwizz CLI that process-level tests run (test.sh, journey tests via DOCWIZZ_DLL, the bench gates): Release and
# ReadyToRun, so each of their several hundred short-lived processes skips most JIT work (about half of a small scan,
# most of it Roslyn). Prints the DLL's path. Same code as a plain build; only how it is compiled differs.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/src/DocWizz/bin/test-cli
dotnet publish "$root/src/DocWizz" -c Release --use-current-runtime --self-contained false -p:PublishReadyToRun=true \
  -o "$out" --nologo -v q >&2
echo "$out/DocWizz.dll"
