#!/usr/bin/env bash
# Smoke check: scan the fixture, assert file counts and the expected code graph.
set -euo pipefail
cd "$(dirname "$0")"
model=$(mktemp)
out=$(dotnet run --project src/DocWizz -- scan fixture "$model")
echo "$out"
grep -q "Files  17" <<<"$out"  # tests/ excluded

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
    ("persists", I + "AppDbContext", D + "Material"),
    ("calls", D + "Material.Save(Fixture.Infrastructure.SqlMaterialRepository)", I + "SqlMaterialRepository.AddAsync(Fixture.Domain.Material)"),
]:
    assert e in edges, f"missing edge {e}"
assert ("creates", A + "MaterialService." + create, D + "Material") in edges
roles = lambda i: nodes[i].get("tags") or []
assert "entity" in roles(D + "Material") and "service" in roles(A + "MaterialService") and "repository" in roles(I + "SqlMaterialRepository")
assert "background-service" in roles(I + "MaterialCleanup") and "middleware" in roles(I + "TimingMiddleware") and "options" in roles(I + "MaterialOptions")
assert nodes[I + "MaterialChanged"]["kind"] == "delegate" and nodes[I + "MaterialChanged"]["parameters"] == ["id: int"]
assert {n.get("language") for n in nodes.values() if n["kind"] not in ("external", "config")} == {"csharp", "vue", "typescript", "msbuild", "npm"}, {n.get("language") for n in nodes.values()}
assert nodes["vue:frontend/src/components/MaterialForm.vue"]["language"] == "vue"
assert ("injects", D + "Material", D + "Material") not in edges, "record copy ctor leaked as injection"
assert "controller" in nodes[API + "MaterialController"]["tags"]
assert nodes[A + "MaterialService." + create]["complexity"] >= 8
assert nodes[A + "IMaterialService.GetAsync(int)"].get("doc")
assert "cs:endpoint:GET /health" in nodes
assert "cs:endpoint:PATCH /api/materials/{id}" in nodes, "MapMethods endpoint"
# minimal API: comment above documents it; handler params are injections
assert "Places an order" in nodes["cs:endpoint:POST /orders"]["doc"]
assert ("injects", "cs:endpoint:POST /orders", I + "AppDbContext") in edges
assert not any("Tests" in n["id"] for n in nodes.values()), "test code in the model"
assert ("tests", "cs:Fixture.Tests.MaterialServiceTests.CreateAsync_Creates(Fixture.Application.MaterialService)", A + "MaterialService." + create) in edges

# Signatures and the API model
create_svc = nodes[A + "MaterialService." + create]
assert create_svc["returns"] == "int" and create_svc["throws"] == ["ArgumentException"], create_svc
assert "urgent: bool" in create_svc["parameters"]
rename = nodes[API + "MaterialController.Rename(int, Fixture.Api.RenameMaterialRequest)"]
assert rename["route"] == "api/materials/{id}" and "authorize" in rename["tags"] and rename["returns"] == "Material", rename
assert "[body] request: RenameMaterialRequest" in rename["parameters"]
assert nodes[API + "MaterialController.Get(int)"]["returns"] == "Material?"
grouped = nodes["cs:endpoint:DELETE /admin/cache"]  # MapGroup prefix + group-level RequireAuthorization
assert "authorize" in grouped["tags"], grouped
# C# events, EF, projects and packages
assert ("publishes", A + "MaterialService." + create, A + "MaterialService.Created") in edges
assert ("subscribes", A + "MaterialNotifier.Attach(Fixture.Application.MaterialService)", A + "MaterialService.Created") in edges
assert nodes["proj:backend/Fixture.csproj"]["kind"] == "project"
deps = {(e["from"], e["to"]) for e in m["edges"] if e["kind"] == "depends-on"}
assert ("proj:backend/Fixture.csproj", "pkg:nuget:Microsoft.EntityFrameworkCore.SqlServer") in deps
assert ("proj:frontend/package.json", "pkg:npm:pinia") in deps

