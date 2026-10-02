using System.Globalization;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

/// <summary>
/// The units of one material. Stock is always counted in <see cref="StockUnit"/>; every other unit is
/// "how many of it make one stock unit" (کارتن 1، شانه 6، عدد 180، کیلوگرم ≈ 12.6).
/// </summary>
public sealed class UnitSet
{
    public const int QuantityScale = 10;

    // Units every material understands without a definition, as a factor of the family's smallest unit.
    public static readonly IReadOnlyDictionary<string, (string Family, decimal Factor)> Standard =
        new Dictionary<string, (string, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["تن"] = ("mass", 1_000_000m), ["کیلوگرم"] = ("mass", 1000m), ["کیلو"] = ("mass", 1000m), ["گرم"] = ("mass", 1m), ["میلی‌گرم"] = ("mass", 0.001m),
            ["لیتر"] = ("volume", 1000m), ["میلی‌لیتر"] = ("volume", 1m), ["سی‌سی"] = ("volume", 1m),
        };

    public static decimal? StandardFactor(string from, string to) =>
        Standard.TryGetValue(from.Trim(), out var f) && Standard.TryGetValue(to.Trim(), out var t) && f.Family == t.Family ? f.Factor / t.Factor : null;

    public sealed record UnitInfo(string Name, decimal PerStockUnit, bool IsApproximate, bool IsDerived);

    private readonly Dictionary<string, UnitInfo> _units = new(StringComparer.OrdinalIgnoreCase);
    public string StockUnit { get; }
    public string RecipeUnit { get; }

    public UnitSet(string stockUnit, string recipeUnit, IEnumerable<MaterialUnitConversion> rows)
    {
        StockUnit = string.IsNullOrWhiteSpace(stockUnit) ? "واحد" : stockUnit.Trim();
        RecipeUnit = string.IsNullOrWhiteSpace(recipeUnit) ? StockUnit : recipeUnit.Trim();
        _units[StockUnit] = new UnitInfo(StockUnit, 1, false, false);
        foreach (var r in rows.Where(x => x.IsActive && x.PerStockUnit > 0 && !string.IsNullOrWhiteSpace(x.UnitName)))
            if (!_units.ContainsKey(r.UnitName.Trim()))
                _units[r.UnitName.Trim()] = new UnitInfo(r.UnitName.Trim(), r.PerStockUnit, r.IsApproximate, false);
        // گرم/کیلوگرم and لیتر/میلی‌لیتر follow from each other once one of them is defined.
        foreach (var (name, _) in Standard)
        {
            if (_units.ContainsKey(name)) continue;
            var known = _units.Values.Where(u => !u.IsDerived).Select(u => (u, f: StandardFactor(u.Name, name))).FirstOrDefault(x => x.f.HasValue);
            if (known.u != null) _units[name] = new UnitInfo(name, known.u.PerStockUnit * known.f!.Value, known.u.IsApproximate, true);
        }
    }

    public static UnitSet For(Material m, IEnumerable<MaterialUnitConversion> rows) => new(m.StockUnitName, m.RecipeUnitName, rows.Where(r => r.MaterialId == m.Id));

    public bool Has(string? unit) => !string.IsNullOrWhiteSpace(unit) && _units.ContainsKey(unit.Trim());
    public UnitInfo? Info(string? unit) => string.IsNullOrWhiteSpace(unit) ? null : _units.GetValueOrDefault(unit.Trim());
    public decimal PerStockUnit(string unit) => Info(unit)?.PerStockUnit ?? throw new Exception($"واحد «{unit}» برای این کالا تعریف نشده است");

    /// <summary>Defined and standard units, the stock unit first and then from large to small.</summary>
    public IEnumerable<UnitInfo> Units => _units.Values.OrderBy(u => u.PerStockUnit).ThenBy(u => u.Name);
    /// <summary>Units worth offering in a picker: the derived standard units only when nothing else of that family is there.</summary>
    public IEnumerable<string> Choices => Units.Where(u => !u.IsDerived || u.Name is "گرم" or "کیلوگرم" or "لیتر" or "میلی‌لیتر").Select(u => u.Name);

    public decimal ToStock(decimal quantity, string unit) => Math.Round(quantity / PerStockUnit(unit), QuantityScale);
    public decimal FromStock(decimal stockQuantity, string unit) => stockQuantity * PerStockUnit(unit);
    /// <summary>Price of one <paramref name="unit"/> when one stock unit costs <paramref name="pricePerStockUnit"/>.</summary>
    public decimal PriceOf(string unit, decimal pricePerStockUnit) => pricePerStockUnit / PerStockUnit(unit);

    /// <summary>Weight in kilograms (or volume in litres) of a quantity, if the material has a mass/volume unit.</summary>
    public decimal? ToKilograms(decimal quantity, string unit)
    {
        var u = Info(unit); if (u == null) return null;
        var kg = Info("کیلوگرم") ?? Info("لیتر");
        return kg == null ? null : Math.Round(quantity / u.PerStockUnit * kg.PerStockUnit, 6);
    }

