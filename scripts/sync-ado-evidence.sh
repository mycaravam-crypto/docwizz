#!/usr/bin/env bash
# First integration increment for #278: one command for ADO evidence imports and HTML generation.
# This entry point deliberately uses existing, validated CLI contracts.
set -euo pipefail

usage() {
  echo "Usage: scripts/sync-ado-evidence.sh <project> <area-path> [history-since]" >&2
  echo "Run from the target repository root. Pass history-since to include GitWizz history." >&2
}

if (( $# < 2 || $# > 3 )) || [[ -z "$1" || -z "$2" ]]; then
  usage
  exit 2
fi

project="$1"
area="$2"
since="${3:-}"

for executable in ado docwizz jq; do
  if ! command -v "$executable" >/dev/null 2>&1; then
    echo "Missing prerequisite: $executable" >&2
    exit 2
  fi
done
if [[ -n "$since" ]] && ! command -v gw >/dev/null 2>&1; then
  echo "Missing prerequisite: gw (required for history export)" >&2
  exit 2
fi

# Keep sensitive ADO exports private and avoid leaking output into the repository.
umask 077
workdir="$(mktemp -d)"
trap 'rm -rf -- "$workdir"' EXIT

echo "Exporting work items from ADO project and area..." >&2
ado workitem export --project "$project" --area "$area" --output "$workdir/workitems.json"
jq -e --arg project "$project" --arg area "$area" '
  .schema == "ado.workitem-catalog/v1" and
  .complete == true and .project == $project and .area == $area and
  (.items | type == "array") and
  ([.items[].id] | length == (unique | length))
' "$workdir/workitems.json" >/dev/null || {
  echo "Incomplete or mismatched ADO area catalog; no imports performed" >&2
  exit 1
}

echo "Exporting ADO test-case links..." >&2
ado testplan trace-export --project "$project" --area "$area" --output "$workdir/tests.json"
jq -e --arg project "$project" --arg area "$area" '
  (.format == "ado.test-traceability/v1" or .format == "ado.test-traceability/v2") and
  .project == $project and .area == $area and
  (.requirements | type == "array") and (.testCases | type == "array")
' "$workdir/tests.json" >/dev/null || {
  echo "Invalid or mismatched ADO test snapshot; no imports performed" >&2
  exit 1
}

if [[ -n "$since" ]]; then
  echo "Exporting GitWizz history..." >&2
  gw history export --since "$since" --provider azure-devops -o "$workdir/history.json"
  jq -e '.schema == "gitwizz.history/v2"' "$workdir/history.json" >/dev/null || {
    echo "Invalid GitWizz history schema; no imports performed" >&2
    exit 1
  }
  mkdir -p "$workdir/work-items"
  # The existing historical index consumes ado.requirement-context/v1, not catalog/v1.
  while IFS= read -r id; do
    ado workitem context "$id" > "$workdir/work-items/$id.json"
    jq -e --argjson id "$id" '.schema == "ado.requirement-context/v1" and .id == $id' \
      "$workdir/work-items/$id.json" >/dev/null || {
      echo "Invalid ADO work-item context for id $id; no imports performed" >&2
      exit 1
    }
  done < <(jq -r '.items[].id' "$workdir/workitems.json")
fi

# Imports are sequential in the current DocWizz API. A future native implementation
# must atomically stage/commit all indexes before AC3 can be marked complete.
echo "Importing validated evidence..." >&2
docwizz requirements catalog-import . "$workdir/workitems.json"
docwizz tests import . "$workdir/tests.json"
if [[ -n "$since" ]]; then
  docwizz requirements import . "$workdir/history.json" "$workdir/work-items/"
fi
docwizz generate . --html
