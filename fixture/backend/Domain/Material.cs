using Fixture.Infrastructure;

namespace Fixture.Domain;

/// <summary>A material that can be requested.</summary>
public record Material(int Id, string Name, int Quantity, string Unit, string Location, int Priority)
{
    // ARCH-001 violation: Domain depends on Infrastructure
    public void Save(SqlMaterialRepository repo) => repo.AddAsync(this);
}
