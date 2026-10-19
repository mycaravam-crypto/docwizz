// Metadata-only bridge from the existing deployment descriptor discovery into the product evidence model.
// No deployment parser, runtime assumptions, secret values, network access or new AI workflow.
static class ProductDeploymentFacts
{
    public static CodeModel Add(CodeModel model, string root, IEnumerable<string> scopedFiles)
    {
        var found = scopedFiles
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(rel => !Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith("../", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Where(rel => DeploymentDescriptors.Is(root, rel))
            .Order(StringComparer.Ordinal)
            .Select(rel => new Node($"deploy:{rel}", "deployment-descriptor", Kind(rel), rel, 1))
            .ToList();
        // These observations belong to this product scan only; the ordinary repository CodeModel is unchanged.
        return model with { Nodes = [.. model.Nodes, .. found] };
    }

    // A filename or descriptor type proves only that a declaration exists in the selected repository scope.
    // In particular, CI configuration does not prove that any job ran or that any deployment succeeded.
    static string Kind(string rel)
    {
        var name = Path.GetFileName(rel);
        if (rel.StartsWith(".github/workflows/", StringComparison.Ordinal) ||
            name is "Jenkinsfile" or "azure-pipelines.yml" or ".gitlab-ci.yml" or "bitbucket-pipelines.yml" ||
            rel == ".circleci/config.yml") return "CI-Deskriptor";
        if (DeploymentDescriptors.IsCompose(rel)) return "Compose-Deskriptor";
        if (name.StartsWith("Dockerfile", StringComparison.Ordinal) || name.EndsWith(".Dockerfile", StringComparison.Ordinal)) return "Dockerfile";
        if (name.EndsWith(".bicep", StringComparison.Ordinal) || name.EndsWith(".tf", StringComparison.Ordinal)) return "IaC-Deskriptor";
        if (name == "Chart.yaml") return "Helm-Chart";
        return "Deployment-Deskriptor";
    }
}
