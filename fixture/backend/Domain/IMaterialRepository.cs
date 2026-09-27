namespace Fixture.Domain;

public interface IMaterialRepository
{
    Task<Material?> FindAsync(int id);
    Task<int> AddAsync(Material material);
}
