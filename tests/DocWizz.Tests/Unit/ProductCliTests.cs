namespace DocWizz.Tests.Unit;

public class ProductCliTests
{
    [Fact]
    public void Product_command_takes_a_context_file_and_opt_in_ai()
    {
        var request = Cli.Parse(["product", ".", "templates/vmodell-xt/sw-architecture.yaml"]);
        Assert.Null(request.Error);
        Assert.Equal("product", request.Command);
        Assert.Equal(3, request.Positional.Count);
        Assert.Null(Cli.Parse(["product", ".", "template.yaml", "--ai"]).Error);   // P1: opt-in local synthesis (#93)
        Assert.NotNull(Cli.Parse(["product", ".", "template.yaml", "--html"]).Error);
        var withContext = Cli.Parse(["product", ".", "template.yaml", "--context", "project.yaml"]);
        Assert.Null(withContext.Error);
        Assert.Equal("project.yaml", withContext.Options["context"]);
        Assert.Contains("needs a value", Cli.Parse(["product", ".", "template.yaml", "--context"]).Error);
    }
}
