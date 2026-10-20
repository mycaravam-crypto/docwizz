# Native ADO synchronization (Issue #278)

The first native, opt-in integration is available on this branch:

```bash
docwizz generate . --html --ado-project Shop --ado-area 'Shop\Orders' --history-since v1.0.0
```

Omit `--history-since` to import only the Area Requirements Catalog and explicit ADO test-case relationships. Without `--ado-project` and `--ado-area`, generation remains offline; both values must be given together. Native mode requires the `ado` binary, and optionally `gw`, configured on `PATH`, with ADO authentication already performed.

The native synchronizer executes external programs with an argument array (not a shell), validates the staged JSON schema and requested project/area, and imports only after the exports succeed. For historical mapping, it retrieves work-item context for all exported IDs. Temporary files use a private directory and are removed on completion.

**Limitations:** The native generate path now snapshots the existing imported evidence and restores it if a later import fails. This is rollback on failure, not atomic visibility for concurrent readers or crash-safe multi-file commit. Full provenance/origin validation, incremental remote export, comprehensive fake-executable journey tests and atomic publication for concurrent readers remains open in #278. Never publish generated documentation without reviewing work-item confidentiality.

## Alternative script

Initial, opt-in integration: `scripts/sync-ado-evidence.sh` orchestrates existing ADO/GitWizz/DocWizz CLIs without changing the offline `docwizz generate` command.

Run **from the application repository root** (requires `ado`, `docwizz`, `jq` and optionally `gw`; configure ADO auth first):

```bash
bash /path/to/docwizz/scripts/sync-ado-evidence.sh Shop 'Shop\Orders' v1.0.0
```

Omit `v1.0.0` to import the area catalog and ADO test-case relationships without Git history. With a history ref, all catalog work items are exported as individual `ado.requirement-context/v1` files before history indexing, removing manual per-ID commands.

Security: restrictive temporary file permissions (`umask 077`); temporary exports are deleted on exit; no credential values are explicitly echoed. Do not publish the HTML output without reviewing its confidentiality and hosting permissions.

Limitations of this first increment: the external CLI programs must already be installed and authenticated; the snapshot metadata and foreign origin checks still require native implementation; DocWizz's existing imports are sequential and **not atomic**. An import failure after an earlier successful import can leave mixed versions. Concurrent-reader atomicity remains open; full AC1 is **not yet satisfied**. See #278.
