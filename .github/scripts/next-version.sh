#!/usr/bin/env bash
# The version a merged pull request releases, from the csproj version, the PR's labels and whether that version is
# already tagged. Prints the version, or nothing when there is nothing to release. Used by .github/workflows/release.yml;
# next-version.test.sh is its specification.
#
#   next-version.sh <csproj version> <labels, comma-separated> <tagged: true|false>
#
# - release:none: no release (an untagged version waits for the next merge without it)
# - not tagged yet (the first release, or a version set by hand): release it unchanged
# - release:major / release:minor / no label: bump major / minor / patch
# - a tagged pre-release (1.0.0-rc.1) is finalized by any bump (1.0.0)
set -euo pipefail

current=$1 labels=",$2," tagged=$3

if [[ ! $current =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "not a MAJOR.MINOR.PATCH version: $current" >&2
  exit 1
fi
major=${BASH_REMATCH[1]} minor=${BASH_REMATCH[2]} patch=${BASH_REMATCH[3]} pre=${BASH_REMATCH[4]}

bumps=()
for l in major minor none; do [[ $labels == *",release:$l,"* ]] && bumps+=("$l"); done
if (( ${#bumps[@]} > 1 )); then
  echo "conflicting release labels: ${bumps[*]/#/release:}; keep one" >&2
  exit 1
fi
bump=${bumps[0]:-patch}

if [[ $bump == none ]]; then exit 0; fi
if [[ $tagged != true ]]; then echo "$current"; exit 0; fi
case $pre:$bump in
  -*) echo "$major.$minor.$patch" ;;
  :major) echo "$((major + 1)).0.0" ;;
  :minor) echo "$major.$((minor + 1)).0" ;;
  :patch) echo "$major.$minor.$((patch + 1))" ;;
esac
