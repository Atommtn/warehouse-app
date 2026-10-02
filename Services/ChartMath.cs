using System.Globalization;

namespace WarehouseApp.Services;

public record ChartSeries(string Name, string Color, IReadOnlyList<decimal> Values);

public static class ChartMath
{
    /// <summary>A round axis maximum and its ticks (0, 25M, 50M, ...).</summary>
    public static (decimal Max, List<decimal> Ticks) Ticks(decimal max, int count = 4)
    {
        if (max <= 0) max = 1;
        var raw = (double)max / count; var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * mag).First(s => s >= raw);
        var ticks = Enumerable.Range(0, count + 1).Select(i => (decimal)(step * i)).ToList();
        while (ticks[^1] < max) ticks.Add(ticks[^1] + (decimal)step);
        return (ticks[^1], ticks);
    }

    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Short Persian amount: ۵٫۳ میلیون، ۲۸٫۷ میلیارد.</summary>
    public static string Compact(decimal v)
    {
        var a = Math.Abs(v);
        string N(decimal x) => x.ToString(x >= 100 ? "#,0" : "#,0.#", CultureInfo.InvariantCulture);
        return a >= 1_000_000_000_000m ? $"{N(v / 1_000_000_000_000m)} هزار میلیارد"
             : a >= 1_000_000_000m ? $"{N(v / 1_000_000_000m)} میلیارد"
             : a >= 1_000_000m ? $"{N(v / 1_000_000m)} میلیون"
             : a >= 1_000m ? $"{N(v / 1_000m)} هزار"
             : N(v);
    }

    // Column with a 4px rounded top and a square base.
    public static string Column(double x, double y, double w, double h)
    {
        var r = Math.Min(4, Math.Min(w / 2, h));
        return $"M{F(x)},{F(y + h)}V{F(y + r)}Q{F(x)},{F(y)} {F(x + r)},{F(y)}H{F(x + w - r)}Q{F(x + w)},{F(y)} {F(x + w)},{F(y + r)}V{F(y + h)}Z";
    }
}
