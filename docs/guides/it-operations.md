# IT operations concept

[Documentation home](../README.md) · [Product documents](product-documents.md)

The IT operations template drafts the technical basis of an operations concept (German: *Betriebskonzept*) from the
code model and sourced statements. The result is a draft, not an approved operations manual or an ISO conformity
statement. The template is in German and produces a German document.

```bash
docwizz product /path/to/app /path/to/docwizz/templates/it-operations/betriebskonzept.yaml
docwizz product check /path/to/app /path/to/docwizz/templates/it-operations/betriebskonzept.yaml --format json

# with sourced project statements and an optional local model:
docwizz product /path/to/app /path/to/docwizz/templates/it-operations/betriebskonzept.yaml --context project.yaml --ai
```

It writes `docs/products/it-betriebskonzept.md` and `it-betriebskonzept.evidence.json`. Without `--ai` the draft is
fully offline and reproducible. The template uses `document_type: it-operations` instead of the V-Modell XT metadata
(`xt_variant`, `xt_version`, `tailoring`).

## What is filled from the code

| Section | Source | Limit |
|---|---|---|
| System structure | code model nodes and their locations | not an intended business model |
| Runtime and projects | project file, role, target framework | no evidence of the real operating systems or hosts |
| Interfaces | static code dependencies | no statement about runtime connectivity |
| Configuration | key names and where they are read | **no values, secrets or URL hosts** |
| Deployment and CI descriptors | path and type of the files the deployment scanner recognized (Dockerfile, Compose, Kubernetes, Helm, IaC, CI) | no service details, configuration values or run results |
| Environments, deployment and rollback, monitoring, backup, incidents, roles, SLA/RTO/RPO | sourced statements only (`--context`) | always `OFFEN` without a source |

Each code-filled section lists at most 40 entries, so lists are not necessarily complete. The `deployment` section
stays `OFFEN` without a human source even when descriptors are found. `product check` reports the open required
sections as `PROD-001` warnings, and flags the evidence report as stale when descriptor files are added or removed;
changes to the contents of an existing descriptor are not detected.

`--ai` words only sections with evidence, and every sentence must cite it. A model can't determine RTO/RPO or
organizational rules that no source states.

Not covered: detailed evaluation of Docker, Kubernetes or CI contents (services, images, jobs, ports, environment),
whether backups succeed, real operating metrics, or decisions about operations.
