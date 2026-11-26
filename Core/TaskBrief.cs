using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static SuperAutomation.Core.AmisClient;

namespace SuperAutomation.Core;

public static partial class HtmlText
{
    /// <summary>Đổi HTML mô tả/bình luận AMIS sang văn bản cho AI đọc. imgHandler nhận src của ảnh, trả chuỗi hiển thị.</summary>
    public static string ToText(string? html, Func<string, string>? imgHandler = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = ImgTag().Replace(html, m => $"[Ảnh: {(imgHandler is null ? "inline" : imgHandler(m.Groups[1].Value))}]");
        s = LinkTag().Replace(s, m => $"{m.Groups[2].Value} ({m.Groups[1].Value})");
        s = BlockEnd().Replace(s, "\n");
        s = LiStart().Replace(s, "- ");
        s = CellEnd().Replace(s, " | ");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = TrailingSpace().Replace(s, "\n");
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<img[^>]*?src\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)] private static partial Regex ImgTag();
    [GeneratedRegex(@"<a[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex LinkTag();
    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex BlockEnd();
    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex LiStart();
    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)] private static partial Regex CellEnd();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"[ \t]+\n")] private static partial Regex TrailingSpace();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex ManyNewlines();
}

/// <summary>Dựng task.md — tài liệu yêu cầu mà các bước AI đọc.</summary>
public static partial class TaskBrief
{
    public static string Write(TaskBundle b, string jobDir, string? note, IReadOnlyList<string> inputFiles)
    {
        var inputs = Path.Combine(jobDir, "inputs");
        Directory.CreateDirectory(inputs);
        var images = new ImageSaver(inputs);
        var d = b.Detail;
        var sb = new StringBuilder();

        sb.AppendLine($"# Task AMIS #{Str(d, "TaskID")}: {Str(d, "TaskName")}");
        sb.AppendLine();
        sb.AppendLine($"- Dự án: {Str(d, "ProjectName")} — cột: {Str(d, "KanbanName")}");
        sb.AppendLine($"- Hạn: {Str(d, "EndDate")}");
        sb.AppendLine($"- Người giao: {Str(d, "AssignerName") ?? Str(d, "OwnerName")} — người thực hiện: {Str(d, "AssigneeName")}");
        if (b.Parent is not null)
            sb.AppendLine($"- Đây là công việc con của task #{Str(b.Parent.Detail, "TaskID")} \"{Str(b.Parent.Detail, "TaskName")}\" — yêu cầu và tài liệu chính thường nằm ở task cha (mục cuối).");
        else if (!string.IsNullOrWhiteSpace(Str(d, "ParentName")))
            sb.AppendLine($"- Task cha: {Str(d, "ParentName")} (không đọc được chi tiết)");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(note))
        {
            sb.AppendLine("## Ghi chú của người giao việc cho AI");
            sb.AppendLine(note.Trim());
            sb.AppendLine();
        }

        AppendTask(sb, b, images, "##");

        if (b.Parent is { } p)
        {
            sb.AppendLine($"## Task cha #{Str(p.Detail, "TaskID")}: {Str(p.Detail, "TaskName")}");
            sb.AppendLine($"- Cột: {Str(p.Detail, "KanbanName")} — người giao: {Str(p.Detail, "AssignerName") ?? Str(p.Detail, "OwnerName")}");
            sb.AppendLine();
            AppendTask(sb, p, images, "###");
        }

        sb.AppendLine("## Tệp bổ sung trong inputs/");
        var all = inputFiles.Concat(images.Saved).ToList();
        if (all.Count == 0) sb.AppendLine("(không có)");
        foreach (var f in all)
        {
            var extracted = DocExtract.ExtractedName(f);
            sb.AppendLine(File.Exists(Path.Combine(inputs, extracted))
                ? $"- inputs/{f} — nội dung đã trích ra inputs/{extracted}"
                : $"- inputs/{f}");
        }
        sb.AppendLine();
        sb.AppendLine("Dữ liệu gốc từ AMIS: task.raw.json");

