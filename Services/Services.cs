using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

public class AuthService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public User? CurrentUser { get; private set; }
    public event Action? OnAuthChanged;
    private HashSet<string> _allowedPages = new(StringComparer.OrdinalIgnoreCase);

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
        _allowedPages = user.Role==UserRole.Admin ? new HashSet<string>(PageAccess.Pages.Select(x=>x.Key),StringComparer.OrdinalIgnoreCase) :
            (await db.RolePagePermissions.Where(x=>x.Role==user.Role&&x.IsAllowed).Select(x=>x.PageKey).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        OnAuthChanged?.Invoke(); return true;
    }
    public bool CanAccess(string pageKey)=>IsAdmin||_allowedPages.Contains(pageKey);
    public void Logout() { CurrentUser = null; _allowedPages.Clear(); OnAuthChanged?.Invoke(); }
}

public static class PageAccess
{
    public static readonly (string Key,string Label)[] Pages =
    [
        ("dashboard","داشبورد"),("materials","مواد اولیه"),("archive","آرشیو مواد"),("warehouses","انبارها و تفصیل"),("unit-config","بازتعریف واحدها"),("definitions","تعاریف اولیه"),
        ("entry","ورود به انبار"),("withdrawal","خروج از انبار"),("transfer","انتقال بین انبارها"),("warehouse-stock","موجودی تفکیکی"),("cardex","کاردکس"),
        ("history","تاریخچه"),("prices","تحلیل قیمت"),("recipes","رسپی‌ها"),("alerts","هشدارها"),("assets","اموال و اثاثیه"),("users","مدیریت کاربران")
    ];
}

