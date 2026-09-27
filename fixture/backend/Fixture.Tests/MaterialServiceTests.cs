namespace Fixture.Tests;

// Excluded by default config: test code needs no docs.
public class MaterialServiceTests
{
    public void CreateAsync_RejectsEmptyName_AndOtherComplexCases(int a, int b, int c, int d)
    {
        if (a > 0 && b > 0 || c > 0 && d > 0) { if (a > b) { if (c > d) { } } }
    }
}
