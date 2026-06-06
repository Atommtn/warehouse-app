using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

public class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public User? CurrentUser { get; private set; }
    public event Action? OnAuthChanged;

    public AuthService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public bool IsAuthenticated => CurrentUser != null;
    public bool IsAdmin      => CurrentUser?.Role == UserRole.Admin;
    public bool IsManager    => CurrentUser?.Role is UserRole.Admin or UserRole.Manager;
    public bool IsOperator   => CurrentUser?.Role is UserRole.Admin or UserRole.Manager or UserRole.Operator;
    public bool IsAttendant  => CurrentUser?.Role is UserRole.Admin or UserRole.Manager or UserRole.Operator or UserRole.Attendant;
    public bool CanApprove   => CurrentUser?.Role is UserRole.Admin or UserRole.Manager;
    public bool CanViewPrice => CurrentUser?.Role is UserRole.Admin or UserRole.Manager;

    public async Task<bool> LoginAsync(string username, string password)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) return false;
        CurrentUser = user;
        OnAuthChanged?.Invoke();
        return true;
    }

    public void Logout() { CurrentUser = null; OnAuthChanged?.Invoke(); }
}

public class WarehouseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public WarehouseService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    // ── Users ──
    public async Task<List<User>> GetUsersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Users.OrderBy(u => u.Username).ToListAsync();
    }
    public async Task AddUserAsync(User u)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.Add(u); await db.SaveChangesAsync();
    }
    public async Task UpdateUserAsync(User u)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.Update(u); await db.SaveChangesAsync();
    }
    public async Task<bool> UsernameExistsAsync(string username, int? excludeId = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Users.AnyAsync(u => u.Username == username && u.Id != excludeId);
    }

    // ── Suppliers ──
    public async Task<List<Supplier>> GetSuppliersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Suppliers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
    }
    public async Task<Supplier?> GetSupplierAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Suppliers.FindAsync(id);
    }
    public async Task AddSupplierAsync(Supplier s)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Suppliers.Add(s); await db.SaveChangesAsync();
    }
    public async Task UpdateSupplierAsync(Supplier s)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Suppliers.Update(s); await db.SaveChangesAsync();
    }
    public async Task DeleteSupplierAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var s = await db.Suppliers.FindAsync(id);
        if (s != null) { s.IsActive = false; await db.SaveChangesAsync(); }
    }

    // ── Materials ──
    public async Task<List<Material>> GetMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.Materials.Include(m => m.Supplier).Where(m => m.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(m => m.Name.Contains(search) || m.Code.Contains(search));
        return await q.OrderBy(m => m.Code).ToListAsync();
    }
    public async Task<Material?> GetMaterialAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Materials.Include(m => m.Supplier).FirstOrDefaultAsync(m => m.Id == id);
    }
    public async Task<bool> CodeExistsAsync(string code, int? excludeId = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Materials.AnyAsync(m => m.Code == code && m.Id != excludeId);
    }
    public async Task AddMaterialAsync(Material m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Materials.Add(m); await db.SaveChangesAsync();
    }
    public async Task UpdateMaterialAsync(Material m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Materials.Update(m); await db.SaveChangesAsync();
    }
    public async Task DeleteMaterialAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var m = await db.Materials.FindAsync(id);
        if (m != null) { m.IsActive = false; await db.SaveChangesAsync(); }
    }

    // ── Stock Entries ──
    public async Task AddStockEntryAsync(StockEntry entry)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.StockEntries.Add(entry);
        var mat = await db.Materials.FindAsync(entry.MaterialId);
        if (mat != null) { mat.CurrentStock += entry.Quantity; mat.PricePerUnit = entry.PricePerUnit; }
        await db.SaveChangesAsync();
    }
    public async Task<List<StockEntry>> GetEntriesAsync(int? materialId = null, string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockEntries.Include(e => e.Material).AsQueryable();
        if (materialId.HasValue) q = q.Where(e => e.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(e => e.Material!.Name.Contains(search) || e.Material.Code.Contains(search));
        return await q.OrderByDescending(e => e.EntryDate).Take(200).ToListAsync();
    }

    // ── Stock Withdrawals ──
    public async Task AddWithdrawalAsync(StockWithdrawal w)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var mat = await db.Materials.FindAsync(w.MaterialId);
        if (mat == null) throw new Exception("ماده یافت نشد");
        if (mat.CurrentStock < w.Quantity) throw new Exception($"موجودی کافی نیست (موجودی: {mat.CurrentStock} {mat.Unit})");
        // متصدی: در انتظار تایید — موجودی هنوز کم نمیشه
        // اپراتور و بالاتر: مستقیم تایید میشه
        if (w.Status == WithdrawalStatus.Approved)
            mat.CurrentStock -= w.Quantity;
        db.StockWithdrawals.Add(w);
        await db.SaveChangesAsync();
    }
    public async Task<List<StockWithdrawal>> GetWithdrawalsAsync(int? materialId = null, string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockWithdrawals.Include(w => w.Material).AsQueryable();
        if (materialId.HasValue) q = q.Where(w => w.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(w => w.Material!.Name.Contains(search) || w.Material.Code.Contains(search));
        return await q.OrderByDescending(w => w.WithdrawalDate).Take(200).ToListAsync();
    }
    public async Task<List<StockWithdrawal>> GetPendingWithdrawalsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.StockWithdrawals.Include(w => w.Material)
            .Where(w => w.Status == WithdrawalStatus.Pending)
            .OrderByDescending(w => w.CreatedAt).ToListAsync();
    }
    public async Task ApproveWithdrawalAsync(int withdrawalId, int approvedByUserId, string approvedByUsername)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var w = await db.StockWithdrawals.Include(x => x.Material).FirstOrDefaultAsync(x => x.Id == withdrawalId);
        if (w == null) return;
        var mat = await db.Materials.FindAsync(w.MaterialId);
        if (mat == null) return;
        if (mat.CurrentStock < w.Quantity) throw new Exception($"موجودی کافی نیست (موجودی: {mat.CurrentStock} {mat.Unit})");
        mat.CurrentStock -= w.Quantity;
        w.Status = WithdrawalStatus.Approved;
        w.ApprovedByUserId = approvedByUserId;
        w.ApprovedByUsername = approvedByUsername;
        w.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
    public async Task RejectWithdrawalAsync(int withdrawalId, int rejectedByUserId, string rejectedByUsername, string reason)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var w = await db.StockWithdrawals.FindAsync(withdrawalId);
        if (w == null) return;
        w.Status = WithdrawalStatus.Rejected;
        w.ApprovedByUserId = rejectedByUserId;
        w.ApprovedByUsername = rejectedByUsername;
        w.ApprovedAt = DateTime.UtcNow;
        w.RejectReason = reason;
        await db.SaveChangesAsync();
    }

    // ── Alerts ──
    public async Task<List<StockAlert>> GetAlertsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var alerts = new List<StockAlert>();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var now = DateTime.UtcNow;
        var soonDate = now.AddDays(7);

        foreach (var m in materials)
        {
            if (!string.IsNullOrWhiteSpace(search) &&
                !m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !m.Code.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (m.CurrentStock <= m.MinStockLevel)
                alerts.Add(new StockAlert { MaterialId=m.Id, MaterialName=m.Name, MaterialCode=m.Code, Unit=m.Unit, CurrentStock=m.CurrentStock, MinStockLevel=m.MinStockLevel, Type=AlertType.LowStock, Message=$"موجودی {m.Name} ({m.CurrentStock} {m.Unit}) به حد هشدار رسیده" });
        }

        var expiring = await db.StockEntries.Include(e => e.Material)
            .Where(e => e.ExpiryDate <= soonDate && e.ExpiryDate >= now && e.Material!.IsActive).ToListAsync();
        foreach (var e in expiring)
            alerts.Add(new StockAlert { MaterialId=e.MaterialId, MaterialName=e.Material?.Name??"", MaterialCode=e.Material?.Code??"", Unit=e.Material?.Unit??"", CurrentStock=e.Quantity, ExpiryDate=e.ExpiryDate, Type=AlertType.Expiring, Message=$"{e.Material?.Name} تا {(e.ExpiryDate-now).Days} روز دیگر منقضی می‌شود" });

        var expired = await db.StockEntries.Include(e => e.Material)
            .Where(e => e.ExpiryDate < now && e.Material!.IsActive).ToListAsync();
        foreach (var e in expired)
            alerts.Add(new StockAlert { MaterialId=e.MaterialId, MaterialName=e.Material?.Name??"", MaterialCode=e.Material?.Code??"", Unit=e.Material?.Unit??"", CurrentStock=e.Quantity, ExpiryDate=e.ExpiryDate, Type=AlertType.Expired, Message=$"{e.Material?.Name} (ورودی {e.EntryDate:yyyy/MM/dd}) منقضی شده!" });

        return alerts;
    }

    public async Task<(decimal totalValue, int totalMaterials, int lowStockCount, int alertCount, int pendingCount)> GetDashboardStatsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var alerts = await GetAlertsAsync();
        var pendingCount = await db.StockWithdrawals.CountAsync(w => w.Status == WithdrawalStatus.Pending);
        return (materials.Sum(m => m.CurrentStock * m.PricePerUnit), materials.Count,
                materials.Count(m => m.CurrentStock <= m.MinStockLevel), alerts.Count, pendingCount);
    }

    public async Task<(List<Material> materials, List<StockEntry> entries, List<StockWithdrawal> withdrawals, List<StockAlert> alerts)> GetExportDataAsync()
    {
        var materials = await GetMaterialsAsync();
        var entries = await GetEntriesAsync();
        var withdrawals = await GetWithdrawalsAsync();
        var als = await GetAlertsAsync();
        return (materials, entries, withdrawals, als);
    }
}