public partial class WarehouseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly LowStockSignal? _lowStock;
    public WarehouseService(IDbContextFactory<AppDbContext> factory, LowStockSignal? lowStock = null) { _factory = factory; _lowStock = lowStock; }
    public async Task<List<RolePagePermission>> GetRolePermissionsAsync(UserRole role){await using var db=await _factory.CreateDbContextAsync();return await db.RolePagePermissions.Where(x=>x.Role==role).OrderBy(x=>x.PageKey).ToListAsync();}
    public async Task SaveRolePermissionsAsync(UserRole role,IEnumerable<string> allowed){await using var db=await _factory.CreateDbContextAsync();var set=allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);var rows=await db.RolePagePermissions.Where(x=>x.Role==role).ToListAsync();foreach(var page in PageAccess.Pages){var row=rows.FirstOrDefault(x=>x.PageKey==page.Key);if(row==null){row=new RolePagePermission{Role=role,PageKey=page.Key};db.RolePagePermissions.Add(row);}row.IsAllowed=role==UserRole.Admin||set.Contains(page.Key);}await db.SaveChangesAsync();}

    public async Task<List<Warehouse>> GetWarehousesAsync() { await using var db=await _factory.CreateDbContextAsync(); return await db.Warehouses.Where(x=>x.IsActive).OrderBy(x=>x.Id).ToListAsync(); }
    public async Task DeleteWarehouseAsync(int warehouseId)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var warehouse=await db.Warehouses.FindAsync(warehouseId)??throw new Exception("انبار یافت نشد");
        var stock=await db.WarehouseStocks.Where(x=>x.WarehouseId==warehouseId).SumAsync(x=>x.Quantity);
        if(stock!=0)throw new Exception($"این انبار {stock:N3} واحد موجودی دارد؛ ابتدا موجودی را به انبار دیگری منتقل کنید.");
        warehouse.IsActive=false;
        await db.SaveChangesAsync();
    }
    public async Task UpdateWarehouseAsync(Warehouse item){await using var db=await _factory.CreateDbContextAsync();db.Warehouses.Update(item);await db.SaveChangesAsync();}
    public async Task<List<WarehouseStock>> GetWarehouseStocksAsync(int? warehouseId=null, string? search=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.WarehouseStocks.Include(x=>x.Warehouse).Include(x=>x.Material).ThenInclude(x=>x!.Unit).Where(x=>x.Material!.IsActive&&x.Quantity!=0).AsQueryable();
        if(warehouseId.HasValue) q=q.Where(x=>x.WarehouseId==warehouseId);
        if(!string.IsNullOrWhiteSpace(search)){var ids=await MatchingMaterialIdsAsync(db,search);q=q.Where(x=>ids.Contains(x.MaterialId));}
        return await q.OrderBy(x=>x.Material!.Code).ThenBy(x=>x.Warehouse!.Name).ToListAsync();
    }
    public async Task<decimal> GetWarehouseStockAsync(int warehouseId,int materialId)
    { await using var db=await _factory.CreateDbContextAsync(); return await db.WarehouseStocks.Where(x=>x.WarehouseId==warehouseId&&x.MaterialId==materialId).Select(x=>x.Quantity).FirstOrDefaultAsync(); }

    private static async Task<WarehouseStock> StockAsync(AppDbContext db,int warehouseId,int materialId)
    {
        var stock=await db.WarehouseStocks.FirstOrDefaultAsync(x=>x.WarehouseId==warehouseId&&x.MaterialId==materialId);
        if(stock!=null) return stock;
        stock=new WarehouseStock{WarehouseId=warehouseId,MaterialId=materialId}; db.WarehouseStocks.Add(stock); return stock;
    }
    // Unit factors like 1 عدد = 1/30 شانه cannot be stored exactly; taking "everything" must not fail on a tiny rounding gap.
    private static string Document(string prefix)=>$"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    public async Task<List<InventoryTransaction>> GetCardexAsync(int? materialId=null,int? warehouseId=null,string? search=null,DateTime? from=null,DateTime? to=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.InventoryTransactions.Include(x=>x.Material).ThenInclude(x=>x!.Unit).Include(x=>x.Warehouse).Include(x=>x.RelatedWarehouse).AsQueryable();
        if(materialId.HasValue) q=q.Where(x=>x.MaterialId==materialId); if(warehouseId.HasValue) q=q.Where(x=>x.WarehouseId==warehouseId);
        if(from.HasValue) q=q.Where(x=>x.TransactionDate>=from); if(to.HasValue) q=q.Where(x=>x.TransactionDate<to.Value.AddDays(1));
        if(!string.IsNullOrWhiteSpace(search)){var ids=await MatchingMaterialIdsAsync(db,search);q=q.Where(x=>ids.Contains(x.MaterialId)||x.DocumentNumber.Contains(search));}
        return await q.OrderByDescending(x=>x.TransactionDate).ThenByDescending(x=>x.Id).Take(1000).ToListAsync();
    }
    // Recomputes the material total; a drop to the minimum is signalled so an SMS can go out (the sender re-checks after commit).
    private async Task SyncTotalAsync(AppDbContext db,int materialId)
    {
        var mat=await db.Materials.FindAsync(materialId); if(mat==null) return;
        var before=mat.CurrentStock; mat.CurrentStock=await db.WarehouseStocks.Where(x=>x.MaterialId==materialId).SumAsync(x=>x.Quantity);
        if(mat.MinStockLevel>0&&before>mat.MinStockLevel&&mat.CurrentStock<=mat.MinStockLevel) _lowStock?.Raise(materialId);
    }

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
    public async Task UpdateGroupAsync(MaterialGroup g)
    { await using var db=await _factory.CreateDbContextAsync(); db.MaterialGroups.Update(g); await db.SaveChangesAsync(); }
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
    public async Task<List<Material>> GetArchivedMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.Materials
            .Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group)
            .Where(m => !m.IsActive).AsQueryable();
        var list = await q.OrderBy(m => m.Code).ToListAsync();
        return string.IsNullOrWhiteSpace(search) ? list : list.Where(m => TextSearch.Matches(search, m.Name, m.Code)).ToList();
    }
    public async Task<Material?> GetMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).FirstOrDefaultAsync(m => m.Id == id); }
    public async Task<bool> CodeExistsAsync(string code, int? excludeId = null)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.AnyAsync(m => m.Code == code && m.Id != excludeId); }
    public async Task ArchiveMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var m = await db.Materials.FindAsync(id); if (m!=null){m.IsActive=false; await db.SaveChangesAsync();} }
    public async Task RestoreMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var m = await db.Materials.FindAsync(id); if (m!=null){m.IsActive=true; await db.SaveChangesAsync();} }
    public Task DeleteMaterialAsync(int id) => ArchiveMaterialAsync(id);


    // ── Assets (اموال) ──
    public async Task<List<Asset>> GetAssetsAsync(string? search=null,string? location=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.Assets.AsQueryable();
        if(!string.IsNullOrWhiteSpace(search)) q=q.Where(x=>x.Name.Contains(search)||x.Notes.Contains(search));
        if(!string.IsNullOrWhiteSpace(location)) q=q.Where(x=>x.Location==location);
        return await q.OrderBy(x=>x.Location).ThenBy(x=>x.Name).ToListAsync();
    }
    public async Task<List<string>> GetAssetLocationsAsync()
    {
        await using var db=await _factory.CreateDbContextAsync();
        var known=new List<string>{"مغازه","کارگاه تر","کارگاه نان","اداری"};
        known.AddRange(await db.Warehouses.Where(x=>x.IsActive).OrderBy(x=>x.Id).Select(x=>x.Name).ToListAsync());
        known.AddRange(await db.Assets.Where(x=>x.Location!="").Select(x=>x.Location).Distinct().ToListAsync());
        return known.Distinct().ToList();
    }
    public async Task SaveAssetAsync(Asset input,string username)
    {
        if(string.IsNullOrWhiteSpace(input.Name)) throw new Exception("نام اموال الزامی است");
        if(string.IsNullOrWhiteSpace(input.Location)) throw new Exception("محل نگهداری را مشخص کنید");
        if(input.Quantity<0) throw new Exception("تعداد نمی‌تواند منفی باشد");
        if(input.UnitPrice<0) throw new Exception("فی نمی‌تواند منفی باشد");
        await using var db=await _factory.CreateDbContextAsync();
        if(input.Id==0){ if(input.Quantity<=0) throw new Exception("تعداد باید بیشتر از صفر باشد"); input.CreatedByUsername=username; input.Name=input.Name.Trim(); db.Assets.Add(input); }
        else
        {
            var existing=await db.Assets.FindAsync(input.Id)??throw new Exception("اموال یافت نشد");
            existing.Name=input.Name.Trim(); existing.Location=input.Location; existing.Quantity=input.Quantity; existing.UnitPrice=input.UnitPrice; existing.PurchaseDate=input.PurchaseDate; existing.Notes=input.Notes;
        }
        await db.SaveChangesAsync();
    }
    public async Task DeleteAssetAsync(int id){await using var db=await _factory.CreateDbContextAsync();var a=await db.Assets.FindAsync(id);if(a!=null){db.Assets.Remove(a);await db.SaveChangesAsync();}}
    public async Task<List<AssetDisposal>> GetAssetDisposalsAsync(string? search=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.AssetDisposals.Include(x=>x.Asset).AsQueryable();
        if(!string.IsNullOrWhiteSpace(search)) q=q.Where(x=>x.Asset!.Name.Contains(search)||x.Reason.Contains(search));
        return await q.OrderByDescending(x=>x.DisposalDate).ThenByDescending(x=>x.Id).ToListAsync();
    }
    public async Task SaveAssetDisposalAsync(AssetDisposal input,string username)
    {
        if(input.Quantity<=0) throw new Exception("تعداد خروج باید بیشتر از صفر باشد");
        await using var db=await _factory.CreateDbContextAsync();
        if(input.Id==0)
        {
            var asset=await db.Assets.FindAsync(input.AssetId)??throw new Exception("اموال را انتخاب کنید");
            if(asset.Quantity<input.Quantity) throw new Exception($"تعداد موجود این اموال {asset.Quantity:N0} است");
            asset.Quantity-=input.Quantity; input.CreatedByUsername=username; db.AssetDisposals.Add(input);
        }
        else
        {
            var existing=await db.AssetDisposals.FindAsync(input.Id)??throw new Exception("سند خروج یافت نشد");
            var asset=await db.Assets.FindAsync(existing.AssetId)??throw new Exception("اموال یافت نشد");
            var difference=input.Quantity-existing.Quantity;
            if(asset.Quantity<difference) throw new Exception($"تعداد موجود این اموال {asset.Quantity:N0} است");
            asset.Quantity-=difference; existing.Quantity=input.Quantity; existing.DisposalDate=input.DisposalDate; existing.Reason=input.Reason;
        }
        await db.SaveChangesAsync();
    }
    public async Task DeleteAssetDisposalAsync(int id)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var d=await db.AssetDisposals.FindAsync(id); if(d==null) return;
        var asset=await db.Assets.FindAsync(d.AssetId); if(asset!=null) asset.Quantity+=d.Quantity;
        db.AssetDisposals.Remove(d); await db.SaveChangesAsync();
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
    /// <summary>
    /// Low stock (only materials with a minimum set) and expiry of what is still on the shelf: stock is assumed to leave
    /// first-in-first-out, so only the newest entries that make up today's stock can expire.
    /// </summary>
    public async Task<List<StockAlert>> GetAlertsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var alerts = new List<StockAlert>();
        var materials = (await db.Materials.Where(m => m.IsActive).ToListAsync()).Where(m => TextSearch.Matches(search, m.Name, m.Code)).ToList();
        var ids = materials.Where(m => m.CurrentStock > 0).Select(m => m.Id).ToList();
        var entries = (await db.StockEntries.Where(e => ids.Contains(e.MaterialId)).ToListAsync()).ToLookup(e => e.MaterialId);
        var now = DateTime.Now; var soon = now.AddDays(7);
        foreach (var mat in materials)
        {
            if (IsLowStock(mat))
                alerts.Add(new StockAlert { MaterialId=mat.Id, MaterialName=mat.Name, MaterialCode=mat.Code, Unit=mat.StockUnitName, CurrentStock=mat.CurrentStock, MinStockLevel=mat.MinStockLevel, Type=AlertType.LowStock, Message=$"موجودی {mat.Name} ({UnitSet.N(mat.CurrentStock)} {mat.StockUnitName}) به حد هشدار ({UnitSet.N(mat.MinStockLevel)}) رسیده" });
            var remaining = mat.CurrentStock;
            foreach (var e in entries[mat.Id].OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id))
            {
                if (remaining <= 0) break;
                var onShelf = Math.Min(e.Quantity, remaining); remaining -= onShelf;
                if (e.ExpiryDate < now)
                    alerts.Add(new StockAlert { MaterialId=mat.Id, MaterialName=mat.Name, MaterialCode=mat.Code, Unit=mat.StockUnitName, CurrentStock=onShelf, Type=AlertType.Expired, ExpiryDate=e.ExpiryDate, Message=$"{UnitSet.N(onShelf)} {mat.StockUnitName} از {mat.Name} (ورود {e.EntryDate.ToShamsi()}) منقضی شده!" });
                else if (e.ExpiryDate <= soon)
                    alerts.Add(new StockAlert { MaterialId=mat.Id, MaterialName=mat.Name, MaterialCode=mat.Code, Unit=mat.StockUnitName, CurrentStock=onShelf, Type=AlertType.Expiring, ExpiryDate=e.ExpiryDate, Message=$"{UnitSet.N(onShelf)} {mat.StockUnitName} از {mat.Name} تا {(e.ExpiryDate.Date-now.Date).Days} روز دیگر منقضی می‌شود" });
            }
        }
        return alerts;
    }
    public static bool IsLowStock(Material m) => m.MinStockLevel > 0 && m.CurrentStock <= m.MinStockLevel;

    public async Task<(List<Material> materials, List<StockEntry> entries, List<StockWithdrawal> withdrawals, List<StockAlert> alerts)> GetExportDataAsync()
    {
        var mats = await GetMaterialsAsync(); var entries = await GetEntriesAsync();
        var withdrawals = await GetWithdrawalsAsync(); var als = await GetAlertsAsync();
        return (mats, entries, withdrawals, als);
    }

    public async Task<List<StockEntry>> GetInvalidPriceEntriesAsync(string? search=null)
    {
        await using var db=await _factory.CreateDbContextAsync();var q=db.StockEntries.Include(x=>x.Material).Include(x=>x.Warehouse).Where(x=>x.PricePerUnit<100000&&!x.PriceConfirmed);
        if(!string.IsNullOrWhiteSpace(search)){var ids=await MatchingMaterialIdsAsync(db,search);q=q.Where(x=>ids.Contains(x.MaterialId));}
        return await q.OrderByDescending(x=>x.EntryDate).ToListAsync();
    }
    public async Task ConfirmEntryPriceAsync(int id,string username){await using var db=await _factory.CreateDbContextAsync();var e=await db.StockEntries.FindAsync(id);if(e==null)return;e.PriceConfirmed=true;e.PriceConfirmedBy=username;e.PriceConfirmedAt=DateTime.UtcNow;await db.SaveChangesAsync();}
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
    public decimal TotalMainWeightKg    { get; set; }
    // Main ingredients whose weight is unknown (no گرم/کیلوگرم relation) and are left out of the cost per kilogram.
    public List<string> IngredientsWithoutWeight { get; set; } = new();
    public List<RecipeCostLine> Lines { get; set; } = new();
}


public class MovementDashboard{public List<MovementDashboardRow> Rows{get;set;}=new();public decimal IncomingQuantity=>Rows.Sum(x=>x.IncomingQuantity);public decimal OutgoingQuantity=>Rows.Sum(x=>x.OutgoingQuantity);public decimal IncomingValue=>Rows.Sum(x=>x.IncomingValue);public decimal OutgoingValue=>Rows.Sum(x=>x.OutgoingValue);}
public class MovementDashboardRow{public int MaterialId{get;set;}public string MaterialCode{get;set;}="";public string MaterialName{get;set;}="";public string Unit{get;set;}="";public decimal IncomingQuantity{get;set;}public decimal OutgoingQuantity{get;set;}public decimal IncomingValue{get;set;}public decimal OutgoingValue{get;set;}public bool HasEstimatedOutgoingValue{get;set;}}
internal record CardexRebuildEvent(DateTime Date,int Order,long Id,int Warehouse,decimal In,decimal Out,decimal Price,string Description,int UserId,string User,DateTime Created,string Doc,InventoryTransaction? Existing);
