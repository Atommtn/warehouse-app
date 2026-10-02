using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

public enum Granularity { Day, Week, Month, Year }

/// <summary>One period of a chart, in the Persian calendar. End is exclusive.</summary>
public record Bucket(DateTime Start, DateTime End, string Label, string LongLabel);

public class FlowPoint
{
    public Bucket Bucket { get; init; } = default!;
    public decimal InValue { get; set; }
    public decimal OutValue { get; set; }
    // Stock-unit quantities; only meaningful when one material is selected.
    public decimal InQuantity { get; set; }
    public decimal OutQuantity { get; set; }
}

public class ValuePoint
{
    public Bucket Bucket { get; init; } = default!;
    // Stock value at the end of the period.
    public decimal Value { get; set; }
}

public class ConsumptionRow
{
    public Material Material { get; init; } = default!;
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
    public decimal PerDay { get; set; }
    public decimal CurrentStock { get; set; }
    // Days the current stock lasts at the average rate; null when nothing was used.
    public decimal? DaysLeft { get; set; }
}

public class GroupValue
{
    public string Group { get; init; } = "";
    public decimal Value { get; set; }
}

public class DashboardData
{
    public List<FlowPoint> Flow { get; set; } = new();
    public List<ValuePoint> StockValue { get; set; } = new();
    public decimal CurrentValue { get; set; }
    public List<GroupValue> ValueByGroup { get; set; } = new();
    public List<MovementDashboardRow> Movement { get; set; } = new();
}

/// <summary>Numbers behind the dashboard charts. Everything is computed in memory from entries, withdrawals and transfers.</summary>
public class DashboardAnalyticsService
{
    private static readonly PersianCalendar Pc = new();
    private readonly IDbContextFactory<AppDbContext> _factory;
    public DashboardAnalyticsService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public static string Label(Granularity g) => g switch { Granularity.Day => "روز", Granularity.Week => "هفته", Granularity.Month => "ماه", _ => "سال" };

    /// <summary>The last <paramref name="count"/> periods up to today (count 0 = from the first movement).</summary>
    public static List<Bucket> Buckets(Granularity g, int count, DateTime firstMovement)
    {
        var today = DateTime.Today;
        var current = StartOf(g, today);
        var first = count > 0 ? current : StartOf(g, firstMovement < today ? firstMovement : today);
        for (var i = 1; i < count; i++) first = Previous(g, first);
        var list = new List<Bucket>();
        for (var s = first; s <= current && list.Count < 400; s = Next(g, s)) list.Add(Make(g, s));
        return list;
    }

    private static Bucket Make(Granularity g, DateTime s)
    {
        var e = Next(g, s); int y = Pc.GetYear(s), m = Pc.GetMonth(s), d = Pc.GetDayOfMonth(s);
        return g switch
        {
            Granularity.Day => new Bucket(s, e, $"{m:00}/{d:00}", s.ToShamsiLong()),
            Granularity.Week => new Bucket(s, e, $"{m:00}/{d:00}", $"هفته‌ی {s.ToShamsi()} تا {e.AddDays(-1).ToShamsi()}"),
            Granularity.Month => new Bucket(s, e, $"{PersianDate.MonthName(m)} {y % 100:00}", $"{PersianDate.MonthName(m)} {y}"),
            _ => new Bucket(s, e, $"{y}", $"سال {y}"),
        };
    }

    private static DateTime StartOf(Granularity g, DateTime d)
    {
        d = d.Date;
        return g switch
        {
            Granularity.Day => d,
            // The Persian week starts on Saturday.
            Granularity.Week => d.AddDays(-(((int)d.DayOfWeek + 1) % 7)),
            Granularity.Month => Pc.ToDateTime(Pc.GetYear(d), Pc.GetMonth(d), 1, 0, 0, 0, 0),
            _ => Pc.ToDateTime(Pc.GetYear(d), 1, 1, 0, 0, 0, 0),
        };
    }
    private static DateTime Next(Granularity g, DateTime s) => g switch
    {
        Granularity.Day => s.AddDays(1), Granularity.Week => s.AddDays(7), Granularity.Month => Pc.AddMonths(s, 1), _ => Pc.AddYears(s, 1),
    };
    private static DateTime Previous(Granularity g, DateTime s) => g switch
    {
        Granularity.Day => s.AddDays(-1), Granularity.Week => s.AddDays(-7), Granularity.Month => Pc.AddMonths(s, -1), _ => Pc.AddYears(s, -1),
    };

