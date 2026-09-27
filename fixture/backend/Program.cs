using Fixture.Application;
using Fixture.Domain;
using Fixture.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IMaterialService, MaterialService>();
builder.Services.AddScoped<IMaterialRepository, SqlMaterialRepository>();
builder.Services.AddControllers();
builder.Services.AddHostedService<MaterialCleanup>();
builder.Services.AddHttpClient<ErpClient>(c => c.BaseAddress = new Uri("https://erp.example.com/"));
builder.Services.AddStackExchangeRedisCache(o => { });
builder.Services.AddHttpClient<WarehouseClient>(c => c.BaseAddress = new Uri(builder.Configuration["Warehouse:BaseUrl"]!));
builder.Services.Configure<MaterialOptions>(builder.Configuration.GetSection("Materials"));
var featureFlag = Environment.GetEnvironmentVariable("MATERIALS__BETA");
var auditEndpoint = Environment.GetEnvironmentVariable("AUDIT_ENDPOINT");

var app = builder.Build();
app.UseHttpsRedirection();
app.UseMiddleware<TimingMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => "ok").Produces<string>(200);
app.Run();

// Places an order for materials.
app.MapPost("/orders", async (Fixture.Infrastructure.AppDbContext db) => { if (db is null) return; await Task.CompletedTask; });
app.MapMethods("/api/materials/{id}", ["PATCH"], (int id, bool? notify, Fixture.Api.RenameMaterialRequest change, CancellationToken ct) => id);

var admin = app.MapGroup("/admin").RequireAuthorization();
admin.MapDelete("/cache", () => Results.NoContent());
