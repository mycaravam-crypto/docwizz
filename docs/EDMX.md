# EDMX reader (issue #262, P0)

Run `docwizz edmx . fixture-legacy/Shop/Models/Legacy.edmx --format json` to read an EF Designer EDMX file without building or executing repository code.

The `docwizz.edmx/v1` report describes conceptual/storage entities and sets, properties/keys, associations/roles/multiplicities, navigation properties, inheritance metadata, basic entity/association mappings and function imports. Every fact carries a repository-relative source location. `findings` contains missing references, invalid XML and unsupported mapping diagnostics. JSON output is sorted deterministically; the command exits with code 1 for error findings.

The file must be below the chosen repository root. Symbolic links in its path, DTDs, external entities and files over 20 MiB are rejected. Document/element counts are bounded. The parser is read-only and never calls external services.

**P0 scope limit:** XML extraction is not a complete Entity Framework runtime mapping interpreter. Complex/conditional mappings are reported as partial. Code links, graph metrics, domain inference, HTML/Markdown pages and opt-in local AI will be added in later increments of #262 using #187's existing evidence/domain layer.
