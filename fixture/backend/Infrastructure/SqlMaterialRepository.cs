using Fixture.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fixture.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Material> Materials => Set<Material>();
}

public class SqlMaterialRepository(AppDbContext db) : IMaterialRepository
{
    public Task<Material?> FindAsync(int id) => db.Materials.FindAsync(id).AsTask();

    public async Task<int> AddAsync(Material material)
    {
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        return material.Id;
    }
}
