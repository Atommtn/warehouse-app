using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using WarehouseApp.Data;
using WarehouseApp.Models;

namespace WarehouseApp.Services;

/// <summary>Asanak credentials, from configuration only (environment: Sms__Username, Sms__Password, Sms__Source).</summary>
public class SmsOptions
{
    public string Url { get; set; } = "https://panel.asanak.com/webservice/v2rest/sendsms";
    public string StatusUrl { get; set; } = "https://panel.asanak.com/webservice/v2rest/msgstatus";
    // Trimmed: a .env saved on Windows leaves "\r" (or a stray space/quote) at the end, which the panel rejects as a wrong password.
    private string _username = "", _password = "", _source = "";
    public string Username { get => _username; set => _username = Clean(value); }
    public string Password { get => _password; set => _password = Clean(value); }
    public string Source { get => _source; set => _source = Clean(value); }
    private static string Clean(string? v) => (v ?? "").Trim().Trim('"', '\'').Trim();
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(Source);
}

/// <summary>SMS settings people change from the alerts page.</summary>
public class SmsSettings
{
    public List<string> Recipients { get; set; } = new();
    public bool DailyEnabled { get; set; } = true;
    public int DailyHour { get; set; } = 8;
    public bool InstantEnabled { get; set; } = true;
    public string Prefix { get; set; } = "WH";
    public string LastDailyDate { get; set; } = "";
    // The SMS itself: a neutral text that says nothing about the stock.
    public string Text { get; set; } = DefaultText;
    // Append the coded summary (L/E/X + material codes) after the text.
    public bool IncludeCodes { get; set; }
    public const string DefaultText = "هشدار انبار\nلطفاً نرم افزار را بررسی نمایید.";

    public string Compose(string? codes) => IncludeCodes && !string.IsNullOrWhiteSpace(codes) ? $"{Text}\n{codes}" : Text;
}

/// <summary>Materials whose stock just dropped to their minimum; the background sender turns them into SMS.</summary>
public class LowStockSignal
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    public void Raise(int materialId) => _channel.Writer.TryWrite(materialId);
    public ChannelReader<int> Reader => _channel.Reader;
}

public class SmsService
{
    private readonly IHttpClientFactory _http;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly SmsOptions _options;
    private readonly ILogger<SmsService> _log;

    public SmsService(IHttpClientFactory http, IDbContextFactory<AppDbContext> factory, Microsoft.Extensions.Options.IOptions<SmsOptions> options, ILogger<SmsService> log)
    { _http = http; _factory = factory; _options = options.Value; _log = log; }

    public bool IsConfigured => _options.IsConfigured;
    public string Source => _options.Source;

