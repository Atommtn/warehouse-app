using WarehouseApp.Models;

namespace WarehouseApp.Services;

/// <summary>Unit sets of all materials, for pages that list many materials at once.</summary>
public class StockDisplay
{
    private readonly Dictionary<int, UnitSet> _sets;
    public StockDisplay(Dictionary<int, UnitSet> sets) => _sets = sets;
    public static readonly StockDisplay Empty = new(new());

    public UnitSet For(Material m) => _sets.TryGetValue(m.Id, out var s) ? s : new UnitSet(m.StockUnitName, m.RecipeUnitName, Array.Empty<MaterialUnitConversion>());
    public UnitSet? For(int materialId) => _sets.GetValueOrDefault(materialId);
    public string UnitOf(Material? m) => m == null ? "" : For(m).StockUnit;

    /// <summary>A stock quantity with its unit, e.g. "2 کارتن و 3 شانه".</summary>
    public string Format(decimal stockQuantity, Material? material) => material == null ? UnitSet.N(stockQuantity) : For(material).Format(stockQuantity);
}
