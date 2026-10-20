# Coding-agent context and MCP

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Context for coding agents

```bash
docwizz generate .                                    # once, and after every change
cp templates/AGENTS.md ./AGENTS.md                    # or append it to the agent file you already have
```

Coding agents (Claude Code, OpenCode, Qwen Code, Cline, Aider) can read the generated docs instead of exploring the
code file by file, which is where local models with small context windows drift on long runs.
[templates/AGENTS.md](../../templates/AGENTS.md) is a section for `AGENTS.md`, `CLAUDE.md`, `.clinerules` or
`CONVENTIONS.md`. It tells the agent:

- **what to read first:** `docs/index.md`, `docs/solutions/<solution>.md`, `docs/projects/<project>.md`, then
  `docs/modules/<module>.md` and `docs/architecture.md` for the folders it will touch (folder path with `/` → `-`);
- **how much to trust it:** detected facts over `(inferred)` ones; 🤖 marks a draft, never a fact;
- **that the docs are navigation:** read the real code before changing it, starting at the `file:line` links;
- **when it is done:** `docwizz check . --since <base> --format json` reports no new problems, `docwizz diff` has been
  reviewed, and `docwizz generate .` has refreshed the docs.

Most useful in practice: a module page's *Flows through this module* answers "what does this endpoint touch" in one
read; `api.md` maps a route to its handler's `file:line` and frontend callers; *Documentation gaps* is the to-do list.
Generated pages are written for people, though: a large page can crowd out an agent's context. For a budgeted slice,
use `docwizz context` or the MCP server.

#### A context package per target

```bash
docwizz context . --for Fixture.Application.MaterialService.CreateAsync   # a symbol (id or qualified name)
docwizz context . --for backend/Api/MaterialController.cs                 # a file
docwizz context . --for backend/Infrastructure                            # a folder (module)
docwizz context . --for "POST /api/materials" --budget 2000 --format json # an endpoint, as data
```

`context` writes the slice of the model that matters for one target within a token budget (default 6000, estimated as
characters / 4). Sections in priority order, cut from the end when the budget runs out:

1. header: repository, scanned commit, budget, a warning when the model is stale;
2. the target: kind, signature, visibility, layer, `file:line`, documentation and its sources, complexity, parameters,
   responses, exceptions, members;
3. neighbours one hop away: callers, callees, injections, implementations, HTTP callers;
4. further neighbours up to `--hops` (default 2, at most 4), signature and location only;
5. request flows through it, external systems with their certainty, configuration keys it reads (names only);
6. linked tests, documentation gaps and architecture findings around it;
7. *Read before you change it*: the `file:line` of the target and of the code across its edges.

- The header says what was cut. Every line ends with its provenance: `[detected]`, `[inferred]`, `[human]` or
  `[ai-drafted]`. AI drafts are left out unless you pass `--include-ai`. Output is byte-identical for the same inputs.
- `--for` resolves as: exact id (`cs:Fixture.Domain.Material`), qualified name with or without parameters, file,
  folder, then `VERB /route`. Ambiguous or unknown targets exit 1 with the candidates on stderr: it never guesses.
- It reads `docs/.docwizz/model.json` when present, else scans. A model from another commit, or older than the source
  files, is reported as stale. No network, no build, no AI. `--format json` is compact, since whitespace costs tokens.
- `--requirements` adds a *Requirements* section right after the target: the same bounded selection a model gets
  (`docwizz requirements import` first), each line `[history]`: what was asked for then, not proof of what the code
  does. Per symbol only; without the flag the package is unchanged.

#### A read-only MCP server

```bash
docwizz mcp .                  # stdio; the agent's harness starts it
docwizz mcp . --auto-rescan    # rescan when the model is stale instead of only saying so
```

`mcp` serves the code model over the [Model Context Protocol](https://modelcontextprotocol.io) on stdio:

| Tool | Answers |
|---|---|
| `find_symbol(query, kind?)` | symbols, endpoints and modules by name: exact, then prefix, then substring |
| `context(target, hops?, budget?, include_ai?, requirements?)` | the same package as `docwizz context`, as JSON |
| `callers(id)` / `callees(id)` | incoming / outgoing `calls`, `injects` and `http` edges of a symbol and its members |
| `trace_endpoint(verb, route)` | the request flow down to databases and external systems, and the frontend callers |
| `module_summary(path)` | a folder's symbols, layer, endpoints, dependencies both ways, gap and finding counts |
| `impact(ref?)` | `docwizz diff` against a ref (default `HEAD`: the uncommitted change) |
| `gaps(path?)` / `violations(path?)` | documentation gaps (critical first) / architecture violations and cycles |
| `check(since?)` | the quality gate as JSON; with `since`, only what the change introduces |
| `requirements_for_symbol(id)` | `docwizz requirements show --for` as JSON: confirmed and candidate work items, with commits and lines |
| `symbols_for_requirement(work_item)` | the symbols one work item is linked to, current code first |
| `rescan()` | scan the code again, in memory |

It is **read-only**: stdio only, no network listener, nothing written. Every answer carries a `model` object with the
scanned commit, `HEAD`, and `stale: true` with reasons when they differ. Answers take a `budget` in tokens (default
4000); long lists are paged (`offset`, `limit`, `nextOffset`) and cuts listed under `truncated`. Configuration values
never appear, only keys.

Register it with your agent (replace the path with your build of docwizz):

```bash
claude mcp add docwizz -- dotnet /opt/docwizz/DocWizz.dll mcp .     # Claude Code
```

```jsonc
// OpenCode: opencode.json
{ "mcp": { "docwizz": { "type": "local", "command": ["dotnet", "/opt/docwizz/DocWizz.dll", "mcp", "."], "enabled": true } } }

// Cline: cline_mcp_settings.json
{ "mcpServers": { "docwizz": { "command": "dotnet", "args": ["/opt/docwizz/DocWizz.dll", "mcp", "/path/to/repo"] } } }
```
