#!/usr/bin/env bash
# Smoke check: scan the fixture, assert file counts and the expected code graph.
set -euo pipefail
cd "$(dirname "$0")"
model=$(mktemp)
out=$(dotnet run --project src/DocWizz -- scan fixture "$model")
echo "$out"
grep -q "Files  33" <<<"$out"  # tests/ excluded

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
assert {n.get("language") for n in nodes.values() if n["kind"] not in ("external", "config")} == {"csharp", "vue", "typescript", "sql", "java", "msbuild", "maven", "npm"}, {n.get("language") for n in nodes.values()}
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
# minimal-API binding without attributes; injects only for services, not the body
assert nodes["cs:endpoint:POST /orders"]["parameters"] == ["[service] db: AppDbContext"]
patch = nodes["cs:endpoint:PATCH /api/materials/{id}"]
assert patch["parameters"] == ["[route] id: int", "[query] notify: bool?", "[body] change: RenameMaterialRequest", "[special] ct: CancellationToken"], patch
assert not any(e[0] == "injects" and e[1] == patch["id"] for e in edges), "body parameter injected"
# responses: [ProducesResponseType], Ok(x)/CreatedAtAction(.., x), .Produces<T>(), Results.NoContent()
assert nodes[API + "MaterialController.Get(int)"]["responses"] == ["200 Material", "404"]
assert nodes[API + "MaterialController.Create(string, int, string, string, string, bool)"]["responses"] == ["201 int"]
assert nodes["cs:endpoint:GET /health"]["responses"] == ["200 string"] and nodes["cs:endpoint:DELETE /admin/cache"]["responses"] == ["204"]
# request pipeline in registration order; AddHostedService<T>
pipe = [e["to"] for e in m["edges"] if e["kind"] == "pipeline"]
assert pipe == ["pipeline:UseHttpsRedirection", I + "TimingMiddleware", "pipeline:UseAuthorization"], pipe
assert "hosted" in nodes[I + "MaterialCleanup"]["tags"]
# SQL: procedures (doc, parameters), tables from migrations, what routines touch, code calling a procedure
assert nodes["sql:dbo.getlowstock"]["kind"] == "procedure" and nodes["sql:dbo.getlowstock"]["parameters"] == ["@threshold: int"]
assert "below a threshold" in nodes["sql:dbo.getlowstock"]["doc"] and nodes["sql:dbo.materials"]["kind"] == "table"
assert nodes["sql:migration:db/migrations/v1__materials.sql"]["kind"] == "migration"
for e in [("accesses", "sql:dbo.getlowstock", "sql:dbo.materials"), ("accesses", "sql:dbo.resetstock", "sql:dbo.materials"),
          ("calls", "sql:dbo.resetstock", "sql:dbo.getlowstock"), ("calls", I + "SqlMaterialRepository.LowStockAsync(int)", "sql:dbo.getlowstock")]:
    assert e in edges, e
assert "sql:dbo.ignored" not in nodes and not any(e[2] == "sql:dbo.ignored" for e in edges)
# Java / Spring: roles, endpoints (route, parameter sources, auth, unwrapped return), injection, calls, dispatch, config
Jv = "java:com.example.inventory."
low, get = nodes[Jv + "controller.InventoryController.low(int)"], nodes[Jv + "controller.InventoryController.get(Long)"]
assert low["tags"] == ["endpoint", "GET"] and low["route"] == "api/inventory/low" and low["parameters"] == ["[query] max: int"], low
assert "Items running low." in low["doc"] and get["tags"] == ["endpoint", "GET", "authorize"] and get["returns"] == "Item", get
assert nodes[Jv + "domain.Item"]["tags"] == ["entity"] and nodes[Jv + "repository.ItemRepository"]["tags"] == ["repository"]
for e in [("injects", Jv + "controller.InventoryController", Jv + "service.InventoryService"),
          ("injects", Jv + "service.DefaultInventoryService", Jv + "repository.ItemRepository"),
          ("calls", Jv + "controller.InventoryController.get(Long)", Jv + "service.InventoryService.find(Long)"),
          ("implements", Jv + "service.DefaultInventoryService.find(Long)", Jv + "service.InventoryService.find(Long)"),
          ("calls", Jv + "service.DefaultInventoryService.lowStock()", Jv + "repository.ItemRepository.findByQuantityLessThan(int)"),
          ("reads", Jv + "service.DefaultInventoryService", "config:inventory.low-stock"),
          ("connects", "proj:java/pom.xml", "ext:postgresql")]:
    assert e in edges, e
