using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace WarehouseApp.Services;

/// <summary>Where the Dasht (Sepidar POS) item list comes from: a read-only n8n webhook guarded by an API key header.</summary>
public class DashtItemOptions
{
    // Trimmed for the same reason as SmsOptions: a .env saved on Windows can leave "\r" at the end.
    private string _url = "", _apiKey = "";
    public string Url { get => _url; set => _url = (value ?? "").Trim().Trim('"'); }
    public string ApiKey { get => _apiKey; set => _apiKey = (value ?? "").Trim().Trim('"'); }
    public string HeaderName { get; set; } = "X-Api-Key";
    public bool IsConfigured => _url.Length > 0 && _apiKey.Length > 0;
}

public record DashtItem(int Id, string Code, string Title, string GroupCode, string GroupTitle);

/// <summary>Loads the active Dasht items once and keeps them for a few minutes; the recipe form searches this list.</summary>
public class DashtItemService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly IHttpClientFactory _http;
    private readonly DashtItemOptions _options;
    private readonly ILogger<DashtItemService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<DashtItem> _items = new();
    private DateTime _loadedAt = DateTime.MinValue;

    public DashtItemService(IHttpClientFactory http, IOptions<DashtItemOptions> options, ILogger<DashtItemService> log)
    { _http = http; _options = options.Value; _log = log; }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>The item list, or an empty list plus a Persian error message when Dasht cannot be reached.</summary>
    public async Task<(List<DashtItem> Items, string Error)> GetItemsAsync(bool refresh = false)
    {
        if (!IsConfigured) return (new(), "اتصال به کالاهای دشت تنظیم نشده است (DASHT_ITEMS_URL و DASHT_ITEMS_KEY در .env).");
        if (!refresh && _items.Count > 0 && DateTime.UtcNow - _loadedAt < CacheFor) return (_items, "");
        await _lock.WaitAsync();
        try
        {
            if (!refresh && _items.Count > 0 && DateTime.UtcNow - _loadedAt < CacheFor) return (_items, "");
            var client = _http.CreateClient("dasht");
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
            request.Headers.Add(_options.HeaderName, _options.ApiKey);
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (_items, $"دریافت کالاهای دشت ناموفق بود (کد {(int)response.StatusCode}).");
            var body = await response.Content.ReadFromJsonAsync<ItemsResponse>();
            var items = (body?.Items ?? new()).Where(i => i.Id > 0)
                .Select(i => new DashtItem(i.Id, i.Code ?? "", i.Title ?? "", i.GroupCode ?? "", i.GroupTitle ?? ""))
                .OrderBy(i => i.Code).ToList();
            if (items.Count > 0) { _items = items; _loadedAt = DateTime.UtcNow; }
            return (_items, items.Count > 0 ? "" : "لیست کالاهای دشت خالی برگشت.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Loading Dasht items failed");
            // Keep serving the last good list if there is one.
            return (_items, "ارتباط با دشت برقرار نشد: " + ex.Message);
        }
        finally { _lock.Release(); }
    }

    private class ItemsResponse { public List<ItemDto> Items { get; set; } = new(); }
    private class ItemDto
    {
        public int Id { get; set; }
        public string? Code { get; set; }
        public string? Title { get; set; }
        public string? GroupCode { get; set; }
        public string? GroupTitle { get; set; }
    }
}
