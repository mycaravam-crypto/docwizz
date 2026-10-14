# Domain discovery: recursive, evidence-led architecture exploration (MVP)

The **read-only domain crawler** starts at one code symbol / endpoint, or at one imported Azure DevOps work
item, and walks the selected, directed relationships in DocWizz's existing code model. It optionally
attaches requirements from the local historical index. The crawler **does not infer** business
capabilities or DDD bounded contexts from method names; it exposes candidate terms and the evidence
required for human review.

## Quick start

From the application checkout, with installed GitWizz, ado-devops and DocWizz:

```bash
# The history range must cover the period you want to understand.
gitwizz history export --since v1.0.0 --provider azure-devops --commit-lookups 300 -o history.json

# Export the work items referenced in the history (one file per ID).
mkdir -p work-items
ado workitem context 4711 > work-items/4711.json

# Native gitwizz.history/v2 import; no conversion adapter.
docwizz requirements import . history.json work-items/

# Bottom-up from an API entry point or a named class/method:
docwizz domain discover . --seed "POST /api/orders" --depth 4 --limit 150 --format json

# Top-down from one historic requirement:
docwizz domain discover . --work-item 4711 --depth 3 --limit 100 --format json

# Reviewable Markdown with a Mermaid graph:
docwizz domain discover . docs/orders-domain.md --seed "POST /api/orders" --format md

# Readable console output, still no AI or network:
docwizz domain discover . --seed "Shop.OrderService.Place" --depth 3
```

When no requirements index exists, **code-seeded discovery works** but returns
`requirementsIndex.status = unavailable`; work-item seeding requires an imported index.
All queries are offline; only the separate GitWizz/ADO export commands contact Azure DevOps.
The crawler writes to stdout, or to the file named after `<dir>` (`domain discover . orders.md --format md`).
In a file, the Markdown links every location relative to the file, with GitHub line anchors (`#L12-L20`).
An existing file that is not a DocWizz domain report is never overwritten.

## Discovery semantics

- Uses a BFS with deterministic ordering, cycle detection, max depth 0–8 (default 3)
  and 1–500 reached nodes (default 100). No arbitrary reverse caller walk.
- The node budget goes to behaviour first: `creates` edges and edges into `record`/`struct`/`enum`
  types (DTO construction) are deferred, together with everything behind them, until no behaviour
  path within the depth bound is left. `depth` is the hop count along the path that admitted the node.
- Traverses code-model calls, injections, accesses, creation, interface implementations,
  registrations, inheritance, persistence, events, HTTP, rendering, routes and external
  systems. Type-to-member `contains` edges are traversed **only** from a seed that is a container type:
  a class reached through a field or constructor is listed, but its other members are not.
- For interface implementations, can follow an `implements` edge backwards to reach a
  concrete implementation; **the exported edge still preserves its original direction**.
- File locations and evidence come from the existing scanner, never guessed by the model.
  Unresolved calls, runtime reflection and dynamically loaded implementations may be absent.
- Work-item seeding uses `RequirementResolver.Symbols` to find **current** related code symbols.
  When none remain, the report explains that; deleted/renamed historical symbols are not invented
  as current implementation sites.
- Per reached code symbol, `RequirementResolver.For` attaches directly linked commit
  requirements as `confirmed` and PR-only / message-mention links as `candidate`.
  Maximum 10 linked work items per symbol and three criteria per item (further details remain in
  `docwizz requirements show`). History freshness and partial linkages are warnings.
- `candidateTerms[]` contains only **historical Work Item titles** and their associated
  current code symbols. The classification is explicitly `historical-requirement-title`:
  titles are *not* automatically validated capabilities or bounded contexts.
- The output is stably ordered JSON (`docwizz.domain-discovery/v1`), Markdown (`--format md`), or a
  human-readable console summary. The Markdown has three parts:
  - **As-is**: the scanner's code facts, as a Mermaid graph (up to 80 symbols) and a symbol table.
  - **As-intended**: confirmed links (a commit that changed the symbol names the work item) whose code has not
    changed since, with their acceptance criteria.
  - **Unclear**: every other link, each with its reason (PR or message only, code changed since, work item not
    exported, newer work-item revision), and the reached methods that have no linked work item.

  Contradictions between code and intent are not claimed: that needs a reader (or a model) comparing the two. The same code model, history index, seed, commit and bounds produce the same output.

## Candidate business areas

`docwizz domain areas . [--format json]` is the first layer of a separate domain model over the unchanged code model
(`docwizz.domain-areas/v1`). Production methods are grouped by folder; then two kinds of evidence move a method:

- **Delegation:** a method in an outer folder (one no other folder calls into: controllers, jobs) that calls into exactly
  one other type joins that type's area (`CheckoutController.Track` → shipping, `CardPayments.Charge` → billing).
- **Implementation:** an interface method joins its implementations' area when they all agree.

Every member names its reason (`folder`, `delegates to …`, `implemented by …`) and every area is `inferred`. On the
benchmark's hand labels this reaches pair precision 0.906 / recall 1.0, against 0.685 / 0.771 by namespace
([bench/domain/README.md](../bench/domain/README.md)). Its known miss: a job that only reads one service (`ReportsJob.Run`)
is put with that service.

## What it does not claim

- A method changed under a linked work item does **not** necessarily implement the requirement.
- Adjacent methods and the current implementation can contradict historical business intent.
- A candidate term is not a confirmed business capability, business rule or bounded context.
- `--depth` is a maximum graph hop distance, not a count of LLM reasoning iterations.
- There are no automatic DevOps calls, no AI prompts, no edits and no architectural compliance claims.

## Next iterations

1. Extend the domain model beyond inferred areas (`domain areas` exists): business rules, actors and process steps;
   stable human review/confirmation.
2. Bidirectional code/history enrichment with incremental fingerprints.
3. Mermaid domain/process views and optional local-only LLM wording over the cited facts (As-is /
   As-intended / Unclear Markdown exists: `--format md`).
4. Domain-level gold labels for semantic accuracy and reviewer corrections (the traversal benchmark exists:
   [bench/domain/README.md](../bench/domain/README.md)).

See the parent implementation task in [GitHub issue #177](https://github.com/mycaravam-crypto/docwizz/issues/177).