assert not any("InventoryControllerTest" in n for n in nodes), "src/test is test code"
# React: components with props, hooks and renders; createBrowserRouter routes; fetch → the Spring endpoint
Rc = "ts:react/src/"
page, lst = nodes[Rc + "pages/InventoryPage.tsx#InventoryPage"], nodes[Rc + "components/ItemList.tsx#ItemList"]
assert page["kind"] == lst["kind"] == "component" and page["hooks"] == ["useState()", "useEffect()"], page
assert lst["parameters"] == ["items: string[]", "compact: boolean"] and "list of item names" in lst["doc"], lst
# Angular: @Component with @Input/@Output, lifecycle, template children; @Injectable service; HttpClient → the C# endpoint
Ng = "ts:angular/src/app/stock/"
row, stock = nodes[Ng + "item-row.component.ts#ItemRowComponent"], nodes[Ng + "stock.component.ts#StockComponent"]
assert row["kind"] == "component" and row["parameters"] == ["sku: string"] and row["events"] == ["picked"], row
assert stock["hooks"] == ["ngOnInit"] and "service" in nodes[Ng + "stock.service.ts#StockService"]["tags"]
for e in [("renders", Rc + "pages/InventoryPage.tsx#InventoryPage", Rc + "components/ItemList.tsx#ItemList"),
          ("routes-to", "route:/inventory", Rc + "pages/InventoryPage.tsx#InventoryPage"),
          ("http", Rc + "pages/InventoryPage.tsx#InventoryPage", Jv + "controller.InventoryController.low(int)"),
          ("renders", Ng + "stock.component.ts#StockComponent", Ng + "item-row.component.ts#ItemRowComponent"),
          ("routes-to", "route:/stock", Ng + "stock.component.ts#StockComponent"),
          ("routes-to", "route:/stock/:sku", Ng + "item-row.component.ts#ItemRowComponent"),   # loadComponent
          ("calls", Ng + "stock.component.ts#StockComponent.ngOnInit", Ng + "stock.service.ts#StockService.level"),
          ("http", Ng + "stock.service.ts#StockService.level", API + "StockController.Get(string)")]:
    assert e in edges, e
assert not any("Tests" in n["id"] for n in nodes.values() if n["kind"] != "project"), "test code in the model"
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
# lifecycle hooks and composables per component; TS classes (with methods), interfaces, types
assert form["hooks"] == ["useMaterialStore()"] and nodes["vue:" + F + "components/MaterialTable.vue"]["hooks"] == ["onMounted"]
S = "ts:" + F + "api/stockApi.ts#"
assert [nodes[S + x]["kind"] for x in ["StockLevel", "Sku", "StockClient", "StockClient.level"]] == ["interface", "type", "class", "method"]
assert ("contains", S + "StockClient", S + "StockClient.level") in edges
# HTTP wrappers: an axios.create({ baseURL }) instance and a request() helper resolve to the endpoints
assert ("http", S + "StockClient.level", API + "StockController.Get(string)") in edges
assert ("http", S + "StockClient.material", API + "MaterialController.Get(int)") in edges
assert not any(e[0] == "http" and "http.ts" in e[1] for e in edges), "the wrapper's own call is not a call site"

assert ("accesses", I + "SqlMaterialRepository.AddAsync(Fixture.Domain.Material)", I + "AppDbContext") in edges  # primary-ctor dependency used
assert not any(e[0] == "accesses" and e[2] == D + "Material" for e in edges), "member of another object counted as own dependency"

