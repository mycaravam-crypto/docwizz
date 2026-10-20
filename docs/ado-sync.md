# One-command ADO evidence integration (Issue #278)

Initial, opt-in integration: `scripts/sync-ado-evidence.sh` orchestrates existing ADO/GitWizz/DocWizz CLIs without changing the offline `docwizz generate` command.

Run **from the application repository root** (requires `ado`, `docwizz`, `jq` and optionally `gw`; configure ADO auth first):

```bash
bash /path/to/docwizz/scripts/sync-ado-evidence.sh Shop 'Shop\Orders' v1.0.0
```

Omit `v1.0.0` to import the area catalog and ADO test-case relationships without Git history. With a history ref, all catalog work items are exported as individual `ado.requirement-context/v1` files before history indexing, removing manual per-ID commands.

Security: restrictive temporary file permissions (`umask 077`); temporary exports are deleted on exit; no credential values are explicitly echoed. Do not publish the HTML output without reviewing its confidentiality and hosting permissions.

Limitations of this first increment: the external CLI programs must already be installed and authenticated; the snapshot metadata and foreign origin checks still require native implementation; DocWizz's existing imports are sequential and **not atomic**. An import failure after an earlier successful import can leave mixed versions. AC3 and full AC1 are **not yet satisfied**. See #278.