    // ── Settings ──
    private const string Prefix = "sms.";
    public async Task<SmsSettings> GetSettingsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var map = await db.AppSettings.Where(x => x.Key.StartsWith(Prefix)).ToDictionaryAsync(x => x.Key, x => x.Value);
        string? V(string k) => map.TryGetValue(Prefix + k, out var v) ? v : null;
        var s = new SmsSettings();
        if (V("recipients") is { } r) s.Recipients = r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (bool.TryParse(V("daily.enabled"), out var de)) s.DailyEnabled = de;
        if (int.TryParse(V("daily.hour"), out var h)) s.DailyHour = Math.Clamp(h, 0, 23);
        if (bool.TryParse(V("instant.enabled"), out var ie)) s.InstantEnabled = ie;
        if (V("prefix") is { Length: > 0 } p) s.Prefix = p;
        s.LastDailyDate = V("daily.last") ?? "";
        if (V("text") is { Length: > 0 } t) s.Text = t;
        if (bool.TryParse(V("codes"), out var ic)) s.IncludeCodes = ic;
        return s;
    }

    public async Task SaveSettingsAsync(SmsSettings s)
    {
        var numbers = s.Recipients.Select(NormalizeNumber).ToList();
        if (numbers.Any(n => n == null)) throw new Exception("شماره‌ی موبایل نامعتبر است؛ به شکل 09121234567 بنویسید");
        await SetAsync(new()
        {
            ["recipients"] = string.Join(",", numbers), ["daily.enabled"] = s.DailyEnabled.ToString(), ["daily.hour"] = s.DailyHour.ToString(),
            ["instant.enabled"] = s.InstantEnabled.ToString(), ["prefix"] = string.IsNullOrWhiteSpace(s.Prefix) ? "WH" : s.Prefix.Trim(),
            ["text"] = string.IsNullOrWhiteSpace(s.Text) ? SmsSettings.DefaultText : s.Text.Trim(), ["codes"] = s.IncludeCodes.ToString(),
        });
    }

    public async Task SetAsync(Dictionary<string, string> values)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var (k, v) in values)
        {
            var row = await db.AppSettings.FindAsync(Prefix + k);
            if (row == null) db.AppSettings.Add(new AppSetting { Key = Prefix + k, Value = v }); else row.Value = v;
        }
        await db.SaveChangesAsync();
    }

    public async Task<List<SmsLog>> GetLogsAsync(int take = 30)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.SmsLogs.OrderByDescending(x => x.Id).Take(take).ToListAsync();
    }

    /// <summary>09121234567 / +989121234567 / ۰۹۱۲... → 989121234567.</summary>
    public static string? NormalizeNumber(string? input)
    {
        var digits = new string(TextSearch.Normalize(input).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0098")) digits = digits[2..];
        if (digits.StartsWith("09")) digits = "98" + digits[1..];
        else if (digits.StartsWith("9") && digits.Length == 10) digits = "98" + digits;
        return digits.Length == 12 && digits.StartsWith("989") ? digits : null;
    }

    /// <summary>Sends one message to each recipient and logs every attempt. Returns how many were accepted.</summary>
    public async Task<int> SendAsync(IEnumerable<string> recipients, string message, string kind)
    {
        if (!_options.IsConfigured) throw new Exception("اطلاعات پنل پیامک (SMS_USERNAME، API_CODE، SMS_SOURCE) در فایل .env سرور تنظیم نشده است");
        var ok = 0;
        foreach (var to in recipients.Select(NormalizeNumber).Where(n => n != null).Distinct())
        {
            var log = new SmsLog { Kind = kind, Recipient = to!, Message = message };
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = _options.Username, ["password"] = _options.Password, ["source"] = _options.Source, ["message"] = message, ["destination"] = to!,
                });
                var client = _http.CreateClient("sms"); client.Timeout = TimeSpan.FromSeconds(20);
                var response = await client.PostAsync(_options.Url, content);
                var body = await response.Content.ReadAsStringAsync();
                log.Response = $"{(int)response.StatusCode} {Truncate(body, 900)}";
                log.Success = response.IsSuccessStatusCode && !LooksLikeError(body);
                if (body.Contains("\"status\":1008")) log.Response = "نام کاربری یا رمز وب‌سرویس آسانک اشتباه است (SMS_USERNAME / API_CODE در .env) — " + log.Response;
                log.MessageId = FindMessageId(body) ?? "";
            }
            catch (Exception ex) { log.Response = Truncate(ex.Message, 900); _log.LogWarning(ex, "SMS to {To} failed", to); }
            if (log.Success) ok++;
            await using var db = await _factory.CreateDbContextAsync();
            db.SmsLogs.Add(log); await db.SaveChangesAsync();
        }
        return ok;
    }

    /// <summary>Asks Asanak (msgstatus) about recent messages that have a msgid; returns how many were checked.</summary>
    public async Task<int> RefreshStatusesAsync(int take = 30)
    {
        if (!_options.IsConfigured) return 0;
        await using var db = await _factory.CreateDbContextAsync();
        var logs = await db.SmsLogs.Where(x => x.MessageId != "").OrderByDescending(x => x.Id).Take(take).ToListAsync();
        foreach (var log in logs)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = _options.Username, ["password"] = _options.Password, ["msgid"] = log.MessageId });
                var client = _http.CreateClient("sms"); client.Timeout = TimeSpan.FromSeconds(20);
                var response = await client.PostAsync(_options.StatusUrl, content);
                log.DeliveryStatus = Truncate(DescribeStatus(await response.Content.ReadAsStringAsync()), 500);
            }
            catch (Exception ex) { log.DeliveryStatus = Truncate("خطا: " + ex.Message, 500); }
            log.StatusCheckedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
        return logs.Count;
    }

    // The msgid may be a number, a string or a one-item array anywhere in the JSON (e.g. data.msgid[0]).
    private static string? FindMessageId(string body)
    {
        try { using var doc = JsonDocument.Parse(body); return Find(doc.RootElement); }
        catch (JsonException) { return null; }
        static string? Find(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name.Equals("msgid", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("msg_id", StringComparison.OrdinalIgnoreCase))
                        {
                            var v = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().FirstOrDefault() : p.Value;
                            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.String) return v.ToString();
                        }
                        if (Find(p.Value) is { } inner) return inner;
                    }
                    return null;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray()) if (Find(x) is { } inner) return inner;
                    return null;
                default: return null;
            }
        }
    }

    // Shows the status text Asanak returns (a "status"/"message" field when present, else the raw answer).
    private static string DescribeStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            string? Pick(JsonElement e, params string[] names)
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        if (names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return p.Value.ToString();
                        if (Pick(p.Value, names) is { } inner) return inner;
                    }
                if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) if (Pick(x, names) is { } inner) return inner;
                return null;
            }
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            return Pick(data, "status", "state", "message") is { } s ? $"{s} — {body}" : body;
        }
        catch (JsonException) { return body; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    // Asanak answers with JSON; a non-200 meta.status (or an "error" field) means it was refused.
    private static bool LooksLikeError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() != 200) return true;
                if (root.TryGetProperty("error", out var err) && err.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) return true;
            }
            return false;
        }
        catch (JsonException) { return body.Contains("error", StringComparison.OrdinalIgnoreCase); }
    }
}

