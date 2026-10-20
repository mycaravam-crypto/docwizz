using System.Diagnostics;
using System.Text.Json;

// Explicit online synchronization. Offline generate never invokes this component.
static class AdoEvidenceSync
{
    public sealed class Staged : IDisposable
    {
        public string DirectoryPath { get; }
        public string Catalog => Path.Join(DirectoryPath, "catalog.json");
        public string Tests => Path.Join(DirectoryPath, "tests.json");
        public string History => Path.Join(DirectoryPath, "history.json");
        public string WorkItems => Path.Join(DirectoryPath, "work-items");
        public bool WithHistory { get; }
        public AdoTestSnapshot.ImportData? ValidatedTests { get; set; }
        public Staged(bool history)
        {
            DirectoryPath = Directory.CreateTempSubdirectory("docwizz-ado-").FullName;
            WithHistory = history;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(DirectoryPath)) System.IO.Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    static async Task Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}");
        // Drain both streams to avoid a blocked child process. Do not display raw output, which may contain secrets.
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, errors);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} export failed (exit {process.ExitCode}); evidence was not imported");
    }

    // Pure selection logic ensures DLL execution and PATH fallback can be regression-tested.
    internal static (string Executable, string[] Prefix) GitWizzCommand(string executable, string? assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly)) return (executable, []);
        string file = Path.GetFullPath(assembly);
        if (!File.Exists(file))
            throw new ArgumentException("DOCWIZZ_GITWIZZ_DLL does not point to an existing file");
        return ("dotnet", [file]);
    }

    static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static JsonElement Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }

    /// <summary>Export and validate ADO area requirements, tests, and optional GitWizz history before importing any evidence.</summary>
    public static Task<Staged> Export(string project, string area, string? since, Action<string>? status = null) =>
        Export(project, area, since, "ado", "gw", status);

    internal static async Task<Staged> Export(string project, string area, string? since, string adoExecutable, string gwExecutable, Action<string>? status = null)
    {
        var result = new Staged(since is not null);
        try
        {
            status?.Invoke("ADO: exporting area work items");
            await Run(adoExecutable, "workitem", "export", "--project", project, "--area", area, "--output", result.Catalog);
            JsonElement catalog = Read(result.Catalog);
            if (catalog.GetProperty("schema").GetString() != "ado.workitem-catalog/v1" ||
                !catalog.GetProperty("complete").GetBoolean() ||
                catalog.GetProperty("project").GetString() != project || catalog.GetProperty("area").GetString() != area)
                throw new ArgumentException("Incomplete or mismatched ADO catalog");
            int[] ids = catalog.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToArray();
            if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
                throw new ArgumentException("Invalid or duplicate ADO work item ids");

            status?.Invoke("ADO: exporting test-case relationships");
            await Run(adoExecutable, "testplan", "trace-export", "--project", project, "--area", area, "--output", result.Tests);
            JsonElement tests = Read(result.Tests);
            if (tests.GetProperty("format").GetString() is not ("ado.test-traceability/v1" or "ado.test-traceability/v2") ||
                tests.GetProperty("project").GetString() != project || tests.GetProperty("area").GetString() != area)
                throw new ArgumentException("Mismatched ADO test snapshot");
            result.ValidatedTests = AdoTestSnapshot.Load(result.Tests);
            Restrict(result.Catalog);
            Restrict(result.Tests);

            if (since is not null)
            {
                status?.Invoke("GitWizz: exporting history");
                // A Git Bash alias/function named gw is not executable by Process.Start on Windows.
                // Point DOCWIZZ_GITWIZZ_DLL at the published assembly to run it without shell activation.
                var command = GitWizzCommand(gwExecutable, Environment.GetEnvironmentVariable("DOCWIZZ_GITWIZZ_DLL"));
                try
                {
                    await Run(command.Executable, [.. command.Prefix, "history", "export", "--since", since,
                        "--provider", "azure-devops", "-o", result.History]);
                }
                catch (System.ComponentModel.Win32Exception e) when (OperatingSystem.IsWindows() && command.Prefix.Length == 0)
                {
                    throw new InvalidOperationException(
                        "GitWizz is not available as a Windows executable. Git Bash aliases/functions cannot be started by .NET. " +
                        "Set DOCWIZZ_GITWIZZ_DLL to the published gitwizz.dll path, or install a native gitwizz.exe on PATH.", e);
                }
                Restrict(result.History);
                if (Read(result.History).GetProperty("schema").GetString() != "gitwizz.history/v2")
                    throw new ArgumentException("Invalid GitWizz history schema");
                System.IO.Directory.CreateDirectory(result.WorkItems);
                int processed = 0;
                foreach (var id in ids)
                {
                    status?.Invoke($"ADO: work-item contexts {processed + 1}/{ids.Length}");
                    var file = Path.Join(result.WorkItems, id + ".json");
                    // The CLI writes the context to stdout; unlike export commands it has no --output flag.
                    var psi = new ProcessStartInfo(adoExecutable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    psi.ArgumentList.Add("workitem"); psi.ArgumentList.Add("context"); psi.ArgumentList.Add(id.ToString());
                    using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ado");
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    string data = await stdout;
                    await stderr;
                    if (p.ExitCode != 0) throw new InvalidOperationException($"ADO context export failed for work item {id}");
                    File.WriteAllText(file, data);
                    Restrict(file);
                    JsonElement context = Read(file);
                    if (context.GetProperty("schema").GetString() != "ado.requirement-context/v1" ||
                        context.GetProperty("id").GetInt32() != id)
                        throw new ArgumentException($"Invalid ADO context for work item {id}");
                    processed++;
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
