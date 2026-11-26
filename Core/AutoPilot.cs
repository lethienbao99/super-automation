using System.Text;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>Cấu hình chế độ tự động — mặc định TẮT, bật từ ⚙ Cấu hình. Lưu ở appsettings.Local.json, mục AutoPilot.</summary>
public sealed class AutoPilotOptions
{
    /// <summary>Bật chạy nền: định kỳ làm mới danh sách task từ AMIS.</summary>
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Nhóm task theo dõi: TotalTaskDue | TotalTaskDueSoon | TotalTaskExpire.</summary>
    public List<string> TaskTypes { get; set; } = ["TotalTaskDue"];
    /// <summary>Tự giao task mới cho AI phân tích yêu cầu (chỉ đọc). Code vẫn theo AutoCodeWhenClear.</summary>
    public bool AutoAnalyze { get; set; }
    /// <summary>Chỉ tự giao task thuộc dự án khớp nhánh repo hiện tại.</summary>
    public bool OnlyMatchingBranch { get; set; } = true;
    /// <summary>Mã dự án được tự giao, cách nhau bằng dấu phẩy (DEF, ABC). Trống = mọi dự án.</summary>
    public string Projects { get; set; } = "";
    public int MaxPerRun { get; set; } = 3;

    public AutoPilotOptions Clone() => new()
    {
        Enabled = Enabled, IntervalMinutes = IntervalMinutes, TaskTypes = [.. TaskTypes], AutoAnalyze = AutoAnalyze,
        OnlyMatchingBranch = OnlyMatchingBranch, Projects = Projects, MaxPerRun = MaxPerRun,
    };
}

public sealed record AutoPilotAction(DateTime At, string Text, bool Error = false);

/// <summary>So khớp dự án AMIS với nhánh git — cùng quy tắc với giao diện (app.js: projectKey, branchCheck).</summary>
public static partial class ProjectMatch
{
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>"DEF - HĐ-2026/11" → DEF, "GHI (POC) - …" → GHI.</summary>
    public static string Key(string? projectName) =>
        string.IsNullOrEmpty(projectName) ? "" : Norm(Paren().Replace(projectName.Split(" - ")[0], ""));

    public static bool Matches(string? projectName, string branch, IReadOnlyDictionary<string, string> map)
    {
        var key = Key(projectName);
        if (key.Length == 0) return false;
        var hit = map.FirstOrDefault(kv => Norm(kv.Key) == key);
        var b = Norm(branch);
        if (hit.Value is { } v)
            return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(k => k == "*" || (Norm(k).Length > 0 && b.Contains(Norm(k))));
        // Mặc định: từ đầu của mã dự án là đầu một đoạn nhánh (XYZ ↔ XYZ_v8…, ABC ↔ ABCVN_…), từ khác trùng hẳn một đoạn, hoặc cả mã liền nằm trong nhánh
        if (b.Contains(key)) return true;
        var words = Words(projectName);
        var tokens = Tokens(branch);
        return words.Select((w, i) => (w, i)).Any(x => tokens.Any(t => t == x.w || (x.i == 0 && t.StartsWith(x.w, StringComparison.Ordinal))));
    }

    /// <summary>"XYZ Power - …" → [XYZ, POWER]; bỏ phần trong ngoặc, từ dưới 2 ký tự.</summary>
    public static List<string> Words(string? projectName) =>
        string.IsNullOrEmpty(projectName) ? [] :
        WordSep().Split(Paren().Replace(projectName.Split(" - ")[0], "")).Select(Norm).Where(w => w.Length >= 2).ToList();

    /// <summary>"dev/XYZ_v8.12.46" → [DEV, XYZ, V8, 12, 46].</summary>
    public static List<string> Tokens(string branch) =>
        NonAlnum().Split(Norm2(branch)).Where(t => t.Length > 0).ToList();

    /// <summary>Như Norm nhưng giữ dấu phân cách (thay bằng khoảng trắng) để tách đoạn.</summary>
    private static string Norm2(string s)
    {
        var d = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c < 128 && char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"[\s._\-/]+")] private static partial Regex WordSep();
    [GeneratedRegex(@"\s+")] private static partial Regex NonAlnum();

    [GeneratedRegex(@"\(.*?\)")] private static partial Regex Paren();
}