    public async Task<DashboardData> GetAsync(Granularity g, int count, int? warehouseId, int? materialId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.AsNoTracking().Include(m => m.Group).ToDictionaryAsync(m => m.Id);
        var entries = await db.StockEntries.AsNoTracking().ToListAsync();
        var outs = await db.StockWithdrawals.AsNoTracking().Where(w => w.Status == WithdrawalStatus.Approved).ToListAsync();
        var transfers = await db.InventoryTransactions.AsNoTracking().Where(t => t.Type == InventoryTransactionType.TransferIn || t.Type == InventoryTransactionType.TransferOut).ToListAsync();
        var stocks = await db.WarehouseStocks.AsNoTracking().ToListAsync();
        var prices = new PriceBook(entries, materials.Values);

        bool InScope(int? wid, int mid) => (!warehouseId.HasValue || wid == warehouseId) && (!materialId.HasValue || mid == materialId);
        var first = entries.Select(e => e.EntryDate).Concat(outs.Select(w => w.WithdrawalDate)).DefaultIfEmpty(DateTime.Today).Min();
        var buckets = Buckets(g, count, first);
        var data = new DashboardData();
        if (buckets.Count == 0) return data;
        DateTime from = buckets[0].Start, to = buckets[^1].End;

        var scopedEntries = entries.Where(e => InScope(e.WarehouseId, e.MaterialId)).ToList();
        var scopedOuts = outs.Where(w => InScope(w.WarehouseId, w.MaterialId)).ToList();
        foreach (var b in buckets)
        {
            var p = new FlowPoint { Bucket = b };
            foreach (var e in scopedEntries.Where(e => e.EntryDate >= b.Start && e.EntryDate < b.End)) { p.InValue += e.Quantity * e.PricePerStockUnit; p.InQuantity += e.Quantity; }
            foreach (var w in scopedOuts.Where(w => w.WithdrawalDate >= b.Start && w.WithdrawalDate < b.End)) { p.OutValue += w.Quantity * prices.At(w.MaterialId, w.WithdrawalDate); p.OutQuantity += w.Quantity; }
            data.Flow.Add(p);
        }

        // Stock at a past moment = today's stock minus what came in since, plus what went out since.
        var scopedStocks = stocks.Where(s => InScope(s.WarehouseId, s.MaterialId)).GroupBy(s => s.MaterialId).ToDictionary(x => x.Key, x => x.Sum(s => s.Quantity));
        var scopedTransfers = transfers.Where(t => InScope(t.WarehouseId, t.MaterialId)).ToList();
        // Walk back from now: undo every movement on or after each period end.
        var qty = new Dictionary<int, decimal>(scopedStocks);
        var events = scopedEntries.Select(e => (Date: e.EntryDate, e.MaterialId, Delta: -e.Quantity))
            .Concat(scopedOuts.Select(w => (Date: w.WithdrawalDate, w.MaterialId, Delta: w.Quantity)))
            .Concat(warehouseId.HasValue ? scopedTransfers.Select(t => (Date: t.TransactionDate, t.MaterialId, Delta: t.OutgoingQuantity - t.IncomingQuantity)) : Enumerable.Empty<(DateTime Date, int MaterialId, decimal Delta)>())
            .OrderByDescending(x => x.Date).ToList();
        var next = 0; var values = new decimal[buckets.Count];
        for (var i = buckets.Count - 1; i >= 0; i--)
        {
            var at = buckets[i].End < DateTime.Now ? buckets[i].End : DateTime.Now;
            for (; next < events.Count && events[next].Date >= at; next++) qty[events[next].MaterialId] = qty.GetValueOrDefault(events[next].MaterialId) + events[next].Delta;
            values[i] = qty.Where(x => x.Value > 0).Sum(x => x.Value * prices.At(x.Key, at));
        }
        for (var i = 0; i < buckets.Count; i++) data.StockValue.Add(new ValuePoint { Bucket = buckets[i], Value = values[i] });
        data.CurrentValue = scopedStocks.Where(x => x.Value > 0).Sum(x => x.Value * prices.At(x.Key, DateTime.Now));
        data.ValueByGroup = scopedStocks.Where(x => x.Value > 0 && materials.ContainsKey(x.Key))
            .GroupBy(x => materials[x.Key].Group?.Name ?? "بدون گروه")
            .Select(gr => new GroupValue { Group = gr.Key, Value = gr.Sum(x => x.Value * prices.At(x.Key, DateTime.Now)) })
            .Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToList();

        foreach (var mid in scopedEntries.Where(e => e.EntryDate >= from && e.EntryDate < to).Select(e => e.MaterialId)
                     .Union(scopedOuts.Where(w => w.WithdrawalDate >= from && w.WithdrawalDate < to).Select(w => w.MaterialId)))
        {
            if (!materials.TryGetValue(mid, out var m)) continue;
            var ins = scopedEntries.Where(e => e.MaterialId == mid && e.EntryDate >= from && e.EntryDate < to).ToList();
            var os = scopedOuts.Where(w => w.MaterialId == mid && w.WithdrawalDate >= from && w.WithdrawalDate < to).ToList();
            data.Movement.Add(new MovementDashboardRow
            {
                MaterialId = mid, MaterialCode = m.Code, MaterialName = m.Name, Unit = m.StockUnitName,
                IncomingQuantity = ins.Sum(e => e.Quantity), IncomingValue = ins.Sum(e => e.Quantity * e.PricePerStockUnit),
                OutgoingQuantity = os.Sum(w => w.Quantity), OutgoingValue = os.Sum(w => w.Quantity * prices.At(mid, w.WithdrawalDate)),
            });
        }
        return data;
    }

