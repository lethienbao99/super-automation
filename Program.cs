using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SuperAutomation.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Services.Configure<AmisOptions>(builder.Configuration.GetSection("Amis"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<AmisSession>();
builder.Services.AddSingleton<AmisClient>();
builder.Services.AddSingleton<AmisDocs>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<RepoService>();
builder.Services.AddSingleton<CostLedger>();
builder.Services.AddSingleton<SkillKit>();
builder.Services.AddSingleton<Insights>();
builder.Services.AddSingleton<TfsClient>();
builder.Services.AddSingleton<KeepAwake>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KeepAwake>());
builder.Services.AddSingleton<AmisChatBot>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AmisChatBot>());
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
builder.Services.AddSingleton<TestEnvService>();
builder.Services.AddSingleton<TestDataService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<PortalDevRunner>();
builder.Services.AddSingleton<BranchAdvisor>();
builder.Services.AddSingleton<DbProfiles>();
builder.Services.AddSingleton<AutoPilotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoPilotService>());
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 200 * 1024 * 1024);

var app = builder.Build();
RepoService.OnStaleLock = m => app.Logger.LogWarning("{Msg}", m);
Standards.Dir = Path.Combine(app.Environment.ContentRootPath, "standards");

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (AmisException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (HttpRequestException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new { error = "Không kết nối được AMIS: " + ex.Message });
    }
});
app.UseDefaultFiles();
// no-cache: trình duyệt luôn hỏi lại (ETag → 304 nếu không đổi) — sửa giao diện xong tải lại trang là thấy, không dính CSS/JS cũ
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

api.MapGet("/config", (SettingsService s) => s.View());

// ---------- Cấu hình: thư mục source, repo, Claude (đăng nhập / API key) ----------
api.MapGet("/settings", (SettingsService s) => s.View());

api.MapPut("/settings", (SettingsUpdate body, SettingsService s, JobStore store, JobRunner runner, EventHub hub) =>
{
    var sourceChanged = body.WorkspaceRoot is not null && !string.Equals(Path.GetFullPath(body.WorkspaceRoot.Trim().Trim('"')), s.Pipeline.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                        || body.RepoRelativePath is not null && !string.Equals(body.RepoRelativePath.Trim().Trim('"', '\\', '/'), s.Pipeline.RepoRelativePath, StringComparison.OrdinalIgnoreCase)
                        || body.ExtraRepos is not null && !body.ExtraRepos.Order().SequenceEqual(s.Pipeline.ExtraRepos.Order(), StringComparer.OrdinalIgnoreCase);
    // Job đang chạy hoặc đang chờ trả lời phụ thuộc vào nhánh AI trong repo hiện tại
    if (sourceChanged && (runner.IsBusy || store.All().Any(j => j.Status is JobStatus.Queued or JobStatus.WaitingForUser || j.IsRunning)))
        return Results.Conflict(new { error = "Đang có job chạy, xếp hàng hoặc chờ trả lời trên repo hiện tại. Hoàn tất hoặc huỷ chúng trước khi đổi thư mục source." });
    if (s.Apply(body) is { } err) return Results.BadRequest(new { error = err });
    hub.Publish(new { kind = "repo" });
    return Results.Ok(s.View());
});

api.MapPut("/settings/project-branch", (ProjectBranchBody body, SettingsService s) =>
    s.SetProjectBranch(body.Project ?? "", body.Keywords) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(s.View()));

// ---------- Môi trường test ----------
api.MapGet("/testenv", (TestEnvService t) => t.View());

// Tình trạng môi trường (ô "Môi trường · Lịch"): IIS, Portal dev server, DB test, Claude — giữ 30 giây, ?force=true để kiểm lại ngay
api.MapGet("/health", async (bool? force, HealthService h, CancellationToken ct) => Results.Ok(await h.Get(force == true, ct)));

// Dev server Portal V3: bật một module (chạy nền, trả ngay — xem lại ô Môi trường sau 2–5 phút) / tắt các dev server do tool bật
api.MapPost("/portal/start", (PortalStartBody body, SettingsService s, TestEnvService t, PortalDevRunner runner, ILogger<PortalDevRunner> log) =>
{
    if (PortalDev.FindRoot(s.Pipeline) is not { } root) return Results.BadRequest(new { error = "Chưa khai báo repo Portal (Cấu hình › Repo phụ)." });
    var shellPort = t.ExtraSiteList().Where(x => x.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase))
        .Select(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var u) ? u.Port : 0).FirstOrDefault(p => p > 0);
    var mods = PortalDev.Modules(root, shellPort > 0 ? shellPort : 4200);
    var m = body.Project is null or "" or "shell" ? mods[0] : mods.FirstOrDefault(x => string.Equals(x.Project, body.Project, StringComparison.OrdinalIgnoreCase));
    if (m is null) return Results.BadRequest(new { error = $"Không có module {body.Project}. Có: {string.Join(", ", mods.Skip(1).Select(x => x.Project))}" });
    if (m.Running) return Results.Ok(new { message = $"{m.Project} :{m.Port} đang chạy rồi." });
    _ = Task.Run(async () =>
    {
        var (ok, msg) = await runner.EnsureRunning(root, m, TimeSpan.FromMinutes(6), CancellationToken.None);
        log.LogInformation("Portal dev server: {Message}", msg);
    });
    return Results.Ok(new { message = $"Đang bật {m.Project} (yarn {m.Script}) — build lần đầu mất 2–5 phút, bấm ↻ ở ô Môi trường để xem." });
});
// Đề xuất nhánh gốc BE/FE theo dự án (chỉ fetch + đọc ref, không đổi nhánh) — cùng logic lệnh chat "nhánh ABC"
api.MapGet("/branches/suggest", async (string project, BranchAdvisor b, CancellationToken ct) =>
    Results.Ok(await b.Suggest(project, ct)));
// Hồ sơ DB theo dự án (mẫu VnrConfigTool): xem / lưu / xoá / áp
api.MapGet("/dbprofiles", (DbProfiles d) => Results.Ok(d.View()));
api.MapPut("/dbprofiles", (DbProfileBody b, DbProfiles d) =>
    d.Save(b.Project ?? "", b.Template, null, b.AiUser, b.AiPassword) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(d.View()));
api.MapPut("/dbprofiles/dir", (DbProfileDirBody b, DbProfiles d) =>
    d.SetTemplateDir(b.Dir) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(d.View()));
api.MapPost("/dbprofiles/check", async (DbProfileBody b, DbProfiles d, CancellationToken ct) =>
{
    var (ok, info) = await d.CheckLogin(b.Project ?? "", b.Template, b.AiUser, b.AiPassword, ct);
    return Results.Ok(new { ok, info });
});
api.MapDelete("/dbprofiles/{project}", (string project, DbProfiles d) => { d.Delete(project); return Results.Ok(d.View()); });
api.MapPost("/dbprofiles/{project}/apply", (string project, DbProfiles d, JobRunner runner) =>
    runner.IsBusy ? Results.BadRequest(new { error = "AI đang chạy job — đợi xong rồi mới đổi DB." })
    : d.Apply(project) is { } msg ? Results.Ok(new { message = msg }) : Results.BadRequest(new { error = "Chưa có hồ sơ DB cho dự án này." }));
