// EF6 + classic MVC reference shape without external package restore. IDs must be exact, not simple-name matches.
namespace System.Data.Entity
{
    public class DbContext { }
    public class DbSet<T> where T : class { }
}
namespace System.Web.Mvc
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
    public sealed class RouteAttribute(string value) : System.Attribute { public string Value { get; } = value; }
    public sealed class HttpGetAttribute : System.Attribute { }
    public class Controller { }
}
namespace Legacy
{
    public class Customer { public int Id { get; set; } }
    public class Order { public int Id { get; set; } }
    public class Address { public int Id { get; set; } }
    public class Audit { public int Id { get; set; } }
    public class PremiumCustomer : Customer { public int Tier { get; set; } }
    // No Legacy.Tag class: code link must remain unknown, never match another namespace's Tag.

    public class LegacyContext : System.Data.Entity.DbContext
    {
        public System.Data.Entity.DbSet<Customer> Customers { get; set; } = new();
        public System.Data.Entity.DbSet<Order> Orders { get; set; } = new();
        public System.Data.Entity.DbSet<Address> Addresses { get; set; } = new();
        public System.Data.Entity.DbSet<Audit> Audits { get; set; } = new();
    }

    public class OrderService
    {
        readonly LegacyContext _db;
        public OrderService(LegacyContext db) { _db = db; }
        public void Place() { _ = _db.Customers; }
    }

    [System.Web.Mvc.Route("orders")]
    public class OrdersController : System.Web.Mvc.Controller
    {
        readonly OrderService _service;
        public OrdersController(OrderService service) { _service = service; }
        [System.Web.Mvc.HttpGet]
        public void Index() { _service.Place(); }
        public void Unrelated() { }
    }

    // Conventional classic MVC without explicit route information: route must remain unknown.
    public class PlainController : System.Web.Mvc.Controller
    {
        readonly OrderService _service;
        public PlainController(OrderService service) { _service = service; }
        public void List() { _service.Place(); }
    }
}
namespace Unrelated
{
    public class Tag { }
}
