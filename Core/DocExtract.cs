using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SuperAutomation.Core;

/// <summary>
/// Trích nội dung tài liệu Office (docx, xlsx, pptx) thành markdown để Claude đọc được — công cụ Read của Claude
/// chỉ đọc trực tiếp văn bản, ảnh và PDF. Ảnh nhúng trong docx/pptx được lưu ra file riêng cạnh bản trích.
/// </summary>
public static partial class DocExtract
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const int MaxRowsPerSheet = 2000;

    public static string ExtractedName(string fileName) => fileName + ".md";

    public static bool Supports(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".docx" or ".xlsx" or ".xlsm" or ".pptx";

    /// <summary>Trích các tệp hỗ trợ trong thư mục inputs. Trả về thông báo cho log (đã trích / lỗi).</summary>
    public static List<string> ExtractAll(string inputsDir, IEnumerable<string> files)
    {
        var notes = new List<string>();
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".doc" or ".xls" or ".ppt")
            {
                notes.Add($"{f}: định dạng Office cũ (nhị phân) chưa trích được — lưu lại dạng {ext}x hoặc PDF rồi upload.");
                continue;
            }
            if (!Supports(f)) continue;
            try
            {
                var md = Extract(Path.Combine(inputsDir, f), inputsDir);
                File.WriteAllText(Path.Combine(inputsDir, ExtractedName(f)), md, new UTF8Encoding(false));
                notes.Add($"{f}: đã trích nội dung ({md.Length:N0} ký tự) → {ExtractedName(f)}");
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
            {
                notes.Add($"{f}: không trích được ({ex.Message}).");
            }
        }
        return notes;
    }

    public static string Extract(string path, string imageDir)
    {
        using var zip = ZipFile.OpenRead(path);
        var name = Path.GetFileName(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => Docx(zip, name, imageDir),
            ".xlsx" or ".xlsm" => Xlsx(zip, name),
            ".pptx" => Pptx(zip, name, imageDir),
            _ => throw new InvalidDataException("định dạng không hỗ trợ"),
        };
    }

    // ---------- Word ----------

    private static string Docx(ZipArchive zip, string name, string imageDir)
    {
        var doc = Load(zip, "word/document.xml") ?? throw new InvalidDataException("thiếu word/document.xml");
        var rels = Rels(zip, "word/_rels/document.xml.rels", "word/");
        var images = new ImageWriter(zip, imageDir, name);
        var sb = new StringBuilder($"# Nội dung trích từ {name}\n\n");
        var body = doc.Root?.Element(W + "body");
        if (body is null) return sb.ToString();

        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p") AppendParagraph(sb, el, rels, images);
            else if (el.Name == W + "tbl") AppendTable(sb, el, rels, images);
        }
        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, XElement p, Dictionary<string, string> rels, ImageWriter images)
    {
        var text = ParagraphText(p, rels, images).Trim();
        if (text.Length == 0) return;
        var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var heading = HeadingLevel().Match(style);
        if (heading.Success) sb.Append(new string('#', Math.Min(6, int.Parse(heading.Groups[1].Value) + 1))).Append(' ');
        else if (p.Element(W + "pPr")?.Element(W + "numPr") is not null) sb.Append("- ");
        sb.AppendLine(text).AppendLine();
    }

    private static void AppendTable(StringBuilder sb, XElement tbl, Dictionary<string, string> rels, ImageWriter images)
    {
        var rows = tbl.Elements(W + "tr").ToList();
        var first = true;
        foreach (var tr in rows)
        {
            var cells = tr.Elements(W + "tc")
                .Select(tc => string.Join(" ", tc.Elements(W + "p").Select(p => ParagraphText(p, rels, images).Trim()).Where(t => t.Length > 0))
                    .Replace("|", "\\|").Replace("\n", " "))
                .ToList();
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (first)
            {
                sb.AppendLine("|" + string.Concat(cells.Select(_ => " --- |")));
                first = false;
            }
        }
        sb.AppendLine();
    }

    private static string ParagraphText(XElement p, Dictionary<string, string> rels, ImageWriter images)
    {
        var sb = new StringBuilder();
        foreach (var n in p.Descendants())
        {
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append('\t');
            else if (n.Name == W + "br" || n.Name == W + "cr") sb.Append('\n');
            else if (n.Name == A + "blip" && n.Attribute(R + "embed")?.Value is { } id && rels.TryGetValue(id, out var target))
                sb.Append($" [Ảnh: inputs/{images.Save(target)}] ");
        }
        return sb.ToString();
    }

    // ---------- Excel ----------

    private static string Xlsx(ZipArchive zip, string name)
    {
        var shared = Load(zip, "xl/sharedStrings.xml")?.Root?.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() ?? [];
        var rels = Rels(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var sheets = Load(zip, "xl/workbook.xml")?.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? [];
        var sb = new StringBuilder($"# Nội dung trích từ {name}\n\n");

        foreach (var sheet in sheets)
        {
            var rid = sheet.Attribute(R + "id")?.Value;
            if (rid is null || !rels.TryGetValue(rid, out var target)) continue;
            var xml = Load(zip, target);
            if (xml is null) continue;
            sb.AppendLine($"## Sheet: {sheet.Attribute("name")?.Value}");
            var rows = xml.Descendants(S + "row").ToList();
            foreach (var row in rows.Take(MaxRowsPerSheet))
            {
                var cells = row.Elements(S + "c").Select(c => (Ref: c.Attribute("r")?.Value ?? "", Value: CellValue(c, shared))).ToList();
                if (cells.All(c => c.Value.Length == 0)) continue;
                sb.AppendLine($"{row.Attribute("r")?.Value}: " + string.Join(" | ", cells.Where(c => c.Value.Length > 0).Select(c => $"{ColumnOf(c.Ref)}={c.Value}")));
            }
            if (rows.Count > MaxRowsPerSheet) sb.AppendLine($"… (còn {rows.Count - MaxRowsPerSheet} dòng, đã cắt)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string CellValue(XElement c, List<string> shared)
    {
        var v = c.Element(S + "v")?.Value;
        return c.Attribute("t")?.Value switch
        {
            "s" when int.TryParse(v, out var i) && i < shared.Count => shared[i],
            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
            _ => v ?? "",
        };
    }

    private static string ColumnOf(string cellRef) => new(cellRef.TakeWhile(char.IsLetter).ToArray());

    // ---------- PowerPoint ----------

    private static string Pptx(ZipArchive zip, string name, string imageDir)
    {
        var images = new ImageWriter(zip, imageDir, name);
        var sb = new StringBuilder($"# Nội dung trích từ {name}\n\n");
        var slides = zip.Entries
            .Select(e => SlideFile().Match(e.FullName))
            .Where(m => m.Success)
            .OrderBy(m => int.Parse(m.Groups[1].Value));
        foreach (var m in slides)
        {
            var xml = Load(zip, m.Value);
            if (xml is null) continue;
            var rels = Rels(zip, $"ppt/slides/_rels/slide{m.Groups[1].Value}.xml.rels", "ppt/slides/");
            sb.AppendLine($"## Slide {m.Groups[1].Value}");
            foreach (var p in xml.Descendants(A + "p"))
            {
                var text = string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim();
                if (text.Length > 0) sb.AppendLine("- " + text);
            }
            foreach (var blip in xml.Descendants(A + "blip"))
                if (blip.Attribute(R + "embed")?.Value is { } id && rels.TryGetValue(id, out var target))
                    sb.AppendLine($"[Ảnh: inputs/{images.Save(target)}]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ---------- Chung ----------

    private static XDocument? Load(ZipArchive zip, string entry)
    {
        var e = zip.GetEntry(entry);
        if (e is null) return null;
        using var s = e.Open();
        return XDocument.Load(s);
    }

    /// <summary>Id quan hệ → đường dẫn đầy đủ trong gói (target tương đối với baseDir).</summary>
    private static Dictionary<string, string> Rels(ZipArchive zip, string relsEntry, string baseDir)
    {
        var map = new Dictionary<string, string>();
        foreach (var r in Load(zip, relsEntry)?.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var id = r.Attribute("Id")?.Value;
            var target = r.Attribute("Target")?.Value;
            if (id is null || target is null || r.Attribute("TargetMode")?.Value == "External") continue;
            map[id] = target.StartsWith('/') ? target.TrimStart('/') : NormalizePath(baseDir + target);
        }
        return map;
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg is not ("" or ".")) parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Lưu ảnh nhúng một lần cho mỗi entry, tên dạng {tệp gốc}-img-N.{ext}.</summary>
    private sealed class ImageWriter(ZipArchive zip, string dir, string sourceName)
    {
        private readonly Dictionary<string, string> _saved = [];

        public string Save(string entryPath)
        {
            if (_saved.TryGetValue(entryPath, out var existing)) return existing;
            var entry = zip.GetEntry(entryPath);
            if (entry is null) return $"(thiếu {entryPath})";
            var name = $"{Path.GetFileNameWithoutExtension(sourceName)}-img-{_saved.Count + 1}{Path.GetExtension(entryPath).ToLowerInvariant()}";
            entry.ExtractToFile(Path.Combine(dir, name), overwrite: true);
            _saved[entryPath] = name;
            return name;
        }
    }

    [GeneratedRegex(@"^(?:[Hh]eading)?\s*([1-9])$")] private static partial Regex HeadingLevel();
    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")] private static partial Regex SlideFile();
}
