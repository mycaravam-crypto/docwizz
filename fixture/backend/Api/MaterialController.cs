using Fixture.Application;
using Microsoft.AspNetCore.Mvc;

namespace Fixture.Api;

[ApiController]
[Route("api/materials")]
public class MaterialController(IMaterialService service) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id) => Ok(await service.GetAsync(id));

    // Undocumented public endpoint (should be flagged)
    [HttpPost]
    public async Task<IActionResult> Create(string name, int quantity, string unit, string location, string requestedBy, bool urgent)
    {
        var id = await service.CreateAsync(name, quantity, unit, location, requestedBy, urgent);
        return CreatedAtAction(nameof(Get), new { id }, id);
    }
}
