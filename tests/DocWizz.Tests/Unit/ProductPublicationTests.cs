namespace DocWizz.Tests.Unit;

public class ProductPublicationTests
{
    [Fact]
    public void Release_paths_are_confined_to_the_project_root()
    {
        var root = Directory.CreateTempSubdirectory("docwizz-publish-confined-").FullName;
        try
        {
            var within = Path.Combine(root, "templates", "arch.yaml");
            Assert.Equal(Path.GetFullPath(within), ProductPublication.ConfinedPath(root, within));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, "../escape.yaml"));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, Path.Combine(Path.GetTempPath(), "escape.yaml")));
            Assert.Throws<ArgumentException>(() => ProductPublication.ConfinedPath(root, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
