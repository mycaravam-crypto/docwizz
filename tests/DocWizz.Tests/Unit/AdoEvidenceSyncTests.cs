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

}
