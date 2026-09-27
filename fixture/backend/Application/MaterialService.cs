using Fixture.Domain;

namespace Fixture.Application;

public class MaterialService(IMaterialRepository repository, IEventPublisher events) : IMaterialService
{
    /// <summary>Raised after a material is created, with its id.</summary>
    public event Action<int>? Created;

    public Task<Material?> GetAsync(int id) => repository.FindAsync(id);

    // Complex, public, side effects (DB + event), undocumented: HIGH requirement
    public async Task<int> CreateAsync(string name, int quantity, string unit, string location, string requestedBy, bool urgent)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");
        if (quantity <= 0) throw new ArgumentException("quantity must be positive");
        if (unit != "pcs" && unit != "kg" && unit != "m") throw new ArgumentException("bad unit");

        var priority = urgent ? 1 : quantity > 100 ? 2 : 3;
        if (location.StartsWith("EXT") && priority > 1) priority--;

        var material = new Material(0, name, quantity, unit, location, priority);
        var id = await repository.AddAsync(material);

        if (urgent || priority == 1)
            await events.PublishAsync("material-urgent", id);
        else
            await events.PublishAsync("material-created", id);
        Created?.Invoke(id);

        return id;
    }

    // Trivial: should NOT be flagged
    public int Add(int a, int b) => a + b;
}
