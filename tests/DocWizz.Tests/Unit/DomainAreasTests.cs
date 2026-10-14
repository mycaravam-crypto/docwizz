using static DocWizz.Tests.Models;

namespace DocWizz.Tests.Unit;

public class DomainAreasTests
{
    [Fact]
    public void Wrappers_and_interfaces_join_the_area_their_behaviour_lives_in()
    {
        var model = Model(
        [
            N("cs:Shop.Api.Controller.Track()", "src/Api/Controller.cs", kind: "method"),
            N("cs:Shop.Api.Controller.Checkout()", "src/Api/Controller.cs", kind: "method"),
            N("cs:Shop.Api.IPay.Charge()", "src/Api/Pay.cs", kind: "method"),
            N("cs:Shop.Api.CardPay.Charge()", "src/Api/Pay.cs", kind: "method"),
            N("cs:Shop.Shipping.Shipping.Track()", "src/Shipping/Shipping.cs", kind: "method"),
            N("cs:Shop.Billing.Invoices.Pay()", "src/Billing/Invoices.cs", kind: "method"),
            N("cs:Shop.Orders.Orders.Place()", "src/Orders/Orders.cs", kind: "method"),
            N("cs:Shop.Customers.Customers.Limit()", "src/Customers/Customers.cs", kind: "method"),
            N("cs:Shop.Tests.T.Run()", "tests/T.cs", kind: "method", tags: ["test"]),
        ],
        [
            E("cs:Shop.Api.Controller.Track()", "cs:Shop.Shipping.Shipping.Track()"),
            E("cs:Shop.Api.CardPay.Charge()", "cs:Shop.Billing.Invoices.Pay()"),
            E("cs:Shop.Api.CardPay.Charge()", "cs:Shop.Api.IPay.Charge()", "implements"),
            E("cs:Shop.Api.Controller.Checkout()", "cs:Shop.Orders.Orders.Place()"),
            E("cs:Shop.Api.Controller.Checkout()", "cs:Shop.Shipping.Shipping.Track()"),
            // Orders is called from Api, so it is not an outer layer: Place stays even though it calls one other type.
            E("cs:Shop.Orders.Orders.Place()", "cs:Shop.Customers.Customers.Limit()"),
        ]);

        var areas = DomainAreas.Group(model).Areas.ToDictionary(a => a.Name, a => a.Members.Select(m => m.Id).ToList());

        Assert.Equal(["src/Api", "src/Billing", "src/Customers", "src/Orders", "src/Shipping"], areas.Keys);
        Assert.Equal(["cs:Shop.Api.Controller.Checkout()"], areas["src/Api"]);
        Assert.Equal(["cs:Shop.Api.CardPay.Charge()", "cs:Shop.Api.IPay.Charge()", "cs:Shop.Billing.Invoices.Pay()"], areas["src/Billing"]);
        Assert.Equal(["cs:Shop.Orders.Orders.Place()"], areas["src/Orders"]);
        Assert.Equal(["cs:Shop.Api.Controller.Track()", "cs:Shop.Shipping.Shipping.Track()"], areas["src/Shipping"]);
        Assert.All(DomainAreas.Group(model).Areas, a => Assert.Equal("inferred", a.Status));
    }
}
