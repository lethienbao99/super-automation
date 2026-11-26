using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SuperAutomation.Core;

public sealed class AmisException(string message, int status = 502) : Exception(message)
{
    public int Status => status;
}

/// <summary>Giữ x-sessionid hiện hành; cập nhật từ UI được ghi lại vào appsettings.Local.json.</summary>
public sealed class AmisSession(IOptions<AmisOptions> opt, IWebHostEnvironment env)
{
    private string _sid = opt.Value.SessionId?.Trim() ?? "";

    public string SessionId => Volatile.Read(ref _sid);

    public void Update(string sid)
    {
        sid = sid.Trim();
        Volatile.Write(ref _sid, sid);
        LocalConfigFile.Update(env, root => LocalConfigFile.Section(root, "Amis")["SessionId"] = sid);
    }
}

/// <summary>Task, bình luận, tệp đính kèm; Parent là task cha (công việc con thường để yêu cầu và tài liệu ở task cha).</summary>
/// <summary>Một dòng trong danh sách "Việc của tôi".</summary>
public sealed record AmisTask(long TaskId, string? TaskName, string? EndDate, string? ProjectName, long? ProjectId, string? Status, long? ParentId, bool IsParent, string? ParentName = null);

/// <summary>Một tệp đính kèm AMIS (của task, task cha hoặc bình luận) có thể tải về.</summary>
public sealed record AmisFile(string AttachmentId, string FileName, long? Size, string Source);

public sealed record TaskBundle(JsonObject Detail, JsonArray Comments, JsonArray Attachments, TaskBundle? Parent = null);

/// <summary>
/// Gọi API nội bộ của webapp AMIS Công việc bằng header x-sessionid (cùng cách webapp dùng).
/// Mọi response có dạng { Success, UserMessage, Data }.
/// </summary>
public sealed class AmisClient(AmisSession session, IOptions<AmisOptions> opt)
{
    private static readonly JsonSerializerOptions RawJson = new(); // giữ nguyên tên thuộc tính (TaskID, PageIndex…)

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<object> GetMe(CancellationToken ct)
    {
        var d = await Send(HttpMethod.Get, "User/user-info-v2", null, ct)
                ?? throw new AmisException("AMIS không trả thông tin user — session có thể đã hết hạn.", 401);
        return new
        {
            userId = Str(d, "UserID"),
            fullName = Str(d, "FullName"),
            userName = Str(d, "UserName"),
            email = Str(d, "MISAIDEmail") ?? Str(d, "ContactEmail"),
            tenantName = Str(d, "TenantName"),
            jobPosition = Str(d, "JobPositionName"),
            organizationUnit = Str(d, "OrganizationUnitName"),
        };
    }

    private (string Id, string Name)? _who;

    /// <summary>UserID và tên của tài khoản đang đăng nhập (nhớ lại cho các lần sau).</summary>
    public async Task<(string Id, string Name)> WhoAmI(CancellationToken ct)
    {
        if (_who is { } w) return w;
        var d = await Send(HttpMethod.Get, "User/user-info-v2", null, ct)
                ?? throw new AmisException("AMIS không trả thông tin user — session có thể đã hết hạn.", 401);
        var r = (Str(d, "UserID") ?? throw new AmisException("Không đọc được UserID từ AMIS.", 401), Str(d, "FullName") ?? "");
        _who = r;
        return r;
    }

    // ---------- Ghi lên AMIS (chỉ khi người dùng bấm gửi) ----------

    /// <summary>Đăng bình luận lên task, dưới tên người đang đăng nhập (giống ô bình luận trên web AMIS).</summary>
    public async Task PostComment(long taskId, string html, CancellationToken ct)
    {
        var me = await Send(HttpMethod.Get, "User/user-info-v2", null, ct)
                 ?? throw new AmisException("AMIS không trả thông tin user — session có thể đã hết hạn.", 401);
        await Send(HttpMethod.Post, "TaskComment/save-async", new
        {
            IsPinned = false,
            TaskID = taskId,
            UserID = Str(me, "UserID"),
            FullName = Str(me, "FullName"),
            State = 1,
            CommentContent = html,
            ListTagUsers = "",
        }, ct);
    }

