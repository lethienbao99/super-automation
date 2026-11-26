using System.Text.Json.Serialization;

namespace SuperAutomation.Core;

public sealed class AmisOptions
{
    public string BaseUrl { get; set; } = "https://amisapp.misa.vn/task/APIS/g1/TaskAPI/api/";
    public string SessionId { get; set; } = "";
}

public sealed class PipelineOptions
{
    public string WorkspaceRoot { get; set; } = @"D:\Source\hrm-wiki";
    public string RepoRelativePath { get; set; } = @"src\HRM9";
    public string DataDir { get; set; } = "data";
    public string ClaudePath { get; set; } = "claude";
    public string? Model { get; set; }
    public string PermissionMode { get; set; } = "acceptEdits";
    // Để trống mặc định: binder cấu hình nối thêm vào list có sẵn, nên giá trị chỉ lấy từ appsettings
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    public int StageTimeoutMinutes { get; set; } = 60;
    public int MaxFixRounds { get; set; } = 1;
    /// <summary>Mức review: auto = theo rủi ro của diff (đầy đủ khi đụng SQL/quyền/luồng duyệt hoặc diff lớn, còn lại nhẹ), full = luôn có vnr-reviewer, light = luôn review nhẹ.</summary>
    public string ReviewMode { get; set; } = "auto";
    public int ReviewLightMaxLines { get; set; } = 150;
    public int ReviewLightMaxFiles { get; set; } = 6;
    /// <summary>Model cho agent vnr-reviewer khi tool gọi review đầy đủ: sonnet (rẻ ~một nửa), opus, hoặc "" = theo file agent.</summary>
    public string ReviewerModel { get; set; } = "sonnet";
    public bool RunTests { get; set; } = true;
    public string BranchPrefix { get; set; } = "ai/amis-";

    /// <summary>Công cụ cho khung chat — chỉ đọc (xem code, git log/diff), không sửa file.</summary>
    public List<string> ChatAllowedTools { get; set; } = [];
    public List<string> ChatDisallowedTools { get; set; } = [];
    public int ChatTimeoutMinutes { get; set; } = 15;
    /// <summary>Xong pipeline KHÔNG tự commit: giữ code trên nhánh AI, người dùng xem lại rồi bấm Tạo commit (và push).</summary>
    public bool ConfirmCommit { get; set; } = true;
    /// <summary>Cho chat tạo file (docx, xlsx, biên bản test…) — chỉ trong thư mục outputs của task, cấp thêm python/node để sinh file.</summary>
    public bool ChatCanWriteFiles { get; set; }
    /// <summary>Trần chi phí (USD) cho cả một job — phân tích + code + review + sửa + test. 0 = không giới hạn.</summary>
    public double MaxJobCostUsd { get; set; }
    /// <summary>Trần chi phí (USD) cho mỗi tin nhắn chat. 0 = không giới hạn.</summary>
    public double MaxChatCostUsd { get; set; }
    /// <summary>Chỉ áp trần khi trả tiền thật (API key/Agent SDK); tắt thì áp cả khi dùng tài khoản đăng nhập (chi phí ước tính).</summary>
    public bool CostLimitPaidOnly { get; set; } = true;

    /// <summary>Phân tích xong mà yêu cầu đã rõ thì tự chuyển sang code, không chờ người dùng bấm.</summary>
    public bool AutoCodeWhenClear { get; set; }

    /// <summary>
    /// Tự duyệt thay đổi nhỏ: review PASS, test PASS (nếu chạy test), AI không tự quyết điểm nào, không sửa SQL,
    /// tối đa AutoApproveMaxFiles file / AutoApproveMaxLines dòng. Chỉ commit vào nhánh AI — KHÔNG push.
    /// </summary>
    public bool AutoApproveSmall { get; set; }
    /// <summary>Trước khi tạo nhánh AI: fetch rồi fast-forward nhánh đang đứng theo origin (chỉ khi kéo thẳng được).</summary>
    public bool PullBeforeTask { get; set; } = true;
    public int AutoApproveMaxFiles { get; set; } = 3;
    public int AutoApproveMaxLines { get; set; } = 80;
    public int AnalyzeTimeoutMinutes { get; set; } = 20;

