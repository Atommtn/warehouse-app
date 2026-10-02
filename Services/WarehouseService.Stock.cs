using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

// Materials, units and every stock movement. All quantities are stored in the material's stock unit.
public partial class WarehouseService
{
    // Withdrawing "everything" in a smaller unit (e.g. 6 × 1 شانه of 1 کارتن) can overshoot by a rounding hair.
    private const decimal RoundingTolerance = 0.000001m;
    private static bool IsRoundingGap(decimal available, decimal requested) => available > 0 && requested - available <= RoundingTolerance;

    // ── Materials ──
    public async Task<List<Material>> GetMaterialsAsync(string? search = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var list = await db.Materials.Include(m => m.Supplier).Include(m => m.Unit).Include(m => m.Group).Where(m => m.IsActive).OrderBy(m => m.Code).ToListAsync();
        return string.IsNullOrWhiteSpace(search) ? list : list.Where(m => TextSearch.Matches(search, m.Name, m.Code)).ToList();
    }

    /// <summary>Ids of materials whose name or code matches, with Persian normalization (SQL LIKE cannot do that).</summary>
    private static async Task<List<int>> MatchingMaterialIdsAsync(AppDbContext db, string search)
    {
        var all = await db.Materials.Select(m => new { m.Id, m.Name, m.Code }).ToListAsync();
        return all.Where(m => TextSearch.Matches(search, m.Name, m.Code)).Select(m => m.Id).ToList();
    }

    public async Task AddMaterialAsync(Material m, decimal recipeUnitsPerStockUnit = 0, bool approximate = false)
    {
        m.StockUnitName = m.StockUnitName.Trim(); m.RecipeUnitName = m.RecipeUnitName.Trim();
        if (string.IsNullOrWhiteSpace(m.StockUnitName)) throw new Exception("واحد موجودی را انتخاب کنید");
        if (string.IsNullOrWhiteSpace(m.RecipeUnitName)) m.RecipeUnitName = m.StockUnitName;
        var rows = new List<MaterialUnitConversion> { new() { UnitName = m.StockUnitName, DefinedRefUnit = m.StockUnitName, DefinedAmount = 1 } };
        if (!UnitSet.Same(m.StockUnitName, m.RecipeUnitName) && UnitSet.StandardFactor(m.StockUnitName, m.RecipeUnitName) == null)
        {
            if (recipeUnitsPerStockUnit <= 0) throw new Exception($"بگویید هر ۱ {m.StockUnitName} چند {m.RecipeUnitName} است");
            rows.Add(new() { UnitName = m.RecipeUnitName, DefinedCount = recipeUnitsPerStockUnit, DefinedAmount = 1, DefinedRefUnit = m.StockUnitName, IsApproximate = approximate });
        }
        UnitSet.Resolve(m.StockUnitName, rows);
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        m.UnitId = await db.Units.Where(u => u.Name == m.StockUnitName).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        m.BaseQuantity = 1; m.UnitsNormalized = true;
        db.Materials.Add(m); await db.SaveChangesAsync();
        foreach (var r in rows) { r.MaterialId = m.Id; db.MaterialUnitConversions.Add(r); }
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    /// <summary>Saves the descriptive fields only; units are changed through <see cref="SaveMaterialUnitsAsync"/>.</summary>
    public async Task UpdateMaterialAsync(Material input)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var m = await db.Materials.FindAsync(input.Id) ?? throw new Exception("ماده یافت نشد");
        m.Code = input.Code; m.Name = input.Name; m.GroupId = input.GroupId; m.SupplierId = input.SupplierId; m.Notes = input.Notes;
        if (m.MinStockLevel != input.MinStockLevel)
        {
            m.MinStockLevel = input.MinStockLevel;
            foreach (var s in await db.WarehouseStocks.Where(x => x.MaterialId == m.Id).ToListAsync()) s.MinStockLevel = input.MinStockLevel;
        }
        await db.SaveChangesAsync();
    }

    // ── Units ──
    public async Task<UnitSet> GetUnitSetAsync(int materialId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var m = await db.Materials.AsNoTracking().FirstOrDefaultAsync(x => x.Id == materialId) ?? throw new Exception("ماده یافت نشد");
        return UnitSet.For(m, await db.MaterialUnitConversions.AsNoTracking().Where(x => x.MaterialId == materialId).ToListAsync());
    }

