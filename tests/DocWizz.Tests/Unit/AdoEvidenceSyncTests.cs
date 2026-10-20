using System.Text.Json;

namespace DocWizz.Tests.Unit;

public class AdoEvidenceSyncTests
{
    [Fact]
    public void Staged_directory_is_private_and_removed_when_disposed()
    {
        string path;
        using (var staged = new AdoEvidenceSync.Staged(false))
        {
            path = staged.DirectoryPath;
            Assert.True(Directory.Exists(path));
            Assert.False(staged.WithHistory);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.Equal(UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            }
        }
        Assert.False(Directory.Exists(path));
    }


    [Fact]
    public async Task Export_validates_complete_snapshots_and_rejects_missing_fields()
    {
        if (OperatingSystem.IsWindows()) return;
        var folder = Directory.CreateTempSubdirectory("docwizz-ado-sync-test-").FullName;
        try
        {
            var success = Path.Combine(folder, "success.sh");
            File.WriteAllText(success, """
                #!/usr/bin/env bash
                set -euo pipefail
                if [[ "$1 $2" == "workitem export" ]]; then
                  while (( $# )); do
                    if [[ "$1" == "--output" ]]; then
                      printf '%s\n' '{"schema":"ado.workitem-catalog/v1","complete":true,"project":"Shop","area":"Shop\\Orders","items":[{"id":4711}]}' > "$2"
                      exit 0
                    fi
                    shift
                  done
                elif [[ "$1 $2" == "testplan trace-export" ]]; then
                  while (( $# )); do
                    if [[ "$1" == "--output" ]]; then
                      printf '%s\n' '{"format":"ado.test-traceability/v2","project":"Shop","area":"Shop\\Orders","generatedAt":"2026-10-20T08:00:00Z","plans":[],"testCases":[],"assignments":[],"requirements":[]}' > "$2"
                      exit 0
                    fi
                    shift
                  done
                elif [[ "$1 $2" == "workitem context" ]]; then
                  echo '{"schema":"ado.requirement-context/v1","id":4711}'
                  exit 0
                fi
                exit 3
                """);
            File.SetUnixFileMode(success, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var staged = await AdoEvidenceSync.Export("Shop", "Shop\\Orders", null, success, success))
            {
                Assert.True(File.Exists(staged.Catalog));
                Assert.True(File.Exists(staged.Tests));
                Assert.False(staged.WithHistory);
            }

            var gitwizz = Path.Combine(folder, "gw.sh");
            File.WriteAllText(gitwizz, """
                #!/usr/bin/env bash
                set -euo pipefail
                while (( $# )); do
                  if [[ "$1" == "-o" ]]; then
                    echo '{"schema":"gitwizz.history/v2"}' > "$2"
                    exit 0
                  fi
                  shift
                done
                exit 3
                """);
            File.SetUnixFileMode(gitwizz, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var history = await AdoEvidenceSync.Export("Shop", "Shop\\Orders", "v1.0.0", success, gitwizz))
            {
                Assert.True(history.WithHistory);
                Assert.True(File.Exists(history.History));
                Assert.True(File.Exists(Path.Combine(history.WorkItems, "4711.json")));
            }

            var invalid = Path.Combine(folder, "invalid.sh");
            File.WriteAllText(invalid, """
                #!/usr/bin/env bash
                set -euo pipefail
                for arg in "$@"; do
                  if [[ "$arg" == "--output" ]]; then
                    break
                  fi
                done
                shift $(( $# - 1 ))
                printf '%s\n' '{"schema":"ado.workitem-catalog/v1"}' > "$1"
                """);
            File.SetUnixFileMode(invalid, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => AdoEvidenceSync.Export("Shop", "Shop\\Orders", null, invalid, invalid));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }


    [Fact]
    public void Failed_import_restores_original_evidence_and_successful_import_keeps_changes()
    {
        string root = Directory.CreateTempSubdirectory("docwizz-rollback-test-").FullName;
        try
        {
            string folder = Path.Join(root, "docs", ".docwizz");
            Directory.CreateDirectory(folder);
            string file = Path.Join(folder, "requirements.json");
            File.WriteAllText(file, "original");
            using (new AdoEvidenceRollback(root))
            {
                File.WriteAllText(file, "partial");
                File.WriteAllText(Path.Join(folder, "new.json"), "unexpected");
            }
            Assert.Equal("original", File.ReadAllText(file));
            Assert.False(File.Exists(Path.Join(folder, "new.json")));

            using (var transaction = new AdoEvidenceRollback(root))
            {
                File.WriteAllText(file, "committed");
                transaction.Commit();
            }
            Assert.Equal("committed", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
