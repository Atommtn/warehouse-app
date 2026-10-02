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

public class WarehouseService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public WarehouseService(IDbContextFactory<AppDbContext> factory) => _factory = factory;
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
    // A material defined without unit conversions still needs its stock unit and base unit to be selectable on entry/withdrawal.
    private static async Task EnsureMaterialUnitsAsync(AppDbContext db,int materialId)
    {
        var existing=await db.MaterialUnitConversions.Where(x=>x.MaterialId==materialId).ToListAsync();
        var m=await db.Materials.Include(x=>x.Unit).FirstOrDefaultAsync(x=>x.Id==materialId); if(m==null) return;
        var names=existing.Select(x=>x.UnitName).ToHashSet(StringComparer.OrdinalIgnoreCase); var addedStock=false;
        var stockUnit=m.Unit?.Name;
        if(!existing.Any(x=>x.IsActive)&&!string.IsNullOrWhiteSpace(stockUnit)&&names.Add(stockUnit)){db.MaterialUnitConversions.Add(new MaterialUnitConversion{MaterialId=m.Id,UnitName=stockUnit,FactorToBaseUnit=m.BaseQuantity>0?m.BaseQuantity:1,IsLegacyStockUnit=true,IsActive=true});addedStock=true;}
        if(!string.IsNullOrWhiteSpace(m.BaseUnitName)&&names.Add(m.BaseUnitName)) db.MaterialUnitConversions.Add(new MaterialUnitConversion{MaterialId=m.Id,UnitName=m.BaseUnitName,FactorToBaseUnit=1,IsLegacyStockUnit=!addedStock&&!existing.Any(x=>x.IsLegacyStockUnit),IsActive=true});
        if(db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
    }
    public async Task<StockDisplay> GetStockDisplayAsync()
    {
        await using var db=await _factory.CreateDbContextAsync();
        var rows=await (from m in db.Materials where m.DisplayUnitName!="" && m.DisplayUnitName!=m.BaseUnitName
                        join c in db.MaterialUnitConversions on new{MaterialId=m.Id,UnitName=m.DisplayUnitName} equals new{c.MaterialId,c.UnitName}
                        where c.IsActive select new{m.Id,c.UnitName,c.FactorToBaseUnit}).ToListAsync();
        return new StockDisplay(rows.GroupBy(x=>x.Id).ToDictionary(g=>g.Key,g=>(g.First().UnitName,g.First().FactorToBaseUnit)));
    }
    public async Task<List<MaterialUnitConversion>> GetMaterialUnitsAsync(int materialId){await using var db=await _factory.CreateDbContextAsync();await EnsureMaterialUnitsAsync(db,materialId);return await db.MaterialUnitConversions.AsNoTracking().Where(x=>x.MaterialId==materialId&&x.IsActive).OrderByDescending(x=>x.IsLegacyStockUnit).ThenBy(x=>x.UnitName).ToListAsync();}
    public async Task ConfigureMaterialUnitsAsync(int materialId,string baseUnit,List<MaterialUnitConversion> conversions,string displayUnit="")
    {
        if(string.IsNullOrWhiteSpace(baseUnit))throw new Exception("واحد محاسبه (رسپی) را انتخاب کنید");
        baseUnit=baseUnit.Trim();
        conversions=conversions.Where(x=>!string.IsNullOrWhiteSpace(x.UnitName)).ToList();
        if(conversions.Any(x=>x.FactorToBaseUnit<=0))throw new Exception("مقدار معادل همه‌ی واحدها باید بیشتر از صفر باشد");
        if(conversions.Select(x=>x.UnitName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=conversions.Count)throw new Exception("نام واحد تکراری است");
        // The calculation unit is always part of the list with factor 1; add it instead of asking the user to.
        var baseRow=conversions.FirstOrDefault(x=>string.Equals(x.UnitName.Trim(),baseUnit,StringComparison.OrdinalIgnoreCase));
        if(baseRow==null){baseRow=new MaterialUnitConversion{UnitName=baseUnit,IsActive=true};conversions.Add(baseRow);}
        baseRow.FactorToBaseUnit=1;baseRow.DefinedRefUnit=baseUnit;baseRow.DefinedCount=1;baseRow.DefinedAmount=1;
        if(conversions.Count(x=>x.IsLegacyStockUnit)>1)throw new Exception("فقط یک واحد می‌تواند واحد ثبت سوابق قبلی باشد");
        if(!conversions.Any(x=>x.IsLegacyStockUnit))
        {
            await using var lookup=await _factory.CreateDbContextAsync();
            var oldLegacyName=await lookup.MaterialUnitConversions.Where(x=>x.MaterialId==materialId&&x.IsLegacyStockUnit).Select(x=>x.UnitName).FirstOrDefaultAsync();
            (conversions.FirstOrDefault(x=>string.Equals(x.UnitName.Trim(),oldLegacyName,StringComparison.OrdinalIgnoreCase))??baseRow).IsLegacyStockUnit=true;
        }
        await using var db=await _factory.CreateDbContextAsync();await using var tx=await db.Database.BeginTransactionAsync();var m=await db.Materials.FindAsync(materialId)??throw new Exception("ماده یافت نشد");var oldLegacy=await db.MaterialUnitConversions.Where(x=>x.MaterialId==materialId&&x.IsLegacyStockUnit).Select(x=>x.FactorToBaseUnit).FirstOrDefaultAsync();if(oldLegacy<=0)oldLegacy=m.BaseQuantity>0?m.BaseQuantity:1;var newLegacy=conversions.Single(x=>x.IsLegacyStockUnit).FactorToBaseUnit;var ratio=newLegacy/oldLegacy;
        var old=await db.MaterialUnitConversions.Where(x=>x.MaterialId==materialId).ToListAsync();db.MaterialUnitConversions.RemoveRange(old);foreach(var c in conversions){c.Id=0;c.Material=null;c.MaterialId=materialId;c.UnitName=c.UnitName.Trim();db.MaterialUnitConversions.Add(c);}m.BaseUnitName=baseUnit.Trim();m.DisplayUnitName=conversions.Any(x=>string.Equals(x.UnitName.Trim(),displayUnit?.Trim(),StringComparison.OrdinalIgnoreCase))?displayUnit!.Trim():"";m.BaseQuantity=newLegacy;m.CurrentStock*=ratio;m.MinStockLevel*=ratio;
        var stocks=await db.WarehouseStocks.Where(x=>x.MaterialId==materialId).ToListAsync();foreach(var s in stocks){s.Quantity*=ratio;s.MinStockLevel*=ratio;}
        var map=conversions.ToDictionary(x=>x.UnitName,x=>x.FactorToBaseUnit,StringComparer.OrdinalIgnoreCase);var entries=await db.StockEntries.Where(x=>x.MaterialId==materialId).ToListAsync();foreach(var e in entries){if(e.EnteredQuantity==0)e.EnteredQuantity=e.Quantity/(e.ConversionFactor<=0?1:e.ConversionFactor);if(string.IsNullOrWhiteSpace(e.EnteredUnitName))e.EnteredUnitName=conversions.Single(x=>x.IsLegacyStockUnit).UnitName;if(map.TryGetValue(e.EnteredUnitName,out var f)){e.ConversionFactor=f;e.Quantity=e.EnteredQuantity*f;}}
        var outs=await db.StockWithdrawals.Where(x=>x.MaterialId==materialId).ToListAsync();foreach(var w in outs){if(w.EnteredQuantity==0)w.EnteredQuantity=w.Quantity/(w.ConversionFactor<=0?1:w.ConversionFactor);if(string.IsNullOrWhiteSpace(w.EnteredUnitName))w.EnteredUnitName=conversions.Single(x=>x.IsLegacyStockUnit).UnitName;if(map.TryGetValue(w.EnteredUnitName,out var f)){w.ConversionFactor=f;w.Quantity=w.EnteredQuantity*f;}}
        var recipes=await db.RecipeIngredients.Where(x=>x.MaterialId==materialId).ToListAsync();foreach(var r in recipes)if(string.IsNullOrWhiteSpace(r.UnitName))r.UnitName=baseUnit;
        await db.SaveChangesAsync();await RebuildMaterialCardexAsync(db,materialId);await db.SaveChangesAsync();await tx.CommitAsync();
    }
    private static async Task RebuildMaterialCardexAsync(AppDbContext db,int materialId)
    {
        var replace=await db.InventoryTransactions.Where(x=>x.MaterialId==materialId&&(x.Type==InventoryTransactionType.Entry||x.Type==InventoryTransactionType.Withdrawal||x.Type==InventoryTransactionType.Adjustment||x.DocumentNumber.StartsWith("OPEN-"))).ToListAsync();db.InventoryTransactions.RemoveRange(replace);await db.SaveChangesAsync();var entries=await db.StockEntries.Where(x=>x.MaterialId==materialId).ToListAsync();var outs=await db.StockWithdrawals.Where(x=>x.MaterialId==materialId&&x.Status==WithdrawalStatus.Approved).ToListAsync();var preserved=await db.InventoryTransactions.Where(x=>x.MaterialId==materialId).ToListAsync();var events=new List<CardexRebuildEvent>();events.AddRange(entries.Select(x=>new CardexRebuildEvent(x.EntryDate,1,x.Id,x.WarehouseId!.Value,x.Quantity,0,x.PricePerUnit/(x.ConversionFactor<=0?1:x.ConversionFactor),x.Notes,x.CreatedByUserId,x.CreatedByUsername,x.CreatedAt,$"LEGACY-IN-{x.Id}",null)));events.AddRange(outs.Select(x=>new CardexRebuildEvent(x.WithdrawalDate,2,x.Id,x.WarehouseId!.Value,0,x.Quantity,0,x.Reason,x.CreatedByUserId,x.CreatedByUsername,x.CreatedAt,$"LEGACY-OUT-{x.Id}",null)));events.AddRange(preserved.Select(x=>new CardexRebuildEvent(x.TransactionDate,3,x.Id,x.WarehouseId,x.IncomingQuantity,x.OutgoingQuantity,x.UnitPrice,x.Description,x.CreatedByUserId,x.CreatedByUsername,x.CreatedAt,x.DocumentNumber,x)));var balances=new Dictionary<int,decimal>();foreach(var e in events.OrderBy(x=>x.Date).ThenBy(x=>x.Order).ThenBy(x=>x.Id)){balances.TryGetValue(e.Warehouse,out var b);b+=e.In-e.Out;balances[e.Warehouse]=b;if(e.Existing!=null)e.Existing.BalanceAfter=b;else db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=e.Doc,Type=e.In>0?InventoryTransactionType.Entry:InventoryTransactionType.Withdrawal,WarehouseId=e.Warehouse,MaterialId=materialId,IncomingQuantity=e.In,OutgoingQuantity=e.Out,BalanceAfter=b,UnitPrice=e.Price,TransactionDate=e.Date,Description=e.Description,CreatedByUserId=e.UserId,CreatedByUsername=e.User,CreatedAt=e.Created});}await db.SaveChangesAsync();var stocks=await db.WarehouseStocks.Where(x=>x.MaterialId==materialId).ToListAsync();foreach(var s in stocks){balances.TryGetValue(s.WarehouseId,out var b);var diff=s.Quantity-b;if(diff!=0)db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=$"RECON-{materialId}-{s.WarehouseId}",Type=InventoryTransactionType.Adjustment,WarehouseId=s.WarehouseId,MaterialId=materialId,IncomingQuantity=diff>0?diff:0,OutgoingQuantity=diff<0?-diff:0,BalanceAfter=s.Quantity,UnitPrice=0,TransactionDate=DateTime.Now,Description="اصلاح شفاف مغایرت مانده با سوابق قدیمی",CreatedByUsername="system"});}}
    public async Task UpdateWarehouseAsync(Warehouse item){await using var db=await _factory.CreateDbContextAsync();db.Warehouses.Update(item);await db.SaveChangesAsync();}
    public async Task<List<WarehouseStock>> GetWarehouseStocksAsync(int? warehouseId=null, string? search=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.WarehouseStocks.Include(x=>x.Warehouse).Include(x=>x.Material).ThenInclude(x=>x!.Unit).Where(x=>x.Material!.IsActive&&x.Quantity!=0).AsQueryable();
        if(warehouseId.HasValue) q=q.Where(x=>x.WarehouseId==warehouseId);
        if(!string.IsNullOrWhiteSpace(search)) q=q.Where(x=>x.Material!.Name.Contains(search)||x.Material.Code.Contains(search));
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
    private static bool IsRoundingGap(decimal available,decimal requested)=>available>0&&requested-available<=Math.Max(0.001m,requested*0.0005m);
    private static string Document(string prefix)=>$"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

    public async Task TransferAsync(int sourceId,int destinationId,int materialId,decimal enteredQuantity,string unitName,DateTime date,string notes,int userId,string username)
    {
        if(sourceId==destinationId) throw new Exception("انبار مبدأ و مقصد نمی‌توانند یکسان باشند");
        if(enteredQuantity<=0) throw new Exception("مقدار انتقال باید بیشتر از صفر باشد");
        await using var db=await _factory.CreateDbContextAsync(); await using var tx=await db.Database.BeginTransactionAsync();
        var conversion=await db.MaterialUnitConversions.FirstOrDefaultAsync(x=>x.MaterialId==materialId&&x.UnitName==unitName&&x.IsActive)??throw new Exception("واحد انتقال معتبر نیست");var quantity=enteredQuantity*conversion.FactorToBaseUnit;
        var source=await StockAsync(db,sourceId,materialId); if(source.Quantity<quantity&&IsRoundingGap(source.Quantity,quantity)) quantity=source.Quantity; if(source.Quantity<quantity) throw new Exception($"موجودی انبار مبدأ کافی نیست (موجودی: {source.Quantity:N3})");
        var destination=await StockAsync(db,destinationId,materialId); source.Quantity-=quantity; destination.Quantity+=quantity;
        var latestEntry=await db.StockEntries.Where(x=>x.MaterialId==materialId).OrderByDescending(x=>x.EntryDate).ThenByDescending(x=>x.Id).FirstOrDefaultAsync();
        var unitPrice=latestEntry==null?0:latestEntry.PricePerUnit/(latestEntry.ConversionFactor<=0?1:latestEntry.ConversionFactor);
        var doc=Document("TR");
        db.InventoryTransactions.AddRange(
            new InventoryTransaction{DocumentNumber=doc,Type=InventoryTransactionType.TransferOut,WarehouseId=sourceId,RelatedWarehouseId=destinationId,MaterialId=materialId,OutgoingQuantity=quantity,BalanceAfter=source.Quantity,UnitPrice=unitPrice,TransactionDate=date,Description=notes,CreatedByUserId=userId,CreatedByUsername=username},
            new InventoryTransaction{DocumentNumber=doc,Type=InventoryTransactionType.TransferIn,WarehouseId=destinationId,RelatedWarehouseId=sourceId,MaterialId=materialId,IncomingQuantity=quantity,BalanceAfter=destination.Quantity,UnitPrice=unitPrice,TransactionDate=date,Description=notes,CreatedByUserId=userId,CreatedByUsername=username});
        await db.SaveChangesAsync(); await SyncTotalAsync(db,materialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<InventoryTransaction>> GetCardexAsync(int? materialId=null,int? warehouseId=null,string? search=null,DateTime? from=null,DateTime? to=null)
    {
        await using var db=await _factory.CreateDbContextAsync();
        var q=db.InventoryTransactions.Include(x=>x.Material).ThenInclude(x=>x!.Unit).Include(x=>x.Warehouse).Include(x=>x.RelatedWarehouse).AsQueryable();
        if(materialId.HasValue) q=q.Where(x=>x.MaterialId==materialId); if(warehouseId.HasValue) q=q.Where(x=>x.WarehouseId==warehouseId);
        if(from.HasValue) q=q.Where(x=>x.TransactionDate>=from); if(to.HasValue) q=q.Where(x=>x.TransactionDate<to.Value.AddDays(1));
        if(!string.IsNullOrWhiteSpace(search)) q=q.Where(x=>x.Material!.Name.Contains(search)||x.Material.Code.Contains(search)||x.DocumentNumber.Contains(search));
        return await q.OrderByDescending(x=>x.TransactionDate).ThenByDescending(x=>x.Id).Take(1000).ToListAsync();
    }
    private static async Task SyncTotalAsync(AppDbContext db,int materialId)
    { var mat=await db.Materials.FindAsync(materialId); if(mat!=null) mat.CurrentStock=await db.WarehouseStocks.Where(x=>x.MaterialId==materialId).SumAsync(x=>x.Quantity); }

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
    public async Task<List<Material>> GetMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).Where(m => m.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(m => m.Name.Contains(search) || m.Code.Contains(search));
        return await q.OrderBy(m => m.Code).ToListAsync();
    }
    public async Task<List<Material>> GetArchivedMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.Materials
            .Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group)
            .Where(m => !m.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(m => m.Name.Contains(search) || m.Code.Contains(search));
        return await q.OrderBy(m => m.Code).ToListAsync();
    }
    public async Task<Material?> GetMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).FirstOrDefaultAsync(m => m.Id == id); }
    public async Task<bool> CodeExistsAsync(string code, int? excludeId = null)
    { await using var db = await _factory.CreateDbContextAsync(); return await db.Materials.AnyAsync(m => m.Code == code && m.Id != excludeId); }
    public async Task AddMaterialAsync(Material m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Materials.Add(m); await db.SaveChangesAsync();
        var stockUnit=m.UnitId.HasValue?await db.Units.Where(x=>x.Id==m.UnitId).Select(x=>x.Name).FirstOrDefaultAsync():null;
        if(!string.IsNullOrWhiteSpace(stockUnit))db.MaterialUnitConversions.Add(new MaterialUnitConversion{MaterialId=m.Id,UnitName=stockUnit,FactorToBaseUnit=m.BaseQuantity>0?m.BaseQuantity:1,IsLegacyStockUnit=true,IsActive=true});
        if(!string.IsNullOrWhiteSpace(m.BaseUnitName)&&!string.Equals(stockUnit,m.BaseUnitName,StringComparison.OrdinalIgnoreCase))db.MaterialUnitConversions.Add(new MaterialUnitConversion{MaterialId=m.Id,UnitName=m.BaseUnitName,FactorToBaseUnit=1,IsLegacyStockUnit=false,IsActive=true});
        m.UnitsNormalized=true;await db.SaveChangesAsync();
    }
    public async Task UpdateMaterialAsync(Material m)
    { await using var db = await _factory.CreateDbContextAsync(); db.Materials.Update(m); await db.SaveChangesAsync(); }
    public async Task ArchiveMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var m = await db.Materials.FindAsync(id); if (m!=null){m.IsActive=false; await db.SaveChangesAsync();} }
    public async Task RestoreMaterialAsync(int id)
    { await using var db = await _factory.CreateDbContextAsync(); var m = await db.Materials.FindAsync(id); if (m!=null){m.IsActive=true; await db.SaveChangesAsync();} }
    public Task DeleteMaterialAsync(int id) => ArchiveMaterialAsync(id);


    // ── Stock Entries ──
    public async Task AddStockEntryAsync(StockEntry entry)
    {
        if(!entry.WarehouseId.HasValue) throw new Exception("انبار را انتخاب کنید");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx=await db.Database.BeginTransactionAsync();
        var unit=await db.MaterialUnitConversions.FirstOrDefaultAsync(x=>x.MaterialId==entry.MaterialId&&x.UnitName==entry.EnteredUnitName&&x.IsActive)??throw new Exception("واحد ورود معتبر نیست");entry.EnteredQuantity=entry.EnteredQuantity>0?entry.EnteredQuantity:entry.Quantity;entry.ConversionFactor=unit.FactorToBaseUnit;entry.Quantity=entry.EnteredQuantity*entry.ConversionFactor;db.StockEntries.Add(entry);
        var mat = await db.Materials.FindAsync(entry.MaterialId);
        var stock=await StockAsync(db,entry.WarehouseId.Value,entry.MaterialId); stock.Quantity+=entry.Quantity;
        if (mat != null) mat.PricePerUnit = entry.PricePerUnit;
        db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=Document("IN"),Type=InventoryTransactionType.Entry,WarehouseId=entry.WarehouseId.Value,MaterialId=entry.MaterialId,IncomingQuantity=entry.Quantity,BalanceAfter=stock.Quantity,UnitPrice=entry.PricePerUnit,TransactionDate=entry.EntryDate,Description=entry.Notes,CreatedByUserId=entry.CreatedByUserId,CreatedByUsername=entry.CreatedByUsername});
        await db.SaveChangesAsync(); await SyncTotalAsync(db,entry.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<StockEntry>> GetEntriesAsync(int? materialId = null, string? search = null, DateTime? from = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockEntries.Include(e => e.Material).Include(e=>e.Warehouse).AsQueryable();
        if (materialId.HasValue) q = q.Where(e => e.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(e => e.Material!.Name.Contains(search) || e.Material.Code.Contains(search));
        if (from.HasValue) q = q.Where(e => e.EntryDate >= from.Value);
        return await q.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).ToListAsync();
    }
    public async Task UpdateStockEntryAsync(StockEntry entry)
    {
        await using var db = await _factory.CreateDbContextAsync(); await using var tx=await db.Database.BeginTransactionAsync();
        var existing = await db.StockEntries.FindAsync(entry.Id);
        if (existing == null) return;
        var oldQty = existing.Quantity;
        var unit=await db.MaterialUnitConversions.FirstOrDefaultAsync(x=>x.MaterialId==existing.MaterialId&&x.UnitName==entry.EnteredUnitName&&x.IsActive)??throw new Exception("واحد ورود معتبر نیست");var newBaseQuantity=entry.EnteredQuantity*unit.FactorToBaseUnit;var difference=newBaseQuantity-oldQty;
        existing.EnteredQuantity=entry.EnteredQuantity;existing.EnteredUnitName=entry.EnteredUnitName;existing.ConversionFactor=unit.FactorToBaseUnit;
        existing.Quantity     = newBaseQuantity;
        existing.PricePerUnit = entry.PricePerUnit;
        existing.EntryDate    = entry.EntryDate;
        existing.ExpiryDate   = entry.ExpiryDate;
        existing.Notes        = entry.Notes;
        var mat = await db.Materials.FindAsync(existing.MaterialId);
        if(existing.WarehouseId.HasValue && difference!=0){var stock=await StockAsync(db,existing.WarehouseId.Value,existing.MaterialId);if(stock.Quantity+difference<0)throw new Exception("ویرایش باعث منفی شدن موجودی انبار می‌شود");stock.Quantity+=difference;db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=Document("ADJ"),Type=InventoryTransactionType.Adjustment,WarehouseId=existing.WarehouseId.Value,MaterialId=existing.MaterialId,IncomingQuantity=difference>0?difference:0,OutgoingQuantity=difference<0?-difference:0,BalanceAfter=stock.Quantity,UnitPrice=entry.PricePerUnit,TransactionDate=DateTime.Now,Description=$"اصلاح سند ورود شماره {entry.Id}",CreatedByUserId=entry.CreatedByUserId,CreatedByUsername=entry.CreatedByUsername});}
        if (mat != null)
        {
            mat.CurrentStock += difference;
            var lastEntry = await db.StockEntries
                .Where(e => e.MaterialId == mat.Id && e.Id != entry.Id)
                .OrderByDescending(e => e.EntryDate)
                .FirstOrDefaultAsync();
            mat.PricePerUnit = entry.EntryDate >= (lastEntry?.EntryDate ?? DateTime.MinValue)
                ? entry.PricePerUnit
                : (lastEntry?.PricePerUnit ?? mat.PricePerUnit);
        }
        await db.SaveChangesAsync(); await SyncTotalAsync(db,existing.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    // ── Stock Withdrawals ──
    public async Task AddWithdrawalAsync(StockWithdrawal w)
    {
        if(!w.WarehouseId.HasValue) throw new Exception("انبار را انتخاب کنید");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx=await db.Database.BeginTransactionAsync();
        var mat = await db.Materials.FindAsync(w.MaterialId);var unit=await db.MaterialUnitConversions.FirstOrDefaultAsync(x=>x.MaterialId==w.MaterialId&&x.UnitName==w.EnteredUnitName&&x.IsActive)??throw new Exception("واحد خروج معتبر نیست");w.EnteredQuantity=w.EnteredQuantity>0?w.EnteredQuantity:w.Quantity;w.ConversionFactor=unit.FactorToBaseUnit;w.Quantity=w.EnteredQuantity*w.ConversionFactor;
        if (mat == null) throw new Exception("ماده یافت نشد");
        var stock=await StockAsync(db,w.WarehouseId.Value,w.MaterialId); if(stock.Quantity<w.Quantity&&IsRoundingGap(stock.Quantity,w.Quantity)) w.Quantity=stock.Quantity; if(stock.Quantity<w.Quantity) throw new Exception($"موجودی کافی نیست (موجودی انبار: {stock.Quantity:N3})");
        stock.Quantity-=w.Quantity; w.Status=WithdrawalStatus.Approved; w.ApprovedAt=DateTime.UtcNow; w.ApprovedByUserId=w.CreatedByUserId; w.ApprovedByUsername=w.CreatedByUsername;var latestPurchase=await db.StockEntries.Where(x=>x.MaterialId==w.MaterialId).OrderByDescending(x=>x.EntryDate).ThenByDescending(x=>x.Id).FirstOrDefaultAsync();var pricePerBase=latestPurchase==null?0:latestPurchase.PricePerUnit/(latestPurchase.ConversionFactor<=0?1:latestPurchase.ConversionFactor);
        db.StockWithdrawals.Add(w); db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=Document("OUT"),Type=InventoryTransactionType.Withdrawal,WarehouseId=w.WarehouseId.Value,MaterialId=w.MaterialId,OutgoingQuantity=w.Quantity,BalanceAfter=stock.Quantity,UnitPrice=pricePerBase,TransactionDate=w.WithdrawalDate,Description=string.IsNullOrWhiteSpace(w.Department)?w.Reason:$"مصرف در بخش {w.Department}"+(string.IsNullOrWhiteSpace(w.Reason)?"":$" — {w.Reason}"),CreatedByUserId=w.CreatedByUserId,CreatedByUsername=w.CreatedByUsername});
        await db.SaveChangesAsync(); await SyncTotalAsync(db,w.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public async Task<List<StockWithdrawal>> GetWithdrawalsAsync(int? materialId = null, string? search = null, DateTime? from = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockWithdrawals.Include(w => w.Material).Include(w=>w.Warehouse).AsQueryable();
        if (materialId.HasValue) q = q.Where(w => w.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(w => w.Material!.Name.Contains(search) || w.Material.Code.Contains(search) || w.Department.Contains(search));
        if (from.HasValue) q = q.Where(w => w.WithdrawalDate >= from.Value);
        return await q.OrderByDescending(w => w.WithdrawalDate).ThenByDescending(w => w.Id).ToListAsync();
    }
    public async Task<List<string>> GetWithdrawalDepartmentsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.StockWithdrawals.Where(w => w.Department != "").Select(w => w.Department).Distinct().OrderBy(x => x).ToListAsync();
    }
    public async Task UpdateWithdrawalAsync(StockWithdrawal input,int userId,string username)
    {
        await using var db=await _factory.CreateDbContextAsync(); await using var tx=await db.Database.BeginTransactionAsync();
        var existing=await db.StockWithdrawals.FindAsync(input.Id)??throw new Exception("سند خروج یافت نشد");
        if(input.EnteredQuantity<=0) throw new Exception("مقدار باید بیشتر از صفر باشد");
        if(string.IsNullOrWhiteSpace(input.Department)) throw new Exception("بخش مصرف‌کننده را مشخص کنید");
        var unit=await db.MaterialUnitConversions.FirstOrDefaultAsync(x=>x.MaterialId==existing.MaterialId&&x.UnitName==input.EnteredUnitName&&x.IsActive)??throw new Exception("واحد خروج معتبر نیست");
        var newBaseQuantity=input.EnteredQuantity*unit.FactorToBaseUnit;
        var difference=existing.Status==WithdrawalStatus.Approved?newBaseQuantity-existing.Quantity:0;
        if(existing.WarehouseId.HasValue&&difference!=0)
        {
            var stock=await StockAsync(db,existing.WarehouseId.Value,existing.MaterialId);
            if(stock.Quantity-difference<0) throw new Exception($"موجودی کافی نیست (موجودی انبار: {stock.Quantity:N3})");
            stock.Quantity-=difference;
            var latestPurchase=await db.StockEntries.Where(x=>x.MaterialId==existing.MaterialId).OrderByDescending(x=>x.EntryDate).ThenByDescending(x=>x.Id).FirstOrDefaultAsync();
            var pricePerBase=latestPurchase==null?0:latestPurchase.PricePerUnit/(latestPurchase.ConversionFactor<=0?1:latestPurchase.ConversionFactor);
            db.InventoryTransactions.Add(new InventoryTransaction{DocumentNumber=Document("ADJ"),Type=InventoryTransactionType.Adjustment,WarehouseId=existing.WarehouseId.Value,MaterialId=existing.MaterialId,IncomingQuantity=difference<0?-difference:0,OutgoingQuantity=difference>0?difference:0,BalanceAfter=stock.Quantity,UnitPrice=pricePerBase,TransactionDate=DateTime.Now,Description=$"اصلاح سند خروج شماره {existing.Id}",CreatedByUserId=userId,CreatedByUsername=username});
        }
        existing.EnteredQuantity=input.EnteredQuantity; existing.EnteredUnitName=input.EnteredUnitName; existing.ConversionFactor=unit.FactorToBaseUnit; existing.Quantity=newBaseQuantity;
        existing.WithdrawalDate=input.WithdrawalDate; existing.Department=input.Department.Trim(); existing.Reason=input.Reason;
        await db.SaveChangesAsync(); await SyncTotalAsync(db,existing.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

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
    public async Task<List<StockAlert>> GetAlertsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var alerts = new List<StockAlert>();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var now = DateTime.UtcNow; var soon = now.AddDays(7);
        foreach (var mat in materials)
        {
            if (!string.IsNullOrWhiteSpace(search) && !mat.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !mat.Code.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            if (mat.CurrentStock <= mat.MinStockLevel)
                alerts.Add(new StockAlert { MaterialId=mat.Id, MaterialName=mat.Name, MaterialCode=mat.Code, Unit=mat.BaseUnitName, CurrentStock=mat.CurrentStock, MinStockLevel=mat.MinStockLevel, Type=AlertType.LowStock, Message=$"موجودی {mat.Name} ({mat.CurrentStock}) به حد هشدار رسیده" });
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
        var invalidPriceCount=await db.StockEntries.CountAsync(x=>x.PricePerUnit<100000&&!x.PriceConfirmed);
        var pendingCount = await db.StockWithdrawals.CountAsync(w => w.Status == WithdrawalStatus.Pending);
        var latestEntries = await db.StockEntries
            .OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id)
            .ToListAsync();
        var totalValue = materials.Sum(m =>
        {
            var entry = latestEntries.FirstOrDefault(x => x.MaterialId == m.Id);
            var basePrice = entry == null ? 0 : entry.PricePerUnit / (entry.ConversionFactor <= 0 ? 1 : entry.ConversionFactor);
            return m.CurrentStock * basePrice;
        });
        return (totalValue, materials.Count, materials.Count(m => m.CurrentStock <= m.MinStockLevel), alerts.Count+invalidPriceCount, pendingCount);
    }

    public async Task<(List<Material> materials, List<StockEntry> entries, List<StockWithdrawal> withdrawals, List<StockAlert> alerts)> GetExportDataAsync()
    {
        var mats = await GetMaterialsAsync(); var entries = await GetEntriesAsync();
        var withdrawals = await GetWithdrawalsAsync(); var als = await GetAlertsAsync();
        return (mats, entries, withdrawals, als);
    }

    public async Task<List<StockEntry>> GetInvalidPriceEntriesAsync(string? search=null)
    {
        await using var db=await _factory.CreateDbContextAsync();var q=db.StockEntries.Include(x=>x.Material).Include(x=>x.Warehouse).Where(x=>x.PricePerUnit<100000&&!x.PriceConfirmed);
        if(!string.IsNullOrWhiteSpace(search))q=q.Where(x=>x.Material!.Name.Contains(search)||x.Material.Code.Contains(search));
        return await q.OrderByDescending(x=>x.EntryDate).ToListAsync();
    }
    public async Task ConfirmEntryPriceAsync(int id,string username){await using var db=await _factory.CreateDbContextAsync();var e=await db.StockEntries.FindAsync(id);if(e==null)return;e.PriceConfirmed=true;e.PriceConfirmedBy=username;e.PriceConfirmedAt=DateTime.UtcNow;await db.SaveChangesAsync();}
    public async Task CorrectEntryPriceAsync(int id,decimal price,string username)
    {
        if(price<=0)throw new Exception("مبلغ باید بیشتر از صفر باشد");await using var db=await _factory.CreateDbContextAsync();var e=await db.StockEntries.FindAsync(id);if(e==null)return;e.PricePerUnit=price;e.PriceConfirmed=price<100000;e.PriceConfirmedBy=username;e.PriceConfirmedAt=DateTime.UtcNow;
        var cardexCandidates=await db.InventoryTransactions.Where(x=>x.Type==InventoryTransactionType.Entry&&x.MaterialId==e.MaterialId&&x.WarehouseId==e.WarehouseId&&x.TransactionDate.Date==e.EntryDate.Date&&x.IncomingQuantity==e.Quantity).ToListAsync();var cardexEntry=cardexCandidates.OrderBy(x=>Math.Abs((x.CreatedAt-e.CreatedAt).Ticks)).FirstOrDefault();if(cardexEntry!=null)cardexEntry.UnitPrice=price;
        var latest=await db.StockEntries.Where(x=>x.MaterialId==e.MaterialId).OrderByDescending(x=>x.EntryDate).ThenByDescending(x=>x.Id).FirstOrDefaultAsync();var m=await db.Materials.FindAsync(e.MaterialId);if(m!=null&&latest?.Id==e.Id)m.PricePerUnit=price;await db.SaveChangesAsync();
    }

    public async Task<MovementDashboard> GetMovementDashboardAsync(DateTime from,DateTime to,int? warehouseId=null,int? materialId=null)
    {
        await using var db=await _factory.CreateDbContextAsync();var end=to.Date.AddDays(1);
        var entries=await db.StockEntries.Include(x=>x.Material).ThenInclude(x=>x!.Unit).Where(x=>x.EntryDate>=from.Date&&x.EntryDate<end&&(!warehouseId.HasValue||x.WarehouseId==warehouseId)&&(!materialId.HasValue||x.MaterialId==materialId)).ToListAsync();
        var outs=await db.StockWithdrawals.Include(x=>x.Material).ThenInclude(x=>x!.Unit).Where(x=>x.Status==WithdrawalStatus.Approved&&x.WithdrawalDate>=from.Date&&x.WithdrawalDate<end&&(!warehouseId.HasValue||x.WarehouseId==warehouseId)&&(!materialId.HasValue||x.MaterialId==materialId)).ToListAsync();
        var txPrices=await db.InventoryTransactions.Where(x=>x.Type==InventoryTransactionType.Withdrawal&&x.TransactionDate>=from.Date&&x.TransactionDate<end).ToListAsync();
        var ids=entries.Select(x=>x.MaterialId).Concat(outs.Select(x=>x.MaterialId)).Distinct().ToList();var priceHistory=await db.StockEntries.Where(x=>ids.Contains(x.MaterialId)&&x.EntryDate<end).ToListAsync();var result=new MovementDashboard();
        foreach(var id in ids){var e=entries.Where(x=>x.MaterialId==id).ToList();var o=outs.Where(x=>x.MaterialId==id).ToList();var mat=e.FirstOrDefault()?.Material??o.FirstOrDefault()?.Material;var row=new MovementDashboardRow{MaterialId=id,MaterialCode=mat?.Code??"",MaterialName=mat?.Name??"",Unit=mat?.BaseUnitName??"",IncomingQuantity=e.Sum(x=>x.Quantity),IncomingValue=e.Sum(x=>(x.EnteredQuantity>0?x.EnteredQuantity:x.Quantity)*x.PricePerUnit),OutgoingQuantity=o.Sum(x=>x.Quantity)};foreach(var w in o){var price=txPrices.Where(x=>x.MaterialId==id&&x.WarehouseId==w.WarehouseId).OrderBy(x=>Math.Abs((x.CreatedAt-w.CreatedAt).Ticks)).Select(x=>x.UnitPrice).FirstOrDefault();if(price<=0){var pe=priceHistory.Where(x=>x.MaterialId==id&&x.EntryDate<=w.WithdrawalDate).OrderByDescending(x=>x.EntryDate).ThenByDescending(x=>x.Id).FirstOrDefault();price=pe==null?0:pe.PricePerUnit/(pe.ConversionFactor<=0?1:pe.ConversionFactor);row.HasEstimatedOutgoingValue=true;}row.OutgoingValue+=w.Quantity*price;}result.Rows.Add(row);}return result;
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
        var resultLines = new List<RecipeCostLine>();

        foreach (var ing in recipe.Ingredients)
        {
            var lastEntry = await db.StockEntries
                .Where(e => e.MaterialId == ing.MaterialId)
                .OrderByDescending(e=>e.EntryDate).ThenByDescending(e=>e.Id).FirstOrDefaultAsync();

            // ضریب تبدیل: چند واحد پایه در هر واحد انبار
            var material = await db.Materials.Include(x=>x.Unit).FirstOrDefaultAsync(x=>x.Id==ing.MaterialId);
            decimal lastPricePerStockUnit = lastEntry?.PricePerUnit ?? material?.PricePerUnit ?? 0;
            var purchaseFactor=lastEntry?.ConversionFactor??material?.BaseQuantity??1;if(purchaseFactor<=0)purchaseFactor=1;
            var ingredientFactor=await db.MaterialUnitConversions.Where(x=>x.MaterialId==ing.MaterialId&&x.UnitName==ing.UnitName&&x.IsActive).Select(x=>x.FactorToBaseUnit).FirstOrDefaultAsync();if(ingredientFactor<=0)ingredientFactor=1;

            // قیمت هر واحد پایه (مثلاً هر کیلو)
            decimal pricePerBase = lastPricePerStockUnit / purchaseFactor;

            // هزینه این قلم = مقدار (بر اساس واحد پایه) × قیمت هر واحد پایه
            var baseQuantity=ing.Quantity*ingredientFactor;var lineCost = baseQuantity * pricePerBase;
            resultLines.Add(new RecipeCostLine{MaterialId=ing.MaterialId,MaterialCode=material?.Code??"",MaterialName=material?.Name??"",IsTopping=ing.IsTopping,Quantity=ing.Quantity,InputUnitName=ing.UnitName,BaseQuantity=baseQuantity,BaseUnitName=material?.BaseUnitName??"",StockUnitName=lastEntry?.EnteredUnitName??material?.Unit?.Name??"",ConversionFactor=ingredientFactor,LastPurchasePrice=lastPricePerStockUnit,PricePerBaseUnit=pricePerBase,LineCost=lineCost});
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
        decimal totalMainWeight = resultLines.Where(i=>!i.IsTopping).Sum(i=>i.BaseUnitName.Contains("گرم")&&!i.BaseUnitName.Contains("کیلو")?i.BaseQuantity/1000m:i.BaseQuantity);

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
            ProfitPercent        = profitPercent,
            Lines                = resultLines
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
    public List<RecipeCostLine> Lines { get; set; } = new();
}

public class RecipeCostLine
{
    public int MaterialId { get;set; } public string MaterialCode {get;set;}=""; public string MaterialName {get;set;}=""; public bool IsTopping {get;set;}
    public decimal Quantity {get;set;} public string InputUnitName{get;set;}="";public decimal BaseQuantity{get;set;} public string BaseUnitName {get;set;}=""; public string StockUnitName {get;set;}=""; public decimal ConversionFactor {get;set;}
    public decimal LastPurchasePrice {get;set;} public decimal PricePerBaseUnit {get;set;} public decimal LineCost {get;set;}
}

public class MovementDashboard{public List<MovementDashboardRow> Rows{get;set;}=new();public decimal IncomingQuantity=>Rows.Sum(x=>x.IncomingQuantity);public decimal OutgoingQuantity=>Rows.Sum(x=>x.OutgoingQuantity);public decimal IncomingValue=>Rows.Sum(x=>x.IncomingValue);public decimal OutgoingValue=>Rows.Sum(x=>x.OutgoingValue);}
public class MovementDashboardRow{public int MaterialId{get;set;}public string MaterialCode{get;set;}="";public string MaterialName{get;set;}="";public string Unit{get;set;}="";public decimal IncomingQuantity{get;set;}public decimal OutgoingQuantity{get;set;}public decimal IncomingValue{get;set;}public decimal OutgoingValue{get;set;}public bool HasEstimatedOutgoingValue{get;set;}}
internal record CardexRebuildEvent(DateTime Date,int Order,long Id,int Warehouse,decimal In,decimal Out,decimal Price,string Description,int UserId,string User,DateTime Created,string Doc,InventoryTransaction? Existing);