api.MapPost("/portal/stop-started", (PortalDevRunner runner) => Results.Ok(new { stopped = runner.StopStarted() }));

api.MapPut("/testenv", (TestEnvBody body, TestEnvService t) =>
{
    var o = new TestEnvOptions
    {
        Enabled = body.Enabled ?? true, UiTests = body.UiTests, MaxTestCostUsd = body.MaxTestCostUsd ?? 8, AutoFixOnTestFail = body.AutoFixOnTestFail ?? true, MaxTestFixRounds = Math.Clamp(body.MaxTestFixRounds ?? 2, 1, 5), AutoStartPortal = body.AutoStartPortal ?? true, TempAccounts = body.TempAccounts ?? false, BaseUrl = body.BaseUrl ?? "", User = body.User ?? "", Password = body.Password ?? "",
        BuildBeforeTest = body.BuildBeforeTest, RebuildAfterReturn = body.RebuildAfterReturn, ExtraSites = body.ExtraSites ?? "",
        Accounts = (body.Accounts ?? []).Select(a => new TestAccount { Role = a.Role ?? "", User = a.User ?? "", Password = a.Password }).ToList(),
    };
    return t.Update(o, keepPassword: body.Password is null) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(t.View());
});

// ---------- DB dữ liệu test ----------
api.MapGet("/testdb", (TestDataService t) => t.View());

api.MapPut("/testdb", (TestDbBody body, TestDataService t) =>
    t.Update(body.ToOptions(), keepPassword: body.Password is null) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(t.View()));

// Kiểm tra kết nối với thông tin đang nhập trên form (mật khẩu trống = dùng mật khẩu đã lưu)
api.MapPost("/testdb/check", async (TestDbBody body, TestDataService t, CancellationToken ct) =>
{
    var o = body.ToOptions();
    if (body.Password is null) o.Password = t.Options.Password;
    var (ok, info) = await t.Check(o, ct);
    return Results.Ok(new { ok, info });
});

// AI ghi DB test trong lúc job chạy: chỉ nhận mã của job đang chạy (biến môi trường SA_SQL_TOKEN tool cấp cho phiên AI)
api.MapPost("/ai-sql", async (HttpRequest req, JobRunner runner, CancellationToken ct) =>
{
    if (runner.JobBySqlToken(req.Headers["X-Job-Token"].ToString()) is not { } job)
        return Results.Text("Mã không hợp lệ hoặc job không còn chạy.", statusCode: 403);
    using var reader = new StreamReader(req.Body, System.Text.Encoding.UTF8);
    var sql = await reader.ReadToEndAsync(ct);
    var (ok, output) = await runner.RunAiSql(job, sql, ct);
    return Results.Text(output, "text/plain; charset=utf-8", statusCode: ok ? 200 : 400);
});

api.MapPost("/jobs/{id}/db-undo", async (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    if (job.IsRunning) return Results.Conflict(new { error = "Job đang chạy — đợi xong rồi hoàn tác." });
    var (ok, output) = await runner.UndoAiSql(job);
    return ok ? Results.Ok(store.Get(id)) : Results.BadRequest(new { error = output.Length > 600 ? output[..600] : output });
});

// Trả store đã deploy lên DB test về bản cũ (đối tượng mới thì xoá)
api.MapPost("/jobs/{id}/restore-sql", async (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    if (job.IsRunning) return Results.Conflict(new { error = "Job đang chạy — đợi xong rồi khôi phục." });
    var r = await runner.RestoreSql(job);
    return r.Ok ? Results.Ok(store.Get(id)) : Results.BadRequest(new { error = r.Message });
});

api.MapPost("/jobs/{id}/cleanup-data", async (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    if (job.IsRunning) return Results.Conflict(new { error = "Job đang chạy — đợi xong rồi dọn." });
    var r = await runner.CleanupTestData(job);
    return r.Ok ? Results.Ok(new { ok = true, message = r.Message }) : Results.BadRequest(new { error = r.Message });
});

// Bằng chứng test (ảnh chụp, trace…) trong data/jobs/{id}/test-artifacts
api.MapGet("/jobs/{id}/artifacts", (string id, JobStore store) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var dir = Path.Combine(store.Dir(id), "test-artifacts");
    if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
    return Results.Ok(Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .Select(f => new { name = Path.GetRelativePath(dir, f).Replace('\\', '/'), size = new FileInfo(f).Length })
        .OrderBy(f => f.name));
});

api.MapGet("/jobs/{id}/artifacts/{**path}", (string id, string path, JobStore store) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var dir = Path.GetFullPath(Path.Combine(store.Dir(id), "test-artifacts")) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(Path.Combine(dir, path));
    if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    return Results.File(full, types.TryGetContentType(full, out var ct) ? ct : "application/octet-stream");
});

// ---------- Chế độ tự động ----------
api.MapGet("/autopilot", (AutoPilotService ap) => ap.State());

api.MapPut("/autopilot", (AutoPilotOptions body, AutoPilotService ap) =>
    ap.Update(body) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(ap.State()));

api.MapPost("/autopilot/run", (AutoPilotService ap) => { ap.RunNow(); return Results.Ok(new { ok = true }); });

// File đã track đang sửa trong một repo — để chọn nhanh file cấu hình local
api.MapGet("/settings/modified", async (string repo, SettingsService s, RepoService repos, CancellationToken ct) =>
{
    var rel = repo.Trim().Trim('"', '\\', '/');
    var path = Path.Combine(s.Pipeline.WorkspaceRoot, rel);
    if (rel is "" or "." || !SettingsService.IsGitRepo(path)) return Results.BadRequest(new { error = $"{rel} không phải git repo con của workspace." });
    var files = await repos.Modified(rel, path, ct);
    return Results.Ok(files.Select(f => new { path = f.Path, local = f.Local }));
});

api.MapGet("/settings/repos", (string? root, SettingsService s) =>
{
    var r = string.IsNullOrWhiteSpace(root) ? s.Pipeline.WorkspaceRoot : root.Trim().Trim('"');
    if (!Directory.Exists(r)) return Results.BadRequest(new { error = $"Không tìm thấy thư mục {r}" });
    return Results.Ok(new
    {
        root = Path.GetFullPath(r),
        hasClaudeMd = File.Exists(Path.Combine(r, "CLAUDE.md")),
        repos = SettingsService.FindRepos(r),
    });
});

