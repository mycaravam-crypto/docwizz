#!/usr/bin/env bash
# Linux baseline for full and project-scoped DocWizz scans, excluding compilation.
# Usage: bash scripts/benchmark-multi-project.sh
# Override PROJECTS=40 SOURCES_PER_PROJECT=20 RUNS=5 for additional measurements.
set -euo pipefail
cd "$(dirname "$0")/.."
root=$PWD
sandbox=$(mktemp -d)
trap 'rm -rf "$sandbox"' EXIT
projects=${PROJECTS:-30}
sources=${SOURCES_PER_PROJECT:-12}
runs=${RUNS:-3}
if (( projects < 3 || sources < 1 || runs < 1 || projects > 500 || sources > 200 || runs > 30 )); then
  echo "Expected 3<=PROJECTS<=500, 1<=SOURCES_PER_PROJECT<=200 and 1<=RUNS<=30" >&2
  exit 2
fi

dotnet build "$root/src/DocWizz" --nologo -v q -c Release >/dev/null
dll=$(dotnet msbuild "$root/src/DocWizz" -p:Configuration=Release -getProperty:TargetPath)
fixture="$sandbox/repo"
mkdir -p "$fixture"
for ((p=1; p<=projects; p++)); do
  name=$(printf 'Project%03d' "$p")
  mkdir -p "$fixture/$name"
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' > "$fixture/$name/$name.csproj"
  for ((n=1; n<=sources; n++)); do
    printf 'namespace %s; public class Sample%03d { public int Value() => %d; }\n' \
      "$name" "$n" "$n" > "$fixture/$name/Sample$n.cs"
  done
done

scan() {
  (cd "$sandbox"; dotnet "$dll" scan "$fixture" "$sandbox/model.json" >/dev/null)
}
bench() {
  local name=$1 start end ms j
  local -a samples=()
  scan # warm-up outside timed sample set
  for ((j=1; j<=runs; j++)); do
    start=$(date +%s%N)
    scan
    end=$(date +%s%N)
    ms=$(( (end-start)/1000000 ))
    samples+=("$ms")
    printf '%s,%d,%d\n' "$name" "$j" "$ms"
  done
  mapfile -t ordered < <(printf '%s\n' "${samples[@]}" | sort -n)
  printf '# %s median_ms=%d\n' "$name" "${ordered[$((runs/2))]}"
}
echo "# DotWizz multi-project benchmark (Linux; dotnet $(dotnet --version); $projects projects, $sources files/project)"
echo "mode,run,elapsed_ms"
bench full
cat > "$fixture/docwizz.yaml" <<'YAML'
profile: default
scope:
  projects:
    include: ["Project001/Project001.csproj", "Project002/Project002.csproj", "Project003/Project003.csproj"]
    references: metadata
    test_links: false
  unassigned_sources: exclude
  repository_files: include
YAML
bench scoped
echo "# Samples are platform-specific; ProjectHierarchyIndexTests separately guard asymptotic behavior."
