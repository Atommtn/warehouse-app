using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? "Server=sql2019,1433;Database=WarehouseDB;User Id=sa;Password=Mohammad1111;TrustServerCertificate=True;";

// استفاده از DbContextFactory برای جلوگیری از مشکل concurrent access
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<WarehouseService>();

var app = builder.Build();

// Initialize DB
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    DbInitializer.Initialize(db);
}

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