/// <summary>
/// Chạy nền theo chu kỳ: làm mới danh sách task AMIS (đẩy lên UI qua SSE) và — nếu được bật — tự giao task mới cho AI phân tích.
/// Chỉ tự giao task chưa từng có job (kể cả job đã huỷ), để không tốn phí lặp lại cho task bạn đã chủ động bỏ.
/// </summary>
public sealed class AutoPilotService(
    AmisClient amis, JobStore store, JobRunner runner, RepoService repo, SettingsService settings,
    EventHub hub, IWebHostEnvironment env, IConfiguration config, ILogger<AutoPilotService> log) : BackgroundService
{
    private static readonly HashSet<string> Types = ["TotalTaskDue", "TotalTaskDueSoon", "TotalTaskExpire"];
    private readonly object _lock = new();
    private readonly List<AutoPilotAction> _actions = [];
    private AutoPilotOptions _opt = Load(config);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime? _lastRun, _nextRun;
    private bool _running;

    public AutoPilotOptions Options { get { lock (_lock) return _opt.Clone(); } }

    private static AutoPilotOptions Load(IConfiguration config)
    {
        var o = new AutoPilotOptions();
        var s = config.GetSection("AutoPilot");
        if (!s.Exists()) return o;
        o.Enabled = s.GetValue("Enabled", o.Enabled);
        o.IntervalMinutes = s.GetValue("IntervalMinutes", o.IntervalMinutes);
        var types = s.GetSection("TaskTypes").Get<string[]>();
        if (types is { Length: > 0 }) o.TaskTypes = types.Where(Types.Contains).Distinct().ToList();
        o.AutoAnalyze = s.GetValue("AutoAnalyze", o.AutoAnalyze);
        o.OnlyMatchingBranch = s.GetValue("OnlyMatchingBranch", o.OnlyMatchingBranch);
        o.Projects = s.GetValue("Projects", o.Projects) ?? "";
        o.MaxPerRun = s.GetValue("MaxPerRun", o.MaxPerRun);
        return o;
    }

    public object State()
    {
        lock (_lock)
            return new { options = _opt.Clone(), lastRun = _lastRun, nextRun = _opt.Enabled ? _nextRun : null, running = _running, actions = _actions.ToList() };
    }

    public string? Update(AutoPilotOptions o)
    {
        if (o.IntervalMinutes is < 5 or > 24 * 60) return "Chu kỳ phải từ 5 phút đến 24 giờ.";
        if (o.MaxPerRun is < 1 or > 20) return "Số task tự giao mỗi lần phải từ 1 đến 20.";
        o.TaskTypes = (o.TaskTypes ?? []).Where(Types.Contains).Distinct().ToList();
        if (o.Enabled && o.TaskTypes.Count == 0) return "Chọn ít nhất một nhóm task để theo dõi.";
        o.Projects = string.Join(", ", (o.Projects ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "AutoPilot");
            s["Enabled"] = o.Enabled;
            s["IntervalMinutes"] = o.IntervalMinutes;
            s["TaskTypes"] = new System.Text.Json.Nodes.JsonArray(o.TaskTypes.Select(t => (System.Text.Json.Nodes.JsonNode?)t).ToArray());
            s["AutoAnalyze"] = o.AutoAnalyze;
            s["OnlyMatchingBranch"] = o.OnlyMatchingBranch;
            s["Projects"] = o.Projects;
            s["MaxPerRun"] = o.MaxPerRun;
        });
        lock (_lock)
        {
            _opt = o.Clone();
            _nextRun = o.Enabled ? DateTime.Now.AddMinutes(o.IntervalMinutes) : null;
        }
        Note($"Đã lưu cấu hình tự động: {(o.Enabled ? $"bật, mỗi {o.IntervalMinutes} phút" : "tắt")}{(o.Enabled && o.AutoAnalyze ? ", tự giao AI phân tích" : "")}.");
        Wake();
        return null;
    }

    /// <summary>Chạy ngay một lượt (nút "Chạy ngay"), kể cả khi chế độ tự động đang tắt.</summary>
    public void RunNow()
    {
        lock (_lock) _nextRun = DateTime.Now;
        Wake(force: true);
    }

    private bool _forceOnce;

    private void Wake(bool force = false)
    {
        lock (_lock)
        {
            if (force) _forceOnce = true;
            _wake.TrySetResult();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        lock (_lock) _nextRun = _opt.Enabled ? DateTime.Now.AddMinutes(1) : null; // chạy lượt đầu sau 1 phút khi tool khởi động
        Publish();
        while (!stop.IsCancellationRequested)
        {
            Task wake;
            TimeSpan delay;
            lock (_lock)
            {
                if (_wake.Task.IsCompleted) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
                delay = _nextRun is { } n ? n - DateTime.Now : Timeout.InfiniteTimeSpan;
                if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            }
            await Task.WhenAny(wake, Task.Delay(delay, stop));
            if (stop.IsCancellationRequested) break;

            bool due, force;
            lock (_lock)
            {
                force = _forceOnce;
                due = force || (_opt.Enabled && _nextRun is { } n && n <= DateTime.Now);
                _forceOnce = false;
            }
            if (!due) { Publish(); continue; }

            await RunOnce(stop);
            lock (_lock) _nextRun = _opt.Enabled ? DateTime.Now.AddMinutes(_opt.IntervalMinutes) : null;
            Publish();
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        var o = Options;
        lock (_lock) _running = true;
        Publish();
        try
        {
            var created = 0;
            var summary = new List<string>();
            var branch = "";
            if (o.AutoAnalyze)
            {
                try { branch = (await repo.Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim(); }
                catch (PipelineException ex) { Note($"Không đọc được nhánh hiện tại, bỏ qua tự giao: {ex.Message}", true); }
            }
            var allowed = o.Projects.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ProjectMatch.Norm).ToHashSet();

            foreach (var type in o.TaskTypes)
            {
                var tasks = await amis.GetMyTasks(type, ct);
                hub.Publish(new { kind = "tasks", type, list = tasks });
                summary.Add($"{Label(type)}: {tasks.Count}");
                if (!o.AutoAnalyze || branch.Length == 0) continue;

                foreach (var t in tasks)
                {
                    if (created >= o.MaxPerRun) break;
                    if (store.All().Any(j => j.TaskId == t.TaskId)) continue; // đã từng giao (kể cả đã huỷ)
                    var key = ProjectMatch.Key(t.ProjectName);
                    if (allowed.Count > 0 && !allowed.Contains(key)) continue;
                    if (o.OnlyMatchingBranch && !ProjectMatch.Matches(t.ProjectName, branch, settings.ProjectBranches)) continue;

                    var job = store.Create(t.TaskId, t.TaskName ?? $"Task {t.TaskId}", t.ProjectName, t.EndDate, null);
                    store.Log(job, "auto", "stage", $"Chế độ tự động giao task ({Label(type)}, dự án {key}, nhánh {branch}) để AI phân tích yêu cầu.");
                    store.Save(job);
                    runner.Enqueue(job);
                    created++;
                    Note($"Tự giao AI phân tích: #{t.TaskId} {t.TaskName} ({key})");
                }
            }
            lock (_lock) _lastRun = DateTime.Now;
            Note($"Đã làm mới {string.Join(", ", summary)}{(o.AutoAnalyze ? $"; tự giao {created} task" : "")}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is AmisException or HttpRequestException or PipelineException)
        {
            lock (_lock) _lastRun = DateTime.Now;
            Note($"Lỗi khi chạy tự động: {ex.Message}", true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "AutoPilot lỗi ngoài dự kiến");
            Note($"Lỗi ngoài dự kiến: {ex.Message}", true);
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    private static string Label(string type) => type switch
    {
        "TotalTaskDue" => "đến hạn hôm nay",
        "TotalTaskDueSoon" => "sắp đến hạn",
        "TotalTaskExpire" => "quá hạn",
        _ => type,
    };

    private void Note(string text, bool error = false)
    {
        lock (_lock)
        {
            _actions.Insert(0, new AutoPilotAction(DateTime.Now, text, error));
            if (_actions.Count > 30) _actions.RemoveRange(30, _actions.Count - 30);
        }
        if (error) log.LogWarning("AutoPilot: {Text}", text);
        Publish();
    }

    private void Publish() => hub.Publish(new { kind = "autopilot", state = State() });
}
