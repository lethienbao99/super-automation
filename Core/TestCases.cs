using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Test case của job: AI ghi testcases.json (sinh từ tiêu chí chấp nhận, rồi điền kết quả sau khi chạy test);
/// tool dựng testcases.md (xem trong giao diện) và testcases.docx (gửi QC, kèm ảnh bằng chứng trong test-artifacts\).
/// File docx viết tay theo OpenXML tối thiểu để không cần thêm thư viện.
/// </summary>
public static class TestCases
{
    public const string JsonFile = "testcases.json";
    public const string MdFile = "testcases.md";
    public const string DocxFile = "testcases.docx";

    public sealed class Case
    {
        public string Id { get; set; } = "";
        public string Criterion { get; set; } = "";
        public string Title { get; set; } = "";
        public string Screen { get; set; } = "";
        public string Type { get; set; } = "";
        public string Account { get; set; } = "";
        public string Precondition { get; set; } = "";
        public string Data { get; set; } = "";
        public List<string> Steps { get; set; } = [];
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
        public string Status { get; set; } = "NOT_RUN";
        public List<string> Evidence { get; set; } = [];
        public string Note { get; set; } = "";
    }

    public sealed record Summary(int Total, int Pass, int Fail, int Blocked, int NotRun)
    {
        public override string ToString() =>
            $"{Total} TC: {Pass} đạt" + (Fail > 0 ? $", {Fail} lỗi" : "") + (Blocked > 0 ? $", {Blocked} chưa kiểm được" : "") + (NotRun > 0 ? $", {NotRun} chưa chạy" : "");
    }

    public static string NormStatus(string s) => (s ?? "").Trim().ToUpperInvariant().Replace(' ', '_') switch
    {
        "PASS" or "PASSED" or "OK" or "ĐẠT" => "PASS",
        "FAIL" or "FAILED" or "LỖI" => "FAIL",
        "BLOCKED" or "BLOCK" or "SKIPPED" or "SKIP" or "PARTIAL" => "BLOCKED",
        _ => "NOT_RUN",
    };

    /// <summary>Đọc testcases.json dễ tính (key không phân biệt hoa thường, steps là mảng hoặc chuỗi nhiều dòng, có comment/dấu phẩy thừa).</summary>
    public static List<Case>? Load(string dir)
    {
        var path = Path.Combine(dir, JsonFile);
        if (!File.Exists(path)) return null;
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var arr = root as JsonArray ?? Get(root as JsonObject, "cases", "testcases", "items") as JsonArray;
        if (arr is null) return null;
        var list = new List<Case>();
        var n = 0;
        foreach (var o in arr.OfType<JsonObject>())
        {
            n++;
            var c = new Case
            {
                Id = Str(o, "id", "code", "ma") is { Length: > 0 } id ? id : $"TC{n:00}",
                Criterion = Str(o, "criterion", "criteria", "tieuchi"),
                Title = Str(o, "title", "name", "tieude"),
                Screen = Str(o, "screen", "menu", "manhinh"),
                Type = Str(o, "type", "loai", "category"),
                Account = Str(o, "account", "user", "taikhoan"),
                Precondition = Str(o, "precondition", "preconditions", "dieukien"),
                Data = Str(o, "data", "testdata", "dulieu"),
                Steps = List(o, "steps", "buoc"),
                Expected = Str(o, "expected", "expectedresult", "mongdoi"),
                Actual = Str(o, "actual", "actualresult", "thucte"),
                Status = NormStatus(Str(o, "status", "result", "trangthai")),
                Evidence = List(o, "evidence", "evidences", "screenshots", "bangchung"),
                Note = Str(o, "note", "notes", "ghichu"),
            };
            list.Add(c);
        }
        return list;
    }

