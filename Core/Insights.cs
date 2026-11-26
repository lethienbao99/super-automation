using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>Một lưu ý rút ra từ góp ý của người dùng, gắn vào prompt các task sau để AI không lặp lại lỗi cũ.</summary>
public sealed class Lesson
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Text { get; set; } = "";
    /// <summary>true = gợi ý tự gom từ góp ý (yêu cầu sửa / lý do từ chối), chờ người dùng sửa lời và duyệt; chưa đưa vào prompt.</summary>
    public bool Pending { get; set; }
    public bool Enabled { get; set; } = true;
    public long? TaskId { get; set; }
    public string? TaskName { get; set; }
    /// <summary>revise | reject | manual</summary>
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Giúp các task sau nhanh hơn nhờ dữ liệu của task trước:
/// bài học từ góp ý, task tương tự đã làm (đưa vào prompt), và thống kê thời gian từng bước để biết nghẽn ở đâu.
/// </summary>
public sealed class Insights(JobStore store, CostLedger costs, SettingsService settings, IWebHostEnvironment env)
{
    private readonly object _lock = new();
    private const int MaxPending = 100;

    private string LessonFile => Path.GetFullPath(Path.Combine(env.ContentRootPath, settings.Pipeline.DataDir, "lessons.json"));

    // ---------- Bài học ----------

    public List<Lesson> Lessons()
    {
        lock (_lock)
        {
            try { return File.Exists(LessonFile) ? JsonSerializer.Deserialize<List<Lesson>>(File.ReadAllText(LessonFile), EventHub.Json) ?? [] : []; }
            catch (JsonException) { return []; }
        }
    }

