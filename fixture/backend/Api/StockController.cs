using Fixture.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Fixture.Api;

/// <summary>Stock levels, straight from the ERP.</summary>
[ApiController]
[Route("api/stock")]
public class StockController(ErpClient erp) : ControllerBase
{
    /// <summary>Stock for one article.</summary>
    [HttpGet("{sku}")]
    public Task<string> Get(string sku) => erp.StockAsync(sku);
}