    public string RepoPath => Path.Combine(WorkspaceRoot, RepoRelativePath);

    /// <summary>Repo phụ AI được sửa cùng repo chính (ví dụ FE src\Vnr.Dev.HrmPortal), đường dẫn tương đối workspace.</summary>
    public List<string> ExtraRepos { get; set; } = [];

    public IEnumerable<(string Rel, string Path)> ExtraRepoPaths => ExtraRepos.Select(r => (r, Path.Combine(WorkspaceRoot, r)));

    /// <summary>
    /// File cấu hình môi trường luôn sửa local (connection string, URL, auth…) theo repo: không tính là "chưa commit",
    /// không bao giờ commit vào nhánh AI, AI không được sửa. Khoá = đường dẫn repo tương đối workspace, giá trị = đường dẫn trong repo (dấu /).
    /// </summary>
    public Dictionary<string, List<string>> LocalFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> LocalFilesOf(string repoRel) =>
        LocalFiles.FirstOrDefault(kv => string.Equals(kv.Key.Trim('\\', '/'), repoRel.Trim('\\', '/'), StringComparison.OrdinalIgnoreCase)).Value ?? [];

    /// <summary>Khớp đúng đường dẫn, hoặc mẫu có "*" (vd "Main/.../Downloads/*" = mọi file trong thư mục đó, kể cả thư mục con).</summary>
    public bool IsLocalFile(string repoRel, string path)
    {
        var p = path.Replace('\\', '/').Trim('/');
        return LocalFilesOf(repoRel).Any(f =>
        {
            var pat = f.Replace('\\', '/').Trim('/');
            if (!pat.Contains('*')) return string.Equals(pat, p, StringComparison.OrdinalIgnoreCase);
            var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace(@"\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(p, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        });
    }

    public PipelineOptions Clone()
    {
        var c = (PipelineOptions)MemberwiseClone();
        c.ExtraRepos = [.. ExtraRepos];
        c.LocalFiles = new(LocalFiles.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), StringComparer.OrdinalIgnoreCase);
        return c;
    }
}

public enum JobStatus
{
    Queued,
    /// <summary>AI đọc task (cả task cha, tệp đính kèm) và đánh giá yêu cầu đã đủ để code chưa — chỉ đọc, không đụng git.</summary>
    Analyzing,
    /// <summary>Yêu cầu chưa đủ: chờ người dùng trả lời/bổ sung tài liệu rồi phân tích lại (hoặc ép giao code).</summary>
    NeedsInfo,
    /// <summary>Yêu cầu đã rõ: chờ người dùng bấm "Bắt đầu code" (hoặc tự chạy nếu AutoCodeWhenClear).</summary>
    Clarified,
    Fetching, Coding, Reviewing, Fixing, Testing,
    /// <summary>AI chưa hiểu yêu cầu, đã đặt câu hỏi — chờ người dùng trả lời trong chat rồi tiếp tục.</summary>
    WaitingForUser,
    AwaitingApproval, Approved, Rejected, Failed, Cancelled,
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public long TaskId { get; set; }
    public string TaskName { get; set; } = "";
    /// <summary>Task cha trên AMIS (nếu có) — tiêu đề task cha dùng làm tên commit.</summary>
    public long? ParentTaskId { get; set; }
    public string? ParentTaskName { get; set; }
    /// <summary>Tiêu đề PR gợi ý theo chuẩn "[ID TASK] - Mô tả" (cũng là dòng đầu commit).</summary>
    public string? PrTitle { get; set; }
    /// <summary>Code AI đang nằm CHƯA COMMIT trên nhánh AI (repo bị giữ) — chờ người dùng bấm Tạo commit.</summary>
    public bool Held { get; set; }
    public string? ProjectName { get; set; }
    public string? EndDate { get; set; }
    public string? Note { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? BaseBranch { get; set; }
    public string? BaseSha { get; set; }
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }
    /// <summary>Nhánh trả working copy về khi job dừng (nhánh của người dùng lúc job bắt đầu/tiếp tục).</summary>
    public string? ReturnBranch { get; set; }
    /// <summary>Phiên Claude của bước code — dùng --resume khi người dùng trả lời câu hỏi.</summary>
    public string? CodeSessionId { get; set; }
    /// <summary>Tổng cộng dồn Claude báo cho CodeSessionId — lần --resume sau chỉ cộng phần tăng thêm vào CostUsd.</summary>
    public double CodeSessionCostUsd { get; set; }
    /// <summary>Câu trả lời của người dùng đang chờ đưa vào phiên code.</summary>
    public string? PendingAnswer { get; set; }
    public int QuestionRounds { get; set; }
    /// <summary>Kết luận bước phân tích: CLEAR | UNCLEAR. Null khi chưa phân tích.</summary>
    public string? Requirement { get; set; }
    public int AnalyzeRounds { get; set; }
    /// <summary>Người dùng đã cho phép code (yêu cầu rõ, hoặc ép giao dù chưa rõ). False thì lần chạy kế tiếp là phân tích.</summary>
    public bool CodeApproved { get; set; }
    /// <summary>Nhánh của repo lúc phân tích — AI đã đọc source trên nhánh này.</summary>
    public string? AnalyzedBranch { get; set; }
    public string? CodeStatus { get; set; }
    public string? ReviewVerdict { get; set; }
    public string? TestVerdict { get; set; }
    /// <summary>Tóm tắt testcases.json sau bước test, vd "12 TC: 10 đạt, 1 lỗi, 1 chưa kiểm được"; null nếu chưa có.</summary>
    public string? TestCaseSummary { get; set; }
    /// <summary>Chế độ test riêng của job: "off" (không test), "data" (chỉ kiểm DB/API), "full" (Playwright + ảnh). Null = theo Cấu hình › Test.</summary>
    public string? TestMode { get; set; }
    /// <summary>Số vòng AI tự sửa theo kết quả test FAIL (tối đa 1).</summary>
    public int TestFixRounds { get; set; }
    /// <summary>Số test case FAIL ngay trước vòng tự sửa theo test gần nhất — để biết vòng đó có tiến triển không.</summary>
    public int? TestFixPrevFails { get; set; }
    /// <summary>Dấu vân tay code (diff + file mới) lúc review gần nhất và kết luận khi đó — "test lại" mà code không đổi thì giữ kết luận, bỏ review.</summary>
    public string? ReviewedFingerprint { get; set; }
    public string? ReviewedVerdict { get; set; }
    /// <summary>Đã ghi nhật ký "giữ dữ liệu test, dọn tay" (chỉ báo một lần).</summary>
    public bool CleanupNoted { get; set; }
    /// <summary>Nhánh AI conflict với nhánh gốc mới nhất lúc push — tool tự gộp + AI giải conflict rồi mới push / tạo PR.</summary>
    public bool MergePending { get; set; }
    public int FixRounds { get; set; }
    /// <summary>Thống kê thay đổi lúc xong pipeline (so với commit gốc, mọi repo) — để báo nhanh qua chat.</summary>
    /// <summary>
    /// Task sửa lỗi QC: task con "SE xử lý" / "SE xử lý bug" do QC tạo sau khi test task cha (code gốc đã merge vào nhánh chính).
    /// Lỗi nằm ở bình luận và file test case của task cha — pipeline chạy theo luồng sửa lỗi thay vì làm tính năng.
    /// </summary>
    public bool QcFix => QcFixName.IsMatch((TaskName ?? "").Normalize(System.Text.NormalizationForm.FormC));
    private static readonly System.Text.RegularExpressions.Regex QcFixName =
        new(@"\bSE\s*[-–:]?\s*x[ửưu]\s*l[ýyíi]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>Bình luận soạn sẵn trả lời QC (qc-reply.md): từng lỗi — nguyên nhân, đã sửa gì, hoặc vì sao không phải lỗi.</summary>
    public string? QcReply { get; set; }

    /// <summary>Tin nhắn gom mọi câu hỏi gửi BA một lần (mục "Tin nhắn gửi BA" của bản phân tích) — null khi yêu cầu đã rõ.</summary>
    public string? BaMessage { get; set; }
    public int DiffAdded { get; set; }
    public int DiffDeleted { get; set; }
    public List<string> DiffList { get; set; } = [];
    /// <summary>Tool tự duyệt vì thay đổi nhỏ và review/test đều PASS (đã commit nhánh AI, chưa push).</summary>
    public bool AutoApproved { get; set; }
    public double CostUsd { get; set; }
    public string? Error { get; set; }
    public string? LastLog { get; set; }
    public List<string> InputFiles { get; set; } = [];

    /// <summary>File chưa track có sẵn trước khi AI chạy — không được commit nhầm vào nhánh AI.</summary>
    /// <summary>Lưu lại để sau khi tool khởi động lại vẫn biết file chưa track nào là của người dùng.</summary>
    public HashSet<string>? UntrackedBefore { get; set; }

    /// <summary>Project tool đã build trên nhánh AI cho bước test — build lại sau khi trả nhánh để web chạy code người dùng.</summary>
    public List<string> BuiltProjects { get; set; } = [];

    /// <summary>Repo phụ (FE…) AI sửa cùng job này, chốt lúc tạo nhánh — đổi cấu hình sau đó không ảnh hưởng job đang dở.</summary>
    public List<RepoState> ExtraRepos { get; set; } = [];

    /// <summary>"review": lần chạy kế tiếp bỏ qua bước code, làm tiếp review → test trên code đã commit ở nhánh AI.</summary>
    public string? ResumeFrom { get; set; }

    /// <summary>Yêu cầu sửa thêm người dùng gửi khi job đã xong (ResumeFrom = "revise"); AI sửa trên nhánh AI rồi chạy lại review → test.</summary>
    public string? Revision { get; set; }
    public int Revisions { get; set; }

    /// <summary>Tool đã chạy test-data.sql và chưa dọn — dọn khi test xong (hoặc khi duyệt/từ chối nếu chọn giữ dữ liệu).</summary>
    public bool TestDataPending { get; set; }
    /// <summary>DB đã ghi dữ liệu test (server/tên DB) — hiển thị để tránh nhầm dự án.</summary>
    public string? TestDataDb { get; set; }
    /// <summary>Store/function/view của nhánh AI đang deploy trên DB test (chưa khôi phục) — bản cũ lưu ở data/jobs/{id}/sql-backup.</summary>
    public List<string> SqlDeployed { get; set; } = [];
    /// <summary>Số lần AI đã ghi DB test (commit) chưa hoàn tác — có thì hiện nút "Hoàn tác thay đổi DB".</summary>
    public int AiDbWrites { get; set; }
    public string? SqlDeployDb { get; set; }

    /// <summary>
    /// Giao theo lô (chạy hàng loạt): yêu cầu rõ thì tự code, KHÔNG giữ repo — xong tự commit vào nhánh AI (không push)
    /// và trả nhánh để task sau chạy tiếp. Người dùng duyệt/push sau, từng task.
    /// </summary>
    public bool Batch { get; set; }
    /// <summary>Chạy qua đêm: không dừng hỏi — điểm chưa rõ thì AI chọn phương án đề xuất và ghi vào assumptions.md để người dùng xác nhận sau.</summary>
    public bool Unattended { get; set; }
    /// <summary>Số lần tool tự chạy lại sau lỗi kỹ thuật (chạy hàng loạt).</summary>
    public int AutoRetries { get; set; }
    /// <summary>Đang chờ tự chạy lại (job vẫn ở trạng thái lỗi trong 2 phút chờ).</summary>
    public bool RetryPending { get; set; }
    /// <summary>Repo đang được chuyển sang nhánh AI của job để người dùng kiểm tay trên web (hàng đợi code tạm dừng).</summary>
    public bool Inspecting { get; set; }
    /// <summary>Nhánh repo đang đứng trước khi chuyển sang kiểm — trả về đây khi xong (ReturnBranch giữ nguyên làm đích PR).</summary>
    public string? InspectReturn { get; set; }
    public List<string?> InspectExtraReturn { get; set; } = [];
    /// <summary>Số lần tool tự trả lời "chọn phương án đề xuất" khi AI hỏi lúc chạy qua đêm.</summary>
    public int AutoAnswers { get; set; }
    public string? BatchId { get; set; }
    /// <summary>Lô: task đụng cùng file với task trước thì tạo nhánh nối tiếp từ nhánh task đó (false = vẫn tách nhánh, chỉ cảnh báo).</summary>
    public bool BatchChain { get; set; } = true;
    /// <summary>File dự kiến sửa (lấy từ mục "Phạm vi" của bản phân tích) — để xếp nối tiếp task đụng cùng file.</summary>
    public List<string> PlannedFiles { get; set; } = [];
    /// <summary>File thực sự đã sửa (kèm repo, dấu /), cập nhật mỗi lần job trả nhánh.</summary>
    public List<string> ChangedFiles { get; set; } = [];
    /// <summary>Nhánh này tạo nối tiếp từ nhánh AI của task khác (PR task đó phải merge trước).</summary>
    public long? ChainedFromTask { get; set; }
    public string? ChainedFromBranch { get; set; }
    /// <summary>Task khác (đang mở) có sửa cùng file → dễ conflict khi merge. Khoá = TaskId.</summary>
    public Dictionary<string, List<string>> Overlaps { get; set; } = [];
    /// <summary>PR đã tạo trên TFS cho nhánh AI: repo (rel) → URL.</summary>
    public Dictionary<string, string> PrUrls { get; set; } = [];
    /// <summary>Lần cuối người dùng gửi bình luận kết quả lên AMIS từ tool (task nào, lúc nào).</summary>
    public long? AmisCommentTask { get; set; }
    public DateTime? AmisCommentAt { get; set; }
    /// <summary>Lúc tool chuyển task con sang Hoàn thành trên AMIS.</summary>
    public DateTime? AmisDoneAt { get; set; }

    /// <summary>Hash nội dung file cấu hình local lúc AI bắt đầu ("repo|path" → sha) — để phát hiện AI lỡ sửa chúng.</summary>
    public Dictionary<string, string> LocalHashes { get; set; } = [];

    /// <summary>Đang chờ người dùng thao tác (không chạy, không xếp hàng) — giữ nguyên khi tool khởi động lại.</summary>
    public bool IsParked => Status is JobStatus.NeedsInfo or JobStatus.Clarified or JobStatus.WaitingForUser or JobStatus.AwaitingApproval;
    public bool IsFinal => Status is JobStatus.Approved or JobStatus.Rejected or JobStatus.Failed or JobStatus.Cancelled;
    public bool IsRunning => Status is JobStatus.Analyzing or JobStatus.Fetching or JobStatus.Coding or JobStatus.Reviewing or JobStatus.Fixing or JobStatus.Testing;
}

/// <summary>Trạng thái git của một repo phụ trong job (repo chính dùng các trường BaseBranch/BaseSha… của Job).</summary>
public sealed class RepoState
{
    public string Rel { get; set; } = "";
    public string Path { get; set; } = "";
    public string? BaseBranch { get; set; }
    public string? BaseSha { get; set; }
    public string? HeadSha { get; set; }
    public string? ReturnBranch { get; set; }
    /// <summary>Lưu lại để sau khi tool khởi động lại vẫn biết file chưa track nào là của người dùng.</summary>
    public HashSet<string>? UntrackedBefore { get; set; }
}

/// <summary>Đường dẫn trong dòng git status --porcelain ("XY path" hoặc "XY old -> new").</summary>
public static class Porcelain
{
    public static string PathOf(string line)
    {
        var p = line.Length > 3 ? line[3..] : "";
        var i = p.IndexOf(" -> ", StringComparison.Ordinal);
        if (i >= 0) p = p[(i + 4)..];
        return p.Trim().Trim('"');
    }
}

public sealed record LogEntry(DateTime At, string Stage, string Kind, string Text);

/// <summary>Lỗi nghiệp vụ của pipeline — thông điệp hiển thị thẳng cho người dùng.</summary>
public class PipelineException(string message) : Exception(message);

/// <summary>Phiên chạm trần chi phí riêng của bước (vd bước test), chưa chạm trần cả job — bước đó tự kết thúc gọn.</summary>
public sealed class StageBudgetException(string message) : PipelineException(message);

/// <summary>Người dùng xác nhận commit: message (rỗng = message chuẩn), có push không, file mới (chưa track) được chọn theo từng repo.</summary>
public sealed record CommitPlan(string? Message, bool Push, Dictionary<string, List<string>>? NewFiles, Dictionary<string, List<string>>? Skip = null);
