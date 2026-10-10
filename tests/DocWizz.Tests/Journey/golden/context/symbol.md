# Context: Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)

- target: symbol `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)`
- repository: `fixture`, model at commit `unknown` from a fresh scan (no docs/.docwizz/model.json)
- budget: 6000 tokens, estimated as characters / 4
- provenance: detected = read from the code; inferred = heuristic, confirm in the code; human = written docs, may be outdated; ai-drafted = 🤖 draft, not a fact
- this is a map, not the code: read the code before you change it

## Target

- method `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)`, public, layer application — backend/Application/MaterialService.cs:13-32 [detected]
- member of class `Fixture.Application.MaterialService` — backend/Application/MaterialService.cs:5-36 [detected]
- parameters: `name: string`, `quantity: int`, `unit: string`, `location: string`, `requestedBy: string`, `urgent: bool` [detected]
- returns `int` [detected]
- throws `ArgumentException` [detected]
- cyclomatic complexity 13 [detected]
- dependencies: IMaterialService, IMaterialRepository, IEventPublisher, Material [detected]
- side effects: db, event [inferred]
- summary: Creates a material request. [human]
- documentation partial, missing param (high need: public; complexity 13; 6 params; side effects: db, event) [detected]

## Neighbours

- calls `Fixture.Application.IEventPublisher.PublishAsync(string, int)` — backend/Application/IEventPublisher.cs:5 [detected]
- calls `Fixture.Domain.IMaterialRepository.AddAsync(Fixture.Domain.Material)` — backend/Domain/IMaterialRepository.cs:6 [detected]
- injects, accesses `Fixture.Application.IEventPublisher` — backend/Application/IEventPublisher.cs:3-6 [detected]
- injects, accesses `Fixture.Domain.IMaterialRepository` — backend/Domain/IMaterialRepository.cs:3-7 [detected]
- implements `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` — backend/Application/IMaterialService.cs:12 [detected]
- publishes `Fixture.Application.MaterialService.Created` — backend/Application/MaterialService.cs:8 [detected]
- creates `Fixture.Domain.Material` — backend/Domain/Material.cs:6-10 [detected]

## Further neighbours (signature only)

- `Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)` (hop 2, via `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)`) — backend/Api/MaterialController.cs:18-23 [detected]
- `Fixture.Application.MaterialNotifier.Attach(Fixture.Application.MaterialService)` (hop 2, via `Fixture.Application.MaterialService.Created`) — backend/Application/MaterialNotifier.cs:7 [detected]
- `Fixture.Application.MaterialService.GetAsync(int)` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Application/MaterialService.cs:10 [detected]
- `Fixture.Infrastructure.AppDbContext` (hop 2, via `Fixture.Domain.Material`) — backend/Infrastructure/SqlMaterialRepository.cs:6-9 [detected]
- `Fixture.Infrastructure.MaterialCleanup` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Infrastructure/Hosting.cs:8-11 [detected]
- `Fixture.Infrastructure.MaterialCleanup.ExecuteAsync(System.Threading.CancellationToken)` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Infrastructure/Hosting.cs:10 [detected]
- `Fixture.Infrastructure.SqlMaterialRepository` (hop 2, via `Fixture.Domain.IMaterialRepository`) — backend/Infrastructure/SqlMaterialRepository.cs:11-24 [detected]
- `Fixture.Infrastructure.SqlMaterialRepository.AddAsync(Fixture.Domain.Material)` (hop 2, via `Fixture.Domain.IMaterialRepository.AddAsync(Fixture.Domain.Material)`) — backend/Infrastructure/SqlMaterialRepository.cs:18-23 [detected]

## Endpoints, flows, external systems, configuration

- flow `GET /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:13-15 [inferred]
- flow `POST /api/materials → MaterialService → IEventPublisher, SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:18-23 [inferred]
- flow `PUT /api/materials/{id} → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)` — backend/Api/MaterialController.cs:28-31 [inferred]
- external system SQL Server (database) [inferred]

## Tests, documentation gaps, architecture findings (a link means test code uses the symbol; it is not code coverage)

- test `Fixture.Tests.MaterialServiceTests.CreateAsync_Creates` [detected]
- documentation gap (high, critical): `Fixture.Application.MaterialService.CreateAsync(string, int, string, string, string, bool)` missing param — backend/Application/MaterialService.cs:13-32 [detected]
- documentation gap (medium): `Fixture.Application.IMaterialService.CreateAsync(string, int, string, string, string, bool)` missing param — backend/Application/IMaterialService.cs:12 [detected]

## Read before you change it

- `CreateAsync` — backend/Application/MaterialService.cs:13-32 [detected]
- `MaterialService` — backend/Application/MaterialService.cs:5-36 [detected]
- `PublishAsync` — backend/Application/IEventPublisher.cs:5 [detected]
- `AddAsync` — backend/Domain/IMaterialRepository.cs:6 [detected]
- `IEventPublisher` — backend/Application/IEventPublisher.cs:3-6 [detected]
- `IMaterialRepository` — backend/Domain/IMaterialRepository.cs:3-7 [detected]
- `CreateAsync` — backend/Application/IMaterialService.cs:12 [detected]
- `Created` — backend/Application/MaterialService.cs:8 [detected]
- `Material` — backend/Domain/Material.cs:6-10 [detected]
