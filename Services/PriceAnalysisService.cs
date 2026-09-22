using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

public class PriceAnalysis
{
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    public string? Unit { get; set; }
    public decimal CurrentStock { get; set; }

    // قیمت‌ها
    public decimal WeightedAvgPrice { get; set; }   // میانگین موزون
    public decimal LastPrice { get; set; }           // آخرین قیمت خرید
    public decimal FirstPrice { get; set; }          // اولین قیمت خرید
    public decimal MinPrice { get; set; }            // حداقل قیمت
    public decimal MaxPrice { get; set; }            // حداکثر قیمت

    // تغییر قیمت
    public decimal PriceChange { get; set; }         // مقدار تغییر (آخرین - قبلی)
    public decimal PriceChangePercent { get; set; }  // درصد تغییر
    public PriceTrend Trend { get; set; }

    // ارزش موجودی
    public decimal StockValue { get; set; }          // موجودی × میانگین موزون

    // تاریخچه خریدها
    public List<PriceHistory> History { get; set; } = new();
}

public class PriceHistory
{
    public int EntryId { get; set; }
    public DateTime EntryDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal CumulativeAvg { get; set; }
}

public enum PriceTrend { Up, Down, Stable, NoData }

public class PriceAnalysisService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public PriceAnalysisService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<PriceAnalysis>> GetAllAnalysisAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var materials = await db.Materials.Where(m => m.IsActive).ToListAsync();
        var result = new List<PriceAnalysis>();

        foreach (var m in materials)
        {
            var analysis = await CalculateAsync(db, m);
            result.Add(analysis);
        }

        return result.OrderByDescending(a => a.PriceChangePercent).ToList();
    }

    public async Task<PriceAnalysis> GetAnalysisAsync(int materialId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var m = await db.Materials.FindAsync(materialId);
        if (m == null) return new PriceAnalysis();
        return await CalculateAsync(db, m);
    }

    private async Task<PriceAnalysis> CalculateAsync(AppDbContext db, Material m)
    {
        var entries = await db.StockEntries
            .Where(e => e.MaterialId == m.Id)
            .OrderBy(e => e.EntryDate)
            .ToListAsync();

        var analysis = new PriceAnalysis
        {
            MaterialId = m.Id,
            MaterialName = m.Name,
            MaterialCode = m.Code,
            Unit = m.BaseUnitName,
            CurrentStock = m.CurrentStock
        };

        if (!entries.Any())
        {
            analysis.Trend = PriceTrend.NoData;
            return analysis;
        }

        // میانگین موزون: Σ(quantity × price) / Σ(quantity)
        decimal totalQty = entries.Sum(e => e.Quantity);
        decimal totalValue = entries.Sum(e => (e.EnteredQuantity>0?e.EnteredQuantity:e.Quantity) * e.PricePerUnit);
        analysis.WeightedAvgPrice = totalQty > 0 ? totalValue / totalQty : 0;

        decimal BasePrice(StockEntry e)=>e.PricePerUnit/(e.ConversionFactor<=0?1:e.ConversionFactor);
        analysis.LastPrice  = BasePrice(entries.Last());
        analysis.FirstPrice = BasePrice(entries.First());
        analysis.MinPrice   = entries.Min(BasePrice);
        analysis.MaxPrice   = entries.Max(BasePrice);
        analysis.StockValue = m.CurrentStock * analysis.WeightedAvgPrice;

        // تغییر قیمت نسبت به خرید قبلی
        if (entries.Count >= 2)
        {
            var prev = BasePrice(entries[entries.Count - 2]);
            var last = BasePrice(entries.Last());
            analysis.PriceChange = last - prev;
            analysis.PriceChangePercent = prev > 0 ? Math.Round((last - prev) / prev * 100, 1) : 0;
            analysis.Trend = analysis.PriceChange > 0 ? PriceTrend.Up
                           : analysis.PriceChange < 0 ? PriceTrend.Down
                           : PriceTrend.Stable;
        }
        else
        {
            analysis.Trend = PriceTrend.Stable;
        }

        // تاریخچه با میانگین تجمعی
        decimal cumQty = 0, cumValue = 0;
        foreach (var e in entries)
        {
            cumQty   += e.Quantity;
            cumValue += (e.EnteredQuantity>0?e.EnteredQuantity:e.Quantity) * e.PricePerUnit;
            analysis.History.Add(new PriceHistory
            {
                EntryId       = e.Id,
                EntryDate     = e.EntryDate,
                Quantity      = e.Quantity,
                Price         = BasePrice(e),
                CumulativeAvg = cumQty > 0 ? Math.Round(cumValue / cumQty, 0) : 0
            });
        }

        return analysis;
    }

    public async Task<decimal> GetTotalWarehouseValueAsync()
    {
        var all = await GetAllAnalysisAsync();
        return all.Sum(a => a.StockValue);
    }
}