    private void Save(List<Lesson> list)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LessonFile)!);
            var tmp = LessonFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, EventHub.JsonIndented), new UTF8Encoding(false));
            File.Move(tmp, LessonFile, true);
        }
    }

    /// <summary>Góp ý của người dùng (yêu cầu sửa, lý do từ chối) thành gợi ý bài học chờ duyệt.</summary>
    public void Suggest(Job job, string source, string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 8) return;
        if (t.Length > 1500) t = t[..1500] + "…";
        lock (_lock)
        {
            var list = Lessons();
            if (list.Any(l => string.Equals(l.Text.Trim(), t, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Lesson { Text = t, Pending = true, TaskId = job.TaskId, TaskName = job.TaskName, Source = source });
            var pending = list.Where(l => l.Pending).OrderBy(l => l.CreatedAt).ToList();
            foreach (var old in pending.Take(Math.Max(0, pending.Count - MaxPending))) list.Remove(old);
            Save(list);
        }
    }

    public Lesson Upsert(string? id, string text, bool enabled)
    {
        var t = text.Trim();
        if (t.Length == 0) throw new PipelineException("Nội dung bài học trống.");
        if (t.Length > 2000) throw new PipelineException("Bài học quá dài (tối đa 2000 ký tự) — viết gọn thành quy tắc.");
        lock (_lock)
        {
            var list = Lessons();
            var l = id is null ? null : list.FirstOrDefault(x => x.Id == id);
            if (l is null) list.Add(l = new Lesson());
            l.Text = t;
            l.Enabled = enabled;
            l.Pending = false;
            Save(list);
            return l;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var list = Lessons();
            var n = list.RemoveAll(l => l.Id == id);
            if (n > 0) Save(list);
            return n > 0;
        }
    }

    private string LessonBlock(bool review)
    {
        var active = Lessons().Where(l => !l.Pending && l.Enabled).ToList();
        if (active.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Lưu ý người dùng rút ra từ các task trước (áp dụng khi liên quan tới task này; yêu cầu và chat.md của chính task được ưu tiên hơn):");
        foreach (var l in active) sb.AppendLine("- " + l.Text.Replace("\n", "\n  "));
        if (review) sb.AppendLine("Khi review: code vi phạm lưu ý nào ở trên (mà lưu ý đó liên quan tới task) là \"Lỗi phải sửa\".");
        return sb.ToString();
    }

    // ---------- Task tương tự ----------

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "và", "của", "cho", "các", "khi", "thì", "là", "có", "không", "được", "trong", "với", "theo", "để", "một", "những", "đã", "sẽ",
        "bị", "ra", "vào", "lại", "này", "đó", "nếu", "thêm", "sửa", "lỗi", "chỉnh", "cập", "nhật", "màn", "hình", "task", "the", "and", "for",
    };

    /// <summary>Tập âm tiết + cặp âm tiết liền nhau của tên task (bỏ mã [..] và nhãn loại) — tiếng Việt nhiều từ ghép hai âm tiết.</summary>
    private static HashSet<string> Terms(params string?[] names)
    {
        var set = new HashSet<string>();
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var words = Regex.Split(Standards.Description(n).ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 1).ToList();
            for (var i = 0; i < words.Count; i++)
            {
                if (!Stop.Contains(words[i])) set.Add(words[i]);
                if (i + 1 < words.Count) set.Add(words[i] + " " + words[i + 1]);
            }
        }
        return set;
    }

    private static bool SameFile(string a, string b) =>
        a == b || a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal);

    public sealed record SimilarJob(Job Job, double Score, List<string> SharedFiles);

    private static readonly Regex ParentLine = new(@"công việc con của task #\d+ ""(.+?)""(?= —|\s*$)", RegexOptions.Multiline);

    /// <summary>
    /// Tên mô tả đúng yêu cầu: task cha nếu có (task con trên AMIS thường chỉ tên chung chung "SE thực hiện"),
    /// job cũ chưa lưu tên task cha thì đọc từ task.md.
    /// </summary>
    private string Title(Job j)
    {
        if (!string.IsNullOrWhiteSpace(j.ParentTaskName)) return j.ParentTaskName!;
        try
        {
            var f = Path.Combine(store.Dir(j.Id), "task.md");
            if (File.Exists(f))
            {
                using var r = new StreamReader(f);
                for (var i = 0; i < 12 && r.ReadLine() is { } line; i++)
                    if (ParentLine.Match(line) is { Success: true } m) return m.Groups[1].Value;
            }
        }
        catch (IOException) { }
        return j.TaskName;
    }

    /// <summary>Job khác đã làm xong (đã duyệt/chờ duyệt) hoặc đã phân tích, giống task này theo tên và file dự kiến.</summary>
    public List<SimilarJob> Similar(Job job, int max = 3)
    {
        var mine = Terms(Title(job));
        var list = new List<SimilarJob>();
        foreach (var o in store.All())
        {
            if (o.Id == job.Id || o.TaskId == job.TaskId) continue;
            var done = o.Status is JobStatus.Approved or JobStatus.AwaitingApproval && o.HeadSha is not null;
            if (!done && !File.Exists(Path.Combine(store.Dir(o.Id), "analysis.md"))) continue;
            if (o.Status is JobStatus.Rejected or JobStatus.Cancelled) continue;
            var theirs = Terms(Title(o));
            var union = mine.Count + theirs.Count - mine.Count(theirs.Contains);
            var score = union == 0 ? 0 : (double)mine.Count(theirs.Contains) / union;
            var files = o.ChangedFiles.Count > 0 ? o.ChangedFiles : o.PlannedFiles;
            var shared = job.PlannedFiles.Where(p => files.Any(f => SameFile(p, f))).Distinct().ToList();
            score += Math.Min(0.45, shared.Count * 0.15);
            if (done) score += 0.03;
            if (score >= 0.2) list.Add(new(o, score, shared));
        }
        return list.OrderByDescending(s => s.Score).Take(max).ToList();
    }

    private string SimilarBlock(Job job)
    {
        var sim = Similar(job);
        if (sim.Count == 0) return "";
        var o = settings.Pipeline;
        var sb = new StringBuilder();
        sb.AppendLine("Task tương tự đã làm trước đây — đọc để biết nhanh nghiệp vụ và chỗ cần sửa, đỡ phải dò lại từ đầu; nhưng kiểm lại với code hiện tại, không chép máy móc, task này có thể khác:");
        foreach (var s in sim)
        {
            var j = s.Job;
            var dir = store.Dir(j.Id);
            var state = j.Status switch { JobStatus.Approved => "đã duyệt", JobStatus.AwaitingApproval => "AI làm xong, chờ duyệt", _ => "mới phân tích" };
            var title = Title(j);
            sb.Append($"- #{j.TaskId} \"{title}\"{(title != j.TaskName ? $" (task con \"{j.TaskName}\")" : "")} ({state}");
            if (s.SharedFiles.Count > 0) sb.Append($"; cùng sửa: {string.Join(", ", s.SharedFiles.Take(5).Select(Path.GetFileName))}");
            sb.AppendLine(")");
            var docs = new[] { "analysis.md", "plan.md", "pr.md" }.Where(f => File.Exists(Path.Combine(dir, f))).Select(f => Path.Combine(dir, f)).ToList();
            if (docs.Count > 0) sb.AppendLine("  tài liệu: " + string.Join(" · ", docs));
            if (j.HeadSha is not null && j.BaseSha is not null && j.HeadSha != j.BaseSha)
                sb.AppendLine($"  code (nếu commit còn): git -C \"{o.RepoPath}\" diff {j.BaseSha[..Math.Min(12, j.BaseSha.Length)]} {j.HeadSha[..Math.Min(12, j.HeadSha.Length)]}");
            foreach (var r in j.ExtraRepos.Where(r => r.HeadSha is not null && r.BaseSha is not null && r.HeadSha != r.BaseSha))
                sb.AppendLine($"  code repo {r.Rel}: git -C \"{r.Path}\" diff {r.BaseSha![..Math.Min(12, r.BaseSha!.Length)]} {r.HeadSha![..Math.Min(12, r.HeadSha!.Length)]}");
        }
        return sb.ToString();
    }

    /// <summary>Phần đặt trước prompt của một bước: bài học (code/sửa/review) và task tương tự (phân tích, code lần đầu).</summary>
    public string Context(Job job, string stage)
    {
        var parts = new List<string>();
        var group = CostLedger.StageGroup(stage);
        try
        {
            if (group == "analyze" || stage == "code") parts.Add(SimilarBlock(job));
            if (group is "code" or "revise" or "fix" or "review") parts.Add(LessonBlock(group == "review"));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        parts.RemoveAll(p => p.Length == 0);
        return parts.Count == 0 ? "" : string.Join("\n", parts) + "\n---\n\n";
    }

    // ---------- Thống kê thời gian ----------

    /// <summary>Nhóm bước theo stage trong log; null = mốc thao tác (không tính thời gian máy).</summary>
    private static string? TimeGroup(string stage) => stage switch
    {
        "control" or "answer" or "decision" or "done" or "error" => null,
        "data" => "test-data",
        "git" or "inputs" => "git",
        _ => CostLedger.StageGroup(stage) switch { "code" or "revise" => "code", var g => g },
    };

    private static string StageName(string g) => g switch
    {
        "analyze" => "Phân tích", "code" => "Code / sửa thêm", "review" => "Review", "fix" => "Sửa theo review",
        "test-data" => "Dữ liệu test", "test" => "Test", "build" => "Build", "git" => "Git", _ => g,
    };

    private static bool UserAction(string stage) => stage is "control" or "answer" or "decision";

    public object Stats(int days)
    {
        var from = days > 0 ? DateTime.Today.AddDays(-(days - 1)) : DateTime.MinValue;
        var jobs = store.All().Where(j => j.CreatedAt >= from).ToList();
        var ledger = costs.All().Where(e => e.Kind == "job" && e.JobId is not null).GroupBy(e => e.JobId!).ToDictionary(g => g.Key, g => g.ToList());
        var perJob = new List<(Job Job, Dictionary<string, double> Min, double Wait, double Cost)>();
        foreach (var j in jobs)
        {
            var logs = store.ReadLogs(j.Id);
            if (logs.Count == 0) continue;
            // Gom các dòng log liền nhau cùng stage thành một đoạn
            var segs = new List<(string Stage, DateTime First, DateTime Last)>();
            // (git/build/data do tool tự làm, xong trong vài phút: lặng lâu giữa hai dòng là đang chờ người, ví dụ giữ code chờ bấm commit → tách đoạn.
            //  Bước Claude thì không tách — một lệnh build/test trong phiên có thể chạy lâu mà không ra log.)
            foreach (var e in logs)
                if (segs.Count > 0 && segs[^1].Stage == e.Stage
                    && (TimeGroup(e.Stage) is not ("git" or "build" or "test-data") || e.Stage == "test-data" || (e.At - segs[^1].Last).TotalMinutes <= 15))
                    segs[^1] = (e.Stage, segs[^1].First, e.At);
                else segs.Add((e.Stage, e.At, e.At));
            var min = new Dictionary<string, double>();
            double wait = 0;
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (UserAction(s.Stage))
                {
                    // Khoảng từ lúc máy dừng tới khi người dùng thao tác = thời gian chờ người
                    if (i > 0)
                    {
                        var gap = (s.First - segs[i - 1].Last).TotalMinutes;
                        if (gap > 1) wait += Math.Min(gap, 24 * 60);
                    }
                    continue;
                }
                if (TimeGroup(s.Stage) is not { } g) continue;
                // Bước kéo tới đoạn kế tiếp nếu nối liền; lặng quá 5 phút là máy đã dừng chờ (hộp commit, chờ dọn dữ liệu, tool tắt…)
                var next = i + 1 < segs.Count ? segs[i + 1] : default;
                var end = next.Stage is not null && !UserAction(next.Stage) && (next.First - s.Last).TotalMinutes <= 5 ? next.First : s.Last;
                if (next.Stage is not null && !UserAction(next.Stage) && (next.First - s.Last).TotalMinutes > 5)
                    wait += Math.Min((next.First - s.Last).TotalMinutes, 24 * 60);
                var m = Math.Clamp((end - s.First).TotalMinutes, 0, 180);
                min[g] = min.GetValueOrDefault(g) + m;
            }
            var cost = ledger.TryGetValue(j.Id, out var ce) ? ce.Sum(e => e.CostUsd) : j.CostUsd;
            perJob.Add((j, min, wait, cost));
        }

        static double R(double x) => Math.Round(x, 1);
        var groups = perJob.SelectMany(p => p.Min.Keys).Distinct().ToList();
        var allCost = ledger.Where(kv => perJob.Any(p => p.Job.Id == kv.Key)).SelectMany(kv => kv.Value).ToList();
        var totalMachine = perJob.Sum(p => p.Min.Values.Sum());
        var byStage = groups.Select(g =>
        {
            var with = perJob.Where(p => p.Min.ContainsKey(g)).ToList();
            var total = with.Sum(p => p.Min[g]);
            return new
            {
                key = g,
                minutes = R(total),
                avg = R(with.Count == 0 ? 0 : total / with.Count),
                jobs = with.Count,
                share = totalMachine == 0 ? 0 : Math.Round(total / totalMachine * 100),
                cost = Math.Round(allCost.Where(e => (CostLedger.StageGroup(e.Stage) is "revise" ? "code" : CostLedger.StageGroup(e.Stage)) == g).Sum(e => e.CostUsd), 2),
            };
        }).OrderByDescending(x => x.minutes).ToList();

        var coded = perJob.Where(p => p.Min.ContainsKey("review")).ToList();
        var n = perJob.Count;
        var hints = new List<string>();
        if (byStage.FirstOrDefault() is { } top && top.share >= 30)
            hints.Add($"Bước \"{StageName(top.key)}\" chiếm {top.share}% thời gian máy chạy (trung bình {top.avg} phút/job) — nên tối ưu bước này trước.");
        if (coded.Count >= 2 && coded.Count(p => p.Job.FixRounds > 0) * 2 >= coded.Count)
            hints.Add($"{coded.Count(p => p.Job.FixRounds > 0)}/{coded.Count} job bị review bắt lỗi ngay lần đầu — ghi các lỗi hay gặp vào ⚙ Cấu hình › Bài học để AI code tránh từ đầu.");
        if (n >= 2 && perJob.Sum(p => p.Wait) > totalMachine)
            hints.Add("Thời gian chờ bạn thao tác (duyệt, trả lời, cho chạy tiếp) nhiều hơn thời gian máy chạy — giao theo lô hoặc bật chế độ Tự động để máy chạy liên tục.");
        if (n >= 2 && perJob.Count(p => p.Job.AnalyzeRounds > 1 || p.Job.QuestionRounds > 0) * 3 >= n)
            hints.Add("Nhiều task phải hỏi lại yêu cầu — gom câu hỏi gửi BA một lần, hoặc bổ sung tài liệu trước khi giao.");

        return new
        {
            days,
            jobs = n,
            machineAvg = R(n == 0 ? 0 : totalMachine / n),
            waitAvg = R(n == 0 ? 0 : perJob.Sum(p => p.Wait) / n),
            reviewFirstPass = coded.Count == 0 ? (double?)null : Math.Round(coded.Count(p => p.Job.FixRounds == 0) * 100.0 / coded.Count),
            askedAgain = n == 0 ? (double?)null : Math.Round(perJob.Count(p => p.Job.AnalyzeRounds > 1 || p.Job.QuestionRounds > 0) * 100.0 / n),
            revisionsAvg = n == 0 ? 0 : Math.Round(perJob.Average(p => p.Job.Revisions), 1),
            byStage,
            byJob = perJob.OrderByDescending(p => p.Job.CreatedAt).Select(p => new
            {
                id = p.Job.Id,
                taskId = p.Job.TaskId,
                taskName = p.Job.TaskName,
                status = p.Job.Status.ToString(),
                machine = R(p.Min.Values.Sum()),
                wait = R(p.Wait),
                fixRounds = p.Job.FixRounds,
                revisions = p.Job.Revisions,
                cost = Math.Round(p.Cost, 2),
                stages = p.Min.ToDictionary(kv => kv.Key, kv => R(kv.Value)),
            }).ToList(),
            hints,
        };
    }
}