# Frontend → backend chain: component → store → api client → HTTP → controller endpoint
F = "frontend/src/"
for e in [
    ("calls", "vue:" + F + "components/MaterialForm.vue", "ts:" + F + "stores/materialStore.ts#useMaterialStore"),
    ("calls", "ts:" + F + "stores/materialStore.ts#useMaterialStore", "ts:" + F + "api/materialApi.ts#createMaterial"),
    ("http", "ts:" + F + "api/materialApi.ts#createMaterial", API + "MaterialController.Create(string, int, string, string, string, bool)"),
    ("http", "ts:" + F + "api/materialApi.ts#getMaterial", API + "MaterialController.Get(int)"),
    ("renders", "vue:" + F + "components/MaterialTable.vue", "vue:" + F + "components/MaterialForm.vue"),
    ("routes-to", "route:/materials", "vue:" + F + "components/MaterialTable.vue"),
]:
    assert e in edges, f"missing edge {e}"
form = nodes["vue:" + F + "components/MaterialForm.vue"]
assert form["params"] == 1 and "Form for requesting" in form["doc"] and "emits" in form["tags"]
assert form["parameters"] == ["defaultQuantity: number"] and form["events"] == ["created"], form
assert form["state"] == ["name", "valid (computed)", "watch name"], form
sub = [e for e in m["edges"] if e["kind"] == "subscribes" and e["from"].endswith("MaterialTable.vue")]
assert sub == [{"from": "vue:" + F + "components/MaterialTable.vue", "to": "vue:" + F + "components/MaterialForm.vue", "kind": "subscribes", "label": "created"}], sub
assert nodes["ts:" + F + "stores/materialStore.ts#useMaterialStore"]["kind"] == "store"

assert ("accesses", I + "SqlMaterialRepository.AddAsync(Fixture.Domain.Material)", I + "AppDbContext") in edges  # primary-ctor dependency used
assert not any(e[0] == "accesses" and e[2] == D + "Material" for e in edges), "member of another object counted as own dependency"

# External systems: detected from calls, inferred from packages, attributed to the code (or project) that uses them
ext = {n["id"]: n["tags"] for n in nodes.values() if n["kind"] == "external"}
assert ext == {"ext:http:erp.example.com": ["http-api", "detected"], "ext:redis": ["cache", "detected"],
               "ext:http:rates.example.org": ["http-api", "detected"], "ext:sqlserver": ["database", "inferred"],
               "ext:oidc": ["identity", "inferred"], "ext:http:WarehouseClient": ["http-api", "detected"]}, ext  # relative /api/... calls are not external
connects = {(e["from"], e["to"], e.get("label")) for e in m["edges"] if e["kind"] == "connects"}
for c in [(I + "ErpClient", "ext:http:erp.example.com", "detected"),  # AddHttpClient<ErpClient>(BaseAddress = ...)
          ("ts:" + F + "api/materialApi.ts#getRates", "ext:http:rates.example.org", "detected"),
          ("proj:backend/Fixture.csproj", "ext:redis", "detected"),  # top-level statement → its project
          (I + "AppDbContext", "ext:sqlserver", "inferred")]:
    assert c in connects, c

# Configuration: keys from appsettings*.json (per environment, no values), read/bound by code
url = nodes["config:warehouse:baseurl"]
assert url["file"] == "backend/appsettings.json" and url["line"] == 5, url
assert set(url["tags"]) == {"env:default@backend/appsettings.json", "url:default=warehouse.example.net",
                            "env:Development@backend/appsettings.Development.json", "url:Development=localhost"}, url
assert nodes["config:logging:loglevel:default"]["line"] == 2  # nested keys on one line
assert not any("not-a-real-secret" in json.dumps(n) for n in nodes.values()), "config value leaked into the model"
reads = {(e["kind"], e["from"], e["to"]) for e in m["edges"] if e["kind"] in ("reads", "binds")}
assert reads == {("reads", I + "WarehouseClient", "config:warehouse:baseurl"),  # AddHttpClient<WarehouseClient>(.. config["Warehouse:BaseUrl"])
                 ("binds", I + "MaterialOptions", "config:materials"),        # Configure<MaterialOptions>(GetSection("Materials"))
                 ("reads", "proj:backend/Fixture.csproj", "config:materials:beta"),
                 ("reads", "proj:backend/Fixture.csproj", "config:audit_endpoint")}, reads  # GetEnvironmentVariable("MATERIALS__BETA")
assert nodes["ext:http:WarehouseClient"]["name"] == "warehouse.example.net"  # typed client named by its configured URL
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

