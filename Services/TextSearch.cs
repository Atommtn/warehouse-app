using System.Text;

namespace WarehouseApp.Services;

/// <summary>Search that ignores the usual Persian typing differences: ي/ی، ك/ک، ة/ه، آ/ا، نیم‌فاصله، فاصله و ارقام فارسی و عربی.</summary>
public static class TextSearch
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var b = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = raw switch
            {
                'ي' or 'ى' or 'ئ' => 'ی',
                'ك' => 'ک',
                'ة' or 'ۀ' => 'ه',
                'آ' or 'أ' or 'إ' or 'ٱ' => 'ا',
                'ؤ' => 'و',
                >= '۰' and <= '۹' => (char)('0' + (raw - '۰')),
                >= '٠' and <= '٩' => (char)('0' + (raw - '٠')),
                _ => char.ToLowerInvariant(raw),
            };
            // Spaces, ZWNJ, tatweel and diacritics are dropped so "تخم مرغ"، "تخم‌مرغ" and "تخممرغ" match.
            if (char.IsWhiteSpace(c) || c is '‌' or '‍' or 'ـ' || (c >= 'ً' && c <= 'ٟ') || c == 'ٰ') continue;
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>True when every word of <paramref name="query"/> appears in one of the fields.</summary>
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var target = string.Concat(fields.Select(f => Normalize(f) + "|"));
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0).All(target.Contains);
    }
}
