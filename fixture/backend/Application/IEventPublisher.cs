namespace Fixture.Application;

public interface IEventPublisher
{
    Task PublishAsync(string name, int id);
}