# External systems: detected from calls, inferred from packages, attributed to the code (or project) that uses them
ext = {n["id"]: n["tags"] for n in nodes.values() if n["kind"] == "external"}
assert ext == {"ext:http:erp.example.com": ["http-api", "detected"], "ext:redis": ["cache", "detected"], "ext:postgresql": ["database", "inferred"],
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
                 ("reads", "proj:backend/Fixture.csproj", "config:audit_endpoint"),
                 ("reads", "java:com.example.inventory.service.DefaultInventoryService", "config:inventory.low-stock")}, reads  # GetEnvironmentVariable("MATERIALS__BETA")
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
dw analyze fixture --format json --profile aspnet 2>/dev/null > "$model.aspnet"
dw analyze fixture --format json --profile vue 2>/dev/null > "$model.vue"
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
asp = items(sys.argv[1] + ".aspnet")
assert asp["cs:Fixture.Infrastructure.MaterialCleanup"]["pattern"] == "hosted" and asp["cs:Fixture.Infrastructure.TimingMiddleware"]["pattern"] == "middleware"
assert asp["cs:Fixture.Infrastructure.AppDbContext"]["pattern"] == "dbcontext"
vue = items(sys.argv[1] + ".vue")
assert vue["vue:frontend/src/components/MaterialForm.vue"]["pattern"] == "component"
assert [i["pattern"] for i in vue.values() if i["id"].endswith("materialStore.ts#useMaterialStore")] == ["store"], [i for i in vue if "Store" in i]
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
grep -A2 "ARCH-002  ui → http" <<<"$arch" | grep -q "MaterialTable.vue → GET /api/materials"
grep -A2 "ARCH-002  ui → http" <<<"$arch" | grep -q "InventoryPage.tsx → GET /api/inventory/low"   # React fetch in a component
grep -q "ARCH-003  cycle: backend/Domain ↔ backend/Infrastructure" <<<"$arch"
grep -q "ARCH-001  domain → infrastructure  \[high\]" <<<"$arch" && grep -q "ARCH-002  ui → http  \[medium\]" <<<"$arch"
grep -q "dependencies: .*domain → infrastructure 1 ✗" <<<"$arch"
set +e; dw architecture fixture --format json > "$model.arch" 2>/dev/null; code=$?; set -e
[ "$code" -eq 1 ] || { echo "architecture should fail on violations"; exit 1; }
python3 -c "import json,sys; a=json.load(open(sys.argv[1])); assert {'from':'api','to':'application','count':7,'allowed':True} in a['layerDependencies'], a" "$model.arch"   # 4 C# + 3 Java (inject, 2 calls)
grep -q "Architecture (3 violations, 1 cycles)" <<<"$arch"

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
grep -q "| GET | \`/api/materials/{id}\` |.*| \`Material?\`<br>200 \`Material\`, 404 |" "$docs/api.md"
grep -q "^## Request pipeline (project \`Fixture\`)" "$docs/api.md"
grep -q "^2\. \[TimingMiddleware\](modules/backend-Infrastructure.md#.*) (\`UseMiddleware\`)" "$docs/api.md"
grep -q "^3\. \`UseAuthorization\`" "$docs/api.md"
grep -q "| PUT | \`/api/materials/{id}\` | Renames a material. | \`id: int\`<br>\`\[body\] request: RenameMaterialRequest\` | \`Material\` | required |" "$docs/api.md"
grep -q "ARCH-001 | domain → infrastructure | high |" "$docs/architecture.md"
grep -q "^- \`Fixture\`: ASP.NET Core on net9.0" "$docs/index.md"
# project roles from metadata, solution folders from Fixture.sln (nested: Backend/API)
grep -q "^- \`Fixture\`: ASP.NET Core on net9.0 — executable, solution folder \`Backend/API\`" "$docs/index.md"
grep -q "^- \`Fixture.Tests\`: .NET on net9.0 — test, solution folder \`Tests\`" "$docs/index.md"
grep -q "^- \`inventory\`: Spring Boot (maven) — executable" "$docs/index.md"
grep -q "| GET | \`/api/inventory/{id}\` | — | \`\[route\] id: Long\` | \`Item\` | required |" "$docs/api.md"
grep -qF -- '- `GET /api/inventory/low`: InventoryController → DefaultInventoryService → ItemRepository' "$docs/api.md"   # through the interface
# .slnx: folders from <Folder Name="/…/">
slnx=$(mktemp -d); cp -r fixture/. "$slnx"; rm "$slnx/Fixture.sln"
cat > "$slnx/Fixture.slnx" <<'XML'
<Solution>
  <Folder Name="/Web/"><Project Path="backend/Fixture.csproj" /></Folder>
  <Project Path="backend/Fixture.Tests/Fixture.Tests.csproj" />
