using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace SuperAutomation.Core;

/// <summary>Phát sự kiện job/log tới mọi trình duyệt đang mở qua Server-Sent Events.</summary>
public sealed class EventHub
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    private readonly ConcurrentDictionary<Guid, Channel<string>> _subs = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        _subs[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subs.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public void Publish(object evt) => PublishRaw(JsonSerializer.Serialize(evt, Json));

    public void PublishRaw(string json)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(json);
    }
}

/// <summary>Job lưu ở data/jobs/{id}/ (job.json, log.jsonl, task.md, plan.md, review.md, test.md…).</summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly EventHub _hub;

    public string Root { get; }

    public JobStore(IOptions<PipelineOptions> opt, IWebHostEnvironment env, EventHub hub, ILogger<JobStore> log)
    {
        _hub = hub;
        Root = Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.DataDir, "jobs"));
        Directory.CreateDirectory(Root);
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, "job.json");
            if (!File.Exists(file)) continue;
            Job? job;
            try { job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file), EventHub.Json); }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Bỏ qua job hỏng {File}", file);
                continue;
            }
            if (job is null) continue;
            if (!job.IsFinal && !job.IsParked)
            {
                job.Status = JobStatus.Failed;
                job.Error = job.Branch is null
                    ? "Bị gián đoạn vì tool khởi động lại (chưa tạo nhánh AI)."
                    : $"Bị gián đoạn vì tool khởi động lại. Code AI vẫn nằm chưa commit trên nhánh {job.Branch} — bấm \"Chạy tiếp: review + test\" hoặc \"Commit & trả nhánh\".";
                // Đang chạy thì repo đang ở nhánh AI với thay đổi chưa commit: giữ nguyên để người dùng quyết
                if (job.Branch is not null) job.Held = true;
                job.FinishedAt ??= DateTime.Now;
                File.WriteAllText(file, JsonSerializer.Serialize(job, EventHub.JsonIndented));
            }
            _jobs[job.Id] = job;
        }
    }

    public string Dir(string id) => Path.Combine(Root, id);

    /// <summary>Thùng rác: job bị xoá được chuyển sang đây (không nạp lại khi khởi động), chép ngược về jobs\ là khôi phục.</summary>
    public string TrashDir => Path.GetFullPath(Path.Combine(Root, "..", "jobs-trash"));

    /// <summary>Xoá job khỏi danh sách: chuyển thư mục job vào thùng rác. Không đụng git (nhánh AI, nếu có, vẫn còn).</summary>
    public bool Delete(string id)
    {
        if (!_jobs.TryRemove(id, out _)) return false;
        Directory.CreateDirectory(TrashDir);
        var src = Dir(id);
        if (Directory.Exists(src))
        {
            var dst = Path.Combine(TrashDir, id);
            if (Directory.Exists(dst)) dst += "-" + DateTime.Now.ToString("HHmmss");
            Directory.Move(src, dst);
            File.WriteAllText(Path.Combine(dst, ".deleted"), DateTime.Now.ToString("o")); // giờ xoá thật (Move giữ nguyên giờ sửa thư mục)
        }
        _hub.Publish(new { kind = "jobRemoved", id });
        return true;
    }

    /// <summary>Các job trong thùng rác (đọc job.json của từng thư mục), mới xoá trước.</summary>
    public List<Job> Trash()
    {
        if (!Directory.Exists(TrashDir)) return [];
        var list = new List<Job>();
        foreach (var dir in Directory.EnumerateDirectories(TrashDir))
        {
            var file = Path.Combine(dir, "job.json");
            if (!File.Exists(file)) continue;
            try
            {
                if (JsonSerializer.Deserialize<Job>(File.ReadAllText(file), EventHub.Json) is { } j)
                {
                    j.Id = Path.GetFileName(dir); // thư mục có thể mang hậu tố khi xoá trùng tên
                    list.Add(j);
                }
            }
            catch (JsonException) { }
        }
        return list.OrderByDescending(j => DeletedAt(j.Id)).ToList();
    }

    public DateTime DeletedAt(string trashId)
    {
        var marker = Path.Combine(TrashDir, trashId, ".deleted");
        return File.Exists(marker) && DateTime.TryParse(File.ReadAllText(marker), out var t) ? t : Directory.GetLastWriteTime(Path.Combine(TrashDir, trashId));
    }

    /// <summary>Khôi phục job từ thùng rác: chuyển thư mục về jobs\ và nạp lại ngay (không cần khởi động lại).</summary>
    public Job? Restore(string trashId)
    {
        var src = Path.Combine(TrashDir, trashId);
        var file = Path.Combine(src, "job.json");
        if (Path.GetFileName(trashId) != trashId || !File.Exists(file)) return null;
        var job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file), EventHub.Json);
        if (job is null) return null;
        if (_jobs.ContainsKey(job.Id) || Directory.Exists(Dir(job.Id)))
            throw new IOException($"Đang có job {job.Id} trong danh sách.");
        Directory.Move(src, Dir(job.Id));
        File.Delete(Path.Combine(Dir(job.Id), ".deleted"));
        _jobs[job.Id] = job;
        _hub.Publish(new { kind = "job", job });
        return job;
    }

    /// <summary>Job dừng/huỷ/lỗi/bị từ chối, không giữ repo — mới được xoá.</summary>
    public static bool Deletable(Job j) => j.Status is JobStatus.Cancelled or JobStatus.Failed or JobStatus.Rejected && !j.Held;

    public IEnumerable<Job> All() => _jobs.Values.OrderByDescending(j => j.CreatedAt);

    public Job? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public Job Create(long taskId, string taskName, string? projectName, string? endDate, string? note)
    {
        var job = new Job
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{taskId}",
            TaskId = taskId,
            TaskName = taskName,
            ProjectName = projectName,
            EndDate = endDate,
            Note = note,
            Status = JobStatus.Queued,
            CreatedAt = DateTime.Now,
        };
        Directory.CreateDirectory(Path.Combine(Dir(job.Id), "inputs"));
        _jobs[job.Id] = job;
        return job;
    }

    public void Save(Job j)
    {
        string json;
        lock (j)
        {
            json = JsonSerializer.Serialize(j, EventHub.JsonIndented);
            File.WriteAllText(Path.Combine(Dir(j.Id), "job.json"), json);
        }
        _hub.Publish(new { kind = "job", job = j });
    }

    public void Log(Job j, string stage, string kind, string text)
    {
        var e = new LogEntry(DateTime.Now, stage, kind, text);
        lock (j)
        {
            File.AppendAllText(Path.Combine(Dir(j.Id), "log.jsonl"), JsonSerializer.Serialize(e, EventHub.Json) + "\n", Encoding.UTF8);
            if (kind is not ("stderr" or "tool-error")) j.LastLog = text.Length > 200 ? text[..200] + "…" : text;
        }
        _hub.Publish(new { kind = "log", jobId = j.Id, entry = e });
    }

    public List<LogEntry> ReadLogs(string id)
    {
        var file = Path.Combine(Dir(id), "log.jsonl");
        if (!File.Exists(file)) return [];
        var list = new List<LogEntry>();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        while (sr.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<LogEntry>(line, EventHub.Json) is { } e) list.Add(e);
            }
            catch (JsonException)
            {
                // Dòng cuối có thể đang được ghi dở khi đọc — bỏ qua dòng đó, lần tải sau sẽ đủ
            }
        }
        return list;
    }
}

public static class Proc
{
    public static async Task<(int Code, string Out, string Err)> Run(string file, string cwd, IEnumerable<string> args, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new PipelineException($"Không chạy được {file}");
        var o = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var e = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(p);
            throw;
        }
        return (p.ExitCode, await o, await e);
    }

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Tiến trình đã thoát giữa lúc kiểm tra HasExited và Kill — không còn gì để dừng
        }
    }
}
