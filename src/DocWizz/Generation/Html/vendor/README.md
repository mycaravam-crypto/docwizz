# Vendored files

Shipped inside docwizz so generated HTML renders its diagrams without a network connection.

| File | Source | Version | License |
|---|---|---|---|
| `mermaid.min.js` | npm `mermaid`, `dist/mermaid.min.js` | 11.4.1 (tarball `sha512-Mb01JT/x6CKDWaxigwfZYuYmDZ6xtrNwNlidKZwkSrDaY9n90tdrJTV5Umk+wP1fZscGptmKFXHsXMDEVZ+Q6A==`) | MIT, `mermaid.LICENSE` |

To upgrade: `npm pack mermaid@<version>`, check the tarball against `npm view mermaid@<version> dist.integrity`,
copy `package/dist/mermaid.min.js` and `package/LICENSE` here, and update `Html.MermaidVersion` and this table.
