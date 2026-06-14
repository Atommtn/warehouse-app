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
        CurrentUser = user; OnAuthChanged?.Invoke(); return true;
    }
    public void Logout() { CurrentUser = null; OnAuthChanged?.Invoke(); }
}

public class WarehouseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public WarehouseService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    // ── Users ──
    public async Task<List<User>> GetUsersAsync()
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Users.OrderBy(u => u.Username).ToListAsync(); }
    public async Task AddUserAsync(User u)
    { await using var db = await _factory.CreateDbContextAsync(); db.Users.Add(u); await db.SaveChangesAsync(); }
    public async Task UpdateUserAsync(User u)
    { await using var db = await _factory.CreateDbContextAsync(); db.Users.Update(u); await db.SaveChangesAsync(); }
    public async Task<bool> UsernameExistsAsync(string username, int? excludeId = null)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Users.AnyAsync(u => u.Username == username && u.Id != excludeId); }

    // ── Units ──
    public async Task<List<Unit>> GetUnitsAsync()
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Units.Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync(); }
    public async Task AddUnitAsync(Unit u)
    { await using var db = await _factory.CreateDbContextAsync(); db.Units.Add(u); await db.SaveChangesAsync(); }
    public async Task DeleteUnitAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var u = await db.Units.FindAsync(id); if (u!=null){u.IsActive=false; await db.SaveChangesAsync();} }

    // ── Groups ──
    public async Task<List<MaterialGroup>> GetGroupsAsync()
    { await using var db = await _factory.CreateDbContextAsync(); return await db.MaterialGroups.Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync(); }
    public async Task AddGroupAsync(MaterialGroup g)
    { await using var db = await _factory.CreateDbContextAsync(); db.MaterialGroups.Add(g); await db.SaveChangesAsync(); }
    public async Task DeleteGroupAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var g = await db.MaterialGroups.FindAsync(id); if (g!=null){g.IsActive=false; await db.SaveChangesAsync();} }

    // ── Suppliers ──
    public async Task<List<Supplier>> GetSuppliersAsync()
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Suppliers.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(); }
    public async Task<Supplier?> GetSupplierAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Suppliers.FindAsync(id); }
    public async Task AddSupplierAsync(Supplier s)
    { await using var db = await _factory.CreateDbContextAsync(); db.Suppliers.Add(s); await db.SaveChangesAsync(); }
    public async Task UpdateSupplierAsync(Supplier s)
    { await using var db = await _factory.CreateDbContextAsync(); db.Suppliers.Update(s); await db.SaveChangesAsync(); }
    public async Task DeleteSupplierAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var s = await db.Suppliers.FindAsync(id); if (s!=null){s.IsActive=false; await db.SaveChangesAsync();} }

    // ── Materials ──
    public async Task<List<Material>> GetMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).Where(m => m.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(m => m.Name.Contains(search) || m.Code.Contains(search));
        return await q.OrderBy(m => m.Code).ToListAsync();
    }
    public async Task<Material?> GetMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).FirstOrDefaultAsync(m => m.Id == id); }
    public async Task<bool> CodeExistsAsync(string code, int? excludeId = null)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.AnyAsync(m => m.Code == code && m.Id != excludeId); }
    public async Task AddMaterialAsync(Material m)
    { await using var db = await _factory.CreateDbContextAsync(); db.Materials.Add(m); await db.SaveChangesAsync(); }
    public async Task UpdateMaterialAsync(Material m)
    { await using var db = await _factory.CreateDbContextAsync(); db.Materials.Update(m); await db.SaveChangesAsync(); }
    public async Task DeleteMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var m = await db.Materials.FindAsync(id); if (m!=null){m.IsActive=false; await db.SaveChangesAsync();} }

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
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(e => e.Material!.Name.Contains(search) || e.Material.Code.Contains(search));
        return await q.OrderByDescending(e => e.EntryDate).Take(200).ToListAsync();
    }
    public async Task UpdateStockEntryAsync(StockEntry entry)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.StockEntries.FindAsync(entry.Id);
        if (existing == null) return;
        var oldQty = existing.Quantity;
        existing.Quantity     = entry.Quantity;
        existing.PricePerUnit = entry.PricePerUnit;
        existing.EntryDate    = entry.EntryDate;
        existing.ExpiryDate   = entry.ExpiryDate;
        existing.Notes        = entry.Notes;
        var mat = await db.Materials.FindAsync(existing.MaterialId);
        if (mat != null)
        {
            mat.CurrentStock += (entry.Quantity - oldQty);
            var lastEntry = await db.StockEntries
                .Where(e => e.MaterialId == mat.Id && e.Id != entry.Id)
                .OrderByDescending(e => e.EntryDate)
                .FirstOrDefaultAsync();
            mat.PricePerUnit = entry.EntryDate >= (lastEntry?.EntryDate ?? DateTime.MinValue)
                ? entry.PricePerUnit
                : (lastEntry?.PricePerUnit ?? mat.PricePerUnit);
        }
        await db.SaveChangesAsync();
    }

    // ── Stock Withdrawals ──
    public async Task AddWithdrawalAsync(StockWithdrawal w)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var mat = await db.Materials.FindAsync(w.MaterialId);
        if (mat == null) throw new Exception("ماده یافت نشد");
        if (mat.CurrentStock < w.Quantity) throw new Exception($"موجودی کافی نیست (موجودی: {mat.CurrentStock})");
        if (w.Status == WithdrawalStatus.Approved) mat.CurrentStock -= w.Quantity;
        db.StockWithdrawals.Add(w); await db.SaveChangesAsync();
    }
    public async Task<List<StockWithdrawal>> GetWithdrawalsAsync(int? materialId = null, string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockWithdrawals.Include(w => w.Material).AsQueryable();
        if (materialId.HasValue) q = q.Where(w => w.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(w => w.Material!.Name.Contains(search) || w.Material.Code.Contains(search));
        return await q.OrderByDescending(w => w.WithdrawalDate).Take(200).ToListAsync();
    }
    public async Task<List<StockWithdrawal>> GetPendingWithdrawalsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.StockWithdrawals.Include(w => w.Material).Where(w => w.Status == WithdrawalStatus.Pending).OrderByDescending(w => w.CreatedAt).ToListAsync();
    }
    public async Task ApproveWithdrawalAsync(int id, int userId, string username)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var w = await db.StockWithdrawals.Include(x => x.Material).FirstOrDefaultAsync(x => x.Id == id);
        if (w == null) return;
        var mat = await db.Materials.FindAsync(w.MaterialId);
        if (mat == null) return;
        if (mat.CurrentStock < w.Quantity) throw new Exception("موجودی کافی نیست");
        mat.CurrentStock -= w.Quantity;
        w.Status = WithdrawalStatus.Approved; w.ApprovedByUserId = userId; w.ApprovedByUsername = username; w.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
    public async Task RejectWithdrawalAsync(int id, int userId, string username, string reason)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var w = await db.StockWithdrawals.FindAsync(id);
        if (w == null) return;
        w.Status = WithdrawalStatus.Rejected; w.ApprovedByUserId = userId; w.ApprovedByUsername = username; w.ApprovedAt = DateTime.UtcNow; w.RejectReason = reason;
        await db.SaveChangesAsync();
    }

    // ── Alerts ──
    public async Task<List<StockAlert>> GetAlertsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var alerts = new List<StockAlert>();
        var materials = await db.Materials.Include(m => m.Unit).Where(m => m.IsActive).ToListAsync();
        var now = DateTime.UtcNow; var soon = now.AddDays(7);
        foreach (var mat in materials)
        {
            if (!string.IsNullOrWhiteSpace(search) && !mat.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !mat.Code.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (mat.CurrentStock <= mat.MinStockLevel)
                alerts.Add(new StockAlert { MaterialId=mat.Id, MaterialName=mat.Name, MaterialCode=mat.Code, Unit=mat.Unit?.Name??"", CurrentStock=mat.CurrentStock, MinStockLevel=mat.MinStockLevel, Type=AlertType.LowStock, Message=$"موجودی {mat.Name} ({mat.CurrentStock}) به حد هشدار رسیده" });
        }
        var expiring = await db.StockEntries.Include(e => e.Material).Where(e => e.ExpiryDate <= soon && e.ExpiryDate >= now && e.Material!.IsActive).ToListAsync();
        foreach (var e in expiring)
            alerts.Add(new StockAlert { MaterialId=e.MaterialId, MaterialName=e.Material?.Name??"", MaterialCode=e.Material?.Code??"", Type=AlertType.Expiring, ExpiryDate=e.ExpiryDate, Message=$"{e.Material?.Name} تا {(e.ExpiryDate-now).Days} روز دیگر منقضی می‌شود" });
        var expired = await db.StockEntries.Include(e => e.Material).Where(e => e.ExpiryDate < now && e.Material!.IsActive).ToListAsync();
        foreach (var e in expired)
            alerts.Add(new StockAlert { MaterialId=e.MaterialId, MaterialName=e.Material?.Name??"", MaterialCode=e.Material?.Code??"", Type=AlertType.Expired, ExpiryDate=e.ExpiryDate, Message=$"{e.Material?.Name} منقضی شده!" });
        return alerts;
    }

    public async Task<(decimal totalValue, int totalMaterials, int lowStockCount, int alertCount, int pendingCount)> GetDashboardStatsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var alerts = await GetAlertsAsync();
        var pendingCount = await db.StockWithdrawals.CountAsync(w => w.Status == WithdrawalStatus.Pending);
        return (materials.Sum(m => m.CurrentStock * m.PricePerUnit), materials.Count, materials.Count(m => m.CurrentStock <= m.MinStockLevel), alerts.Count, pendingCount);
    }

    public async Task<(List<Material> materials, List<StockEntry> entries, List<StockWithdrawal> withdrawals, List<StockAlert> alerts)> GetExportDataAsync()
    {
        var mats = await GetMaterialsAsync(); var entries = await GetEntriesAsync();
        var withdrawals = await GetWithdrawalsAsync(); var als = await GetAlertsAsync();
        return (mats, entries, withdrawals, als);
    }

    // ── Recipes ──
    public async Task<List<Recipe>> GetRecipesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Recipes
            .Include(r => r.Ingredients).ThenInclude(i => i.Material).ThenInclude(m => m!.Unit)
            .Where(r => r.IsActive)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    public async Task<Recipe?> GetRecipeAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Recipes
            .Include(r => r.Ingredients).ThenInclude(i => i.Material).ThenInclude(m => m!.Unit)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<int> AddRecipeAsync(Recipe recipe)
    {
        await using var db = await _factory.CreateDbContextAsync();
        recipe.CreatedAt = DateTime.UtcNow;
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    public async Task UpdateRecipeAsync(Recipe recipe)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.Recipes.Include(r => r.Ingredients).FirstOrDefaultAsync(r => r.Id == recipe.Id);
        if (existing == null) return;
        existing.Name              = recipe.Name;
        existing.Notes             = recipe.Notes;
        existing.BakingLossPercent = recipe.BakingLossPercent;
        existing.PieceWeightGrams  = recipe.PieceWeightGrams;
        existing.IsActive          = recipe.IsActive;
        db.RecipeIngredients.RemoveRange(existing.Ingredients);
        foreach (var ing in recipe.Ingredients) { ing.Id = 0; ing.RecipeId = existing.Id; }
        existing.Ingredients = recipe.Ingredients;
        await db.SaveChangesAsync();
    }

    public async Task DeleteRecipeAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var r = await db.Recipes.FindAsync(id);
        if (r != null) { r.IsActive = false; await db.SaveChangesAsync(); }
    }

    // محاسبه هزینه رسپی — مقدار مواد بر اساس BaseUnitName ماده وارد می‌شود
    // مثال: آرد → BaseQuantity=40 (kg/گونی) → قیمت هر kg = قیمت گونی ÷ 40
    public async Task<RecipeCostResult> CalculateRecipeCostAsync(Recipe recipe, decimal profitPercent = 0)
    {
        await using var db = await _factory.CreateDbContextAsync();

        decimal mainCost    = 0;
        decimal toppingCost = 0;

        foreach (var ing in recipe.Ingredients)
        {
            // میانگین وزنی قیمت per stock-unit از StockEntries
            var entries = await db.StockEntries
                .Where(e => e.MaterialId == ing.MaterialId)
                .ToListAsync();

            decimal avgPricePerStockUnit;
            if (entries.Any())
            {
                var totalQty   = entries.Sum(e => e.Quantity);
                var totalValue = entries.Sum(e => e.Quantity * e.PricePerUnit);
                avgPricePerStockUnit = totalQty > 0 ? totalValue / totalQty : 0;
            }
            else
            {
                var mat = await db.Materials.FindAsync(ing.MaterialId);
                avgPricePerStockUnit = mat?.PricePerUnit ?? 0;
            }

            // ضریب تبدیل: چند واحد پایه در هر واحد انبار
            var material = await db.Materials.FindAsync(ing.MaterialId);
            var baseQty  = (material?.BaseQuantity ?? 1);
            if (baseQty <= 0) baseQty = 1;

            // قیمت هر واحد پایه (مثلاً هر کیلو)
            decimal pricePerBase = avgPricePerStockUnit / baseQty;

            // هزینه این قلم = مقدار (بر اساس واحد پایه) × قیمت هر واحد پایه
            var lineCost = ing.Quantity * pricePerBase;
            if (ing.IsTopping) toppingCost += lineCost;
            else               mainCost    += lineCost;
        }

        decimal totalRaw = mainCost + toppingCost;

        // افت پخت
        decimal afterLoss = recipe.BakingLossPercent > 0 && recipe.BakingLossPercent < 100
            ? totalRaw / (1 - recipe.BakingLossPercent / 100)
            : totalRaw;

        // جمع وزن مواد اصلی در واحد پایه (برای محاسبه هزینه/کیلو)
        // اگر واحد پایه کیلوگرم باشد مستقیم جمع می‌شود؛ در غیر این صورت کاربر باید واحد یکسان بزند
        decimal totalMainWeight = recipe.Ingredients.Where(i => !i.IsTopping).Sum(i => i.Quantity);

        decimal costPerKg    = totalMainWeight > 0 ? afterLoss / totalMainWeight : 0;
        decimal costPerPiece = recipe.PieceWeightGrams > 0
            ? costPerKg * (recipe.PieceWeightGrams / 1000m)
            : 0;

        decimal sellingPricePerKg    = costPerKg    * (1 + profitPercent / 100);
        decimal sellingPricePerPiece = costPerPiece * (1 + profitPercent / 100);

        return new RecipeCostResult
        {
            MainIngredientCost   = mainCost,
            ToppingCost          = toppingCost,
            TotalRawCost         = totalRaw,
            TotalAfterLoss       = afterLoss,
            CostPerKg            = costPerKg,
            CostPerPiece         = costPerPiece,
            SellingPricePerKg    = sellingPricePerKg,
            SellingPricePerPiece = sellingPricePerPiece,
            ProfitPercent        = profitPercent
        };
    }
}

public class RecipeCostResult
{
    public decimal MainIngredientCost   { get; set; }
    public decimal ToppingCost          { get; set; }
    public decimal TotalRawCost         { get; set; }
    public decimal TotalAfterLoss       { get; set; }
    public decimal CostPerKg            { get; set; }
    public decimal CostPerPiece         { get; set; }
    public decimal SellingPricePerKg    { get; set; }
    public decimal SellingPricePerPiece { get; set; }
    public decimal ProfitPercent        { get; set; }
}