        var raw = new JsonObject
        {
            ["Detail"] = d.DeepClone(),
            ["Comments"] = b.Comments.DeepClone(),
            ["Attachments"] = b.Attachments.DeepClone(),
        };
        if (b.Parent is not null)
            raw["Parent"] = new JsonObject
            {
                ["Detail"] = b.Parent.Detail.DeepClone(),
                ["Comments"] = b.Parent.Comments.DeepClone(),
                ["Attachments"] = b.Parent.Attachments.DeepClone(),
            };
        File.WriteAllText(Path.Combine(jobDir, "task.raw.json"), raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var path = Path.Combine(jobDir, "task.md");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Mô tả, checklist, việc con, bình luận và tệp AMIS của một task. h là cấp tiêu đề (## hoặc ###).</summary>
    private static void AppendTask(StringBuilder sb, TaskBundle b, ImageSaver images, string h)
    {
        var d = b.Detail;
        sb.AppendLine($"{h} Mô tả");
        var desc = HtmlText.ToText(Str(d, "Description"), images.Save);
        sb.AppendLine(desc.Length > 0 ? desc : "(trống)");
        sb.AppendLine();

        AppendJsonSection(sb, $"{h} Checklist", d["CheckList"]);
        AppendJsonSection(sb, $"{h} Trường tuỳ chỉnh", d["CustomFieldValue"]);

        var childs = (d["ListChildTask"] as JsonArray ?? d["TaskChilds"] as JsonArray ?? []).Where(c => c is not null).ToList();
        if (childs.Count > 0)
        {
            sb.AppendLine($"{h} Công việc con");
            foreach (var c in childs) sb.AppendLine($"- #{Str(c, "TaskID")} {Str(c, "TaskName")} (trạng thái {Str(c, "Status")})");
            sb.AppendLine();
        }

        var comments = UserComments(b.Comments).ToList();
        sb.AppendLine($"{h} Bình luận ({comments.Count})");
        foreach (var c in comments)
        {
            sb.AppendLine($"{h}# {Str(c, "FullName")} — {Str(c, "LogTime")}");
            var text = HtmlText.ToText(Str(c, "CommentContent"), images.Save);
            if (text.Length > 0) sb.AppendLine(text);
            var files = CommentFileNames(c);
            if (files.Count > 0) sb.AppendLine($"Tệp đính kèm: {string.Join(", ", files)}");
            sb.AppendLine();
        }

        sb.AppendLine($"{h} Tệp đính kèm trên AMIS");
        if (b.Attachments.Count == 0) sb.AppendLine("(không có)");
        foreach (var a in b.Attachments)
        {
            var local = AmisClient.SafeFileName(Str(a, "FileName") ?? "");
            var extracted = DocExtract.ExtractedName(local);
            var where = !File.Exists(Path.Combine(images.Dir, local))
                ? "chưa tải được nội dung — cần người dùng upload bản sao vào inputs/"
                : File.Exists(Path.Combine(images.Dir, extracted))
                    ? $"đã tải về inputs/{local}, nội dung trích ở inputs/{extracted}"
                    : $"đã tải về inputs/{local}";
            sb.AppendLine($"- {Str(a, "FileName")} ({Str(a, "FileType")}, {Str(a, "FileSize")} byte) — {where}");
        }
        sb.AppendLine();
    }

    private static void AppendJsonSection(StringBuilder sb, string title, JsonNode? node)
    {
        var text = node switch
        {
            null => null,
            JsonValue v => v.ToString(),
            _ => node.ToJsonString(),
        };
        if (string.IsNullOrWhiteSpace(text) || text is "[]" or "{}" or "null") return;
        sb.AppendLine($"## {title}");
        sb.AppendLine("```json");
        sb.AppendLine(text.Length > 8000 ? text[..8000] + "…" : text);
        sb.AppendLine("```");
        sb.AppendLine();
    }

    /// <summary>Ảnh dán thẳng vào mô tả (data URI) được lưu thành file để AI mở xem được.</summary>
    private sealed partial class ImageSaver(string dir)
    {
        public string Dir => dir;
        public List<string> Saved { get; } = [];

        public string Save(string src)
        {
            var m = DataUri().Match(src);
            if (!m.Success) return src;
            var ext = m.Groups[1].Value.ToLowerInvariant() switch { "jpeg" => "jpg", var e => e };
            byte[] bytes;
            try { bytes = Convert.FromBase64String(m.Groups[2].Value); }
            catch (FormatException) { return "(ảnh base64 lỗi định dạng)"; }
            var name = $"desc-img-{Saved.Count + 1}.{ext}";
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            Saved.Add(name);
            return $"inputs/{name}";
        }

        [GeneratedRegex(@"^data:image/(png|jpe?g|gif|webp|bmp);base64,(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex DataUri();
    }
}
