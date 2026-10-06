#!/usr/bin/env bash
# Specification of next-version.sh: csproj version, labels, tagged → released version ("" = no release, ERR = fails).
set -uo pipefail
cd "$(dirname "$0")"
failed=0
expect() {
  local got
  got=$(./next-version.sh "$1" "$2" "$3" 2>/dev/null) || got=ERR
  if [[ $got == "$4" ]]; then echo "ok    $1 [$2] tagged=$3 → ${4:-no release}"
  else echo "FAIL  $1 [$2] tagged=$3: expected '${4}', got '$got'"; failed=1; fi
}
# the version in the csproj isn't released yet: release it as is, whatever the label
expect 0.1.0 ""                 false 0.1.0
expect 0.3.0 "release:major"    false 0.3.0
expect 1.0.0-rc.1 ""            false 1.0.0-rc.1
expect 0.3.0 "release:none"     false ""
# already released: bump by label, patch by default
expect 0.1.0 ""                 true  0.1.1
expect 0.1.0 "bug,docs"         true  0.1.1
expect 0.1.9 "release:minor"    true  0.2.0
expect 0.4.2 "release:major"    true  1.0.0
expect 1.2.3 "ui,release:minor" true  1.3.0
expect 1.2.3 "release:none"     true  ""
# a released pre-release is finalized by any bump
expect 1.0.0-rc.1 ""              true 1.0.0
expect 1.0.0-rc.1 "release:minor" true 1.0.0
# mistakes fail loudly instead of guessing
expect 1.2.3 "release:minor,release:major" true ERR
expect 1.2.3 "release:none,release:minor"  true ERR
expect 1.2   ""                            true ERR
expect "1.2.3+abc" ""                      true ERR
# a label that merely starts with a release label is not one
expect 1.2.3 "release:minor-ish" true 1.2.4
exit $failed
