#!/usr/bin/env bash
set -euo pipefail
script="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/sync-ado-evidence.sh"
tmp="$(mktemp -d)"
trap 'rm -rf -- "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/project"
cat > "$tmp/bin/ado" <<'ADO'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
  "workitem export")
    while (( $# )); do
      if [[ "$1" == "--output" ]]; then
        cat > "$2" <<'JSON'
{"schema":"ado.workitem-catalog/v1","project":"Shop","area":"Shop\\Orders","complete":true,"items":[{"id":4711}]}
JSON
        exit
      fi
      shift
    done ;;
  "testplan trace-export")
    while (( $# )); do
      if [[ "$1" == "--output" ]]; then
        cat > "$2" <<'JSON'
{"format":"ado.test-traceability/v2","project":"Shop","area":"Shop\\Orders","requirements":[],"testCases":[],"plans":[],"assignments":[]}
JSON
        exit
      fi
      shift
    done ;;
  "workitem context") printf '{"schema":"ado.requirement-context/v1","id":%s}\n' "$3"; exit ;;
esac
exit 1
ADO
cat > "$tmp/bin/gw" <<'GW'
#!/usr/bin/env bash
set -euo pipefail
while (( $# )); do
  if [[ "$1" == "-o" ]]; then
    echo '{"schema":"gitwizz.history/v2"}' > "$2"
    exit
  fi
  shift
done
exit 1
GW
cat > "$tmp/bin/docwizz" <<'DOCWIZZ'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$TEST_CALLS"
DOCWIZZ
chmod +x "$tmp/bin/ado" "$tmp/bin/gw" "$tmp/bin/docwizz"
export TEST_CALLS="$tmp/calls"
export PATH="$tmp/bin:$PATH"
cd "$tmp/project"

bash "$script" Shop 'Shop\Orders' v1.0.0 >/dev/null
grep -q 'requirements catalog-import' "$TEST_CALLS"
grep -q 'tests import' "$TEST_CALLS"
grep -q 'requirements import' "$TEST_CALLS"
grep -q 'generate . --html' "$TEST_CALLS"
test "$(wc -l < "$TEST_CALLS")" -eq 4

: > "$TEST_CALLS"
bash "$script" Shop 'Shop\Orders' >/dev/null
if grep -q 'requirements import' "$TEST_CALLS"; then
  echo "History import must remain optional" >&2
  exit 1
fi
test "$(wc -l < "$TEST_CALLS")" -eq 3

: > "$TEST_CALLS"
if bash "$script" Wrong 'Shop\Orders' >/dev/null 2>&1; then
  echo "Mismatched project should fail" >&2
  exit 1
fi
test ! -s "$TEST_CALLS"
echo "ADO sync integration smoke tests passed"