# Documentation model: section provenance, sources, profiles, JSON
dw() { dotnet run --project src/DocWizz -- "$@"; }
dw analyze fixture --format json 2>/dev/null > "$model"
dw analyze fixture --format json --profile software 2>/dev/null > "$model.software"
dw analyze fixture --format json --profile api 2>/dev/null > "$model.api"
dw analyze fixture --format json --profile architecture 2>/dev/null > "$model.arch-profile"
python3 - "$model" <<'PY'
import json, sys
def items(f): return {i["id"]: i for i in json.load(open(f))["documentation"]["items"]}
A, API = "cs:Fixture.Application.", "cs:Fixture.Api."
create = A + "MaterialService.CreateAsync(string, int, string, string, string, bool)"
d = items(sys.argv[1])
c = d[create]
assert c["sections"]["summary"] == {"origin": "written", "text": "Creates a material request."}, c
assert c["sections"]["side_effects"] == {"origin": "inferred", "text": "db, event"}, c
assert c["sections"]["dependencies"]["origin"] == "fact" and "IMaterialRepository" in c["sections"]["dependencies"]["text"]
assert A + "IMaterialService.CreateAsync(string, int, string, string, string, bool)" in c["sources"], "interface doc not traced"
assert c["missing"] == ["param"] and c["tested"]
assert c["evidence"][0] == "backend/Application/MaterialService.cs:13-32", c["evidence"]
assert any(e.startswith("backend/Application/IMaterialService.cs:") for e in c["evidence"]), c["evidence"]
sw = items(sys.argv[1] + ".software")
assert sw[create]["missing"] == ["param", "returns", "exception"], sw[create]["missing"]
api = items(sys.argv[1] + ".api")
rename = api[API + "MaterialController.Rename(int, Fixture.Api.RenameMaterialRequest)"]
assert rename["sections"]["input"]["text"] == "id: int, [body] request: RenameMaterialRequest"
assert rename["sections"]["authorization"] == {"origin": "fact", "text": "required"}
assert json.load(open(sys.argv[1] + ".api"))["documentation"]["profile"] == "api"
form = items(sys.argv[1] + ".arch-profile")["vue:frontend/src/components/MaterialForm.vue"]
assert form["sections"]["state"] == {"origin": "fact", "text": "name, valid (computed), watch name"}, form
PY
if dw analyze fixture --profile nope >/dev/null 2>&1; then echo "unknown profile accepted"; exit 1; fi
# Organisational profile from a file; init writes a starter config once
team=$(mktemp -d)
cat > "$team/team.yaml" <<'YAML'
patterns:
  repo: { match: { tag: repository }, level: high, sections: [summary, remarks] }
YAML
dw analyze fixture --format json --profile "$team/team.yaml" 2>/dev/null > "$team/out.json"
python3 -c "import json,sys; i={i['id']:i for i in json.load(open(sys.argv[1]))['documentation']['items']}['cs:Fixture.Infrastructure.SqlMaterialRepository']; assert i['pattern']=='repo' and i['missing']==['summary','remarks'], i" "$team/out.json"
dw init "$team" | grep -q "docwizz.yaml"
grep -q "^profile: default" "$team/docwizz.yaml"
if dw init "$team" >/dev/null 2>&1; then echo "init overwrote config"; exit 1; fi
rm -rf "$team"
set +e; iso=$(dw check fixture --profile iso-42010 2>/dev/null); set -e
grep -q "human-authored sections (docs/architecture/): stakeholders ✗" <<<"$iso"
grep -q "check: FAIL.*missing architecture sections: stakeholders, concerns, decisions, deployment, security" <<<"$iso"

# Architecture: planted violations and the cycle they cause
arch=$(sed -n '/^Architecture/,$p' <<<"$report")
grep -A1 "ARCH-001  domain → infrastructure" <<<"$arch" | grep -q "Domain/Material.cs → backend/Infrastructure/SqlMaterialRepository.cs"
grep -A1 "ARCH-002  ui → http" <<<"$arch" | grep -q "MaterialTable.vue → GET /api/materials"
grep -q "ARCH-003  cycle: backend/Domain ↔ backend/Infrastructure" <<<"$arch"
grep -q "ARCH-001  domain → infrastructure  \[high\]" <<<"$arch" && grep -q "ARCH-002  ui → http  \[medium\]" <<<"$arch"
grep -q "dependencies: .*domain → infrastructure 1 ✗" <<<"$arch"
set +e; dw architecture fixture --format json > "$model.arch" 2>/dev/null; code=$?; set -e
[ "$code" -eq 1 ] || { echo "architecture should fail on violations"; exit 1; }
python3 -c "import json,sys; a=json.load(open(sys.argv[1])); assert {'from':'api','to':'application','count':4,'allowed':True} in a['layerDependencies'], a" "$model.arch"
grep -q "Architecture (2 violations, 1 cycles)" <<<"$arch"

