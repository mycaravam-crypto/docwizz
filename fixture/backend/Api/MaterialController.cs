using Fixture.Application;
using Fixture.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Fixture.Api;

[ApiController]
[Route("api/materials")]
public class MaterialController(IMaterialService service) : ControllerBase
{
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Material?>> Get(int id) => Ok(await service.GetAsync(id));

    // Undocumented public endpoint (should be flagged)
    [HttpPost]
    public async Task<IActionResult> Create(string name, int quantity, string unit, string location, string requestedBy, bool urgent)
    {
        var id = await service.CreateAsync(name, quantity, unit, location, requestedBy, urgent);
        return CreatedAtAction(nameof(Get), new { id }, id);
    }

    /// <summary>Renames a material.</summary>
    /// <param name="id">Material id.</param>
    /// <param name="request">The new name.</param>
    [Authorize]
    [HttpPut("{id}")]
    public async Task<ActionResult<Material>> Rename(int id, [FromBody] RenameMaterialRequest request) =>
        await service.GetAsync(id) ?? throw new KeyNotFoundException();
}

/// <summary>Request body for renaming a material.</summary>
public record RenameMaterialRequest(string Name);
