using System.IO.Enumeration;

// Deployment descriptors among the repository's files: Dockerfiles, compose, Kubernetes, IaC, Helm, CI pipelines.
// The deployment view reads them; project scoping keeps them in scope.
static class DeploymentDescriptors
{
    public static readonly string[] Globs = ["Dockerfile*", "*.Dockerfile", "docker-compose*.yml", "docker-compose*.yaml",
        "compose*.yml", "compose*.yaml", "*.bicep", "*.tf", "Chart.yaml", "azure-pipelines.yml", ".gitlab-ci.yml", "Jenkinsfile",
        "bitbucket-pipelines.yml", ".circleci/config.yml",
        ".github/workflows/*.yml", ".github/workflows/*.yaml", "Procfile", "fly.toml", "vercel.json", "netlify.toml"];

    public static bool IsCompose(string f) => Path.GetFileName(f).Contains("compose");

    // A deployment descriptor by name, or a Kubernetes manifest by content. `rel` is relative to `root`, `/`-separated.
    public static bool Is(string root, string rel) =>
        Globs.Any(g => FileSystemName.MatchesSimpleExpression(g, g.Contains('/') ? rel : Path.GetFileName(rel)))
        || IsKubernetes(Path.Combine(root, rel));

    // Any other YAML file with a top-level apiVersion and kind.
    static bool IsKubernetes(string f)
    {
        if (!(f.EndsWith(".yaml") || f.EndsWith(".yml")) || IsCompose(f)) return false;
        var head = File.ReadLines(f).Take(50).ToList();
        return head.Any(l => l.StartsWith("apiVersion:")) && head.Any(l => l.StartsWith("kind:"));
    }
}
