using Microsoft.EntityFrameworkCore;
using WarehouseApp.Models;

namespace WarehouseApp.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<StockEntry> StockEntries => Set<StockEntry>();
    public DbSet<StockWithdrawal> StockWithdrawals => Set<StockWithdrawal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Material>()
            .HasMany(m => m.Entries).WithOne(e => e.Material).HasForeignKey(e => e.MaterialId);
        modelBuilder.Entity<Material>()
            .HasMany(m => m.Withdrawals).WithOne(w => w.Material).HasForeignKey(w => w.MaterialId);
        modelBuilder.Entity<Supplier>()
            .HasMany(s => s.Materials).WithOne(m => m.Supplier).HasForeignKey(m => m.SupplierId);

        modelBuilder.Entity<Material>().HasIndex(m => m.Code).IsUnique();
        modelBuilder.Entity<Material>().Property(m => m.PricePerUnit).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<Material>().Property(m => m.MinStockLevel).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<Material>().Property(m => m.CurrentStock).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<StockEntry>().Property(e => e.Quantity).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<StockEntry>().Property(e => e.PricePerUnit).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<StockWithdrawal>().Property(w => w.Quantity).HasColumnType("decimal(18,2)");
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
                new User { Username="admin",     FullName="مدیر سیستم",    PasswordHash=BCrypt.Net.BCrypt.HashPassword("admin123"),   Role=UserRole.Admin },
                new User { Username="manager1",  FullName="مدیر انبار",    PasswordHash=BCrypt.Net.BCrypt.HashPassword("manager123"), Role=UserRole.Manager },
                new User { Username="operator1", FullName="اپراتور انبار", PasswordHash=BCrypt.Net.BCrypt.HashPassword("op123"),      Role=UserRole.Operator },
                new User { Username="attendant1",FullName="متصدی انبار",  PasswordHash=BCrypt.Net.BCrypt.HashPassword("att123"),     Role=UserRole.Attendant },
                new User { Username="viewer1",   FullName="بازبین",        PasswordHash=BCrypt.Net.BCrypt.HashPassword("view123"),    Role=UserRole.Viewer }
            );
            context.SaveChanges();
        }

        if (!context.Suppliers.Any())
        {
            context.Suppliers.AddRange(
                new Supplier { Name="شرکت آرمان تجارت",     ContactPerson="علی رضایی",    Phone="021-12345678", Email="info@arman.com" },
                new Supplier { Name="تأمین‌کننده پارسیان", ContactPerson="محمد احمدی",   Phone="021-87654321", Email="info@parsian.com" }
            );
            context.SaveChanges();
        }

        if (!context.Materials.Any())
        {
            var sup1 = context.Suppliers.First();
            context.Materials.AddRange(
                new Material { Code="MAT-001", Name="کاکائو", Unit="کیلوگرم", PricePerUnit=250000, MinStockLevel=10, CurrentStock=8,  SupplierId=sup1.Id },
                new Material { Code="MAT-002", Name="شکر",    Unit="کیلوگرم", PricePerUnit=45000,  MinStockLevel=20, CurrentStock=35, SupplierId=sup1.Id },
                new Material { Code="MAT-003", Name="روغن",   Unit="لیتر",    PricePerUnit=80000,  MinStockLevel=15, CurrentStock=5,  SupplierId=sup1.Id }
            );
            context.SaveChanges();
        }
    }
}
