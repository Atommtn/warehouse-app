using System.Globalization;

namespace WarehouseApp.Services;

public static class PersianDate
{
    private static readonly PersianCalendar _pc = new();

    public static string ToShamsi(this DateTime date)
    {
        try
        {
            int y = _pc.GetYear(date);
            int m = _pc.GetMonth(date);
            int d = _pc.GetDayOfMonth(date);
            return $"{y}/{m:00}/{d:00}";
        }
        catch { return date.ToString("yyyy/MM/dd"); }
    }

    public static string ToShamsiWithTime(this DateTime date)
    {
        try
        {
            int y = _pc.GetYear(date);
            int m = _pc.GetMonth(date);
            int d = _pc.GetDayOfMonth(date);
            return $"{y}/{m:00}/{d:00} {date:HH:mm}";
        }
        catch { return date.ToString("yyyy/MM/dd HH:mm"); }
    }

    public static string MonthName(int month) => month switch
    {
        1 => "فروردین", 2 => "اردیبهشت", 3 => "خرداد",
        4 => "تیر",     5 => "مرداد",    6 => "شهریور",
        7 => "مهر",     8 => "آبان",     9 => "آذر",
        10 => "دی",     11 => "بهمن",    12 => "اسفند",
        _ => ""
    };

    public static string ToShamsiLong(this DateTime date)
    {
        try
        {
            int y = _pc.GetYear(date);
            int m = _pc.GetMonth(date);
            int d = _pc.GetDayOfMonth(date);
            return $"{d} {MonthName(m)} {y}";
        }
        catch { return date.ToString("yyyy/MM/dd"); }
    }

    // تبدیل از شمسی به میلادی برای ذخیره در دیتابیس
    public static DateTime FromShamsi(int year, int month, int day)
    {
        return _pc.ToDateTime(year, month, day, 0, 0, 0, 0);
    }

    public static DateTime? TryParseShamsi(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parts = input.Replace("-", "/").Split('/');
        if (parts.Length != 3) return null;
        if (int.TryParse(parts[0], out int y) &&
            int.TryParse(parts[1], out int m) &&
            int.TryParse(parts[2], out int d))
        {
            try { return FromShamsi(y, m, d); }
            catch { return null; }
        }
        return null;
    }
}
