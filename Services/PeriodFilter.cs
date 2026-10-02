namespace WarehouseApp.Services;

public static class PeriodFilter
{
    public static readonly (string Key, string Label)[] Options =
    [
        ("week", "هفتگی (۷ روز اخیر)"), ("month", "ماهانه (۱ ماه اخیر)"), ("quarter", "فصلی (۳ ماه اخیر)"), ("year", "سالانه (۱ سال اخیر)"), ("all", "کلی (همه‌ی سوابق)")
    ];

    public static DateTime? Start(string key) => key switch
    {
        "week" => DateTime.Today.AddDays(-7),
        "month" => DateTime.Today.AddMonths(-1),
        "quarter" => DateTime.Today.AddMonths(-3),
        "year" => DateTime.Today.AddYears(-1),
        _ => null
    };
}
