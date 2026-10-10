# Context: backend/Infrastructure

- target: module `backend/Infrastructure`
- repository: `fixture`, model at commit `unknown` from a fresh scan (no docs/.docwizz/model.json)
- budget: 6000 tokens, estimated as characters / 4
- provenance: detected = read from the code; inferred = heuristic, confirm in the code; human = written docs, may be outdated; ai-drafted = 🤖 draft, not a fact
- this is a map, not the code: read the code before you change it

## Target

- module `backend/Infrastructure`: 3 files, 17 symbols, layer infrastructure [detected]
- class `Fixture.Infrastructure.ErpClient`: Reads stock levels from the ERP system. — backend/Infrastructure/ErpClient.cs:4-8 [human]
- class `Fixture.Infrastructure.WarehouseClient`: Books material movements in the warehouse system. — backend/Infrastructure/ErpClient.cs:11-15 [human]
- class `Fixture.Infrastructure.MaterialCleanup`: Removes materials nobody requested for a while. — backend/Infrastructure/Hosting.cs:8-11 [human]
- class `Fixture.Infrastructure.TimingMiddleware`: Adds the elapsed time to every response. — backend/Infrastructure/Hosting.cs:14-17 [human]
- class `Fixture.Infrastructure.MaterialOptions`: Material limits, bound from configuration. — backend/Infrastructure/Hosting.cs:20-23 [human]
- delegate `Fixture.Infrastructure.MaterialChanged`: Called when a material changes. — backend/Infrastructure/Hosting.cs:26 [human]
- class `Fixture.Infrastructure.AppDbContext` — backend/Infrastructure/SqlMaterialRepository.cs:6-9 [detected]
- class `Fixture.Infrastructure.SqlMaterialRepository` — backend/Infrastructure/SqlMaterialRepository.cs:11-24 [detected]

## Neighbours

- calls `dbo.getlowstock` — db/procedures.sql:2-8 [detected]
- injects, implements, accesses `Fixture.Domain.IMaterialRepository` — backend/Domain/IMaterialRepository.cs:3-7 [detected]
- implements `Fixture.Domain.IMaterialRepository.AddAsync(Fixture.Domain.Material)` — backend/Domain/IMaterialRepository.cs:6 [detected]
- implements `Fixture.Domain.IMaterialRepository.FindAsync(int)` — backend/Domain/IMaterialRepository.cs:5 [detected]
- persists `Fixture.Domain.Material` — backend/Domain/Material.cs:6-10 [detected]
- called by, accessed by `Fixture.Api.StockController.Get(string)` — backend/Api/StockController.cs:12-13 [detected]
- called by `Fixture.Domain.Material.Save(Fixture.Infrastructure.SqlMaterialRepository)` — backend/Domain/Material.cs:9 [detected]
- injected into `Fixture.Api.StockController` — backend/Api/StockController.cs:7-14 [detected]
- injected into `endpoint:POST /orders` — backend/Program.cs:26 [detected]
- registered (DI) for `Fixture.Domain.IMaterialRepository` — backend/Domain/IMaterialRepository.cs:3-7 [detected]

## Further neighbours (signature only)

- `Fixture.Application.MaterialService` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Application/MaterialService.cs:5-36 [detected]
- `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Application/MaterialService.cs:13-32 [detected]
- `Fixture.Application.MaterialService.GetAsync(int)` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Application/MaterialService.cs:10 [detected]
- `dbo.materials` (hop 2, via `dbo.getlowstock`) — db/migrations/V1__materials.sql:2 [detected]
- `dbo.resetstock` (hop 2, via `dbo.getlowstock`) — db/procedures.sql:9-12 [detected]
- `angular/src/app/stock/stock.service.ts#StockService.level` (hop 2, via `Fixture.Api.StockController.Get(string)`) — angular/src/app/stock/stock.service.ts:9-11 [detected]
- `frontend/src/api/stockApi.ts#StockClient.level` (hop 2, via `Fixture.Api.StockController.Get(string)`) — frontend/src/api/stockApi.ts:10-12 [detected]

## Endpoints, flows, external systems, configuration

- flow `GET /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:13-15 [inferred]
- flow `POST /api/materials → MaterialService → IEventPublisher, SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:18-23 [inferred]
- flow `PUT /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:28-31 [inferred]
- flow `GET /api/stock/{sku} → ErpClient → erp.example.com` — backend/Api/StockController.cs:12-13 [detected]
- flow `POST /orders → AppDbContext → SQL Server (inferred)` — backend/Program.cs:26 [inferred]
- external system warehouse.example.net (http-api) [detected]
- external system erp.example.com (http-api) [detected]
- external system SQL Server (database) [inferred]
- configuration key `Materials` (bound) [detected]
- configuration key `Warehouse:BaseUrl` (read) [detected]

## Tests, documentation gaps, architecture findings (a link means test code uses the symbol; it is not code coverage)

- documentation gap (high, critical): `Fixture.Api.StockController.Get(string)` missing param — backend/Api/StockController.cs:12-13 [detected]
- ARCH-001 (high): Fixture.Domain.Material.Save calls Fixture.Infrastructure.SqlMaterialRepository.AddAsync — backend/Domain/Material.cs [detected]
- ARCH-003 module cycle: backend/Domain ↔ backend/Infrastructure [detected]

## Read before you change it

- `ErpClient` — backend/Infrastructure/ErpClient.cs:4-8 [detected]
- `WarehouseClient` — backend/Infrastructure/ErpClient.cs:11-15 [detected]
- `MaterialCleanup` — backend/Infrastructure/Hosting.cs:8-11 [detected]
- `TimingMiddleware` — backend/Infrastructure/Hosting.cs:14-17 [detected]
- `MaterialOptions` — backend/Infrastructure/Hosting.cs:20-23 [detected]
- `MaterialChanged` — backend/Infrastructure/Hosting.cs:26 [detected]
- `AppDbContext` — backend/Infrastructure/SqlMaterialRepository.cs:6-9 [detected]
- `SqlMaterialRepository` — backend/Infrastructure/SqlMaterialRepository.cs:11-24 [detected]
- `GetLowStock` — db/procedures.sql:2-8 [detected]
- `IMaterialRepository` — backend/Domain/IMaterialRepository.cs:3-7 [detected]
- `AddAsync` — backend/Domain/IMaterialRepository.cs:6 [detected]
- `FindAsync` — backend/Domain/IMaterialRepository.cs:5 [detected]
- `Material` — backend/Domain/Material.cs:6-10 [detected]
- `Get` — backend/Api/StockController.cs:12-13 [detected]
- `Save` — backend/Domain/Material.cs:9 [detected]
- `StockController` — backend/Api/StockController.cs:7-14 [detected]
- `POST /orders` — backend/Program.cs:26 [detected]
