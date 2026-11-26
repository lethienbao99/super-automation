using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SuperAutomation.Core;

/// <summary>
/// Chạy pipeline từng job một (cùng một working copy src/HRM9 nên không chạy song song):
/// đọc AMIS → tạo nhánh → code (⇄ hỏi người dùng) → review (→ sửa) → test → commit vào nhánh AI → trả nhánh → chờ duyệt.
/// Khi AI cần xác nhận, job dừng ở WaitingForUser (phần dở được commit WIP, repo trả về nhánh người dùng);
/// người dùng trả lời trong chat thì job tiếp tục đúng phiên code cũ bằng --resume.
/// Tool không bao giờ push; người duyệt tự push nhánh AI.
/// </summary>
public sealed class JobRunner(JobStore store, AmisClient amis, RepoService repo, ChatService chat, SettingsService settings, TestEnvService testEnv, TestDataService testData, CostLedger costs, Insights insights, TfsClient tfs, PortalDevRunner portalDev, ILogger<JobRunner> logger) : BackgroundService
{
    private const int MaxParallelAnalyses = 2;

    // Phân tích chỉ đọc nên chạy song song; code/review/test dùng chung working copy nên chạy từng job một
    private readonly Channel<string> _analyzeQueue = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _codeQueue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private volatile string? _codeJob;
    // Job đang được kiểm tay trên web: repo ở nhánh AI của job đó → hàng đợi code chờ tới khi bấm "Xong kiểm"
    private volatile string? _inspectJob;
    public string? InspectingJob => _inspectJob;

    private PipelineOptions O => settings.Pipeline;

    /// <summary>Có job đang dùng working copy — không được đổi nhánh lúc này.</summary>
    public bool IsBusy => _codeJob is not null;

    public void Enqueue(Job job) => (job.CodeApproved ? _codeQueue : _analyzeQueue).Writer.TryWrite(job.Id);