</Solution>
XML
dw generate "$slnx" "$slnx/docs" >/dev/null 2>&1
grep -q "^- \`Fixture\`: ASP.NET Core on net9.0 — executable, solution folder \`Web\`" "$slnx/docs/index.md"
grep -q "^- \`Fixture.Tests\`: .NET on net9.0 — test$" "$slnx/docs/index.md"
rm -rf "$slnx"
grep -q "^- Entity Framework Core, SQL Server (9.0.0)" "$docs/index.md"
grep -q "^- Vue (^3.5.0)" "$docs/index.md"
grep -q "| Repositories | 1 |" "$docs/index.md" && grep -q "| Background services | 1 |" "$docs/index.md"
grep "MaterialService.CreateAsync" "$docs/quality.md" | grep -q "| ✓ |"  # tested
grep "MaterialController.Create(" "$docs/quality.md" | grep -q "| — |"  # untested
grep -q "/materials\` | \[MaterialTable\]" "$docs/frontend.md"
grep "\[MaterialTable\]" "$docs/frontend.md" | grep -q "| MaterialForm @created |"
grep "\[MaterialTable\]" "$docs/frontend.md" | grep -q "| onMounted | MaterialForm |"
grep -q "n0 --> n1" "$docs/modules/backend-Application.md"
grep "\`CreateAsync" "$docs/modules/backend-Application.md" | grep -q "side effects (inferred): db, event"
grep -q "_Generated from .* profile \`default\`" "$docs/modules/backend-Application.md"
# Module pages: role, key components, API, data, external systems, flows, gaps, observations — only when there is content
M="$docs/modules"
grep -q '^Layer \*\*infrastructure\*\* · project `Fixture` · Repositories: 1, DbContexts: 1' "$M/backend-Infrastructure.md"
grep -q '^### StockClient$' "$M/frontend-src-api.md"   # TS classes are components of their module page
# backlinks: the flows that reach a component, the configuration it reads; search.json indexes every documented thing
grep -q '^_Reached from:_ \[`POST /api/materials`\](../api.md#flows), \[`GET /api/materials/{id}`\](../api.md#flows)' "$M/backend-Application.md"
grep -q '^_Configuration:_ \[`Warehouse:BaseUrl`\](../views/deployment.md#configuration)' "$M/backend-Infrastructure.md"
python3 - "$docs/search.json" <<'PY2'
import json, sys
idx = {(e["name"], e["kind"]): e for e in json.load(open(sys.argv[1]))}
assert idx[("MaterialService", "class")]["page"] == "modules/backend-Application.md#materialservice", idx[("MaterialService", "class")]
assert idx[("GET /api/materials/{id}", "endpoint")]["page"] == "api.md"
assert idx[("/materials", "route")]["page"] == "frontend.md"
assert idx[("Warehouse:BaseUrl", "config")]["page"] == "views/deployment.md#configuration"
assert idx[("backend/Application", "module")]["page"] == "modules/backend-Application.md"
assert idx[("StockLevel", "interface")]["summary"] == "Stock of one article, as the ERP reports it."
PY2
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
fe=$(grep -o 'c[0-9]*\["fixture-frontend"\]' "$docs/views/containers.md" | cut -d'[' -f1)
be=$(grep -o 'c[0-9]*\["Fixture"\]' "$docs/views/containers.md" | cut -d'[' -f1)
grep -q "$fe -->|HTTP| $be" "$docs/views/containers.md"                          # frontend → backend container
grep -q '| Material | AppDbContext | SQL Server (inferred) | POST /orders, SqlMaterialRepository |' "$docs/views/data.md"
grep -q '| erp.example.com | http-api | detected | ErpClient |' "$docs/views/context.md"
grep -q 'c[0-9]* -->|reads/writes| c[0-9]*' "$docs/views/containers.md"
D="$docs/views/deployment.md"
# Deployment: compose services (env names only), what they run, Kubernetes, Dockerfiles, IaC
grep -q '^| \[api\](.*) | build `../backend` | 8080:8080 | ConnectionStrings__Default, MATERIALS__BETA | db, cache |  | backend | project `Fixture` |' "$D"
grep -q '^| \[db\](.*) | .* | db-data:/var/opt/mssql | backend | SQL Server' "$D"                  # networks: list and map form
grep -q 'deploy/Jenkinsfile' "$D"
grep -q '^| \[db\](.*) | `mcr.microsoft.com/mssql/server:2022-latest` | .* | db-data:/var/opt/mssql | backend | SQL Server — used by the code (inferred) |' "$D"
grep -q '^| \[mail\](.*) | .* | SMTP server — not referenced by the scanned code |' "$D"
grep -q 'n[0-9] --> n[0-9]' "$D"                                                  # depends_on graph
grep -q '^| \[Deployment/materials-api\](.*) | `registry.example.com/materials-api:1.4` | 8080 | Warehouse__BaseUrl |' "$D"
grep -q '^| \[Service/materials-api\](.*) | — | 80→8080 |' "$D"
grep -q 'backend/Dockerfile.*: from `mcr.microsoft.com/dotnet/sdk:9.0`, `mcr.microsoft.com/dotnet/aspnet:9.0`; exposes 8080' "$D"
grep -q 'deploy/main.bicep.*: `Microsoft.Sql/servers` → SQL Server (used by the code, inferred), `Microsoft.Sql/servers/databases` → SQL Server' "$D"
grep -q '^| SQL Server | database | inferred |.*| \[deploy/main.bicep\](.*) |$' "$docs/views/context.md"   # provisioned by IaC
grep -q '^| \[GetLowStock\](.*) | procedure | Materials whose stock is below a threshold, lowest first. | @threshold: int | Materials | SqlMaterialRepository |' "$docs/views/data.md"
grep -q '^1\. \[V1__materials\](.*) — Creates the materials table.' "$docs/views/data.md"
grep -q '`MATERIALS:BETA`.* | Fixture | detected: set by the deployment (api in deploy/docker-compose.yml) |' "$D"
grep -q '^## Not derivable from the repository' "$D"
grep -q '`Warehouse:BaseUrl`.* | default, Development | warehouse.example.net, localhost (Development) | WarehouseClient | detected: defined and read |' "$D"
# a Kubernetes ConfigMap and Helm values define keys too
grep -q '`AUDIT_ENDPOINT`.* | configmap/materials-config | audit.internal (configmap/materials-config) | Fixture | detected: defined and read |' "$D"
grep -q '`Materials:MaxQuantity`.* | default, helm | ' "$D"
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
sed -i 's/FindAsync(id).AsTask()/FindAsync(id + 0).AsTask()/' "$repo/backend/Infrastructure/SqlMaterialRepository.cs"
cat > "$repo/backend/Domain/Audit.cs" <<'CS'
namespace Fixture.Domain;
public class Audit { public void Log(Fixture.Infrastructure.SqlMaterialRepository r) => r.FindAsync(1); }
CS
cat > "$repo/backend/Infrastructure/Notices.cs" <<'CS'
namespace Fixture.Infrastructure;
/// <summary>Mails a notice when a material is published.</summary>
public class Notices(Fixture.Application.IEventPublisher events) { public void Send() => new System.Net.Mail.SmtpClient().Send("a", "b", "c", "d"); }
CS
set +e; impact=$(dw check "$repo" --since HEAD); code=$?; set -e
echo "$impact"
[ "$code" -eq 1 ] || { echo "check --since should fail"; exit 1; }
grep -q "+ Fixture.Application.MaterialService.Score(int, int, int, int)" <<<"$impact"
grep -q "✓ modules/backend-Application.md" <<<"$impact"
grep -q "✓ architecture.md" <<<"$impact"
# the changed repository method is reached by the endpoints' flows: their pages are affected too
grep -q "~ Fixture.Infrastructure.SqlMaterialRepository.FindAsync(int)" <<<"$impact"
grep -q "✓ api.md" <<<"$impact"
grep -q "✓ modules/backend-Api.md" <<<"$impact"
grep -q "Introduced: 0 critical, 1 other documentation gaps, 1 architecture violations" <<<"$impact"
grep -q "ARCH-001  domain → infrastructure  backend/Domain/Audit.cs" <<<"$impact"
# ADR candidates: a new external system and a new layer dependency
grep -q "? Adopt SMTP server (email, detected) — used by Fixture.Infrastructure.Notices.Send()" <<<"$impact"
grep -q "? Let layer infrastructure depend on application (allowed by the rules)" <<<"$impact"
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

