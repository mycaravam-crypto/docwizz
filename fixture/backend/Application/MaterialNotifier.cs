namespace Fixture.Application;

/// <summary>Writes a line for every created material.</summary>
public class MaterialNotifier
{
    /// <summary>Starts listening to <paramref name="service"/>.</summary>
    public void Attach(MaterialService service) => service.Created += id => Console.WriteLine(id);
}
