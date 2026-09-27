using Fixture.Application;
using Fixture.Domain;
using Fixture.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IMaterialService, MaterialService>();
builder.Services.AddScoped<IMaterialRepository, SqlMaterialRepository>();
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.MapGet("/health", () => "ok");
app.Run();