    /// <summary>Average use of each material over the last <paramref name="days"/> days, most expensive first.</summary>
    public async Task<List<ConsumptionRow>> GetConsumptionAsync(int days, int? warehouseId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var since = DateTime.Today.AddDays(-days + 1);
        var materials = await db.Materials.AsNoTracking().Where(m => m.IsActive).ToDictionaryAsync(m => m.Id);
        var entries = await db.StockEntries.AsNoTracking().ToListAsync();
        var prices = new PriceBook(entries, materials.Values);
        var outs = await db.StockWithdrawals.AsNoTracking().Where(w => w.Status == WithdrawalStatus.Approved && w.WithdrawalDate >= since && (!warehouseId.HasValue || w.WarehouseId == warehouseId)).ToListAsync();
        var stocks = (await db.WarehouseStocks.AsNoTracking().Where(s => !warehouseId.HasValue || s.WarehouseId == warehouseId).ToListAsync())
            .GroupBy(s => s.MaterialId).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
        return outs.Where(w => materials.ContainsKey(w.MaterialId)).GroupBy(w => w.MaterialId).Select(g =>
        {
            var qty = g.Sum(w => w.Quantity); var perDay = qty / days; var stock = stocks.GetValueOrDefault(g.Key);
            return new ConsumptionRow
            {
                Material = materials[g.Key], Quantity = qty, Value = g.Sum(w => w.Quantity * prices.At(w.MaterialId, w.WithdrawalDate)),
                PerDay = perDay, CurrentStock = stock, DaysLeft = perDay > 0 ? Math.Floor(stock / perDay) : null,
            };
        }).OrderByDescending(r => r.Value).ThenByDescending(r => r.Quantity).ToList();
    }

    /// <summary>Price of one stock unit at a moment: the last purchase on or before it, else the first purchase, else the material price.</summary>
    private sealed class PriceBook
    {
        private readonly Dictionary<int, List<(DateTime Date, decimal Price)>> _history;
        private readonly Dictionary<int, decimal> _fallback;
        public PriceBook(IEnumerable<StockEntry> entries, IEnumerable<Material> materials)
        {
            _history = entries.Where(e => e.PricePerStockUnit > 0).GroupBy(e => e.MaterialId)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.EntryDate).ThenBy(e => e.Id).Select(e => (e.EntryDate, e.PricePerStockUnit)).ToList());
            _fallback = materials.ToDictionary(m => m.Id, m => m.PricePerUnit);
        }
        public decimal At(int materialId, DateTime when)
        {
            if (!_history.TryGetValue(materialId, out var list) || list.Count == 0) return _fallback.GetValueOrDefault(materialId);
            var price = list[0].Price;
            foreach (var (date, p) in list) { if (date > when) break; price = p; }
            return price;
        }
    }
}
