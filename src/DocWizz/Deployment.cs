using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

// Deployment: what the repository's descriptors say — compose services, Dockerfiles, Kubernetes workloads,
// infrastructure-as-code resources — and, as plainly, what they can't. Environment values are never shown, only names.
partial class Generator
{
    // A compose service or Kubernetes workload/Service.
    record DeployedUnit(string File, int Line, string Name, string? Image, string? Build, List<string> Ports, List<string> Env,
        List<string> DependsOn, List<string> Volumes);

    static readonly string[] DeploymentGlobs = ["Dockerfile*", "*.Dockerfile", "docker-compose*.yml", "docker-compose*.yaml",
        "compose*.yml", "compose*.yaml", "*.bicep", "*.tf", "Chart.yaml", "azure-pipelines.yml", ".gitlab-ci.yml",
        ".github/workflows/*.yml", ".github/workflows/*.yaml", "Procfile", "fly.toml", "vercel.json", "netlify.toml"];
    static readonly string[] Workloads = ["Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "Job", "CronJob", "Pod"];

    List<string>? deploymentFiles;
    List<string> DeploymentFiles => deploymentFiles ??= [.. FindDeploymentFiles().Order()];
    List<DeployedUnit>? composeUnits, kubernetesUnits;
    List<DeployedUnit> ComposeUnits => composeUnits ??= [.. DeploymentFiles.Where(IsCompose).SelectMany(Compose)];
    List<DeployedUnit> KubernetesUnits => kubernetesUnits ??= [.. DeploymentFiles.Where(f => !IsCompose(f) && (f.EndsWith(".yaml") || f.EndsWith(".yml"))).SelectMany(Kubernetes)];

    static bool IsCompose(string f) => Path.GetFileName(f).Contains("compose");

    // Configuration keys the deployment sets (compose `environment`, Kubernetes `env`), A__B read as A:B → where.
    ILookup<string, string>? deploymentEnv;
    ILookup<string, string> DeploymentEnv => deploymentEnv ??= ComposeUnits.Concat(KubernetesUnits)
        .SelectMany(u => u.Env.Select(e => (Key: e.Replace("__", ":").ToLowerInvariant(), Where: $"{u.Name} in {u.File}")))
        .ToLookup(x => x.Key, x => x.Where);

    string DeploymentView()
    {
        var sb = new StringBuilder("# Deployment\n\n");
        sb.AppendLine("What the repository's deployment descriptors state. Environment variables are listed by name only.\n");
        var hosts = Projects.Where(p => p.Tags?.Any(t => t.StartsWith("Microsoft.NET.Sdk.Web") || t == "npm") == true).ToList();
        if (hosts.Count > 0)
        {
            sb.AppendLine("## Deployable units (from project files)\n");
            foreach (var h in hosts) sb.AppendLine($"- {SourceLink(h.File, 0, h.Name, sub: "views")} — {(h.Tags!.Contains("npm") ? "frontend (npm)" : "ASP.NET Core web host")}");
            sb.AppendLine();
        }

        foreach (var file in ComposeUnits.GroupBy(u => u.File))
        {
            sb.AppendLine($"## Services ({SourceLink(file.Key, 0, file.Key, sub: "views")})\n");
            var deps = file.SelectMany(u => u.DependsOn.Select(d => (u.Name, d, 0))).ToList();
            if (deps.Count > 0) sb.AppendLine(Mermaid("graph LR", deps, x => x));
            UnitTable(sb, file, withBuild: true);
        }
        if (KubernetesUnits.Count > 0)
        {
            sb.AppendLine("## Kubernetes\n");
            UnitTable(sb, KubernetesUnits, withBuild: false);
        }

        var images = DeploymentFiles.Where(f => Path.GetFileName(f).StartsWith("Dockerfile") || f.EndsWith(".Dockerfile")).Select(f => (f, Dockerfile(f))).ToList();
        if (images.Count > 0)
        {
            sb.AppendLine("## Container images\n");
            foreach (var (f, (from, expose)) in images)
                sb.AppendLine($"- {SourceLink(f, 0, f, sub: "views")}: from {(from.Count > 0 ? string.Join(", ", from.Select(i => $"`{i}`")) : "—")}" +
                    (expose.Count > 0 ? $"; exposes {string.Join(", ", expose)}" : ""));
            sb.AppendLine();
        }

        var iac = DeploymentFiles.Where(f => f.EndsWith(".tf") || f.EndsWith(".bicep")).Select(f => (f, Resources(f))).Where(x => x.Item2.Count > 0).ToList();
        if (iac.Count > 0)
        {
            sb.AppendLine("## Infrastructure as code\n");
            foreach (var (f, resources) in iac)
                sb.AppendLine($"- {SourceLink(f, 0, f, sub: "views")}: {string.Join(", ", resources.GroupBy(r => r).Select(g => g.Count() > 1 ? $"`{g.Key}` ×{g.Count()}" : $"`{g.Key}`"))}");
            sb.AppendLine();
        }

        var described = ComposeUnits.Concat(KubernetesUnits).Select(u => u.File).Concat(images.Select(i => i.f)).Concat(iac.Select(i => i.f)).ToHashSet();
        var other = DeploymentFiles.Where(f => !described.Contains(f)).ToList();
        if (other.Count > 0)
            sb.AppendLine("## Other descriptors\n\n" + string.Join("\n", other.Select(f => $"- {SourceLink(f, 0, f, sub: "views")}")) + "\n");
        if (DeploymentFiles.Count == 0) sb.AppendLine("No deployment descriptors found (Dockerfile, compose, Kubernetes, Bicep/Terraform, Helm, CI pipelines).\n");

        sb.AppendLine(ConfigurationSection());
        sb.AppendLine("## Not derivable from the repository\n\n" +
            "- which environments exist and where they are hosted, beyond the configuration files and descriptors above\n" +
            "- values of secrets and of environment variables set outside the repository\n" +
            "- scaling, availability and network topology not declared in the descriptors\n" +
            "- who operates the system and how it is released\n");
        sb.AppendLine(HumanNote("deployment", "None of this can be read from the code."));
        return sb.ToString();
    }

    void UnitTable(StringBuilder sb, IEnumerable<DeployedUnit> units, bool withBuild)
    {
        sb.AppendLine($"| {(withBuild ? "Service" : "Workload")} | Image{(withBuild ? " / build" : "")} | Ports | Environment | Depends on | Volumes | Runs |\n|---|---|---|---|---|---|---|");
        foreach (var u in units)
            sb.AppendLine($"| {SourceLink(u.File, u.Line, u.Name, sub: "views")} | {(u.Image is not null ? $"`{u.Image}`" : u.Build is not null ? $"build `{u.Build}`" : "—")} | " +
                $"{Esc(string.Join(", ", u.Ports))} | {Esc(string.Join(", ", u.Env))} | {string.Join(", ", u.DependsOn)} | {Esc(string.Join(", ", u.Volumes))} | {Runs(u)} |");
        sb.AppendLine();
    }

    // What a unit runs: the project its build context contains, or the known system its image is.
    string Runs(DeployedUnit u)
    {
        if (u.Build is not null)
        {
            var dir = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(u.File) ?? "", u.Build))).Replace('\\', '/');
            var projects = Projects.Where(p => dir == "." || p.File.StartsWith(dir.TrimEnd('/') + "/")).Select(p => $"project `{p.Name}`").ToList();
            if (projects.Count > 0) return string.Join(", ", projects);
        }
        if (u.Image is not null && Externals.ByImage(u.Image) is { } k)
            return nodes.TryGetValue($"ext:{k.Key}", out var ext)
                ? $"{k.Name} — used by the code ({Externals.Certainty(ext)})"
                : $"{k.Name} — not referenced by the scanned code";
        return "";
    }

    YamlStream? LoadYaml(string rel)
    {
        try
        {
            var ys = new YamlStream();
            ys.Load(new StringReader(File.ReadAllText(Path.Combine(root, rel))));
            return ys;
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or IOException) { return null; } // e.g. Helm templates
    }

    static YamlNode? Get(YamlNode? n, string key) => n is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;
    static string? Str(YamlNode? n) => (n as YamlScalarNode)?.Value;
    static IEnumerable<YamlNode> Items(YamlNode? n) => n is YamlSequenceNode s ? s.Children : [];

    // Names only: `- KEY=value`, `KEY: value` or Kubernetes `- name: KEY`.
    static List<string> EnvNames(YamlNode? n) => n switch
    {
        YamlMappingNode m => [.. m.Children.Keys.Select(k => Str(k)!)],
        YamlSequenceNode s => [.. s.Children.Select(c => Str(c)?.Split('=')[0] ?? Str(Get(c, "name"))).OfType<string>()],
        _ => [],
    };

    static List<string> Scalars(YamlNode? n) => n switch
    {
        YamlMappingNode m => [.. m.Children.Keys.Select(k => Str(k)!)],   // depends_on: { db: { condition: .. } }
        YamlSequenceNode s => [.. s.Children.Select(c => Str(c) ?? $"{Str(Get(c, "published")) ?? Str(Get(c, "source"))}:{Str(Get(c, "target"))}")],
        YamlScalarNode sc => [sc.Value!],
        _ => [],
    };

    IEnumerable<DeployedUnit> Compose(string rel)
    {
        foreach (var doc in LoadYaml(rel)?.Documents ?? [])
            if (Get(doc.RootNode, "services") is YamlMappingNode services)
                foreach (var (k, v) in services.Children)
                    yield return new(rel, (int)k.Start.Line, Str(k)!, Str(Get(v, "image")), Str(Get(v, "build")) ?? Str(Get(Get(v, "build"), "context")),
                        Scalars(Get(v, "ports")), EnvNames(Get(v, "environment")), Scalars(Get(v, "depends_on")), Scalars(Get(v, "volumes")));
    }

    IEnumerable<DeployedUnit> Kubernetes(string rel)
    {
        foreach (var doc in LoadYaml(rel)?.Documents ?? [])
        {
            var r = doc.RootNode;
            var (kind, name) = (Str(Get(r, "kind")), Str(Get(Get(r, "metadata"), "name")));
            if (kind is null || name is null) continue;
            if (Workloads.Contains(kind))
            {
                var pod = kind switch
                {
                    "Pod" => Get(r, "spec"),
                    "CronJob" => Get(Get(Get(Get(Get(r, "spec"), "jobTemplate"), "spec"), "template"), "spec"),
                    _ => Get(Get(Get(r, "spec"), "template"), "spec"),
                };
                var containers = Items(Get(pod, "containers")).ToList();
                yield return new(rel, (int)r.Start.Line, $"{kind}/{name}", string.Join(", ", containers.Select(c => Str(Get(c, "image"))).OfType<string>()) is { Length: > 0 } img ? img : null,
                    null, [.. containers.SelectMany(c => Items(Get(c, "ports"))).Select(p => Str(Get(p, "containerPort"))).OfType<string>()],
                    [.. containers.SelectMany(c => EnvNames(Get(c, "env")))], [], [.. Items(Get(pod, "volumes")).Select(v => Str(Get(v, "name"))).OfType<string>()]);
            }
            else if (kind == "Service")
                yield return new(rel, (int)r.Start.Line, $"Service/{name}", null, null,
                    [.. Items(Get(Get(r, "spec"), "ports")).Select(p => $"{Str(Get(p, "port"))}→{Str(Get(p, "targetPort")) ?? Str(Get(p, "port"))}")], [], [], []);
        }
    }

    (List<string> From, List<string> Expose) Dockerfile(string rel)
    {
        var lines = File.ReadAllLines(Path.Combine(root, rel));
        return ([.. lines.Select(l => Regex.Match(l, @"^\s*FROM\s+(?:--\S+\s+)*(\S+)", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()],
            [.. lines.Select(l => Regex.Match(l, @"^\s*EXPOSE\s+(.+)$", RegexOptions.IgnoreCase)).Where(m => m.Success).SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))]);
    }

    // Terraform `resource "azurerm_postgresql_flexible_server" "db"`, Bicep `resource db 'Microsoft.DBforPostgreSQL/flexibleServers@2022-12-01'`.
    List<string> Resources(string rel) => [.. File.ReadLines(Path.Combine(root, rel))
        .Select(l => Regex.Match(l, @"^\s*resource\s+(?:""([^""]+)""|\w+\s+'([^'@]+)@)")).Where(m => m.Success)
        .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)];

    IEnumerable<string> FindDeploymentFiles()
    {
        string[] skip = [".git", "node_modules", "bin", "obj", "dist"];
        var stack = new Stack<string>([root]);
        while (stack.TryPop(out var dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (DeploymentGlobs.Any(g => FileSystemName.MatchesSimpleExpression(g, g.Contains('/') ? rel : Path.GetFileName(f))) || IsKubernetes(f))
                    yield return rel;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (!skip.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    // Any other YAML file with a top-level apiVersion and kind.
    static bool IsKubernetes(string f)
    {
        if (!(f.EndsWith(".yaml") || f.EndsWith(".yml")) || IsCompose(f)) return false;
        var head = File.ReadLines(f).Take(50).ToList();
        return head.Any(l => l.StartsWith("apiVersion:")) && head.Any(l => l.StartsWith("kind:"));
    }
}