    public bool Cancel(string id)
    {
        var job = store.Get(id);
        if (job is null) return false;
        if (job.Status is JobStatus.Queued or JobStatus.WaitingForUser or JobStatus.NeedsInfo or JobStatus.Clarified && !_running.ContainsKey(id))
        {
            job.Status = JobStatus.Cancelled;
            job.PendingAnswer = null;
            job.FinishedAt = DateTime.Now;
            store.Log(job, "control", "stage", "Đã huỷ job.");
            store.Save(job);
            return true;
        }
        if (_running.TryGetValue(id, out var cts))
        {
            store.Log(job, "control", "info", "Người dùng yêu cầu huỷ.");
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Người dùng trả lời trong chat. Job đang chờ ở bước code (WaitingForUser) thì chạy tiếp phiên code;
    /// job đang ở bước phân tích (NeedsInfo/Clarified) thì phân tích lại với câu trả lời mới.
    /// </summary>
    public PipelineException? Answer(string id, string text, List<string>? files = null)
    {
        files ??= [];
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (job.Status is not (JobStatus.WaitingForUser or JobStatus.NeedsInfo or JobStatus.Clarified))
            return new PipelineException("Job không ở trạng thái chờ trả lời.");
        if (string.IsNullOrWhiteSpace(text) && files.Count == 0) return new PipelineException("Câu trả lời đang trống.");
        chat.Append(job.TaskId, job.TaskName, new ChatMessage { Role = "user", Text = text.Trim(), At = DateTime.Now, JobId = job.Id, Files = files });
        var reanalyze = job.Status != JobStatus.WaitingForUser;
        if (!reanalyze)
            job.PendingAnswer = (text.Trim() + (files.Count > 0 ? $"\n\n(Đính kèm, đã chép vào inputs/: {string.Join(", ", files)} — đọc hết, kể cả ảnh.)" : "")).Trim();
        job.Status = JobStatus.Queued;
        job.Error = null;
        store.Log(job, "answer", "stage", reanalyze ? "Người dùng đã trả lời — xếp hàng phân tích lại" : "Người dùng đã trả lời — xếp hàng chạy tiếp");
        store.Save(job);
        Enqueue(job);
        return null;
    }

    /// <summary>Từ cột "Cần bổ sung"/"Đã rõ yêu cầu": giao code (mode=code) hoặc phân tích lại (mode=reanalyze).</summary>
    private readonly ConcurrentDictionary<string, byte> _proceeding = new(); // job đang xử lý yêu cầu chạy tiếp — chặn bấm trùng

    public async Task<PipelineException?> Proceed(string id, string mode, CancellationToken ct, string? text = null)
    {
        if (!_proceeding.TryAdd(id, 0)) return new PipelineException("Yêu cầu chạy tiếp trước của job này vẫn đang xử lý — đợi một chút.");
        try { return await ProceedCore(id, mode, ct, text); }
        finally { _proceeding.TryRemove(id, out _); }
    }

    private async Task<PipelineException?> ProceedCore(string id, string mode, CancellationToken ct, string? text)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (job.Inspecting) return new PipelineException("Task đang được kiểm trên web — bấm \"Xong kiểm\" trước.");
        if (mode is "review" or "revise")
        {
            if (mode == "revise" && chat.IsBusy(job.TaskId))
                return new PipelineException("Chat đang trả lời — đợi xong hoặc bấm dừng rồi gửi yêu cầu sửa.");
            // "Đã duyệt" mà code vẫn đang giữ chưa commit (vd duyệt qua chat lúc commit bị ngắt) cũng làm tiếp được
            var approvedHeld = job.Status == JobStatus.Approved && job.Held;
            if ((job.Status is not (JobStatus.Failed or JobStatus.Cancelled or JobStatus.AwaitingApproval) && !approvedHeld) || job.Branch is null || (job.HeadSha is null && !job.Held))
                return new PipelineException("Chỉ chạy tiếp được job đã lỗi/huỷ/chờ duyệt sau khi AI code xong (code đã commit, hoặc đang giữ trên nhánh AI).");
            if (mode == "review" && job.HeadSha is null && !await HasChanges(job, ct))
                return new PipelineException("AI chưa sửa gì trên nhánh (bước code bị ngắt trước khi có thay đổi) — bấm \"Chạy lại bước code\".");
            if (store.All().Any(j => j.TaskId == job.TaskId && j.Id != job.Id && !j.IsFinal))
                return new PipelineException("Task này đang có job khác chưa kết thúc.");
            // Job đang giữ repo thì làm tiếp ngay trên working tree; còn lại repo phải sạch để chuyển sang nhánh AI
            if (!job.Held)
                foreach (var (rel, path) in new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))))
                {
                    var dirty = await repo.Dirty(rel, path, ct);
                    if (dirty.Count > 0)
                        return new PipelineException($"{rel} đang có {dirty.Count} file sửa chưa commit — commit hoặc stash trước khi chạy tiếp.");
                }
            if (job.TestDataPending && mode == "revise" && !testData.Options.KeepData)
            {
                // Sửa thêm: bước dữ liệu soạn lại từ đầu → dọn dữ liệu lần trước để không bị trùng.
                // "Test lại" thì KHÔNG dọn: bước dữ liệu dùng lại chính dữ liệu đó (dọn rồi là mất dữ liệu cần test, lại tốn thời gian khoá DB).
                var clean = await CleanupTestData(job);
                if (!clean.Ok) return new PipelineException($"Chưa dọn được dữ liệu test của lần trước: {clean.Message}");
            }
            job.ResumeFrom = mode;
            job.CodeApproved = true;
            job.Status = JobStatus.Queued;
            if (mode == "revise")
            {
                var req = (text ?? "").Trim();
                job.Revision = req.Length > 0 ? req : "(Không ghi thêm — làm theo những gì đã chốt ở cuối chat.md.)";
                // Ghi vào chat để chat.md (và các bước review/test) thấy yêu cầu này
                if (req.Length > 0) insights.Suggest(job, "revise", req);
                if (req.Length > 0)
                    chat.Append(job.TaskId, job.TaskName, new ChatMessage { Role = "user", Text = "Yêu cầu sửa thêm cho job:\n" + req, At = DateTime.Now, JobId = job.Id });
                store.Log(job, "control", "stage", "Người dùng yêu cầu sửa thêm: " + ClaudeCli.Trunc(job.Revision, 300));
            }
            else store.Log(job, "control", "stage", "Người dùng cho chạy tiếp: review → test");
            store.Save(job);
            Enqueue(job);
            return null;
        }
        if (mode == "recode")
        {
            if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled) || job.HeadSha is not null || (job.Branch is null && !job.CodeApproved))
                return new PipelineException("Chỉ chạy lại bước code cho job bị huỷ/lỗi khi AI chưa code xong (chưa có commit trên nhánh AI).");
            if (store.All().Any(j => j.TaskId == job.TaskId && j.Id != job.Id && !j.IsFinal))
                return new PipelineException("Task này đang có job khác chưa kết thúc.");
            if (!job.Held)
                foreach (var (rel, path) in new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))))
                {
                    var dirty = await repo.Dirty(rel, path, ct);
                    if (dirty.Count > 0)
                        return new PipelineException($"{rel} đang có {dirty.Count} file sửa chưa commit — commit hoặc stash trước khi chạy lại.");
                }
            job.ResumeFrom = job.Branch is null ? null : "recode"; // chưa có nhánh (lỗi trước khi tạo nhánh): code từ đầu, dùng lại bản phân tích
            job.Error = null;
            job.CodeApproved = true;
            job.Status = JobStatus.Queued;
            store.Log(job, "control", "stage", job.CodeSessionId is not null
                ? "Người dùng cho chạy tiếp bước code (làm tiếp phiên AI bị ngắt)"
                : "Người dùng cho chạy lại bước code trên nhánh cũ");
            store.Save(job);
            Enqueue(job);
            return null;
        }
        if (job.Status is not (JobStatus.NeedsInfo or JobStatus.Clarified))
            return new PipelineException("Job không ở bước phân tích yêu cầu.");
        if (mode == "code")
        {
            // Báo sớm thay vì để job lỗi ở bước git: repo bẩn thì job vẫn nằm ở cột hiện tại
            foreach (var (rel, path) in ConfiguredRepos())
            {
                var dirty = await repo.Dirty(rel, path, ct);
                if (dirty.Count > 0)
                    return new PipelineException($"{rel} đang có {dirty.Count} file sửa chưa commit ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", …" : "")}). " +
                                                 "Commit hoặc stash trước khi cho AI code — nếu là file cấu hình môi trường luôn sửa local, khai báo ở ⚙ Cấu hình › File cấu hình local.");
            }
            job.CodeApproved = true;
            store.Log(job, "control", "stage", job.Requirement == "CLEAR"
                ? "Người dùng cho bắt đầu code"
                : "Người dùng giao code dù yêu cầu còn điểm cần bổ sung — AI theo phương án đề xuất trong bản phân tích");
        }
        else if (mode == "reanalyze")
            store.Log(job, "control", "stage", "Người dùng yêu cầu phân tích lại");
        else return new PipelineException("mode phải là code hoặc reanalyze.");
        job.Status = JobStatus.Queued;
        job.Error = null;
        store.Save(job);
        Enqueue(job);
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Tool khởi động lại giữa lúc đang kiểm trên web: repo vẫn ở nhánh AI → giữ hàng đợi dừng tới khi bấm "Xong kiểm"
        _inspectJob = store.All().FirstOrDefault(j => j.Inspecting)?.Id;
        await RecoverUncommitted();
        await Task.WhenAll(AnalyzeLoop(stop), CodeLoop(stop));
    }

    /// <summary>
    /// Tool tắt đúng lúc job vừa xong pipeline đang commit vào nhánh AI (job chờ duyệt/đã duyệt nhưng chưa có commit, repo còn ở nhánh AI
    /// với thay đổi chưa commit) → đánh dấu job giữ repo để người dùng commit được ("Tạo commit" / "#id push"), không để code AI trôi nổi.
    /// </summary>
    private async Task RecoverUncommitted()
    {
        foreach (var j in store.All().Where(j => !j.Held && j.HeadSha is null && j.Branch is not null
                                                 && j.Status is JobStatus.AwaitingApproval or JobStatus.Approved).ToList())
        {
            try
            {
                if (!await OnAiBranch(j) || !await HasChanges(j, CancellationToken.None)) continue;
            }
            catch (PipelineException) { continue; }
            j.Held = true;
            store.Log(j, "git", "error", $"Code AI vẫn CHƯA commit trên nhánh {j.Branch} (tool bị tắt giữa lúc commit) — " +
                                         $"bấm \"Tạo commit\" trong tool, hoặc gõ trong nhóm chat: #{j.TaskId} push (commit + push + tạo PR) / #{j.TaskId} commit.");
            store.Save(j);
        }
    }

    private async Task AnalyzeLoop(CancellationToken stop)
    {
        using var slots = new SemaphoreSlim(MaxParallelAnalyses);
        var tasks = new List<Task>();
        await foreach (var id in _analyzeQueue.Reader.ReadAllAsync(stop))
        {
            var job = store.Get(id);
            if (job is null || job.Status != JobStatus.Queued || job.CodeApproved) continue;
            await slots.WaitAsync(stop);
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Task.Run(async () =>
            {
                try { await Execute(job, Analyze, stop); }
                finally { slots.Release(); }
                MaybeRetry(job, stop);
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);
    }

    private async Task CodeLoop(CancellationToken stop)
    {
        await foreach (var id in _codeQueue.Reader.ReadAllAsync(stop))
        {
            var job = store.Get(id);
            if (job is null || job.Status != JobStatus.Queued || !job.CodeApproved) continue;
            // Người dùng đang kiểm tay một job khác trên web (repo ở nhánh AI của job đó) → chờ
            while (_inspectJob is not null && !stop.IsCancellationRequested) await Task.Delay(3000, stop);
            // Job trước còn giữ code chưa commit trên nhánh AI (chờ bạn xem rồi bấm "Tạo commit") → task này CHỜ trong hàng đợi,
            // không chạy rồi lỗi "đang giữ repo". Bạn commit / từ chối / tự chuyển nhánh là nó chạy tiếp.
            var waitedFor = (string?)null;
            while (!stop.IsCancellationRequested && job.Status == JobStatus.Queued)
            {
                await ReleaseStaleHolds();
                if (store.All().FirstOrDefault(j => j.Held && j.Id != job.Id) is not { } holder) break;
                if (waitedFor != holder.Id)
                {
                    waitedFor = holder.Id;
                    store.Log(job, "queue", "info", $"Chờ task #{holder.TaskId} — code của task đó đang chờ bạn xem trên nhánh {holder.Branch} (chưa commit). " +
                                                    "Bấm \"Tạo commit\", \"Từ chối\" hoặc \"Tôi tự làm\" ở task đó là task này chạy tiếp.");
                    store.Save(job);
                }
                await Task.Delay(5000, stop);
            }
            if (job.Status != JobStatus.Queued) continue;
            _codeJob = id;
            try { await Execute(job, Run, stop); }
            finally { _codeJob = null; }
            MaybeRetry(job, stop);
        }
    }

    private async Task Execute(Job job, Func<Job, CancellationToken, Task> step, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[job.Id] = cts;
        try
        {
            await step(job, cts.Token);
        }
        catch (Exception ex)
        {
            // Bước đã tự xử lý lỗi của job; tới đây là lỗi ngoài dự kiến — ghi log để vòng lặp phục vụ job tiếp theo
            logger.LogError(ex, "Job {Id} lỗi ngoài dự kiến", job.Id);
        }
        finally
        {
            _running.TryRemove(job.Id, out _);
        }
    }

    /// <summary>
    /// Đọc AMIS (cả task cha), trích nội dung tệp, rồi cho Claude (chỉ đọc) đánh giá yêu cầu.
    /// Kết quả: Clarified (đã rõ) hoặc NeedsInfo (thiếu — câu hỏi đưa vào chat). Không đụng git.
    /// </summary>
    private async Task Analyze(Job job, CancellationToken ct)
    {
        var dir = store.Dir(job.Id);
        job.StartedAt ??= DateTime.Now;
        try
        {
            job.AnalyzeRounds++;
            if (job.QcFix && job.AnalyzeRounds == 1)
                store.Log(job, "analyze", "info", "🐞 Task sửa lỗi QC — đọc lỗi từ bình luận và file test case của task cha, chạy theo luồng sửa lỗi (không làm lại tính năng).");
            SetStatus(job, JobStatus.Analyzing, "analyze", job.AnalyzeRounds == 1
                ? "Đọc task (kèm task cha, tệp đính kèm) từ AMIS và phân tích yêu cầu"
                : $"Phân tích lại yêu cầu (lần {job.AnalyzeRounds})");
            var bundle = await amis.GetTaskBundle(job.TaskId, ct);
            job.TaskName = AmisClient.Str(bundle.Detail, "TaskName") ?? job.TaskName;
            job.ProjectName = AmisClient.Str(bundle.Detail, "ProjectName") ?? job.ProjectName;
            SetParent(job, bundle);
            try
            {
                job.AnalyzedBranch = (await Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
                store.Log(job, "analyze", "info", $"Phân tích trên nhánh {job.AnalyzedBranch} của {O.RepoRelativePath}");
            }
            catch (PipelineException ex)
            {
                store.Log(job, "analyze", "stderr", $"Không đọc được nhánh hiện tại: {ex.Message}");
            }
            await DownloadAmisFiles(job, bundle, dir, "analyze", ct);
            SyncChatFiles(job, dir);
            foreach (var note in DocExtract.ExtractAll(Path.Combine(dir, "inputs"), job.InputFiles))
                store.Log(job, "analyze", "info", note);
            TaskBrief.Write(bundle, dir, job.Note, job.InputFiles);
            ExportChat(job, dir);
            WriteRelated(job, dir);

            var analysis = Path.Combine(dir, "analysis.md");
            if (File.Exists(analysis)) File.Copy(analysis, Path.Combine(dir, "analysis.prev.md"), overwrite: true);
            var stage = job.AnalyzeRounds == 1 ? "analyze" : $"analyze{job.AnalyzeRounds}";
            var result = await Claude(job, stage, Prompts.Analyze(job, dir, O), ct, readOnly: true);
            job.Requirement = Marker(result, "REQUIREMENT") ?? "UNCLEAR";
            var doc = StripMarker(result, "REQUIREMENT");
            await File.WriteAllTextAsync(analysis, doc, new UTF8Encoding(false), CancellationToken.None);
            job.PlannedFiles = PlannedFiles(Section(doc, "Phạm vi") ?? doc);
            if (job.QcFix && Section(doc, "Trả lời QC") is { } qr) job.QcReply = qr.Trim();
            var ba = Section(doc, "Tin nhắn gửi BA")?.Trim();
            job.BaMessage = job.Requirement == "CLEAR" || string.IsNullOrWhiteSpace(ba) || ba.StartsWith("(không có") ? null : ba;
            if (job.PlannedFiles.Count > 0)
                store.Log(job, "analyze", "info", $"File dự kiến sửa ({job.PlannedFiles.Count}): {Trunc(string.Join(", ", job.PlannedFiles.Select(Path.GetFileName)), 400)}");

            var autoCode = O.AutoCodeWhenClear || job.Batch;
            if (job.Requirement == "CLEAR")
            {
                chat.Append(job.TaskId, job.TaskName, new ChatMessage
                {
                    Role = "pipeline", At = DateTime.Now, JobId = job.Id,
                    Text = "Mình đã phân tích xong: yêu cầu đủ rõ để code. Bản phân tích ở tab Tài liệu › Phân tích. " +
                           (autoCode ? $"Tool sẽ tự chuyển sang code{(job.Batch ? " (chạy hàng loạt)" : "")}." : "Bấm \"Bắt đầu code\" khi bạn đồng ý, hoặc trao đổi thêm ở đây."),
                });
                if (autoCode)
                {
                    job.CodeApproved = true;
                    SetStatus(job, JobStatus.Queued, "analyze", "Yêu cầu đã rõ — tự xếp hàng code");
                    Enqueue(job);
                }
                else SetStatus(job, JobStatus.Clarified, "analyze", "Yêu cầu đã rõ — xem bản phân tích rồi bấm \"Bắt đầu code\"");
            }
            else if (job.Unattended)
            {
                var missing = Section(doc, "Cần bổ sung") ?? "";
                AppendAssumptions(dir, $"Bước phân tích (lần {job.AnalyzeRounds}) — yêu cầu chưa rõ, AI làm theo phương án đề xuất", missing);
                chat.Append(job.TaskId, job.TaskName, new ChatMessage
                {
                    Role = "pipeline", At = DateTime.Now, JobId = job.Id,
                    Text = $"Yêu cầu còn điểm chưa rõ, nhưng task chạy qua đêm nên AI làm theo phương án đề xuất. Các điểm này nằm ở tab Tài liệu › Giả định — xem lại trước khi duyệt:\n\n{missing}",
                });
                job.CodeApproved = true;
                SetStatus(job, JobStatus.Queued, "analyze", "Yêu cầu chưa rõ — chạy qua đêm: làm theo phương án đề xuất (xem Giả định)");
                Enqueue(job);
            }
            else
            {
                var missing = Section(doc, "Cần bổ sung") ?? doc;
                chat.Append(job.TaskId, job.TaskName, new ChatMessage
                {
                    Role = "pipeline", At = DateTime.Now, JobId = job.Id,
                    Text = $"Yêu cầu chưa đủ để code (phân tích lần {job.AnalyzeRounds}). Cần bạn bổ sung:\n\n{missing}\n\n" +
                           "Trả lời ở đây rồi bấm \"Gửi & phân tích lại\", hoặc upload tài liệu còn thiếu ở phần chi tiết job.",
                });
                SetStatus(job, JobStatus.NeedsInfo, "analyze", "Yêu cầu còn thiếu — trả lời câu hỏi trong Chat hoặc bổ sung tài liệu");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "Đã huỷ.";
        }
        catch (Exception ex) when (ex is PipelineException or AmisException or HttpRequestException)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            store.Log(job, "error", "error", ex.Message);
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            store.Log(job, "error", "error", ex.ToString());
        }
        finally
        {
            if (job.IsFinal) job.FinishedAt = DateTime.Now;
            store.Save(job);
        }
    }

    private const long MaxAmisFileBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Tải tệp đính kèm AMIS (task, task cha, bình luận) vào inputs/ để AI đọc. Tệp đã có cùng kích thước thì bỏ qua;
    /// tệp lỗi chỉ ghi log — bước phân tích sẽ tự xin người dùng upload nếu thiếu tài liệu.
    /// </summary>
    private async Task DownloadAmisFiles(Job job, TaskBundle bundle, string dir, string stage, CancellationToken ct)
    {
        var inputs = Path.Combine(dir, "inputs");
        Directory.CreateDirectory(inputs);
        foreach (var f in AmisClient.Files(bundle))
        {
            var name = AmisClient.SafeFileName(f.FileName);
            var path = Path.Combine(inputs, name);
            if (f.Size > MaxAmisFileBytes)
            {
                store.Log(job, stage, "info", $"Bỏ qua {name} ({f.Size / 1024 / 1024} MB, quá giới hạn {MaxAmisFileBytes / 1024 / 1024} MB) — tải tay nếu cần.");
                continue;
            }
            if (File.Exists(path) && (f.Size is null || new FileInfo(path).Length == f.Size))
            {
                if (!job.InputFiles.Contains(name)) job.InputFiles.Add(name);
                continue;
            }
            try
            {
                var bytes = await amis.Download(f.AttachmentId, ct);
                await File.WriteAllBytesAsync(path, bytes, ct);
                if (!job.InputFiles.Contains(name)) job.InputFiles.Add(name);
                store.Log(job, stage, "info", $"Đã tải từ AMIS ({f.Source}): {name} ({bytes.Length / 1024.0:N0} KB)");
            }
            catch (Exception ex) when (ex is AmisException or HttpRequestException or IOException)
            {
                store.Log(job, stage, "stderr", $"Không tải được {name} từ AMIS: {ex.Message}");
            }
        }
        store.Save(job);
    }

    /// <summary>Nội dung một mục "## {title}" trong markdown (tới mục ## kế tiếp).</summary>
    private static string? Section(string md, string title)
    {
        var m = Regex.Match(md, $@"(?ms)^##\s*{Regex.Escape(title)}[^\n]*\n(.*?)(?=^##\s|\z)");
        var text = m.Success ? m.Groups[1].Value.Trim() : "";
        return text.Length > 0 ? text : null;
    }

    private async Task Run(Job job, CancellationToken ct)
    {
        if (job.ResumeFrom == "merge") { await MergeResolve(job, ct); return; }
        var dir = store.Dir(job.Id);
        job.StartedAt ??= DateTime.Now;
        var resuming = job.PendingAnswer is not null && job.Branch is not null;
        var fromReview = job.ResumeFrom is "review" or "revise" && job.Branch is not null;
        var revise = fromReview && job.ResumeFrom == "revise";
        var recode = job.ResumeFrom == "recode" && job.Branch is not null;
        string? autoApprove = null; // lý do tự duyệt (null = chờ người duyệt)
        try
        {
            string code;
            if (fromReview)
            {
                // Code đã xong và đã commit vào nhánh AI ở lần chạy trước — chỉ làm lại review → test
                await ResumeGit(job, ct);
                ExportChat(job, dir);
                EnsureParent(job);
                job.ResumeFrom = null;
                job.Error = null;
                job.FinishedAt = null;
                job.FixRounds = 0;
                job.ReviewVerdict = null;
                job.TestVerdict = null;
                job.TestFixRounds = 0; // mỗi lần bạn yêu cầu sửa/test lại: được lại đủ số vòng tự sửa theo test
                job.TestFixPrevFails = null;
                if (revise)
                {
                    job.Revisions++;
                    var request = job.Revision ?? "";
                    job.Revision = null;
                    SetStatus(job, JobStatus.Coding, "code", $"AI sửa thêm theo yêu cầu của bạn (lần {job.Revisions}) trên nhánh {job.Branch}");
                    code = await Claude(job, $"revise{job.Revisions}", Prompts.Revise(job, dir, O, request), ct);
                }
                else
                {
                    store.Log(job, "control", "stage", $"Chạy tiếp từ bước review trên nhánh {job.Branch} (bỏ qua bước code đã xong)");
                    code = "STATUS: DONE";
                }
            }
            else if (recode)
            {
                // Bước code bị huỷ/lỗi giữa chừng: làm tiếp phiên cũ nếu còn, không thì code lại trên đúng nhánh đó
                await ResumeGit(job, ct);
                ExportChat(job, dir);
                job.ResumeFrom = null;
                job.Error = null;
                job.FinishedAt = null;
                job.CodeStatus = null;
                if (job.CodeSessionId is not null)
                {
                    SetStatus(job, JobStatus.Coding, "code", $"AI làm tiếp phần code bị ngắt trên nhánh {job.Branch}");
                    code = await Claude(job, $"code-cont{job.QuestionRounds}", Prompts.Resume(job, dir, O,
                        "(Không phải câu trả lời — phiên trước bị ngắt giữa chừng do người dùng huỷ hoặc lỗi kỹ thuật.) " +
                        "Phần đã sửa (nếu có) vẫn còn trên nhánh. Kiểm lại phần đã làm, làm tiếp phần còn dở tới xong, chạy ship-check" + Prompts.NoReviewer + "."), ct, resume: job.CodeSessionId);
                }
                else
                {
                    SetStatus(job, JobStatus.Coding, "code", $"AI code lại trên nhánh {job.Branch} (lần trước bị ngắt trước khi có kết quả)");
                    code = await Claude(job, "code", Prompts.Code(job, dir, O), ct, captureSession: true);
                }
            }
            else if (resuming)
            {
                await ResumeGit(job, ct);
                ExportChat(job, dir);
                SetStatus(job, JobStatus.Coding, "code", "AI đọc câu trả lời và làm tiếp");
                var answer = job.PendingAnswer!;
                job.PendingAnswer = null;
                code = await Claude(job, $"code-r{job.QuestionRounds}", Prompts.Resume(job, dir, O, answer), ct, resume: job.CodeSessionId);
            }
            else
            {
                // Thường đã có task.md từ bước phân tích; chỉ đọc lại AMIS khi chưa có
                if (!File.Exists(Path.Combine(dir, "task.md")))
                {
                    SetStatus(job, JobStatus.Fetching, "fetch", "Đọc task, bình luận và tệp đính kèm từ AMIS");
                    var bundle = await amis.GetTaskBundle(job.TaskId, ct);
                    job.TaskName = AmisClient.Str(bundle.Detail, "TaskName") ?? job.TaskName;
                    job.ProjectName = AmisClient.Str(bundle.Detail, "ProjectName") ?? job.ProjectName;
            SetParent(job, bundle);
                    await DownloadAmisFiles(job, bundle, dir, "fetch", ct);
                    DocExtract.ExtractAll(Path.Combine(dir, "inputs"), job.InputFiles);
                    var brief = TaskBrief.Write(bundle, dir, job.Note, job.InputFiles);
                    store.Log(job, "fetch", "info", $"Đã tạo tài liệu yêu cầu {brief}");
                }
                ExportChat(job, dir);
                WriteRelated(job, dir); // task anh em có thể vừa được BA trả lời sau lúc phân tích

                await PrepareGit(job, ct);

                SetStatus(job, JobStatus.Coding, "code", "AI đọc yêu cầu và code");
                code = await Claude(job, "code", Prompts.Code(job, dir, O), ct, captureSession: true);
            }

            var prevCodeStatus = job.CodeStatus;
            job.CodeStatus = Marker(code, "STATUS") ?? "UNKNOWN";
            while (job.CodeStatus == "NEED_INPUT" && job.Unattended && job.AutoAnswers < 2)
            {
                job.AutoAnswers++;
                var qFile = Path.Combine(dir, "questions.md");
                var asked = StripMarker(code, "STATUS");
                if (asked.Length < 40 && File.Exists(qFile)) asked = File.ReadAllText(qFile).Trim();
                AppendAssumptions(dir, $"Khi code — AI hỏi (lần {job.AutoAnswers}), tool trả lời: chọn phương án đề xuất", asked);
                store.Log(job, "code", "stage", $"Chạy qua đêm: AI cần hỏi — tự trả lời \"chọn phương án đề xuất\" (lần {job.AutoAnswers}), câu hỏi lưu ở Giả định.");
                code = await Claude(job, $"code-auto{job.AutoAnswers}", Prompts.Resume(job, dir, O,
                    $"(Chạy qua đêm — không có người trả lời.) Với từng câu hỏi: chọn phương án bạn đề xuất, ghi vào {dir}\\assumptions.md " +
                    "(câu hỏi, phương án đã chọn, lý do, nếu chọn khác thì sửa ở đâu) rồi làm tiếp tới xong. Không hỏi lại."), ct, resume: job.CodeSessionId);
                job.CodeStatus = Marker(code, "STATUS") ?? "UNKNOWN";
            }
            if (job.CodeStatus == "NEED_INPUT")
            {
                AskUser(job, dir, code);
                return; // finally commit WIP và trả nhánh
            }
            if (job.CodeStatus == "BLOCKED")
                throw new PipelineException("AI không làm tiếp được — xem tóm tắt trong log/code.result.md.");
            if (!await HasChanges(job, ct) && job.HeadSha is null)
            {
                // Chạy tiếp từ review không có phiên code mới: "DONE" chỉ là giả định — trả lại trạng thái code thật
                if (fromReview && !revise) job.CodeStatus = prevCodeStatus;
                throw new PipelineException("AI không tạo ra thay đổi nào trong repo.");
            }

            string? testFixNote = null; // vòng tự sửa theo test FAIL: ghi chú cho lần test lại
            for (var pass = 0; ; pass++)
            {
            // "Test lại" (không yêu cầu sửa) mà code y như lúc review gần nhất → giữ kết luận review, khỏi review lại
            var skipReview = false;
            if (pass == 0 && fromReview && !revise && job.ReviewedVerdict is { } rv && job.ReviewedFingerprint is { } fp0
                && await CodeFingerprint(job, ct) == fp0)
            {
                job.ReviewVerdict = rv;
                skipReview = true;
                store.Log(job, "review", "info", $"Code không đổi kể từ lần review gần nhất — giữ kết luận review {rv}, bỏ qua review (chỉ chạy test).");
            }
            var reviewFixed = false; // lượt review kế tiếp là sau vòng sửa theo review (trong lượt chạy này)
            while (!skipReview)
            {
                SetStatus(job, JobStatus.Reviewing, "review", pass > 0 ? "Review lại sau khi sửa theo test" : reviewFixed ? $"Review lại phần vừa sửa (vòng {job.FixRounds})" : "Review độc lập (vnr-reviewer, soat-slop)");
                var reviewStage = pass > 0 ? $"review-tf{pass}" : job.FixRounds == 0 ? "review" : $"review{job.FixRounds + 1}";
                // Sau vòng sửa theo review: chỉ soát phần vừa sửa (reviewfix.diff) — phần còn lại đã được review ở lượt trước
                var light = false;
                if (pass == 0 && !reviewFixed)
                {
                    var (full, why) = await ChooseReview(job, ct);
                    light = !full;
                    store.Log(job, "review", "info", (full ? "Review đầy đủ (có vnr-reviewer): " : "Review nhẹ (không gọi vnr-reviewer): ") + why);
                    if (light) SetStatus(job, JobStatus.Reviewing, "review", "Review nhẹ (" + why + ")");
                }
                var reviewPrompt = pass > 0 && File.Exists(Path.Combine(dir, "testfix.diff")) ? Prompts.ReviewDelta(job, dir, O)
                    : reviewFixed ? Prompts.ReviewFixDelta(job, dir, O)
                    : light ? Prompts.ReviewLight(job, dir, O)
                    : Prompts.Review(job, dir, O);
                var review = await Claude(job, reviewStage, reviewPrompt, ct);
                // Review nhẹ thấy điểm đáng ngờ vượt sức một lượt nhẹ → chạy review đầy đủ ngay
                if (light && Marker(review, "VERDICT") == "ESCALATE")
                {
                    store.Log(job, "review", "info", "Review nhẹ đề nghị nâng lên review đầy đủ — chạy vnr-reviewer.");
                    SetStatus(job, JobStatus.Reviewing, "review", "Review đầy đủ (review nhẹ đề nghị nâng)");
                    reviewStage += "-full";
                    review = await Claude(job, reviewStage, Prompts.Review(job, dir, O), ct);
                }
                review = await EnsureMarker(job, reviewStage, review, "VERDICT",
                    $"Phiên review vừa kết thúc mà CHƯA có kết luận (thiếu dòng VERDICT). Nếu đang chờ agent vnr-reviewer thì lấy kết quả của nó " +
                    $"(hoặc tự review nốt phần còn thiếu, không chạy nền), ghi {Path.Combine(dir, "review.md")} đủ ba mục, rồi kết thúc bằng đúng một dòng VERDICT: PASS hoặc VERDICT: FAIL.", ct);
                job.ReviewVerdict = Marker(review, "VERDICT") ?? "UNKNOWN";
                store.Log(job, "review", "info", $"Kết luận review: {job.ReviewVerdict}");
                if (job.ReviewVerdict != "FAIL" || job.FixRounds >= O.MaxFixRounds)
                {
                    // Ghi lại trạng thái code vừa được review (sau đó không còn vòng sửa nào trong lượt này)
                    job.ReviewedFingerprint = await CodeFingerprint(job, ct);
                    job.ReviewedVerdict = job.ReviewVerdict;
                    break;
                }
                job.FixRounds++;
                SetStatus(job, JobStatus.Fixing, "fix", $"Sửa theo review (vòng {job.FixRounds})");
                var fixSnaps = await SnapshotRepos(job, ct);
                await Claude(job, $"fix{job.FixRounds}", Prompts.Fix(job, dir, O), ct);
                await WriteDeltaDiff(job, fixSnaps, Path.Combine(dir, "reviewfix.diff"), ct);
                reviewFixed = true;
            }

            var testMode = TestModeFor(job);
            if (testMode == "off")
                store.Log(job, "test", "info", "Bỏ qua bước test (chế độ test: tắt" + (job.TestMode is null ? " trong Cấu hình › Test" : " cho task này") + ").");
            else
            {
                // Deploy store của nhánh AI lên DB test trước khi build/gọi thử web — web chạy đúng code mới cả C# lẫn SQL
                var sqlNote = await PrepareSql(job, dir, ct);
                var (context, envVars) = await PrepareTest(job, dir, ct);
                // Chạy lại sau review/sửa thêm: giữ kết quả test lần trước để AI chỉ kiểm lại phần bị ảnh hưởng
                var prevTest = Path.Combine(dir, "test.md");
                var rerun = File.Exists(prevTest);
                if (rerun) File.Copy(prevTest, Path.Combine(dir, "test.prev.md"), overwrite: true);
                context = context with
                {
                    DataNote = await PrepareTestData(job, dir, ct, reuse: rerun && (!revise || (job.TestDataPending && testData.Options.KeepData))),
                    Rerun = rerun,
                    Revision = testFixNote ?? (revise ? job.Revision : null),
                    SqlNote = sqlNote,
                    AiDbWrite = AiWriteEnabled,
                };
                SetStatus(job, JobStatus.Testing, "test", context.UiTests && context.HasCredentials && context.BuildOk
                    ? "AI viết và chạy test giao diện (Playwright) + kiểm dữ liệu"
                    : "AI kiểm thử (không có test giao diện — xem lý do trong test.md)");
                var budget = testEnv.Options.MaxTestCostUsd;
                context = context with { Budget = budget, Mode = testMode, TempAccounts = testEnv.Options.TempAccounts };
                string test;
                try
                {
                    test = await Claude(job, pass > 0 ? $"test-tf{pass}" : "test", Prompts.Test(job, dir, O, context), ct, extraEnv: envVars, stageBudget: budget > 0 ? budget : null);
                }
                catch (StageBudgetException)
                {
                    test = await FinishTestOverBudget(job, dir, budget, envVars, ct);
                }
                test = await EnsureMarker(job, "test", test, "TEST",
                    $"Phiên test vừa kết thúc mà CHƯA có kết luận (thiếu dòng TEST). Làm nốt phần đang dở (không chạy nền), ghi {Path.Combine(dir, "test.md")}, " +
                    "rồi kết thúc bằng đúng một dòng TEST: PASS, TEST: PARTIAL, TEST: FAIL hoặc TEST: SKIPPED.", ct, envVars);
                job.TestVerdict = Marker(test, "TEST") ?? "UNKNOWN";
                store.Log(job, "test", "info", $"Kết luận test: {job.TestVerdict}");
                await ExportTestCases(job, dir, ct, envVars);
            }

            // Test FAIL vì lỗi code → AI tự tái hiện + sửa 1 vòng rồi review và test lại (chỉ các test case đã FAIL)
            testFixNote = await TryFixFromTest(job, dir, testMode, ct);
            if (testFixNote is null) break;
            }

            await ComputeDiffStat(job, ct);
            if (job.QcFix && File.Exists(Path.Combine(dir, "qc-reply.md")))
                job.QcReply = (await File.ReadAllTextAsync(Path.Combine(dir, "qc-reply.md"), ct)).Trim();
            // Bạn vừa yêu cầu sửa thêm thì luôn để bạn tự xem lại kết quả
            autoApprove = revise ? null : AutoApproveReason(job, dir);
            SetStatus(job, JobStatus.AwaitingApproval, "done", "Xong pipeline — chờ người duyệt");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "Đã huỷ.";
        }
        catch (Exception ex) when (ex is PipelineException or AmisException or HttpRequestException)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            store.Log(job, "error", "error", ex.Message);
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            store.Log(job, "error", "error", ex.ToString());
        }
        finally
        {
            // Job lỗi/huỷ hoặc xong chờ duyệt: dọn theo cấu hình (mặc định giữ để bạn tự dọn)
            await AutoCleanup(job);
            if (job.Status == JobStatus.Cancelled && job.SqlDeployed.Count > 0) await RestoreSql(job);
            try
            {
                // Chạy hàng loạt: không giữ repo (task sau phải dùng được), luôn commit vào nhánh AI rồi trả nhánh
                // Chỉ giữ khi AI thật sự đã sửa gì đó — huỷ lúc AI mới đọc code thì trả nhánh luôn, không giữ repo
                if (autoApprove is not null && job.Status == JobStatus.AwaitingApproval && job.Branch is not null)
                {
                    // Thay đổi nhỏ, review/test PASS: commit vào nhánh AI (không push) rồi đánh dấu đã duyệt
                    await FinalizeGit(job);
                    job.Status = JobStatus.Approved;
                    job.AutoApproved = true;
                    store.Log(job, "decision", "stage", $"Tự duyệt (thay đổi nhỏ): {autoApprove}. Đã commit vào nhánh {job.Branch} — CHƯA push, bạn tự push khi muốn.");
                    await AutoCleanup(job);
                }
                else if (O.ConfirmCommit && !job.Batch && job.Branch is not null && await OnAiBranch(job)
                    && (job.HeadSha is not null || await HasChanges(job, CancellationToken.None)))
                {
                    // Không tự commit: giữ code trên nhánh AI để người dùng xem diff, kiểm tay rồi bấm "Tạo commit"
                    job.Held = true;
                    store.Log(job, "git", "stage", $"Giữ thay đổi CHƯA COMMIT trên nhánh {job.Branch} — xem lại rồi bấm \"Tạo commit & push\" (repo bị giữ tới lúc đó).");
                }
                else await FinalizeGit(job);
            }
            catch (Exception ex) when (ex is PipelineException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                job.Error = (job.Error is null ? "" : job.Error + " | ") + "Lỗi git khi kết thúc: " + ex.Message;
                store.Log(job, "git", "error", ex.Message);
                // Commit dở: code AI vẫn nằm chưa commit trên nhánh AI → giữ repo để người dùng "Tạo commit"/"#id push", không để trôi nổi
                try
                {
                    if (!job.Held && job.Branch is not null && await OnAiBranch(job) && await HasChanges(job, CancellationToken.None))
                    {
                        job.Held = true;
                        store.Log(job, "git", "error", $"Code AI vẫn CHƯA commit trên nhánh {job.Branch} — bấm \"Tạo commit\" trong tool, hoặc gõ #{job.TaskId} push / #{job.TaskId} commit.");
                    }
                }
                catch (PipelineException) { }
            }
            try { await CheckOverlaps(job); }
            catch (PipelineException ex) { store.Log(job, "git", "stderr", "Không kiểm được file trùng với task khác: " + ex.Message); }
            if (job.IsFinal) job.FinishedAt = DateTime.Now;
            store.Save(job);
        }
    }

    /// <summary>
    /// related.md: các task anh em (cùng task cha) tool đã làm — trỏ tới bản phân tích, trao đổi, giả định của chúng,
    /// để AI dùng lại câu trả lời BA đã cho thay vì hỏi lại. Không có task anh em thì xoá file.
    /// </summary>
    private void WriteRelated(Job job, string dir)
    {
        var path = Path.Combine(dir, "related.md");
        var sibs = job.ParentTaskId is > 0
            ? store.All().Where(o => o.TaskId != job.TaskId && o.ParentTaskId == job.ParentTaskId)
                .GroupBy(o => o.TaskId).Select(g => g.OrderByDescending(o => o.CreatedAt).First())
                .OrderByDescending(o => o.CreatedAt).Take(10).ToList()
            : [];
        if (sibs.Count == 0) { try { File.Delete(path); } catch (IOException) { } return; }
        var sb = new StringBuilder();
        sb.AppendLine($"# Task anh em cùng task cha #{job.ParentTaskId}: {job.ParentTaskName}");
        sb.AppendLine();
        sb.AppendLine("Các task con khác của cùng task cha mà tool đã làm. Câu trả lời / quyết định của người dùng hoặc BA ở đây thường áp dụng luôn cho task này");
        sb.AppendLine("(cùng nghiệp vụ, cùng màn) — đọc trước khi đặt câu hỏi; dùng lại thì ghi rõ nguồn \"theo task #…\".");
        sb.AppendLine();
        foreach (var o in sibs)
        {
            var od = store.Dir(o.Id);
            sb.AppendLine($"## #{o.TaskId}: {o.TaskName} — {o.Status}{(o.QcFix ? $" — 🐞 vòng sửa lỗi QC trước (tạo {o.CreatedAt:dd/MM/yyyy HH:mm})" : " — task gốc / task anh em")}");
            // Chép nội dung (cắt gọn) — AI chỉ được đọc thư mục job hiện tại, không mở được thư mục job khác
            string Read(string file) { try { var f = Path.Combine(od, file); return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; } catch (IOException) { return ""; } }
            string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
            var an = Read("analysis.md");
            foreach (var (title, text) in new[]
                     {
                         ("Hiểu yêu cầu", Trunc(Section(an, "Hiểu yêu cầu") ?? "", 1200)),
                         ("Giả định đã dùng", Trunc(Section(an, "Giả định") ?? "", 1200)),
                         ("Câu hỏi khi phân tích", Trunc(Section(an, "Cần bổ sung") ?? "", 1200)),
                         ("Trao đổi với người dùng — câu trả lời, quyết định (đoạn cuối)", Tail(Read("chat.md"), 3000)),
                         ("Câu hỏi AI đã hỏi khi code", Trunc(Read("questions.md"), 1200)),
                         ("Điểm AI tự quyết khi code", Trunc(Read("assumptions.md"), 1200)),
                     })
            {
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("(không có")) continue;
                sb.AppendLine($"### {title}");
                sb.AppendLine(text);
                sb.AppendLine();
            }
            if (o.ChangedFiles.Count > 0) sb.AppendLine($"File đã sửa: {string.Join(", ", o.ChangedFiles.Take(12))}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        store.Log(job, "analyze", "info", $"Có {sibs.Count} task anh em cùng task cha — AI sẽ đọc để dùng lại câu trả lời đã có: {string.Join(", ", sibs.Select(o => "#" + o.TaskId))}");
    }

    // Dấu hiệu rủi ro trong diff (tên file hoặc dòng thêm/xoá): quyền, menu, luồng duyệt / chuông / mail → luôn review đầy đủ
    private static readonly Regex ReviewRiskPermission = new(
        @"AuthorizePermission|ConstantPermission|Get_Data_Permission|ResourcePermission|EnumResource\.OtherResource|IsAllowEditDelete|menu2\.json|MENU_SiteMap|PermissionService",
        RegexOptions.Compiled);
    private static readonly Regex ReviewRiskApproval = new(
        @"Approv|Reject|Sys_Notification|Notify|SendMail|Email|UserProcess|RequestCancel|Delegate",
        RegexOptions.Compiled);

    /// <summary>Chọn mức review theo cấu hình và rủi ro của diff. Trả (đầy đủ?, lý do để ghi log).</summary>
    private async Task<(bool Full, string Why)> ChooseReview(Job job, CancellationToken ct)
    {
        if (O.ReviewMode == "full") return (true, "cấu hình luôn review đầy đủ");
        if (O.ReviewMode == "light") return (false, "cấu hình luôn review nhẹ");
        await ComputeDiffStat(job, ct);
        var files = job.DiffList.Select(f => f.Split(" (")[0]).ToList();
        var sql = files.Count(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
        if (sql > 0) return (true, $"đổi {sql} file .sql");
        var lines = job.DiffAdded + job.DiffDeleted;
        if (files.Count > O.ReviewLightMaxFiles) return (true, $"{files.Count} file (quá {O.ReviewLightMaxFiles})");
        if (lines > O.ReviewLightMaxLines) return (true, $"{lines} dòng (quá {O.ReviewLightMaxLines})");
        // Dòng thay đổi (+/-) và tên file của mọi repo, kể cả nội dung file mới
        var sb = new StringBuilder(string.Join("\n", files)).Append('\n');
        try
        {
            foreach (var (rel, path, baseSha, before) in new[] { (O.RepoRelativePath, O.RepoPath, job.BaseSha, job.UntrackedBefore) }
                         .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.BaseSha, r.UntrackedBefore))))
            {
                if (baseSha is null) continue;
                foreach (var l in (await GitIn(path, ct, "diff", "-U0", baseSha)).Split('\n'))
                    if (l.Length > 1 && (l[0] == '+' || l[0] == '-') && !l.StartsWith("+++") && !l.StartsWith("---")) sb.Append(l).Append('\n');
                foreach (var f in (await Untracked(path, ct)).Where(f => !(before ?? []).Contains(f) && !O.IsLocalFile(rel, f) && !RuntimeFile.IsMatch(f)))
                    try { sb.Append(File.ReadAllText(Path.Combine(path, f.Replace('/', '\\')))).Append('\n'); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (PipelineException) { return (true, "không đọc được diff để đánh giá rủi ro"); }
        var text = sb.ToString();
        if (ReviewRiskPermission.Match(text) is { Success: true } mp) return (true, $"đụng quyền/menu ({mp.Value})");
        if (ReviewRiskApproval.Match(text) is { Success: true } ma) return (true, $"đụng luồng duyệt/chuông/mail ({ma.Value})");
        return (false, $"{files.Count} file, {lines} dòng, không đụng SQL/quyền/luồng duyệt");
    }

    /// <summary>Đếm file/dòng AI đổi so với commit gốc (cả file mới chưa track), mọi repo. Lỗi thì bỏ qua — chỉ để báo.</summary>
    private async Task ComputeDiffStat(Job job, CancellationToken ct)
    {
        int add = 0, del = 0;
        var list = new List<string>();
        try
        {
            foreach (var (rel, path, baseSha, before) in new[] { (O.RepoRelativePath, O.RepoPath, job.BaseSha, job.UntrackedBefore) }
                         .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.BaseSha, r.UntrackedBefore))))
            {
                if (baseSha is null) continue;
                foreach (var line in (await GitIn(path, ct, "diff", "--numstat", baseSha)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var p = line.Split('\t');
                    if (p.Length < 3 || O.IsLocalFile(rel, p[2]) || RuntimeFile.IsMatch(p[2])) continue;
                    int.TryParse(p[0], out var a);
                    int.TryParse(p[1], out var d);
                    add += a; del += d;
                    list.Add($"{p[2]} (+{a} −{d})");
                }
                foreach (var f in (await Untracked(path, ct)).Where(f => !(before ?? []).Contains(f) && !O.IsLocalFile(rel, f) && !RuntimeFile.IsMatch(f)))
                {
                    var n = 0;
                    try { n = File.ReadLines(Path.Combine(path, f.Replace('/', '\\'))).Count(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    add += n;
                    list.Add($"{f} (file mới, +{n})");
                }
            }
        }
        catch (PipelineException ex) { store.Log(job, "git", "stderr", "Không đếm được thay đổi: " + ex.Message); return; }
        job.DiffAdded = add;
        job.DiffDeleted = del;
        job.DiffList = list;
    }

    /// <summary>Đủ điều kiện tự duyệt thì trả lý do (để ghi log), không thì null.</summary>
    private string? AutoApproveReason(Job job, string dir)
    {
        if (!O.AutoApproveSmall) return null;
        if (job.QcFix) return null; // sửa lỗi QC trả lại QC — bạn luôn xem trước
        if (job.ReviewVerdict != "PASS") return null;
        var mode = TestModeFor(job);
        // Chỉ kiểm dữ liệu: tiêu chí giao diện luôn "chưa kiểm được" → chấp nhận PARTIAL
        if (mode != "off" && job.TestVerdict != "PASS" && !(mode == "data" && job.TestVerdict == "PARTIAL")) return null;
        if (job.Requirement != "CLEAR") return null; // AI phải tự đoán yêu cầu → bạn xem
        var asm = Path.Combine(dir, "assumptions.md");
        if (File.Exists(asm) && new FileInfo(asm).Length > 0) return null; // AI tự quyết điểm nào đó → bạn xem
        if (job.DiffList.Count == 0 || job.DiffList.Count > O.AutoApproveMaxFiles) return null;
        if (job.DiffAdded + job.DiffDeleted > O.AutoApproveMaxLines) return null;
        if (job.DiffList.Any(f => Regex.IsMatch(f, @"\.(sql|config|csproj|sln)\b", RegexOptions.IgnoreCase))) return null; // store/cấu hình: luôn xem tay
        return $"{job.DiffList.Count} file, +{job.DiffAdded} −{job.DiffDeleted} dòng, review PASS" + (mode == "off" ? ", không test" : $", test {job.TestVerdict}");
    }

    /// <summary>
    /// Duyệt / từ chối job chờ duyệt đã commit (không giữ repo) — dùng chung cho nút trong tool và lệnh AMIS Chat.
    /// </summary>
    public async Task<PipelineException?> Decide(string id, bool approve, bool push, string? reason, string via = "")
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (job.Status != JobStatus.AwaitingApproval) return new PipelineException("Job không ở trạng thái chờ duyệt.");
        if (job.Inspecting) return new PipelineException("Task đang được kiểm trên web — bấm \"Xong kiểm\" trước (để phần bạn sửa thêm được commit vào nhánh).");
        if (!job.Held && job.Branch is not null && await OnAiBranch(job) && await HasChanges(job, CancellationToken.None))
        {
            job.Held = true; // còn code AI chưa commit (vd bước commit lúc xong bị lỗi) — chuyển sang luồng "Tạo commit"
            store.Save(job);
        }
        if (job.Held) return new PipelineException("Code AI chưa commit — dùng nút \"Tạo commit & push\" hoặc \"Từ chối\" trong hộp xác nhận commit.");
        if (approve && push && (await ChainDrift(job)).FirstOrDefault() is { } drift)
            return new PipelineException($"Nhánh cha {drift.Parent} đã đổi sau khi tạo nhánh này — bấm \"Cập nhật theo nhánh cha\" trước khi push, nếu không PR sẽ kéo theo bản cũ của nhánh cha.");
        job.Status = approve ? JobStatus.Approved : JobStatus.Rejected;
        job.FinishedAt = DateTime.Now;
        store.Log(job, "decision", "stage", (approve
            ? $"Đã duyệt. Nhánh {job.Branch} sẵn sàng để push."
            : $"Từ chối{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason)}. Nhánh {job.Branch} vẫn được giữ lại.") + via);
        store.Save(job);
        if (!approve) insights.Suggest(job, "reject", reason);
        // Dữ liệu test được giữ để người dùng kiểm tay — duyệt/từ chối xong thì dọn
        await AutoCleanup(job);
        if (!approve && job.SqlDeployed.Count > 0) await RestoreSql(job);
        return null;
    }

    /// <summary>
    /// Duyệt / từ chối từ AMIS Chat — KHÔNG push. Job đang giữ code chưa commit: commit tự động vào nhánh AI
    /// (bỏ file sinh ra khi chạy app như .db/.log, như mặc định của hộp "Tạo commit") rồi trả nhánh.
    /// </summary>
    public async Task<PipelineException?> DecideFromChat(string id, bool approve, string? reason, string via = "qua AMIS Chat")
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (!job.Held && job.Branch is not null && job.Status == JobStatus.AwaitingApproval
            && await OnAiBranch(job) && await HasChanges(job, CancellationToken.None))
        {
            job.Held = true; // code chưa commit mà cờ giữ bị mất (vd tool khởi động lại) — commit thay vì chỉ đánh dấu duyệt
            store.Save(job);
        }
        if (!job.Held) return await Decide(id, approve, push: false, reason, $" ({via})");
        if (job.Status != JobStatus.AwaitingApproval) return new PipelineException("Job không ở trạng thái chờ duyệt.");
        if (job.Inspecting) return new PipelineException("Task đang được kiểm trên web — bấm \"Xong kiểm\" trong tool trước.");
        var err = await CommitHeld(id, new CommitPlan(null, false, null, await RuntimeSkip(job)), approve ? "approve" : "reject");
        if (err is not null) return err;
        job = store.Get(id)!;
        if (!approve)
        {
            if (!string.IsNullOrWhiteSpace(reason)) store.Log(job, "decision", "info", $"Lý do từ chối ({via}): " + reason);
            insights.Suggest(job, "reject", reason);
        }
        else store.Log(job, "decision", "info", "Duyệt qua AMIS Chat — chưa push.");
        return null;
    }

    /// <summary>
    /// Lệnh "#id push" từ AMIS Chat (người dùng gõ rõ chữ push): job chờ duyệt thì duyệt + commit vào nhánh AI trước,
    /// rồi push nhánh AI và tạo PR (nếu bật tạo PR tự động). Job đã duyệt mà chưa push thì chỉ push + tạo PR.
    /// Trả (lỗi, các link PR).
    /// </summary>
    /// <summary>File sinh ra khi chạy web (.db, .log…) đang sửa — bỏ khỏi commit tự động, như mặc định của hộp "Tạo commit".</summary>
    private async Task<Dictionary<string, List<string>>> RuntimeSkip(Job job)
    {
        var skip = new Dictionary<string, List<string>>();
        foreach (var (rel, path) in new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))))
        {
            var runtime = (await repo.Dirty(rel, path, CancellationToken.None)).Where(f => RuntimeFile.IsMatch(f)).ToList();
            if (runtime.Count > 0) skip[rel] = runtime;
        }
        return skip;
    }

    /// <summary>"#id commit" từ chat: commit code AI đang giữ vào nhánh AI (KHÔNG push) rồi trả nhánh; job chờ duyệt thì thành Đã duyệt.</summary>
    public async Task<PipelineException?> CommitFromChat(string id)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (job.IsRunning) return new PipelineException("Job đang chạy — chờ xong rồi commit.");
        if (job.Status == JobStatus.AwaitingApproval) return await DecideFromChat(id, approve: true, null);
        if (!job.Held) return job.HeadSha is not null
            ? new PipelineException($"Đã commit rồi ({job.HeadSha[..10]} trên {job.Branch}) — gõ #{job.TaskId} push để push và tạo PR.")
            : new PipelineException("Không có code AI nào đang chờ commit.");
        return await CommitHeld(id, new CommitPlan(null, false, null, await RuntimeSkip(job)), decision: null);
    }

    public async Task<(PipelineException? Error, List<string> PrUrls)> PushFromChat(string id)
    {
        var job = store.Get(id);
        if (job is null) return (new PipelineException("Không thấy job."), []);
        if (job.IsRunning) return (new PipelineException("Job đang chạy — chờ xong rồi push."), []);
        if (job.Status is not (JobStatus.AwaitingApproval or JobStatus.Approved))
            return (new PipelineException($"Chỉ push được job đang chờ duyệt hoặc đã duyệt (job đang ở {job.Status})."), []);
        if (job.TestVerdict == "FAIL")
            store.Log(job, "decision", "info", "Push qua AMIS Chat dù test FAIL — theo lệnh của bạn.");
        if ((await ChainDrift(job)).FirstOrDefault() is { } drift)
            return (new PipelineException($"Nhánh cha {drift.Parent} đã đổi sau khi tạo nhánh này — vào tool bấm \"Cập nhật theo nhánh cha\" trước khi push."), []);
        if ((job.Status == JobStatus.AwaitingApproval || job.Held) && await CommitFromChat(id) is { } derr) return (derr, []);
        job = store.Get(id)!;
        if (job.HeadSha is null) return (new PipelineException("Job chưa có commit trên nhánh AI — không có gì để push."), []);
        if (await PushBranch(job) is { } perr) return (perr, []);
        store.Log(job, "decision", "info", "Push qua AMIS Chat.");
        var prErr = AutoPr ? await CreatePrs(job) : null;
        await ReturnToBase(job); // đã push: không để repo đứng lại ở nhánh AI
        if (prErr is not null)
            return (new PipelineException("Đã push, nhưng " + prErr.Message + " — vào tool bấm \"Tạo PR\" sau khi xử lý."), job.PrUrls.Values.ToList());
        store.Save(job);
        return (null, job.PrUrls.Values.ToList());
    }

    private static void AppendAssumptions(string dir, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        File.AppendAllText(Path.Combine(dir, "assumptions.md"), $"## {title}\n\n{text.Trim()}\n\n", new UTF8Encoding(false));
    }

    // ---------- Tự chạy lại sau lỗi kỹ thuật (chạy hàng loạt) ----------

    private static readonly string[] NoRetry =
    [
        "AI không làm tiếp được", "không tạo ra thay đổi", "giới hạn chi phí", "chưa commit", "đang giữ repo",
        "Đã huỷ", "đang có job", "Task này đang có", "Không thấy job",
    ];

    /// <summary>Job hàng loạt lỗi (timeout, mạng, Claude quá tải, key hết hạn mức…) → chờ 2 phút rồi chạy lại MỘT lần từ chỗ dở.</summary>
    private void MaybeRetry(Job job, CancellationToken stop)
    {
        if (!job.Batch || job.Status != JobStatus.Failed || job.AutoRetries >= 1) return;
        var err = job.Error ?? "";
        if (NoRetry.Any(k => err.Contains(k, StringComparison.OrdinalIgnoreCase))) return;
        job.AutoRetries++;
        job.RetryPending = true;
        store.Log(job, "control", "stage", $"Lỗi \"{Trunc(err, 200)}\" — chạy hàng loạt nên tự thử lại sau 2 phút (lần {job.AutoRetries}).");
        store.Save(job);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); }
            catch (OperationCanceledException) { return; }
            job.RetryPending = false;
            if (store.Get(job.Id) is not { Status: JobStatus.Failed } j) { store.Save(job); return; } // người dùng đã xử lý trong lúc chờ
            if (j.CodeApproved && j.Branch is not null)
            {
                if (j.CodeStatus == "DONE" && j.HeadSha is not null) j.ResumeFrom = "review"; // code xong rồi, lỗi ở review/test
                else if (j.CodeSessionId is not null)
                    j.PendingAnswer = $"(Không phải câu trả lời — phiên trước bị ngắt do lỗi kỹ thuật: {Trunc(err, 300)}.) " +
                                      "Code đã làm được vẫn còn trên nhánh. Kiểm lại phần đã làm, làm tiếp phần còn dở, chạy ship-check" + Prompts.NoReviewer + ".";
                else
                {
                    store.Log(j, "control", "error", "Không tự thử lại được: lỗi xảy ra lúc tạo nhánh — bấm chạy lại bằng tay.");
                    j.RetryPending = false;
                    store.Save(j);
                    return;
                }
            }
            j.Status = JobStatus.Queued;
            j.Error = null;
            j.FinishedAt = null;
            store.Log(j, "control", "stage", "Tự thử lại sau lỗi kỹ thuật.");
            store.Save(j);
            Enqueue(j);
        }, CancellationToken.None);
    }

    // ---------- Kiểm tay trên web ----------

    /// <summary>
    /// Chuyển repo sang nhánh AI của job (đã commit), build để web IIS chạy code đó — người dùng kiểm tay, có thể sửa thêm.
    /// Hàng đợi code tạm dừng tới khi gọi FinishInspect.
    /// </summary>
    public async Task<PipelineException?> StartInspect(string id)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (_inspectJob is { } other) return new PipelineException($"Đang kiểm task #{store.Get(other)?.TaskId} trên web — bấm \"Xong kiểm\" ở task đó trước.");
        if (_codeJob is not null) return new PipelineException("AI đang chạy một task trong repo — chờ task đó xong rồi bấm lại (task kế tiếp sẽ chờ bạn kiểm xong).");
        await ReleaseStaleHolds();
        if (store.All().FirstOrDefault(j => j.Held) is { } held) return new PipelineException($"Task #{held.TaskId} đang giữ repo (code chưa commit) — xử lý task đó trước.");
        if (job.Branch is null || job.HeadSha is null || job.Held) return new PipelineException("Job chưa có code đã commit trên nhánh AI.");
        if (job.Status is not (JobStatus.AwaitingApproval or JobStatus.Approved or JobStatus.Failed))
            return new PipelineException("Chỉ kiểm được job đã chạy xong (chờ duyệt / đã duyệt / lỗi).");
        _inspectJob = id; // chặn hàng đợi ngay, trước khi đổi nhánh
        var keep = (job.ReturnBranch, job.ExtraRepos.Select(r => r.ReturnBranch).ToList());
        try
        {
            await ResumeGit(job, CancellationToken.None); // repo phải sạch; nhớ nhánh hiện tại để trả về; chuyển sang nhánh AI
        }
        catch (PipelineException ex)
        {
            _inspectJob = null;
            return ex;
        }
        // Nhánh lúc giao task là đích PR — chỉ dùng nhánh hiện tại để trả về sau khi kiểm
        job.InspectReturn = job.ReturnBranch;
        job.ReturnBranch = keep.Item1;
        job.InspectExtraReturn = job.ExtraRepos.Select(r => r.ReturnBranch).ToList();
        for (var i = 0; i < job.ExtraRepos.Count; i++) job.ExtraRepos[i].ReturnBranch = keep.Item2.ElementAtOrDefault(i);
        job.Inspecting = true;
        store.Log(job, "inspect", "stage", $"Kiểm trên web: repo đã chuyển sang {job.Branch}. Đang build để web chạy code của task…");
        store.Save(job);
        _ = Task.Run(async () =>
        {
            try
            {
                var none = CancellationToken.None;
                var changed = await ChangedFiles(job, none);
                var projects = testEnv.ProjectsFor(changed);
                if (projects.Count > 0)
                {
                    var b = await testEnv.Build(projects, withDependents: true, TimeSpan.FromMinutes(O.StageTimeoutMinutes), none);
                    job.BuiltProjects = projects;
                    store.Log(job, "inspect", b.Ok ? "info" : "error", b.Ok
                        ? $"Build xong {projects.Count} project."
                        : $"Build lỗi — web có thể chưa chạy đúng code: {Trunc(b.Output, 400)}");
                }
                var affected = testEnv.AffectedSites(changed, projects);
                var main = testEnv.Sites().Where(s => s.Url == testEnv.BaseUrl());
                var warm = await testEnv.WarmUp(affected.Concat(main).DistinctBy(s => s.Url), none);
                store.Log(job, "inspect", "stage", "Sẵn sàng — mở web để kiểm" + (warm.Length > 0 ? $" ({warm})" : "") +
                    ". Có thể sửa thêm trong Visual Studio; bấm \"Xong kiểm\" để commit phần sửa vào nhánh AI và trả repo.");
            }
            catch (Exception ex) when (ex is PipelineException or IOException or InvalidOperationException)
            {
                store.Log(job, "inspect", "error", "Chuẩn bị web lỗi: " + ex.Message);
            }
            store.Save(job);
        });
        return null;
    }

    /// <summary>Kết thúc kiểm: commit phần người dùng sửa thêm (nếu có) vào nhánh AI, trả repo về nhánh cũ, build lại.</summary>
    public async Task<PipelineException?> FinishInspect(string id)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (!job.Inspecting) return new PipelineException("Job không ở chế độ kiểm trên web.");
        if (!await OnAiBranch(job)) return new PipelineException($"Repo không ở nhánh {job.Branch} — chuyển về nhánh đó rồi bấm lại.");
        var target = (job.ReturnBranch, job.ExtraRepos.Select(r => r.ReturnBranch).ToList());
        job.ReturnBranch = job.InspectReturn ?? job.ReturnBranch;
        for (var i = 0; i < job.ExtraRepos.Count; i++) job.ExtraRepos[i].ReturnBranch = job.InspectExtraReturn.ElementAtOrDefault(i) ?? job.ExtraRepos[i].ReturnBranch;
        var before = (job.HeadSha, job.ExtraRepos.Select(r => r.HeadSha).ToList());
        try { await FinalizeGit(job); }
        catch (PipelineException ex)
        {
            store.Log(job, "inspect", "error", ex.Message);
            store.Save(job);
            return ex;
        }
        finally
        {
            // Trả lại đích PR (nhánh lúc giao task)
            job.ReturnBranch = target.Item1;
            for (var i = 0; i < job.ExtraRepos.Count; i++) job.ExtraRepos[i].ReturnBranch = target.Item2.ElementAtOrDefault(i);
        }
        job.Inspecting = false;
        job.InspectReturn = null;
        job.InspectExtraReturn = [];
        _inspectJob = null;
        var edited = job.HeadSha != before.Item1 || !job.ExtraRepos.Select(r => r.HeadSha).SequenceEqual(before.Item2);
        if (edited)
        {
            job.ChangedFiles = [];
            try { await CheckOverlaps(job); } catch (PipelineException) { }
            store.Log(job, "inspect", "stage", $"Đã commit phần bạn sửa thêm vào {job.Branch}" +
                (job.PrUrls.Count > 0 ? " — PR đã có: push lại nhánh để PR cập nhật (bấm \"Duyệt & push\" hoặc git push)." : "."));
        }
        else store.Log(job, "inspect", "stage", "Xong kiểm — không có sửa thêm. Repo đã trả về nhánh cũ, hàng đợi AI chạy tiếp.");
        store.Save(job);
        return null;
    }

    private void AskUser(Job job, string dir, string answerText)
    {
        job.QuestionRounds++;
        var qFile = Path.Combine(dir, "questions.md");
        var questions = File.Exists(qFile) ? File.ReadAllText(qFile).Trim() : "";
        // Câu trả lời cuối của phiên code đã là danh sách câu hỏi; dùng questions.md nếu AI chỉ ghi file
        var text = StripMarker(answerText, "STATUS");
        if (text.Length < 40 && questions.Length > 0) text = questions;
        chat.Append(job.TaskId, job.TaskName, new ChatMessage
        {
            Role = "pipeline",
            Text = $"Mình cần bạn xác nhận trước khi làm tiếp (vòng {job.QuestionRounds}):\n\n{text}",
            At = DateTime.Now,
            JobId = job.Id,
        });
        SetStatus(job, JobStatus.WaitingForUser, "ask", "AI cần bạn xác nhận — trả lời trong khung Chat rồi bấm \"Gửi & tiếp tục\"");
    }

    /// <summary>Chép tệp người dùng đính kèm trong chat sang inputs/ của job (cả bản trích *.md và ảnh tách ra) để các bước AI đọc được.</summary>
    private void SyncChatFiles(Job job, string dir)
    {
        var src = chat.FilesDir(job.TaskId);
        if (!Directory.Exists(src)) return;
        var inputs = Path.Combine(dir, "inputs");
        Directory.CreateDirectory(inputs);
        var attached = chat.AttachedFiles(job.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(inputs, name);
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(file).Length) continue;
            File.Copy(file, dest, overwrite: true);
            if (attached.Contains(name) && !job.InputFiles.Contains(name))
            {
                job.InputFiles.Add(name);
                store.Log(job, "inputs", "info", $"Lấy tệp đính kèm từ chat: {name}");
            }
        }
    }

    private void ExportChat(Job job, string dir)
    {
        SyncChatFiles(job, dir);
        var md = chat.ExportMarkdown(job.TaskId);
        File.WriteAllText(Path.Combine(dir, "chat.md"), md ?? "(Chưa có trao đổi nào.)\n", new UTF8Encoding(false));
    }

    private void SetStatus(Job job, JobStatus status, string stage, string message)
    {
        job.Status = status;
        store.Log(job, stage, "stage", message);
        store.Save(job);
    }

    // ---------- Git ----------

    /// <summary>Repo chính + repo phụ đang cấu hình (dùng khi tạo nhánh cho job mới).</summary>
    private IEnumerable<(string Rel, string Path)> ConfiguredRepos() =>
        new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(O.ExtraRepoPaths);

    private async Task EnsureCleanRepo(IEnumerable<(string Rel, string Path)> repos, CancellationToken ct, string action)
    {
        foreach (var (rel, path) in repos)
        {
            if (!SettingsService.IsGitRepo(path)) throw new PipelineException($"Không thấy git repo tại {path}.");
            var dirty = await repo.Dirty(rel, path, ct);
            if (dirty.Count > 0)
                throw new PipelineException($"{rel} đang có thay đổi chưa commit. Commit hoặc stash trước khi {action} " +
                                            $"(file cấu hình môi trường thì khai báo ở ⚙ Cấu hình › File cấu hình local):\n{Trunc(string.Join("\n", dirty), 800)}");
        }
    }

    private async Task<string> CurrentBranch(string rel, string path, CancellationToken ct)
    {
        var branch = (await GitIn(path, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        if (branch == "HEAD") throw new PipelineException($"{rel} đang ở trạng thái detached HEAD. Chuyển về một nhánh trước.");
        return branch;
    }

    /// <summary>
    /// Job đánh dấu "giữ repo" (code chưa commit trên nhánh AI) nhưng repo đã rời nhánh đó — người dùng tự chuyển nhánh
    /// ngoài tool. Cờ giữ không còn đúng: bỏ để task khác chạy được (thay đổi chưa commit, nếu còn, đã đi theo lần chuyển nhánh
    /// và sẽ bị bước kiểm repo sạch chặn lại). Không đụng job đang chạy.
    /// </summary>
    public async Task ReleaseStaleHolds()
    {
        if (!await _holdCheck.WaitAsync(0)) return; // đang có lần kiểm khác (giao diện làm mới dồn dập)
        try
        {
            foreach (var j in store.All().Where(j => j.Held && !j.IsRunning && j.Id != _codeJob && j.Id != _committing && j.Branch is not null).ToList())
            {
                if (await OnAiBranch(j))
                {
                    if (j.Held && j.Id != _committing) await AdoptManualCommit(j);
                    continue;
                }
                if (!j.Held || j.Id == _committing) continue;
                j.Held = false;
                store.Log(j, "git", "info", $"Repo không còn ở nhánh {j.Branch} (đã chuyển nhánh ngoài tool) — bỏ trạng thái giữ repo để task khác chạy được. " +
                                            "Thay đổi chưa commit (nếu còn) đã đi theo lần chuyển nhánh đó — kiểm tra git status nếu cần.");
                store.Save(j);
            }
        }
        finally { _holdCheck.Release(); }
    }
    /// <summary>
    /// Người dùng tự commit (có thể cả push) phần code AI ngay trên nhánh AI, ngoài tool: repo vẫn ở nhánh AI, HEAD đã khác điểm gốc
    /// và không còn file đã track nào sửa dở (file local/sinh khi chạy web bỏ qua). Nhận commit đó làm kết quả job:
    /// đã push → Đã duyệt; chưa push → bỏ giữ repo, job chờ duyệt với code đã commit (nút Duyệt &amp; push bình thường).
    /// Không đổi nhánh working copy.
    /// </summary>
    private async Task AdoptManualCommit(Job job)
    {
        var none = CancellationToken.None;
        try
        {
            var repos = new[] { (Rel: O.RepoRelativePath, Path: O.RepoPath, Base: job.BaseSha, Extra: (RepoState?)null) }
                .Concat(job.ExtraRepos.Select(r => (Rel: r.Rel, Path: r.Path, Base: r.BaseSha, Extra: (RepoState?)r))).ToList();
            var heads = new List<(string Rel, string Head, bool Pushed, RepoState? Extra)>();
            var leftover = new List<string>();
            foreach (var (rel, path, baseSha, extra) in repos)
            {
                leftover.AddRange((await repo.Dirty(rel, path, none)).Where(f => !RuntimeFile.IsMatch(f)).Select(f => $"{rel}/{f}"));
                var head = (await GitIn(path, none, "rev-parse", "HEAD")).Trim();
                string? upstream = null;
                try { upstream = (await GitIn(path, none, "rev-parse", "@{u}")).Trim(); }
                catch (PipelineException) { } // nhánh chưa có upstream = chưa push
                heads.Add((rel, head, upstream == head, extra));
            }
            var main = heads[0];
            if (job.BaseSha is null || main.Head == job.BaseSha) return; // repo chính chưa có commit mới — chưa phải tự commit
            var committedRepos = heads.Where((x, i) => i == 0 || x.Head != repos[i].Base).ToList();
            // Còn file sửa dở mà chưa push: có thể bạn đang commit dở — vẫn giữ. Đã push: bạn đã quyết, nhận commit và nhắc file còn sót.
            if (leftover.Count > 0 && !committedRepos.All(x => x.Pushed)) return;
            var left = leftover.Count == 0 ? "" :
                $" ⚠ Còn {leftover.Count} file đã track sửa mà CHƯA commit (kiểm xem có cần đưa vào PR không): " +
                string.Join(", ", leftover.Take(8)) + (leftover.Count > 8 ? $" … (+{leftover.Count - 8})" : "") + ".";
            job.HeadSha = main.Head;
            foreach (var x in heads.Skip(1))
                if (x.Extra is { } e && x.Head != e.BaseSha) e.HeadSha = x.Head;
            job.Held = false;
            var pushed = committedRepos.All(x => x.Pushed);
            var where = string.Join(", ", committedRepos.Select(x => $"{x.Rel} {x.Head[..10]}{(x.Pushed ? " (đã push)" : " (chưa push)")}"));
            if (pushed && job.Status == JobStatus.AwaitingApproval)
            {
                job.Status = JobStatus.Approved;
                job.FinishedAt = DateTime.Now;
                store.Log(job, "decision", "stage", $"Bạn đã tự commit & push nhánh {job.Branch} ngoài tool ({where}) — đánh dấu Đã duyệt. " +
                                                    $"Repo vẫn đang ở nhánh {job.Branch}; chuyển về {job.ReturnBranch} trước khi giao task mới." + left);
                await AutoCleanup(job);
            }
            else
                store.Log(job, "git", "info", $"Bạn đã tự commit trên nhánh {job.Branch} ({where}) — bỏ trạng thái giữ repo." +
                                              (pushed ? "" : " Chưa push hết: bấm \"Duyệt & push\" hoặc tự push.") + left);
            store.Save(job);
        }
        catch (PipelineException ex) { logger.LogInformation("Không kiểm được commit tay của job {Id}: {Msg}", job.Id, ex.Message); }
    }

    private readonly SemaphoreSlim _holdCheck = new(1, 1);
    private volatile string? _committing;

    private async Task PrepareGit(Job job, CancellationToken ct)
    {
        await ReleaseStaleHolds();
        if (store.All().FirstOrDefault(j => j.Held && j.Id != job.Id) is { } holder)
            throw new PipelineException($"Job #{holder.TaskId} đang giữ repo trên nhánh {holder.Branch} (code chưa commit). Mở job đó, bấm \"Tạo commit\" hoặc \"Commit & trả nhánh\" trước.");
        var repos = ConfiguredRepos().ToList();
        await EnsureCleanRepo(repos, ct, "giao cho AI");
        if (O.PullBeforeTask)
            foreach (var (rel, path) in repos) await PullLatest(job, rel, path, ct);
        job.Branch = $"{O.BranchPrefix}{job.TaskId}-{DateTime.Now:yyyyMMdd-HHmm}";

        var userBranch = await CurrentBranch(O.RepoRelativePath, O.RepoPath, ct);
        job.ReturnBranch = userBranch;
        job.ChainedFromTask = null;
        job.ChainedFromBranch = null;
        // Chạy hàng loạt: task trước (đang chờ duyệt) đã sửa file mà task này cũng định sửa → nối nhánh từ nhánh task đó để không conflict
        var parent = job.Batch && job.BatchChain ? await ChainParent(job, userBranch, ct) : null;
        string? from = null;
        if (parent is { Job: var pj, Files: var shared })
        {
            from = pj.Branch!;
            job.ChainedFromTask = pj.TaskId;
            job.ChainedFromBranch = pj.Branch;
            store.Log(job, "git", "stage", $"Task #{pj.TaskId} (đang chờ duyệt) đã sửa {shared.Count} file mà task này cũng định sửa ({Trunc(string.Join(", ", shared.Select(Path.GetFileName)), 300)}) " +
                                           $"— tạo nhánh NỐI TIẾP từ {pj.Branch} để không conflict. Khi merge: PR của #{pj.TaskId} vào trước.");
        }
        job.BaseBranch = from ?? userBranch;
        job.BaseSha = (await Git(ct, "rev-parse", from ?? "HEAD")).Trim();
        job.UntrackedBefore = await Untracked(O.RepoPath, ct);
        // Đọc trạng thái mọi repo trước rồi mới tạo nhánh, để lỗi ở repo phụ không để lại nhánh dở ở repo chính
        job.ExtraRepos = [];
        var extraFrom = new Dictionary<string, string?>();
        foreach (var (rel, path) in O.ExtraRepoPaths)
        {
            var b = await CurrentBranch(rel, path, ct);
            var rf = from is not null && await BranchExists(path, from, ct) ? from : null;
            extraFrom[rel] = rf;
            job.ExtraRepos.Add(new RepoState
            {
                Rel = rel, Path = path, BaseBranch = rf ?? b, ReturnBranch = b,
                BaseSha = (await GitIn(path, ct, "rev-parse", rf ?? "HEAD")).Trim(),
                UntrackedBefore = await Untracked(path, ct),
            });
        }

        SnapshotLocalFiles(job, new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))));
        await Git(ct, ["switch", "-c", job.Branch, .. from is null ? Array.Empty<string>() : [from]]);
        store.Log(job, "git", "info", $"{O.RepoRelativePath}: tạo nhánh {job.Branch} từ {job.BaseBranch} ({job.BaseSha[..Math.Min(10, job.BaseSha.Length)]}).");
        foreach (var r in job.ExtraRepos)
        {
            var rf = extraFrom[r.Rel];
            await GitIn(r.Path, ct, ["switch", "-c", job.Branch, .. rf is null ? Array.Empty<string>() : [rf]]);
            store.Log(job, "git", "info", $"{r.Rel}: tạo nhánh {job.Branch} từ {r.BaseBranch} ({r.BaseSha![..Math.Min(10, r.BaseSha!.Length)]}).");
        }
        store.Save(job);
    }

    /// <summary>
    /// Kéo nhánh đang đứng lên bản mới nhất trên origin — chỉ fast-forward (không merge/rebase, không tạo commit).
    /// Không kéo được (không có upstream, lệch hai phía, mất mạng, file local chặn) thì ghi cảnh báo và làm trên bản ở máy.
    /// </summary>
    private async Task PullLatest(Job job, string rel, string path, CancellationToken ct)
    {
        string upstream;
        try { upstream = (await GitIn(path, ct, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}")).Trim(); }
        catch (PipelineException)
        {
            store.Log(job, "git", "info", $"{rel}: nhánh đang đứng chưa có upstream trên origin — không kéo code mới.");
            return;
        }
        var slash = upstream.IndexOf('/');
        if (slash <= 0) return;
        var (remote, remoteBranch) = (upstream[..slash], upstream[(slash + 1)..]);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            await GitIn(path, cts.Token, "fetch", remote, remoteBranch);
        }
        catch (Exception ex) when (ex is PipelineException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            store.Log(job, "git", "error", $"{rel}: không fetch được {upstream} ({Trunc(ex.Message, 200)}) — làm trên code đang có ở máy.");
            return;
        }
        var counts = (await GitIn(path, ct, "rev-list", "--left-right", "--count", "HEAD...@{u}")).Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length < 2 || !int.TryParse(counts[0], out var ahead) || !int.TryParse(counts[1], out var behind)) return;
        if (behind == 0) return;
        if (ahead > 0)
        {
            store.Log(job, "git", "error", $"{rel}: nhánh của bạn có {ahead} commit chưa push và origin có {behind} commit mới — KHÔNG tự kéo (cần merge/rebase tay). AI làm trên code ở máy.");
            return;
        }
        try
        {
            await GitIn(path, ct, "merge", "--ff-only", "@{u}");
            store.Log(job, "git", "info", $"{rel}: đã kéo {behind} commit mới từ {upstream} (fast-forward) trước khi tạo nhánh AI.");
        }
        catch (PipelineException ex)
        {
            store.Log(job, "git", "error", $"{rel}: không fast-forward được theo {upstream} ({Trunc(ex.Message, 200)}) — AI làm trên code ở máy.");
        }
    }

    private async Task<bool> BranchExists(string path, string branch, CancellationToken ct)
    {
        try { await GitIn(path, ct, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch); return true; }
        catch (PipelineException) { return false; }
    }

    // ---------- Chạy hàng loạt: file trùng giữa các task ----------

    private static readonly Regex FileToken = new(@"[\w.\-/\\]+\.(?:cs|cshtml|sql|js|mjs|ts|tsx|jsx|vue|html|htm|css|scss|less|json|xml|config|resx|csproj|aspx|ascx|master|asmx|svc)\b", RegexOptions.IgnoreCase);

    /// <summary>Đường dẫn file trong đoạn markdown (mục Phạm vi của bản phân tích), chuẩn hoá dấu / và chữ thường.</summary>
    private List<string> PlannedFiles(string text)
    {
        var root = O.WorkspaceRoot.Replace('\\', '/').TrimEnd('/').ToLowerInvariant() + "/";
        return FileToken.Matches(text).Select(m => NormPath(m.Value, root)).Where(p => p.Length > 0).Distinct().Take(200).ToList();
    }

    private static string NormPath(string p, string? root = null)
    {
        var s = p.Replace('\\', '/').Trim().Trim('`', '"', '\'', '(', ')', '.', '/').ToLowerInvariant();
        if (root is not null && s.StartsWith(root)) s = s[root.Length..];
        return s;
    }

    /// <summary>Hai đường dẫn chỉ cùng một file: bằng nhau, hoặc một bên là đuôi của bên kia (vd "Foo.cs" ↔ "src/hrm9/x/foo.cs").</summary>
    private static bool SameFile(string a, string b) =>
        a == b || a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal);

    private static List<string> Shared(IEnumerable<string> mine, IEnumerable<string> theirs)
    {
        var t = theirs.ToList();
        return mine.Where(m => t.Any(x => SameFile(m, x))).Distinct().ToList();
    }

    /// <summary>File đã sửa trên nhánh AI so với commit gốc, ở mọi repo — dạng "src/hrm9/...".</summary>
    private async Task<List<string>> BranchFiles(Job job, CancellationToken ct)
    {
        var list = new List<string>();
        async Task Add(string rel, string path, string? baseSha, string? head)
        {
            if (baseSha is null || head is null || baseSha == head) return;
            var names = await GitIn(path, ct, "diff", "--name-only", baseSha, head);
            var prefix = rel.Replace('\\', '/').Trim('/').ToLowerInvariant() + "/";
            list.AddRange(names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !O.IsLocalFile(rel, f)).Select(f => prefix + NormPath(f)));
        }
        await Add(O.RepoRelativePath, O.RepoPath, job.BaseSha, job.HeadSha);
        foreach (var r in job.ExtraRepos) await Add(r.Rel, r.Path, r.BaseSha, r.HeadSha);
        return list;
    }

    /// <summary>Job đang mở có code đã commit trên nhánh AI, cùng gốc với nhánh của người dùng — có thể conflict khi merge.</summary>
    private IEnumerable<Job> OpenSiblings(Job job, string userBranch) =>
        store.All().Where(o => o.Id != job.Id && o.TaskId != job.TaskId && o.Branch is not null && o.HeadSha is not null && !o.Held
                               && o.Status is JobStatus.AwaitingApproval or JobStatus.WaitingForUser
                               && string.Equals(o.ReturnBranch ?? o.BaseBranch, userBranch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Chọn task gần nhất (đang chờ duyệt) đã sửa file trùng với file task này định sửa.</summary>
    private async Task<(Job Job, List<string> Files)?> ChainParent(Job job, string userBranch, CancellationToken ct)
    {
        if (job.PlannedFiles.Count == 0)
        {
            store.Log(job, "git", "info", "Bản phân tích không nêu file dự kiến — tạo nhánh riêng, sẽ kiểm file trùng sau khi code xong.");
            return null;
        }
        foreach (var o in OpenSiblings(job, userBranch).Where(o => o.Status == JobStatus.AwaitingApproval).OrderByDescending(o => o.FinishedAt ?? o.StartedAt ?? o.CreatedAt))
        {
            if (o.ChangedFiles.Count == 0) o.ChangedFiles = await BranchFiles(o, ct);
            var shared = Shared(job.PlannedFiles, o.ChangedFiles);
            if (shared.Count > 0 && await BranchExists(O.RepoPath, o.Branch!, ct)) return (o, shared);
        }
        return null;
    }

    /// <summary>Các task cùng chuỗi nối tiếp với job (tổ tiên lẫn con cháu) — đã nằm chung một nhánh nên không tính là trùng.</summary>
    private HashSet<long> Lineage(Job job)
    {
        var all = store.All();
        var set = new HashSet<long> { job.TaskId };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var o in all)
            {
                if (o.ChainedFromTask is { } p && set.Contains(o.TaskId) && set.Add(p)) changed = true;
                if (o.ChainedFromTask is { } q && set.Contains(q) && set.Add(o.TaskId)) changed = true;
            }
        }
        return set;
    }

    /// <summary>Sau khi trả nhánh: ghi lại file đã sửa và cảnh báo nếu task khác đang mở (không cùng chuỗi) sửa cùng file.</summary>
    private async Task CheckOverlaps(Job job)
    {
        if (job.Branch is null || job.HeadSha is null || job.Held) return;
        var none = CancellationToken.None;
        job.ChangedFiles = await BranchFiles(job, none);
        var lineage = Lineage(job);
        var userBranch = job.ReturnBranch ?? job.BaseBranch ?? "";
        var found = new Dictionary<string, List<string>>();
        foreach (var o in OpenSiblings(job, userBranch).Where(o => !lineage.Contains(o.TaskId)))
        {
            if (o.ChangedFiles.Count == 0) o.ChangedFiles = await BranchFiles(o, none);
            var shared = Shared(job.ChangedFiles, o.ChangedFiles);
            var key = o.TaskId.ToString();
            if (shared.Count > 0)
            {
                found[key] = shared;
                if (!o.Overlaps.ContainsKey(job.TaskId.ToString()))
                    store.Log(o, "git", "error", $"⚠ Task #{job.TaskId} (nhánh {job.Branch}) cũng sửa {shared.Count} file của task này: {Trunc(string.Join(", ", shared), 400)} — merge PR sau sẽ phải xử lý conflict.");
                o.Overlaps[job.TaskId.ToString()] = shared;
            }
            else o.Overlaps.Remove(job.TaskId.ToString());
            store.Save(o);
        }
        foreach (var (k, files) in found.Where(kv => !job.Overlaps.ContainsKey(kv.Key)))
            store.Log(job, "git", "error", $"⚠ Trùng file với task #{k}: {Trunc(string.Join(", ", files), 400)} — hai nhánh tách riêng nên PR merge sau sẽ phải xử lý conflict.");
        job.Overlaps = found;
    }

    // ---------- Nhánh nối tiếp: nhánh cha đổi sau khi tạo nhánh con ----------

    public sealed record ChainDriftInfo(string Rel, string Parent, int NewCommits, bool Rewritten);

    private sealed record ChainRepo(string Rel, string Path, string Parent, string BaseSha, RepoState? Extra);

    /// <summary>Các repo mà nhánh AI của job được tạo từ nhánh cha (task trước trong chuỗi).</summary>
    private IEnumerable<ChainRepo> ChainRepos(Job job)
    {
        if (job.ChainedFromBranch is not { } parent) yield break;
        if (job.BaseBranch == parent && job.BaseSha is { } b) yield return new(O.RepoRelativePath, O.RepoPath, parent, b, null);
        foreach (var r in job.ExtraRepos)
            if (r.BaseBranch == parent && r.BaseSha is { } rb) yield return new(r.Rel, r.Path, parent, rb, r);
    }

    private async Task<bool> IsAncestor(string path, string a, string b)
    {
        try { await GitIn(path, CancellationToken.None, "merge-base", "--is-ancestor", a, b); return true; }
        catch (PipelineException) { return false; }
    }

    private async Task<bool> RefExists(string path, string fullRef)
    {
        try { await GitIn(path, CancellationToken.None, "rev-parse", "--verify", "--quiet", fullRef); return true; }
        catch (PipelineException) { return false; }
    }

    /// <summary>Nhánh cha có commit mới (hoặc bị viết lại) kể từ lúc tạo nhánh con — PR con sẽ kéo theo bản cũ của nhánh cha.</summary>
    public async Task<List<ChainDriftInfo>> ChainDrift(Job job)
    {
        var list = new List<ChainDriftInfo>();
        if (job.Branch is null) return list;
        var none = CancellationToken.None;
        foreach (var r in ChainRepos(job))
        {
            if (!await BranchExists(r.Path, r.Parent, none)) continue; // nhánh cha đã xoá — thường là đã merge xong
            var tip = (await GitIn(r.Path, none, "rev-parse", "refs/heads/" + r.Parent)).Trim();
            if (tip == r.BaseSha) continue;
            var n = int.TryParse((await GitIn(r.Path, none, "rev-list", "--count", $"{r.BaseSha}..{tip}")).Trim(), out var c) ? c : 0;
            list.Add(new(r.Rel, r.Parent, n, !await IsAncestor(r.Path, r.BaseSha, tip)));
        }
        return list;
    }

    /// <summary>
    /// Dựng lại các commit của nhánh AI lên bản mới nhất của nhánh cha (git replay — không checkout, không đụng working copy).
    /// Làm thử ở mọi repo trước; chỉ khi tất cả không conflict mới dời nhánh.
    /// </summary>
    public async Task<PipelineException?> Restack(string id)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (job.ChainedFromBranch is null || job.Branch is null) return new PipelineException("Job không nối tiếp từ nhánh task khác.");
        if (job.IsRunning || job.Held) return new PipelineException("Job đang chạy hoặc đang giữ code chưa commit — chờ xong rồi cập nhật.");
        if (job.Status is not (JobStatus.AwaitingApproval or JobStatus.WaitingForUser or JobStatus.Approved))
            return new PipelineException("Chỉ cập nhật được job đang chờ duyệt, chờ bổ sung hoặc đã duyệt (chưa push).");
        var none = CancellationToken.None;
        var plan = new List<(ChainRepo R, string Tip, string Old, string New)>();
        try
        {
            foreach (var r in ChainRepos(job))
            {
                if (!await BranchExists(r.Path, r.Parent, none)) continue;
                var tip = (await GitIn(r.Path, none, "rev-parse", "refs/heads/" + r.Parent)).Trim();
                if (tip == r.BaseSha) continue;
                var shortBase = r.BaseSha[..Math.Min(10, r.BaseSha.Length)];
                var manual = $"git rebase --onto {r.Parent} {shortBase} {job.Branch}";
                if ((await GitIn(r.Path, none, "rev-parse", "--abbrev-ref", "HEAD")).Trim() == job.Branch)
                    return new PipelineException($"{r.Rel}: đang checkout nhánh {job.Branch} — chuyển sang nhánh khác rồi thử lại.");
                if (await RefExists(r.Path, $"refs/remotes/origin/{job.Branch}"))
                    return new PipelineException($"{r.Rel}: nhánh {job.Branch} đã push lên origin — cập nhật phải force push nên tool không tự làm. Tự chạy: {manual} rồi git push --force-with-lease.");
                var head = (await GitIn(r.Path, none, "rev-parse", "refs/heads/" + job.Branch)).Trim();
                string newHead;
                if (head == r.BaseSha) newHead = tip; // repo này task không sửa gì: chỉ dời nhánh theo cha
                else
                {
                    string output;
                    try { output = await GitIn(r.Path, none, "replay", "--onto", tip, $"{r.BaseSha}..refs/heads/{job.Branch}"); }
                    catch (PipelineException)
                    {
                        return new PipelineException($"{r.Rel}: code của task này conflict với phần mới trên {r.Parent} — không cập nhật tự động được, chưa đổi gì. " +
                                                     $"Tự xử lý: {manual} (git sẽ dừng ở chỗ conflict để bạn sửa).");
                    }
                    var m = Regex.Match(output, @"^update refs/heads/\S+ ([0-9a-f]{40,64}) ", RegexOptions.Multiline);
                    if (!m.Success) return new PipelineException($"{r.Rel}: git replay không trả về commit mới: {Trunc(output, 200)}");
                    newHead = m.Groups[1].Value;
                }
                plan.Add((r, tip, head, newHead));
            }
        }
        catch (PipelineException ex) { return ex; }
        if (plan.Count == 0) return new PipelineException("Nhánh cha không có gì mới — không cần cập nhật.");

        var done = 0;
        foreach (var (r, tip, old, nu) in plan)
        {
            try { await GitIn(r.Path, none, "update-ref", "-m", "SuperAutomation: dựng lại theo nhánh cha", "refs/heads/" + job.Branch, nu, old); }
            catch (PipelineException ex)
            {
                store.Log(job, "git", "error", $"{r.Rel}: không dời được nhánh {job.Branch}: {Trunc(ex.Message, 300)}");
                store.Save(job);
                return new PipelineException($"{r.Rel}: không dời được nhánh: {Trunc(ex.Message, 300)}" + (done > 0 ? $" ({done} repo trước đã cập nhật xong)" : ""));
            }
            if (r.Extra is null) { job.BaseSha = tip; job.HeadSha = nu; }
            else { r.Extra.BaseSha = tip; r.Extra.HeadSha = nu; }
            done++;
            store.Log(job, "git", "info", $"{r.Rel}: dựng lại nhánh {job.Branch} trên bản mới nhất của {r.Parent} ({tip[..Math.Min(10, tip.Length)]}) — PR giờ chỉ còn phần của task này.");
        }
        job.ChangedFiles = [];
        try { await CheckOverlaps(job); }
        catch (PipelineException ex) { store.Log(job, "git", "stderr", "Không kiểm được file trùng với task khác: " + ex.Message); }
        store.Save(job);
        return null;
    }

    /// <summary>Mọi repo của job đang ở nhánh AI.</summary>
    private async Task<bool> OnAiBranch(Job job)
    {
        try
        {
            if ((await GitIn(O.RepoPath, CancellationToken.None, "rev-parse", "--abbrev-ref", "HEAD")).Trim() != job.Branch) return false;
            foreach (var r in job.ExtraRepos)
                if ((await GitIn(r.Path, CancellationToken.None, "rev-parse", "--abbrev-ref", "HEAD")).Trim() != job.Branch) return false;
            return true;
        }
        catch (PipelineException) { return false; }
    }

    private async Task ResumeGit(Job job, CancellationToken ct)
    {
        if (job.Held)
        {
            // Code AI đang nằm chưa commit trên nhánh AI: làm tiếp tại chỗ, giữ nhánh trả về cũ
            if (!await OnAiBranch(job))
                throw new PipelineException($"Repo không còn ở nhánh {job.Branch} — chuyển về nhánh đó (giữ nguyên thay đổi) rồi chạy lại.");
            job.Held = false;
            store.Log(job, "git", "info", $"Làm tiếp trên nhánh {job.Branch} với thay đổi chưa commit (sẽ trả về {job.ReturnBranch}).");
            store.Save(job);
            return;
        }
        await EnsureCleanRepo(new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))), ct, "cho AI làm tiếp");
        job.ReturnBranch = await CurrentBranch(O.RepoRelativePath, O.RepoPath, ct);
        job.UntrackedBefore = await Untracked(O.RepoPath, ct);
        foreach (var r in job.ExtraRepos)
        {
            r.ReturnBranch = await CurrentBranch(r.Rel, r.Path, ct);
            r.UntrackedBefore = await Untracked(r.Path, ct);
        }
        SnapshotLocalFiles(job, new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))));
        await ClearRuntimeConflicts(job, O.RepoRelativePath, O.RepoPath, job.BaseSha, ct);
        await Git(ct, "switch", job.Branch!);
        await UntrackRuntime(job, O.RepoRelativePath, O.RepoPath, job.BaseSha, ct);
        foreach (var r in job.ExtraRepos)
        {
            await ClearRuntimeConflicts(job, r.Rel, r.Path, r.BaseSha, ct);
            await GitIn(r.Path, ct, "switch", job.Branch!);
            await UntrackRuntime(job, r.Rel, r.Path, r.BaseSha, ct);
        }
        store.Log(job, "git", "info", $"Chuyển sang lại {job.Branch} ở {1 + job.ExtraRepos.Count} repo (sẽ trả về {job.ReturnBranch}).");
        store.Save(job);
    }

    /// <summary>File web tự sinh mà nhánh AI lỡ commit (job cũ) — ở working tree đang có bản IIS vừa sinh, chưa track → git switch từ chối.
    /// Xoá bản ở working tree (IIS sẽ sinh lại) để chuyển nhánh được.</summary>
    private async Task ClearRuntimeConflicts(Job job, string rel, string path, string? baseSha, CancellationToken ct)
    {
        if (baseSha is null || job.Branch is null) return;
        var inBranch = (await GitIn(path, ct, "diff", "--name-only", baseSha, job.Branch)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => RuntimeFile.IsMatch(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inBranch.Count == 0) return;
        foreach (var f in (await Untracked(path, ct)).Where(inBranch.Contains))
        {
            try { File.Delete(Path.Combine(path, f.Replace('/', '\\'))); store.Log(job, "git", "info", $"{rel}: xoá file web tự sinh {f} để chuyển lại nhánh AI (IIS sẽ sinh lại)."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { store.Log(job, "git", "error", $"{rel}: không xoá được {f}: {ex.Message}"); }
        }
    }

    /// <summary>Gỡ file web tự sinh mà nhánh AI lỡ commit khỏi index (giữ file trên đĩa) — lần commit kế tiếp loại nó khỏi nhánh, không lọt vào PR.</summary>
    private async Task UntrackRuntime(Job job, string rel, string path, string? baseSha, CancellationToken ct)
    {
        if (baseSha is null) return;
        var files = (await GitIn(path, ct, "diff", "--name-only", "--diff-filter=A", baseSha, "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => RuntimeFile.IsMatch(f)).ToList();
        if (files.Count == 0) return;
        await GitIn(path, ct, ["rm", "--cached", "--quiet", "--", .. files]);
        store.Log(job, "git", "info", $"{rel}: bỏ {files.Count} file web tự sinh khỏi nhánh AI (lần commit sau sẽ không còn): {string.Join(", ", files.Select(Path.GetFileName))}.");
    }

    private async Task<bool> HasChanges(Job job, CancellationToken ct)
    {
        if (await RepoHasChanges(O.RepoPath, job.UntrackedBefore, ct)) return true;
        foreach (var r in job.ExtraRepos)
            if (await RepoHasChanges(r.Path, r.UntrackedBefore, ct)) return true;
        return false;
    }

    private async Task<bool> RepoHasChanges(string path, HashSet<string>? untrackedBefore, CancellationToken ct)
    {
        var rel = path == O.RepoPath ? O.RepoRelativePath : Path.GetRelativePath(O.WorkspaceRoot, path);
        if ((await repo.Dirty(rel, path, ct)).Count > 0) return true;
        var before = untrackedBefore ?? [];
        return (await Untracked(path, ct)).Any(f => !before.Contains(f));
    }

    /// <summary>Băm nội dung file; file đang bị tiến trình khác khoá ghi (vd IdentityServer.db khi Identity Server chạy) thì null.</summary>
    private static string? Sha(string file)
    {
        try
        {
            using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void SnapshotLocalFiles(Job job, IEnumerable<(string Rel, string Path)> repos)
    {
        job.LocalHashes = [];
        foreach (var (rel, path) in repos)
            foreach (var f in O.LocalFilesOf(rel))
            {
                var full = Path.Combine(path, f.Replace('/', '\\'));
                if (File.Exists(full) && Sha(full) is { } h) job.LocalHashes[$"{rel}|{f}"] = h;
            }
    }

    /// <summary>File đã đổi so với commit gốc (cả file mới chưa track), đường dẫn tương đối repo.</summary>
    private async Task<List<string>> ChangedFiles(Job job, CancellationToken ct)
    {
        var tracked = (await Git(ct, "diff", "--name-only", job.BaseSha!)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !O.IsLocalFile(O.RepoRelativePath, f));
        var before = job.UntrackedBefore ?? [];
        return tracked.Concat((await Untracked(ct)).Where(f => !before.Contains(f))).Distinct().ToList();
    }

    /// <summary>
    /// Build các project bị sửa (web IIS chạy thẳng từ working copy nên sẽ chạy code nhánh AI), gọi thử site,
    /// và chuẩn bị biến môi trường cho Playwright. Lỗi build không dừng pipeline — bước test ghi FAIL kèm log.
    /// </summary>
    private async Task<(Prompts.TestContext, Dictionary<string, string>)> PrepareTest(Job job, string dir, CancellationToken ct)
    {
        var te = testEnv.Options;
        var ui = TestModeFor(job) == "full"; // chế độ "chỉ kiểm dữ liệu" = không Playwright
        var changed = await ChangedFiles(job, ct);
        var projects = testEnv.ProjectsFor(changed);
        bool ran = false, ok = true;
        var summary = "";
        var warm = "";
        if (te.BuildBeforeTest && projects.Count > 0)
        {
            SetStatus(job, JobStatus.Testing, "build", $"Build {projects.Count} project trên nhánh AI (kèm project phụ thuộc) để web IIS chạy code mới");
            var b = await testEnv.Build(projects, withDependents: true, TimeSpan.FromMinutes(O.StageTimeoutMinutes), ct);
            ran = true;
            ok = b.Ok;
            job.BuiltProjects = projects;
            await File.WriteAllTextAsync(Path.Combine(dir, "build.txt"), $"Projects:\n{string.Join("\n", projects)}\n\n{b.Output}", new UTF8Encoding(false), ct);
            summary = b.Ok ? "Build đạt" : "Build lỗi";
            store.Log(job, "build", b.Ok ? "info" : "error", $"{summary}: {string.Join(", ", projects.Select(Path.GetFileNameWithoutExtension))}" + (b.Ok ? "" : $" — {Trunc(b.Output, 400)}"));
            store.Save(job);
        }
        var affected = testEnv.AffectedSites(changed, projects);
        if (ok && ui)
        {
            var main = testEnv.Sites().Where(s => s.Url == testEnv.BaseUrl());
            warm = await testEnv.WarmUp(affected.Concat(main).DistinctBy(s => s.Url).Concat(testEnv.ExtraSiteList()), ct);
            if (warm.Length > 0) store.Log(job, "build", "info", "Gọi thử web: " + warm);
        }
        var env = new Dictionary<string, string>();
        var hasCreds = te.User.Length > 0 && te.Password.Length > 0;
        if (ui && hasCreds)
        {
            env["HRM_BASE_URL"] = testEnv.BaseUrl();
            env["HRM_USER"] = te.User;
            env["HRM_PASSWORD"] = te.Password;
            // Portal V3 (ngoài IIS): auth.setup mở sẵn một lần với ngôn ngữ VN để Portal không đẩy màn về 403 (xem tools/e2e/tests/auth.setup.ts)
            if (testEnv.ExtraSiteList().FirstOrDefault(s => s.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase)) is { } portal)
                env["HRM_PORTAL_URL"] = portal.Url;
        }
        // Tài khoản thêm (vai trò khác): HRM_USER_<n>/HRM_PASSWORD_<n>; auth.setup.ts đăng nhập từng tài khoản, lưu .auth/acc<n>.json
        var accounts = ui && hasCreds ? testEnv.UsableAccounts() : [];
        if (accounts.Count > 0)
        {
            env["HRM_ACCOUNT_COUNT"] = accounts.Count.ToString();
            for (var i = 0; i < accounts.Count; i++)
            {
                env[$"HRM_USER_{i + 1}"] = accounts[i].User;
                env[$"HRM_PASSWORD_{i + 1}"] = accounts[i].Password!;
            }
        }
        Directory.CreateDirectory(Path.Combine(dir, "test-artifacts"));
        var portalNote = ui ? await CheckPortalDev(job, ct) : "";
        var (loggedIn, accOk) = ok && ui && hasCreds ? await PreLogin(job, env, accounts.Count, ct) : (false, new bool[accounts.Count]);
        var accInfo = accounts.Select((a, i) => new Prompts.TestAccountInfo(i + 1, a.Role, a.User, accOk[i])).ToList();
        return (new Prompts.TestContext(ran, ok, summary, testEnv.Sites(), testEnv.ExtraSiteList(), affected, testEnv.BaseUrl(), ui, hasCreds, warm,
            PreLoggedIn: loggedIn, Accounts: accInfo, DefaultUser: te.User, PortalNote: portalNote), env);
    }

    /// <summary>
    /// Portal V3 = vỏ :4200 + mỗi module một dev server (Module Federation). Module task sửa mà chưa chạy thì AI mở màn chỉ thấy dashboard
    /// → báo ngay trong nhật ký (kèm lệnh cần chạy) và đưa trạng thái vào prompt test. Trả về ghi chú cho prompt ("" nếu không có repo Portal).
    /// </summary>
    private async Task<string> CheckPortalDev(Job job, CancellationToken ct)
    {
        try
        {
            var repo = job.ExtraRepos.FirstOrDefault(r => PortalDev.IsPortalRepo(r.Path));
            var root = repo?.Path ?? O.ExtraRepoPaths.Select(p => p.Path).FirstOrDefault(PortalDev.IsPortalRepo);
            if (root is null) return "";
            var shellPort = testEnv.ExtraSiteList().Where(s => s.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase))
                .Select(s => Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Port : 0).FirstOrDefault(p => p > 0);
            var mods = PortalDev.Modules(root, shellPort > 0 ? shellPort : 4200);
            var changed = new List<string>();
            if (repo?.BaseSha is { Length: > 0 } baseSha)
            {
                changed.AddRange((await GitIn(root, ct, "diff", "--name-only", baseSha)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                changed.AddRange(await Untracked(root, ct));
            }
            var touched = PortalDev.TouchedProjects(changed);
            var shell = mods[0];
            var need = mods.Skip(1).Where(m => touched.Contains(m.Project)).ToList();
            var missing = (shell.Running ? Array.Empty<PortalDev.Module>() : new[] { shell }).Concat(need.Where(m => !m.Running)).ToList();
            // Tự bật phần còn thiếu (chỉ cổng trống; bật song song, chờ build xong) — bật xong để chạy tiếp cho task sau
            if (missing.Count > 0 && testEnv.Options.AutoStartPortal)
            {
                store.Log(job, "build", "info", $"Portal chưa chạy {string.Join(", ", missing.Select(m => $"{m.Project} :{m.Port}"))} — tool tự bật: {string.Join(" + ", missing.Select(m => "yarn " + m.Script))} (build lần đầu 2–5 phút).");
                var results = await Task.WhenAll(missing.Select(m => portalDev.EnsureRunning(root, m, TimeSpan.FromMinutes(6), ct)));
                foreach (var (ok, msg) in results) store.Log(job, "build", ok ? "info" : "error", "Portal: " + msg);
                mods = PortalDev.Modules(root, shellPort > 0 ? shellPort : 4200);
                shell = mods[0];
                need = mods.Skip(1).Where(m => touched.Contains(m.Project)).ToList();
                missing = (shell.Running ? Array.Empty<PortalDev.Module>() : new[] { shell }).Concat(need.Where(m => !m.Running)).ToList();
            }
            var running = mods.Where(m => m.Running).Select(m => $"{m.Project} :{m.Port}");
            if (missing.Count > 0)
                store.Log(job, "build", "error",
                    $"Portal chưa chạy {string.Join(", ", missing.Select(m => $"{m.Project} (cổng {m.Port})"))} — AI sẽ không mở được màn Portal để test giao diện. " +
                    $"Chạy trong {root}: {string.Join(" + ", missing.Select(m => "yarn " + m.Script))}, rồi gõ \"#{job.TaskId} test lại\" hoặc bấm Test lại.");
            else if (need.Count > 0)
                store.Log(job, "build", "info", $"Portal dev server sẵn sàng: {string.Join(", ", running)}");
            var sb = new System.Text.StringBuilder();
            sb.Append($"- Portal V3 dev server (repo {root}): đang chạy {(running.Any() ? string.Join(", ", running) : "KHÔNG có gì")}.");
            if (need.Count > 0) sb.Append($" Task sửa FE module: {string.Join(", ", need.Select(m => m.Project))}.");
            if (missing.Count > 0)
                sb.Append($"\n  CHƯA CHẠY: {string.Join(", ", missing.Select(m => $"{m.Project} :{m.Port} (yarn {m.Script})"))} → màn của module này KHÔNG mở được qua giao diện." +
                          " Đừng mất thời gian dò; kiểm bằng API/DB và ghi \"chưa kiểm được qua giao diện: module Portal chưa chạy\".");
            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PipelineException)
        {
            store.Log(job, "build", "info", "Không kiểm được dev server Portal: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Đăng nhập Playwright một lần trước bước test (project "setup" lưu cookie vào .auth/user.json),
    /// để mỗi lần AI chạy test dùng --no-deps, khỏi đăng nhập lại — nhật ký cho thấy AI chạy Playwright 6–10 lần mỗi bước test.
    /// </summary>
    private async Task<(bool Main, bool[] Accounts)> PreLogin(Job job, Dictionary<string, string> env, int accountCount, CancellationToken ct)
    {
        var accOk = new bool[accountCount];
        var e2e = Path.Combine(O.WorkspaceRoot, "tools", "e2e");
        if (!File.Exists(Path.Combine(e2e, "playwright.config.ts"))) return (false, accOk);
        var auth = Path.Combine(e2e, ".auth");
        // Xoá cookie cũ của tài khoản thêm để file còn lại sau lần chạy chắc chắn là đăng nhập thành công lần này
        if (Directory.Exists(auth))
            foreach (var f in Directory.GetFiles(auth, "acc*.json")) File.Delete(f);
        var started = DateTime.Now.AddSeconds(-2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3 + accountCount));
        try
        {
            var (code, output, err) = await Proc.Run("cmd.exe", e2e, ["/c", "npx", "playwright", "test", "--project=setup", "--reporter=line"], cts.Token, env);
            bool Fresh(string name) => File.Exists(Path.Combine(auth, name)) && File.GetLastWriteTime(Path.Combine(auth, name)) >= started;
            for (var i = 0; i < accountCount; i++) accOk[i] = Fresh($"acc{i + 1}.json");
            var main = code == 0 || Fresh("user.json");
            var failedAcc = Enumerable.Range(1, accountCount).Where(i => !accOk[i - 1]).ToList();
            if (main)
            {
                store.Log(job, "build", failedAcc.Count == 0 ? "info" : "error",
                    $"Đăng nhập sẵn cho Playwright ({sw.Elapsed.TotalSeconds:0}s, {1 + accountCount - failedAcc.Count}/{1 + accountCount} tài khoản) — AI chạy test với --no-deps, không phải đăng nhập lại mỗi lần." +
                    (failedAcc.Count == 0 ? "" : $" Tài khoản thêm đăng nhập lỗi: {string.Join(", ", failedAcc.Select(i => env[$"HRM_USER_{i}"]))} — kiểm tra mật khẩu trong Cấu hình › Test. {Trunc((output + "\n" + err).Trim(), 300)}"));
                return (true, accOk);
            }
            store.Log(job, "build", "error", $"Đăng nhập sẵn cho Playwright lỗi — AI sẽ tự đăng nhập khi chạy test: {Trunc((output + "\n" + err).Trim(), 400)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            store.Log(job, "build", "error", "Đăng nhập sẵn cho Playwright quá thời gian — bỏ qua, AI sẽ tự đăng nhập khi chạy test.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            store.Log(job, "build", "error", "Không chạy được npx để đăng nhập sẵn: " + ex.Message);
        }
        return (false, accOk);
    }

    public static string TestMarker(Job job) => $"AI-TEST-{job.TaskId}";

    /// <summary>
    /// Nếu được bật: AI soạn test-data.sql/test-cleanup.sql (chỉ đọc DB), tool kiểm tra và chạy seed trong transaction.
    /// Không ghi khi DB ghi khác DB AI đọc, hoặc tên DB không khớp dự án của task (trừ khi đánh dấu DB dùng chung).
    /// Trả về ghi chú cho prompt test.
    /// </summary>
    /// <summary>
    /// Bật "Tự deploy store khi test": đưa store/function/view mà nhánh AI sửa (file .sql trong Updates/Stores) lên DB test để web
    /// chạy đúng code mới. Bản cũ lưu ở sql-backup để khôi phục khi từ chối/huỷ job hoặc bấm "Khôi phục store".
    /// Trả về ghi chú cho prompt test.
    /// </summary>
    private async Task<string> PrepareSql(Job job, string dir, CancellationToken ct)
    {
        var o = testData.Options;
        if (!testData.Ready || !o.DeploySql || job.BaseSha is null) return "";
        var db = $"{o.Database} trên {o.Server}";
        List<string> files;
        try
        {
            var diff = (await GitIn(O.RepoPath, ct, "diff", "--name-only", job.BaseSha)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var untracked = (await Untracked(O.RepoPath, ct)).Where(f => !(job.UntrackedBefore ?? []).Contains(f));
            files = diff.Concat(untracked).Distinct()
                .Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && f.Contains("/Updates/Stores/", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (PipelineException ex)
        {
            store.Log(job, "sql", "error", "Không liệt kê được file SQL đã sửa: " + ex.Message);
            return "";
        }
        if (files.Count == 0) return "";
        if (!testData.SameAsReadDb())
        {
            store.Log(job, "sql", "error", $"Không deploy store: DB ghi ({db}) khác DB AI đọc qua tools/db — AI sẽ so store với DB khác DB web đang dùng.");
            return "";
        }
        if (!o.AnyProject && TestDataService.DbMatchesProject(job.ProjectName, o.Database) == false)
        {
            store.Log(job, "sql", "error", $"Không deploy store: DB {o.Database} có vẻ không thuộc dự án {ProjectMatch.Key(job.ProjectName)}.");
            return "";
        }

        var objects = new List<SqlObject>();
        var skipped = new List<string>();
        foreach (var f in files)
        {
            var full = Path.Combine(O.RepoPath, f.Replace('/', '\\'));
            if (!File.Exists(full)) { skipped.Add($"{Path.GetFileName(f)} (file bị xoá)"); continue; }
            var found = TestDataService.ParseObjects(TestDataService.ReadAnyText(full));
            if (found.Count == 0) skipped.Add($"{Path.GetFileName(f)} (không phải định nghĩa store/function/view)");
            objects.AddRange(found);
        }
        objects = objects.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        string skipNote = skipped.Count == 0 ? "" : $" Không tự deploy: {string.Join(", ", skipped)} — nếu cần cho test thì ghi rõ là chưa kiểm được.";
        if (objects.Count == 0)
        {
            if (skipped.Count > 0) store.Log(job, "sql", "info", "Không có store nào để deploy." + skipNote);
            return skipped.Count == 0 ? "" : $"Tool không deploy được file SQL nào.{skipNote}";
        }

        SetStatus(job, JobStatus.Testing, "sql", $"Deploy {objects.Count} store/function lên DB test {db}");
        var r = await testData.DeploySql(objects, Path.Combine(dir, "sql-backup"), ct);
        if (!r.Ok)
        {
            store.Log(job, "sql", "error", $"[{db}] {r.Message}");
            return $"Tool deploy store lên DB test LỖI ({r.Message}) — DB vẫn là bản cũ; dùng sp.ps1 -Diff để biết store nào khác, ghi rõ tiêu chí phụ thuộc store mới là chưa kiểm được.";
        }
        job.SqlDeployed = job.SqlDeployed.Concat(r.Done).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        job.SqlDeployDb = db;
        store.Save(job);
        var gen = r.Done.Where(n => n.StartsWith("Generate_SP_", StringComparison.OrdinalIgnoreCase)).ToList();
        store.Log(job, "sql", "info",
            (r.Done.Count > 0 ? $"[{db}] Đã deploy {r.Done.Count} store: {string.Join(", ", r.Done)}." : $"[{db}] Store trên DB đã giống nhánh AI.") +
            (r.Same.Count > 0 && r.Done.Count > 0 ? $" Đã giống DB: {string.Join(", ", r.Same)}." : "") +
            (gen.Count > 0 ? $" Đã EXEC {string.Join(", ", gen)} để sinh lại SP động." : "") +
            " Bản cũ lưu trong sql-backup — tự khôi phục khi từ chối/huỷ job." + skipNote);
        return $"Tool ĐÃ deploy lên DB test {db} các store/function của nhánh AI: {string.Join(", ", r.Done.Concat(r.Same))}" +
               (gen.Count > 0 ? $" (đã EXEC {string.Join(", ", gen)})" : "") +
               ". Web đang chạy đúng store mới — test giao diện bình thường, KHÔNG ghi \"SP chưa deploy\" cho các store này." + skipNote;
    }

    /// <summary>Trả store trên DB test về bản trước khi job deploy (đối tượng mới thì xoá).</summary>
    public async Task<SqlDeployResult> RestoreSql(Job job)
    {
        if (job.SqlDeployed.Count == 0) return new(true, [], [], "Không có store nào cần khôi phục.");
        var r = await testData.RestoreSql(Path.Combine(store.Dir(job.Id), "sql-backup"), CancellationToken.None);
        store.Log(job, "sql", r.Ok ? "info" : "error", r.Ok
            ? $"[{job.SqlDeployDb}] Đã khôi phục {r.Done.Count} store về bản trước khi deploy: {string.Join(", ", r.Done)}."
            : $"[{job.SqlDeployDb}] {r.Message} — store của nhánh AI VẪN đang trên DB test; bấm \"Khôi phục store\" để thử lại.");
        if (r.Ok) job.SqlDeployed = [];
        store.Save(job);
        return r;
    }

    private async Task<string> PrepareTestData(Job job, string dir, CancellationToken ct, bool reuse = false)
    {
        var o = testData.Options;
        if (!testData.Ready) return "";
        var db = $"{o.Database} trên {o.Server}";
        if (!testData.SameAsReadDb())
        {
            var r = testData.ReadDb();
            store.Log(job, "data", "error", $"Không tạo dữ liệu test: DB ghi ({db}) khác DB AI đọc qua tools/db ({(r is { } x ? $"{x.Database} trên {x.Server}" : "chưa cấu hình")}) — AI sẽ soạn dữ liệu theo DB này nhưng ghi sang DB kia.");
            return $"KHÔNG tạo dữ liệu được (cấu hình DB ghi khác DB đọc) — chỉ dùng dữ liệu có sẵn; tiêu chí thiếu dữ liệu ghi rõ là chưa kiểm được.";
        }
        if (!o.AnyProject && TestDataService.DbMatchesProject(job.ProjectName, o.Database) == false)
        {
            store.Log(job, "data", "error", $"Không tạo dữ liệu test: DB {o.Database} có vẻ không thuộc dự án {ProjectMatch.Key(job.ProjectName)}. Sửa DB trong ⚙ Cấu hình, hoặc đánh dấu \"DB dùng chung cho mọi dự án\".");
            return $"KHÔNG tạo dữ liệu được (DB {o.Database} không khớp dự án của task) — chỉ dùng dữ liệu có sẵn; tiêu chí thiếu dữ liệu ghi rõ là chưa kiểm được.";
        }

        var marker = TestMarker(job);
        // Chạy lại (sau review, không phải sửa thêm): script dữ liệu lần trước vẫn hợp với code → dùng lại, bỏ một phiên AI (~6 phút)
        var prevResult = Path.Combine(dir, "test-data.result.md");
        var prevVerdict = reuse && File.Exists(prevResult) ? Marker(await File.ReadAllTextAsync(prevResult, ct), "DATA") : null;
        string verdict;
        if (prevVerdict == "EXISTING" || (prevVerdict == "READY" && File.Exists(Path.Combine(dir, "test-data.sql")) && File.Exists(Path.Combine(dir, "test-cleanup.sql"))))
        {
            verdict = prevVerdict;
            store.Log(job, "data", "info", $"Dùng lại dữ liệu test của lần chạy trước ({verdict}) — bỏ bước AI soạn dữ liệu.");
        }
        else
        {
            SetStatus(job, JobStatus.Testing, "data", $"AI chuẩn bị dữ liệu test trên DB {db}");
            var result = await Claude(job, "test-data", Prompts.TestData(job, dir, O, db, marker, o.AllowExec,
                Prompts.RolesRule(testEnv.Options.User, testEnv.UsableAccounts().Select(a => a.User + (a.Role.Length > 0 ? $" ({a.Role})" : "")), testEnv.Options.TempAccounts, job.TaskId, marker)), ct);
            verdict = Marker(result, "DATA") ?? "UNKNOWN";
            store.Log(job, "data", "info", $"Kết luận dữ liệu test: {verdict}");
        }
        if (verdict != "READY") return verdict == "EXISTING"
            ? $"dùng dữ liệu có sẵn đã ghi trong {dir}\\test-data.md."
            : $"không tạo được dữ liệu (xem {dir}\\test-data.md) — tiêu chí thiếu dữ liệu ghi rõ là chưa kiểm được.";

        // Dữ liệu lần trước vẫn còn trong DB (chưa dọn) → dùng lại luôn, KHÔNG chạy lại test-data.sql (chạy lại = chèn trùng một bộ dữ liệu nữa)
        if (reuse && job.TestDataPending && job.TestDataDb == db)
        {
            store.Log(job, "data", "info", $"Dữ liệu test của lần trước vẫn còn trên {db} — dùng lại, không tạo lại.");
            return $"tool đã tạo dữ liệu test từ lần chạy trước theo {dir}\\test-data.sql trên DB {db} (mọi bản ghi có dấu {marker}), vẫn còn nguyên; dùng đúng dữ liệu ghi trong {dir}\\test-data.md. Không tự xoá — tool sẽ dọn sau.";
        }

        var seedFile = Path.Combine(dir, "test-data.sql");
        var cleanFile = Path.Combine(dir, "test-cleanup.sql");
        if (!File.Exists(seedFile) || !File.Exists(cleanFile))
        {
            store.Log(job, "data", "error", "AI báo READY nhưng thiếu test-data.sql hoặc test-cleanup.sql — không ghi dữ liệu.");
            return "không có script dữ liệu — chỉ dùng dữ liệu có sẵn.";
        }
        // Kiểm script dọn TRƯỚC khi ghi: không dọn được thì không ghi
        if (testData.Validate(await File.ReadAllTextAsync(cleanFile, ct), cleanup: true, marker) is { } cleanErr)
        {
            store.Log(job, "data", "error", $"Script dọn bị từ chối ({cleanErr}) — không ghi dữ liệu test.");
            return "script dọn không hợp lệ nên không tạo dữ liệu — chỉ dùng dữ liệu có sẵn.";
        }
        var run = await testData.Run(await File.ReadAllTextAsync(seedFile, ct), cleanup: false, marker, ct);
        store.Log(job, "data", run.Ok ? "info" : "error", $"[{db}] {run.Message}");
        if (!run.Ok) return $"tạo dữ liệu LỖI ({run.Message}) — chỉ dùng dữ liệu có sẵn; ghi rõ tiêu chí chưa kiểm được.";
        job.TestDataPending = true;
        job.TestDataDb = db;
        store.Save(job);
        return $"tool ĐÃ tạo dữ liệu test theo {dir}\\test-data.sql trên DB {db} (mọi bản ghi có dấu {marker}); dùng đúng dữ liệu ghi trong {dir}\\test-data.md. Không tự xoá — tool sẽ dọn sau.";
    }

    /// <summary>
    /// Dọn dữ liệu test tự động — chỉ khi KHÔNG bật "Giữ dữ liệu test" (KeepData). Bật thì giữ nguyên, ghi nhật ký đường dẫn script để bạn tự chạy
    /// (script dọn quét bảng lớn trên DB dùng chung có thể chạy vài phút và khoá web).
    /// </summary>
    private async Task AutoCleanup(Job job)
    {
        if (!job.TestDataPending) return;
        if (!testData.Options.KeepData) { await CleanupTestData(job); return; }
        if (job.CleanupNoted) return;
        job.CleanupNoted = true;
        store.Log(job, "data", "info", $"Giữ dữ liệu test {TestMarker(job)} trên {job.TestDataDb} (không tự dọn). Dọn tay: chạy {Path.Combine(store.Dir(job.Id), "test-cleanup.sql")} " +
                                       "hoặc bấm \"Dọn dữ liệu test\" trong job.");
        store.Save(job);
    }

    /// <summary>Chạy test-cleanup.sql. Gọi khi test xong, khi job lỗi/huỷ, hoặc khi người dùng duyệt/bấm dọn.</summary>
    public async Task<SqlRunResult> CleanupTestData(Job job)
    {
        var file = Path.Combine(store.Dir(job.Id), "test-cleanup.sql");
        if (!job.TestDataPending) return new(true, 0, "Không có dữ liệu test cần dọn.");
        if (!File.Exists(file)) return new(false, 0, "Thiếu test-cleanup.sql.");
        var run = await testData.Run(await File.ReadAllTextAsync(file), cleanup: true, TestMarker(job), CancellationToken.None);
        store.Log(job, "data", run.Ok ? "info" : "error", $"[{job.TestDataDb}] {run.Message}" + (run.Ok ? "" : " — dữ liệu test VẪN CÒN trong DB, dọn tay bằng test-cleanup.sql."));
        if (run.Ok) job.TestDataPending = false;
        store.Save(job);
        return run;
    }

    /// <summary>
    /// Commit thay đổi của AI vào nhánh AI rồi trả working copy về nhánh của người dùng — làm cho từng repo.
    /// plan = null: tự động (message chuẩn, mọi file mới của AI). Có plan: người dùng đã chọn file, sửa message, có thể push.
    /// </summary>
    private async Task FinalizeGit(Job job, CommitPlan? plan = null)
    {
        if (job.Branch is null) return;
        var returnTo = job.ReturnBranch ?? job.BaseBranch;
        if (returnTo is null) return;
        var none = CancellationToken.None;

        var head = await CommitAndReturn(job, O.RepoRelativePath, O.RepoPath, returnTo, job.UntrackedBefore, plan);
        if (head is not null) job.HeadSha = head;
        foreach (var r in job.ExtraRepos)
        {
            // Lỗi ở một repo không được chặn việc trả nhánh ở các repo còn lại
            try
            {
                var h = await CommitAndReturn(job, r.Rel, r.Path, r.ReturnBranch ?? r.BaseBranch!, r.UntrackedBefore, plan);
                if (h is not null) r.HeadSha = h;
            }
            catch (PipelineException ex)
            {
                store.Log(job, "git", "error", $"{r.Rel}: {ex.Message}");
            }
        }
        if (head is null) return;

        // Web IIS chạy thẳng từ working copy: đang chạy DLL build từ nhánh AI — build lại cho khớp code của người dùng
        if (job.BuiltProjects.Count > 0 && testEnv.Options.RebuildAfterReturn)
        {
            var existing = job.BuiltProjects.Where(File.Exists).ToList();
            store.Log(job, "build", "stage", $"Build lại {existing.Count} project trên nhánh {returnTo} để web chạy lại code của bạn");
            var b = await testEnv.Build(existing, withDependents: true, TimeSpan.FromMinutes(O.StageTimeoutMinutes), none);
            store.Log(job, "build", b.Ok ? "info" : "error", b.Ok
                ? $"Đã build lại trên {returnTo}."
                : $"Build lại trên {returnTo} lỗi (web có thể đang chạy DLL của nhánh AI): {Trunc(b.Output, 400)}");
            job.BuiltProjects = [];
        }
    }

    /// <summary>
    /// Sau khi push/tạo PR: repo nào vẫn đứng ở nhánh AI (vd bước trả nhánh trước đó bị lỗi) mà không còn code chưa commit
    /// (file cấu hình local / file web tự sinh không tính) thì chuyển về nhánh ban đầu của repo đó, rồi build lại cho web IIS.
    /// </summary>
    public async Task ReturnToBase(Job job)
    {
        if (job.Branch is null) return;
        var none = CancellationToken.None;
        var returned = new List<string>();
        foreach (var (rel, path, target) in new[] { (O.RepoRelativePath, O.RepoPath, job.ReturnBranch ?? job.BaseBranch) }
                     .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.ReturnBranch ?? r.BaseBranch))))
        {
            if (target is null) continue;
            try
            {
                if ((await GitIn(path, none, "rev-parse", "--abbrev-ref", "HEAD")).Trim() != job.Branch) continue;
                var dirty = (await repo.Dirty(rel, path, none)).Where(f => !RuntimeFile.IsMatch(f)).ToList();
                if (dirty.Count > 0)
                {
                    store.Log(job, "git", "error", $"{rel}: còn {dirty.Count} file sửa chưa commit trên nhánh {job.Branch} ({Trunc(string.Join(", ", dirty.Take(5)), 300)}) — KHÔNG tự chuyển về {target}, commit hoặc bỏ thay đổi rồi chuyển tay.");
                    continue;
                }
                await GitIn(path, none, "switch", target);
                returned.Add(rel);
                store.Log(job, "git", "info", $"{rel}: đã trả về nhánh {target} (sau khi push).");
            }
            catch (PipelineException ex)
            {
                store.Log(job, "git", "error", $"{rel}: không trả được về {target}: {Trunc(ex.Message, 300)}");
            }
        }
        job.Held = job.Held && returned.Count == 0;
        store.Save(job);
        if (returned.Contains(O.RepoRelativePath) && job.BuiltProjects.Count > 0 && testEnv.Options.RebuildAfterReturn)
        {
            var existing = job.BuiltProjects.Where(File.Exists).ToList();
            var target = job.ReturnBranch ?? job.BaseBranch;
            store.Log(job, "build", "stage", $"Build lại {existing.Count} project trên nhánh {target} để web chạy lại code của bạn");
            var b = await testEnv.Build(existing, withDependents: true, TimeSpan.FromMinutes(O.StageTimeoutMinutes), none);
            store.Log(job, "build", b.Ok ? "info" : "error", b.Ok ? $"Đã build lại trên {target}." : $"Build lại trên {target} lỗi: {Trunc(b.Output, 400)}");
            job.BuiltProjects = [];
            store.Save(job);
        }
    }

    private static void SetParent(Job job, TaskBundle bundle)
    {
        if (bundle.Parent is null) return;
        job.ParentTaskId = AmisClient.Long(bundle.Parent.Detail, "TaskID") ?? job.ParentTaskId;
        job.ParentTaskName = AmisClient.Str(bundle.Parent.Detail, "TaskName") ?? job.ParentTaskName;
    }

    /// <summary>Job cũ chưa lưu task cha thì đọc lại từ task.raw.json.</summary>
    private void EnsureParent(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.ParentTaskName))
        {
            try
            {
                var raw = Path.Combine(store.Dir(job.Id), "task.raw.json");
                if (File.Exists(raw) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(raw)) is { } j
                    && j["Parent"]?["Detail"] is { } pd)
                {
                    job.ParentTaskName = (string?)pd["TaskName"];
                    job.ParentTaskId = pd["TaskID"]?.GetValue<long>();
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
    }

    /// <summary>
    /// Message commit theo chuẩn công ty (standards/pr-naming.md mục 3.1): "[ID TASK] - Mô tả", mã + mô tả lấy từ tên task cha.
    /// Thân commit = mô tả PR (pr.md, markdown 3 mục do bước review viết, bước test điền "Test local") — TFS lấy làm mô tả PR mặc định.
    /// </summary>
    /// <summary>
    /// Nội dung pr.md (mô tả PR, cũng là thân commit). Khung mới: markdown 3 mục "🐛 Nguyên nhân gốc / 🎯 Mục đích & Thay đổi / 🔍 Ảnh hưởng & Kiểm tra".
    /// Bỏ dòng "Test local" còn để giữ chỗ (bước test không chạy). pr.md kiểu cũ (một đoạn) → xếp vào "Mục đích:".
    /// </summary>
    public string PrBody(Job job)
    {
        var file = Path.Combine(store.Dir(job.Id), "pr.md");
        if (!File.Exists(file)) return "";
        var body = File.ReadAllText(file).Replace("\r\n", "\n").Trim();
        if (body.Length == 0) return "";
        if (body.StartsWith("##"))
            return Regex.Replace(body, @"(?m)^\s*-\s*Test local[^\n]*\((để trống|bước test)[^\n]*\n?", "").Trim();
        if (!body.StartsWith("Mục đích", StringComparison.OrdinalIgnoreCase)) body = "Mục đích:\n" + body + "\nThay đổi:\n";
        return body;
    }

    /// <summary>Markdown của pr.md → văn bản thuần (bình luận AMIS): "## 🐛 Tiêu đề" → "🐛 Tiêu đề", bỏ ** và `.</summary>
    public static string PlainText(string md) =>
        Regex.Replace(Regex.Replace(md, @"(?m)^#{1,6}\s*", ""), @"\*\*|`", "").Trim();

    public string DefaultCommitMessage(Job job, bool wip)
    {
        EnsureParent(job);
        var (prTitle, _, _) = Standards.Title(job);
        job.PrTitle = prTitle;
        var subject = Standards.CommitSubject(job, wip);
        if (wip) return subject;
        var body = PrBody(job);
        return body.Length == 0 ? subject : subject + "\n\n" + body;
    }

    /// <summary>Commit phần AI sửa trong một repo vào nhánh AI (tuỳ chọn push) rồi chuyển về nhánh người dùng. Trả SHA cuối của nhánh AI, null nếu repo không ở nhánh AI.</summary>
    private async Task<string?> CommitAndReturn(Job job, string rel, string path, string returnTo, HashSet<string>? untrackedBefore, CommitPlan? plan)
    {
        var none = CancellationToken.None;
        var current = (await GitIn(path, none, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        if (current != job.Branch)
        {
            if (current != returnTo)
                store.Log(job, "git", "error", $"{rel}: nhánh hiện tại là {current}, không phải {job.Branch} — bỏ qua commit và trả nhánh, kiểm tra tay.");
            return null;
        }

        // File cấu hình local không bao giờ vào nhánh AI (pathspec exclude giữ nguyên chúng ở working tree)
        var locals = O.LocalFilesOf(rel);
        // File đã sửa mà người dùng bỏ tick (vd IdentityServer.db đổi khi chạy web) cũng giữ nguyên ở working tree, không commit
        var skip = plan?.Skip is { } sk && sk.TryGetValue(rel, out var s1) ? s1 : [];
        await GitIn(path, none, ["add", "-u", "--", ".", .. locals.Concat(skip).Select(f => $":(exclude){f}")]);
        foreach (var f in locals)
        {
            var full = Path.Combine(path, f.Replace('/', '\\'));
            if (job.LocalHashes.TryGetValue($"{rel}|{f}", out var h0) && File.Exists(full) && Sha(full) is { } h1 && h1 != h0)
                store.Log(job, "git", "error", $"{rel}: AI đã sửa file cấu hình local {f} — tool KHÔNG commit thay đổi này; kiểm tra tay nội dung file.");
        }
        // File mới: tự động thì lấy mọi file chưa track xuất hiện sau khi AI chạy; người dùng xác nhận thì đúng các file họ tick
        var untracked = await Untracked(path, none);
        var newFiles = plan?.NewFiles is { } chosen
            ? untracked.Where(f => chosen.TryGetValue(rel, out var sel) && sel.Contains(f) && !O.IsLocalFile(rel, f)).ToList()
            : untracked.Where(f => !(untrackedBefore ?? []).Contains(f) && !O.IsLocalFile(rel, f) && !RuntimeFile.IsMatch(f)).ToList();
        foreach (var chunk in newFiles.Chunk(50))
            await GitIn(path, none, ["add", "--", .. chunk]);

        // File local người dùng ĐÃ STAGE SẴN từ trước (git add / git rm --cached trên nhánh gốc, index đi theo khi tạo nhánh AI):
        // "exclude" ở trên không gỡ được chúng khỏi index → gỡ tường minh, file trên đĩa giữ nguyên. (Sự cố job #4258190.)
        var leaked = (await GitIn(path, none, "diff", "--cached", "--name-only")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => O.IsLocalFile(rel, f) || skip.Contains(f)).ToList();
        foreach (var chunk in leaked.Chunk(50))
            await GitIn(path, none, ["reset", "-q", "HEAD", "--", .. chunk]);
        if (leaked.Count > 0)
            store.Log(job, "git", "info", $"{rel}: bỏ {leaked.Count} file cấu hình local đang stage sẵn trong index khỏi commit (file trên đĩa giữ nguyên): " +
                                          string.Join(", ", leaked.Take(8).Select(Path.GetFileName)) + (leaked.Count > 8 ? "…" : ""));

        var staged = (await GitIn(path, none, "diff", "--cached", "--name-only")).Trim();
        if (staged.Length > 0)
        {
            var msg = plan?.Message is { Length: > 0 } m ? m : DefaultCommitMessage(job, wip: job.Status != JobStatus.AwaitingApproval);
            var msgFile = Path.Combine(store.Dir(job.Id), "commit-msg.txt");
            await File.WriteAllTextAsync(msgFile, msg.Replace("\r\n", "\n"), new UTF8Encoding(false));
            await GitIn(path, none, "commit", "--cleanup=whitespace", "-F", msgFile); // giữ dòng "## …" của mô tả PR (cleanup=strip sẽ xoá như comment)
            store.Log(job, "git", "info", $"{rel}: đã commit {staged.Split('\n').Length} file vào {job.Branch}: {msg.Split('\n')[0].Trim()}");
        }

        var head = (await GitIn(path, none, "rev-parse", "HEAD")).Trim();
        var pushConflicts = plan?.Push == true ? await PushConflicts(job, rel, path) : null;
        if (pushConflicts is { Count: > 0 })
        {
            job.MergePending = true;
            store.Log(job, "git", "error", $"{rel}: nhánh AI conflict với nhánh gốc mới nhất ở {pushConflicts.Count} file ({string.Join(", ", pushConflicts.Take(6).Select(Path.GetFileName))}) — " +
                                           "CHƯA push; tool sẽ tự gộp nhánh gốc, để AI giải conflict, rồi mới push và tạo PR.");
        }
        else if (plan?.Push == true && head != (rel == O.RepoRelativePath ? job.BaseSha : job.ExtraRepos.FirstOrDefault(r => r.Rel == rel)?.BaseSha))
        {
            try
            {
                await GitIn(path, none, "push", "-u", "origin", job.Branch!);
                store.Log(job, "git", "info", $"{rel}: đã push {job.Branch} lên origin.");
            }
            catch (PipelineException ex)
            {
                // Không chặn việc trả nhánh: commit vẫn còn, người dùng push tay
                store.Log(job, "git", "error", $"{rel}: push lỗi — tự chạy: git -C \"{path}\" push -u origin {job.Branch}\n{Trunc(ex.Message, 400)}");
            }
        }
        await GitIn(path, none, "switch", returnTo);
        store.Log(job, "git", "info", $"{rel}: đã trả về nhánh {returnTo}.");
        return head;
    }

    // File sinh ra khi chạy app/IDE — mặc định không commit
    // File web/IIS tự sinh khi chạy: không commit, không tính là thay đổi của AI (AssembliesRuntime/_LM_W3SVC_*.txt do ASP.NET ghi lại mỗi lần app khởi động)
    private static readonly Regex RuntimeFile = new(@"\.(db|sqlite|mdf|ldf|log|suo|user|cache)$|(^|/)AssembliesRuntime/", RegexOptions.IgnoreCase);

    /// <summary>Danh sách thay đổi đang giữ trên nhánh AI để người dùng xem trước khi commit.</summary>
    public async Task<object> PendingCommit(Job job, CancellationToken ct)
    {
        var repos = new List<object>();
        foreach (var (rel, path, before) in new[] { (O.RepoRelativePath, O.RepoPath, job.UntrackedBefore) }
                     .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.UntrackedBefore))))
        {
            var branch = (await GitIn(path, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
            var modified = (await repo.Dirty(rel, path, ct)).ToList();
            var untracked = (await Untracked(path, ct)).Where(f => !O.IsLocalFile(rel, f)).OrderBy(f => f)
                .Select(f => new { path = f, selected = before is not null && !before.Contains(f), knownBefore = before?.Contains(f) ?? false }).ToList();
            repos.Add(new { rel, path, branch, onAiBranch = branch == job.Branch, untracked, untrackedKnown = before is not null,
                modified = modified.Select(f => new { path = f, selected = !RuntimeFile.IsMatch(f) }) });
        }
        return new
        {
            branch = job.Branch, returnBranch = job.ReturnBranch, held = job.Held,
            message = DefaultCommitMessage(job, wip: job.Status != JobStatus.AwaitingApproval),
            prTitle = job.PrTitle, repos,
        };
    }

    /// <summary>
    /// Người dùng xác nhận: commit (tuỳ chọn push) phần đang giữ rồi trả nhánh.
    /// decision: "approve" → Đã duyệt, "reject" → Từ chối, null → giữ trạng thái (commit tạm cho job lỗi/huỷ).
    /// </summary>
    public async Task<PipelineException?> CommitHeld(string id, CommitPlan plan, string? decision)
    {
        var job = store.Get(id);
        if (job is null) return new PipelineException("Không thấy job.");
        if (!job.Held) return new PipelineException("Job không giữ thay đổi chưa commit.");
        if (job.IsRunning || _codeJob is not null) return new PipelineException("Đang có job chạy trong repo — chờ xong rồi commit.");
        if (decision == "approve" && job.Status != JobStatus.AwaitingApproval) return new PipelineException("Chỉ duyệt được job đang chờ duyệt.");
        if (!await OnAiBranch(job)) return new PipelineException($"Repo không ở nhánh {job.Branch} — chuyển về nhánh đó rồi thử lại.");
        if (plan.Message is { } m && !Regex.IsMatch(m.TrimStart(), @"^\[[^\]]*[0-9][^\]]*\]\s*-\s*\S"))
            store.Log(job, "git", "error", "Dòng đầu commit không theo chuẩn \"[ID TASK] - Mô tả\" (N9) — vẫn commit theo ý bạn.");
        _committing = job.Id; // ReleaseStaleHolds không coi lần chuyển nhánh này là "người dùng tự chuyển"
        try
        {
            await FinalizeGit(job, plan);
        }
        catch (PipelineException ex)
        {
            store.Log(job, "git", "error", ex.Message);
            store.Save(job);
            return ex;
        }
        finally { _committing = null; }
        job.Held = false;
        try { await CheckOverlaps(job); }
        catch (PipelineException ex) { store.Log(job, "git", "stderr", "Không kiểm được file trùng với task khác: " + ex.Message); }
        if (decision is "approve" or "reject")
        {
            job.Status = decision == "approve" ? JobStatus.Approved : JobStatus.Rejected;
            job.FinishedAt = DateTime.Now;
            store.Log(job, "decision", "stage", decision == "approve"
                ? $"Đã duyệt và commit{(plan.Push ? " + push" : "")} nhánh {job.Branch}."
                : $"Từ chối. Code AI được commit tạm vào nhánh {job.Branch} (không push).");
            await AutoCleanup(job);
            if (decision == "reject" && job.SqlDeployed.Count > 0) await RestoreSql(job);
        }
        store.Save(job);
        return null;
    }

    /// <summary>Push nhánh AI đã commit (job không giữ repo) ở mọi repo có commit mới — không đổi nhánh working copy.</summary>
    public async Task<PipelineException?> PushBranch(Job job)
    {
        if (job.Branch is null || job.HeadSha is null) return new PipelineException("Job chưa có commit trên nhánh AI.");
        if (job.Held) return new PipelineException("Code AI chưa commit — dùng hộp \"Tạo commit & push\".");
        if (job.MergePending || await AnyPushConflict(job)) return ScheduleMerge(job);
        var none = CancellationToken.None;
        var errors = new List<string>();
        foreach (var (rel, path, baseSha, head) in new[] { (O.RepoRelativePath, O.RepoPath, job.BaseSha, job.HeadSha) }
                     .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.BaseSha, r.HeadSha))))
        {
            if (head is null || head == baseSha) continue;
            try
            {
                await GitIn(path, none, "push", "-u", "origin", job.Branch);
                store.Log(job, "git", "info", $"{rel}: đã push {job.Branch} lên origin." +
                    (job.ChainedFromTask is { } p ? $" Nhánh này nối tiếp từ task #{p} — PR của #{p} phải merge trước." : ""));
            }
            catch (PipelineException ex)
            {
                errors.Add($"{rel}: {Trunc(ex.Message, 300)}");
                store.Log(job, "git", "error", $"{rel}: push lỗi — tự chạy: git -C \"{path}\" push -u origin {job.Branch}\n{Trunc(ex.Message, 400)}");
            }
        }
        store.Save(job);
        return errors.Count > 0 ? new PipelineException("Push lỗi: " + string.Join(" | ", errors)) : null;
    }

    public bool AutoPr => tfs.AutoPr;

    /// <summary>
    /// Tạo PR trên TFS cho nhánh AI (đã push) ở mọi repo có commit mới. Đích: nhánh người dùng đứng lúc giao task;
    /// task nối nhánh thì đích là nhánh của task cha để PR chỉ chứa phần của task này.
    /// </summary>
    /// <summary>
    /// Mô tả PR gọn cho TFS (giới hạn ~4000 ký tự): giữ tiêu đề mọi mục "## …", mỗi mục chia đều ngân sách, cắt theo dòng
    /// (gạch con "  - " bỏ trước), dòng dài cắt ở ~220 ký tự. Bản đầy đủ vẫn nằm trong thân commit và pr.md.
    /// </summary>
    public static string ShortPrDescription(string md, Job job, int max = 1800)
    {
        md = md.Replace("\r\n", "\n").Trim();
        if (md.Length <= max) return md;
        var footer = $"\n\n_Mô tả rút gọn — chi tiết trong thân commit và task AMIS #{job.TaskId}._";
        var sections = System.Text.RegularExpressions.Regex.Split(md, @"(?m)^(?=## )").Where(s => s.Trim().Length > 0).ToList();
        var budget = Math.Max(300, (max - footer.Length) / Math.Max(1, sections.Count));
        var outSb = new StringBuilder();
        foreach (var sec in sections)
        {
            var all = sec.Trim().Split('\n');
            var lines = all.Skip(1).Where(l => l.Trim().Length > 0)
                .Select(l => (Text: l.Length > 200 ? l[..197].TrimEnd() + "…" : l, Nested: l.StartsWith("  ") || l.StartsWith("\t"),
                              Must: l.TrimStart().StartsWith("- Test local", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var keep = new bool[lines.Count];
            var used = all[0].Length + 1;
            for (var i = 0; i < lines.Count; i++) if (lines[i].Must) { keep[i] = true; used += lines[i].Text.Length + 1; }
            // Lượt 1: gạch cấp 1 theo thứ tự; lượt 2: gạch con của gạch đã giữ, còn chỗ thì thêm
            foreach (var nestedPass in new[] { false, true })
                for (int i = 0, parent = -1; i < lines.Count; i++)
                {
                    if (!lines[i].Nested) parent = i;
                    if (keep[i] || lines[i].Nested != nestedPass || (nestedPass && (parent < 0 || !keep[parent]))) continue;
                    if (used + lines[i].Text.Length + 1 > budget) continue;
                    keep[i] = true;
                    used += lines[i].Text.Length + 1;
                }
            var sb = new StringBuilder(all[0]).Append('\n');
            for (var i = 0; i < lines.Count; i++)
            {
                if (!keep[i]) continue;
                // Gạch cấp 1 kết thúc bằng ":" mà mọi gạch con bị bỏ → thêm "…" để không bỏ lửng
                var t = lines[i].Text;
                if (!lines[i].Nested && t.TrimEnd().TrimEnd('*').EndsWith(':') && (i + 1 >= lines.Count || !lines[i + 1].Nested || !keep[i + 1])) t = t.TrimEnd() + " …";
                sb.Append(t).Append('\n');
            }
            if (keep.Contains(false)) sb.Append("- …\n");
            outSb.Append(sb).Append('\n');
        }
        return outSb.ToString().TrimEnd() + footer;
    }

    public async Task<PipelineException?> CreatePrs(Job job)
    {
        if (job.MergePending) return ScheduleMerge(job);
        if (job.Branch is null || job.HeadSha is null) return new PipelineException("Job chưa có commit trên nhánh AI.");
        if (job.Held) return new PipelineException("Code AI chưa commit — commit & push trước rồi mới tạo PR.");
        var none = CancellationToken.None;
        var (title, _, _) = Standards.Title(job);
        var desc = ShortPrDescription(PrBody(job), job);
        if (desc.Length == 0) desc = $"Task AMIS #{job.TaskId}: {job.TaskName}";
        // Khung mới đã có dòng "Test local" do bước test điền; khung cũ thì ghi thêm kết luận test
        if (!desc.StartsWith("##") && job.TestVerdict is { Length: > 0 } tv && tv != "UNKNOWN") desc += $"\n\nKiểm thử tự động: {tv}";
        var repos = new[] { (Rel: O.RepoRelativePath, Path: O.RepoPath, Base: job.BaseBranch, Return: job.ReturnBranch, BaseSha: job.BaseSha, Head: job.HeadSha) }
            .Concat(job.ExtraRepos.Select(r => (Rel: r.Rel, Path: r.Path, Base: r.BaseBranch, Return: r.ReturnBranch, BaseSha: r.BaseSha, Head: r.HeadSha)));
        var errors = new List<string>();
        var made = 0;
        foreach (var r in repos)
        {
            if (r.Head is null || r.Head == r.BaseSha) continue;
            if (job.PrUrls.ContainsKey(r.Rel)) { made++; continue; }
            if (!await RefExists(r.Path, $"refs/remotes/origin/{job.Branch}")) { errors.Add($"{r.Rel}: nhánh {job.Branch} chưa push lên origin"); continue; }
            var chained = job.ChainedFromBranch is not null && r.Base == job.ChainedFromBranch;
            var target = chained ? job.ChainedFromBranch! : r.Return ?? r.Base;
            if (target is null) { errors.Add($"{r.Rel}: không biết nhánh đích"); continue; }
            if (!await RefExists(r.Path, $"refs/remotes/origin/{target}"))
            {
                errors.Add($"{r.Rel}: nhánh đích {target} chưa có trên origin" + (chained ? $" — push/tạo PR cho task #{job.ChainedFromTask} trước" : ""));
                continue;
            }
            var d = chained ? desc + $"\n\nPR nối tiếp PR của task #{job.ChainedFromTask} (nhánh {target}) vì hai task sửa cùng file — merge PR đó trước, rồi đổi đích PR này về nhánh chính." : desc;
            try
            {
                var pr = await tfs.CreatePr(r.Path, job.Branch, target, title, d, none);
                job.PrUrls[r.Rel] = pr.Url;
                made++;
                store.Log(job, "git", "info", $"{r.Rel}: {(pr.Existed ? "đã có" : "đã tạo")} PR #{pr.Id} {job.Branch} → {target}: {pr.Url}");
            }
            catch (Exception ex) when (ex is PipelineException or HttpRequestException or TaskCanceledException)
            {
                errors.Add($"{r.Rel}: {Trunc(ex.Message, 300)}");
                store.Log(job, "git", "error", $"{r.Rel}: tạo PR lỗi — {Trunc(ex.Message, 400)}");
            }
        }
        store.Save(job);
        if (errors.Count > 0) return new PipelineException("Tạo PR lỗi: " + string.Join(" | ", errors));
        return made == 0 ? new PipelineException("Không có repo nào có commit mới để tạo PR.") : null;
    }

    public async Task<string> Diff(Job job, CancellationToken ct)
    {
        var parts = new List<string> { await RepoDiff(job, O.RepoPath, job.BaseSha, job.HeadSha, ct) };
        foreach (var r in job.ExtraRepos)
        {
            var d = await RepoDiff(job, r.Path, r.BaseSha, r.HeadSha, ct);
            if (d.Trim().Length > 0) parts.Add($"######## Repo {r.Rel} ########\n" + d);
        }
        if (job.ExtraRepos.Count > 0 && parts[0].Trim().Length > 0) parts[0] = $"######## Repo {O.RepoRelativePath} ########\n" + parts[0];
        return string.Join("\n", parts.Where(p => p.Trim().Length > 0));
    }

    private async Task<string> RepoDiff(Job job, string path, string? baseSha, string? headSha, CancellationToken ct)
    {
        if (baseSha is null) return "";
        var current = (await GitIn(path, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        // Đang chạy trên nhánh AI: diff gồm cả phần chưa commit; ngược lại so sánh với commit cuối của nhánh AI
        string[] range = current == job.Branch ? [baseSha] : headSha is not null ? [baseSha, headSha] : [];
        if (range.Length == 0) return "";
        var stat = await GitIn(path, ct, ["diff", "--stat", .. range]);
        var full = await GitIn(path, ct, ["diff", .. range]);
        const int max = 1_500_000;
        return stat + "\n" + (full.Length > max ? full[..max] + "\n… (diff quá dài, đã cắt)" : full);
    }

    private async Task<HashSet<string>> Untracked(string path, CancellationToken ct) =>
        (await GitIn(path, ct, "ls-files", "--others", "--exclude-standard", "-z"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private Task<HashSet<string>> Untracked(CancellationToken ct) => Untracked(O.RepoPath, ct);

    private Task<string> GitIn(string path, CancellationToken ct, params string[] args) => repo.GitIn(path, ct, args);

    private Task<string> Git(CancellationToken ct, params string[] args) => repo.Git(ct, args);

    // ---------- Claude Code ----------

    /// <summary>Chạy một phiên `claude -p` cho một bước, đẩy diễn biến lên log; trả về câu trả lời cuối.</summary>
    private async Task<string> Claude(Job job, string stage, string prompt, CancellationToken ct, bool captureSession = false, string? resume = null, bool readOnly = false, IReadOnlyDictionary<string, string>? extraEnv = null, string? sideResume = null, double? stageBudget = null)
    {
        var dir = store.Dir(job.Id);
        // Bài học từ góp ý trước đây + task tương tự đã làm — giúp AI đỡ dò lại và không lặp lỗi cũ
        if (insights.Context(job, stage) is { Length: > 0 } extra) prompt = extra + prompt;
        await File.WriteAllTextAsync(Path.Combine(dir, $"{stage}.prompt.md"), prompt, new UTF8Encoding(false), ct);

        // Bước phân tích dùng bộ công cụ chỉ đọc của chat
        var timeout = readOnly ? O.AnalyzeTimeoutMinutes : O.StageTimeoutMinutes;
        var args = readOnly
            ? ClaudeCli.BaseArgs("default", O.Model, O.ChatAllowedTools, O.ChatDisallowedTools)
            : ClaudeCli.BaseArgs(O.PermissionMode, O.Model, O.AllowedTools, O.DisallowedTools);
        args.AddRange(["--add-dir", dir]);
        if (resume is not null) args.AddRange(["--resume", resume]);
        else if (sideResume is not null) args.AddRange(["--resume", sideResume]);

        // Trần chi phí cả job: phiên này chỉ được tiêu phần còn lại
        var cap = settings.CostCap(O.MaxJobCostUsd);
        double? jobLeft = null;
        if (cap is { } c)
        {
            var left = Math.Round(c - job.CostUsd, 2);
            if (left < 0.05)
                throw new PipelineException($"Job đã dùng ${job.CostUsd:0.00}, chạm giới hạn chi phí ${c:0.00}/job — chưa chạy bước {stage}. " +
                                            "Tăng giới hạn ở ⚙ Cấu hình rồi bấm chạy tiếp nếu muốn làm tiếp.");
            jobLeft = left;
        }
        // Trần riêng của bước (vd bước test) — lấy trần nhỏ hơn giữa trần bước và phần còn lại của job
        var stageBinding = stageBudget is { } sb && (jobLeft is null || sb < jobLeft);
        if ((stageBinding ? stageBudget : jobLeft) is { } budgetArg)
            args.AddRange(["--max-budget-usd", Math.Round(budgetArg, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        // Cho AI ghi DB test qua tool (mã riêng của job, hết hiệu lực khi job dừng) — chỉ phiên làm việc, không phải phân tích/chat
        if (!readOnly && AiWriteEnabled)
        {
            var env2 = new Dictionary<string, string>(extraEnv ?? new Dictionary<string, string>())
            {
                ["SA_SQL_URL"] = "http://localhost:5080/api/ai-sql",
                ["SA_SQL_TOKEN"] = _sqlTokens.GetOrAdd(job.Id, _ => Guid.NewGuid().ToString("N")),
            };
            extraEnv = env2;
        }
        var run = await ClaudeCli.Run(O.ClaudePath, O.WorkspaceRoot, args, prompt, Path.Combine(dir, $"{stage}.jsonl"),
            e =>
            {
                // Chạy bằng API key thì Claude Code luôn báo tắt connector claude.ai — vô hại với pipeline, ghi dạng thông tin cho đỡ rối
                var kind = e.Kind == "init" || (e.Kind == "stderr" && e.Text.Contains("claude.ai connectors are disabled", StringComparison.OrdinalIgnoreCase))
                    ? "info" : e.Kind;
                store.Log(job, stage, kind, e.Text);
                // Ghi lại mã phiên ngay khi bắt đầu — bị huỷ/lỗi giữa chừng vẫn làm tiếp được phiên này
                if (captureSession && e.Kind == "init" && Regex.Match(e.Text, @"Phiên Claude ([0-9a-f-]{36})") is { Success: true } sm)
                {
                    job.CodeSessionId = sm.Groups[1].Value;
                    job.CodeSessionCostUsd = 0;
                    store.Save(job);
                }
            },
            TimeSpan.FromMinutes(timeout), ct, WithEnv(settings.ClaudeAuth(), extraEnv));

        // Phiên --resume báo tổng cộng dồn cả phiên → chỉ cộng phần tăng thêm
        var resumed = resume is not null && run.SessionId == resume;
        var spent = resumed ? Math.Max(0, run.CostUsd - job.CodeSessionCostUsd)
            : sideResume is not null && run.SessionId == sideResume && _lastRun.TryGetValue(job.Id, out var prev) ? Math.Max(0, run.CostUsd - prev.Cost)
            : run.CostUsd;
        if (run.SessionId is not null) _lastRun[job.Id] = (run.SessionId, run.CostUsd);
        job.CostUsd += spent;
        costs.Add(new CostEntry(DateTime.Now, job.TaskId, job.TaskName, job.Id, "job", stage, run.Model, run.Paid, run.AuthLabel,
            Math.Round(spent, 6), run.InTokens, run.OutTokens, run.CacheRead, run.CacheWrite));
        if ((captureSession || resume is not null) && run.SessionId is not null)
        {
            job.CodeSessionId = run.SessionId;
            job.CodeSessionCostUsd = run.CostUsd;
        }
        if (run.TimedOut)
            throw new PipelineException($"Bước {stage} chạy quá {timeout} phút, đã dừng.");
        if (run.Subtype == "error_max_budget_usd")
        {
            store.Save(job);
            if (stageBinding)
                throw new StageBudgetException($"Bước {stage} chạm trần chi phí riêng ${stageBudget:0.00}.");
            throw new PipelineException($"Dừng ở bước {stage}: chạm giới hạn chi phí ${cap:0.00}/job (đã dùng ${job.CostUsd:0.00}). " +
                                        "Phần AI đã làm vẫn giữ trên nhánh AI — tăng giới hạn ở ⚙ Cấu hình rồi bấm chạy tiếp, hoặc xử lý tay.");
        }
        if (run.Result is null)
            throw new PipelineException($"Claude kết thúc bước {stage} mà không có kết quả (exit {run.ExitCode}, {run.Subtype ?? "không rõ"}). Xem {stage}.jsonl.");
        if (run.IsError)
            throw new PipelineException($"Claude báo lỗi ở bước {stage} ({run.Subtype}): {Trunc(run.Result, 500)}");

        await File.WriteAllTextAsync(Path.Combine(dir, $"{stage}.result.md"), run.Result, new UTF8Encoding(false), CancellationToken.None);
        if (stage.StartsWith("code")) await CopyResult(job, Path.Combine(dir, $"{stage}.result.md"), Path.Combine(dir, "code.result.md"));
        store.Save(job);
        return run.Result;
    }

    // ---------- AI ghi DB test ----------

    private readonly ConcurrentDictionary<string, string> _sqlTokens = new(); // jobId → mã

    public bool AiWriteEnabled => testData.Ready && testData.Options.AllowAiWrite;

    /// <summary>Job đang chạy sở hữu mã này (mã hết hiệu lực khi job không còn chạy).</summary>
    public Job? JobBySqlToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var id = _sqlTokens.FirstOrDefault(kv => kv.Value == token).Key;
        return id is not null && store.Get(id) is { IsRunning: true } j ? j : null;
    }

    /// <summary>Chạy script AI gửi lên + ghi nhật ký vào data/jobs/{id}/db-writes.sql (cả script bị từ chối).</summary>
    public async Task<(bool Ok, string Output)> RunAiSql(Job job, string sql, CancellationToken ct)
    {
        var r = await testData.RunAi(sql, ct);
        var o = testData.Options;
        var entry = $"-- ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · bước {job.Status} · {(r.Ok ? "ĐÃ COMMIT" : "KHÔNG GHI")} · {o.Database} trên {o.Server}\n" +
                    sql.Trim() + "\n/* Kết quả:\n" + Trunc(r.Output.Replace("*/", "* /"), 2000) + "\n*/\nGO\n\n";
        await File.AppendAllTextAsync(Path.Combine(store.Dir(job.Id), "db-writes.sql"), entry, new UTF8Encoding(false), CancellationToken.None);
        store.Log(job, "sql", r.Ok ? "info" : "error", $"AI ghi DB test: {Trunc(r.Output.Split('\n')[0], 300)}");
        if (r.Ok) { job.AiDbWrites++; store.Save(job); }
        return r;
    }

    /// <summary>Người dùng bấm hoàn tác: chạy db-undo.sql (AI soạn) qua cùng lớp kiểm.</summary>
    public async Task<(bool Ok, string Output)> UndoAiSql(Job job)
    {
        var file = Path.Combine(store.Dir(job.Id), "db-undo.sql");
        if (!File.Exists(file)) return (false, "Không có db-undo.sql.");
        var r = await testData.RunAi(await File.ReadAllTextAsync(file), CancellationToken.None);
        store.Log(job, "sql", r.Ok ? "info" : "error", $"Hoàn tác thay đổi DB của AI (db-undo.sql): {Trunc(r.Output.Split('\n')[0], 300)}");
        if (r.Ok)
        {
            File.Move(file, Path.Combine(store.Dir(job.Id), $"db-undo.done-{DateTime.Now:yyyyMMdd-HHmmss}.sql"));
            job.AiDbWrites = 0;
        }
        store.Save(job);
        return r;
    }

    /// <summary>Phiên Claude gần nhất của mỗi job (mã phiên, chi phí cộng dồn) — để nhắc AI làm nốt đúng phiên đó.</summary>
    private readonly ConcurrentDictionary<string, (string Session, double Cost)> _lastRun = new();

    /// <summary>
    /// Phiên kết thúc mà thiếu dòng kết luận (ví dụ AI gọi agent chạy nền rồi dừng ở "đang chờ") → tiếp tục đúng phiên đó MỘT lần
    /// để AI làm nốt và ghi kết luận, thay vì để kết quả UNKNOWN.
    /// </summary>
    private async Task<string> EnsureMarker(Job job, string stage, string text, string key, string nudge, CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (Marker(text, key) is not null || !_lastRun.TryGetValue(job.Id, out var last)) return text;
        store.Log(job, stage, "info", $"Bước {stage} kết thúc mà chưa có dòng {key} — nhắc AI làm nốt trong cùng phiên.");
        try { return await Claude(job, stage + "-finish", nudge, ct, extraEnv: extraEnv, sideResume: last.Session); }
        catch (PipelineException ex)
        {
            store.Log(job, stage, "error", $"Nhắc AI ghi kết luận không thành: {ex.Message}");
            return text;
        }
    }

    /// <summary>
    /// Test FAIL có test case FAIL (lỗi code, không phải môi trường) → một vòng AI tái hiện + sửa. Trả ghi chú cho lần test lại nếu đã sửa,
    /// null nếu không chạy vòng sửa (tắt, đã sửa rồi, task QC, không có TC FAIL, hoặc AI không tái hiện được).
    /// </summary>
    private async Task<string?> TryFixFromTest(Job job, string dir, string testMode, CancellationToken ct)
    {
        if (testMode == "off" || job.TestVerdict != "FAIL" || job.QcFix || !testEnv.Options.AutoFixOnTestFail) return null;
        var maxRounds = Math.Max(1, testEnv.Options.MaxTestFixRounds);
        if (job.TestFixRounds >= maxRounds)
        {
            store.Log(job, "test", "info", $"Test vẫn FAIL sau {job.TestFixRounds} vòng tự sửa (tối đa {maxRounds}) — dừng, chờ bạn xem.");
            return null;
        }
        List<TestCases.Case> failed;
        try { failed = (TestCases.Load(dir) ?? []).Where(c => c.Status == "FAIL").ToList(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { failed = []; }
        if (failed.Count == 0)
        {
            store.Log(job, "test", "info", "Test FAIL nhưng không có test case FAIL cụ thể trong testcases.json — không tự sửa, chờ bạn xem.");
            return null;
        }
        // Từ vòng 2: chỉ sửa tiếp khi vòng trước làm giảm số test case FAIL — không thì AI đang không sửa được, dừng để khỏi tốn tiền
        if (job.TestFixRounds > 0 && job.TestFixPrevFails is { } prev && failed.Count >= prev)
        {
            store.Log(job, "test", "info", $"Vòng tự sửa {job.TestFixRounds} không làm giảm số test case FAIL ({prev} → {failed.Count}) — dừng tự sửa, chờ bạn xem.");
            return null;
        }
        if (job.TestFixPrevFails is { } before) store.Log(job, "test", "info", $"Vòng tự sửa {job.TestFixRounds} có tiến triển ({before} → {failed.Count} test case FAIL) — sửa tiếp vòng {job.TestFixRounds + 1}/{maxRounds}.");
        job.TestFixPrevFails = failed.Count;
        job.TestFixRounds++;
        var snaps = await SnapshotRepos(job, ct);
        var list = string.Join("\n", failed.Select(c => $"  - {c.Id} {c.Title}: mong đợi \"{Trunc(c.Expected, 200)}\", thực tế \"{Trunc(c.Actual, 300)}\"" + (c.Note.Length > 0 ? $" (ghi chú: {Trunc(c.Note, 200)})" : "")));
        SetStatus(job, JobStatus.Fixing, "fix", $"Tự sửa theo kết quả test: {failed.Count} test case FAIL ({string.Join(", ", failed.Select(c => c.Id))})");
        var fix = await Claude(job, $"testfix{job.TestFixRounds}", Prompts.TestFix(job, dir, O, list), ct);
        var result = Marker(fix, "FIX") ?? "UNKNOWN";
        store.Log(job, "fix", result == "DONE" ? "info" : "error", result switch
        {
            "DONE" => "AI đã tái hiện và sửa lỗi theo test (chi tiết: testfix.md) — review và test lại các test case đã FAIL.",
            "NOT_REPRODUCED" => "AI không tái hiện được lỗi test báo — không sửa code, chờ bạn xem (testfix.md).",
            "NOT_CODE" => "AI xác định lỗi do môi trường/dữ liệu test, không phải code — không sửa, chờ bạn xem (testfix.md).",
            _ => $"Vòng tự sửa theo test kết thúc không rõ kết quả ({result}) — chờ bạn xem.",
        });
        if (result != "DONE") return null;
        await WriteDeltaDiff(job, snaps, Path.Combine(dir, "testfix.diff"), ct);
        return $"Vừa tự sửa theo test FAIL (xem {Path.Combine(dir, "testfix.md")}). Kiểm lại ĐÚNG các test case đã FAIL: {string.Join(", ", failed.Select(c => c.Id))}, " +
               "cộng test case liên quan trực tiếp tới phần vừa sửa; các test case khác giữ kết quả cũ.";
    }

    /// <summary>Dấu vân tay code hiện tại của job: diff so với gốc ở mọi repo + nội dung file mới chưa track (không tính file local/runtime).</summary>
    private async Task<string> CodeFingerprint(Job job, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (rel, path, baseSha, before) in new[] { (O.RepoRelativePath, O.RepoPath, job.BaseSha, job.UntrackedBefore) }
                     .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.BaseSha, r.UntrackedBefore))))
        {
            if (baseSha is null) continue;
            sb.Append(rel).Append('\n').Append(await GitIn(path, ct, "diff", baseSha));
            foreach (var f in (await Untracked(path, ct)).Where(f => !(before ?? []).Contains(f) && !O.IsLocalFile(rel, f) && !RuntimeFile.IsMatch(f)).OrderBy(f => f))
                sb.Append(f).Append(':').Append(Sha(Path.Combine(path, f.Replace('/', '\\'))) ?? "?").Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Chụp trạng thái code từng repo (git stash create: chỉ tạo object, không đổi nhánh/index/working tree) + danh sách file chưa track.</summary>
    private async Task<List<(string Rel, string Path, string Sha, HashSet<string> Untracked)>> SnapshotRepos(Job job, CancellationToken ct)
    {
        var list = new List<(string, string, string, HashSet<string>)>();
        foreach (var (rel, path) in new[] { (O.RepoRelativePath, O.RepoPath) }.Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path))))
        {
            var sha = (await GitIn(path, ct, "stash", "create")).Trim();
            if (sha.Length == 0) sha = (await GitIn(path, ct, "rev-parse", "HEAD")).Trim();
            list.Add((rel, path, sha, await Untracked(path, ct)));
        }
        return list;
    }

    /// <summary>Diff của phần sửa sau snapshot (file đã track) + file mới xuất hiện sau snapshot — cho bước review gọn.</summary>
    private async Task WriteDeltaDiff(Job job, List<(string Rel, string Path, string Sha, HashSet<string> Untracked)> snaps, string file, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (rel, path, sha, before) in snaps)
        {
            var d = await GitIn(path, ct, "diff", sha);
            var added = (await Untracked(path, ct)).Where(f => !before.Contains(f) && !O.IsLocalFile(rel, f) && !RuntimeFile.IsMatch(f)).ToList();
            if (d.Trim().Length == 0 && added.Count == 0) continue;
            sb.Append($"######## Repo {rel} ########\n").Append(d);
            if (added.Count > 0) sb.Append("\n# File mới tạo trong vòng sửa (đọc trực tiếp):\n").Append(string.Join("\n", added.Select(f => "#   " + f))).Append('\n');
        }
        await File.WriteAllTextAsync(file, sb.Length > 0 ? sb.ToString() : "(vòng sửa không đổi file nào đã track)\n", new UTF8Encoding(false), ct);
    }

    // ---------- Conflict với nhánh gốc khi push ----------

    /// <summary>Nhánh gốc (trên origin) mà PR của repo này sẽ merge vào; null nếu nhánh gốc là nhánh AI khác (nối tiếp) hoặc không có trên origin.</summary>
    private async Task<string?> UpstreamOf(Job job, string rel, string path)
    {
        var baseBranch = rel == O.RepoRelativePath ? job.BaseBranch : job.ExtraRepos.FirstOrDefault(r => r.Rel == rel)?.BaseBranch;
        if (baseBranch is null || baseBranch.StartsWith("ai/")) return null;
        var (code, _, _) = await Proc.Run("git", path, ["rev-parse", "--verify", "-q", "origin/" + baseBranch], CancellationToken.None);
        return code == 0 ? "origin/" + baseBranch : null;
    }

    /// <summary>Fetch rồi thử merge (merge-tree: chỉ tính, không đụng working tree) nhánh AI vào nhánh gốc mới nhất. Trả danh sách file conflict (rỗng = merge sạch).</summary>
    private async Task<List<string>> PushConflicts(Job job, string rel, string path)
    {
        try
        {
            await Proc.Run("git", path, ["fetch", "-q", "origin"], CancellationToken.None);
            if (await UpstreamOf(job, rel, path) is not { } up || job.Branch is null) return [];
            var (code, output, _) = await Proc.Run("git", path, ["merge-tree", "--write-tree", "--name-only", "--no-messages", up, job.Branch], CancellationToken.None);
            if (code == 0) return [];
            // Dòng 1 = tree OID, sau đó là các file conflict tới dòng trống
            return output.Replace("\r", "").Split('\n').Skip(1).TakeWhile(l => l.Trim().Length > 0).Select(l => l.Trim()).Distinct().ToList();
        }
        catch (Exception ex) when (ex is PipelineException or System.ComponentModel.Win32Exception)
        {
            store.Log(job, "git", "info", $"{rel}: không kiểm được conflict trước khi push ({ex.Message}) — push như bình thường.");
            return [];
        }
    }

    private async Task<bool> AnyPushConflict(Job job)
    {
        var any = false;
        foreach (var (rel, path, baseSha, head) in new[] { (O.RepoRelativePath, O.RepoPath, job.BaseSha, job.HeadSha) }
                     .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, r.BaseSha, r.HeadSha))))
        {
            if (head is null || head == baseSha) continue;
            if (await PushConflicts(job, rel, path) is { Count: > 0 } cf)
            {
                any = true;
                store.Log(job, "git", "error", $"{rel}: nhánh AI conflict với nhánh gốc mới nhất ở {cf.Count} file ({string.Join(", ", cf.Take(6).Select(Path.GetFileName))}) — CHƯA push.");
            }
        }
        return any;
    }

    /// <summary>Đưa job vào hàng đợi code ở chế độ "gộp nhánh": tool merge nhánh gốc, AI giải conflict, rồi push + tạo PR.</summary>
    private PipelineException ScheduleMerge(Job job)
    {
        if (job.ResumeFrom != "merge" || job.Status != JobStatus.Queued)
        {
            job.MergePending = true;
            job.ResumeFrom = "merge";
            job.CodeApproved = true;
            job.Status = JobStatus.Queued;
            store.Log(job, "control", "stage", "Xếp hàng gộp nhánh gốc mới nhất vào nhánh AI + AI giải conflict — xong tự push và tạo PR.");
            store.Save(job);
            Enqueue(job);
        }
        return new PipelineException("nhánh AI conflict với nhánh gốc mới nhất — tool đang tự gộp nhánh gốc và để AI giải conflict; xong sẽ tự push và tạo PR (theo dõi trong job).");
    }

    /// <summary>
    /// Chế độ "gộp nhánh": chuyển repo sang nhánh AI, merge nhánh gốc mới nhất (--no-commit), AI giải conflict, tool kiểm hết dấu conflict,
    /// commit merge, push, tạo PR, trả nhánh. Không giải được → merge --abort, trả nhánh, báo người dùng xử lý tay.
    /// </summary>
    private async Task MergeResolve(Job job, CancellationToken ct)
    {
        var dir = store.Dir(job.Id);
        var none = CancellationToken.None;
        var repos = new[] { (Rel: O.RepoRelativePath, Path: O.RepoPath, Base: job.BaseSha, Head: job.HeadSha) }
            .Concat(job.ExtraRepos.Select(r => (r.Rel, r.Path, Base: r.BaseSha, Head: r.HeadSha))).Where(r => r.Head is not null && r.Head != r.Base).ToList();
        var merging = new List<(string Rel, string Path, List<string> Files)>();
        string? fail = null;
        try
        {
            SetStatus(job, JobStatus.Fixing, "merge", "Gộp nhánh gốc mới nhất vào nhánh AI và giải conflict");
            foreach (var r in repos)
                if (await repo.Dirty(r.Rel, r.Path, ct) is { Count: > 0 } d)
                    throw new PipelineException($"{r.Rel} đang có {d.Count} file sửa chưa commit — commit/stash trước rồi bấm Tạo PR lại.");
            await ResumeGit(job, ct); // mọi repo sang nhánh AI (giữ file cấu hình local)
            var list = new System.Text.StringBuilder();
            foreach (var r in repos)
            {
                if (await UpstreamOf(job, r.Rel, r.Path) is not { } up) continue;
                await Proc.Run("git", r.Path, ["fetch", "-q", "origin"], none);
                var (code, output, err) = await Proc.Run("git", r.Path, ["merge", "--no-ff", "--no-commit", up], none);
                var files = (await GitIn(r.Path, none, "diff", "--name-only", "--diff-filter=U")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (code != 0 && files.Count == 0)
                    throw new PipelineException($"{r.Rel}: git merge {up} lỗi (không phải conflict nội dung): {Trunc((output + err).Trim(), 300)}");
                merging.Add((r.Rel, r.Path, files));
                store.Log(job, "git", files.Count > 0 ? "error" : "info", $"{r.Rel}: đã gộp {up} vào {job.Branch}" + (files.Count > 0 ? $" — {files.Count} file conflict: {string.Join(", ", files.Take(8))}" : " — không conflict."));
                foreach (var f in files) list.Append($"  - {r.Rel}\\{f.Replace('/', '\\')}\n");
            }
            if (merging.Any(m => m.Files.Count > 0))
            {
                var res = await Claude(job, "merge", Prompts.Merge(job, dir, O, list.ToString()), ct);
                if ((Marker(res, "MERGE") ?? "UNKNOWN") != "DONE")
                    throw new PipelineException("AI không giải xong conflict (xem merge.md) — cần bạn xử lý tay.");
            }
            foreach (var m in merging)
            {
                var left = (await GitIn(m.Path, none, "diff", "--name-only", "--diff-filter=U")).Trim();
                var markers = m.Files.Where(f => File.Exists(Path.Combine(m.Path, f)) &&
                    System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(m.Path, f)), @"(?m)^(<{7}|>{7})( |$)")).ToList();
                if (left.Length > 0 || markers.Count > 0)
                    throw new PipelineException($"{m.Rel}: vẫn còn conflict ở {(left.Length > 0 ? left.Replace("\n", ", ") : string.Join(", ", markers))} — cần bạn xử lý tay.");
                var locals = O.LocalFilesOf(m.Rel);
                await GitIn(m.Path, none, ["add", "-u", "--", ".", .. locals.Select(f => $":(exclude){f}")]);
                foreach (var chunk in (await GitIn(m.Path, none, "diff", "--cached", "--name-only")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Where(f => O.IsLocalFile(m.Rel, f)).Chunk(50))
                    await GitIn(m.Path, none, ["reset", "-q", "HEAD", "--", .. chunk]);
                await GitIn(m.Path, none, "commit", "--no-edit");
                var head = (await GitIn(m.Path, none, "rev-parse", "HEAD")).Trim();
                if (m.Rel == O.RepoRelativePath) job.HeadSha = head;
                else if (job.ExtraRepos.FirstOrDefault(x => x.Rel == m.Rel) is { } er) er.HeadSha = head;
                store.Log(job, "git", "info", $"{m.Rel}: đã commit merge nhánh gốc vào {job.Branch} ({head[..10]}).");
            }
            merging.Clear();
            job.MergePending = false;
            job.ResumeFrom = null;
            job.Status = JobStatus.Approved;
            store.Save(job);
            if (await PushBranch(job) is { } perr) throw perr;
            if (AutoPr && await CreatePrs(job) is { } prErr) store.Log(job, "git", "error", "Đã push sau khi gộp, nhưng " + prErr.Message);
            else store.Log(job, "decision", "stage", "Đã gộp nhánh gốc, giải conflict, push và tạo PR.");
        }
        catch (Exception ex) when (ex is PipelineException or OperationCanceledException)
        {
            fail = ex is OperationCanceledException ? "Đã huỷ." : ex.Message;
        }
        finally
        {
            foreach (var m in merging)
            {
                try { await GitIn(m.Path, none, "merge", "--abort"); } catch (PipelineException) { }
            }
            if (fail is not null)
            {
                job.MergePending = false; // lần bấm Tạo PR / push sau sẽ kiểm conflict lại từ đầu
                job.ResumeFrom = null;
                job.Status = JobStatus.Approved;
                job.Error = "Chưa push/tạo PR: " + fail;
                store.Log(job, "merge", "error", $"Gộp nhánh gốc không xong — đã huỷ merge, CHƯA push. {fail}");
            }
            store.Save(job);
            await ReturnToBase(job);
        }
    }

    /// <summary>"off" | "data" | "full", hoặc null nếu giá trị không hợp lệ/để trống (= theo cấu hình).</summary>
    public static string? NormTestMode(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "off" or "none" or "tat" => "off",
        "data" or "du-lieu" => "data",
        "full" or "ui" => "full",
        _ => null,
    };

    /// <summary>Chế độ test áp cho job: theo job nếu có, không thì theo Cấu hình › Test.</summary>
    public string TestModeFor(Job job)
    {
        if (NormTestMode(job.TestMode) is { } m) return m;
        var te = testEnv.Options;
        return !O.RunTests || !te.Enabled ? "off" : !te.UiTests ? "data" : "full";
    }

    /// <summary>
    /// Bước test chạm trần chi phí riêng: nhắc AI (cùng phiên) dừng chạy test, ghi ngay kết quả đang có.
    /// Trần cho lượt nhắc = trần bước + $2 (phiên --resume có thể tính chi phí cộng dồn).
    /// </summary>
    private async Task<string> FinishTestOverBudget(Job job, string dir, double budget, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        store.Log(job, "test", "error", $"Bước test chạm trần ${budget:0.##} (Cấu hình › Test) — nhắc AI dừng test và ghi kết quả đang có.");
        if (!_lastRun.TryGetValue(job.Id, out var last)) return "";
        try
        {
            return await Claude(job, "test-finish",
                $"ĐÃ HẾT ngân sách bước test (${budget:0.##}). DỪNG ngay: không chạy Playwright, không truy vấn thêm. Chỉ ghi kết quả ĐÃ CÓ:\n" +
                $"- {Path.Combine(dir, "test.md")}: bảng tiêu chí với kết quả đã kiểm; tiêu chí chưa kiểm ghi \"chưa kiểm: hết ngân sách test\".\n" +
                $"- {Path.Combine(dir, TestCases.JsonFile)}: test case chưa chạy để status NOT_RUN, note \"hết ngân sách test\".\n" +
                "- pr.md (dòng Test local) nếu có.\n" +
                "Dòng cuối: TEST: FAIL nếu đã thấy lỗi, không thì TEST: PARTIAL.", ct, extraEnv: env, sideResume: last.Session, stageBudget: budget + 2);
        }
        catch (PipelineException ex)
        {
            store.Log(job, "test", "error", $"AI không ghi được kết quả sau khi hết ngân sách: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Dựng testcases.md + testcases.docx (gửi QC) từ testcases.json AI ghi ở bước test. Thiếu/sai JSON thì nhắc AI sửa một lần trong cùng phiên.
    /// </summary>
    private async Task ExportTestCases(Job job, string dir, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            string? problem;
            try
            {
                var sum = ExportTestCasesNow(job, dir);
                if (sum is not null)
                {
                    store.Log(job, "test", sum.Fail > 0 ? "error" : "info", $"Đã xuất test case cho QC ({TestCases.DocxName(job, dir)}): {sum}");
                    store.Save(job);
                    return;
                }
                problem = $"chưa có {TestCases.JsonFile} hoặc file không có test case nào";
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problem = $"{TestCases.JsonFile} lỗi: {Trunc(ex.Message, 200)}";
            }
            if (attempt > 0 || !_lastRun.TryGetValue(job.Id, out var last))
            {
                store.Log(job, "test", "error", $"Không xuất được test case: {problem}.");
                return;
            }
            store.Log(job, "test", "info", $"Test case: {problem} — nhắc AI ghi lại trong cùng phiên.");
            try
            {
                await Claude(job, "test-cases", $"Bước test đã xong nhưng {problem}. Ghi lại {Path.Combine(dir, TestCases.JsonFile)} đúng định dạng đã hướng dẫn " +
                    "(mỗi test case: id, title, criterion, screen, type, account, precondition, data, steps[], expected, actual, status PASS/FAIL/BLOCKED/NOT_RUN, evidence[], note), " +
                    "lấy kết quả từ test.md và ảnh trong test-artifacts. Không chạy lại test. Trả lời ngắn khi xong.", ct, extraEnv: env, sideResume: last.Session);
            }
            catch (PipelineException ex)
            {
                store.Log(job, "test", "error", $"Nhắc AI ghi test case không thành: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>Dựng lại file test case từ testcases.json hiện có (cũng dùng khi người dùng bấm xuất lại). Null nếu chưa có test case.</summary>
    public TestCases.Summary? ExportTestCasesNow(Job job, string dir)
    {
        var te = testEnv.Options;
        var accounts = new[] { te.User }.Concat(testEnv.UsableAccounts().Select(a => a.User + (a.Role.Length > 0 ? $" ({a.Role})" : "")));
        var db = job.SqlDeployDb ?? job.TestDataDb ?? "";
        var sum = TestCases.Export(job, dir, db, accounts);
        job.TestCaseSummary = sum?.ToString();
        return sum;
    }

    /// <summary>
    /// Chép file tóm tắt — file đích có thể đang bị trình xem/Claude giữ: thử lại vài lần, vẫn lỗi thì chỉ cảnh báo
    /// (bản gốc {stage}.result.md vẫn còn), không để một file phụ làm hỏng cả pipeline.
    /// </summary>
    private async Task CopyResult(Job job, string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < 5)
            {
                logger.LogDebug(ex, "Chép {File} lần {Attempt} lỗi, thử lại", to, attempt);
                await Task.Delay(attempt * 400);
            }
            catch (IOException ex)
            {
                store.Log(job, "code", "stderr", $"Không ghi được {Path.GetFileName(to)} ({ex.Message}). Nội dung vẫn có ở {Path.GetFileName(from)}.");
                return;
            }
        }
    }

    private static ClaudeAuth WithEnv(ClaudeAuth auth, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is null || extra.Count == 0) return auth;
        var env = new Dictionary<string, string>(auth.Env);
        foreach (var kv in extra) env[kv.Key] = kv.Value;
        return auth with { Env = env };
    }

    /// <summary>Lấy giá trị dòng đánh dấu cuối cùng, ví dụ "VERDICT: PASS" → PASS, "STATUS: NEED_INPUT" → NEED_INPUT.</summary>
    public static string? Marker(string text, string key)
    {
        var ms = Regex.Matches(text, $@"(?im)^\W*{key}\s*:\s*\**\s*([A-Z_]+)");
        return ms.Count > 0 ? ms[^1].Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string StripMarker(string text, string key) =>
        Regex.Replace(text, $@"(?im)^\W*{key}\s*:\s*\**\s*[A-Z_]+\W*$", "").Trim();

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