    /// <summary>File đã tải lên kho AMIS (chưa gắn vào task).</summary>
    public sealed record UploadedFile(string FileId, string FileName, string FileType, long FileSize);

    /// <summary>
    /// Tải nội dung một file lên kho AMIS như ô đính kèm của web (POST appupload/50, form-data trường "files").
    /// Trả FileID dạng "guid.đuôi". Định dạng AMIS không nhận → AmisException (người gọi có thể nén zip rồi thử lại).
    /// </summary>
    public async Task<UploadedFile> UploadFile(string fileName, byte[] bytes, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MimeOf(fileName));
        form.Add(part, "files", fileName);
        var data = await SendContent(HttpMethod.Post, "appupload/50", form, ct);
        // Response chưa thấy mẫu: nhận cả mảng lẫn object, tìm FileID / AttachmentID / chuỗi "guid.đuôi"
        var node = data is JsonArray a ? a.FirstOrDefault() : data;
        static bool IsFileId(string? s) => s is not null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9a-fA-F-]{36}\.\w+$");
        var id = Str(node, "FileID") ?? Str(node, "AttachmentID");
        if (id is null && IsFileId(Str(node, "FileName"))) id = Str(node, "FileName");
        if (id is null && node is JsonValue v && IsFileId(v.ToString())) id = v.ToString();
        if (id is null) throw new AmisException($"AMIS nhận file {fileName} nhưng không trả FileID: {Trunc(data?.ToJsonString() ?? "(rỗng)", 300)}");
        return new UploadedFile(id, fileName, Path.GetExtension(fileName).ToLowerInvariant(), bytes.LongLength);
    }

    /// <summary>
    /// Gắn các file đã tải lên vào task: lưu thông tin file (save-list-attachemnt), rồi ghi lại TOÀN BỘ danh sách đính kèm của task
    /// (update-field ghi đè cả danh sách) = danh sách hiện có + file mới — không làm mất file người khác đã đính kèm.
    /// </summary>
    public async Task AttachToTask(long taskId, IReadOnlyList<UploadedFile> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var me = await Send(HttpMethod.Get, "User/user-info-v2", null, ct)
                 ?? throw new AmisException("AMIS không trả thông tin user — session có thể đã hết hạn.", 401);
        var (userId, fullName, email) = (Str(me, "UserID"), Str(me, "FullName"), Str(me, "MISAIDEmail") ?? Str(me, "ContactEmail"));
        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
        const string folder = "Thư mục chung";

        await Send(HttpMethod.Post, "attachment/save-list-attachemnt/false/false", files.Select(f => new
        {
            FileID = f.FileId, f.FileName, FileType = f.FileType, FileSize = f.FileSize, StorageType = 0,
            Uploader = "00000000-0000-0000-0000-000000000000", UploadDate = now, LastDownloadDate = (string?)null, ApplicationCode = (string?)null,
            EnumTypeFile = 1, MasterID = taskId, AttachmentID = f.FileId, FullName = fullName, Email = email, UserID = userId,
            FolderID = (string?)null, FolderName = folder, State = 1, MasterType = 0, ID = Path.GetFileNameWithoutExtension(f.FileId),
        }).ToList(), ct);

        // Danh sách hiện có lấy từ chi tiết task (đúng định dạng update-field cần), đọc NGAY trước khi ghi để không đè file vừa được thêm
        var detail = await Send(HttpMethod.Get, $"Task/getTaskByIDAsync/{taskId}", null, ct) as JsonObject
                     ?? throw new AmisException($"Không đọc được task {taskId}.", 404);
        var list = ParseAttachments(Str(detail, "Attachments"));
        foreach (var f in files.Where(f => !list.Any(x => Str(x, "AttachmentID") == f.FileId)))
            list.Add(new JsonObject
            {
                ["AttachmentID"] = f.FileId, ["FileName"] = f.FileName, ["FullName"] = fullName, ["Email"] = email, ["UploadDate"] = now,
                ["UserID"] = userId, ["TaskID"] = taskId, ["FileSize"] = f.FileSize, ["SyncID"] = Guid.NewGuid().ToString(),
                ["MasterType"] = 0, ["FolderID"] = null, ["FolderName"] = folder,
            });
        await Send(HttpMethod.Post, "Task/update-field", new
        {
            ModelName = "Task", FieldKey = "TaskID", ValueKey = taskId,
            FieldNameAndValue = new Dictionary<string, object?> { ["Attachments"] = list.ToJsonString() },
        }, ct);
    }

    /// <summary>Tên + dung lượng các file đang đính kèm trên task (để bỏ qua file đã gửi).</summary>
    public async Task<List<(string Name, long Size)>> TaskAttachmentNames(long taskId, CancellationToken ct)
    {
        var detail = await Send(HttpMethod.Get, $"Task/getTaskByIDAsync/{taskId}", null, ct) as JsonObject
                     ?? throw new AmisException($"Không đọc được task {taskId}.", 404);
        return ParseAttachments(Str(detail, "Attachments"))
            .Select(x => (Str(x, "FileName") ?? "", long.TryParse(Str(x, "FileSize"), out var n) ? n : -1)).ToList();
    }

    private static JsonArray ParseAttachments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try { return JsonNode.Parse(raw) as JsonArray ?? []; }
        catch (JsonException) { throw new AmisException("Danh sách đính kèm của task trên AMIS không đọc được — dừng để tránh ghi đè mất file."); }
    }

    private static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".pdf" => "application/pdf", ".zip" => "application/zip", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".txt" => "text/plain", ".md" => "text/markdown", ".sql" => "text/plain", ".json" => "application/json",
        _ => "application/octet-stream",
    };

    /// <summary>Trạng thái task trên AMIS: 3 = Hoàn thành (theo request của web khi bấm hoàn thành task).</summary>
    public const int StatusDone = 3;

    public Task SetStatus(long taskId, int status, CancellationToken ct) =>
        Send(HttpMethod.Post, "Task/updateStatusAsync", new
        {
            TaskID = taskId,
            Status = status,
            NotApplyAutomation = true,
            TrackingID = $"\"{Guid.NewGuid()}\"", // web gửi GUID bọc trong dấu nháy
        }, ct);

    /// <summary>Văn bản thuần → HTML của ô bình luận AMIS: mỗi dòng một &lt;p&gt;.</summary>
    public static string ToCommentHtml(string text) =>
        string.Concat(text.Replace("\r\n", "\n").Split('\n').Select(l =>
            l.Trim().Length == 0 ? "<p><br></p>" : "<p>" + System.Net.WebUtility.HtmlEncode(l) + "</p>"));

    /// <summary>
    /// Danh sách "Việc của tôi" theo nhóm hạn, giống widget dashboard của AMIS.
    /// type: TotalTaskDue (đến hạn hôm nay) | TotalTaskDueSoon (sắp đến hạn) | TotalTaskExpire (quá hạn).
    /// </summary>
    public async Task<List<AmisTask>> GetMyTasks(string type, CancellationToken ct)
    {
        const int pageSize = 100;
        var seen = new HashSet<long>();
        var result = new List<AmisTask>();
        for (var page = 0; page < 20; page++)
        {
            var body = new
            {
                pageIndex = page,
                pageSize,
                typeReport = "ListTaskToday",
                typeTask = type,
                fromDate = (DateTime?)null,
                toDate = (DateTime?)null,
                recentDay = 8,
            };
            if (await Send(HttpMethod.Post, "Dashboard/getTaskPagingReportMyTask", body, ct) is not JsonArray arr || arr.Count == 0)
                break;
            foreach (var t in arr)
            {
                var id = Long(t, "TaskID");
                if (id is null || !seen.Add(id.Value)) continue;
                result.Add(new AmisTask(id.Value, Str(t, "TaskName"), Str(t, "EndDate"), Str(t, "ProjectName"),
                    Long(t, "ProjectID"), Str(t, "Status"), Long(t, "ParentID"), Str(t, "IsParent") == "true"));
            }
            // Webapp trả tới pageSize+1 dòng để báo còn trang sau
            if (arr.Count < pageSize) break;
        }
        return result;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _names = new();

    /// <summary>Tên task (chỉ đọc chi tiết, nhớ lại) — dùng để hiện tên task cha thay cho "SE thực hiện".</summary>
    public async Task<string?> GetTaskName(long taskId, CancellationToken ct)
    {
        if (_names.TryGetValue(taskId, out var n)) return n;
        try
        {
            var name = Str(await Send(HttpMethod.Get, $"Task/getTaskByIDAsync/{taskId}", null, ct), "TaskName");
            if (name is not null) _names[taskId] = name;
            return name;
        }
        catch (AmisException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<TaskBundle> GetTaskBundle(long taskId, CancellationToken ct)
    {
        var b = await GetSingle(taskId, ct);
        var parentId = Long(b.Detail, "ParentID");
        if (parentId is null or <= 0 || parentId == taskId) return b;
        try
        {
            return b with { Parent = await GetSingle(parentId.Value, ct) };
        }
        catch (AmisException ex) when (ex.Status == 404)
        {
            // Không có quyền xem task cha: vẫn làm việc với task con
            return b;
        }
    }

    private async Task<TaskBundle> GetSingle(long taskId, CancellationToken ct)
    {
        var detailT = Send(HttpMethod.Get, $"Task/getTaskByIDAsync/{taskId}", null, ct);
        var commentsT = Send(HttpMethod.Post, "TaskComment/getTaskCommentPaging",
            new { TaskID = taskId, PageIndex = 1, PageSize = 200 }, ct);
        var attachT = Send(HttpMethod.Get, $"attachment/getAttachmentByTaskID/{taskId}", null, ct);
        await Task.WhenAll(detailT, commentsT, attachT);

        var detail = await detailT as JsonObject
                     ?? throw new AmisException($"Không đọc được task {taskId} (không tồn tại hoặc không có quyền).", 404);
        var comments = (await commentsT as JsonObject)?["PageData"] as JsonArray ?? [];
        var attachments = await attachT as JsonArray ?? [];
        // Tách node khỏi cây cha để có thể gắn vào JSON khác
        return new TaskBundle(detail, (JsonArray)comments.DeepClone(), (JsonArray)attachments.DeepClone());
    }

    /// <summary>Dữ liệu rút gọn cho popup xem trước khi giao task cho AI.</summary>
    public static object ToPreview(TaskBundle b)
    {
        var d = b.Detail;
        var p = b.Parent;
        return new
        {
            taskId = Long(d, "TaskID"),
            taskName = Str(d, "TaskName"),
            description = Str(d, "Description"),
            endDate = Str(d, "EndDate"),
            projectName = Str(d, "ProjectName"),
            kanbanName = Str(d, "KanbanName"),
            ownerName = Str(d, "OwnerName"),
            assignerName = Str(d, "AssignerName"),
            assigneeName = Str(d, "AssigneeName"),
            comments = UserComments(b.Comments).Select(c => new
            {
                fullName = Str(c, "FullName"),
                logTime = Str(c, "LogTime"),
                content = Str(c, "CommentContent"),
                files = CommentFileNames(c),
            }),
            attachments = b.Attachments.Select(a => (a, false))
                .Concat((p?.Attachments ?? []).Select(a => (a, true)))
                .Select(x => new
                {
                    attachmentId = Str(x.a, "AttachmentID"),
                    fileName = Str(x.a, "FileName"),
                    fileType = Str(x.a, "FileType"),
                    fileSize = Long(x.a, "FileSize"),
                    fromParent = x.Item2,
                }),
            parent = p is null ? null : new
            {
                taskId = Long(p.Detail, "TaskID"),
                taskName = Str(p.Detail, "TaskName"),
                description = Str(p.Detail, "Description"),
                commentCount = UserComments(p.Comments).Count(),
            },
        };
    }

    /// <summary>Mọi tệp đính kèm tải được: của task, bình luận, rồi của task cha và bình luận task cha.</summary>
    public static List<AmisFile> Files(TaskBundle b)
    {
        var list = new List<AmisFile>();
        void Add(TaskBundle x, string who)
        {
            foreach (var a in x.Attachments)
                if (Str(a, "AttachmentID") is { } id)
                    list.Add(new AmisFile(id, Str(a, "FileName") ?? id, Long(a, "FileSize"), who));
            foreach (var c in UserComments(x.Comments))
            {
                if (Str(c, "Attachments") is not { } raw || string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    foreach (var a in JsonNode.Parse(raw) as JsonArray ?? [])
                        if (Str(a, "AttachmentID") is { } id)
                            list.Add(new AmisFile(id, Str(a, "FileName") ?? id, Long(a, "FileSize"), $"bình luận {who} của {Str(c, "FullName")}"));
                }
                catch (JsonException) { /* danh sách tệp của bình luận hỏng: bỏ qua, task.md vẫn ghi tên tệp */ }
            }
        }
        Add(b, "task");
        if (b.Parent is not null) Add(b.Parent, "task cha");
        return list.DistinctBy(f => f.AttachmentId).ToList();
    }

    /// <summary>
    /// Tải nội dung một tệp đính kèm như webapp: lấy token qua DownloadTMS/{StorageType}/{AttachmentID}
    /// rồi GET download/{token}. StorageType của tệp công việc là 50 (enum TASK của dịch vụ lưu trữ).
    /// </summary>
    public async Task<byte[]> Download(string attachmentId, CancellationToken ct)
    {
        var token = (await Send(HttpMethod.Get, $"DownloadTMS/50/{Uri.EscapeDataString(attachmentId)}", null, ct))?.ToString();
        if (string.IsNullOrWhiteSpace(token)) throw new AmisException($"AMIS không cấp token tải cho tệp {attachmentId}.");

        using var req = new HttpRequestMessage(HttpMethod.Get, opt.Value.BaseUrl.TrimEnd('/') + "/download/" + token);
        req.Headers.Add("x-sessionid", session.SessionId);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        var isJson = res.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
        if (!res.IsSuccessStatusCode || isJson)
            throw new AmisException($"AMIS không trả nội dung tệp {attachmentId} (HTTP {(int)res.StatusCode}: {Trunc(System.Text.Encoding.UTF8.GetString(bytes), 200)}).");
        return bytes;
    }

    public static string SafeFileName(string name)
    {
        var n = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? $"file-{Guid.NewGuid():N}" : n;
    }

    public static IEnumerable<JsonNode> UserComments(JsonArray comments) =>
        comments.Where(c => c is not null && Str(c, "IsTaskComment") == "1"
                            && (!string.IsNullOrWhiteSpace(HtmlText.ToText(Str(c, "CommentContent"))) || CommentFileNames(c).Count > 0))!;

    public static List<string> CommentFileNames(JsonNode c)
    {
        var raw = Str(c, "Attachments");
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return (JsonNode.Parse(raw) as JsonArray ?? [])
                .Select(a => Str(a, "FileName") ?? Str(a, "AttachmentID") ?? "")
                .Where(n => n.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return ["(không đọc được danh sách tệp của bình luận)"];
        }
    }

    public static string? Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v ? v.ToString() : null;

    public static long? Long(JsonNode? n, string key) =>
        long.TryParse(Str(n, key), out var x) ? x : null;

    private Task<JsonNode?> Send(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendContent(method, path, body is null ? null : JsonContent.Create(body, options: RawJson), ct);

    private async Task<JsonNode?> SendContent(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var sid = session.SessionId;
        if (string.IsNullOrWhiteSpace(sid))
            throw new AmisException("Chưa cấu hình session AMIS (x-sessionid).", 401);

        using var req = new HttpRequestMessage(method, opt.Value.BaseUrl.TrimEnd('/') + "/" + path);
        req.Headers.Add("x-sessionid", sid);
        req.Headers.Accept.ParseAdd("application/json");
        if (content is not null) req.Content = content;

        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AmisException("Session AMIS hết hạn hoặc không hợp lệ. Cập nhật lại x-sessionid.", 401);
        if (!res.IsSuccessStatusCode)
            throw new AmisException($"AMIS {path} trả HTTP {(int)res.StatusCode}: {Trunc(text, 300)}");

        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException)
        {
            // Hết phiên thì webapp trả trang HTML chuyển hướng về /login thay vì JSON
            throw new AmisException($"AMIS {path} không trả JSON — session có thể đã hết hạn.", 401);
        }
        if (root is not JsonObject obj || obj["Success"]?.GetValue<bool>() != true)
            throw new AmisException($"AMIS {path} báo lỗi: {Str(root, "UserMessage") ?? Str(root, "SystemMessage") ?? Trunc(text, 300)}");
        return obj["Data"];
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
