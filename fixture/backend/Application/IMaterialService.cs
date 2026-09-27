using Fixture.Domain;

namespace Fixture.Application;

/// <summary>Application service for managing materials.</summary>
public interface IMaterialService
{
    /// <summary>Gets a material by id.</summary>
    Task<Material?> GetAsync(int id);

    Task<int> CreateAsync(string name, int quantity, string unit, string location, string requestedBy, bool urgent);
}