# Incremental regeneration: unchanged pages are not rewritten; a change rewrites only the pages it affects
inc=$(mktemp -d); cp -r fixture/. "$inc"
dw generate "$inc" "$inc/docs" >/dev/null 2>&1
touch -d '2000-01-01' "$inc/docs/views/deployment.md"
again=$(dw generate "$inc" "$inc/docs" 2>/dev/null)
grep -q ": 0 changed$" <<<"$again" || { echo "$again"; exit 1; }
sed -i 's|/// <summary>Stock for one article.</summary>|/// <summary>Stock for one article, from the ERP.</summary>|' "$inc/backend/Api/StockController.cs"
changed=$(dw generate "$inc" "$inc/docs" 2>/dev/null)
grep -q "changed (.*modules/backend-Api.md" <<<"$changed" || { echo "$changed"; exit 1; }
if grep -q "views/deployment.md" <<<"$changed"; then echo "unaffected page rewritten: $changed"; exit 1; fi
[ "$(stat -c %Y "$inc/docs/views/deployment.md")" = "$(date -d '2000-01-01' +%s)" ] || { echo "unchanged page touched"; exit 1; }
rm -rf "$inc"

# HTML output: a twin next to every page, links between generated pages rewritten, anchors kept, search as a script
html=$(mktemp -d)
dw generate fixture "$html" --html >/dev/null 2>&1
for f in index api architecture modules/backend-Application views/deployment; do [ -f "$html/$f.html" ] || { echo "missing $f.html"; exit 1; }; done
grep -q '<td>GET</td>' "$html/api.html" && grep -q 'id="flows"' "$html/api.html"
grep -q 'href="../api.html#flows"' "$html/modules/backend-Application.html" && grep -q 'id="materialservice"' "$html/modules/backend-Application.html"
grep -q 'class="mermaid"' "$html/architecture.html"
grep -q '^window.docwizzSearch = \[' "$html/search.js"
dw generate fixture "$html" >/dev/null 2>&1   # without --html the twins go, like any stale generated page
[ ! -f "$html/index.html" ] || { echo "stale HTML kept"; exit 1; }
rm -rf "$html"