# Docs generation: pages, cross-links, and safe regeneration
docs=$(mktemp -d)
echo "hand-written" > "$docs/notes.md"
echo "<!-- generated by docwizz: edits are overwritten -->" > "$docs/stale.md"
dotnet run --project src/DocWizz -- generate fixture "$docs" >/dev/null
for f in index.md architecture.md api.md frontend.md quality.md modules/backend-Application.md modules/backend.md .docwizz/model.json \
         views/context.md views/containers.md views/components.md views/data.md views/deployment.md architecture-description.md; do
  [ -f "$docs/$f" ] || { echo "missing $f"; exit 1; }
done
grep -q "| POST | \`/api/materials\` |.*\`createMaterial\`" "$docs/api.md"
grep -q "| PUT | \`/api/materials/{id}\` | Renames a material. | \`id: int\`<br>\`\[body\] request: RenameMaterialRequest\` | \`Material\` | required |" "$docs/api.md"
grep -q "ARCH-001 | domain → infrastructure | high |" "$docs/architecture.md"
grep -q "^- \`Fixture\`: ASP.NET Core on net9.0" "$docs/index.md"
grep -q "^- Entity Framework Core, SQL Server (9.0.0)" "$docs/index.md"
grep -q "^- Vue (^3.5.0)" "$docs/index.md"
grep -q "| Repositories | 1 |" "$docs/index.md" && grep -q "| Background services | 1 |" "$docs/index.md"
grep "MaterialService.CreateAsync" "$docs/quality.md" | grep -q "| ✓ |"  # tested
grep "MaterialController.Create(" "$docs/quality.md" | grep -q "| — |"  # untested
grep -q "/materials\` | \[MaterialTable\]" "$docs/frontend.md"
grep "\[MaterialTable\]" "$docs/frontend.md" | grep -q "| MaterialForm @created |"
grep -q "n0 --> n1" "$docs/modules/backend-Application.md"
grep "\`CreateAsync" "$docs/modules/backend-Application.md" | grep -q "side effects (inferred): db, event"
grep -q "_Generated from .* profile \`default\`" "$docs/modules/backend-Application.md"
# Module pages: role, key components, API, data, external systems, flows, gaps, observations — only when there is content
M="$docs/modules"
grep -q '^Layer \*\*infrastructure\*\* · project `Fixture` · Repositories: 1, DbContexts: 1' "$M/backend-Infrastructure.md"
grep -q '^| \[AppDbContext\](#appdbcontext) | dbcontext | 2 |' "$M/backend-Infrastructure.md"
grep -qF -- '- `AppDbContext` stores Material in SQL Server (inferred)' "$M/backend-Infrastructure.md"
grep -qF -- '- `SqlMaterialRepository` uses `AppDbContext`' "$M/backend-Infrastructure.md"
grep -qF -- '- erp.example.com (http-api, detected) — used by `ErpClient`' "$M/backend-Infrastructure.md"
grep -qF -- '- `GET /api/stock/{sku}`: StockController → ErpClient → erp.example.com' "$M/backend-Infrastructure.md"
grep -q '^- ARCH-003: part of a dependency cycle' "$M/backend-Infrastructure.md"
grep -qF '| `PUT /api/materials/{id}` | Renames a material. | required |' "$M/backend-Api.md"
grep -q '^4 items need documentation (4 critical)' "$M/backend-Api.md"
grep -qF -- '- `/materials` → MaterialTable →' "$M/frontend-src-components.md"
grep -q '^- ARCH-002 (medium): ui → http' "$M/frontend-src-components.md"
if grep -q '^## API' "$M/backend-Domain.md"; then echo "empty section rendered"; exit 1; fi
if grep -q '`GET /health`:$' "$M/backend.md"; then echo "empty flow listed"; exit 1; fi
grep -q '`Attach(Fixture.Application.MaterialService)`.* | Starts listening to `service`. |' "$docs/modules/backend-Application.md"
grep -q "_Evidence:_ \[backend/Api/MaterialController.cs:[0-9]*-[0-9]*\](" "$docs/modules/backend-Api.md"
python3 -c "import json,sys; assert json.load(open(sys.argv[1]))['profile'] == 'default'" "$docs/.docwizz/documentation.json"
[ -f "$docs/notes.md" ] || { echo "deleted a hand-written file"; exit 1; }
grep -q 'c0 -->|HTTP| c1' "$docs/views/containers.md"                          # frontend → backend container
grep -q '| Material | AppDbContext | SQL Server (inferred) | POST /orders, SqlMaterialRepository |' "$docs/views/data.md"
grep -q '| erp.example.com | http-api | detected | ErpClient |' "$docs/views/context.md"
grep -q 'c[0-9]* -->|reads/writes| c[0-9]*' "$docs/views/containers.md"
D="$docs/views/deployment.md"
# Deployment: compose services (env names only), what they run, Kubernetes, Dockerfiles, IaC
grep -q '^| \[api\](.*) | build `../backend` | 8080:8080 | ConnectionStrings__Default, MATERIALS__BETA | db, cache |  | project `Fixture` |' "$D"
grep -q '^| \[db\](.*) | `mcr.microsoft.com/mssql/server:2022-latest` | .* | db-data:/var/opt/mssql | SQL Server — used by the code (inferred) |' "$D"
grep -q '^| \[mail\](.*) | .* | SMTP server — not referenced by the scanned code |' "$D"
grep -q 'n[0-9] --> n[0-9]' "$D"                                                  # depends_on graph
grep -q '^| \[Deployment/materials-api\](.*) | `registry.example.com/materials-api:1.4` | 8080 | Warehouse__BaseUrl |' "$D"
grep -q '^| \[Service/materials-api\](.*) | — | 80→8080 |' "$D"
grep -q 'backend/Dockerfile.*: from `mcr.microsoft.com/dotnet/sdk:9.0`, `mcr.microsoft.com/dotnet/aspnet:9.0`; exposes 8080' "$D"
grep -q 'deploy/main.bicep.*: `Microsoft.Sql/servers`, `Microsoft.Sql/servers/databases`' "$D"
grep -q '`MATERIALS:BETA`.* | Fixture | detected: set by the deployment (api in deploy/docker-compose.yml) |' "$D"
grep -q '^## Not derivable from the repository' "$D"
grep -q '`Warehouse:BaseUrl`.* | default, Development | warehouse.example.net, localhost (Development) | WarehouseClient | detected: defined and read |' "$D"
grep -q '`AUDIT_ENDPOINT`.* | Fixture | unknown: read, but no repository file defines it' "$D"
grep -q '`Materials:MaxQuantity`.* | detected: bound as part of a section |' "$D"
grep -q '`LegacyExport:Folder`.* | defined, not read by scanned code |' "$D"
grep -q '`AllowedHosts`.* | read by the framework |' "$D"
grep -q '| warehouse.example.net | http-api | detected | WarehouseClient | `Warehouse:BaseUrl` |' "$docs/views/context.md"
grep -q '| `Materials` | MaterialOptions (binds) | detected: defined and read |' "$docs/modules/backend-Infrastructure.md"
if grep -rq "not-a-real-secret" "$docs"; then echo "config value leaked into docs"; exit 1; fi
grep -q 'subgraph application\["application"\]' "$docs/views/components.md"
grep -q '\["createMaterial"\]' "$docs/api.md"                                  # API flow diagram
# Flows: endpoint → handler → services (through interfaces) → data → external systems; route → … → endpoints
grep -qF -- '- `POST /api/materials`: MaterialController → MaterialService → IEventPublisher, SqlMaterialRepository → AppDbContext → SQL Server (inferred)' "$docs/api.md"
grep -qF -- '- `GET /api/stock/{sku}`: StockController → ErpClient → erp.example.com' "$docs/api.md"
grep -qF -- '- `POST /orders`: AppDbContext → SQL Server (inferred)' "$docs/api.md"
grep -qF -- '- `/materials` → MaterialTable → GET /api/materials (no endpoint found), MaterialForm → useMaterialStore → createMaterial, getMaterial → GET /api/materials/{id}, POST /api/materials' "$docs/frontend.md"
if grep -q "rates.example.org" "$docs/api.md"; then echo "external URL listed as missing endpoint"; exit 1; fi
grep -q '| stakeholders | missing |' "$docs/architecture-description.md"
grep -q 'Endpoints without declared authorization: .*`GET /health`' "$docs/architecture-description.md"
mkdir -p "$docs/architecture" && echo "# Stakeholders" > "$docs/architecture/stakeholders.md"
dotnet run --project src/DocWizz -- generate fixture "$docs" >/dev/null 2>&1
grep -q '| stakeholders | \[present\](architecture/stakeholders.md) |' "$docs/architecture-description.md"
[ "$(cat "$docs/architecture/stakeholders.md")" = "# Stakeholders" ] || { echo "human section overwritten"; exit 1; }
[ ! -f "$docs/stale.md" ] || { echo "stale generated page kept"; exit 1; }
rm -rf "$docs"

