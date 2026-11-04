using System.Security.Cryptography;

namespace DocWizz.Tests.Unit;

public sealed class JourneyEvidenceTests
{
    [Fact]
    public void Imports_validated_screenshots_and_renders_reviewable_guide()
    {
        var root = Directory.CreateTempSubdirectory("docwizz-journeys-");
        try
        {
            var bundle = Path.Combine(root.FullName, "wi-4711-case-512-a1b2c3d4e5f6");
            var pictures = Path.Combine(bundle, "screenshots");
            Directory.CreateDirectory(pictures);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlXn3cAAAAASUVORK5CYII=");
            var imagePath = Path.Combine(pictures, "step-0002.png");
            File.WriteAllBytes(imagePath, png);
            var hash = Convert.ToHexStringLower(SHA256.HashData(png));
            var manifest = $$"""
                {
                  "schema": "testwizz.ui-evidence/v1", "status": "passed",
                  "title": "Create order", "route": "/orders",
                  "source": {
                    "workItemId": 4711, "workItemRevision": 5,
                    "testCaseId": 512, "testCaseRevision": 3, "planId": 12,
                    "project": "Shop", "relation": "selected-not-verified"
                  },
                  "acceptanceCriteria": [{ "id": "AC1", "text": "Save works" }],
                  "steps": [{
                    "stepId": 2, "action": "Select New", "expected": "Form appears",
                    "result": "passed", "screenshot": "screenshots/step-0002.png",
                    "sha256": "{{hash}}"
                  }]
                }
                """;
            File.WriteAllText(Path.Combine(bundle, "manifest.json"), manifest);
            var output = Path.Combine(root.FullName, "site");
            IReadOnlyList<JourneyEvidence.Guide> imported = JourneyEvidence.Import(bundle, output);
            Assert.Single(imported);
            Assert.Single(JourneyEvidence.Import(bundle + Path.DirectorySeparatorChar, output));
            Assert.Equal(4711, imported[0].WorkItem);
            var markdown = JourneyEvidence.Markdown(imported[0]);
            Assert.Contains("Select New", markdown);
            Assert.Contains("human review required", markdown);
            Assert.Contains("selected, not independently verified", markdown);
            Assert.True(File.Exists(Path.Combine(output, "user-guides", "images",
                "wi-4711-case-512-a1b2c3d4e5f6-step-0002.png")));

            // A directly selected bundle is valid even when the enclosing directory was renamed.
            string renamed = Path.Combine(root.FullName, "renamed-evidence");
            Directory.Move(bundle, renamed);
            IReadOnlyList<JourneyEvidence.Guide> relocated = JourneyEvidence.Import(
                renamed + Path.DirectorySeparatorChar, output);
            Assert.Single(relocated);
            Assert.StartsWith("wi-4711-case-512-", relocated[0].Id);
            Directory.Move(renamed, bundle);

            // ADO test steps can omit expectedResult; importing still requires a passed browser assertion.
            File.WriteAllText(Path.Combine(bundle, "manifest.json"),
                manifest.Replace("\"expected\": \"Form appears\",", "", StringComparison.Ordinal));
            IReadOnlyList<JourneyEvidence.Guide> noExpected = JourneyEvidence.Import(bundle, output);
            Assert.Equal("", noExpected[0].Steps[0].Expected);

            JourneyEvidence.CleanupImages(output, []);
            Assert.False(File.Exists(Path.Combine(output, "user-guides", "images",
                "wi-4711-case-512-a1b2c3d4e5f6-step-0002.png")));

            File.WriteAllBytes(imagePath, [1, 2, 3, 4]);
            Assert.Throws<InvalidDataException>(() => JourneyEvidence.Import(bundle, output));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void Missing_evidence_folder_fails_instead_of_silently_generating_docs()
    {
        var unknown = Path.Combine(Path.GetTempPath(), "docwizz-missing-evidence-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => JourneyEvidence.Import(unknown, Path.GetTempPath()));
    }
}
