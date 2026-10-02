using Microsoft.EntityFrameworkCore;
using WarehouseApp.Models;
using WarehouseApp.Services;

namespace WarehouseApp.Data;

/// <summary>
/// One-time move to the stock-unit model: every quantity is stored in the material's stock unit (گونی، کارتن، ...)
/// and every other unit says how many of it make one stock unit.
/// Handles both earlier states of the database:
/// <list type="bullet">
/// <item>legacy (normalize-units-v1 skipped): quantities and prices are already per stock unit (Materials.UnitId), BaseQuantity = recipe units per stock unit;</item>
/// <item>normalized: quantities are in the base/recipe unit and MaterialUnitConversions.FactorToBaseUnit = base units per unit.</item>
/// </list>
/// </summary>
public static class StockUnitMigration
{
    private const string Key = "stock-units-v1";

    public static void Run(AppDbContext db)
    {
        if (Applied(db, Key)) return;
        var legacy = Applied(db, "normalize-units-v1-skipped");
        using var tx = db.Database.BeginTransaction();
        var materials = db.Materials.Include(m => m.Unit).ToList();
        var conversions = db.MaterialUnitConversions.ToList();
        var byMaterial = conversions.ToLookup(c => c.MaterialId);
        var entries = db.StockEntries.ToList().ToLookup(x => x.MaterialId);
        var withdrawals = db.StockWithdrawals.ToList().ToLookup(x => x.MaterialId);
        var stocks = db.WarehouseStocks.ToList().ToLookup(x => x.MaterialId);
        var transactions = db.InventoryTransactions.ToList().ToLookup(x => x.MaterialId);
        var ingredients = db.RecipeIngredients.ToList().ToLookup(x => x.MaterialId);
        db.MaterialUnitConversions.RemoveRange(conversions);
        db.SaveChanges();

        foreach (var m in materials)
        {
            var rows = legacy
                ? Legacy(m, entries[m.Id], withdrawals[m.Id])
                : Normalized(m, byMaterial[m.Id].Where(c => c.IsActive && c.FactorToBaseUnit > 0).ToList(), entries[m.Id], withdrawals[m.Id], stocks[m.Id], transactions[m.Id]);
            foreach (var r in rows) { r.Id = 0; r.MaterialId = m.Id; r.IsActive = true; r.IsLegacyStockUnit = false; db.MaterialUnitConversions.Add(r); }
            var units = new UnitSet(m.StockUnitName, m.RecipeUnitName, rows);
            foreach (var i in ingredients[m.Id]) if (!units.Has(i.UnitName)) i.UnitName = units.Has(m.RecipeUnitName) ? m.RecipeUnitName : m.StockUnitName;
            var latest = entries[m.Id].OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id).FirstOrDefault();
            if (latest != null) m.PricePerUnit = Math.Round(latest.PricePerStockUnit, 2);
            m.UnitsNormalized = true;
        }
        db.SaveChanges();
        db.Database.ExecuteSqlRaw("INSERT INTO [AppDataMigrations]([MigrationKey],[AppliedAt]) VALUES({0},SYSUTCDATETIME())", Key);
        tx.Commit();
        Console.WriteLine($"[upgrade] {Key}: {materials.Count} materials moved to stock units ({(legacy ? "legacy" : "normalized")} data).");
    }

    private static bool Applied(AppDbContext db, string key) =>
        db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [AppDataMigrations] WHERE [MigrationKey]={0}", key).AsEnumerable().First() > 0;

    // Quantities are already counted in the stock unit (Materials.UnitId); only the units and entry details are filled in.
    private static List<MaterialUnitConversion> Legacy(Material m, IEnumerable<StockEntry> entries, IEnumerable<StockWithdrawal> withdrawals)
    {
        var stock = string.IsNullOrWhiteSpace(m.Unit?.Name) ? "واحد" : m.Unit!.Name.Trim();
        var recipe = string.IsNullOrWhiteSpace(m.RecipeUnitName) ? stock : m.RecipeUnitName.Trim();
        var rows = new List<MaterialUnitConversion> { new() { UnitName = stock, DefinedRefUnit = stock, DefinedAmount = 1 } };
        if (!UnitSet.Same(stock, recipe))
            rows.Add(new() { UnitName = recipe, DefinedCount = m.BaseQuantity > 0 ? m.BaseQuantity : 1, DefinedAmount = 1, DefinedRefUnit = stock });
        UnitSet.Resolve(stock, rows);
        m.StockUnitName = stock; m.RecipeUnitName = recipe;
        foreach (var e in entries)
        {
            if (e.EnteredQuantity <= 0) e.EnteredQuantity = e.Quantity;
            if (string.IsNullOrWhiteSpace(e.EnteredUnitName)) e.EnteredUnitName = stock;
            e.ConversionFactor = e.EnteredQuantity > 0 ? Math.Round(e.Quantity / e.EnteredQuantity, UnitSet.QuantityScale) : 1;
            if (e.TotalPrice <= 0) e.TotalPrice = Math.Round(e.Quantity * e.PricePerUnit, 2);
        }
        foreach (var w in withdrawals)
        {
            if (w.EnteredQuantity <= 0) w.EnteredQuantity = w.Quantity;
            if (string.IsNullOrWhiteSpace(w.EnteredUnitName)) w.EnteredUnitName = stock;
            w.ConversionFactor = w.EnteredQuantity > 0 ? Math.Round(w.Quantity / w.EnteredQuantity, UnitSet.QuantityScale) : 1;
        }
        return rows;
    }

    // Quantities are in the base unit; divide by the stock unit's factor (S base units per stock unit).
    private static List<MaterialUnitConversion> Normalized(Material m, List<MaterialUnitConversion> old, IEnumerable<StockEntry> entries, IEnumerable<StockWithdrawal> withdrawals, IEnumerable<WarehouseStock> stocks, IEnumerable<InventoryTransaction> transactions)
    {
        var recipe = string.IsNullOrWhiteSpace(m.RecipeUnitName) ? "کیلوگرم" : m.RecipeUnitName.Trim();
        if (old.Count == 0)
        {
            // Material without conversions: the old code treated it as its Units row with BaseQuantity base units.
            old.Add(new MaterialUnitConversion { UnitName = string.IsNullOrWhiteSpace(m.Unit?.Name) ? "واحد" : m.Unit!.Name, FactorToBaseUnit = m.BaseQuantity > 0 ? m.BaseQuantity : 1, IsLegacyStockUnit = true });
            if (!old.Any(x => UnitSet.Same(x.UnitName, recipe))) old.Add(new MaterialUnitConversion { UnitName = recipe, FactorToBaseUnit = 1 });
        }
        var stockRow = old.FirstOrDefault(x => !string.IsNullOrWhiteSpace(m.DisplayUnitName) && UnitSet.Same(x.UnitName, m.DisplayUnitName))
                    ?? old.FirstOrDefault(x => x.IsLegacyStockUnit)
                    ?? old.FirstOrDefault(x => UnitSet.Same(x.UnitName, m.Unit?.Name))
                    ?? old.OrderByDescending(x => x.FactorToBaseUnit).First();
        var stock = stockRow.UnitName.Trim();
        var s = stockRow.FactorToBaseUnit;
        var legacyName = old.FirstOrDefault(x => x.IsLegacyStockUnit)?.UnitName ?? stock;
        var factors = old.ToDictionary(x => x.UnitName.Trim(), x => x.FactorToBaseUnit, StringComparer.OrdinalIgnoreCase);

        var rows = old.Select(x =>
        {
            var name = x.UnitName.Trim(); var per = Math.Round(s / x.FactorToBaseUnit, UnitSet.QuantityScale);
            var keepDefinition = !string.IsNullOrWhiteSpace(x.DefinedRefUnit) && x.DefinedAmount > 0 && !UnitSet.Same(x.DefinedRefUnit, name);
            return new MaterialUnitConversion
            {
                UnitName = name, PerStockUnit = UnitSet.Same(name, stock) ? 1 : per, FactorToBaseUnit = x.FactorToBaseUnit,
                DefinedCount = UnitSet.Same(name, stock) ? 1 : keepDefinition ? x.DefinedCount : per,
                DefinedAmount = UnitSet.Same(name, stock) ? 1 : keepDefinition ? x.DefinedAmount : 1,
                DefinedRefUnit = UnitSet.Same(name, stock) ? stock : keepDefinition ? x.DefinedRefUnit.Trim() : stock,
            };
        }).ToList();
        m.StockUnitName = stock; m.RecipeUnitName = recipe;

        decimal Q(decimal q) => Math.Round(q / s, UnitSet.QuantityScale);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.EnteredUnitName)) e.EnteredUnitName = legacyName;
            var enteredFactor = factors.TryGetValue(e.EnteredUnitName.Trim(), out var f) ? f : (e.ConversionFactor > 0 ? e.ConversionFactor : 1);
            if (e.EnteredQuantity <= 0) e.EnteredQuantity = Math.Round(e.Quantity / enteredFactor, 3);
            // PricePerUnit was the price of one entered unit.
            if (e.TotalPrice <= 0) e.TotalPrice = Math.Round(e.EnteredQuantity * e.PricePerUnit, 2);
            e.Quantity = Q(e.Quantity);
            e.PricePerUnit = e.Quantity > 0 ? Math.Round(e.TotalPrice / e.Quantity, 2) : 0;
            e.ConversionFactor = e.EnteredQuantity > 0 ? Math.Round(e.Quantity / e.EnteredQuantity, UnitSet.QuantityScale) : 1;
        }
        foreach (var w in withdrawals)
        {
            if (string.IsNullOrWhiteSpace(w.EnteredUnitName)) w.EnteredUnitName = legacyName;
            var enteredFactor = factors.TryGetValue(w.EnteredUnitName.Trim(), out var f) ? f : (w.ConversionFactor > 0 ? w.ConversionFactor : 1);
            if (w.EnteredQuantity <= 0) w.EnteredQuantity = Math.Round(w.Quantity / enteredFactor, 3);
            w.Quantity = Q(w.Quantity);
            w.ConversionFactor = w.EnteredQuantity > 0 ? Math.Round(w.Quantity / w.EnteredQuantity, UnitSet.QuantityScale) : 1;
        }
        foreach (var x in stocks) { x.Quantity = Q(x.Quantity); x.MinStockLevel = Q(x.MinStockLevel); }
        foreach (var t in transactions) { t.IncomingQuantity = Q(t.IncomingQuantity); t.OutgoingQuantity = Q(t.OutgoingQuantity); t.BalanceAfter = Q(t.BalanceAfter); t.UnitPrice = Math.Round(t.UnitPrice * s, 2); }
        m.CurrentStock = Q(m.CurrentStock); m.MinStockLevel = Q(m.MinStockLevel);
        return rows;
    }
}
