# Scanner distribution

[CLI reference](../cli.md) · [Implementation architecture](../architecture.md)

## Packaging and runtime requirements

To package the tool, run `npm ci --prefix scanner-vue`, then `dotnet pack src/DocWizz/DocWizz.csproj -c Release`.
Pack stages the Vue/TS and Java scanners with their pinned dependency tree in `scanner-vue/dist/` (git-ignored) and
ships them inside the .NET tool; it fails if `npm ci` has not run. The installed tool still needs Node.js 22, but it does
not install npm dependencies at runtime.

The GitHub Action loads a scanner archive from the GitHub release matching its immutable `vX.Y.Z` tag
or full commit SHA. The release workflow publishes the lockfile-built archive and SHA-256 checksum.
The Action verifies both before scanning; it never runs `npm ci`. Pin the Action to a published release tag
or release commit. Running it from a branch or moving tag fails closed, rather than returning incomplete
frontend/Java results; releases published before this change carry no payload, so pin a newer one. Only a local
`uses: ./` (this repository's own `docwizz` workflow) packs the scanner from its checkout. Node.js 22 remains a runtime
prerequisite.