api.MapPost("/settings/test-claude", async (TestClaudeBody? body, SettingsService s, CancellationToken ct) =>
{
    ClaudeAuth auth;
    try { auth = s.ClaudeAuthFor(body?.AuthMode, body?.ApiKey, body?.KeyId); }
    catch (PipelineException ex) { return Results.Ok(new { ok = false, auth = "API key", info = (string?)null, result = ex.Message, costUsd = 0.0 }); }
    return Results.Ok(await TestClaude(s, auth, body?.Model, ct));
});

// Kiểm lần lượt mọi key đang bật (theo cách xác thực đang chọn trên form)
api.MapPost("/settings/test-keys", async (TestClaudeBody? body, SettingsService s, CancellationToken ct) =>
{
    var mode = body?.AuthMode is "apikey" or "sdk" ? body.AuthMode : "sdk";
    var results = new List<object>();
    foreach (var (id, label) in s.EnabledKeys())
    {
        object r;
        try { r = await TestClaude(s, s.ClaudeAuthFor(mode, null, id), body?.Model, ct); }
        catch (PipelineException ex) { r = new { ok = false, auth = label, info = (string?)null, result = ex.Message, costUsd = 0.0 }; }
        results.Add(new { id, label, r });
    }
    return Results.Ok(new { mode, results, view = s.View() });
});

api.MapPost("/session", (SessionBody body, AmisSession session) =>
{
    if (string.IsNullOrWhiteSpace(body.SessionId)) return Results.BadRequest(new { error = "Thiếu sessionId." });
    session.Update(body.SessionId);
    return Results.Ok(new { ok = true });
});

api.MapGet("/me", (AmisClient amis, CancellationToken ct) => amis.GetMe(ct));

