using System.Security.Cryptography;
using System.Text.Json;

namespace DocWizz.Tests.Unit;

public class ProductPublicationTests
{
    [Fact]
    public void Release_paths_are_confined_to_the_project_root()
    {
        var root = Directory.CreateTempSubdirectory("docwizz-publish-confined-").FullName;
        try
        {
            var within = Path.Join(root, "templates", "arch.yaml");
            Assert.Equal(Path.GetFullPath(within), ProductPublication.ConfinedPath(root, within));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, "../escape.yaml"));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, Path.Join(Path.GetTempPath(), "escape.yaml")));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("1.0/other")]
    [InlineData(@"1.0\other")]
    [InlineData("1.0 release")]
    [InlineData(".hidden")]
    [InlineData("..")]
    public void Publication_version_is_safe_for_output_file_names(string version)
    {
        Assert.Throws<ArgumentException>(() => ProductPublication.ValidateReleaseVersion(version));
    }

    [Fact]
    public void Publication_version_accepts_semantic_and_prefixed_identifiers()
    {
        ProductPublication.ValidateReleaseVersion("1.0.0-rc.1");
        ProductPublication.ValidateReleaseVersion("v1_2026");
    }

    // Synthetic independent signers: a valid pair must bind the same exact payload bytes
    // to the original document, evidence report, control metadata and repository revision.
    [Fact]
    public void Signed_payload_requires_independent_signers_and_unchanged_sources()
    {
        using var quality = RSA.Create(2048);
        using var security = RSA.Create(2048);
        var dir = Directory.CreateTempSubdirectory("docwizz-signing-");
        try
        {
            var root = dir.FullName;
            var metadata = """
                schema: 1
                document_id: TEST-001
                title: Testdokument
                version: "1.0"
                author: CI
                owner: Team
                created: 2026-10-19
                classification: Intern
                distribution: Intern
                retention: Testaufbewahrung
                changes:
                  - version: "1.0"
                    date: 2026-10-19
                    author: CI
                    description: Teststand
                approvals: []
                """;
            var control = ControlledDocument.Parse(metadata);
            var draft = Path.Join(root, "draft.md");
            var evidence = Path.Join(root, "draft.evidence.json");
            var controlFile = Path.Join(root, "document-control.yaml");
            var policyFile = Path.Join(root, "policy.json");
            var approvalFile = Path.Join(root, "signed.json");
            var qualityKey = Path.Join(root, "quality.pem");
            var securityKey = Path.Join(root, "security.pem");
            File.WriteAllText(draft, "Generated draft");
            File.WriteAllText(evidence, "{\"generator\":\"docwizz product\"}");
            File.WriteAllText(controlFile, metadata);
            File.WriteAllText(qualityKey, quality.ExportSubjectPublicKeyInfoPem());
            File.WriteAllText(securityKey, security.ExportSubjectPublicKeyInfoPem());
            var policy = new { schema = 1, keys = new[] {
                new { id = "q", role = "quality", public_key = "quality.pem" },
                new { id = "s", role = "security", public_key = "security.pem" }
            }};
            File.WriteAllText(policyFile, JsonSerializer.Serialize(policy));
            var commit = new string('a', 40);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new {
                status = "approved", document_id = control.Id, version = control.Version,
                draft_sha256 = ProductPublication.Sha256(draft),
                evidence_sha256 = ProductPublication.Sha256(evidence),
                control_sha256 = control.Hash, source_commit = commit
            });
            var approved = new { schema = 1, payload_base64 = Convert.ToBase64String(payload),
                signatures = new[] {
                    new { role = "quality", key_id = "q", signature_base64 = Convert.ToBase64String(
                        quality.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
                    new { role = "security", key_id = "s", signature_base64 = Convert.ToBase64String(
                        security.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }
                }
            };
            File.WriteAllText(approvalFile, JsonSerializer.Serialize(approved));
            ProductPublication.Verify(root, policyFile, approvalFile, control, draft, evidence, commit);

            File.AppendAllText(draft, "\nModified after signing");
            Assert.Contains("does not match", Assert.Throws<ArgumentException>(() =>
                ProductPublication.Verify(root, policyFile, approvalFile, control, draft, evidence, commit)).Message);
            File.WriteAllText(draft, "Generated draft");

            // The trust policy and signed JSON must not be loaded from outside the chosen repository.
            Assert.Contains("escapes", Assert.Throws<ArgumentException>(() =>
                ProductPublication.Verify(root, Path.Join(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "outside-policy.json"),
                    approvalFile, control, draft, evidence, commit)).Message);
            Assert.Contains("escapes", Assert.Throws<ArgumentException>(() =>
                ProductPublication.Verify(root, policyFile, approvalFile, control,
                    Path.Join(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), "outside-draft.md"), evidence, commit)).Message);

            var invalid = new { schema = 1, payload_base64 = "%%%invalid%%%", signatures = approved.signatures };
            File.WriteAllText(approvalFile, JsonSerializer.Serialize(invalid));
            Assert.Throws<FormatException>(() =>
                ProductPublication.Verify(root, policyFile, approvalFile, control, draft, evidence, commit));

            File.WriteAllText(approvalFile, JsonSerializer.Serialize(approved));
            File.WriteAllText(policyFile, JsonSerializer.Serialize(new { schema = 1, keys = new[] {
                new { id = "q", role = "quality", public_key = "quality.pem" },
                new { id = "s", role = "security", public_key = "quality.pem" }
            }}));
            Assert.Contains("independent public keys", Assert.Throws<ArgumentException>(() =>
                ProductPublication.Verify(root, policyFile, approvalFile, control, draft, evidence, commit)).Message);
        }
        finally { dir.Delete(recursive: true); }
    }
}
