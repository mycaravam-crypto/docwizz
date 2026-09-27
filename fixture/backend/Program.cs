using Fixture.Application;
using Fixture.Domain;
using Fixture.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IMaterialService, MaterialService>();
builder.Services.AddScoped<IMaterialRepository, SqlMaterialRepository>();
builder.Services.AddControllers();
builder.Services.AddHttpClient<ErpClient>(c => c.BaseAddress = new Uri("https://erp.example.com/"));
builder.Services.AddStackExchangeRedisCache(o => { });

var app = builder.Build();
app.MapControllers();
app.MapGet("/health", () => "ok");
app.Run();

// Places an order for materials.
app.MapPost("/orders", async (Fixture.Infrastructure.AppDbContext db) => { if (db is null) return; await Task.CompletedTask; });
app.MapMethods("/api/materials/{id}", ["PATCH"], (int id) => id);

var admin = app.MapGroup("/admin").RequireAuthorization();
admin.MapDelete("/cache", () => Results.NoContent());