    public static string N(decimal v, int decimals = 3) => Math.Round(v, decimals).ToString("#,0.###", CultureInfo.InvariantCulture);

    /// <summary>e.g. "2.5 کارتن" or, when exact smaller units exist, "2 کارتن و 3 شانه".</summary>
    public string Format(decimal stockQuantity, bool withBreakdown = true)
    {
        var main = $"{N(stockQuantity)} {StockUnit}";
        if (!withBreakdown || stockQuantity <= 0 || stockQuantity == Math.Floor(stockQuantity)) return main;
        var breakdown = Breakdown(stockQuantity);
        return breakdown == null ? main : $"{main} ({breakdown})";
    }

    // Counted units only (no estimates), each a whole multiple of the previous one: کارتن → شانه → عدد.
    private string? Breakdown(decimal stockQuantity)
    {
        var chain = new List<UnitInfo>();
        foreach (var u in Units.Where(u => !u.IsApproximate && !u.IsDerived))
        {
            var prev = chain.LastOrDefault();
            if (prev == null || (u.PerStockUnit > prev.PerStockUnit && u.PerStockUnit % prev.PerStockUnit == 0)) chain.Add(u);
        }
        if (chain.Count < 2) return null;
        var parts = new List<string>(); var rest = stockQuantity;
        for (var i = 0; i < chain.Count; i++)
        {
            var u = chain[i]; var amount = rest * u.PerStockUnit; var last = i == chain.Count - 1;
            var whole = last ? Math.Round(amount, 2) : Math.Floor(amount + 0.000001m);
            if (whole > 0) parts.Add($"{N(whole, 2)} {u.Name}");
            rest -= whole / u.PerStockUnit;
            if (rest <= 0.0000001m) break;
        }
        return parts.Count > 0 ? string.Join(" و ", parts) : null;
    }

    /// <summary>
    /// Turns definitions like "1 کارتن = 6 شانه", "1 شانه = 30 عدد", "1 عدد = 70 گرم" into how many of each unit make one stock unit.
    /// Throws when a unit cannot be linked to the stock unit.
    /// </summary>
    public static void Resolve(string stockUnit, List<MaterialUnitConversion> rows)
    {
        stockUnit = stockUnit.Trim();
        var byName = rows.Where(r => !string.IsNullOrWhiteSpace(r.UnitName)).ToDictionary(r => r.UnitName.Trim(), StringComparer.OrdinalIgnoreCase);
        // Undirected graph: "Count A = Amount B" means 1 A = Amount/Count B.
        var edges = new List<(string A, string B, decimal BperA, bool Approx)>();
        foreach (var r in byName.Values)
        {
            if (Same(r.UnitName, stockUnit) || string.IsNullOrWhiteSpace(r.DefinedRefUnit)) continue;
            if (r.DefinedCount <= 0 || r.DefinedAmount <= 0) throw new Exception($"مقدار رابطه‌ی واحد «{r.UnitName}» باید بیشتر از صفر باشد");
            edges.Add((r.UnitName.Trim(), r.DefinedRefUnit.Trim(), r.DefinedAmount / r.DefinedCount, r.IsApproximate));
        }
        var names = byName.Keys.Concat(edges.Select(e => e.B)).Append(stockUnit).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var a in names) foreach (var b in names)
            if (!Same(a, b) && StandardFactor(a, b) is decimal f) edges.Add((a, b, f, false));
        var per = new Dictionary<string, (decimal Per, bool Approx)>(StringComparer.OrdinalIgnoreCase) { [stockUnit] = (1, false) };
        var queue = new Queue<string>(); queue.Enqueue(stockUnit);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue(); var (curPer, curApprox) = per[cur];
            foreach (var e in edges)
            {
                // 1 A = k B  →  per(B) = per(A) * k ; per(A) = per(B) / k
                if (Same(e.A, cur) && !per.ContainsKey(e.B)) { per[e.B] = (curPer * e.BperA, curApprox || e.Approx); queue.Enqueue(e.B); }
                else if (Same(e.B, cur) && !per.ContainsKey(e.A)) { per[e.A] = (curPer / e.BperA, curApprox || e.Approx); queue.Enqueue(e.A); }
            }
        }
        foreach (var r in byName.Values)
        {
            if (!per.TryGetValue(r.UnitName.Trim(), out var p)) throw new Exception($"رابطه‌ی واحد «{r.UnitName}» با {stockUnit} مشخص نیست؛ آن را بر حسب یک واحد دیگر بنویسید");
            r.UnitName = r.UnitName.Trim();
            r.PerStockUnit = Math.Round(p.Per, QuantityScale);
            if (r.PerStockUnit <= 0) throw new Exception($"واحد «{r.UnitName}» نسبت به {stockUnit} بیش از حد کوچک است");
            r.IsApproximate = p.Approx;
            r.FactorToBaseUnit = Math.Round(1 / p.Per, 6);
            if (Same(r.UnitName, stockUnit)) { r.DefinedRefUnit = stockUnit; r.DefinedCount = 1; r.DefinedAmount = 1; r.IsApproximate = false; }
        }
    }

    public static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
