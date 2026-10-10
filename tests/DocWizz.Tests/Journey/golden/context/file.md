# Context: backend/Api/MaterialController.cs

- target: file `backend/Api/MaterialController.cs`
- repository: `fixture`, model at commit `unknown` from a fresh scan (no docs/.docwizz/model.json)
- budget: 6000 tokens, estimated as characters / 4
- provenance: detected = read from the code; inferred = heuristic, confirm in the code; human = written docs, may be outdated; ai-drafted = 🤖 draft, not a fact
- this is a map, not the code: read the code before you change it

## Target

- file `backend/Api/MaterialController.cs`: 1 file, 5 symbols, layer api [detected]
- class `Fixture.Api.MaterialController` — backend/Api/MaterialController.cs:9-32 [detected]
- record `Fixture.Api.RenameMaterialRequest`: Request body for renaming a material. — backend/Api/MaterialController.cs:35 [human]

## Neighbours

- calls `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` — backend/Application/IMaterialService.cs:12 [detected]
- calls `Fixture.Application.IMaterialService.GetAsync(int)` — backend/Application/IMaterialService.cs:9 [detected]
- injects, accesses `Fixture.Application.IMaterialService` — backend/Application/IMaterialService.cs:6-13 [detected]
- called over HTTP by `frontend/src/api/materialApi.ts#createMaterial` — frontend/src/api/materialApi.ts:7-9 [detected]
- called over HTTP by `frontend/src/api/materialApi.ts#getMaterial` — frontend/src/api/materialApi.ts:3-5 [detected]
- called over HTTP by `frontend/src/api/stockApi.ts#StockClient.material` — frontend/src/api/stockApi.ts:14-16 [detected]

## Further neighbours (signature only)

- `Fixture.Application.MaterialService` (hop 2, via `Fixture.Application.IMaterialService`) — backend/Application/MaterialService.cs:5-36 [detected]
- `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)` (hop 2, via `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)`) — backend/Application/MaterialService.cs:13-32 [detected]
- `Fixture.Application.MaterialService.GetAsync(int)` (hop 2, via `Fixture.Application.IMaterialService.GetAsync(int)`) — backend/Application/MaterialService.cs:10 [detected]
- `frontend/src/api/http.ts#request` (hop 2, via `frontend/src/api/stockApi.ts#StockClient.material`) — frontend/src/api/http.ts:7-9 [detected]
- `frontend/src/stores/materialStore.ts#useMaterialStore` (hop 2, via `frontend/src/api/materialApi.ts#createMaterial`) — frontend/src/stores/materialStore.ts:4-10 [detected]

## Endpoints, flows, external systems, configuration

- flow `GET /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:13-15 [inferred]
- flow `POST /api/materials → MaterialService → IEventPublisher, SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:18-23 [inferred]
- flow `PUT /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:28-31 [inferred]
- flow `/materials → MaterialTable → GET /api/materials (no endpoint found), MaterialForm → useMaterialStore → createMaterial, getMaterial → GET /api/materials/{id}, POST /api/materials` — frontend/src/router/index.ts:6 [detected]
- external system SQL Server (database) [inferred]

## Tests, documentation gaps, architecture findings (a link means test code uses the symbol; it is not code coverage)

- documentation gap (high, critical): `Fixture.Api.MaterialController` missing summary — backend/Api/MaterialController.cs:9-32 [detected]
- documentation gap (high, critical): `Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)` missing summary, param — backend/Api/MaterialController.cs:18-23 [detected]
- documentation gap (high, critical): `Fixture.Api.MaterialController.Get(int)` missing summary, param — backend/Api/MaterialController.cs:13-15 [detected]
- documentation gap (medium): `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` missing param — backend/Application/IMaterialService.cs:12 [detected]

## Read before you change it

- `MaterialController` — backend/Api/MaterialController.cs:9-32 [detected]
- `RenameMaterialRequest` — backend/Api/MaterialController.cs:35 [detected]
- `CreateAsync` — backend/Application/IMaterialService.cs:12 [detected]
- `GetAsync` — backend/Application/IMaterialService.cs:9 [detected]
- `IMaterialService` — backend/Application/IMaterialService.cs:6-13 [detected]
- `createMaterial` — frontend/src/api/materialApi.ts:7-9 [detected]
- `getMaterial` — frontend/src/api/materialApi.ts:3-5 [detected]
- `material` — frontend/src/api/stockApi.ts:14-16 [detected]
