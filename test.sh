#!/usr/bin/env bash
# Smoke check: scan the fixture, assert file counts and the expected code graph.
set -euo pipefail
cd "$(dirname "$0")"
model=$(mktemp)
out=$(dotnet run --project src/DocWizz -- scan fixture "$model")
echo "$out"
grep -q "Files  13" <<<"$out"  # tests/ excluded

python3 - "$model" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
edges = {(e["kind"], e["from"], e["to"]) for e in m["edges"]}
nodes = {n["id"]: n for n in m["nodes"]}
A, D, I, API = "cs:Fixture.Application.", "cs:Fixture.Domain.", "cs:Fixture.Infrastructure.", "cs:Fixture.Api."
create = "CreateAsync(string, int, string, string, string, bool)"
for e in [
    ("injects", API + "MaterialController", A + "IMaterialService"),
    ("calls", API + "MaterialController.Create(string, int, string, string, string, bool)", A + "IMaterialService." + create),
    ("implements", A + "MaterialService", A + "IMaterialService"),
    ("injects", A + "MaterialService", D + "IMaterialRepository"),
    ("injects", A + "MaterialService", A + "IEventPublisher"),
    ("calls", A + "MaterialService." + create, A + "IEventPublisher.PublishAsync(string, int)"),
    ("registers", A + "IMaterialService", A + "MaterialService"),
    ("dbset", I + "AppDbContext", D + "Material"),
    ("calls", D + "Material.Save(Fixture.Infrastructure.SqlMaterialRepository)", I + "SqlMaterialRepository.AddAsync(Fixture.Domain.Material)"),
]:
    assert e in edges, f"missing edge {e}"
assert ("injects", D + "Material", D + "Material") not in edges, "record copy ctor leaked as injection"
assert "controller" in nodes[API + "MaterialController"]["tags"]
assert nodes[A + "MaterialService." + create]["complexity"] >= 8
assert nodes[A + "IMaterialService.GetAsync(int)"].get("doc")
assert "cs:endpoint:GET /health" in nodes
assert "cs:endpoint:PATCH /api/materials/{id}" in nodes, "MapMethods endpoint"
# minimal API: comment above documents it; handler params are injections
assert "Places an order" in nodes["cs:endpoint:POST /orders"]["doc"]
assert ("injects", "cs:endpoint:POST /orders", I + "AppDbContext") in edges
assert not any("Tests" in n["id"] for n in nodes.values()), "tests/ not excluded"

# Frontend → backend chain: component → store → api client → HTTP → controller endpoint
F = "frontend/src/"
for e in [
    ("calls", "vue:" + F + "components/MaterialForm.vue", "ts:" + F + "stores/materialStore.ts#useMaterialStore"),
    ("calls", "ts:" + F + "stores/materialStore.ts#useMaterialStore", "ts:" + F + "api/materialApi.ts#createMaterial"),
    ("http", "ts:" + F + "api/materialApi.ts#createMaterial", API + "MaterialController.Create(string, int, string, string, string, bool)"),
    ("http", "ts:" + F + "api/materialApi.ts#getMaterial", API + "MaterialController.Get(int)"),
    ("renders", "vue:" + F + "components/MaterialTable.vue", "vue:" + F + "components/MaterialForm.vue"),
    ("routes", "route:/materials", "vue:" + F + "components/MaterialTable.vue"),
]:
    assert e in edges, f"missing edge {e}"
form = nodes["vue:" + F + "components/MaterialForm.vue"]
assert form["params"] == 1 and "Form for requesting" in form["doc"] and "emits" in form["tags"]
assert nodes["ts:" + F + "stores/materialStore.ts#useMaterialStore"]["kind"] == "store"
PY

# Documentation analysis: planted undocumented code is critical, trivial code is ignored.
set +e; report=$(dotnet run --project src/DocWizz -- check fixture); code=$?; set -e
echo "$report"
[ "$code" -eq 1 ] || { echo "check should fail on fixture"; exit 1; }
critical=$(sed -n '/^Critical/,/^Warnings/p' <<<"$report")
grep -q "MaterialService.CreateAsync" <<<"$critical"
grep -q "MaterialController.Create(" <<<"$critical"
grep -q "side effects: db, event" <<<"$critical"
if grep -q "\.Add(int, int)" <<<"$report"; then echo "trivial Add flagged"; exit 1; fi
# interface <summary> covers the implementation → only param missing
grep -A1 "  Fixture.Application.MaterialService.CreateAsync" <<<"$report" | grep -q "partial, missing: param"
if grep -q "MaterialServiceTests" <<<"$report"; then echo "test code analyzed"; exit 1; fi

# Architecture: planted violations and the cycle they cause
arch=$(sed -n '/^Architecture/,$p' <<<"$report")
grep -A1 "ARCH-001  domain → infrastructure" <<<"$arch" | grep -q "Domain/Material.cs → backend/Infrastructure/SqlMaterialRepository.cs"
grep -A1 "ARCH-002  ui → http" <<<"$arch" | grep -q "MaterialTable.vue → GET /api/materials"
grep -q "ARCH-003  cycle: backend/Domain ↔ backend/Infrastructure" <<<"$arch"
grep -q "Architecture (2 violations, 1 cycles)" <<<"$arch"
echo PASS
