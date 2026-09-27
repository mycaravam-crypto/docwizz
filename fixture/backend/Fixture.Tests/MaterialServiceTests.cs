using Fixture.Application;

namespace Fixture.Tests;

// Test code needs no docs; its calls only link tests to the code under test.
public class MaterialServiceTests
{
    public void CreateAsync_RejectsEmptyName_AndOtherComplexCases(int a, int b, int c, int d)
    {
        if (a > 0 && b > 0 || c > 0 && d > 0) { if (a > b) { if (c > d) { } } }
    }

    public async Task CreateAsync_Creates(MaterialService service) =>
        await service.CreateAsync("bolt", 1, "pcs", "A1", "me", false);
}
