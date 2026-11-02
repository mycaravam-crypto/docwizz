# Requirements history import: performance verification (#232)

The import normally attempts a changed-file scan and retains the full historical scan as a safe fallback. The C# scanner still materializes historical C# context for semantic binding, so a selective-mode label alone **does not** prove that only changed files were materialized.

## Reproducible comparison

Use the **same checkout**, history export and work-item directory for both runs. Save the resulting `docs/.docwizz/requirements.json` outside the repository after each run; remove that generated index between runs to prevent reuse from hiding scanning cost. Run on a disposable checkout because import writes the generated index.

```sh
# On a disposable checkout with history.json and work-items/ already present:
rm -f docs/.docwizz/requirements.json
DOCWIZZ_REQUIREMENTS_PROFILE=1 DOCWIZZ_REQUIREMENTS_FORCE_FULL=1 \
  dotnet run --project src/DocWizz -- requirements import . history.json work-items/ \
  2>full-profile.log
cp docs/.docwizz/requirements.json full-requirements.json

rm -f docs/.docwizz/requirements.json
DOCWIZZ_REQUIREMENTS_PROFILE=1 \
  dotnet run --project src/DocWizz -- requirements import . history.json work-items/ \
  2>selective-profile.log
cp docs/.docwizz/requirements.json selective-requirements.json
```

Compare the generated requirement indices with a structural JSON diff before asserting equivalent evidence. In particular check commit SHA, symbol, changed lines, work-item association, relation/provenance, `confirmed`/`candidate`, and `changedSince`. A zero-exit import alone is not evidence of equivalence.

For each `docwizz requirements profile:` completion line, record `elapsed`, `changedFiles`, `materializedFiles`, `actualMode`, and `fallback`. Report the number of commits with `actualMode=selective`, `full-fallback`, and `scoped-full` / `full`; aggregate wall time separately from per-commit time. Compare on the original 170-commit dataset and state the host, SDK version, history size, work-item count and cache state. Do not derive a speedup from the earlier uncontrolled baseline of about 55 minutes at 57/170.

`DOCWIZZ_REQUIREMENTS_FORCE_FULL=1` is an **opt-in comparison switch**, not the default operational mode. `DOCWIZZ_REQUIREMENTS_PROFILE=1` enables diagnostics; neither option should change work-item confidence semantics.
