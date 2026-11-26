using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Bot thông báo qua AMIS Chat: gửi tin vào MỘT nhóm người dùng chọn (dưới tên tài khoản AMIS của họ) khi AI cần xác nhận,
/// khi lô chạy hàng loạt xong, khi job lỗi; và nhận câu trả lời trong nhóm đó ("#&lt;mã task&gt; 1: A, 2: đồng ý" / "ok" / "dừng").
/// Đọc 20 tin gần nhất của nhóm mỗi 15 giây, xử lý các tin mới theo thứ tự; bot xác nhận từng lệnh đã nhận.
/// Cấu hình ở appsettings.Local.json, mục AmisChat.
/// </summary>
public sealed class AmisChatBot(IWebHostEnvironment env, AmisSession session, AmisClient amis, JobStore store, JobRunner runner,
    RepoService repo, SettingsService settings, CostLedger costs, BranchAdvisor branches, DbProfiles dbProfiles, ILogger<AmisChatBot> log)
    : BackgroundService
{
    private const string Base = "https://amisapp.misa.vn/chat/api/business/g2/v1/";
    public const string Prefix = "🤖 ";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(30) };

    // Id tin đã thấy. Id do client sinh nên KHÔNG tăng theo thời gian — không so sánh thứ tự Id được.
    private HashSet<string>? _seen;
    private (string Id, string Name)? _me;
    private readonly Dictionary<string, JobStatus> _prev = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _outbox = new(); // tin nhắn chờ gửi (kết quả việc chạy nền)
    private BranchAdvisor.Offer? _branchOffer; // đề xuất nhánh gần nhất, chờ "đổi nhánh <số>"
    private (string Project, List<DbProfiles.Template> List, DateTime At)? _dbOffer; // danh sách mẫu DB gần nhất, chờ "dùng db <số>"
    private readonly HashSet<string> _activeBatches = [];
    private bool _primed;
    // Nội dung các tin bot vừa gửi — phòng khi AMIS đổi/bỏ emoji đầu tin, bot không tự đọc tin của mình thành lệnh
    private readonly Queue<string> _sent = new();

    public string? LastError { get; private set; }
    public DateTime? LastPoll { get; private set; }

    // ---------- Cấu hình ----------

    public sealed record Config(bool Enabled, string ConversationId, bool NotifyAsk, bool NotifyBatch, bool NotifyDone, bool NotifyFail);

    public Config Cfg()
    {
        JsonObject? s = null;
        try
        {
            var f = LocalConfigFile.PathOf(env);
            s = File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?["AmisChat"] as JsonObject : null;
        }
        catch (JsonException) { }
        bool B(string k, bool d) => s?[k] is { } n ? (bool)n : d;
        return new(B("Enabled", false), (string?)s?["ConversationId"] ?? "", B("NotifyAsk", true), B("NotifyBatch", true), B("NotifyDone", true), B("NotifyFail", true));
    }

    public void Save(Config c) => LocalConfigFile.Update(env, root =>
    {
        var s = LocalConfigFile.Section(root, "AmisChat");
        s["Enabled"] = c.Enabled;
        s["ConversationId"] = c.ConversationId;
        s["NotifyAsk"] = c.NotifyAsk;
        s["NotifyBatch"] = c.NotifyBatch;
        s["NotifyDone"] = c.NotifyDone;
        s["NotifyFail"] = c.NotifyFail;
    });

    /// <summary>Nhận link nhóm (…/chat/cv/&lt;id&gt;) hoặc mã nhóm.</summary>
    public static string ParseConversation(string? input)
    {
        var s = (input ?? "").Trim();
        var m = Regex.Match(s, @"/cv/([0-9a-fA-F]{24})");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(s, "^[0-9a-fA-F]{24}$") ? s : "";
    }

    // ---------- Gọi API AMIS Chat (cookie phiên AMIS) ----------

    private HttpRequestMessage Req(HttpMethod method, string path, string conversationId)
    {
        var sid = session.SessionId;
        if (string.IsNullOrWhiteSpace(sid)) throw new AmisException("Chưa cấu hình session AMIS.", 401);
        // x-tenantid = 32 ký tự hex đầu của session, dạng GUID (đúng như cookie của web AMIS)
        var tid = sid.Length >= 32 ? $"{sid[..8]}-{sid[8..12]}-{sid[12..16]}-{sid[16..20]}-{sid[20..32]}" : "";
        var req = new HttpRequestMessage(method, Base + path);
        req.Headers.TryAddWithoutValidation("Cookie", $"x-sessionid={sid}; x-tenantid={tid}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("devicename", "Web");
        req.Headers.TryAddWithoutValidation("response-type", "json");
        req.Headers.TryAddWithoutValidation("x-client-app-code", "chat");
        req.Headers.TryAddWithoutValidation("Referer", $"https://amisapp.misa.vn/chat/cv/{conversationId}");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SuperAutomation");
        return req;
    }

    private async Task<string> Call(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var res = await _http.SendAsync(req, ct))
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || text.TrimStart().StartsWith('<'))
                throw new AmisException("AMIS Chat từ chối — session AMIS hết hạn hoặc bị Cloudflare chặn. Cập nhật session ở ⚙ Cấu hình › AMIS.", 401);
            if (!res.IsSuccessStatusCode) throw new AmisException($"AMIS Chat trả HTTP {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
            return text;
        }
    }

    /// <summary>Mã tin nhắn kiểu ObjectId (4 byte thời gian + 8 byte ngẫu nhiên) — web AMIS tự sinh khi gửi.</summary>
    private static string NewMessageId()
    {
        var b = new byte[12];
        BitConverter.GetBytes((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Reverse().ToArray().CopyTo(b, 0);
        RandomNumberGenerator.Fill(b.AsSpan(4));
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    public async Task Send(string text, CancellationToken ct, string? conversationId = null, string? replyTo = null)
    {
        var conv = conversationId ?? Cfg().ConversationId;
        if (conv.Length == 0) throw new PipelineException("Chưa chọn nhóm AMIS Chat.");
        _me ??= await amis.WhoAmI(ct);
        // Bot gửi dưới tên chính chủ nên bong bóng giống hệt tin người dùng → thêm dòng tiêu đề và "↪ lệnh" để dễ phân biệt
        var head = Prefix + "SuperAutomation ━━━━━━━━━━";
        if (!string.IsNullOrWhiteSpace(replyTo)) head += "\n↪ trả lời: " + Clip(replyTo.Replace('\n', ' '), 60);
        var content = head + "\n" + (text.Length > 3500 ? text[..3500] + "\n…" : text);
        lock (_sent) { _sent.Enqueue(Norm(content)); while (_sent.Count > 50) _sent.Dequeue(); }
        var req = Req(HttpMethod.Post, "messages/send/text", conv);
        req.Headers.TryAddWithoutValidation("idempotency-key", Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        req.Content = JsonContent.Create(new
        {
            conversationId = conv,
            conversationType = 2,
            isFirstMessage = false,
            isCrossTenant = false,
            messageId = NewMessageId(),
            senderId = _me.Value.Id,
            senderName = _me.Value.Name,
            isHandoffMessage = false,
            url = $"https://amisapp.misa.vn/chat/cv/{conv}",
            fromApp = "amis-chat",
            content,
            originalContent = "",
        });
        await Call(req, ct);
    }

    /// <summary>Tin chữ gần nhất của nhóm, cũ → mới (Id kiểu ObjectId tăng dần theo thời gian).</summary>
    private async Task<List<(string Id, string SenderId, string Content)>> Recent(string conv, CancellationToken ct)
    {
        var text = await Call(Req(HttpMethod.Get, $"messages/conversation/{conv}?Limit=20&ThreadMessageId=&ConversationContextId=&IgnoreJoin=false", conv), ct);
        return (JsonNode.Parse(text) as JsonArray ?? [])
            .Where(m => (int?)m?["Type"] == 1 && m?["IsDelete"]?.GetValue<bool>() != true && (string?)m?["Id"] is not null)
            .Select(m => (At: DateTimeOffset.TryParse((string?)m!["CreatedDate"] ?? (string?)m["SendDate"], out var at) ? at : DateTimeOffset.MinValue,
                          Msg: ((string)m["Id"]!, (string?)m["SenderId"] ?? "", (string?)m["Content"] ?? "")))
            .OrderBy(x => x.At)
            .Select(x => x.Msg)
            .ToList();
    }

    /// <summary>Tên nhóm — để giao diện xác nhận đúng nhóm.</summary>
    public async Task<string> ConversationName(string conv, CancellationToken ct)
    {
        var text = await Call(Req(HttpMethod.Get, $"conversations/{conv}?includeMember=false&includeFirstUnreadMessage=false&memberLimit=1", conv), ct);
        return (string?)JsonNode.Parse(text)?["Name"] ?? "(không tên)";
    }

    // ---------- Vòng nền ----------

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
        while (!stop.IsCancellationRequested)
        {
            var c = Cfg();
            if (c.Enabled && c.ConversationId.Length > 0 && !string.IsNullOrWhiteSpace(session.SessionId))
            {
                try
                {
                    await Notify(c, stop);
                    await ReadReplies(c, stop);
                    LastError = null;
                }
                catch (Exception ex) when (ex is AmisException or HttpRequestException or TaskCanceledException or PipelineException or JsonException)
                {
                    if (stop.IsCancellationRequested) break;
                    if (LastError != ex.Message) log.LogWarning("AMIS Chat: {Msg}", ex.Message);
                    LastError = ex.Message;
                }
                LastPoll = DateTime.Now;
            }
            else { _primed = false; _seen = null; }
            try { await Task.Delay(Tick, stop); } catch (OperationCanceledException) { break; }
        }
    }

    // ---------- Thông báo theo thay đổi trạng thái job ----------

    private static bool Busy(Job j) => j.IsRunning || j.Status == JobStatus.Queued || j.RetryPending;

    private async Task Notify(Config c, CancellationToken ct)
    {
        while (_outbox.TryPeek(out var msg))
        {
            await Send(msg, ct);
            _outbox.TryDequeue(out _);
        }
        var jobs = store.All().ToList();
        if (!_primed)
        {
            // Lần đầu: chỉ ghi nhận trạng thái hiện tại, không gửi lại chuyện cũ
            foreach (var j in jobs) _prev[j.Id] = j.Status;
            foreach (var b in jobs.Where(j => j.Batch && Busy(j)).Select(j => j.BatchId ?? "")) _activeBatches.Add(b);
            _primed = true;
            return;
        }
        foreach (var j in jobs)
        {
            var had = _prev.TryGetValue(j.Id, out var before);
            _prev[j.Id] = j.Status;
            if (j.Batch && Busy(j)) _activeBatches.Add(j.BatchId ?? "");
            if (!had || before == j.Status) continue;
            var dir = store.Dir(j.Id);
            switch (j.Status)
            {
                case JobStatus.NeedsInfo when c.NotifyAsk:
                    await Send($"{Head(j)}\nYêu cầu chưa đủ để code — cần bạn bổ sung:\n\n{Clip(Section(Read(dir, "analysis.md"), "Cần bổ sung"), 2200)}\n\n{Help(j.TaskId)}" +
                        (string.IsNullOrWhiteSpace(j.BaMessage) ? "" : $"\n\n📨 Cần hỏi BA? Tin soạn sẵn, copy gửi một lần:\n{Clip(j.BaMessage, 1500)}"), ct);
                    break;
                case JobStatus.WaitingForUser when c.NotifyAsk:
                    await Send($"{Head(j)}\nAI cần bạn xác nhận trước khi code tiếp:\n\n{Clip(Read(dir, "questions.md"), 2200)}\n\n{Help(j.TaskId)}", ct);
                    break;
                case JobStatus.Failed when c.NotifyFail && !j.RetryPending:
                    await Send($"❌ {Head(j)}\nLỗi: {Clip(j.Error, 500)}\nMở tool để xử lý.", ct);
                    break;
                case JobStatus.AwaitingApproval when c.NotifyDone && !j.Batch:
                    await Send($"✅ {Head(j)}\nAI làm xong — review {j.ReviewVerdict ?? "—"} · test {j.TestVerdict ?? "—"}\n{DiffText(j)}{AssumptionText(dir)}\n{ApproveHelp(j.TaskId)}" +
                        (j.QcFix && !string.IsNullOrWhiteSpace(j.QcReply) ? $"\n\n🐞 Trả lời QC soạn sẵn (dán vào task cha sau khi duyệt):\n{Clip(j.QcReply, 1500)}" : ""), ct);
                    break;
                case JobStatus.Approved when c.NotifyDone && j.AutoApproved:
                    await Send($"🤖✅ Tự duyệt — {Head(j)}\nThay đổi nhỏ, review {j.ReviewVerdict} · test {j.TestVerdict ?? "—"}\n{DiffText(j)}Đã commit vào nhánh {j.Branch} — CHƯA push. Mở tool xem diff, hoặc trả lời: #{j.TaskId} push  — push và tạo pull request.", ct);
                    break;
            }
        }
        // Lô chạy hàng loạt: hết job đang chạy/chờ → gửi bản tin
        foreach (var b in _activeBatches.ToList())
        {
            var batch = jobs.Where(j => j.Batch && (j.BatchId ?? "") == b).ToList();
            if (batch.Any(Busy)) continue;
            _activeBatches.Remove(b);
            if (!c.NotifyBatch || batch.Count == 0) continue;
            var lines = batch.OrderBy(j => j.CreatedAt).Select(j =>
            {
                var asm = Read(store.Dir(j.Id), "assumptions.md");
                var n = asm.Split('\n').Count(l => l.StartsWith("## "));
                return $"• {Line(j, 70)} — {StatusText(j.Status)}" +
                       (j.ReviewVerdict is { } r ? $" · review {r}" : "") + (j.TestVerdict is { } t ? $" · test {t}" : "") +
                       (n > 0 ? $" · ⚠ {n} điểm AI tự quyết" : "");
            });
            var waiting = batch.Count(j => j.Status == JobStatus.AwaitingApproval);
            await Send($"☀ Lô {b} chạy xong: {waiting}/{batch.Count} task chờ bạn duyệt.\n{string.Join("\n", lines)}\n\nMở tool › ☀ Bản tin để review, hoặc trả lời ngay: #<mã> duyệt · #<mã> push · #<mã> sửa <yêu cầu> · #<mã> từ chối <lý do>.", ct);
        }
    }

    private static string Help(long taskId) =>
        $"Trả lời trong nhóm này:\n#{taskId} 1: A, 2: đồng ý  — câu trả lời của bạn\n#{taskId} ok  — làm theo phương án AI đề xuất\n#{taskId} dừng  — huỷ job";

    /// <summary>Lệnh push phải gõ rõ: "push", "duyệt push", "duyệt và push", "tạo pr" — "ok"/"duyệt" không push.</summary>
    private static bool IsPush(string body) =>
        Regex.IsMatch(Fold(body.Trim().Normalize(System.Text.NormalizationForm.FormC)), @"^((duyet|approve|commit|tao commit)\s*(va|\+|&)?\s*)?(push|tao pr|tao pull request)\b");

    /// <summary>"test lại", "review lại", "review + test lại", "retest" — chạy lại review + test trên code đang có, không code lại.</summary>
    private static bool IsRetest(string body) =>
        Regex.IsMatch(Fold(body.Trim().Normalize(System.Text.NormalizationForm.FormC)), @"^((review|rv)\s*(va|\+|&)?\s*)?(test lai|review lai|retest|kiem tra lai)\b");

    /// <summary>"commit", "tạo commit" — commit vào nhánh AI, không push.</summary>
    private static bool IsCommit(string body) =>
        Regex.IsMatch(Fold(body.Trim().Normalize(System.Text.NormalizationForm.FormC)), @"^(tao )?commit\b");

    private static string ApproveHelp(long taskId) =>
        $"Trả lời trong nhóm này:\n#{taskId} duyệt (hoặc commit)  — commit vào nhánh AI (KHÔNG push)\n#{taskId} push  — commit, push nhánh AI và tạo pull request\n#{taskId} test lại  — AI review + test lại (không code lại; thêm \"đầy đủ\" hoặc \"chỉ kiểm dữ liệu\" để đổi mức test)\n#{taskId} sửa <yêu cầu>  — AI sửa thêm rồi review/test lại\n#{taskId} từ chối <lý do>  — AI rút kinh nghiệm cho lần sau";

    /// <summary>Số file/dòng đổi + vài file đầu, để duyệt nhanh trên điện thoại.</summary>
    private static string DiffText(Job j)
    {
        if (j.DiffList.Count == 0) return "";
        var files = string.Join("\n", j.DiffList.Take(8).Select(f => "  • " + f));
        return $"📝 {j.DiffList.Count} file · +{j.DiffAdded} −{j.DiffDeleted} dòng:\n{files}" +
               (j.DiffList.Count > 8 ? $"\n  … và {j.DiffList.Count - 8} file khác" : "") + "\n";
    }

    /// <summary>Các điểm AI tự quyết (assumptions.md) — bạn cần xác nhận khi duyệt.</summary>
    private static string AssumptionText(string dir)
    {
        var asm = Read(dir, "assumptions.md");
        if (asm.Length == 0) return "";
        var n = asm.Split('\n').Count(l => l.StartsWith("## "));
        var body = Regex.Replace(asm, @"(?m)^##\s.*\n?", "").Trim();
        return $"⚠ AI tự quyết {n} điểm — kiểm trước khi duyệt:\n{Clip(body, 700)}\n";
    }

    private static string StatusText(JobStatus s) => s switch
    {
        JobStatus.AwaitingApproval => "chờ duyệt", JobStatus.Approved => "đã duyệt", JobStatus.Failed => "lỗi",
        JobStatus.NeedsInfo => "cần bổ sung", JobStatus.WaitingForUser => "chờ trả lời", JobStatus.Cancelled => "đã huỷ",
        JobStatus.Clarified => "đã rõ yêu cầu", _ => s.ToString(),
    };

    /// <summary>Một dòng cho danh sách: mã task con (để trả lời) + tên task cha.</summary>
    private static string Line(Job j, int max) => string.IsNullOrWhiteSpace(j.ParentTaskName)
        ? $"#{j.TaskId} {Clip(j.TaskName, max)}"
        : $"#{j.TaskId} (task cha #{j.ParentTaskId}: {Clip(j.ParentTaskName, max)})";

    /// <summary>Hai dòng: task cha (tên dễ nhận ra) và task con (mã dùng để trả lời bot).</summary>
    private static string Head(Job j) => string.IsNullOrWhiteSpace(j.ParentTaskName)
        ? $"📌 Task #{j.TaskId}: {j.TaskName}"
        : $"📌 Task cha #{j.ParentTaskId}: {j.ParentTaskName}\n    Task con #{j.TaskId}: {j.TaskName}";

    private static string Read(string dir, string file)
    {
        var p = Path.Combine(dir, file);
        try { return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; } catch (IOException) { return ""; }
    }

    private static string Section(string md, string title)
    {
        var m = Regex.Match(md, $@"(?ms)^##\s*{Regex.Escape(title)}[^\n]*\n(.*?)(?=^##\s|\z)");
        return m.Success ? m.Groups[1].Value.Trim() : md;
    }

    /// <summary>Bỏ thẻ HTML, khoảng trắng, emoji — để so tin AMIS trả về với tin bot đã gửi.</summary>
    private static string Norm(string s) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")), @"[\s\p{So}\p{Cs}]+", "");

    private bool IsOwn(string content)
    {
        var n = Norm(content);
        lock (_sent) return _sent.Any(x => x == n || (n.Length > 20 && x.Contains(n, StringComparison.Ordinal)));
    }

    private static string Clip(string? s, int n) => string.IsNullOrEmpty(s) ? "(trống)" : s.Length <= n ? s : s[..n] + "…";

    // ---------- Nhận lệnh ----------

    private static readonly Regex Cmd = new(@"^\s*#?(\d{5,})\s*[:\-–]?\s*(.*)$", RegexOptions.Singleline);
    private static readonly string[] Yes = ["ok", "oke", "okay", "đồng ý", "dong y", "yes", "tiếp", "tiep", "làm tiếp"];
    private static readonly string[] Stop = ["dừng", "dung", "huỷ", "hủy", "huy", "stop", "cancel"];

    private async Task ReadReplies(Config c, CancellationToken ct)
    {
        var recent = await Recent(c.ConversationId, ct);
        if (recent.Count == 0) return;
        if (_seen is null) { _seen = recent.Select(m => m.Id).ToHashSet(); return; } // lần đầu: bỏ qua tin cũ
        var fresh = recent.Where(m => !_seen.Contains(m.Id)).ToList();
        _seen = recent.Select(m => m.Id).ToHashSet(); // chỉ giữ cửa sổ tin gần nhất
        if (fresh.Count == 0) return;
        _me ??= await amis.WhoAmI(ct);
        foreach (var m in fresh)
        {
            // Chỉ nhận lệnh của chính chủ tài khoản, bỏ qua tin do bot gửi
            if (m.SenderId != _me.Value.Id || m.Content.StartsWith(Prefix.Trim()) || IsOwn(m.Content)) continue;
            var cmd = WebUtility.HtmlDecode(Regex.Replace(m.Content, "<[^>]+>", " ")).Trim();
            var reply = await Handle(cmd, ct);
            if (reply is not null) await Send(reply, ct, replyTo: cmd);
        }
    }

    // ---------- Hỏi lịch task AMIS (chỉ đọc) ----------

    /// <summary>Bỏ dấu, chữ thường — để khớp cả "hôm nay" lẫn "hom nay".</summary>
    private static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(System.Text.NormalizationForm.FormD);
        return new string(d.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static readonly Regex AboutTasks = new(@"\b(task|viec|deadline|dead line|han|lich)\b");
    private static readonly Regex IsQuestion = new(@"\?|\b(gi|nao|khong|ko|k|chua|bao nhieu|ds|danh sach|list|xem)\b|deadline|qua han");

    private static DateTime? Due(AmisTask t) =>
        DateTimeOffset.TryParse(t.EndDate, out var d) ? d.LocalDateTime : null;

    private static string Dow(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? "CN" : "T" + ((int)d.DayOfWeek + 1);

    // ---------- Giao task cho AI từ chat ----------

    private static readonly HashSet<string> StartWords = ["lam", "giao", "chay", "bat", "dau", "code"];
    /// <summary>Từ chọn chế độ test kèm lệnh giao task: "không test", "chỉ kiểm dữ liệu", "test đầy đủ/giao diện".</summary>
    private static readonly HashSet<string> TestModeWords = ["khong", "test", "chi", "kiem", "du", "lieu", "day", "full", "dien", "ui"];

    /// <summary>"off" / "data" / "full" nếu tin nhắn có chọn chế độ test, null nếu không nhắc tới.</summary>
    private static string? ChatTestMode(string text)
    {
        var f = Fold(text);
        if (Regex.IsMatch(f, @"\b(khong|ko|k|bo qua|skip)\s*test\b")) return "off";
        if (Regex.IsMatch(f, @"\b(chi\s*)?(kiem|test)\s*(du lieu|data|db)\b")) return "data";
        if (Regex.IsMatch(f, @"\btest\s*(day du|full|giao dien|ui)\b")) return "full";
        return null;
    }

    private static string TestModeVi(string? m) => m switch { "off" => "không test", "data" => "chỉ kiểm dữ liệu", "full" => "test đầy đủ (giao diện + ảnh)", _ => "" };
    private static readonly HashSet<string> FillerWords =
        ["di", "luon", "nhe", "nha", "cho", "ai", "dem", "qua", "task", "viec", "nay", "giup", "minh", "toi", "em", "voi", "va", "con"];

    /// <summary>
    /// Nhận lệnh giao task khi tin chỉ gồm mã task + từ "làm/giao/chạy…" (không có ý gì khác).
    /// Task đã có job đang mở thì KHÔNG coi là lệnh giao — để "#id làm đi" vẫn là câu trả lời cho AI.
    /// </summary>
    private (List<long> Ids, bool Night, string? Mode)? StartCommand(string text)
    {
        var ids = Regex.Matches(text, @"#?(\d{5,})").Select(x => long.Parse(x.Groups[1].Value)).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > 10) return null;
        var words = Regex.Split(Fold(Regex.Replace(text, @"#?\d{5,}", " ")), @"[^a-z]+").Where(w => w.Length > 0).ToList();
        if (!words.Any(StartWords.Contains) || !words.All(w => StartWords.Contains(w) || FillerWords.Contains(w) || TestModeWords.Contains(w))) return null;
        if (ids.Any(id => store.All().Any(j => j.TaskId == id && !j.IsFinal))) return null;
        return (ids, words.Contains("dem"), ChatTestMode(text));
    }

    private async Task<string> StartTasks(List<long> ids, bool night, CancellationToken ct, string? testMode = null)
    {
        if (store.All().FirstOrDefault(j => j.Held) is { } held)
            return $"Job #{held.TaskId} đang giữ repo (code chưa commit) — vào tool bấm \"Tạo commit\" trước rồi giao tiếp.";
        var batchId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lines = new List<string>();
        var created = 0;
        foreach (var id in ids)
        {
            TaskBundle b;
            try { b = await amis.GetTaskBundle(id, ct); }
            catch (AmisException ex) { lines.Add($"• #{id}: không đọc được trên AMIS — {ex.Message}"); continue; }
            var d = b.Detail;
            var job = store.Create(id, AmisClient.Str(d, "TaskName") ?? "", AmisClient.Str(d, "ProjectName"), AmisClient.Str(d, "EndDate"), null);
            job.Batch = true;
            job.BatchId = batchId;
            job.BatchChain = true;
            job.Unattended = night;
            job.TestMode = testMode;
            store.Log(job, "control", "stage", $"Giao qua AMIS Chat (lô {batchId}, {ids.Count} task){(night ? ", chạy qua đêm" : "")}: " +
                                               "yêu cầu rõ thì tự code, xong tự commit vào nhánh AI riêng (không push).");
            store.Save(job);
            runner.Enqueue(job);
            created++;
            var parent = AmisClient.Str(b.Parent?.Detail, "TaskName");
            lines.Add(parent is null ? $"• #{id}: {Clip(job.TaskName, 90)}" : $"• #{id} (task cha: {Clip(parent, 90)})");
        }
        if (created == 0) return "Chưa giao được task nào:\n" + string.Join("\n", lines);

        string? warn = null;
        var o = settings.Pipeline;
        foreach (var (rel, path) in new[] { (o.RepoRelativePath, o.RepoPath) }.Concat(o.ExtraRepoPaths))
        {
            try
            {
                if ((await repo.Dirty(rel, path, ct)).Count is > 0 and var n)
                { warn = $"⚠ {rel} đang có {n} file sửa chưa commit — bước code sẽ lỗi nếu không dọn trước."; break; }
            }
            catch (PipelineException) { }
        }
        return $"🚀 Đã giao {created} task cho AI{(night ? " (chạy qua đêm: không dừng hỏi, tự chọn phương án đề xuất và ghi giả định)" : "")}" +
               (testMode is null ? "" : $" — {TestModeVi(testMode)}") + ":\n" +
               string.Join("\n", lines) +
               "\nAI phân tích → code → review → test, xong tự commit vào nhánh AI riêng (không push). " +
               (night ? "Xong cả lô bot sẽ nhắn tóm tắt." : "AI cần hỏi gì bot sẽ nhắn vào đây; xong cả lô bot nhắn tóm tắt.") +
               (warn is null ? "" : "\n" + warn);
    }

    /// <summary>Thử câu hỏi lịch task mà không gửi gì vào nhóm (chỉ đọc AMIS).</summary>
    public Task<string?> PreviewTaskQuery(string text, CancellationToken ct) => TaskQuery(text, ct);

    /// <summary>Thử nhận dạng lệnh giao task — chỉ phân tích, không tạo job.</summary>
    public object? PreviewStart(string text) => StartCommand(text) is { } s ? new { s.Ids, s.Night, s.Mode } : null;

    private async Task<string?> TaskQuery(string text, CancellationToken ct)
    {
        var f = Fold(text);
        if (!AboutTasks.IsMatch(f) || !IsQuestion.IsMatch(f)) return null;
        var today = DateTime.Today;
        var overdue = Regex.IsMatch(f, @"qua han|tre han|\btre\b|\bton\b");
        string title;
        List<AmisTask> list;
        int older = 0;
        if (overdue)
        {
            var all = (await amis.GetMyTasks("TotalTaskExpire", ct)).Where(t => Due(t) is not null).OrderByDescending(Due).ToList();
            list = all.Where(t => Due(t) >= today.AddDays(-30)).Take(10).ToList();
            older = all.Count - list.Count;
            title = $"Task quá hạn ({all.Count})";
        }
        else
        {
            var due = await amis.GetMyTasks("TotalTaskDue", ct);
            var soon = await amis.GetMyTasks("TotalTaskDueSoon", ct);
            var both = due.Concat(soon).DistinctBy(t => t.TaskId).Where(t => Due(t) is not null).OrderBy(Due).ToList();
            if (Regex.IsMatch(f, @"\bmai\b"))
            {
                list = both.Where(t => Due(t)!.Value.Date == today.AddDays(1)).ToList();
                title = $"Task đến hạn ngày mai {Dow(today.AddDays(1))} {today.AddDays(1):dd/MM}";
            }
            else if (f.Contains("hom nay") || Regex.IsMatch(f, @"\bnay\b"))
            {
                list = both.Where(t => Due(t)!.Value.Date <= today).ToList();
                title = $"Task đến hạn hôm nay {Dow(today)} {today:dd/MM}";
            }
            else
            {
                list = both;
                title = "Task sắp đến hạn";
            }
        }

        // Tên task cha thay cho "SE thực hiện"
        var parents = new Dictionary<long, string?>();
        foreach (var pid in list.Where(t => t.ParentId is > 0).Select(t => t.ParentId!.Value).Distinct())
            parents[pid] = await amis.GetTaskName(pid, ct);

        var jobs = store.All().ToList();
        var sb = new System.Text.StringBuilder($"📅 {title}:");
        if (list.Count == 0) sb.Append(" không có.");
        foreach (var t in list)
        {
            var d = Due(t)!.Value;
            var when = d.Date == today ? "hôm nay" : d.Date == today.AddDays(1) ? "mai" : $"{Dow(d)} {d:dd/MM}";
            var parent = t.ParentId is { } p && parents.GetValueOrDefault(p) is { } pn ? pn : null;
            sb.Append($"\n• ⏰ {when} — {Clip(parent ?? t.TaskName, 90)}");
            sb.Append(parent is null ? $"\n    #{t.TaskId}" : $"\n    Task con #{t.TaskId}: {Clip(t.TaskName, 40)}");
            if (!string.IsNullOrWhiteSpace(t.ProjectName)) sb.Append($" · {t.ProjectName}");
            if (jobs.Where(j => j.TaskId == t.TaskId).OrderByDescending(j => j.CreatedAt).FirstOrDefault() is { } j)
                sb.Append($" · 🤖 AI: {StatusText(j.Status)}");
        }
        if (older > 0) sb.Append($"\n(+{older} task quá hạn hơn 30 ngày — xem trên AMIS)");
        if (!overdue)
        {
            var exp = (await amis.GetMyTasks("TotalTaskExpire", ct)).Count(t => Due(t) >= today.AddDays(-30));
            if (exp > 0) sb.Append($"\n⚠ Có {exp} task quá hạn trong 30 ngày qua — gõ \"task quá hạn\" để xem.");
        }
        return sb.ToString();
    }

    private string StatusList()
    {
        var open = store.All().Where(j => !j.IsFinal || j.Status == JobStatus.AwaitingApproval).Take(15).ToList();
        return open.Count == 0 ? "Không có job nào đang mở."
            : "Job đang mở:\n" + string.Join("\n", open.Select(j => $"• {Line(j, 60)} — {StatusText(j.Status)}"));
    }

    private string HelpAll() =>
        $"Đang chạy/chờ duyệt: {store.All().Count(j => !j.IsFinal || j.Status == JobStatus.AwaitingApproval)} job.\n" +
        "Lệnh: ? (xem job) · #<mã task> <trả lời> · #<mã task> ok · #<mã task> dừng\n" +
        "Job chờ duyệt: #<mã task> duyệt · #<mã task> push (commit + push + tạo PR) · #<mã task> test lại [đầy đủ / chỉ kiểm dữ liệu] · #<mã task> sửa <yêu cầu> · #<mã task> từ chối <lý do>\n" +
        "Hỏi lịch: hôm nay có task gì · mai có task gì · deadline sắp tới · task quá hạn\n" +
        "DB: db (xem) · db ABC (chọn mẫu VnrConfigTool) · dùng db 1 · lưu db ABC (lưu DB đang dùng)\n" +
        "Nhánh: nhánh <mã dự án> (vd nhánh ABC) hoặc nhánh #<mã task> → đề xuất nhánh gốc BE+FE · đổi nhánh 1 · đổi nhánh BE 1 FE 2\n" +
        "Giao task: #<mã task> làm đi · #<mã task> làm qua đêm · giao <mã 1> <mã 2> · thêm \"không test\" / \"chỉ kiểm dữ liệu\" / \"test đầy đủ\" để chọn mức test";

    // ---------- Đổi nhánh gốc BE/FE theo dự án ----------

    /// <summary>
    /// "nhánh ABC" / "đề xuất nhánh ABC" / "nhánh #4260630" → đề xuất nhánh gốc BE+FE (ghép cùng phiên bản);
    /// "đổi nhánh 1" → đổi cả BE+FE theo phương án 1; "đổi nhánh BE 2" / "đổi nhánh BE 1 FE 2" → chọn riêng từng repo.
    /// Không phải lệnh nhánh thì trả null.
    /// </summary>
    private async Task<string?> BranchCommand(string text, CancellationToken ct)
    {
        var f = Fold(text.Trim().Normalize(System.Text.NormalizationForm.FormC));
        var m = Regex.Match(f, @"^(?:doi|chuyen|chon|de xuat|goi y|xem)?\s*nhanh(?:\s+(?:goc|du an))?\s*(?<rest>.*)$");
        if (!m.Success) return null;
        var rest = m.Groups["rest"].Value.Trim();
        // Chọn phương án
        var pick = Regex.Match(rest, @"^(?:so\s*)?(?<all>\d)$|^(?:(?<repo>be|fe)\s*(?<n>\d)\s*)+$");
        if (pick.Success)
        {
            if (_branchOffer is not { } offer || DateTime.Now - offer.At > TimeSpan.FromMinutes(30))
                return "Chưa có đề xuất nhánh nào (hoặc đã quá 30 phút). Gõ trước: nhánh <mã dự án>, ví dụ: nhánh ABC";
            var picks = offer.Repos.Select(_ => (BranchAdvisor.Candidate?)null).ToList();
            string? Bad(int n) => n < 1 || n > offer.Options.Count ? $"Không có phương án {n} — chỉ có 1 → {offer.Options.Count}." : null;
            if (pick.Groups["all"].Success)
            {
                var n = int.Parse(pick.Groups["all"].Value);
                if (Bad(n) is { } e) return e;
                picks = [.. offer.Options[n - 1].Picks];
            }
            else
                for (var i = 0; i < pick.Groups["repo"].Captures.Count; i++)
                {
                    var label = pick.Groups["repo"].Captures[i].Value.ToUpperInvariant();
                    var n = int.Parse(pick.Groups["n"].Captures[i].Value);
                    if (Bad(n) is { } e) return e;
                    var idx = offer.Repos.FindIndex(r => r.Label == label);
                    if (idx < 0) return $"Không có repo {label}.";
                    picks[idx] = offer.Options[n - 1].Picks[idx];
                    if (picks[idx] is null) return $"Phương án {n} không có nhánh {label}.";
                }
            var res = await branches.Switch(offer, picks, ct);
            if (res.Contains('✔')) _branchOffer = null;
            if (res.Split('\n').FirstOrDefault(l => l.StartsWith("✔ BE")) is { } be)
            {
                var target = be.Split(" → ").ElementAtOrDefault(1)?.Split(',', ' ')[0] ?? "";
                _ = branches.StartBuild(target).ContinueWith(t => _outbox.Enqueue(t.IsCompletedSuccessfully ? t.Result : $"🔨 Build BE lỗi: {t.Exception?.GetBaseException().Message}"), TaskScheduler.Default);
                res += "\n⏳ Đang build lại BE (các project web IIS, kèm thư viện) — lần đầu sau khi đổi phiên bản có thể 10–20 phút. Xong tool báo ở đây; đợi build xong rồi giao task.";
            }
            return res;
        }
        // Đề xuất: theo mã dự án hoặc theo task (lấy dự án của job)
        if (rest.Length == 0) return "Gõ kèm mã dự án hoặc mã task, ví dụ: nhánh ABC · nhánh #4260630";
        if (rest.Length > 40) return null; // câu dài có chữ "nhánh" → không phải lệnh, để luồng khác hiểu
        string project;
        if (Regex.Match(rest, @"^#?(\d{5,})$") is { Success: true } tm)
        {
            var id = long.Parse(tm.Groups[1].Value);
            var pj = store.All().Where(j => j.TaskId == id || j.ParentTaskId == id).OrderByDescending(j => j.CreatedAt).Select(j => j.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (pj is null) return $"Chưa biết dự án của #{id} (task chưa có job trong tool). Gõ mã dự án, ví dụ: nhánh ABC";
            project = pj;
        }
        else project = rest;
        var o = await branches.Suggest(project, ct);
        if (o.Options.Count == 0)
            return $"Không thấy nhánh nào khớp dự án {ProjectMatch.Key(project)} ở {string.Join(", ", o.Repos.Select(r => r.Label))}. " +
                   "Nếu tên nhánh dùng từ khoá khác, khai báo ở tool (dự án ↔ từ khoá nhánh) rồi gõ lại.";
        _branchOffer = o;
        var sb = new System.Text.StringBuilder($"Nhánh gốc cho dự án {ProjectMatch.Key(project)} (đã fetch origin):\n");
        for (var i = 0; i < o.Options.Count; i++)
        {
            var op = o.Options[i];
            sb.Append($"{i + 1}. [{BranchAdvisor.VerText(op.Ver)}] ");
            sb.Append(string.Join(" · ", o.Repos.Select((r, k) => $"{r.Label}: {(op.Picks[k] is { } c ? c.Name + (c.Official ? "" : " (không đúng mẫu nhánh gốc)") : "—")}")));
            if (i == 0) sb.Append("  ← mới nhất");
            sb.Append('\n');
        }
        sb.Append("Đang đứng: ").Append(string.Join(" · ", o.Repos.Select(r => $"{r.Label}: {r.Current}"))).Append('\n');
        sb.Append("Gõ \"đổi nhánh 1\" để đổi cả BE+FE theo phương án 1, hoặc \"đổi nhánh BE 1\" / \"đổi nhánh BE 1 FE 2\" để chọn riêng (hiệu lực 30 phút).");
        return sb.ToString();
    }

    // ---------- Hồ sơ DB theo dự án (mẫu VnrConfigTool) ----------

    /// <summary>
    /// "db" → DB web/AI đang dùng; "db ABC" / "db #4260630" → hồ sơ + các mẫu VnrConfigTool khớp dự án (đánh số);
    /// "dùng db 2" → áp mẫu số 2 và ghi nhớ cho dự án; "lưu db ABC" → lưu DB web đang dùng làm hồ sơ của ABC. Không phải lệnh → null.
    /// </summary>
    private string? DbCommand(string text)
    {
        var f = Fold(text.Trim().Normalize(System.Text.NormalizationForm.FormC));
        var m = Regex.Match(f, @"^(?<verb>dung|ap|luu|xem)?\s*(?:db|database|csdl)\s*(?<rest>.*)$");
        if (!m.Success) return null;
        var verb = m.Groups["verb"].Value;
        var rest = m.Groups["rest"].Value.Trim();
        if (rest.Length > 40) return null;
        string? Project(string r)
        {
            if (Regex.Match(r, @"^#?(\d{5,})$") is { Success: true } tm)
            {
                var id = long.Parse(tm.Groups[1].Value);
                return store.All().Where(j => j.TaskId == id || j.ParentTaskId == id).OrderByDescending(j => j.CreatedAt).Select(j => j.ProjectName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            }
            return r.Length > 0 ? r : null;
        }
        var ai = dbProfiles.AiText();
        if (verb is "dung" or "ap")
        {
            if (!int.TryParse(rest, out var n)) return "Gõ: dùng db <số> (số trong danh sách của lệnh \"db <mã dự án>\").";
            if (_dbOffer is not { } o || DateTime.Now - o.At > TimeSpan.FromMinutes(30)) return "Chưa có danh sách mẫu DB (hoặc đã quá 30 phút). Gõ trước: db <mã dự án>, ví dụ: db ABC";
            if (n < 1 || n > o.List.Count) return $"Không có mẫu số {n} — chỉ có 1 → {o.List.Count}.";
            if (runner.IsBusy) return "AI đang chạy job (có thể đang test trên web) — đợi xong rồi mới đổi DB.";
            if (dbProfiles.Save(o.Project, o.List[n - 1].Name, null, null, null) is { } err) return err;
            _dbOffer = null;
            return $"Hồ sơ DB {DbProfiles.KeyOf(o.Project)} = mẫu {o.List[n - 1].Name}. Lần sau đổi nhánh {DbProfiles.KeyOf(o.Project)} tool tự áp.\n" + dbProfiles.Apply(o.Project);
        }
        if (verb == "luu")
        {
            if (Project(rest) is not { } pj) return "Gõ: lưu db <mã dự án> — lưu DB web đang dùng làm hồ sơ của dự án đó.";
            return dbProfiles.Capture(pj);
        }
        var sb = new System.Text.StringBuilder($"DB web đang dùng: {dbProfiles.CurrentText()}\nDB của AI: {ai}\n");
        if (rest.Length == 0)
        {
            var all = dbProfiles.All();
            sb.Append(all.Count == 0 ? "Chưa có hồ sơ DB nào." : "Hồ sơ: " + string.Join(" · ", all.Select(p => $"{p.Project} = {dbProfiles.ProfileText(p)}")));
            sb.Append("\nGõ \"db <mã dự án>\" để xem/chọn mẫu VnrConfigTool cho dự án.");
            return sb.ToString();
        }
        if (Project(rest) is not { } project) return $"Chưa biết dự án của {rest} (task chưa có job trong tool). Gõ mã dự án, ví dụ: db ABC";
        var key = DbProfiles.KeyOf(project);
        if (dbProfiles.Find(project) is { } prof) sb.Append($"Hồ sơ {key}: {dbProfiles.ProfileText(prof)}\n");
        var list = dbProfiles.TemplatesFor(project);
        var matched = list.Count > 0;
        if (!matched) list = dbProfiles.Templates();
        if (list.Count == 0) return sb.Append($"Không thấy mẫu nào trong {dbProfiles.TemplateDir}.").ToString();
        _dbOffer = (project, list, DateTime.Now);
        sb.Append(matched ? $"Mẫu VnrConfigTool khớp {key}:\n" : $"Không có mẫu nào khớp {key} — tất cả mẫu:\n");
        for (var i = 0; i < list.Count; i++) sb.Append($"{i + 1}. {list[i].Name} — {list[i].Database} trên {list[i].Server}\n");
        sb.Append("Gõ \"dùng db <số>\" để áp cho web + AI và ghi nhớ cho dự án (hiệu lực 30 phút).");
        return sb.ToString();
    }

    private List<Job> Waiting() => store.All().Where(j => j.Status is JobStatus.NeedsInfo or JobStatus.WaitingForUser).ToList();

    private async Task<string?> Handle(string text, CancellationToken ct)
    {
        if (text.Length == 0) return null;
        var lower = text.ToLowerInvariant();
        // 1) Lệnh đúng mẫu: xử lý ngay, không cần AI
        if (lower is "?" or "help" or "trạng thái" or "trang thai" or "status") return StatusList();
        if (await BranchCommand(text, ct) is { } br) return br;
        if (DbCommand(text) is { } dbr) return dbr;
        if (StartCommand(text) is { } start) return await StartTasks(start.Ids, start.Night, ct, start.Mode);
        if (Cmd.Match(text) is { Success: true } m)
        {
            var taskId = long.Parse(m.Groups[1].Value);
            var body = m.Groups[2].Value.Trim();
            var job = store.All().Where(j => j.TaskId == taskId).OrderByDescending(j => j.CreatedAt).FirstOrDefault();
            var gitCmd = IsPush(body) || IsCommit(body) || IsRetest(body);
            // Lệnh commit/push: chọn job còn việc git (đang giữ code / chờ duyệt / đã duyệt chưa có PR), không phải job mới nhất (có thể đã huỷ)
            // Job huỷ/lỗi trước khi tạo nhánh (vd job giao nhầm) không có code → bỏ qua; ưu tiên job còn việc git, rồi tới job mới nhất có nhánh AI
            if (gitCmd && store.All().Where(j => j.TaskId == taskId && j.Branch is not null)
                    .OrderByDescending(j => j.Held || j.Status == JobStatus.AwaitingApproval || (j.Status == JobStatus.Approved && j.PrUrls.Count == 0))
                    .ThenByDescending(j => j.CreatedAt).FirstOrDefault() is { } pending)
                job = pending;
            if (job is null && gitCmd)
            {
                // Gõ nhầm mã task CHA: đúng một task con có job thì dùng job đó
                var kids = store.All().Where(j => j.ParentTaskId == taskId).GroupBy(j => j.TaskId).ToList();
                if (kids.Count == 1) job = kids[0].OrderByDescending(j => j.CreatedAt).First();
                else if (kids.Count > 1) return $"#{taskId} là task cha của {kids.Count} task con: {string.Join(", ", kids.Select(k => "#" + k.Key))} — gõ mã task con, ví dụ #{kids[0].Key} push.";
            }
            // Lệnh commit/push: luôn xử lý tại đây (kể cả job đã duyệt), KHÔNG để AI hiểu thành "giao task mới"
            if (gitCmd) return job is null ? $"Không thấy job nào của #{taskId} trong tool." : await Act(job, body, ct);
            if (job is not null && !job.IsFinal) return await Act(job, body, ct);
            // Task chưa có job đang mở: có thể là "#id giao cho AI giúp mình" viết tự do → để AI hiểu
        }
        else if (await TaskQuery(text, ct) is { } tq) return tq;

        // 2) Câu tự do: nhờ AI hiểu ý
        var it = await Understand(text, ct);
        if (it is null)
        {
            // AI không dùng được: giữ cách cũ — đúng một task đang chờ thì coi là câu trả lời
            var w = Waiting();
            return w.Count == 1 ? await Act(w[0], text, ct) : "Mình chưa hiểu tin này.\n" + HelpAll();
        }
        // Chỉ tin mã task có thật trong tin nhắn (không để AI tự bịa mã)
        var inText = Regex.Matches(text, @"\d{5,}").Select(x => long.Parse(x.Value)).ToHashSet();
        var ids = it.TaskIds.Where(inText.Contains).Distinct().ToList();
        Job? Target()
        {
            foreach (var id in ids)
                if (store.All().Where(j => j.TaskId == id && !j.IsFinal).OrderByDescending(j => j.CreatedAt).FirstOrDefault() is { } j) return j;
            var w = Waiting();
            return ids.Count == 0 && w.Count == 1 ? w[0] : null;
        }
        string NeedTarget() => Waiting() is { Count: > 1 } w
            ? $"Đang có {w.Count} task chờ trả lời — ghi mã task ở đầu, ví dụ: #{w[0].TaskId} {Clip(text, 40)}"
            : "Không thấy task nào đang chờ trả lời.\n" + HelpAll();

        switch (it.Kind)
        {
            case "start":
                if (ids.Count == 0) return "Bạn muốn giao task nào? Ghi mã task, ví dụ: #4256244 làm đi";
                var busy = ids.Where(id => store.All().Any(j => (j.TaskId == id || j.ParentTaskId == id)
                    && (!j.IsFinal || j.Held || (j.Status == JobStatus.Approved && j.PrUrls.Count == 0)))).ToList();
                var todo = ids.Except(busy).ToList();
                var note = busy.Count > 0 ? $"\n(Bỏ qua {string.Join(", ", busy.Select(x => "#" + x))}: đang có job.)" : "";
                return todo.Count == 0 ? "Các task này đều đang có job." + note : await StartTasks(todo, it.Night, ct, ChatTestMode(text)) + note;
            case "schedule":
                return await TaskQuery(it.When switch
                {
                    "today" => "hôm nay có task gì",
                    "tomorrow" => "mai có task gì",
                    "overdue" => "task quá hạn",
                    _ => "deadline sắp tới",
                }, ct);
            case "status":
                return StatusList();
            case "approve":
                return Target() is { } ja ? await Act(ja, "ok", ct) : NeedTarget();
            case "cancel":
                // Huỷ không làm theo suy đoán — bắt gõ lệnh rõ ràng
                return Target() is { } jc ? $"Để huỷ, gõ đúng: #{jc.TaskId} dừng" : NeedTarget();
            case "answer":
                if (Target() is not { } jn) return NeedTarget();
                // Gửi nguyên văn tin của bạn cho AI (bỏ mã task ở đầu nếu có), không dùng bản AI diễn giải
                return await Act(jn, Regex.Replace(text, @"^\s*#?\d{5,}\s*[:\-–]?\s*", ""), ct);
            default:
                return (string.IsNullOrWhiteSpace(it.Reply) ? "Mình chưa hiểu tin này." : it.Reply.Trim()) + "\n\n" + HelpAll();
        }
    }

    /// <summary>Thực hiện lệnh trên một job: dừng / ok / câu trả lời.</summary>
    private async Task<string> Act(Job job, string body, CancellationToken ct)
    {
        if (body.Length == 0) return $"#{job.TaskId}: thiếu nội dung trả lời.\n{Help(job.TaskId)}";
        var b = body.ToLowerInvariant().Trim('.', '!', ' ');
        if (Stop.Contains(b))
            return runner.Cancel(job.Id) ? $"#{job.TaskId}: đã huỷ job." : $"#{job.TaskId}: job đang ở trạng thái {StatusText(job.Status)}, không huỷ được từ đây.";
        if (IsRetest(body))
        {
            // "#id test lại [đầy đủ | chỉ kiểm dữ liệu]": đổi chế độ test của job; test đang tắt mà gõ "test lại" thì test đầy đủ
            var mode = ChatTestMode(body);
            if (mode is null && runner.TestModeFor(job) == "off") mode = "full";
            if (mode is not null && mode != job.TestMode) { job.TestMode = mode; store.Save(job); }
            var rerr = await runner.Proceed(job.Id, "review", ct);
            return rerr is null
                ? $"🔁 #{job.TaskId}: đã giao AI review + test lại trên nhánh {job.Branch} (không code lại{(mode is null ? "" : ", " + TestModeVi(mode))}). Xong sẽ báo bạn — rồi gõ #{job.TaskId} push nếu ổn."
                : $"#{job.TaskId}: {rerr.Message}";
        }
        if (IsCommit(body) && !IsPush(body))
        {
            var cerr = await runner.CommitFromChat(job.Id);
            var jc = store.Get(job.Id) ?? job;
            return cerr is null
                ? $"✅ #{job.TaskId}: đã commit vào nhánh {jc.Branch} (CHƯA push) và trả repo về {jc.ReturnBranch}. Gõ #{job.TaskId} push để push và tạo PR."
                : $"#{job.TaskId}: {cerr.Message}";
        }
        if (IsPush(body))
        {
            var (perr, prs) = await runner.PushFromChat(job.Id);
            var j2 = store.Get(job.Id) ?? job;
            var links = string.Join("\n", prs.Select(u => "🔗 " + u));
            if (perr is not null) return $"#{job.TaskId}: {perr.Message}" + (prs.Count > 0 ? "\n" + links : "");
            return $"🚀 #{job.TaskId}: đã commit & push nhánh {j2.Branch}" +
                   (prs.Count > 0 ? ".\nPull request:\n" + links : runner.AutoPr ? "." : " (tạo PR tự động đang tắt — tạo PR trong tool).") +
                   (j2.TestVerdict == "FAIL" ? "\n⚠ Test của task này là FAIL — xem test.md trước khi merge." : "");
        }
        if (job.Status == JobStatus.AwaitingApproval)
        {
            var raw = body.Trim().Normalize(System.Text.NormalizationForm.FormC);
            var f = Fold(raw);
            // Fold giữ nguyên độ dài chuỗi tiếng Việt nên cắt phần sau lệnh theo vị trí trên chuỗi gốc
            string Rest(Match m) => raw[m.Length..].Trim(' ', ':', '-', '–', '.');
            if (Regex.IsMatch(f, @"^(duyet|approve)\b"))
            {
                var err = await runner.DecideFromChat(job.Id, approve: true, null);
                return err is null
                    ? $"✅ #{job.TaskId}: đã duyệt — code nằm trên nhánh {job.Branch}, CHƯA push. Push trong tool (hoặc git push) khi bạn sẵn sàng."
                    : $"#{job.TaskId}: {err.Message}";
            }
            if (Regex.Match(f, @"^(tu choi|reject)\b") is { Success: true } rj)
            {
                var reason = Rest(rj);
                var err = await runner.DecideFromChat(job.Id, approve: false, reason.Length > 0 ? reason : null);
                return err is null ? $"#{job.TaskId}: đã từ chối{(reason.Length > 0 ? " — đã ghi lý do để AI rút kinh nghiệm" : "")}." : $"#{job.TaskId}: {err.Message}";
            }
            if (Regex.Match(f, @"^(sua lai|sua them|sua|revise)\b") is { Success: true } rv)
            {
                var req = Rest(rv);
                if (req.Length == 0) return $"#{job.TaskId}: ghi rõ cần sửa gì, ví dụ: #{job.TaskId} sửa thêm validate ngày kết thúc ≥ ngày bắt đầu";
                var err = await runner.Proceed(job.Id, "revise", ct, req);
                return err is null ? $"🔧 #{job.TaskId}: đã giao AI sửa thêm — xong sẽ review/test lại và báo bạn." : $"#{job.TaskId}: {err.Message}";
            }
            if (Yes.Contains(b))
                return $"#{job.TaskId} đang chờ duyệt — để chắc chắn, gõ đúng: #{job.TaskId} duyệt\n{ApproveHelp(job.TaskId)}";
        }
        if (Yes.Contains(b))
        {
            if (job.Status is JobStatus.NeedsInfo or JobStatus.Clarified)
            {
                var err = await runner.Proceed(job.Id, "code", ct);
                return err is null ? $"✅ #{job.TaskId}: đã giao code theo phương án AI đề xuất." : $"#{job.TaskId}: {err.Message}";
            }
            body = "Đồng ý các phương án bạn đề xuất — làm tiếp.";
        }
        var e = runner.Answer(job.Id, body);
        return e is null ? $"✅ #{job.TaskId}: đã nhận — AI làm tiếp." : $"#{job.TaskId}: {e.Message} (trạng thái: {StatusText(job.Status)})";
    }

    // ---------- AI hiểu tin nhắn tự do ----------

    public sealed record Intent(string Kind, List<long> TaskIds, bool Night, string? When, string? Reply);

    private static readonly string[] NoTools =
        ["Bash", "PowerShell", "Read", "Edit", "Write", "Glob", "Grep", "WebFetch", "WebSearch", "Agent", "Task", "Skill", "NotebookEdit", "TodoWrite"];

    /// <summary>Thử AI hiểu tin mà không làm gì (để kiểm tra).</summary>
    public Task<Intent?> PreviewIntent(string text, CancellationToken ct) => Understand(text, ct);

    /// <summary>Gọi model nhỏ (không công cụ) để phân loại ý định; lỗi thì trả null để dùng cách cũ.</summary>
    private async Task<Intent?> Understand(string text, CancellationToken ct)
    {
        var O = settings.Pipeline;
        var jobs = store.All().Where(j => !j.IsFinal || j.Status == JobStatus.AwaitingApproval).Take(15).ToList();
        var ctx = new System.Text.StringBuilder();
        foreach (var j in jobs)
        {
            ctx.Append($"- task #{j.TaskId}: {Clip(string.IsNullOrWhiteSpace(j.ParentTaskName) ? j.TaskName : j.ParentTaskName, 100)} — {StatusText(j.Status)}");
            if (j.Status is JobStatus.NeedsInfo or JobStatus.WaitingForUser)
            {
                var dir = store.Dir(j.Id);
                var q = j.Status == JobStatus.WaitingForUser ? Read(dir, "questions.md") : Section(Read(dir, "analysis.md"), "Cần bổ sung");
                ctx.Append($" — ĐANG CHỜ NGƯỜI DÙNG TRẢ LỜI. Câu hỏi của AI: {Clip(q.Replace('\n', ' '), 400)}");
            }
            ctx.Append('\n');
        }
        var prompt =
            $$"""
            Bạn là bộ phân loại tin nhắn cho bot điều khiển công cụ AI tự làm task lập trình. Hôm nay {{DateTime.Today:dd/MM/yyyy}} ({{Dow(DateTime.Today)}}).
            Người dùng nhắn tiếng Việt (có thể không dấu, viết tắt, sai chính tả). Xác định ý định của tin nhắn.

            Các job đang mở trong công cụ:
            {{(ctx.Length == 0 ? "(không có)\n" : ctx.ToString())}}
            Ý định (intent) — chọn đúng một:
            - "start": muốn giao task AMIS cho AI làm (bắt đầu job mới). taskIds = mã task trong tin. night = true nếu muốn chạy qua đêm / không hỏi lại.
            - "answer": trả lời / góp ý / bổ sung thông tin cho câu hỏi của AI ở một job đang chờ. taskIds = mã task nếu có ghi.
            - "approve": đồng ý chung chung với phương án AI đề xuất, không có nội dung gì thêm.
            - "cancel": muốn dừng / huỷ một job.
            - "schedule": hỏi về lịch task trên AMIS, hạn chót, việc phải làm. when = "today" | "tomorrow" | "soon" | "overdue".
            - "status": hỏi tình hình các job AI đang làm.
            - "chat": chào hỏi, hỏi cách dùng, hoặc không thuộc loại nào. reply = câu trả lời ngắn (1-2 câu, tiếng Việt).

            Chỉ in MỘT object JSON trên một dòng, không giải thích, không markdown:
            {"intent":"...","taskIds":[],"night":false,"when":null,"reply":null}

            Tin nhắn:
            <<<{{text}}>>>
            """;
        try
        {
            var dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, O.DataDir, "chatbot"));
            Directory.CreateDirectory(dir);
            var args = ClaudeCli.BaseArgs("default", "haiku", [], NoTools);
            args.AddRange(["--max-turns", "1"]);
            var run = await ClaudeCli.Run(O.ClaudePath, dir, args, prompt, Path.Combine(dir, "intent.jsonl"), _ => { },
                TimeSpan.FromSeconds(90), ct, settings.ClaudeAuth());
            costs.Add(new CostEntry(DateTime.Now, 0, "AMIS Chat bot", null, "chat", "chatbot", run.Model, run.Paid, run.AuthLabel,
                Math.Round(run.CostUsd, 6), run.InTokens, run.OutTokens, run.CacheRead, run.CacheWrite));
            if (run.IsError || run.Result is null) { log.LogWarning("AMIS Chat: AI không hiểu được tin ({Err})", run.Result); return null; }
            var json = Regex.Match(run.Result, @"\{.*\}", RegexOptions.Singleline).Value;
            var n = JsonNode.Parse(json);
            var intent = (string?)n?["intent"];
            if (intent is null) return null;
            var ids = (n?["taskIds"] as JsonArray ?? []).Select(x => long.TryParse(x?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList();
            return new Intent(intent, ids, n?["night"]?.GetValueKind() == JsonValueKind.True, (string?)n?["when"], (string?)n?["reply"]);
        }
        catch (Exception ex) when (ex is JsonException or PipelineException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log.LogWarning("AMIS Chat: AI không hiểu được tin — {Msg}", ex.Message);
            return null;
        }
    }
}
