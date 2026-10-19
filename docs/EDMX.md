# EDMX analysis — Issue #262, P0 + P1

The EF6 EDMX reader is offline, read-only and AI-free. It never builds, restores or executes user code.

## Usage

P0 (unchanged: CSDL/SSDL/MSL inventory):

```bash
docwizz edmx . fixture-legacy/Shop/Models/Legacy.edmx --format json
```

P1 (separate versioned, deterministic technical graph):

```bash
docwizz edmx . fixture-legacy/Shop/Models/GraphGolden.edmx --graph --format json
docwizz edmx . fixture-legacy/Shop/Models/GraphGolden.edmx --graph --degree-weight 2 --fan-in-weight 0 --fan-out-weight 0 --inheritance-weight 0 --format json
```

With `--graph` the output has schema `docwizz.edmx-graph/v1`; without it the existing `docwizz.edmx/v1` JSON is unchanged.

## Deterministic metric contract

Only **conceptual CSDL entities** become graph nodes. SSDL storage and MSL join tables never automatically become conceptual/domain entities.

| Field | Meaning |
|---|---|
| EntityCount | Number of conceptual entity declarations in the parser report |
| AssociationCount | Number of conceptual association declarations, including unresolved ones |
| Associations | Resolved binary relationships with two distinct roles, valid multiplicities and valid conceptual entity targets; every edge carries association and end source lines |
| Cardinality | `1:1` if both ends are `1`/`0..1`; `1:n` if exactly one is `*`; `n:m` if both are `*` |
| Degree | Number of incident resolved associations; parallel edges count separately and a self-edge counts once |
| FanIn / FanOut | Number of incoming/outgoing **explicit validated navigation properties**; associations alone are not directed |
| InheritanceLinks | Number of resolved derived/base incidences, counted at both endpoints |
| RelationshipCount | Resolved associations plus resolved inheritance, excluding duplicate navigation projections |
| Technical score | `Degree × degreeWeight + FanIn × fanInWeight + FanOut × fanOutWeight + InheritanceLinks × inheritanceWeight` |
| CentralEntities | All entities tied for highest strictly positive score, sorted by ID |
| IsolatedEntities | Entities with zero degree, fan-in, fan-out and inheritance links |
| Clusters | Undirected connected components over associations and inheritance, including singletons; numbered by lexicographic first member |

Each weight defaults to 1; valid values are decimals between 0 and 100 in invariant notation (e.g. `1.5`). Weight flags require `--graph` and at least one weight must be positive. These values are **technical**, not business importance, priority, DDD bounded contexts, or a statement of data flow.

Association direction in serialized JSON is chosen by sorting conceptual entity IDs/role names. It does **not** mean the association causes a call or has a domain direction. Navigation records are directed only when their owner, association and roles resolve exactly. Missing, invalid or unsupported references are reported as findings; the graph never fabricates an unresolved edge. Every emitted edge has an EDMX source location.

## Acceptance / golden evidence

`fixture-legacy/Shop/Models/GraphGolden.edmx` contains a classic EF6 EDMX model with:

- 1:1 customer/address, 1:n customer/order and n:m order/tag relationships (including a physical join table in SSDL/MSL);
- PremiumCustomer inheritance, an isolated Audit entity, bidirectional navigations, and table/column mappings;
- explicit counts, connectivity, fan metrics and score expectations in `tests/DocWizz.Tests/Golden/edmx-graph-v1.txt`.

Component tests verify the golden output, JSON reproducibility, source evidence, invalid references and customized scoring. The P0 parser prohibits DTD/XXE and external entities, imposes XML/file limits, confines paths to the selected root and rejects symlinks. On error findings, the CLI exits with code 1.

**Scope boundary:** P1 does not yet link EDMX to generated EF classes, DbContext, services or MVC controllers, and does not integrate into generated Markdown/HTML. P2 will hand these proven graph facts to the existing evidence-based domain analysis in #187, without building a competing domain engine.


## P2: evidence-backed EDMX ↔ C# links

Run `docwizz edmx tests/fixtures/edmx-code GraphGolden.edmx --links --format json` for the separate `docwizz.edmx-code-links/v1` output. `--links` and `--graph` are mutually exclusive; P0 and P1 output remains backward-compatible.

P2 links **only exact, fully qualified CSDL/C# type IDs** (e.g. `Legacy.Customer` ↔ `cs:Legacy.Customer`), scanner-observed `DbContext.DbSet<T>` persistence, and directed `calls`, `accesses`, `injects` and `creates` C# edges from MVC actions to DbContext. Links and code-hop endpoints carry both EDMX and source-declaration file/line evidence. Declaring a method is not evidence of the invocation's precise line; code-hop evidence is explicitly named `fromDeclaration` / `toDeclaration`. A call chain only appears when code-scanner edges support it. Maximum path length is 8; traversal is bounded.

Unresolved types, persistence and controller paths are `unknown`; **no guessed** method calls, convention routes, semantic business processes, DDD bounded contexts or cross-namespace name matches. With no explicit attribute route, a matching classic MVC action has `routeStatus: unknown`. When project scope is configured, only selected scoped C# files may participate; an EDMX excluded by that scope is rejected. Non-AI mode performs no build, NuGet restore, external request or user-code execution.

`domainEvidence` reuses **`DomainAreas.Group(CodeModel)` from #187** to label code-method links as `inferred`. There is no additional area-grouping algorithm or automatic `confirmed` state. Generated EF classes are matched as C# types, not claimed to be generated if no generator provenance is present. MSL/SSDL tables are not treated as independently confirmed domain entities. The link schema is an evidence handoff; Markdown/HTML navigation and AI remain P3.

The isolated test-only directory `tests/fixtures/edmx-code` intentionally contains its own EDMX copy and C# model, so CLI integration tests exercise actual source links without contaminating the legacy fixture used by existing end-to-end checks.