# Change impact: only what a change introduces fails `check --since`
repo=$(mktemp -d)
cp -r fixture/. "$repo"
git -C "$repo" init -q && git -C "$repo" add -A && git -C "$repo" -c user.name=t -c user.email=t@t commit -qm base
dw check "$repo" --since HEAD | grep -q "check: PASS"
python3 - "$repo/backend/Application/MaterialService.cs" <<'PY'
import sys; p = sys.argv[1]; s = open(p).read()
s = s.replace("    // Trivial: should NOT be flagged", """    public int Score(int a, int b, int c, int d)
    {
        if (a > b && c > d) return 1;
        if (a < b || c < d) return 2;
        return a > 0 ? (b > 0 ? 3 : 4) : 5;
    }

    // Trivial: should NOT be flagged""")
open(p, "w").write(s)
PY
cat > "$repo/backend/Domain/Audit.cs" <<'CS'
namespace Fixture.Domain;
public class Audit { public void Log(Fixture.Infrastructure.SqlMaterialRepository r) => r.FindAsync(1); }
CS
set +e; impact=$(dw check "$repo" --since HEAD); code=$?; set -e
echo "$impact"
[ "$code" -eq 1 ] || { echo "check --since should fail"; exit 1; }
grep -q "+ Fixture.Application.MaterialService.Score(int, int, int, int)" <<<"$impact"
grep -q "✓ modules/backend-Application.md" <<<"$impact"
grep -q "✓ architecture.md" <<<"$impact"
grep -q "Introduced: 0 critical, 1 other documentation gaps, 1 architecture violations" <<<"$impact"
grep -q "ARCH-001  domain → infrastructure  backend/Domain/Audit.cs" <<<"$impact"
# Two refs: commit the change, then compare HEAD~1..HEAD from inside the repo, without a dir argument
git -C "$repo" add -A && git -C "$repo" -c user.name=t -c user.email=t@t commit -qm change
refs=$(cd "$repo" && dotnet run --project "$OLDPWD/src/DocWizz" -- diff HEAD~1 HEAD)
grep -q "vs HEAD~1, at HEAD" <<<"$refs"
grep -q "+ Fixture.Domain.Audit" <<<"$refs"

