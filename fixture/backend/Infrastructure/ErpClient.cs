namespace Fixture.Infrastructure;

/// <summary>Reads stock levels from the ERP system.</summary>
public class ErpClient(HttpClient http)
{
    /// <summary>Current stock for an article.</summary>
    public Task<string> StockAsync(string sku) => http.GetStringAsync($"stock/{sku}");
}
