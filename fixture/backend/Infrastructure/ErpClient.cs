namespace Fixture.Infrastructure;

/// <summary>Reads stock levels from the ERP system.</summary>
public class ErpClient(HttpClient http)
{
    /// <summary>Current stock for an article.</summary>
    public Task<string> StockAsync(string sku) => http.GetStringAsync($"stock/{sku}");
}

/// <summary>Books material movements in the warehouse system.</summary>
public class WarehouseClient(HttpClient http)
{
    /// <summary>Reserves stock for a material request.</summary>
    public Task ReserveAsync(int materialId) => http.PostAsync($"reservations/{materialId}", null);
}
