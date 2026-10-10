# Context: POST /api/materials

- target: endpoint `POST /api/materials`
- repository: `fixture`, model at commit `unknown` from a fresh scan (no docs/.docwizz/model.json)
- budget: 6000 tokens, estimated as characters / 4
- provenance: detected = read from the code; inferred = heuristic, confirm in the code; human = written docs, may be outdated; ai-drafted = 🤖 draft, not a fact
- this is a map, not the code: read the code before you change it

## Target

- method `Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)`, public, layer api — backend/Api/MaterialController.cs:18-23 [detected]
- route `POST /api/materials` [detected]
- member of class `Fixture.Api.MaterialController` — backend/Api/MaterialController.cs:9-32 [detected]
- parameters: `name: string`, `quantity: int`, `unit: string`, `location: string`, `requestedBy: string`, `urgent: bool` [detected]
- returns `IActionResult` [detected]
- responses: 201 int [detected]
- cyclomatic complexity 1 [detected]
- authorization: none declared [detected]
- input: name: string, quantity: int, unit: string, location: string, requestedBy: string, urgent: bool [detected]
- output: IActionResult [detected]
- documentation undocumented, missing summary, param (high need: public; 6 params; side effects: db, event; pattern endpoint) [detected]

## Neighbours

- calls `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` — backend/Application/IMaterialService.cs:12 [detected]
- injects, accesses `Fixture.Application.IMaterialService` — backend/Application/IMaterialService.cs:6-13 [detected]
- called over HTTP by `frontend/src/api/materialApi.ts#createMaterial` — frontend/src/api/materialApi.ts:7-9 [detected]

## Further neighbours (signature only)

- `Fixture.Api.MaterialController.Get(int)` (hop 2, via `Fixture.Application.IMaterialService`) — backend/Api/MaterialController.cs:13-15 [detected]
- `Fixture.Api.MaterialController.Rename(int, Fixture.Api.RenameMaterialRequest)` (hop 2, via `Fixture.Application.IMaterialService`) — backend/Api/MaterialController.cs:28-31 [detected]
- `Fixture.Application.MaterialService` (hop 2, via `Fixture.Application.IMaterialService`) — backend/Application/MaterialService.cs:5-36 [detected]
- `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)` (hop 2, via `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)`) — backend/Application/MaterialService.cs:13-32 [detected]
- `frontend/src/stores/materialStore.ts#useMaterialStore` (hop 2, via `frontend/src/api/materialApi.ts#createMaterial`) — frontend/src/stores/materialStore.ts:4-10 [detected]

## Endpoints, flows, external systems, configuration

- flow `POST /api/materials → MaterialService → IEventPublisher, SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:18-23 [inferred]
- flow `/materials → MaterialTable → GET /api/materials (no endpoint found), MaterialForm → useMaterialStore → createMaterial, getMaterial → GET /api/materials/{id}, POST /api/materials` — frontend/src/router/index.ts:6 [detected]
- external system SQL Server (database) [inferred]

## Tests, documentation gaps, architecture findings (a link means test code uses the symbol; it is not code coverage)

- documentation gap (high, critical): `Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)` missing summary, param — backend/Api/MaterialController.cs:18-23 [detected]
- documentation gap (medium): `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` missing param — backend/Application/IMaterialService.cs:12 [detected]

## Read before you change it

- `Create` — backend/Api/MaterialController.cs:18-23 [detected]
- `MaterialController` — backend/Api/MaterialController.cs:9-32 [detected]
- `CreateAsync` — backend/Application/IMaterialService.cs:12 [detected]
- `IMaterialService` — backend/Application/IMaterialService.cs:6-13 [detected]
- `createMaterial` — frontend/src/api/materialApi.ts:7-9 [detected]
