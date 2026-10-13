"""The domain-crawler benchmark's repository (#188): bench/requirements_fixture.py's scripted history, plus one commit
that links no work item and adds an API layer over the seven services. That layer gives the crawler what the services
alone don't have: entry points, DTO records it constructs, an interface and its implementation, a call cycle, and a
caller (ReportsJob) the crawler must never walk back into. The requirements benchmark builds its own copy and is
unaffected. Ground truth: bench/domain/gold.json, written by hand from this code.
"""
import json
import os

import requirements_fixture as base

API = """namespace Shop.Api;

public record OrderRequest(int CustomerId, decimal Amount, string Sku, int Quantity);

public record Receipt(int OrderId, int Invoice);

public interface IPayments
{
    bool Charge(int invoiceId, decimal amount);
}

public class CardPayments : IPayments
{
    readonly Shop.Billing.InvoiceService invoices = new();

    public bool Charge(int invoiceId, decimal amount)
    {
        return invoices.Pay(invoiceId, amount);
    }
}

public class CheckoutController
{
    readonly Shop.Orders.OrderService orders = new();
    readonly Shop.Inventory.StockService stock = new();
    readonly Shop.Billing.InvoiceService invoices = new();
    readonly Shop.Notifications.Notifier notifier = new();
    readonly IPayments payments = new CardPayments();

    public Receipt Checkout(OrderRequest request)
    {
        if (!stock.Reserve(request.Sku, request.Quantity)) return new Receipt(0, 0);
        var order = orders.Place(request.CustomerId, request.Amount);
        var invoice = invoices.Issue(order);
        payments.Charge(invoice, request.Amount);
        Confirm(request.CustomerId);
        return new Receipt(order, invoice);
    }

    public void Confirm(int customerId)
    {
        notifier.Email($"customer-{customerId}@example.invalid", "Thank you for your order");
        Retry(customerId);
    }

    // A cycle: Confirm -> Retry -> Confirm.
    void Retry(int customerId)
    {
        if (customerId < 0) Confirm(-customerId);
    }

    public string Track(string code)
    {
        return new Shop.Shipping.ShippingService().Track(code);
    }
}

// Calls OrderService.Total; nothing reaches it, so a crawl from the services must never include it.
public class ReportsJob
{
    readonly Shop.Orders.OrderService orders = new();

    public decimal Run(int orderId)
    {
        return orders.Total(orderId);
    }
}
"""


def build(root, exports):
    """The base history, then the API commit, appended to the history export like any other unlinked commit."""
    history, items = base.build(root, exports)
    path = os.path.join(root, "src", "Api", "Checkout.cs")
    os.makedirs(os.path.dirname(path))
    with open(path, "w") as f:
        f.write(API)
    base.git(root, "add", "-A")
    base.git(root, "commit", "-qm", "Checkout API")
    sha = base.git(root, "rev-parse", "HEAD").strip()
    with open(history) as f:
        h = json.load(f)
    h["commits"].insert(0, {"sha": sha, "parents": [], "subject": "Checkout API", "committedAt": base.DATE.replace("Z", "+00:00"),
                            "kind": "commit", "files": base.changes(root, sha), "workItems": [],
                            "providerLinks": base.provider_links({"status": "verified", "items": []}),
                            "pullRequest": base.pull_request_ref([])})
    h["range"].update(untilSha=sha, commits=len(h["commits"]))
    with open(history, "w") as f:
        json.dump(h, f, indent=2)
    return history, items
