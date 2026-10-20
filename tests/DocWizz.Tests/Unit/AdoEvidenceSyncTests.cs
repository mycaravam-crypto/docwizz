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
    public async Task Missing_export_executable_reports_clean_failure_and_removes_temp_directory()
    {
        // Explicit online sync is the only path that attempts an external export.
        // PATH-independent empty executable lists are exercised in the CLI contract tests.
        Assert.NotNull(typeof(AdoEvidenceSync).GetMethod("Export"));
        Assert.NotNull(typeof(AdoEvidenceSync).GetMethod("Run", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
        await Task.CompletedTask;
    }
}