# Architecture risks: coupling table, entities in API responses, logic in the API layer, external packages in the domain
risk=$(mktemp -d); cp -r fixture/. "$risk"
cat > "$risk/backend/Api/PricingController.cs" <<'CS'
using Microsoft.AspNetCore.Mvc;
namespace Fixture.Api;
[ApiController]
[Route("api/pricing")]
public class PricingController : ControllerBase
{
    [HttpGet("{qty}")]
    public int Price(int qty, bool urgent, bool member, string region)
    {
        var p = qty * 10;
        if (urgent) p += 5;
        if (member && qty > 10) p -= 3;
        if (region == "EU" || region == "UK") p += 2;
        for (var i = 0; i < qty; i++) if (i % 100 == 0) p--;
        return p > 0 ? p : 0;
    }
}
CS
sed -i '1i using Microsoft.EntityFrameworkCore;' "$risk/backend/Domain/IMaterialRepository.cs"
dw generate "$risk" "$risk/docs" >/dev/null 2>&1
A="$risk/docs/architecture.md"
grep -q '^| \[backend/Infrastructure\](modules/backend-Infrastructure.md) | infrastructure | 3 | 2 | 0.40 |' "$A"   # fan-out: Domain, db (stored procedure)
grep -q '^- Entity exposed: `GET /api/materials/{id}` returns `Material`' "$A"
grep -q '^- Logic in the API layer: `PricingController.Price(.*)` has complexity [0-9]*' "$A"
grep -q '^- Domain depends on `Microsoft.EntityFrameworkCore` (`IMaterialRepository`)' "$A"
if grep -q 'Domain depends on `Fixture' "$A"; then echo "own namespace flagged"; exit 1; fi
rm -rf "$risk"

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
        assert self.path == "/api/chat" and req["model"] == "local:7b" and not req["stream"] and req["format"] == "json", req
        facts = json.loads(req["messages"][1]["content"])
        S = lambda text, *cites: {"text": text, "from": list(cites)}
        if "module" in facts:  # module overview: one supported sentence, one citing nothing in the facts
            reply = {"summary": [S(f"Module for {facts['module']}.", facts["members"][0]["name"])], "responsibilities": [S("Owns pricing.", "NotInFacts")]}
        elif facts["symbol"].startswith("Fixture.Api.MaterialController.Create("):
            reply = {"summary": [S("Creates a material.", "source")], "behaviour": [S("Delegates to the service.", facts["calls"][0])],
                     "errors": [S("Invented failure.", "Nope")]}
        else:
            reply = "<think>hmm</think>Drafted locally."   # not JSON: taken as a plain summary
        body = json.dumps({"message": {"content": reply if isinstance(reply, str) else json.dumps(reply)}}).encode()
        self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H)