/// <summary>Builds the short, coded alert texts. Latin text keeps a message within one 160-character SMS.</summary>
public static class SmsText
{
    // WH 0710 L3:01014,01038,01055 E1:01100 X0
    public static string Daily(string prefix, List<StockAlert> alerts, DateTime now)
    {
        string Part(string key, AlertType t)
        {
            var codes = alerts.Where(a => a.Type == t).Select(a => a.MaterialCode).Distinct().ToList();
            if (codes.Count == 0) return $"{key}0";
            var shown = string.Join(",", codes.Take(6));
            return $"{key}{codes.Count}:{shown}{(codes.Count > 6 ? $"+{codes.Count - 6}" : "")}";
        }
        var pc = new System.Globalization.PersianCalendar();
        return $"{prefix} {pc.GetMonth(now):00}{pc.GetDayOfMonth(now):00}\n{Part("L", AlertType.LowStock)}\n{Part("E", AlertType.Expiring)}\n{Part("X", AlertType.Expired)}";
    }

    // WH L 01014=0.5/2
    public static string Low(string prefix, Material m) => $"{prefix} L {m.Code}={UnitSet.N(m.CurrentStock, 2)}/{UnitSet.N(m.MinStockLevel, 2)}";
}

/// <summary>Sends the daily summary at the chosen hour (Tehran time) and an instant SMS when a material drops to its minimum.</summary>
public class SmsAlertWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly LowStockSignal _signal;
    private readonly ILogger<SmsAlertWorker> _log;
    public SmsAlertWorker(IServiceProvider services, LowStockSignal signal, ILogger<SmsAlertWorker> log) { _services = services; _signal = signal; _log = log; }

    public static DateTime TehranNow()
    {
        try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Asia/Tehran"); }
        catch { return DateTime.UtcNow.AddHours(3.5); }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var instant = Task.Run(() => InstantLoop(stop), stop);
        while (!stop.IsCancellationRequested)
        {
            try { await DailyTick(); } catch (Exception ex) { _log.LogWarning(ex, "Daily SMS check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (TaskCanceledException) { break; }
        }
        await instant;
    }

    private async Task DailyTick()
    {
        using var scope = _services.CreateScope();
        var sms = scope.ServiceProvider.GetRequiredService<SmsService>();
        if (!sms.IsConfigured) return;
        var settings = await sms.GetSettingsAsync();
        var now = TehranNow(); var today = now.ToString("yyyy-MM-dd");
        if (!settings.DailyEnabled || settings.Recipients.Count == 0 || now.Hour < settings.DailyHour || settings.LastDailyDate == today) return;
        await sms.SetAsync(new() { ["daily.last"] = today });
        var alerts = await scope.ServiceProvider.GetRequiredService<WarehouseService>().GetAlertsAsync();
        if (alerts.Count == 0) return;
        await sms.SendAsync(settings.Recipients, settings.Compose(SmsText.Daily(settings.Prefix, alerts, now)), "daily");
    }

    private async Task InstantLoop(CancellationToken stop)
    {
        try
        {
            await foreach (var materialId in _signal.Reader.ReadAllAsync(stop))
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var sms = scope.ServiceProvider.GetRequiredService<SmsService>();
                    if (!sms.IsConfigured) continue;
                    var settings = await sms.GetSettingsAsync();
                    if (!settings.InstantEnabled || settings.Recipients.Count == 0) continue;
                    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
                    await using (db)
                    {
                        var m = await db.Materials.FindAsync(materialId);
                        if (m != null && WarehouseService.IsLowStock(m)) await sms.SendAsync(settings.Recipients, settings.Compose(SmsText.Low(settings.Prefix, m)), "low-stock");
                    }
                }
                catch (Exception ex) { _log.LogWarning(ex, "Instant SMS failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
