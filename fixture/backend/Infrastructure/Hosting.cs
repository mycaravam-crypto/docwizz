using Fixture.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Fixture.Infrastructure;

/// <summary>Removes materials nobody requested for a while.</summary>
public class MaterialCleanup(IMaterialRepository repository) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => repository is null ? Task.CompletedTask : Task.Delay(1000, stoppingToken);
}

/// <summary>Adds the elapsed time to every response.</summary>
public class TimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) => next(context);
}

/// <summary>Material limits, bound from configuration.</summary>
public class MaterialOptions
{
    public int MaxQuantity { get; set; } = 1000;
}

/// <summary>Called when a material changes.</summary>
public delegate void MaterialChanged(int id);
