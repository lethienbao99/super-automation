namespace SuperAutomation.Core;

/// <summary>
/// Tình trạng môi trường test cho ô "Môi trường · Lịch" trên trang chính: site IIS, dev server Portal (vỏ + từng module),
/// DB test, Claude. Kết quả giữ 30 giây để nhiều tab/lần vẽ không gọi dồn.
/// </summary>
public sealed class HealthService(TestEnvService testEnv, TestDataService testData, SettingsService settings, PortalDevRunner portalDev)
{
    /// <summary>Action (nếu có): "portal-start:&lt;project&gt;" = bấm để bật dev server, "portal-stop" = tắt dev server do tool bật.</summary>
    public sealed record Item(string Group, string Name, bool? Ok, string Detail, string? Fix = null, string? Action = null);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(4) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, List<Item> Items)? _cache;

    public async Task<object> Get(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _cache is { } c && DateTime.Now - c.At < TimeSpan.FromSeconds(30))
                return new { at = c.At, items = c.Items };
            var items = await Collect(ct);
            _cache = (DateTime.Now, items);
            return new { at = _cache.Value.At, items };
        }
        finally { _gate.Release(); }
    }

    private async Task<List<Item>> Collect(CancellationToken ct)
    {
        var tasks = new List<Task<Item>>();
        // Site IIS trỏ vào repo (Main, API, Identity…)
        foreach (var s in testEnv.Sites())
            tasks.Add(Probe("IIS", ShortName(s.Project.Length > 0 ? s.Project : s.Name), s.Url, ct));
        // DB test
        tasks.Add(Task.Run(async () =>
        {
            if (!testData.Ready) return new Item("Dữ liệu", "DB test", null, "chưa cấu hình", "Cấu hình › DB test");
            var (ok, info) = await testData.Check(null, ct);
            return new Item("Dữ liệu", "DB test", ok, info);
        }, ct));
        var items = (await Task.WhenAll(tasks)).ToList();

        // Portal V3: vỏ + module (cổng TCP — dev server ng serve)
        var root = settings.Pipeline.ExtraRepoPaths.Select(p => p.Path).FirstOrDefault(PortalDev.IsPortalRepo);
        if (root is not null)
        {
            var shellPort = testEnv.ExtraSiteList().Where(s => s.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase))
                .Select(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Port : 0).FirstOrDefault(p => p > 0);
            var mods = PortalDev.Modules(root, shellPort > 0 ? shellPort : 4200);
            var shell = mods[0];
            items.Add(new Item("Portal", "Portal :" + shell.Port, shell.Running, shell.Running ? "vỏ Portal đang chạy" : "vỏ Portal chưa chạy",
                shell.Running ? null : $"yarn {shell.Script}  (trong {root})", shell.Running ? null : "portal-start:shell"));
            foreach (var m in mods.Skip(1).Where(m => m.Running))
                items.Add(new Item("Portal", $"{m.Project} :{m.Port}", true, "module đang chạy"));
            var off = mods.Skip(1).Where(m => !m.Running).ToList();
            if (off.Count > 0)
                items.Add(new Item("Portal", $"{off.Count} module tắt", null, string.Join(", ", off.Select(m => $"{m.Project} :{m.Port}")),
                    "Chạy module của màn cần test, vd: " + string.Join(" · ", off.Take(4).Select(m => "yarn " + m.Script)),
                    "portal-start:" + string.Join(",", off.Select(m => m.Project))));
            var mine = portalDev.Alive();
            if (mine.Count > 0)
                items.Add(new Item("Portal", $"Tool đã bật {mine.Count}", true, string.Join(", ", mine.Select(x => $"{x.Project} :{x.Port}")) + " — đang chạy nền",
                    "Bấm để tắt các dev server do tool bật", "portal-stop"));
        }

        // Claude: có cách xác thực dùng được (đăng nhập hoặc API key đang bật)
        try { var a = settings.ClaudeAuth(); items.Add(new Item("AI", "Claude", true, a.Label)); }
        catch (PipelineException ex) { items.Add(new Item("AI", "Claude", false, ex.Message, "Cấu hình › Claude")); }
        return items;
    }

    private static async Task<Item> Probe(string group, string name, string url, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)r.StatusCode;
            return new Item(group, name, code < 500, $"{url} → HTTP {code}", code < 500 ? null : "Xem log IIS / build lại project của site");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Item(group, name, false, $"{url} → không phản hồi", "Kiểm tra site trong IIS (đang Stop?)");
        }
    }

    /// <summary>"HRM.Presentation.Main" → "Main", "HRM.SC.Service.Identity" → "Identity".</summary>
    private static string ShortName(string project)
    {
        // Không dùng GetFileNameWithoutExtension: "HRM.SC.Service.Identity" sẽ mất ".Identity" (bị coi là đuôi file)
        var n = Path.GetFileName(project.TrimEnd('\\', '/'));
        if (n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) n = n[..^7];
        foreach (var p in new[] { "HRM.Presentation.", "HRM.SC.Service.", "HRM." })
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return n[p.Length..];
        return n;
    }
}
