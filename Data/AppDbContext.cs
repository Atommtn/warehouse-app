using Microsoft.EntityFrameworkCore;
using WarehouseApp.Models;

namespace WarehouseApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<MaterialGroup> MaterialGroups => Set<MaterialGroup>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<StockEntry> StockEntries => Set<StockEntry>();
    public DbSet<StockWithdrawal> StockWithdrawals => Set<StockWithdrawal>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Material>().HasMany(x => x.Entries).WithOne(e => e.Material).HasForeignKey(e => e.MaterialId);
        m.Entity<Material>().HasMany(x => x.Withdrawals).WithOne(w => w.Material).HasForeignKey(w => w.MaterialId);
        m.Entity<Unit>().HasMany(x => x.Materials).WithOne(mat => mat.Unit).HasForeignKey(mat => mat.UnitId);
        m.Entity<MaterialGroup>().HasMany(x => x.Materials).WithOne(mat => mat.Group).HasForeignKey(mat => mat.GroupId);
        m.Entity<Supplier>().HasMany(x => x.Materials).WithOne(mat => mat.Supplier).HasForeignKey(mat => mat.SupplierId);

        m.Entity<Material>().HasIndex(x => x.Code).IsUnique();
        m.Entity<Material>().Property(x => x.PricePerUnit).HasColumnType("decimal(18,2)");
        m.Entity<Material>().Property(x => x.MinStockLevel).HasColumnType("decimal(18,2)");
        m.Entity<Material>().Property(x => x.CurrentStock).HasColumnType("decimal(18,2)");
        m.Entity<StockEntry>().Property(x => x.Quantity).HasColumnType("decimal(18,2)");
        m.Entity<StockEntry>().Property(x => x.PricePerUnit).HasColumnType("decimal(18,2)");
        m.Entity<StockWithdrawal>().Property(x => x.Quantity).HasColumnType("decimal(18,2)");
    }
}

public static class DbInitializer
{
    public static void Initialize(AppDbContext context)
    {
        context.Database.EnsureCreated();
        if (!context.Users.Any())
        {
            context.Users.AddRange(
                new User { Username="admin",      FullName="مدیر سیستم",    PasswordHash=BCrypt.Net.BCrypt.HashPassword("admin123"),   Role=UserRole.Admin },
                new User { Username="manager1",   FullName="مدیر انبار",    PasswordHash=BCrypt.Net.BCrypt.HashPassword("manager123"), Role=UserRole.Manager },
                new User { Username="operator1",  FullName="اپراتور انبار", PasswordHash=BCrypt.Net.BCrypt.HashPassword("op123"),      Role=UserRole.Operator },
                new User { Username="attendant1", FullName="متصدی انبار",   PasswordHash=BCrypt.Net.BCrypt.HashPassword("att123"),     Role=UserRole.Attendant },
                new User { Username="viewer1",    FullName="بازبین",         PasswordHash=BCrypt.Net.BCrypt.HashPassword("view123"),    Role=UserRole.Viewer }
            );
            context.SaveChanges();
        }
    }
}
