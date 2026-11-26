using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Chuẩn PR/commit của công ty (PDC-01-AP-08, bản 18/08/2026) — bản gốc trong thư mục standards/, lấy từ PR-Review-Kit.
/// Commit và tiêu đề PR: "[ID TASK] - Mô tả ngắn gọn thay đổi" (N1, N2, N9, N10), không dùng tiền tố feat/fix/chore.
/// </summary>
public static class Standards
{
    /// <summary>Thư mục chứa pr-naming.md, security-performance.md, huong-dan-tao-pr.md — gán lúc khởi động.</summary>
    public static string Dir { get; set; } = "standards";

    public static string Naming => Path.Combine(Dir, "pr-naming.md");
    public static string SecurityPerformance => Path.Combine(Dir, "security-performance.md");
    public static string PrGuide => Path.Combine(Dir, "huong-dan-tao-pr.md");

    // Cùng biểu thức mã task với check-standards.sh của tool review: chữ cái (cho phép dấu chấm) + >= 2 chữ số
    private static readonly Regex TaskCodeRe = new(@"[A-Za-z][A-Za-z.]*[_-]?[0-9]{2,}");
    private static readonly Regex LeadGroups = new(@"^\s*(\[[^\]]*\]\s*)+");
    // Nhãn phân loại sau mã task trong tên task AMIS: "_ATT_MODIFY:", "ATT_BUG:" …
    private static readonly Regex TypeLabel = new(@"^[\s_\-:]*(?:[A-Z0-9]+(?:_[A-Z0-9]+)*\s*:)?[\s\-:]*");

    /// <summary>Mã task trong nhóm [...] đầu tiên của tên task, vd "[XYZ283]_ATT_MODIFY: …" → XYZ283. Null nếu không có.</summary>
    public static string? TaskCode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var m = Regex.Match(title.Trim(), @"^\[([^\]]*)\]");
        if (!m.Success) return null;
        var group = m.Groups[1].Value.Trim();
        var codes = TaskCodeRe.Matches(group);
        if (codes.Count > 0) return codes[^1].Value;
        // Mã dạng lạ ("TPC.GD2_689") biểu thức trên không bắt được: giữ nguyên nội dung ngoặc nếu có chữ số và không có khoảng trắng
        return group.Length > 0 && group.Any(char.IsDigit) && !group.Any(char.IsWhiteSpace) ? group : null;
    }

    /// <summary>Phần mô tả của tên task: bỏ các nhóm [...] đầu và nhãn loại kiểu "_ATT_MODIFY:".</summary>
    public static string Description(string title)
    {
        var rest = LeadGroups.Replace(title.Trim(), "");
        rest = TypeLabel.Replace(rest, "");
        return Regex.Replace(rest, @"\s+", " ").Trim();
    }

    /// <summary>
    /// "[ID TASK] - Mô tả" từ tên task cha (yêu cầu gốc) rồi tới tên task con.
    /// Trả về (tiêu đề, mã task, có đúng chuẩn không). Không tìm được mã thì dùng AMIS-&lt;id&gt; và báo không chuẩn.
    /// </summary>
    public static (string Title, string Code, bool Ok) Title(Job job)
    {
        foreach (var t in new[] { job.ParentTaskName, job.TaskName })
        {
            if (TaskCode(t) is not { } code) continue;
            var desc = Description(t!);
            if (desc.Length == 0) desc = Description(job.TaskName);
            if (desc.Length == 0) continue;
            return ($"[{code}] - {desc}", code, true);
        }
        var fallback = Description(string.IsNullOrWhiteSpace(job.ParentTaskName) ? job.TaskName : job.ParentTaskName);
        return ($"[AMIS-{job.TaskId}] - {(fallback.Length > 0 ? fallback : job.TaskName)}", $"AMIS-{job.TaskId}", false);
    }

    /// <summary>Dòng tiêu đề commit. Commit tạm (job chưa xong) vẫn giữ "[ID] - " ở đầu để đạt N9/N10.</summary>
    public static string CommitSubject(Job job, bool wip)
    {
        var (title, code, _) = Title(job);
        return wip ? $"[{code}] - (WIP) {title[(code.Length + 5)..]}" : title;
    }
}