    private static JsonNode? Get(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var (k, v) in o)
            if (keys.Any(x => string.Equals(x, k.Replace("_", ""), StringComparison.OrdinalIgnoreCase))) return v;
        return null;
    }

    private static string Str(JsonObject o, params string[] keys) => Get(o, keys) switch
    {
        null => "",
        JsonArray a => string.Join("\n", a.Select(x => x?.ToString() ?? "")),
        JsonValue v => v.ToString().Trim(),
        var x => x.ToJsonString(),
    };

    private static List<string> List(JsonObject o, params string[] keys) => Get(o, keys) switch
    {
        JsonArray a => a.Select(x => x?.ToString().Trim() ?? "").Where(s => s.Length > 0).ToList(),
        JsonValue v => v.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        _ => [],
    };

    public static Summary Summarize(List<Case> cs) => new(cs.Count, cs.Count(c => c.Status == "PASS"), cs.Count(c => c.Status == "FAIL"),
        cs.Count(c => c.Status == "BLOCKED"), cs.Count(c => c.Status == "NOT_RUN"));

    /// <summary>Tên hiển thị: task con kiểu "SE thực hiện" thì lấy tên task cha trong task.md (dòng "## Task cha #id: tên").</summary>
    public static string Title(Job j, string? dir = null)
    {
        var name = j.TaskName ?? "";
        if (Regex.IsMatch(name, @"[A-Z][A-Z0-9]+_\d+") || dir is null) return name;
        var task = Path.Combine(dir, "task.md");
        if (!File.Exists(task)) return name;
        var m = Regex.Match(File.ReadAllText(task), @"(?m)^##\s*Task cha #\d+:\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : name;
    }

    public static string TaskCode(Job j, string? dir = null) =>
        Regex.Match(Title(j, dir), @"[A-Z][A-Z0-9]+_\d+") is { Success: true } m ? m.Value : $"AMIS_{j.TaskId}";

    public static string DocxName(Job j, string? dir = null) => $"TestCase_{TaskCode(j, dir)}_{j.TaskId}.docx";

    /// <summary>Đọc JSON rồi ghi testcases.md + testcases.docx. Trả về tóm tắt, hoặc null nếu chưa có testcases.json.</summary>
    public static Summary? Export(Job job, string dir, string testDb, IEnumerable<string> accounts)
    {
        var cases = Load(dir);
        if (cases is null || cases.Count == 0) return null;
        var sum = Summarize(cases);
        File.WriteAllText(Path.Combine(dir, MdFile), Markdown(job, cases, sum, dir), new UTF8Encoding(false));
        var tmp = Path.Combine(dir, DocxFile + ".tmp");
        WriteDocx(tmp, job, cases, sum, dir, testDb, accounts.ToList());
        File.Move(tmp, Path.Combine(dir, DocxFile), overwrite: true);
        return sum;
    }

    private static string StatusVi(string s) => s switch { "PASS" => "Đạt", "FAIL" => "Lỗi", "BLOCKED" => "Chưa kiểm được", _ => "Chưa chạy" };

    // ---------------- Markdown ----------------

    private static string Markdown(Job j, List<Case> cs, Summary sum, string dir)
    {
        static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        var sb = new StringBuilder();
        sb.AppendLine($"# Test case — {Title(j, dir)}");
        sb.AppendLine();
        sb.AppendLine($"**Tổng:** {sum}");
        sb.AppendLine();
        sb.AppendLine("| Mã | Tiêu đề | Điều kiện / dữ liệu | Các bước | Mong đợi | Thực tế | Kết quả |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in cs)
        {
            var title = c.Title + (c.Criterion.Length > 0 ? $"<br>_Tiêu chí: {Cell(c.Criterion)}_" : "") + (c.Screen.Length > 0 ? $"<br>_Màn: {Cell(c.Screen)}_" : "");
            var pre = string.Join("<br>", new[] { c.Account.Length > 0 ? "TK: " + c.Account : "", c.Precondition, c.Data.Length > 0 ? "Dữ liệu: " + c.Data : "" }.Where(s => s.Length > 0).Select(Cell));
            var steps = string.Join("<br>", c.Steps.Select((s, i) => $"{i + 1}. {Cell(s)}"));
            var mark = c.Status switch { "PASS" => "☑ Đạt", "FAIL" => "☒ **Lỗi**", "BLOCKED" => "☐ Chưa kiểm được", _ => "☐ Chưa chạy" };
            var actual = Cell(c.Actual) + (c.Note.Length > 0 ? $"<br>_{Cell(c.Note)}_" : "") + (c.Evidence.Count > 0 ? "<br>📎 " + string.Join(", ", c.Evidence.Select(Cell)) : "");
            sb.AppendLine($"| {c.Id} | {Cell(title)} | {pre} | {steps} | {Cell(c.Expected)} | {actual} | {mark} |");
        }
        return sb.ToString();
    }

    // ---------------- DOCX ----------------

    private const string NsW = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string X(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var clean = new string(s.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());
        return System.Security.SecurityElement.Escape(clean) ?? "";
    }

    /// <summary>Một đoạn văn; xuống dòng trong text thành &lt;w:br/&gt;.</summary>
    private static string P(string? text, bool bold = false, int size = 0, string? color = null, string? style = null, string align = "", int after = -1)
    {
        var ppr = new StringBuilder();
        if (style != null) ppr.Append($"<w:pStyle w:val=\"{style}\"/>");
        if (after >= 0) ppr.Append($"<w:spacing w:after=\"{after}\"/>");
        if (align.Length > 0) ppr.Append($"<w:jc w:val=\"{align}\"/>");
        return $"<w:p>{(ppr.Length > 0 ? $"<w:pPr>{ppr}</w:pPr>" : "")}{Run(text, bold, size, color)}</w:p>";
    }

    private static string Run(string? text, bool bold = false, int size = 0, string? color = null, bool italic = false)
    {
        var rpr = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "") + (color != null ? $"<w:color w:val=\"{color}\"/>" : "") + (size > 0 ? $"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>" : "");
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        var body = string.Join("<w:br/>", lines.Select(l => $"<w:t xml:space=\"preserve\">{X(l)}</w:t>"));
        return $"<w:r>{(rpr.Length > 0 ? $"<w:rPr>{rpr}</w:rPr>" : "")}{body}</w:r>";
    }

    private static string Cell(string content, int width, string? fill = null) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>{(fill != null ? $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>" : "")}</w:tcPr>{(content.Length > 0 ? content : "<w:p/>")}</w:tc>";

    private static string Table(int[] widths, IEnumerable<string> rows) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblLayout w:type=\"fixed\"/>" +
        "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/>" +
        "<w:right w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"9AA4B2\"/></w:tblBorders>" +
        "<w:tblCellMar><w:top w:w=\"60\" w:type=\"dxa\"/><w:left w:w=\"90\" w:type=\"dxa\"/><w:bottom w:w=\"60\" w:type=\"dxa\"/><w:right w:w=\"90\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>" +
        "<w:tblGrid>" + string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>")) + "</w:tblGrid>" + string.Concat(rows) + "</w:tbl>";

    private static string Row(IEnumerable<string> cells, bool header = false) =>
        $"<w:tr><w:trPr>{(header ? "<w:tblHeader/>" : "")}<w:cantSplit/></w:trPr>{string.Concat(cells)}</w:tr>";

    private static string StatusFill(string s) => s switch { "PASS" => "C6EFCE", "FAIL" => "FFC7CE", "BLOCKED" => "FFEB9C", _ => "EDEDED" };
    private static string StatusColor(string s) => s switch { "PASS" => "006100", "FAIL" => "9C0006", "BLOCKED" => "7F6000", _ => "595959" };

    private sealed class ImageRef { public string Rel = ""; public string Part = ""; public string Src = ""; public long Cx; public long Cy; }

    private static void WriteDocx(string file, Job j, List<Case> cs, Summary sum, string dir, string testDb, List<string> accounts)
    {
        var artifacts = Path.Combine(dir, "test-artifacts");
        var images = new List<ImageRef>();
        var body = new StringBuilder();
        var title = Title(j, dir);

        body.Append(P("KẾT QUẢ KIỂM THỬ", bold: true, size: 36, color: "1F3864", align: "center", after: 60));
        body.Append(P(title, bold: true, size: 26, align: "center", after: 240));

        // Thông tin chung
        var info = new List<(string, string)>
        {
            ("Task AMIS", $"#{j.TaskId}" + (title != j.TaskName ? $" ({j.TaskName})" : "") + (j.ProjectName is { Length: > 0 } pn ? $" — dự án {pn}" : "")),
            ("Nhánh code", j.Branch ?? ""),
            ("Pull request", string.Join("\n", j.PrUrls.Select(kv => $"{kv.Key}: {kv.Value}"))),
            ("Môi trường test", testDb),
            ("Tài khoản test", string.Join(", ", accounts.Where(a => a.Length > 0))),
            ("Người thực hiện", "AI (SuperAutomation) — cần QC xác nhận lại"),
            ("Ngày test", DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
            ("Kết luận", $"Review: {j.ReviewVerdict ?? "—"}   ·   Test: {j.TestVerdict ?? "—"}"),
            ("Tổng hợp", sum.ToString()),
        };
        body.Append(Table([3200, 12000], info.Where(i => i.Item2.Length > 0).Select(i =>
            Row([Cell(P(i.Item1, bold: true, size: 20), 3200, "E8EEF7"), Cell(P(i.Item2, size: 20), 12000)]))));
        body.Append(P("", after: 120));

        // Thống kê theo trạng thái
        body.Append(P("1. Tổng hợp kết quả", bold: true, size: 28, color: "1F3864", after: 120));
        var stats = new[] { ("PASS", sum.Pass), ("FAIL", sum.Fail), ("BLOCKED", sum.Blocked), ("NOT_RUN", sum.NotRun) };
        body.Append(Table([3000, 3000, 3000, 3000, 3000],
        [
            Row([.. stats.Select(s => Cell(P(StatusVi(s.Item1), bold: true, size: 20, align: "center"), 3000, StatusFill(s.Item1)))], header: true),
            Row([.. stats.Select(s => Cell(P(s.Item2.ToString(), bold: true, size: 28, color: StatusColor(s.Item1), align: "center"), 3000))]),
        ]));
        body.Append(P("", after: 120));

        // Bảng test case
        body.Append(P("2. Danh sách test case", bold: true, size: 28, color: "1F3864", after: 120));
        int[] w = [1000, 2700, 2500, 3300, 2400, 2400, 1200];
        string[] heads = ["Mã", "Tiêu đề / Tiêu chí", "Điều kiện trước / Dữ liệu", "Các bước thực hiện", "Kết quả mong đợi", "Kết quả thực tế", "Trạng thái"];
        var rows = new List<string> { Row(heads.Select((h, i) => Cell(P(h, bold: true, size: 19, color: "FFFFFF"), w[i], "1F3864")), header: true) };
        foreach (var c in cs)
        {
            var head = P(c.Title, bold: true, size: 19)
                + (c.Type.Length > 0 ? P("Loại: " + c.Type, size: 17, color: "595959") : "")
                + (c.Screen.Length > 0 ? P("Màn hình: " + c.Screen, size: 17, color: "595959") : "")
                + (c.Criterion.Length > 0 ? P("Tiêu chí: " + c.Criterion, size: 17, color: "595959") : "");
            var pre = (c.Account.Length > 0 ? P("Tài khoản: " + c.Account, size: 18) : "")
                + (c.Precondition.Length > 0 ? P(c.Precondition, size: 18) : "")
                + (c.Data.Length > 0 ? P("Dữ liệu: " + c.Data, size: 18) : "");
            var steps = string.Concat(c.Steps.Select((s, i) => P($"{i + 1}. {Regex.Replace(s, @"^\s*\d+[.)]\s*", "")}", size: 18)));
            var actual = P(c.Actual, size: 18) + (c.Note.Length > 0 ? P("Ghi chú: " + c.Note, size: 17, color: "7F6000") : "")
                + (c.Evidence.Count > 0 ? P("Bằng chứng: " + string.Join(", ", c.Evidence.Select(Path.GetFileName)), size: 16, color: "2F5597") : "");
            rows.Add(Row(
            [
                Cell(P(c.Id, bold: true, size: 18), w[0]), Cell(head, w[1]), Cell(pre, w[2]), Cell(steps, w[3]),
                Cell(P(c.Expected, size: 18), w[4]), Cell(actual, w[5]),
                Cell(P(StatusVi(c.Status), bold: true, size: 18, color: StatusColor(c.Status), align: "center"), w[6], StatusFill(c.Status)),
            ]));
        }
        body.Append(Table(w, rows));

        // Chưa kiểm được / lỗi
        var open = cs.Where(c => c.Status is "FAIL" or "BLOCKED" or "NOT_RUN").ToList();
        if (open.Count > 0)
        {
            body.Append(P("", after: 120));
            body.Append(P("3. Lỗi và tiêu chí chưa kiểm được", bold: true, size: 28, color: "1F3864", after: 120));
            foreach (var c in open)
                body.Append($"<w:p><w:pPr><w:spacing w:after=\"80\"/></w:pPr>{Run($"{c.Id} — {StatusVi(c.Status)}: ", true, 20, StatusColor(c.Status))}{Run(c.Title + (c.Note.Length > 0 ? ". " + c.Note : c.Actual.Length > 0 ? ". " + c.Actual : ""), size: 20)}</w:p>");
        }

        // Ảnh bằng chứng
        // Nhóm theo TC (ảnh AI gắn vào TC) + "Ảnh khác": mọi ảnh còn lại trong test-artifacts\ để file chứa ĐỦ ảnh của lần test
        var groups = cs.Select(c => (head: $"{c.Id} — {c.Title}", files: c.Evidence.Select(e => Resolve(artifacts, e)).OfType<string>()
                .Where(f => ImageSize(f) != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(x => x.files.Count > 0).ToList();
        var used = groups.SelectMany(g => g.files).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = Directory.Exists(artifacts)
            ? Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(f, @"\.(png|jpe?g)$", RegexOptions.IgnoreCase) && !used.Contains(f)
                            && !Regex.IsMatch(Path.GetRelativePath(artifacts, f), @"(^|[\\/])(bin-backup|api-bin-backup|node_modules)[\\/]", RegexOptions.IgnoreCase)
                            && ImageSize(f) != null)
                .OrderBy(File.GetLastWriteTime).Take(60).ToList()
            : [];
        if (others.Count > 0) groups.Add(("Ảnh khác trong lần test (chưa gắn vào test case nào)", others));
        if (groups.Count > 0)
        {
            body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            body.Append(P($"{(open.Count > 0 ? 4 : 3)}. Ảnh bằng chứng ({groups.Sum(g => g.files.Count)} ảnh)", bold: true, size: 28, color: "1F3864", after: 120));
            var docPr = 1;
            foreach (var (head, files) in groups)
            {
                body.Append(P(head, bold: true, size: 22, color: "2F5597", after: 80));
                foreach (var f in files)
                {
                    var (pw, ph) = ImageSize(f!)!.Value;
                    const long maxCx = 22L * 360000, maxCy = 12L * 360000; // tối đa 22 × 12 cm, giữ tỉ lệ
                    double scale = Math.Min((double)maxCx / (pw * 9525L), (double)maxCy / (ph * 9525L));
                    scale = Math.Min(scale, 1.0);
                    var img = new ImageRef
                    {
                        Rel = "rIdImg" + (images.Count + 1),
                        Part = $"media/image{images.Count + 1}{Path.GetExtension(f).ToLowerInvariant().Replace(".jpeg", ".jpg")}",
                        Src = f!, Cx = (long)(pw * 9525L * scale), Cy = (long)(ph * 9525L * scale),
                    };
                    images.Add(img);
                    body.Append($"<w:p><w:pPr><w:jc w:val=\"center\"/><w:spacing w:after=\"40\"/></w:pPr>{Drawing(img, docPr++)}</w:p>");
                    body.Append(P(Path.GetFileName(f), size: 16, color: "7F7F7F", align: "center", after: 200));
                }
            }
        }

        // Trang ngang A4, lề 1,5 cm
        var sect = "<w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/><w:pgMar w:top=\"850\" w:right=\"850\" w:bottom=\"850\" w:left=\"850\" w:header=\"400\" w:footer=\"400\" w:gutter=\"0\"/></w:sectPr>";
        var doc = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                  $"<w:document xmlns:w=\"{NsW}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                  "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                  "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                  $"<w:body>{body}{sect}</w:body></w:document>";

        if (File.Exists(file)) File.Delete(file);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            var bytes = new UTF8Encoding(false).GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
        Add("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
        Add("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/_rels/document.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            string.Concat(images.Select(i => $"<Relationship Id=\"{i.Rel}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{i.Part}\"/>")) +
            "</Relationships>");
        Add("word/styles.xml",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"{NsW}\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/><w:lang w:val=\"vi-VN\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"40\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>");
        Add("word/document.xml", doc);
        foreach (var i in images)
        {
            using var s = zip.CreateEntry("word/" + i.Part, CompressionLevel.NoCompression).Open();
            using var src = File.OpenRead(i.Src);
            src.CopyTo(s);
        }
    }

    private static string Drawing(ImageRef i, int id) =>
        "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
        $"<wp:extent cx=\"{i.Cx}\" cy=\"{i.Cy}\"/><wp:docPr id=\"{id}\" name=\"Picture {id}\"/>" +
        "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
        $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"{X(Path.GetFileName(i.Src))}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
        $"<pic:blipFill><a:blip r:embed=\"{i.Rel}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
        $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{i.Cx}\" cy=\"{i.Cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    /// <summary>Đường dẫn bằng chứng: tương đối test-artifacts\ (hoặc tuyệt đối nằm trong thư mục đó). Ngoài thư mục → bỏ.</summary>
    private static string? Resolve(string artifacts, string e)
    {
        if (string.IsNullOrWhiteSpace(e)) return null;
        var root = Path.GetFullPath(artifacts) + Path.DirectorySeparatorChar;
        var name = e.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (name.StartsWith("test-artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) name = name["test-artifacts".Length..].TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(artifacts, name));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>Kích thước điểm ảnh PNG/JPEG; null nếu không phải ảnh đọc được.</summary>
    private static (int W, int H)? ImageSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[24];
            if (fs.Read(head, 0, 24) < 24) return null;
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return ((head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19], (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23]);
            if (head[0] != 0xFF || head[1] != 0xD8) return null;
            fs.Position = 2;
            while (fs.Position < fs.Length)
            {
                if (fs.ReadByte() != 0xFF) continue;
                var marker = fs.ReadByte();
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                var len = (fs.ReadByte() << 8) | fs.ReadByte();
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    fs.ReadByte();
                    var h = (fs.ReadByte() << 8) | fs.ReadByte();
                    var w = (fs.ReadByte() << 8) | fs.ReadByte();
                    return (w, h);
                }
                fs.Position += len - 2;
            }
        }
        catch (IOException) { }
        return null;
    }
}
