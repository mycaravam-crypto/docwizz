using Microsoft.EntityFrameworkCore;
using Shop.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddDbContext<ShopContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Shop")));
var app = builder.Build();
app.MapControllers();
app.Run();
