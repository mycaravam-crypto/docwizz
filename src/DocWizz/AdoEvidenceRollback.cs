// Restore previously imported evidence when a later import fails. Does not make multi-file updates atomic
// to concurrent readers; publication must be serialized separately.
static class AdoEvidenceRollback : IDisposable
{
    readonly List<(string Target, string Backup, bool Existed)> entries = [];
    readonly string staging = Directory.CreateTempSubdirectory("docwizz-evidence-backup-").FullName;
    bool committed;

    public AdoEvidenceRollback(string repository)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var paths = new[] { Path.Join(repository, "docs", ".docwizz"), Path.Join(repository, "docs", "requirements") };
        for (int i = 0; i < paths.Length; i++)
        {
            string source = paths[i];
            string backup = Path.Join(staging, i.ToString());
            bool existed = Directory.Exists(source);
            if (existed) CopyDirectory(source, backup);
            entries.Add((source, backup, existed));
        }
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Join(destination, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Join(destination, Path.GetFileName(dir)));
    }

    public void Commit() => committed = true;

    public void Dispose()
    {
        try
        {
            if (!committed)
                foreach (var item in entries)
                {
                    if (Directory.Exists(item.Target)) Directory.Delete(item.Target, recursive: true);
                    if (item.Existed) CopyDirectory(item.Backup, item.Target);
                }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