# ARCH-004: a dependency the rules allow only through another layer bypasses it
cat > "$repo/docwizz.yaml" <<'YAML'
architecture:
  layers:
    api: ["*/Api/*.cs", "*/Program.cs"]
    application: ["*/Application/*.cs"]
    infrastructure: ["*/Infrastructure/*.cs"]
  allow:
    api: [application]
    application: [infrastructure]
YAML
set +e; bypass=$(dw architecture "$repo" 2>/dev/null); set -e
grep -q "backend/Program.cs → backend/Infrastructure/SqlMaterialRepository.cs.*bypassing application" <<<"$bypass"
grep -q "ARCH-004  api → infrastructure  \[low\]" <<<"$bypass"
# fail_on: low-severity violations are reported but don't fail; max_complexity does
cat >> "$repo/docwizz.yaml" <<'YAML'
check:
  fail_on: medium
  max_cycles: 5
  max_complexity: 5
YAML
dw architecture "$repo" >/dev/null 2>&1 || { echo "low-severity violation failed architecture despite fail_on: medium"; exit 1; }
set +e; complex=$(dw check "$repo" 2>/dev/null); set -e
grep -q "complexity > 5: Fixture.Application.MaterialService.CreateAsync(.*) ([0-9]*)" <<<"$complex" || { echo "$complex" | tail -3; exit 1; }
if grep -q "violations >" <<<"$complex"; then echo "low violation counted"; exit 1; fi
echo "architecture: { severity: { ARCH-001: extreme } }" > "$repo/docwizz.yaml"
if dw architecture "$repo" >/dev/null 2>&1; then echo "bad severity accepted"; exit 1; fi
rm -rf "$repo"

