using WarehouseApp.Models;

namespace WarehouseApp.Services;

/// <summary>Shows a base-unit quantity in the unit each material is set to display (e.g. کارتن instead of کیلوگرم).</summary>
public class StockDisplay
{
    private readonly Dictionary<int, (string Unit, decimal Factor)> _units;
    public StockDisplay(Dictionary<int, (string Unit, decimal Factor)> units) => _units = units;
    public static readonly StockDisplay Empty = new(new());

    public bool HasDisplayUnit(int materialId) => _units.ContainsKey(materialId);
    public string? UnitOf(int materialId) => _units.TryGetValue(materialId, out var u) ? u.Unit : null;

    public string Format(decimal baseQuantity, Material? material, string format = "N3")
    {
        var baseUnit = material?.BaseUnitName ?? "";
        if (material == null || !_units.TryGetValue(material.Id, out var d) || d.Factor <= 0)
            return $"{baseQuantity.ToString(format)} {baseUnit}";
        return $"{(baseQuantity / d.Factor).ToString(format)} {d.Unit} ({baseQuantity.ToString(format)} {baseUnit})";
    }
}
