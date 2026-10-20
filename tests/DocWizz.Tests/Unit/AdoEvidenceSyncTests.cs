using System.Text.Json;

namespace DocWizz.Tests.Unit;

public class AdoEvidenceSyncTests
{
    [Fact]
    public async Task Preflight_rejects_unknown_history_ref_before_export()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("docwizz-preflight-test-").FullName;
        try
        {
            string ado = Path.Join(root, "ado.sh");
            File.WriteAllText(ado, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(ado, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                ArgumentList = { "-C", root, "init" },
                UseShellExecute = false
            })!;
            git.WaitForExit();
            Assert.Equal(0, git.ExitCode);
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
                () => AdoEvidenceSync.Preflight(root, "not-a-tag-or-branch", ado, ado));
            Assert.Contains("--history-since", error.Message);
            await AdoEvidenceSync.Preflight(root, null, ado, ado);
            using (var commit = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                ArgumentList = { "-C", root, "-c", "user.name=DocWizz Test", "-c",
                    "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "preflight test" },
                UseShellExecute = false
            })!)
            {
                commit.WaitForExit();
                Assert.Equal(0, commit.ExitCode);
            }
            await AdoEvidenceSync.Preflight(root, "HEAD", ado, ado);
            await AdoEvidenceSync.Preflight(root, "HEAD~0", ado, ado);
            await Assert.ThrowsAsync<ArgumentException>(
                () => AdoEvidenceSync.Preflight(root, "-unsafe", ado, ado));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => AdoEvidenceSync.Preflight(root, null, Path.Join(root, "missing-ado"), ado));
            string other = Directory.CreateTempSubdirectory("docwizz-preflight-no-git-").FullName;
            try
            {
                await Assert.ThrowsAsync<ArgumentException>(
                    () => AdoEvidenceSync.Preflight(other, "v1.0.4", ado, ado));
            }
            finally { Directory.Delete(other, recursive: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            var stages = new List<string>();
            using (var staged = await AdoEvidenceSync.Export("Shop", "Shop\\Orders", null, success, success, stages.Add))
            {
                Assert.True(File.Exists(staged.Catalog));
                Assert.True(File.Exists(staged.Tests));
                Assert.False(staged.WithHistory);
            }
            Assert.Contains(stages, stage => stage.Contains("area work items"));
            Assert.Contains(stages, stage => stage.Contains("test-case relationships"));

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
            stages.Clear();
            using (var history = await AdoEvidenceSync.Export("Shop", "Shop\\Orders", "v1.0.0", success, gitwizz, stages.Add))
            {
                Assert.True(history.WithHistory);
                Assert.True(File.Exists(history.History));
                Assert.True(File.Exists(Path.Combine(history.WorkItems, "4711.json")));
            }
            Assert.Contains(stages, stage => stage.Contains("GitWizz: exporting history"));
            Assert.Contains(stages, stage => stage.Contains("contexts 1/1"));

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
    public void GitWizz_dll_override_and_fallback_preserve_command_arguments()
    {
        string root = Directory.CreateTempSubdirectory("docwizz-gitwizz-dll-").FullName;
        try
        {
            string assembly = Path.Combine(root, "git wizz.dll");
            File.WriteAllText(assembly, "");
            var command = AdoEvidenceSync.GitWizzCommand("gw", assembly);
            Assert.Equal("dotnet", command.Executable);
            Assert.Equal([Path.GetFullPath(assembly)], command.Prefix);

            var fallback = AdoEvidenceSync.GitWizzCommand("gw", null);
            Assert.Equal("gw", fallback.Executable);
            Assert.Empty(fallback.Prefix);
            Assert.Throws<ArgumentException>(() =>
                AdoEvidenceSync.GitWizzCommand("gw", Path.Combine(root, "missing.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
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