// ---------- Source code (repo pipeline sửa) ----------
api.MapGet("/repo", async (RepoService repo, JobRunner runner, CancellationToken ct) =>
{
    try
    {
        var info = await repo.GetInfo(ct);
        await runner.ReleaseStaleHolds(); // người dùng tự chuyển nhánh → bỏ cờ giữ repo lỗi thời ngay khi giao diện làm mới
        return Results.Ok(new { info, busy = runner.IsBusy });
    }
    catch (PipelineException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

api.MapGet("/repo/branches", async (RepoService repo, CancellationToken ct) =>
{
    try { return Results.Ok(await repo.GetBranches(ct)); }
    catch (PipelineException ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

api.MapPost("/repo/switch", async (SwitchBody body, RepoService repo, JobRunner runner, EventHub hub, CancellationToken ct) =>
{
    if (runner.IsBusy) return Results.Json(new { error = "AI đang chạy job trên repo này — đợi xong hoặc huỷ job rồi mới đổi nhánh." }, statusCode: 409);
    try
    {
        await repo.Switch(body.Branch, ct);
        hub.Publish(new { kind = "repo" });
        return Results.Ok(new { ok = true });
    }
    catch (PipelineException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 409);
    }
});

api.MapPost("/repo/fetch", async (RepoService repo, EventHub hub, CancellationToken ct) =>
{
    try
    {
        await repo.Fetch(ct);
        hub.Publish(new { kind = "repo" });
        return Results.Ok(new { ok = true });
    }
    catch (PipelineException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

var taskTypes = new HashSet<string> { "TotalTaskDue", "TotalTaskDueSoon", "TotalTaskExpire" };
api.MapGet("/tasks", async (string? type, AmisClient amis, CancellationToken ct) =>
{
    type ??= "TotalTaskDue";
    if (!taskTypes.Contains(type)) return Results.BadRequest(new { error = $"type phải là một trong {string.Join(", ", taskTypes)}." });
    var list = await amis.GetMyTasks(type, ct);
    // Task con thường chỉ tên "SE thực hiện" — kèm tên task cha cho dễ nhận ra (tên được nhớ lại, chỉ gọi AMIS lần đầu)
    var ids = list.Where(t => t.ParentId is > 0).Select(t => t.ParentId!.Value).Distinct().ToList();
    var names = new System.Collections.Concurrent.ConcurrentDictionary<long, string?>();
    await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
        async (id, c) => names[id] = await amis.GetTaskName(id, c));
    return Results.Ok(list.Select(t => t.ParentId is { } pid && names.GetValueOrDefault(pid) is { } n ? t with { ParentName = n } : t));
});

api.MapGet("/tasks/{id:long}", async (long id, AmisClient amis, CancellationToken ct) =>
    Results.Ok(AmisClient.ToPreview(await amis.GetTaskBundle(id, ct))));

api.MapGet("/jobs", (JobStore store) => store.All());

api.MapGet("/jobs/{id}", (string id, JobStore store) =>
    store.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());

api.MapPost("/jobs", async (HttpRequest req, JobStore store, JobRunner runner) =>
{
    var form = await req.ReadFormAsync();
    if (!long.TryParse(form["taskId"], out var taskId)) return Results.BadRequest(new { error = "Thiếu taskId." });
    if (store.All().FirstOrDefault(j => j.TaskId == taskId && !j.IsFinal) is { } active)
        return Results.Conflict(new { error = $"Task này đang có job {active.Id} ({active.Status})." });

    var job = store.Create(taskId, form["taskName"].ToString(), form["projectName"], form["endDate"], form["note"]);
    job.TestMode = JobRunner.NormTestMode(form["testMode"]);
    await SaveInputs(job, form.Files, store);
    store.Save(job);
    runner.Enqueue(job);
    return Results.Ok(job);
});

// Chạy hàng loạt: mỗi task một job (phân tích song song, code lần lượt), không giữ repo — xong tự commit vào nhánh AI riêng
api.MapPost("/jobs/batch", async (BatchBody body, JobStore store, JobRunner runner, RepoService repo, SettingsService settings, CancellationToken ct) =>
{
    var tasks = (body.Tasks ?? []).DistinctBy(t => t.TaskId).ToList();
    if (tasks.Count == 0) return Results.BadRequest(new { error = "Chưa chọn task nào." });
    if (tasks.Count > 30) return Results.BadRequest(new { error = "Tối đa 30 task mỗi lô." });
    await runner.ReleaseStaleHolds();
    if (store.All().FirstOrDefault(j => j.Held) is { } held)
        return Results.BadRequest(new { error = $"Job #{held.TaskId} đang giữ repo (code chưa commit) — mở job đó bấm \"Tạo commit\" trước, nếu không các task trong lô sẽ không code được." });
    var batchId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var created = new List<Job>();
    var skipped = new List<object>();
    foreach (var t in tasks)
    {
        if (store.All().FirstOrDefault(j => j.TaskId == t.TaskId && !j.IsFinal) is { } active)
        {
            skipped.Add(new { taskId = t.TaskId, reason = $"đang có job ({active.Status})" });
            continue;
        }
        var job = store.Create(t.TaskId, t.TaskName ?? "", t.ProjectName, t.EndDate, body.Note);
        job.Batch = true;
        job.BatchId = batchId;
        job.BatchChain = body.Chain;
        job.Unattended = body.Unattended;
        store.Log(job, "control", "stage", $"Giao theo lô {batchId} ({tasks.Count} task): yêu cầu rõ thì tự code, xong tự commit vào nhánh AI riêng (không push), " +
                                           (body.Chain ? "task đụng cùng file với task trước thì nối nhánh." : "task đụng cùng file chỉ cảnh báo."));
        store.Save(job);
        runner.Enqueue(job);
        created.Add(job);
    }
    string? warn = null;
    var o = settings.Pipeline;
    foreach (var (rel, path) in new[] { (o.RepoRelativePath, o.RepoPath) }.Concat(o.ExtraRepoPaths))
    {
        try
        {
            var dirty = await repo.Dirty(rel, path, ct);
            if (dirty.Count > 0) { warn = $"{rel} đang có {dirty.Count} file sửa chưa commit — commit hoặc stash trước khi phân tích xong, nếu không bước code sẽ lỗi."; break; }
        }
        catch (PipelineException) { }
    }
    return Results.Ok(new { batchId, created, skipped, warn });
});

// Bổ sung tài liệu khi job đang chờ ở bước phân tích (thường là tệp đính kèm AMIS mà tool không tải được)
api.MapPost("/jobs/{id}/files", async (string id, HttpRequest req, JobStore store) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    if (job.Status is not (JobStatus.NeedsInfo or JobStatus.Clarified))
        return Results.BadRequest(new { error = "Chỉ bổ sung tài liệu được khi job đang ở cột \"Cần bổ sung\" hoặc \"Đã rõ yêu cầu\"." });
    var form = await req.ReadFormAsync();
    if (form.Files.Count == 0) return Results.BadRequest(new { error = "Chưa chọn tệp." });
    var names = await SaveInputs(job, form.Files, store);
    store.Log(job, "inputs", "info", $"Người dùng bổ sung: {string.Join(", ", names)}. Bấm \"Phân tích lại\" để AI đọc.");
    foreach (var note in DocExtract.ExtractAll(Path.Combine(store.Dir(job.Id), "inputs"), names))
        store.Log(job, "inputs", "info", note);
    store.Save(job);
    return Results.Ok(job);
});

api.MapPost("/jobs/{id}/proceed", async (string id, ProceedBody body, JobRunner runner, CancellationToken ct) =>
    await runner.Proceed(id, body.Mode ?? "", ct, body.Text) is { } err ? Results.BadRequest(new { error = err.Message }) : Results.Ok(new { ok = true }));

api.MapPost("/jobs/{id}/cancel", (string id, JobRunner runner) =>
    runner.Cancel(id) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Job không ở trạng thái huỷ được." }));

// Người dùng tự làm lại task: code AI (nếu còn chưa commit) được commit tạm vào nhánh AI — KHÔNG push — rồi trả repo
// về nhánh ban đầu; job chuyển sang Từ chối để rời cột "Chờ bạn" và nhả repo cho task khác.
api.MapPost("/jobs/{id}/self", async (string id, SelfBody body, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var reason = "Tự làm lại" + (string.IsNullOrWhiteSpace(body.Reason) ? "" : ": " + body.Reason.Trim());
    if (await runner.DecideFromChat(id, false, reason, "tự làm") is { } err) return Results.BadRequest(new { error = err.Message });
    return Results.Ok(store.Get(id));
});

api.MapPost("/jobs/{id}/decision", async (string id, DecisionBody body, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    if (await runner.Decide(id, body.Approve, body.Push, body.Reason) is { } derr) return Results.BadRequest(new { error = derr.Message });
    var job = store.Get(id)!;
    if (body.Approve && body.Push) await runner.ReturnToBase(job);
    if (body.Approve && body.Push && await runner.PushBranch(job) is { } pushErr)
        return Results.BadRequest(new { error = "Đã duyệt, nhưng " + pushErr.Message });
    if (body.Approve && body.Push && runner.AutoPr && await runner.CreatePrs(job) is { } prErr)
        return Results.BadRequest(new { error = "Đã duyệt và push, nhưng " + prErr.Message + " — sửa xong bấm \"Tạo PR\" trong job." });
    return Results.Ok(job);
});

// Bot AMIS Chat: cấu hình, gửi thử
api.MapGet("/amischat", (AmisChatBot bot) =>
{
    var c = bot.Cfg();
    return Results.Ok(new { c.Enabled, c.ConversationId, c.NotifyAsk, c.NotifyBatch, c.NotifyDone, c.NotifyFail, lastError = bot.LastError, lastPoll = bot.LastPoll });
});
api.MapPost("/amischat", async (AmisChatBody body, AmisChatBot bot, CancellationToken ct) =>
{
    var conv = AmisChatBot.ParseConversation(body.Conversation);
    if (body.Enabled && conv.Length == 0) return Results.BadRequest(new { error = "Dán link nhóm AMIS Chat (dạng https://amisapp.misa.vn/chat/cv/…) trước khi bật." });
    string? name = null;
    if (conv.Length > 0)
    {
        try { name = await bot.ConversationName(conv, ct); }
        catch (Exception ex) when (ex is AmisException or HttpRequestException or TaskCanceledException)
        { return Results.BadRequest(new { error = "Không đọc được nhóm: " + ex.Message }); }
    }
    bot.Save(new AmisChatBot.Config(body.Enabled, conv, body.NotifyAsk, body.NotifyBatch, body.NotifyDone, body.NotifyFail));
    return Results.Ok(new { ok = true, name });
});
api.MapGet("/amischat/ask", async (string q, bool? ai, AmisChatBot bot, CancellationToken ct) =>
    Results.Ok(new { start = bot.PreviewStart(q), reply = await bot.PreviewTaskQuery(q, ct), ai = ai == true ? await bot.PreviewIntent(q, ct) : null }));
api.MapPost("/amischat/test",async (AmisChatBot bot, CancellationToken ct) =>
{
    try
    {
        await bot.Send("Kết nối OK — SuperAutomation sẽ nhắn vào nhóm này khi AI cần bạn xác nhận, khi lô chạy xong hoặc khi job lỗi.\n" +
                       "Trả lời: #<mã task> 1: A, 2: đồng ý · #<mã task> ok · #<mã task> dừng · gõ ? để xem job đang mở.\n" +
                       "Giao task: #<mã task> làm đi · Hỏi lịch: mai có task gì", ct);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) when (ex is AmisException or HttpRequestException or TaskCanceledException or PipelineException)
    { return Results.BadRequest(new { error = ex.Message }); }
});

// Kiểm tay trên web: chuyển repo sang nhánh AI của job + build; xong thì commit phần sửa thêm và trả repo
api.MapPost("/jobs/{id}/inspect", async (string id, JobRunner runner, JobStore store) =>
    await runner.StartInspect(id) is { } err ? Results.BadRequest(new { error = err.Message }) : Results.Ok(store.Get(id)));
api.MapPost("/jobs/{id}/inspect/done", async (string id, JobRunner runner, JobStore store) =>
    await runner.FinishInspect(id) is { } err ? Results.BadRequest(new { error = err.Message }) : Results.Ok(store.Get(id)));

// Bản tin sáng: kết quả các lô chạy hàng loạt gần đây — mỗi task một dòng, kèm giả định AI tự chọn
api.MapGet("/brief", (int? days, JobStore store, KeepAwake awake) =>
{
    var from = DateTime.Now.AddDays(-(days ?? 3));
    var jobs = store.All().Where(j => j.Batch && j.CreatedAt >= from).ToList();
    string? Read(Job j, string f)
    {
        var p = Path.Combine(store.Dir(j.Id), f);
        try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; } catch (IOException) { return null; }
    }
    return Results.Ok(new
    {
        keepAwake = awake.Active,
        batches = jobs.GroupBy(j => j.BatchId ?? "").OrderByDescending(g => g.Key).Select(g => new
        {
            batchId = g.Key,
            unattended = g.Any(j => j.Unattended),
            running = g.Count(j => j.IsRunning || j.Status == JobStatus.Queued),
            jobs = g.OrderBy(j => j.CreatedAt).Select(j => new
            {
                j.Id, j.TaskId, j.TaskName, j.ParentTaskName, status = j.Status.ToString(), j.Requirement,
                j.ReviewVerdict, j.TestVerdict, j.FixRounds, j.AutoRetries, j.AutoAnswers, j.Error, j.CostUsd,
                j.Branch, j.ChainedFromTask, j.Overlaps, j.PrUrls, j.Inspecting,
                files = j.ChangedFiles.Count,
                assumptions = Read(j, "assumptions.md"),
                summary = Read(j, "pr.md"),
            }),
        }),
    });
});

// Gửi kết quả lên AMIS: bản nháp bình luận để người dùng xem/sửa, rồi gửi (bình luận + tuỳ chọn chuyển task con sang Hoàn thành)
api.MapGet("/jobs/{id}/amis", (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    var lines = new List<string> { $"Đã xử lý xong task #{job.TaskId}." };
    // Bình luận AMIS không hiển thị markdown: bỏ ##, **, `
    if (runner.PrBody(job) is { Length: > 0 } body) lines.Add(JobRunner.PlainText(body));
    if (job.Branch is not null) lines.Add($"Nhánh: {job.Branch}");
    if (job.PrUrls.Count > 0) lines.Add("Pull Request: " + string.Join(" , ", job.PrUrls.Values));
    if (job.TestVerdict is { Length: > 0 } tv && tv != "UNKNOWN") lines.Add($"Kết quả kiểm thử: {tv}");
    var targets = new List<object> { new { id = job.TaskId, name = job.TaskName, kind = "con" } };
    if (job.ParentTaskId is { } p) targets.Insert(0, new { id = p, name = job.ParentTaskName ?? "", kind = "cha" });
    return Results.Ok(new
    {
        comment = string.Join("\n", lines),
        targets,
        defaultTarget = job.ParentTaskId ?? job.TaskId, // bình luận tình hình lên task cha (yêu cầu gốc); trạng thái Hoàn thành thì chuyển ở task con
        commentedAt = job.AmisCommentAt, commentTask = job.AmisCommentTask, doneAt = job.AmisDoneAt,
    });
});

api.MapPost("/jobs/{id}/amis", async (string id, AmisPostBody body, JobStore store, AmisClient amis, CancellationToken ct) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    var text = (body.Comment ?? "").Trim();
    if (text.Length == 0 && !body.MarkDone) return Results.BadRequest(new { error = "Chưa có gì để gửi." });
    var target = body.TargetTaskId ?? job.TaskId;
    if (target != job.TaskId && target != job.ParentTaskId) return Results.BadRequest(new { error = "Chỉ gửi được lên task này hoặc task cha của nó." });
    var done = new List<string>();
    try
    {
        if (text.Length > 0)
        {
            await amis.PostComment(target, AmisClient.ToCommentHtml(text), ct);
            job.AmisCommentTask = target;
            job.AmisCommentAt = DateTime.Now;
            done.Add($"bình luận lên task #{target}");
            store.Log(job, "amis", "info", $"Đã gửi bình luận lên AMIS task #{target}.");
        }
        if (body.MarkDone)
        {
            await amis.SetStatus(job.TaskId, AmisClient.StatusDone, ct);
            job.AmisDoneAt = DateTime.Now;
            done.Add($"chuyển task #{job.TaskId} sang Hoàn thành");
            store.Log(job, "amis", "info", $"Đã chuyển task #{job.TaskId} sang Hoàn thành trên AMIS.");
        }
    }
    catch (AmisException ex)
    {
        store.Log(job, "amis", "error", "Gửi lên AMIS lỗi: " + ex.Message);
        store.Save(job);
        return Results.BadRequest(new { error = (done.Count > 0 ? $"Đã {string.Join(", ", done)}; nhưng " : "") + ex.Message });
    }
    store.Save(job);
    return Results.Ok(new { job, done });
});

// TFS: cấu hình PAT / tự tạo PR, kiểm kết nối, tạo PR cho một job
api.MapGet("/tfs", (TfsClient tfs) => tfs.View());
api.MapPost("/tfs", (TfsBody body, TfsClient tfs) => { tfs.Update(body.Pat, body.ClearPat, body.AutoPr); return Results.Ok(tfs.View()); });
api.MapPost("/tfs/test", async (TfsClient tfs, SettingsService settings, CancellationToken ct) =>
{
    var lines = new List<string>();
    var ok = true;
    foreach (var (rel, path) in new[] { (settings.Pipeline.RepoRelativePath, settings.Pipeline.RepoPath) }.Concat(settings.Pipeline.ExtraRepoPaths))
    {
        try { lines.Add($"{rel}: {await tfs.Test(path, ct)}"); }
        catch (Exception ex) when (ex is PipelineException or HttpRequestException or TaskCanceledException) { ok = false; lines.Add($"{rel}: {ex.Message}"); }
    }
    return Results.Ok(new { ok, lines });
});
api.MapPost("/jobs/{id}/pr", async (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    // Người dùng bấm "Tạo PR" = đồng ý push: job "Chỉ duyệt" chưa push thì push trước (kèm kiểm conflict; conflict → tool tự gộp rồi tạo PR)
    if (await runner.PushBranch(job) is { } pushErr) return Results.BadRequest(new { error = pushErr.Message });
    if (await runner.CreatePrs(job) is { } err) return Results.BadRequest(new { error = err.Message });
    await runner.ReturnToBase(job); // PR đã tạo: repo còn ở nhánh AI (không còn gì chưa commit) thì về nhánh ban đầu
    return Results.Ok(job);
});

// Bài học: góp ý của người dùng thành lưu ý gắn vào prompt các task sau
api.MapGet("/lessons", (Insights insights) => insights.Lessons().OrderBy(l => l.Pending).ThenByDescending(l => l.CreatedAt));
api.MapPost("/lessons", (LessonBody body, Insights insights) =>
{
    try { return Results.Ok(insights.Upsert(body.Id, body.Text ?? "", body.Enabled)); }
    catch (PipelineException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapDelete("/lessons/{lid}", (string lid, Insights insights) => insights.Delete(lid) ? Results.Ok(new { ok = true }) : Results.NotFound());

// Thời gian từng bước, chờ người, tỉ lệ review đạt lần đầu — để biết nên tối ưu chỗ nào
api.MapGet("/insights/stats", (int? days, Insights insights) => insights.Stats(days ?? 30));
// Task tương tự (xem trước AI sẽ được tham khảo gì)
api.MapGet("/jobs/{id}/similar", (string id, JobStore store, Insights insights) =>
    store.Get(id) is { } job
        ? Results.Ok(insights.Similar(job).Select(s => new { s.Job.Id, s.Job.TaskId, s.Job.TaskName, status = s.Job.Status.ToString(), score = Math.Round(s.Score, 2), s.SharedFiles }))
        : Results.NotFound());

// Nhánh nối tiếp: nhánh cha đã đổi chưa / dựng lại nhánh con trên bản mới của nhánh cha
api.MapGet("/jobs/{id}/chain", async (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    try { return Results.Ok(new { drift = await runner.ChainDrift(job) }); }
    catch (PipelineException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/jobs/{id}/restack", async (string id, JobStore store, JobRunner runner) =>
    await runner.Restack(id) is { } err ? Results.BadRequest(new { error = err.Message }) : Results.Ok(store.Get(id)));

// Xác nhận commit: xem file + message đề xuất, rồi commit (tuỳ chọn push) phần code AI đang giữ trên nhánh AI
api.MapGet("/jobs/{id}/commit", async (string id, JobStore store, JobRunner runner, CancellationToken ct) =>
    store.Get(id) is { } job ? Results.Ok(await runner.PendingCommit(job, ct)) : Results.NotFound());

api.MapPost("/jobs/{id}/commit", async (string id, CommitBody body, JobStore store, JobRunner runner) =>
{
    var err = await runner.CommitHeld(id, new CommitPlan(body.Message, body.Push, body.NewFiles, body.Skip), body.Decision);
    if (err is not null) return Results.BadRequest(new { error = err.Message });
    if (body.Push && runner.AutoPr && store.Get(id) is { } pj && await runner.CreatePrs(pj) is { } prErr)
        return Results.BadRequest(new { error = "Đã commit và push, nhưng " + prErr.Message + " — sửa xong bấm \"Tạo PR\" trong job." });
    return Results.Ok(store.Get(id));
});

// Nhận JSON {text} hoặc multipart (text + files) — tệp được lưu vào chat và chép sang inputs/ của job khi chạy tiếp
api.MapPost("/jobs/{id}/answer", async (string id, HttpRequest req, JobStore store, JobRunner runner, ChatService chat, CancellationToken ct) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    var (text, _, files) = await ReadMessage(req, ct);
    var names = files.Count > 0 ? await chat.SaveFiles(job.TaskId, files, ct) : [];
    return runner.Answer(id, text, names) is { } err ? Results.BadRequest(new { error = err.Message }) : Results.Ok(new { ok = true });
});

// ---------- Bộ skill/agent HRM đi kèm tool ----------
api.MapGet("/kit", (string? target, SkillKit kit) => Results.Ok(kit.View(target == "project" ? "project" : "user")));
api.MapPost("/kit/install", (KitBody body, SkillKit kit) =>
{
    var (done, backup) = kit.Install(body.Ids, body.Target == "project" ? "project" : "user", body.Overwrite);
    return Results.Ok(new { done, backup });
});
api.MapPost("/kit/export", (KitBody body, SkillKit kit) =>
    body.Ids is { Count: > 0 } ids ? Results.Ok(new { done = kit.Export(ids) }) : Results.BadRequest(new { error = "Chưa chọn mục nào." }));
// Gói .zip để chuyển skill/agent nội bộ (bị .gitignore, không có trên repo) sang máy khác
api.MapGet("/kit/package", (string? ids, bool? standards, SkillKit kit) =>
{
    var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    if (list.Count == 0 && standards != true) return Results.BadRequest(new { error = "Chưa chọn mục nào." });
    // ids rỗng = Pick lấy hết → chỉ xuất standards thì truyền một id không tồn tại
    var bytes = kit.Package(list.Count > 0 ? list : ["-"], standards == true);
    return Results.File(bytes, "application/zip", $"claude-kit-{DateTime.Now:yyyyMMdd-HHmm}.zip");
});
api.MapPost("/kit/package", async (HttpRequest req, SkillKit kit) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "Chưa gửi file .zip." });
    var form = await req.ReadFormAsync();
    if (form.Files.FirstOrDefault() is not { Length: > 0 } file) return Results.BadRequest(new { error = "Chưa gửi file .zip." });
    if (file.Length > 50L * 1024 * 1024) return Results.BadRequest(new { error = "File quá 50 MB." });
    using var s = new MemoryStream();
    await file.CopyToAsync(s);
    // Chữ ký zip "PK\x03\x04" (hoặc "PK\x05\x06" cho zip rỗng) — file khác thì ZipArchive ném lỗi khó hiểu
    var buf = s.GetBuffer();
    if (s.Length < 22 || buf[0] != 'P' || buf[1] != 'K') return Results.BadRequest(new { error = "File không phải zip hợp lệ." });
    s.Position = 0;
    try
    {
        var r = kit.ImportPackage(s);
        return Results.Ok(new { done = r.Done, standards = r.Standards, backup = r.Backup });
    }
    catch (PipelineException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) { return Results.BadRequest(new { error = "File zip bị hỏng." }); }
});
api.MapPost("/kit/remove", (KitBody body, SkillKit kit) =>
    body.Ids is [var id] && kit.Remove(id) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Không bỏ được (mục bắt buộc hoặc không có trong bộ cài)." }));

// ---------- Thống kê chi phí ----------
api.MapGet("/costs", (int? days, string? paid, CostLedger costs) =>
    Results.Ok(costs.Stats(Math.Clamp(days ?? 30, 0, 3650), paid is "paid" or "est" ? paid : "all")));

// ---------- Chat theo task ----------
api.MapGet("/chats/{taskId:long}", (long taskId, ChatService chat) => chat.Snapshot(taskId));

api.MapPost("/chats/{taskId:long}/messages", async (long taskId, HttpRequest req, ChatService chat, CancellationToken ct) =>
{
    var (text, taskName, files) = await ReadMessage(req, ct);
    if (text.Length == 0 && files.Count == 0) return Results.BadRequest(new { error = "Tin nhắn đang trống." });
    if (chat.IsBusy(taskId)) return Results.Conflict(new { error = "AI đang trả lời tin nhắn trước, đợi một chút." });
    // Model chọn ở khung chat (trống = theo Cấu hình)
    var model = req.HasFormContentType ? req.Form["model"].ToString().Trim() : "";
    if (model.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(model, @"^claude-[a-z0-9.\-]{1,60}$"))
        return Results.BadRequest(new { error = "Model không hợp lệ." });
    var names = files.Count > 0 ? await chat.SaveFiles(taskId, files, ct) : [];
    return chat.Ask(taskId, taskName, text, names, model.Length > 0 ? model : null)
        ? Results.Ok(new { ok = true })
        : Results.Conflict(new { error = "AI đang trả lời tin nhắn trước, đợi một chút." });
});

api.MapGet("/chats/{taskId:long}/files/{name}", (long taskId, string name, ChatService chat) =>
{
    var safe = AmisClient.SafeFileName(name);
    var path = Path.Combine(chat.FilesDir(taskId), safe);
    if (safe != name || !File.Exists(path)) return Results.NotFound();
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    return Results.File(path, types.TryGetContentType(path, out var ct) ? ct : "application/octet-stream", safe);
});

api.MapPost("/chats/{taskId:long}/cancel", (long taskId, ChatService chat) =>
    chat.Cancel(taskId) ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "Không có câu trả lời nào đang chạy." }));

api.MapPost("/chats/{taskId:long}/reset", (long taskId, ChatService chat) =>
    chat.Reset(taskId) ? Results.Ok(new { ok = true }) : Results.Conflict(new { error = "AI đang trả lời, dừng trước khi bắt đầu phiên mới." }));

api.MapGet("/jobs/{id}/logs", (string id, JobStore store) =>
    store.Get(id) is null ? Results.NotFound() : Results.Ok(store.ReadLogs(id)));

var docName = new Regex(@"^[\w.-]+\.(md|json|txt|sql)$");
api.MapGet("/jobs/{id}/files/{name}", (string id, string name, JobStore store) =>
{
    if (store.Get(id) is null || !docName.IsMatch(name)) return Results.NotFound();
    var path = Path.Combine(store.Dir(id), name);
    return File.Exists(path) ? Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8") : Results.NotFound();
});

// Test case cho QC (Word): tải về với tên TestCase_<mã task>_<id AMIS>.docx
api.MapGet("/jobs/{id}/testcases.docx", (string id, JobStore store) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    var path = Path.Combine(store.Dir(id), TestCases.DocxFile);
    return File.Exists(path)
        ? Results.File(path, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", TestCases.DocxName(job, store.Dir(id)))
        : Results.NotFound();
});

// Dựng lại file test case từ testcases.json (sau khi bạn/AI sửa file JSON)
api.MapPost("/jobs/{id}/testcases/export", (string id, JobStore store, JobRunner runner) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    try
    {
        var sum = runner.ExportTestCasesNow(job, store.Dir(id));
        if (sum is null) return Results.BadRequest(new { error = "Job chưa có testcases.json (bước test chưa sinh test case)." });
        store.Save(job);
        return Results.Ok(new { summary = sum.ToString() });
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
    {
        return Results.BadRequest(new { error = "testcases.json lỗi: " + ex.Message });
    }
});

// Gửi tài liệu của job (specs/*, file Word test case) lên task cha AMIS — chỉ khi người dùng bấm gửi
api.MapGet("/jobs/{id}/amis-docs", async (string id, JobStore store, AmisDocs docs, AmisClient amis, CancellationToken ct) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    var (taskId, taskName) = AmisDocs.Target(job);
    List<(string Name, long Size)> existing;
    try { existing = await amis.TaskAttachmentNames(taskId, ct); }
    catch (AmisException ex) { return Results.BadRequest(new { error = ex.Message }); }
    return Results.Ok(new
    {
        target = new { id = taskId, name = taskName, isParent = job.ParentTaskId is not null },
        existing = existing.Select(e => e.Name),
        files = docs.Candidates(job).Select(c => new { c.Key, c.Group, c.Name, c.Size, c.Zip,
            onAmis = existing.Any(e => e.Name == c.Name || e.Name == Path.GetFileNameWithoutExtension(c.Name) + ".zip") }),
    });
});

api.MapPost("/jobs/{id}/amis-docs", async (string id, AmisDocsBody body, JobStore store, AmisDocs docs, CancellationToken ct) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    try { return Results.Ok(await docs.Send(job, body.Keys ?? [], ct)); }
    catch (Exception ex) when (ex is AmisException or PipelineException or IOException) { return Results.BadRequest(new { error = ex.Message }); }
});

// Xoá job đã dừng/huỷ/lỗi (chuyển vào data\jobs-trash, khôi phục được). Không đụng git.
api.MapDelete("/jobs/{id}", (string id, JobStore store) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    if (!JobStore.Deletable(job))
        return Results.BadRequest(new { error = job.Held ? "Job đang giữ repo (code chưa commit) — xử lý code trước rồi mới xoá." : "Chỉ xoá được job đã dừng, huỷ, lỗi hoặc bị từ chối." });
    try { store.Delete(id); }
    catch (IOException ex) { return Results.BadRequest(new { error = "Không chuyển được thư mục job vào thùng rác (file đang mở?): " + ex.Message }); }
    return Results.Ok(new { ok = true });
});

// Thùng rác job: xem và khôi phục
api.MapGet("/jobs-trash", (JobStore store) => Results.Ok(store.Trash().Select(j => new
{
    id = j.Id, j.TaskId, j.TaskName, j.ParentTaskName, status = j.Status.ToString(), j.CreatedAt, j.CostUsd, j.Branch,
    deletedAt = store.DeletedAt(j.Id),
})));

api.MapPost("/jobs-trash/{id}/restore", (string id, JobStore store) =>
{
    try { return store.Restore(id) is { } j ? Results.Ok(j) : Results.NotFound(); }
    catch (IOException ex) { return Results.BadRequest(new { error = "Không khôi phục được: " + ex.Message }); }
});

// Dọn job trùng: với mỗi task, xoá các job dừng/huỷ/lỗi cũ khi task đó còn job khác mới hơn
api.MapPost("/jobs/cleanup-duplicates", (JobStore store) =>
{
    var removed = new List<string>();
    var failed = new List<string>();
    foreach (var g in store.All().GroupBy(j => j.TaskId))
    {
        var newest = g.OrderByDescending(j => j.CreatedAt).First();
        foreach (var j in g.Where(j => j != newest && JobStore.Deletable(j)))
        {
            try { if (store.Delete(j.Id)) removed.Add(j.Id); }
            catch (IOException) { failed.Add(j.Id); }
        }
    }
    return Results.Ok(new { removed = removed.Count, failed });
});

api.MapGet("/jobs/{id}/diff", async (string id, JobStore store, JobRunner runner, CancellationToken ct) =>
{
    if (store.Get(id) is not { } job) return Results.NotFound();
    try
    {
        return Results.Text(await runner.Diff(job, ct), "text/plain; charset=utf-8");
    }
    catch (PipelineException ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

api.MapGet("/events", async (HttpContext ctx, EventHub hub) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var (subId, reader) = hub.Subscribe();
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            beat.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                if (!await reader.WaitToReadAsync(beat.Token)) break;
                while (reader.TryRead(out var msg)) await ctx.Response.WriteAsync($"data: {msg}\n\n", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Hết 20 giây không có sự kiện: gửi ping để proxy/trình duyệt giữ kết nối
                await ctx.Response.WriteAsync(": ping\n\n", ct);
            }
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Trình duyệt đóng tab/kết nối — kết thúc stream bình thường
    }
    finally
    {
        hub.Unsubscribe(subId);
    }
});

app.Run();

static async Task<List<string>> SaveInputs(Job job, IFormFileCollection files, JobStore store)
{
    var inputs = Path.Combine(store.Dir(job.Id), "inputs");
    Directory.CreateDirectory(inputs);
    var names = new List<string>();
    foreach (var f in files)
    {
        var name = SafeFileName(f.FileName);
        await using var fs = File.Create(Path.Combine(inputs, name));
        await f.CopyToAsync(fs);
        if (!job.InputFiles.Contains(name)) job.InputFiles.Add(name);
        names.Add(name);
    }
    return names;
}

static string SafeFileName(string name) => AmisClient.SafeFileName(name);

/// <summary>Gửi một câu hỏi ngắn để kiểm tra xác thực; key trong danh sách: chạy được thì bỏ tạm nghỉ, lỗi do key thì cho tạm nghỉ.</summary>
static async Task<object> TestClaude(SettingsService s, ClaudeAuth auth, string? model, CancellationToken ct)
{
    var o = s.Pipeline;
    var m = string.IsNullOrWhiteSpace(model) ? o.Model : model.Trim();
    var args = ClaudeCli.BaseArgs("default", m, [], ["Bash", "PowerShell", "Edit", "Write"]);
    args.AddRange(["--max-turns", "1"]);
    string? init = null;
    var raw = Path.Combine(Path.GetTempPath(), $"sa-test-claude-{Guid.NewGuid():N}.jsonl");
    try
    {
        var run = await ClaudeCli.Run(o.ClaudePath, o.WorkspaceRoot, args, "Chỉ trả lời đúng một từ: OK", raw,
            e => { if (e.Kind == "init") init = e.Text; }, TimeSpan.FromMinutes(2), ct, auth);
        var ok = !run.IsError && run.Result is not null && !run.TimedOut;
        if (ok) s.KeyOk(auth.KeyId);
        else if (ClaudeCli.KeyProblem(run, null) is { } p) s.KeyBad(auth.KeyId, p.Reason, p.Cooldown);
        return new
        {
            ok,
            auth = auth.Label,
            info = init,
            result = run.TimedOut ? "Quá 2 phút không phản hồi." : run.Result ?? $"Không có kết quả (exit {run.ExitCode}, {run.Subtype})",
            costUsd = run.CostUsd,
        };
    }
    catch (PipelineException ex)
    {
        return new { ok = false, auth = auth.Label, info = (string?)null, result = ex.Message, costUsd = 0.0 };
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        return new { ok = false, auth = auth.Label, info = (string?)null, result = $"Không chạy được '{o.ClaudePath}': {ex.Message}. Cài Claude Code CLI hoặc sửa ClaudePath.", costUsd = 0.0 };
    }
    finally
    {
        File.Delete(raw);
    }
}

static async Task<(string Text, string TaskName, List<IFormFile> Files)> ReadMessage(HttpRequest req, CancellationToken ct)
{
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync(ct);
        return (form["text"].ToString().Trim(), form["taskName"].ToString(), form.Files.ToList());
    }
    var body = await req.ReadFromJsonAsync<TextBody>(ct);
    return ((body?.Text ?? "").Trim(), body?.TaskName ?? "", []);
}

record SessionBody(string SessionId);
record AmisChatBody(bool Enabled, string? Conversation, bool NotifyAsk = true, bool NotifyBatch = true, bool NotifyDone = true, bool NotifyFail = true);
record AmisPostBody(string? Comment, long? TargetTaskId, bool MarkDone = false);
record TfsBody(string? Pat, bool ClearPat = false, bool? AutoPr = null);
record LessonBody(string? Id, string? Text, bool Enabled = true);
record DecisionBody(bool Approve, string? Reason, bool Push = false);
record SelfBody(string? Reason);
record KitBody(List<string>? Ids, string? Target, bool Overwrite = false);
record BatchTask(long TaskId, string? TaskName, string? ProjectName, string? EndDate);
record BatchBody(List<BatchTask>? Tasks, bool Chain = true, string? Note = null, bool Unattended = false);
record CommitBody(string? Message, bool Push, Dictionary<string, List<string>>? NewFiles, string? Decision, Dictionary<string, List<string>>? Skip = null);
record SwitchBody(string Branch);
record TextBody(string? Text, string? TaskName);
record ProceedBody(string? Mode, string? Text = null);
record TestDbBody(bool Enabled, string? Server, string? Database, string? User, string? Password, bool TrustServerCertificate,
    int MaxRows, bool AllowExec, bool KeepData, bool AnyProject, bool SyncReadDb = true, bool DeploySql = false, bool AllowAiWrite = false)
{
    public TestDbOptions ToOptions() => new()
    {
        Enabled = Enabled, Server = Server ?? "", Database = Database ?? "", User = User ?? "", Password = Password ?? "",
        TrustServerCertificate = TrustServerCertificate, MaxRows = MaxRows <= 0 ? 500 : MaxRows, AllowExec = AllowExec, KeepData = KeepData, AnyProject = AnyProject,
        SyncReadDb = SyncReadDb, DeploySql = DeploySql, AllowAiWrite = AllowAiWrite,
    };
}
record PortalStartBody(string? Project);
record DbProfileBody(string? Project, string? Template, string? AiUser, string? AiPassword);
record DbProfileDirBody(string? Dir);
record TestEnvBody(bool UiTests, string? BaseUrl, string? User, string? Password, bool BuildBeforeTest, bool RebuildAfterReturn, string? ExtraSites,
    List<TestAccountBody>? Accounts = null, bool? Enabled = null, double? MaxTestCostUsd = null, bool? AutoFixOnTestFail = null, bool? TempAccounts = null, int? MaxTestFixRounds = null, bool? AutoStartPortal = null);
record TestAccountBody(string? Role, string? User, string? Password);
record AmisDocsBody(List<string>? Keys);
record ProjectBranchBody(string? Project, string? Keywords);
record TestClaudeBody(string? AuthMode, string? ApiKey, string? Model, string? KeyId = null);
