using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperAutomation.Core;

/// <summary>Chi phí của một phiên Claude (một bước job hoặc một tin nhắn chat).</summary>
/// <param name="Kind">job | chat</param>
/// <param name="Stage">Tên bước: analyze, code, code-r1, review2, fix1, revise1, test-data, test, chat; "older" = phần chênh của các lần chạy cũ không còn log.</param>
/// <param name="Paid">true = trừ tiền thật (API key/Agent SDK); false = ước tính trong gói đăng nhập.</param>
public sealed record CostEntry(DateTime At, long TaskId, string TaskName, string? JobId, string Kind, string Stage,
    string? Model, bool Paid, string? Auth, double CostUsd, long InTokens = 0, long OutTokens = 0, long CacheRead = 0, long CacheWrite = 0);

/// <summary>
/// Sổ chi phí: mỗi phiên Claude ghi một dòng vào data/costs.jsonl (không bị ghi đè khi chạy lại bước).
/// Lần đầu chạy (chưa có file) dựng lại từ log *.jsonl của job/chat và tổng chi phí đã lưu.
/// </summary>
public sealed class CostLedger
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly JobStore _store;
    private readonly string _chatsRoot;
    private readonly ILogger<CostLedger> _log;
    private List<CostEntry>? _entries;

    public CostLedger(JobStore store, SettingsService settings, IWebHostEnvironment env, ILogger<CostLedger> log)
    {
        _store = store;
        _log = log;
        var data = Path.GetFullPath(Path.Combine(env.ContentRootPath, settings.Pipeline.DataDir));
        _file = Path.Combine(data, "costs.jsonl");
        _chatsRoot = Path.Combine(data, "chats");
    }

    public void Add(CostEntry e)
    {
        if (e.CostUsd <= 0 && e.InTokens == 0 && e.OutTokens == 0) return;
        lock (_lock)
        {
            Load();
            _entries!.Add(e);
            File.AppendAllText(_file, JsonSerializer.Serialize(e, EventHub.Json) + "\n", new UTF8Encoding(false));
        }
    }

    public List<CostEntry> All()
    {
        lock (_lock)
        {
            Load();
            return [.. _entries!];
        }
    }

    private void Load()
    {
        if (_entries is not null) return;
        if (!File.Exists(_file))
        {
            _entries = [];
            try
            {
                var rebuilt = Backfill();
                File.WriteAllText(_file, string.Concat(rebuilt.Select(e => JsonSerializer.Serialize(e, EventHub.Json) + "\n")), new UTF8Encoding(false));
                _entries = rebuilt;
                _log.LogInformation("Dựng lại sổ chi phí từ log cũ: {Count} dòng", rebuilt.Count);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Không dựng lại được sổ chi phí từ log cũ"); }
            return;
        }
        _entries = [];
        foreach (var line in File.ReadLines(_file))
        {
            if (line.Trim().Length == 0) continue;
            try { if (JsonSerializer.Deserialize<CostEntry>(line, EventHub.Json) is { } e) _entries.Add(e); }
            catch (JsonException) { }
        }
    }

    // ---------- Dựng lại từ log cũ ----------

    private sealed record LoggedRun(DateTime At, string Stage, string? Session, double Cumulative, string? Model, bool Paid, long In, long Out, long CR, long CW);

    /// <summary>Đọc kết quả các phiên trong thư mục; phiên --resume báo tổng cộng dồn nên trừ phần đã tính của phiên đó.</summary>
    private static List<(LoggedRun Run, double Cost)> ReadRuns(IEnumerable<string> files, Func<string, string> stageOf)
    {
        var runs = new List<LoggedRun>();
        foreach (var f in files)
        {
            string? model = null, source = null;
            foreach (var line in File.ReadLines(f))
            {
                if (!line.StartsWith('{')) continue;
                JsonNode? n;
                try { n = JsonNode.Parse(line); } catch (JsonException) { continue; }
                var type = n?["type"]?.ToString();
                if (type == "system" && n?["subtype"]?.ToString() == "init") { model = n["model"]?.ToString(); source = n["apiKeySource"]?.ToString(); }
                if (type != "result") continue;
                var u = n?["usage"];
                runs.Add(new LoggedRun(File.GetLastWriteTime(f), stageOf(Path.GetFileNameWithoutExtension(f)), n?["session_id"]?.ToString(),
                    Num(n?["total_cost_usd"]), model, source is not (null or "none"),
                    Long(u?["input_tokens"]), Long(u?["output_tokens"]), Long(u?["cache_read_input_tokens"]), Long(u?["cache_creation_input_tokens"])));
            }
        }
        var seen = new Dictionary<string, double>();
        var result = new List<(LoggedRun, double)>();
        foreach (var r in runs.OrderBy(r => r.At))
        {
            var prev = r.Session is not null && seen.TryGetValue(r.Session, out var p) ? p : 0;
            result.Add((r, Math.Max(0, r.Cumulative - prev)));
            if (r.Session is not null) seen[r.Session] = Math.Max(prev, r.Cumulative);
        }
        return result;
    }

    private List<CostEntry> Backfill()
    {
        var list = new List<CostEntry>();
        foreach (var job in _store.All())
        {
            var dir = _store.Dir(job.Id);
            if (!Directory.Exists(dir)) continue;
            var files = Directory.GetFiles(dir, "*.jsonl").Where(f => Path.GetFileName(f) != "log.jsonl");
            var runs = ReadRuns(files, name => name);
            foreach (var (r, cost) in runs)
                list.Add(new CostEntry(r.At, job.TaskId, job.TaskName, job.Id, "job", r.Stage, r.Model, r.Paid, null, Math.Round(cost, 6), r.In, r.Out, r.CR, r.CW));
            // Log mỗi bước bị ghi đè khi chạy lại → phần chênh so với tổng đã lưu là của các lần chạy cũ
            var logged = runs.Sum(x => x.Cost);
            if (job.CostUsd - logged > 0.01)
                list.Add(new CostEntry(job.FinishedAt ?? job.StartedAt ?? job.CreatedAt, job.TaskId, job.TaskName, job.Id, "job", "older", null, false, null, Math.Round(job.CostUsd - logged, 6)));
        }
        if (Directory.Exists(_chatsRoot))
        {
            foreach (var dir in Directory.GetDirectories(_chatsRoot))
            {
                if (!long.TryParse(Path.GetFileName(dir), out var taskId)) continue;
                var name = "";
                try { name = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "chat.json")))?["taskName"]?.ToString() ?? ""; } catch { }
                foreach (var (r, cost) in ReadRuns(Directory.GetFiles(dir, "chat-*.jsonl"), _ => "chat"))
                    list.Add(new CostEntry(r.At, taskId, name, null, "chat", "chat", r.Model, r.Paid, null, Math.Round(cost, 6), r.In, r.Out, r.CR, r.CW));
            }
        }
        return [.. list.OrderBy(e => e.At)];
    }

    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var d) ? d : 0;

    // ---------- Thống kê ----------

    /// <summary>Gộp nhóm bước: code-r1 → code, review2 → review, fix1 → fix, revise1 → revise.</summary>
    public static string StageGroup(string stage) => stage switch
    {
        _ when stage.StartsWith("analyze") => "analyze",
        _ when stage.StartsWith("code") => "code",
        _ when stage.StartsWith("review") => "review",
        _ when stage.StartsWith("fix") => "fix",
        _ when stage.StartsWith("revise") => "revise",
        _ => stage,
    };

    /// <param name="days">0 = toàn bộ</param>
    /// <param name="paid">all | paid | est</param>
    public object Stats(int days, string paid)
    {
        var from = days > 0 ? DateTime.Today.AddDays(-(days - 1)) : DateTime.MinValue;
        var list = All().Where(e => e.At >= from && (paid == "paid" ? e.Paid : paid == "est" ? !e.Paid : true)).ToList();
        static double R(double x) => Math.Round(x, 2);
        var names = _store.All().GroupBy(j => j.TaskId).ToDictionary(g => g.Key, g => g.First().TaskName);
        return new
        {
            days,
            paid,
            total = R(list.Sum(e => e.CostUsd)),
            paidTotal = R(list.Where(e => e.Paid).Sum(e => e.CostUsd)),
            estTotal = R(list.Where(e => !e.Paid).Sum(e => e.CostUsd)),
            runs = list.Count(e => e.Stage != "older"),
            tasks = list.Select(e => e.TaskId).Distinct().Count(),
            byTask = list.GroupBy(e => e.TaskId).Select(g => new
            {
                taskId = g.Key,
                taskName = names.GetValueOrDefault(g.Key) is { Length: > 0 } n ? n : g.Select(e => e.TaskName).FirstOrDefault(x => x.Length > 0) ?? "",
                job = R(g.Where(e => e.Kind == "job").Sum(e => e.CostUsd)),
                chat = R(g.Where(e => e.Kind == "chat").Sum(e => e.CostUsd)),
                total = R(g.Sum(e => e.CostUsd)),
                paid = R(g.Where(e => e.Paid).Sum(e => e.CostUsd)),
                runs = g.Count(e => e.Stage != "older"),
                last = g.Max(e => e.At),
                stages = g.GroupBy(e => e.Kind == "chat" ? "chat" : StageGroup(e.Stage)).ToDictionary(s => s.Key, s => R(s.Sum(e => e.CostUsd))),
            }).OrderByDescending(t => t.total).ToList(),
            byStage = list.GroupBy(e => e.Kind == "chat" ? "chat" : StageGroup(e.Stage))
                .Select(g => new { key = g.Key, cost = R(g.Sum(e => e.CostUsd)), runs = g.Count() }).OrderByDescending(x => x.cost).ToList(),
            byModel = list.Where(e => e.Stage != "older").GroupBy(e => e.Model ?? "?")
                .Select(g => new { key = g.Key, cost = R(g.Sum(e => e.CostUsd)), runs = g.Count() }).OrderByDescending(x => x.cost).ToList(),
            byAuth = list.Where(e => e.Stage != "older").GroupBy(e => e.Auth ?? (e.Paid ? "API key" : "Tài khoản đăng nhập"))
                .Select(g => new { key = g.Key, cost = R(g.Sum(e => e.CostUsd)), runs = g.Count() }).OrderByDescending(x => x.cost).ToList(),
            byDay = list.GroupBy(e => e.At.Date).OrderBy(g => g.Key)
                .Select(g => new { day = g.Key.ToString("yyyy-MM-dd"), cost = R(g.Sum(e => e.CostUsd)), paid = R(g.Where(e => e.Paid).Sum(e => e.CostUsd)) }).ToList(),
        };
    }
}
