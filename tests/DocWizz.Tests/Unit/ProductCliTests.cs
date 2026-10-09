namespace DocWizz.Tests.Unit;

public class ProductCliTests
{
    [Fact]
    public void Product_command_is_registered_without_ai_options()
    {
        var request = Cli.Parse(["product", ".", "templates/vmodell-xt/sw-architecture.yaml"]);
        Assert.Null(request.Error);
        Assert.Equal("product", request.Command);
        Assert.Equal(3, request.Positional.Count);
        Assert.Contains("product", Cli.Commands.Keys);
        Assert.NotNull(Cli.Parse(["product", ".", "template.yaml", "--ai"]).Error);
    }
}