    public async Task<StockDisplay> GetStockDisplayAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.AsNoTracking().ToListAsync();
        var rows = (await db.MaterialUnitConversions.AsNoTracking().ToListAsync()).ToLookup(x => x.MaterialId);
        return new StockDisplay(materials.ToDictionary(m => m.Id, m => UnitSet.For(m, rows[m.Id])));
    }

    /// <summary>The unit definitions as the user wrote them, for the unit editor.</summary>
    public async Task<List<MaterialUnitConversion>> GetMaterialUnitRowsAsync(int materialId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.MaterialUnitConversions.AsNoTracking().Where(x => x.MaterialId == materialId && x.IsActive).OrderBy(x => x.PerStockUnit).ThenBy(x => x.UnitName).ToListAsync();
    }

    /// <summary>
    /// Saves a material's units. Changing a relation only changes prices of that unit; stock and history stay as counted.
    /// Only changing the stock unit itself converts the stored quantities (e.g. 37.8 کیلوگرم → 3 کارتن).
    /// </summary>
    public async Task SaveMaterialUnitsAsync(int materialId, string stockUnit, string recipeUnit, List<MaterialUnitConversion> rows)
    {
        stockUnit = stockUnit?.Trim() ?? ""; recipeUnit = recipeUnit?.Trim() ?? "";
        if (stockUnit == "") throw new Exception("واحد موجودی را انتخاب کنید");
        if (recipeUnit == "") throw new Exception("واحد رسپی را انتخاب کنید");
        rows = rows.Where(x => !string.IsNullOrWhiteSpace(x.UnitName)).Select(x => new MaterialUnitConversion
        {
            UnitName = x.UnitName.Trim(), DefinedCount = x.DefinedCount, DefinedAmount = x.DefinedAmount, DefinedRefUnit = x.DefinedRefUnit?.Trim() ?? "", IsApproximate = x.IsApproximate, IsActive = true,
        }).ToList();
        if (rows.Select(x => x.UnitName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count) throw new Exception("نام واحد تکراری است");
        if (!rows.Any(x => UnitSet.Same(x.UnitName, stockUnit))) rows.Insert(0, new MaterialUnitConversion { UnitName = stockUnit, IsActive = true });
        UnitSet.Resolve(stockUnit, rows);

        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var m = await db.Materials.FindAsync(materialId) ?? throw new Exception("ماده یافت نشد");
        var newSet = new UnitSet(stockUnit, recipeUnit, rows);
        if (!newSet.Has(recipeUnit)) throw new Exception($"رابطه‌ی واحد رسپی «{recipeUnit}» با {stockUnit} را مشخص کنید");
        var oldStock = string.IsNullOrWhiteSpace(m.StockUnitName) ? stockUnit : m.StockUnitName;
        if (!UnitSet.Same(oldStock, stockUnit))
        {
            if (!newSet.Has(oldStock)) throw new Exception($"واحد موجودی قبلی «{oldStock}» را در لیست نگه دارید تا موجودی فعلی به {stockUnit} تبدیل شود");
            await RescaleStockAsync(db, materialId, newSet.PerStockUnit(oldStock));
        }
        db.MaterialUnitConversions.RemoveRange(await db.MaterialUnitConversions.Where(x => x.MaterialId == materialId).ToListAsync());
        await db.SaveChangesAsync();
        foreach (var r in rows) { r.MaterialId = materialId; db.MaterialUnitConversions.Add(r); }
        m.StockUnitName = stockUnit; m.RecipeUnitName = recipeUnit;
        m.UnitId = await db.Units.Where(u => u.Name == stockUnit).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    /// <summary>Re-expresses every stored quantity of a material in a new stock unit; <paramref name="oldUnitsPerNew"/> = old units in one new unit.</summary>
    internal static async Task RescaleStockAsync(AppDbContext db, int materialId, decimal oldUnitsPerNew)
    {
        if (oldUnitsPerNew <= 0 || oldUnitsPerNew == 1) return;
        decimal Q(decimal q) => Math.Round(q / oldUnitsPerNew, UnitSet.QuantityScale);
        decimal P(decimal p) => Math.Round(p * oldUnitsPerNew, 2);
        var m = await db.Materials.FindAsync(materialId);
        if (m != null) { m.CurrentStock = Q(m.CurrentStock); m.MinStockLevel = Q(m.MinStockLevel); m.PricePerUnit = P(m.PricePerUnit); }
        foreach (var s in await db.WarehouseStocks.Where(x => x.MaterialId == materialId).ToListAsync()) { s.Quantity = Q(s.Quantity); s.MinStockLevel = Q(s.MinStockLevel); }
        foreach (var e in await db.StockEntries.Where(x => x.MaterialId == materialId).ToListAsync())
        {
            e.Quantity = Q(e.Quantity); e.PricePerUnit = e.TotalPrice > 0 && e.Quantity > 0 ? Math.Round(e.TotalPrice / e.Quantity, 2) : P(e.PricePerUnit);
            e.ConversionFactor = e.EnteredQuantity > 0 ? Math.Round(e.Quantity / e.EnteredQuantity, UnitSet.QuantityScale) : 1;
        }
        foreach (var w in await db.StockWithdrawals.Where(x => x.MaterialId == materialId).ToListAsync())
        {
            w.Quantity = Q(w.Quantity); w.ConversionFactor = w.EnteredQuantity > 0 ? Math.Round(w.Quantity / w.EnteredQuantity, UnitSet.QuantityScale) : 1;
        }
        foreach (var t in await db.InventoryTransactions.Where(x => x.MaterialId == materialId).ToListAsync())
        { t.IncomingQuantity = Q(t.IncomingQuantity); t.OutgoingQuantity = Q(t.OutgoingQuantity); t.BalanceAfter = Q(t.BalanceAfter); t.UnitPrice = P(t.UnitPrice); }
    }

    private async Task<(Material Material, UnitSet Units)> MaterialUnitsAsync(AppDbContext db, int materialId)
    {
        var m = await db.Materials.FindAsync(materialId) ?? throw new Exception("ماده یافت نشد");
        return (m, UnitSet.For(m, await db.MaterialUnitConversions.AsNoTracking().Where(x => x.MaterialId == materialId).ToListAsync()));
    }

    private static async Task RefreshMaterialPriceAsync(AppDbContext db, Material m)
    {
        var latest = await db.StockEntries.Where(x => x.MaterialId == m.Id).OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        if (latest != null) m.PricePerUnit = Math.Round(latest.PricePerStockUnit, 2);
    }

    private static async Task<decimal> LatestPricePerStockUnitAsync(AppDbContext db, int materialId, DateTime? until = null)
    {
        var q = db.StockEntries.Where(x => x.MaterialId == materialId);
        if (until.HasValue) q = q.Where(x => x.EntryDate <= until.Value);
        var e = await q.OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id).FirstOrDefaultAsync();
        return e?.PricePerStockUnit ?? 0;
    }

    // ── Stock Entries ──
    /// <summary>Registers a purchase: <see cref="StockEntry.EnteredQuantity"/> in <see cref="StockEntry.EnteredUnitName"/> for <see cref="StockEntry.TotalPrice"/>.</summary>
    public async Task AddStockEntryAsync(StockEntry entry)
    {
        if (!entry.WarehouseId.HasValue || entry.WarehouseId == 0) throw new Exception("انبار را انتخاب کنید");
        if (entry.EnteredQuantity <= 0) throw new Exception("مقدار باید بیشتر از صفر باشد");
        if (entry.TotalPrice < 0) throw new Exception("مبلغ نمی‌تواند منفی باشد");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var (mat, units) = await MaterialUnitsAsync(db, entry.MaterialId);
        entry.Quantity = units.ToStock(entry.EnteredQuantity, entry.EnteredUnitName);
        entry.ConversionFactor = Math.Round(entry.Quantity / entry.EnteredQuantity, UnitSet.QuantityScale);
        entry.PricePerUnit = entry.Quantity > 0 ? Math.Round(entry.TotalPrice / entry.Quantity, 2) : 0;
        db.StockEntries.Add(entry);
        var stock = await StockAsync(db, entry.WarehouseId.Value, entry.MaterialId); stock.Quantity += entry.Quantity;
        db.InventoryTransactions.Add(new InventoryTransaction { DocumentNumber = Document("IN"), Type = InventoryTransactionType.Entry, WarehouseId = entry.WarehouseId.Value, MaterialId = entry.MaterialId, IncomingQuantity = entry.Quantity, BalanceAfter = stock.Quantity, UnitPrice = entry.PricePerUnit, TransactionDate = entry.EntryDate, Description = entry.Notes, CreatedByUserId = entry.CreatedByUserId, CreatedByUsername = entry.CreatedByUsername });
        await db.SaveChangesAsync(); await RefreshMaterialPriceAsync(db, mat); await SyncTotalAsync(db, entry.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<List<StockEntry>> GetEntriesAsync(int? materialId = null, string? search = null, DateTime? from = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockEntries.Include(e => e.Material).Include(e => e.Warehouse).AsQueryable();
        if (materialId.HasValue) q = q.Where(e => e.MaterialId == materialId);
        if (!string.IsNullOrWhiteSpace(search)) { var ids = await MatchingMaterialIdsAsync(db, search); q = q.Where(e => ids.Contains(e.MaterialId)); }
        if (from.HasValue) q = q.Where(e => e.EntryDate >= from.Value);
        return await q.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).ToListAsync();
    }

    public async Task UpdateStockEntryAsync(StockEntry input)
    {
        if (input.EnteredQuantity <= 0) throw new Exception("مقدار باید بیشتر از صفر باشد");
        if (input.TotalPrice < 0) throw new Exception("مبلغ نمی‌تواند منفی باشد");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var existing = await db.StockEntries.FindAsync(input.Id) ?? throw new Exception("سند ورود یافت نشد");
        var (mat, units) = await MaterialUnitsAsync(db, existing.MaterialId);
        var newQuantity = units.ToStock(input.EnteredQuantity, input.EnteredUnitName);
        var difference = newQuantity - existing.Quantity;
        existing.EnteredQuantity = input.EnteredQuantity; existing.EnteredUnitName = input.EnteredUnitName.Trim();
        existing.Quantity = newQuantity; existing.ConversionFactor = Math.Round(newQuantity / input.EnteredQuantity, UnitSet.QuantityScale);
        existing.TotalPrice = input.TotalPrice; existing.PricePerUnit = newQuantity > 0 ? Math.Round(input.TotalPrice / newQuantity, 2) : 0;
        existing.EntryDate = input.EntryDate; existing.ExpiryDate = input.ExpiryDate; existing.Notes = input.Notes;
        if (existing.WarehouseId.HasValue && difference != 0)
        {
            var stock = await StockAsync(db, existing.WarehouseId.Value, existing.MaterialId);
            if (stock.Quantity + difference < -RoundingTolerance) throw new Exception("ویرایش باعث منفی شدن موجودی انبار می‌شود");
            stock.Quantity = Math.Max(0, stock.Quantity + difference);
            db.InventoryTransactions.Add(new InventoryTransaction { DocumentNumber = Document("ADJ"), Type = InventoryTransactionType.Adjustment, WarehouseId = existing.WarehouseId.Value, MaterialId = existing.MaterialId, IncomingQuantity = difference > 0 ? difference : 0, OutgoingQuantity = difference < 0 ? -difference : 0, BalanceAfter = stock.Quantity, UnitPrice = existing.PricePerUnit, TransactionDate = DateTime.Now, Description = $"اصلاح سند ورود شماره {existing.Id}", CreatedByUserId = input.CreatedByUserId, CreatedByUsername = input.CreatedByUsername });
        }
        await db.SaveChangesAsync(); await RefreshMaterialPriceAsync(db, mat); await SyncTotalAsync(db, existing.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    /// <summary>Price correction from the alerts page: <paramref name="price"/> is per stock unit.</summary>
    public async Task CorrectEntryPriceAsync(int id, decimal price, string username)
    {
        if (price <= 0) throw new Exception("مبلغ باید بیشتر از صفر باشد");
        await using var db = await _factory.CreateDbContextAsync();
        var e = await db.StockEntries.FindAsync(id); if (e == null) return;
        e.PricePerUnit = price; e.TotalPrice = Math.Round(price * e.Quantity, 0);
        e.PriceConfirmed = price < 100000; e.PriceConfirmedBy = username; e.PriceConfirmedAt = DateTime.UtcNow;
        var cardexEntry = (await db.InventoryTransactions.Where(x => x.Type == InventoryTransactionType.Entry && x.MaterialId == e.MaterialId && x.WarehouseId == e.WarehouseId && x.TransactionDate.Date == e.EntryDate.Date && x.IncomingQuantity == e.Quantity).ToListAsync())
            .OrderBy(x => Math.Abs((x.CreatedAt - e.CreatedAt).Ticks)).FirstOrDefault();
        if (cardexEntry != null) cardexEntry.UnitPrice = price;
        await db.SaveChangesAsync();
        var m = await db.Materials.FindAsync(e.MaterialId); if (m != null) { await RefreshMaterialPriceAsync(db, m); await db.SaveChangesAsync(); }
    }

    // ── Stock Withdrawals ──
    public async Task AddWithdrawalAsync(StockWithdrawal w)
    {
        if (!w.WarehouseId.HasValue || w.WarehouseId == 0) throw new Exception("انبار را انتخاب کنید");
        if (w.EnteredQuantity <= 0) throw new Exception("مقدار باید بیشتر از صفر باشد");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var (_, units) = await MaterialUnitsAsync(db, w.MaterialId);
        w.Quantity = units.ToStock(w.EnteredQuantity, w.EnteredUnitName);
        w.ConversionFactor = Math.Round(w.Quantity / w.EnteredQuantity, UnitSet.QuantityScale);
        var stock = await StockAsync(db, w.WarehouseId.Value, w.MaterialId);
        if (stock.Quantity < w.Quantity && IsRoundingGap(stock.Quantity, w.Quantity)) w.Quantity = stock.Quantity;
        if (stock.Quantity < w.Quantity) throw new Exception($"موجودی کافی نیست (موجودی این انبار: {units.Format(stock.Quantity)})");
        stock.Quantity -= w.Quantity; w.Status = WithdrawalStatus.Approved; w.ApprovedAt = DateTime.UtcNow; w.ApprovedByUserId = w.CreatedByUserId; w.ApprovedByUsername = w.CreatedByUsername;
        var price = Math.Round(await LatestPricePerStockUnitAsync(db, w.MaterialId), 2);
        db.StockWithdrawals.Add(w);
        db.InventoryTransactions.Add(new InventoryTransaction { DocumentNumber = Document("OUT"), Type = InventoryTransactionType.Withdrawal, WarehouseId = w.WarehouseId.Value, MaterialId = w.MaterialId, OutgoingQuantity = w.Quantity, BalanceAfter = stock.Quantity, UnitPrice = price, TransactionDate = w.WithdrawalDate, Description = string.IsNullOrWhiteSpace(w.Department) ? w.Reason : $"مصرف در بخش {w.Department}" + (string.IsNullOrWhiteSpace(w.Reason) ? "" : $" — {w.Reason}"), CreatedByUserId = w.CreatedByUserId, CreatedByUsername = w.CreatedByUsername });
        await db.SaveChangesAsync(); await SyncTotalAsync(db, w.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<List<StockWithdrawal>> GetWithdrawalsAsync(int? materialId = null, string? search = null, DateTime? from = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var q = db.StockWithdrawals.Include(w => w.Material).Include(w => w.Warehouse).AsQueryable();
        if (materialId.HasValue) q = q.Where(w => w.MaterialId == materialId);
        if (from.HasValue) q = q.Where(w => w.WithdrawalDate >= from.Value);
        var list = await q.OrderByDescending(w => w.WithdrawalDate).ThenByDescending(w => w.Id).ToListAsync();
        return string.IsNullOrWhiteSpace(search) ? list : list.Where(w => TextSearch.Matches(search, w.Material?.Name, w.Material?.Code, w.Department)).ToList();
    }

    public async Task<List<string>> GetWithdrawalDepartmentsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.StockWithdrawals.Where(w => w.Department != "").Select(w => w.Department).Distinct().OrderBy(x => x).ToListAsync();
    }

    public async Task UpdateWithdrawalAsync(StockWithdrawal input, int userId, string username)
    {
        if (input.EnteredQuantity <= 0) throw new Exception("مقدار باید بیشتر از صفر باشد");
        if (string.IsNullOrWhiteSpace(input.Department)) throw new Exception("بخش مصرف‌کننده را مشخص کنید");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var existing = await db.StockWithdrawals.FindAsync(input.Id) ?? throw new Exception("سند خروج یافت نشد");
        var (_, units) = await MaterialUnitsAsync(db, existing.MaterialId);
        var newQuantity = units.ToStock(input.EnteredQuantity, input.EnteredUnitName);
        var difference = existing.Status == WithdrawalStatus.Approved ? newQuantity - existing.Quantity : 0;
        if (existing.WarehouseId.HasValue && difference != 0)
        {
            var stock = await StockAsync(db, existing.WarehouseId.Value, existing.MaterialId);
            if (stock.Quantity < difference && IsRoundingGap(stock.Quantity, difference)) { difference = stock.Quantity; newQuantity = existing.Quantity + difference; }
            if (stock.Quantity < difference) throw new Exception($"موجودی کافی نیست (موجودی این انبار: {units.Format(stock.Quantity)})");
            stock.Quantity -= difference;
            var price = Math.Round(await LatestPricePerStockUnitAsync(db, existing.MaterialId), 2);
            db.InventoryTransactions.Add(new InventoryTransaction { DocumentNumber = Document("ADJ"), Type = InventoryTransactionType.Adjustment, WarehouseId = existing.WarehouseId.Value, MaterialId = existing.MaterialId, IncomingQuantity = difference < 0 ? -difference : 0, OutgoingQuantity = difference > 0 ? difference : 0, BalanceAfter = stock.Quantity, UnitPrice = price, TransactionDate = DateTime.Now, Description = $"اصلاح سند خروج شماره {existing.Id}", CreatedByUserId = userId, CreatedByUsername = username });
        }
        existing.EnteredQuantity = input.EnteredQuantity; existing.EnteredUnitName = input.EnteredUnitName.Trim();
        existing.Quantity = newQuantity; existing.ConversionFactor = Math.Round(newQuantity / input.EnteredQuantity, UnitSet.QuantityScale);
        existing.WithdrawalDate = input.WithdrawalDate; existing.Department = input.Department.Trim(); existing.Reason = input.Reason;
        await db.SaveChangesAsync(); await SyncTotalAsync(db, existing.MaterialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    // ── Transfers ──
    public async Task TransferAsync(int sourceId, int destinationId, int materialId, decimal enteredQuantity, string unitName, DateTime date, string notes, int userId, string username)
    {
        if (sourceId == destinationId) throw new Exception("انبار مبدأ و مقصد نمی‌توانند یکسان باشند");
        if (enteredQuantity <= 0) throw new Exception("مقدار انتقال باید بیشتر از صفر باشد");
        await using var db = await _factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        var (_, units) = await MaterialUnitsAsync(db, materialId);
        var quantity = units.ToStock(enteredQuantity, unitName);
        var source = await StockAsync(db, sourceId, materialId);
        if (source.Quantity < quantity && IsRoundingGap(source.Quantity, quantity)) quantity = source.Quantity;
        if (source.Quantity < quantity) throw new Exception($"موجودی انبار مبدأ کافی نیست (موجودی: {units.Format(source.Quantity)})");
        var destination = await StockAsync(db, destinationId, materialId); source.Quantity -= quantity; destination.Quantity += quantity;
        var unitPrice = Math.Round(await LatestPricePerStockUnitAsync(db, materialId), 2);
        var doc = Document("TR");
        db.InventoryTransactions.AddRange(
            new InventoryTransaction { DocumentNumber = doc, Type = InventoryTransactionType.TransferOut, WarehouseId = sourceId, RelatedWarehouseId = destinationId, MaterialId = materialId, OutgoingQuantity = quantity, BalanceAfter = source.Quantity, UnitPrice = unitPrice, TransactionDate = date, Description = notes, CreatedByUserId = userId, CreatedByUsername = username },
            new InventoryTransaction { DocumentNumber = doc, Type = InventoryTransactionType.TransferIn, WarehouseId = destinationId, RelatedWarehouseId = sourceId, MaterialId = materialId, IncomingQuantity = quantity, BalanceAfter = destination.Quantity, UnitPrice = unitPrice, TransactionDate = date, Description = notes, CreatedByUserId = userId, CreatedByUsername = username });
        await db.SaveChangesAsync(); await SyncTotalAsync(db, materialId); await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    // ── Dashboard ──
    public async Task<(decimal totalValue, int totalMaterials, int lowStockCount, int alertCount, int pendingCount, int invalidPriceCount)> GetDashboardStatsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var alerts = await GetAlertsAsync();
        var invalidPriceCount = await db.StockEntries.CountAsync(x => x.PricePerUnit < 100000 && !x.PriceConfirmed);
        var pendingCount = await db.StockWithdrawals.CountAsync(w => w.Status == WithdrawalStatus.Pending);
        var latest = (await db.StockEntries.OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id).ToListAsync()).GroupBy(x => x.MaterialId).ToDictionary(g => g.Key, g => g.First().PricePerStockUnit);
        var totalValue = materials.Sum(m => m.CurrentStock * latest.GetValueOrDefault(m.Id));
        return (totalValue, materials.Count, materials.Count(IsLowStock), alerts.Count, pendingCount, invalidPriceCount);
    }

    public async Task<MovementDashboard> GetMovementDashboardAsync(DateTime from, DateTime to, int? warehouseId = null, int? materialId = null)
    {
        await using var db = await _factory.CreateDbContextAsync(); var end = to.Date.AddDays(1);
        var entries = await db.StockEntries.Include(x => x.Material).Where(x => x.EntryDate >= from.Date && x.EntryDate < end && (!warehouseId.HasValue || x.WarehouseId == warehouseId) && (!materialId.HasValue || x.MaterialId == materialId)).ToListAsync();
        var outs = await db.StockWithdrawals.Include(x => x.Material).Where(x => x.Status == WithdrawalStatus.Approved && x.WithdrawalDate >= from.Date && x.WithdrawalDate < end && (!warehouseId.HasValue || x.WarehouseId == warehouseId) && (!materialId.HasValue || x.MaterialId == materialId)).ToListAsync();
        var txPrices = await db.InventoryTransactions.Where(x => x.Type == InventoryTransactionType.Withdrawal && x.TransactionDate >= from.Date && x.TransactionDate < end).ToListAsync();
        var ids = entries.Select(x => x.MaterialId).Concat(outs.Select(x => x.MaterialId)).Distinct().ToList();
        var priceHistory = await db.StockEntries.Where(x => ids.Contains(x.MaterialId) && x.EntryDate < end).ToListAsync();
        var result = new MovementDashboard();
        foreach (var id in ids)
        {
            var e = entries.Where(x => x.MaterialId == id).ToList(); var o = outs.Where(x => x.MaterialId == id).ToList();
            var mat = e.FirstOrDefault()?.Material ?? o.FirstOrDefault()?.Material;
            var row = new MovementDashboardRow { MaterialId = id, MaterialCode = mat?.Code ?? "", MaterialName = mat?.Name ?? "", Unit = mat?.StockUnitName ?? "", IncomingQuantity = e.Sum(x => x.Quantity), IncomingValue = e.Sum(x => x.Quantity * x.PricePerStockUnit), OutgoingQuantity = o.Sum(x => x.Quantity) };
            foreach (var w in o)
            {
                var price = txPrices.Where(x => x.MaterialId == id && x.WarehouseId == w.WarehouseId).OrderBy(x => Math.Abs((x.CreatedAt - w.CreatedAt).Ticks)).Select(x => x.UnitPrice).FirstOrDefault();
                if (price <= 0) { price = priceHistory.Where(x => x.MaterialId == id && x.EntryDate <= w.WithdrawalDate).OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.Id).FirstOrDefault()?.PricePerStockUnit ?? 0; row.HasEstimatedOutgoingValue = true; }
                row.OutgoingValue += w.Quantity * price;
            }
            result.Rows.Add(row);
        }
        return result;
    }

    // ── Recipe cost ──
    /// <summary>
    /// Cost of a recipe from the last purchase price of each ingredient. Each line is converted to the ingredient's stock unit
    /// (e.g. 140 گرم تخم‌مرغ → 0.0111 کارتن) and priced at the last price of one stock unit.
    /// </summary>
    public async Task<RecipeCostResult> CalculateRecipeCostAsync(Recipe recipe, decimal profitPercent = 0)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var ids = recipe.Ingredients.Select(i => i.MaterialId).Distinct().ToList();
        var materials = await db.Materials.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var rows = (await db.MaterialUnitConversions.AsNoTracking().Where(x => ids.Contains(x.MaterialId)).ToListAsync()).ToLookup(x => x.MaterialId);
        var result = new RecipeCostResult { ProfitPercent = profitPercent };
        decimal totalMainWeight = 0;
        foreach (var ing in recipe.Ingredients)
        {
            if (!materials.TryGetValue(ing.MaterialId, out var material)) continue;
            var units = UnitSet.For(material, rows[material.Id]);
            var unit = units.Has(ing.UnitName) ? ing.UnitName : units.RecipeUnit;
            var lastEntry = await db.StockEntries.Where(e => e.MaterialId == ing.MaterialId).OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).FirstOrDefaultAsync();
            var pricePerStock = lastEntry?.PricePerStockUnit ?? material.PricePerUnit;
            var info = units.Info(unit);
            var line = new RecipeCostLine
            {
                MaterialId = material.Id, MaterialCode = material.Code, MaterialName = material.Name, IsTopping = ing.IsTopping, Quantity = ing.Quantity, InputUnitName = unit,
                StockUnitName = units.StockUnit, PricePerStockUnit = pricePerStock, LastPurchaseDate = lastEntry?.EntryDate,
                UnitKnown = info != null, IsApproximate = info?.IsApproximate ?? false,
            };
            if (info != null)
            {
                line.StockQuantity = ing.Quantity / info.PerStockUnit;
                line.PricePerInputUnit = units.PriceOf(unit, pricePerStock);
                line.LineCost = line.StockQuantity * pricePerStock;
                line.WeightKg = units.ToKilograms(ing.Quantity, unit);
            }
            if (!ing.IsTopping) { if (line.WeightKg.HasValue) totalMainWeight += line.WeightKg.Value; else result.IngredientsWithoutWeight.Add(material.Name); }
            if (ing.IsTopping) result.ToppingCost += line.LineCost; else result.MainIngredientCost += line.LineCost;
            result.Lines.Add(line);
        }
        result.TotalRawCost = result.MainIngredientCost + result.ToppingCost;
        result.TotalAfterLoss = recipe.BakingLossPercent > 0 && recipe.BakingLossPercent < 100 ? result.TotalRawCost / (1 - recipe.BakingLossPercent / 100) : result.TotalRawCost;
        result.TotalMainWeightKg = totalMainWeight;
        result.CostPerKg = totalMainWeight > 0 ? result.TotalAfterLoss / totalMainWeight : 0;
        result.CostPerPiece = recipe.PieceWeightGrams > 0 ? result.CostPerKg * (recipe.PieceWeightGrams / 1000m) : 0;
        result.SellingPricePerKg = result.CostPerKg * (1 + profitPercent / 100);
        result.SellingPricePerPiece = result.CostPerPiece * (1 + profitPercent / 100);
        return result;
    }
}

public class RecipeCostLine
{
    public int MaterialId { get; set; }
    public string MaterialCode { get; set; } = "";
    public string MaterialName { get; set; } = "";
    public bool IsTopping { get; set; }
    public decimal Quantity { get; set; }
    public string InputUnitName { get; set; } = "";
    public decimal StockQuantity { get; set; }
    public string StockUnitName { get; set; } = "";
    public decimal PricePerStockUnit { get; set; }
    public decimal PricePerInputUnit { get; set; }
    public DateTime? LastPurchaseDate { get; set; }
    public decimal LineCost { get; set; }
    public decimal? WeightKg { get; set; }
    public bool UnitKnown { get; set; }
    public bool IsApproximate { get; set; }
}
