using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SuperAutomation.Core;

public sealed class ChatMessage
{
    /// <summary>user | assistant (chat) | pipeline (câu hỏi/thông báo từ job AI).</summary>
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public List<string> Tools { get; set; } = [];
    public bool Error { get; set; }
    public bool Pending { get; set; }
    /// <summary>Tin nhắn là câu trả lời gửi vào job đang chờ.</summary>
    public string? JobId { get; set; }
    /// <summary>Tệp người dùng đính kèm (tên trong data/chats/{taskId}/files/).</summary>
    public List<string> Files { get; set; } = [];
}

public sealed class ChatThread
{
    public long TaskId { get; set; }
    public string TaskName { get; set; } = "";
    public string? SessionId { get; set; }
    public double CostUsd { get; set; }
    /// <summary>Tổng cộng dồn Claude báo cho phiên SessionId tới lần trả lời gần nhất — phiên --resume báo tổng cả phiên, không phải riêng lần đó.</summary>
    public double SessionCostUsd { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// Hội thoại theo từng task AMIS, lưu ở data/chats/{taskId}/chat.json.
/// Phiên chat là một phiên Claude chỉ đọc, dùng --resume để nhớ ngữ cảnh giữa các tin nhắn.
/// Câu hỏi của pipeline (khi AI cần xác nhận) cũng được ghi vào đây để người dùng trả lời tại chỗ.
/// </summary>
public sealed class ChatService
{
    private readonly ConcurrentDictionary<long, ChatThread> _threads = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly JobStore _store;
    private readonly CostLedger _costs;
    private readonly AmisClient _amis;
    private readonly EventHub _hub;
    private readonly SettingsService _settings;
    private readonly ILogger<ChatService> _log;

    public string Root { get; }

    public ChatService(JobStore store, AmisClient amis, EventHub hub, SettingsService settings, IWebHostEnvironment env, CostLedger costs, ILogger<ChatService> log)
    {
        _costs = costs;
        _store = store;
        _amis = amis;
        _hub = hub;
        _settings = settings;
        _log = log;
        Root = Path.GetFullPath(Path.Combine(env.ContentRootPath, settings.Pipeline.DataDir, "chats"));
        Directory.CreateDirectory(Root);
    }

    private PipelineOptions O => _settings.Pipeline;

    public string Dir(long taskId) => Path.Combine(Root, taskId.ToString());

    public string FilesDir(long taskId) => Path.Combine(Dir(taskId), "files");

    /// <summary>Lưu tệp đính kèm của chat (tên trùng thì thêm -2, -3…), trích nội dung tài liệu Office. Trả về tên đã lưu.</summary>
    public async Task<List<string>> SaveFiles(long taskId, IEnumerable<IFormFile> files, CancellationToken ct)
    {
        var dir = FilesDir(taskId);
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        foreach (var f in files)
        {
            var name = AmisClient.SafeFileName(f.FileName);
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var i = 2; File.Exists(Path.Combine(dir, name)); i++) name = $"{stem}-{i}{ext}";
            await using (var fs = File.Create(Path.Combine(dir, name))) await f.CopyToAsync(fs, ct);
            names.Add(name);
        }
        foreach (var note in DocExtract.ExtractAll(dir, names))
            if (!note.Contains("đã trích")) _log.LogWarning("Chat {TaskId}: {Note}", taskId, note);
        return names;
    }

    /// <summary>Tệp gốc người dùng đã đính kèm trong chat (không gồm bản trích *.md và ảnh tách từ tài liệu).</summary>
    public List<string> AttachedFiles(long taskId)
    {
        var t = Get(taskId);
        lock (t) return t.Messages.SelectMany(m => m.Files).Distinct().Where(n => File.Exists(Path.Combine(FilesDir(taskId), n))).ToList();
    }

    /// <summary>Mô tả tệp đính kèm cho prompt: đường dẫn tệp gốc, kèm bản trích nếu có.</summary>
    public string DescribeFiles(long taskId, IEnumerable<string> names, string dir)
    {
        var sb = new StringBuilder();
        foreach (var n in names)
        {
            var extracted = DocExtract.ExtractedName(n);
            sb.AppendLine(File.Exists(Path.Combine(FilesDir(taskId), extracted))
                ? $"- {Path.Combine(dir, n)} (nội dung đã trích: {Path.Combine(dir, extracted)})"
                : $"- {Path.Combine(dir, n)}");
        }
        return sb.ToString();
    }

    public bool IsBusy(long taskId) => _running.ContainsKey(taskId);

    public ChatThread Get(long taskId) => _threads.GetOrAdd(taskId, id =>
    {
        var file = Path.Combine(Dir(id), "chat.json");
        if (File.Exists(file))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatThread>(File.ReadAllText(file), EventHub.Json) is { } t)
                {
                    // Tin nhắn đang chờ lúc tool tắt sẽ không bao giờ có trả lời
                    foreach (var m in t.Messages.Where(m => m.Pending)) { m.Pending = false; m.Error = true; m.Text += "\n(bị gián đoạn)"; }
                    return t;
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "chat.json hỏng cho task {TaskId}, tạo hội thoại mới", id);
            }
        }
        return new ChatThread { TaskId = id };
    });

    public object Snapshot(long taskId) => new { thread = Get(taskId), busy = IsBusy(taskId) };

    /// <summary>Ghi một tin nhắn không qua Claude (câu hỏi của pipeline, câu trả lời gửi vào job).</summary>
    public void Append(long taskId, string taskName, ChatMessage msg)
    {
        var t = Get(taskId);
        lock (t)
        {
            if (taskName.Length > 0) t.TaskName = taskName;
            t.Messages.Add(msg);
        }
        Save(t);
    }

    /// <summary>Gửi câu hỏi cho trợ lý chat (chỉ đọc). Trả về false nếu đang trả lời tin trước.</summary>
    public bool Ask(long taskId, string taskName, string text, List<string>? files = null, string? model = null)
    {
        var t = Get(taskId);
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(O.ChatTimeoutMinutes));
        if (!_running.TryAdd(taskId, cts))
        {
            cts.Dispose();
            return false;
        }
        var reply = new ChatMessage { Role = "assistant", At = DateTime.Now, Pending = true };
        lock (t)
        {
            if (taskName.Length > 0) t.TaskName = taskName;
            t.Messages.Add(new ChatMessage { Role = "user", Text = text, At = DateTime.Now, Files = files ?? [] });
            t.Messages.Add(reply);
        }
        Save(t);
        _ = Task.Run(() => Process(t, text, files ?? [], reply, cts, model));
        return true;
    }

    public bool Cancel(long taskId)
    {
        if (!_running.TryGetValue(taskId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>Bắt đầu phiên chat mới (AI quên ngữ cảnh cũ), giữ lại lịch sử hiển thị.</summary>
    public bool Reset(long taskId)
    {
        if (IsBusy(taskId)) return false;
        var t = Get(taskId);
        lock (t)
        {
            t.SessionId = null;
            t.Messages.Add(new ChatMessage { Role = "pipeline", Text = "— Bắt đầu phiên chat mới: AI đọc lại task từ AMIS và không nhớ phần trao đổi phía trên. —", At = DateTime.Now });
        }
        Save(t);
        return true;
    }

    /// <summary>Xuất hội thoại thành markdown để bước code của pipeline đọc được các quyết định đã chốt.</summary>
    public string? ExportMarkdown(long taskId)
    {
        var t = Get(taskId);
        List<ChatMessage> msgs;
        lock (t) msgs = t.Messages.Where(m => !m.Pending && !m.Error && (m.Text.Trim().Length > 0 || m.Files.Count > 0)).ToList();
        if (msgs.Count == 0) return null;
        var sb = new StringBuilder($"# Trao đổi giữa người dùng và AI về task AMIS #{taskId}\n\n");
        foreach (var m in msgs)
        {
            var who = m.Role switch { "user" => "Người dùng", "pipeline" => "AI (pipeline)", _ => "AI (chat)" };
            sb.AppendLine($"## {who} — {m.At:dd/MM HH:mm}{(m.JobId is null ? "" : " — trả lời câu hỏi của job")}");
            sb.AppendLine(ClaudeCli.Trunc(m.Text.Trim(), 6000));
            // Tool chép tệp chat sang inputs/ của job trước mỗi lần chạy
            if (m.Files.Count > 0) sb.AppendLine("Tệp đính kèm (đọc hết, kể cả ảnh): " + string.Join(", ", m.Files.Select(f =>
                File.Exists(Path.Combine(FilesDir(taskId), DocExtract.ExtractedName(f))) ? $"inputs/{f} (bản trích inputs/{DocExtract.ExtractedName(f)})" : $"inputs/{f}")));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>D:\a\b → //d/a/b/** (dạng đường dẫn tuyệt đối mà luật quyền của Claude CLI nhận trên Windows).</summary>
    private static string PathRule(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        if (full.Length > 1 && full[1] == ':') full = char.ToLowerInvariant(full[0]) + full[2..];
        return "//" + full.TrimStart('/') + "/**";
    }

    private async Task Process(ChatThread t, string text, List<string> files, ChatMessage reply, CancellationTokenSource cts, string? model = null)
    {
        var ct = cts.Token;
        try
        {
            var dir = Dir(t.TaskId);
            Directory.CreateDirectory(dir);
            var fresh = t.SessionId is null;
            if (fresh || !File.Exists(Path.Combine(dir, "task.md")))
            {
                var bundle = await _amis.GetTaskBundle(t.TaskId, ct);
                t.TaskName = AmisClient.Str(bundle.Detail, "TaskName") ?? t.TaskName;
                TaskBrief.Write(bundle, dir, null, []);
            }

            var job = _store.All().FirstOrDefault(j => j.TaskId == t.TaskId);
            var prompt = (fresh ? Prompts.ChatIntro(t, dir, job, _store, O) : Prompts.ChatContext(job, _store, O))
                         + "\n\nTin nhắn của người dùng:\n" + (text.Length > 0 ? text : "(không có lời nhắn — xem tệp đính kèm)")
                         + (files.Count > 0 ? "\n\nNgười dùng đính kèm các tệp sau, hãy đọc hết (kể cả ảnh) trước khi trả lời:\n" + DescribeFiles(t.TaskId, files, FilesDir(t.TaskId)) : "");

            List<string> allowed = [.. O.ChatAllowedTools], disallowed = [.. O.ChatDisallowedTools];
            string? outDir = null;
            if (O.ChatCanWriteFiles)
            {
                // Chỉ được ghi trong outputs của task; Edit(path) áp cho mọi tool ghi file (Write/Edit).
                outDir = Path.Combine(job is not null ? _store.Dir(job.Id) : dir, "outputs");
                Directory.CreateDirectory(outDir);
                allowed.Add($"Edit({PathRule(outDir)})");
                allowed.AddRange(["Bash(python:*)", "Bash(py:*)", "Bash(node:*)", "Bash(mkdir:*)", "Bash(ls:*)",
                                  "PowerShell(python *)", "PowerShell(py *)", "PowerShell(node *)"]);
                disallowed.RemoveAll(x => x is "Edit" or "Write");
                prompt += $"\n\nĐƯỢC PHÉP TẠO FILE khi người dùng yêu cầu (docx, xlsx, md, ảnh…), nhưng CHỈ trong thư mục {outDir} — "
                        + "ghi chỗ khác sẽ bị từ chối. Không sửa source code, không ghi vào repo. Python có sẵn python-docx/openpyxl để sinh docx/xlsx; "
                        + "script tạm cũng đặt trong thư mục đó. Xong thì báo đường dẫn đầy đủ của file đã tạo.";
            }
            var args = ClaudeCli.BaseArgs("default", model ?? O.Model, allowed, disallowed);
            args.AddRange(["--add-dir", dir]);
            if (job is not null) args.AddRange(["--add-dir", _store.Dir(job.Id)]);
            if (!fresh) args.AddRange(["--resume", t.SessionId!]);
            var cap = _settings.CostCap(O.MaxChatCostUsd);
            if (cap is { } c) args.AddRange(["--max-budget-usd", c.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

            var stream = new StringBuilder();
            var run = await ClaudeCli.Run(O.ClaudePath, O.WorkspaceRoot, args, prompt,
                Path.Combine(dir, $"chat-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"),
                e =>
                {
                    lock (t)
                    {
                        if (e.Kind == "ai")
                        {
                            if (stream.Length > 0) stream.Append("\n\n");
                            stream.Append(e.Text);
                            reply.Text = stream.ToString();
                        }
                        else if (e.Kind == "tool") reply.Tools.Add(e.Text);
                        else return;
                    }
                    Publish(t);
                },
                TimeSpan.FromMinutes(O.ChatTimeoutMinutes), ct, _settings.ClaudeAuth());

            lock (t)
            {
                // Phiên resume: total_cost_usd là tổng cả phiên → chỉ cộng phần tăng thêm
                var sameSession = !fresh && run.SessionId is not null && run.SessionId == t.SessionId;
                var spent = sameSession ? Math.Max(0, run.CostUsd - t.SessionCostUsd) : run.CostUsd;
                t.CostUsd += spent;
                _costs.Add(new CostEntry(DateTime.Now, t.TaskId, t.TaskName, null, "chat", "chat", run.Model, run.Paid, run.AuthLabel,
                    Math.Round(spent, 6), run.InTokens, run.OutTokens, run.CacheRead, run.CacheWrite));
                if (run.SessionId is not null) { t.SessionId = run.SessionId; t.SessionCostUsd = run.CostUsd; }
                if (run.TimedOut) { reply.Error = true; reply.Text = $"Quá {O.ChatTimeoutMinutes} phút chưa trả lời xong, đã dừng."; }
                else if (run.Subtype == "error_max_budget_usd")
                {
                    reply.Error = true;
                    reply.Text = (reply.Text + $"\n\n(Dừng: chạm giới hạn chi phí ${cap:0.00}/tin nhắn — chỉnh ở ⚙ Cấu hình nếu cần.)").Trim();
                }
                else if (run.Result is null || run.IsError)
                {
                    reply.Error = true;
                    reply.Text = $"Claude báo lỗi ({run.Subtype ?? $"exit {run.ExitCode}"}): {run.Result}".Trim();
                    // Phiên cũ không resume được (bị xoá/hết hạn) — lần sau bắt đầu phiên mới
                    if (!fresh) t.SessionId = null;
                }
                else reply.Text = run.Result.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            lock (t) { reply.Error = true; reply.Text = (reply.Text + "\n(đã dừng)").Trim(); }
        }
        catch (Exception ex) when (ex is AmisException or PipelineException or HttpRequestException)
        {
            lock (t) { reply.Error = true; reply.Text = ex.Message; }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat task {TaskId} lỗi", t.TaskId);
            lock (t) { reply.Error = true; reply.Text = ex.Message; }
        }
        finally
        {
            reply.Pending = false;
            _running.TryRemove(t.TaskId, out _);
            cts.Dispose();
            Save(t);
        }
    }

    private void Save(ChatThread t)
    {
        string json;
        lock (t) json = JsonSerializer.Serialize(t, EventHub.JsonIndented);
        Directory.CreateDirectory(Dir(t.TaskId));
        File.WriteAllText(Path.Combine(Dir(t.TaskId), "chat.json"), json);
        Publish(t);
    }

    private void Publish(ChatThread t)
    {
        string json;
        lock (t) json = JsonSerializer.Serialize(new { kind = "chat", taskId = t.TaskId, busy = IsBusy(t.TaskId), thread = t }, EventHub.Json);
        _hub.PublishRaw(json);
    }
}