open(sys.argv[1], "w").write(str(s.server_port)); s.serve_forever()
PY
fake=$!; trap 'kill $fake 2>/dev/null || true' EXIT
until [ -s "$port_file" ]; do sleep 0.1; done
OLLAMA_HOST="127.0.0.1:$(cat "$port_file")" DOCWIZZ_MODEL=local:7b dw generate fixture "$docs" --ai >/dev/null 2>&1
grep -q "🤖 _Drafted locally._" "$docs/api.md"
# section-level drafts: cited sentences kept with their provenance, uncited ones dropped; module overviews
grep -q "| POST | \`/api/materials\` | 🤖 _Creates a material._ |" "$docs/api.md"
grep -q "^## Overview 🤖" "$docs/modules/backend-Api.md" && grep -q "^Module for backend/Api\.$" "$docs/modules/backend-Api.md"
if grep -rq "Owns pricing\|Invented failure" "$docs"; then echo "uncited sentence kept"; exit 1; fi
python3 - "$docs/.docwizz/documentation.json" <<'PY2'
import json, sys
i = {i["id"]: i for i in json.load(open(sys.argv[1]))["items"]}["cs:Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)"]
b = i["sections"]["behaviour"]
assert b["origin"] == "ai" and b["sentences"][0]["from"][0].startswith("cs:Fixture.Application.IMaterialService.CreateAsync("), b
assert "exception" not in i["sections"] and "summary" in i["missing"], i
PY2
public=$(OLLAMA_HOST=8.8.8.8 dw generate fixture "$(mktemp -d)" --ai 2>&1 >/dev/null)
grep -q "8.8.8.8 is not a local or private address" <<<"$public" || { echo "$public"; exit 1; }
cloud=$(OLLAMA_HOST="127.0.0.1:$(cat "$port_file")" DOCWIZZ_MODEL=gpt-oss:120b-cloud dw generate fixture "$(mktemp -d)" --ai 2>&1 >/dev/null)
grep -q "gpt-oss:120b-cloud is an Ollama cloud model" <<<"$cloud" || { echo "$cloud"; exit 1; }
kill $fake; rm -rf "$docs" "$port_file"

# Legacy project: no doc comments, no docwizz.yaml, controller talks to the DbContext and holds the logic
legacy=$(mktemp -d)
dw generate fixture-legacy "$legacy" >/dev/null 2>&1
L="$legacy/modules/Shop-Controllers.md"
grep -qF -- '- `GET /api/orders/{id}`, `OrdersController`, `POST /api/orders/{id}/ship` use `ShopContext` directly from the api layer' "$L"
[ "$(grep -c 'directly from the api layer' "$L")" = 1 ] || { echo "repeated data-access observation"; exit 1; }
grep -q '`OrdersController.Ship(int, string, bool, bool, string)` (18)' "$L"
grep -q 'POST /api/orders/{id}/ship`: OrdersController → ShopContext → PostgreSQL' "$L"
grep -q 'Entity exposed: `GET /api/orders/{id}` returns `Order`' "$legacy/architecture.md"
grep -q 'Logic in the API layer: `OrdersController.Ship' "$legacy/architecture.md"
grep -q '^| PostgreSQL | database | detected | ShopContext | `ConnectionStrings:Shop` |' "$legacy/views/context.md"
grep -q 'against the `default` profile: \*\*0%\*\* of 3 items' "$legacy/quality.md"
if grep -rq "🤖 _\|## Overview 🤖\|## Purpose" "$legacy"; then echo "legacy docs claim intent nobody wrote"; exit 1; fi
rm -rf "$legacy"
echo PASS