# AI drafts: served from the cache per (symbol, body hash), marked, never over a written summary
docs=$(mktemp -d)
dotnet run --project src/DocWizz -- generate fixture "$docs" >/dev/null 2>&1
python3 - "$docs" <<'PY'
import json, sys
d = sys.argv[1]
nodes = {n["id"]: n for n in json.load(open(d + "/.docwizz/model.json"))["nodes"]}
get = "cs:Fixture.Api.MaterialController.Get(int)"
json.dump({f"{get}@{nodes[get]['hash']}": {"text": "Returns one material by id.", "sources": [get, "cs:Fixture.Application.IMaterialService.GetAsync(int)"]},
           "cs:Fixture.Api.MaterialController.Get(int)@stale": "outdated draft"}, open(d + "/.docwizz/ai-cache.json", "w"))
PY
dotnet run --project src/DocWizz -- generate fixture "$docs" >/dev/null 2>&1
grep -q "| GET | \`/api/materials/{id}\` | 🤖 _Returns one material by id._ |" "$docs/api.md"
grep -q "🤖 marks 1 AI-drafted" "$docs/index.md"
grep -q '| `Fixture.Api.MaterialController.Get(int)` | Returns one material by id. | .*`Fixture.Application.IMaterialService.GetAsync(int)`' "$docs/quality.md"
if grep -q "outdated draft" -r "$docs"/*.md; then echo "stale draft used"; exit 1; fi
python3 - "$docs/.docwizz/documentation.json" <<'PY'
import json, sys
i = {i["id"]: i for i in json.load(open(sys.argv[1]))["items"]}["cs:Fixture.Api.MaterialController.Get(int)"]
assert i["sections"]["summary"]["origin"] == "ai" and "summary" in i["missing"], i  # drafts never close a gap
assert "cs:Fixture.Application.IMaterialService.GetAsync(int)" in i["sections"]["summary"]["from"], i  # provenance
PY
rm -rf "$docs"

# --ai talks only to a self-hosted Ollama: a fake one on loopback drafts; public hosts and cloud models are refused
docs=$(mktemp -d); port_file=$(mktemp)
python3 - "$port_file" <<'PY' &
import http.server, json, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        req = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert self.path == "/api/chat" and req["model"] == "local:7b" and not req["stream"], req
        body = json.dumps({"message": {"content": "<think>hmm</think>Drafted locally."}}).encode()
        self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H)
open(sys.argv[1], "w").write(str(s.server_port)); s.serve_forever()
PY
fake=$!; trap 'kill $fake 2>/dev/null' EXIT
until [ -s "$port_file" ]; do sleep 0.1; done
OLLAMA_HOST="127.0.0.1:$(cat "$port_file")" DOCWIZZ_MODEL=local:7b dw generate fixture "$docs" --ai >/dev/null 2>&1
grep -q "🤖 _Drafted locally._" "$docs/api.md"
public=$(OLLAMA_HOST=8.8.8.8 dw generate fixture "$(mktemp -d)" --ai 2>&1 >/dev/null)
grep -q "8.8.8.8 is not a local or private address" <<<"$public" || { echo "$public"; exit 1; }
cloud=$(OLLAMA_HOST="127.0.0.1:$(cat "$port_file")" DOCWIZZ_MODEL=gpt-oss:120b-cloud dw generate fixture "$(mktemp -d)" --ai 2>&1 >/dev/null)
grep -q "gpt-oss:120b-cloud is an Ollama cloud model" <<<"$cloud" || { echo "$cloud"; exit 1; }
kill $fake; rm -rf "$docs" "$port_file"
echo PASS
