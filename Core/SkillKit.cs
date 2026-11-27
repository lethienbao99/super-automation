using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>Một mục trong bộ cài: skill (thư mục skills/{name}) hoặc agent (file agents/{name}.md).</summary>
public sealed class KitItem
{
    public string Kind { get; set; } = "skill";
    public string Name { get; set; } = "";
    /// <summary>Prompt của tool gọi tới mục này (thiếu thì bước code/review yếu hẳn).</summary>
    public bool Required { get; set; }
    public string? Note { get; set; }
    public string Id => $"{Kind}/{Name}";
}

/// <summary>
/// Bộ skill/agent HRM đi kèm tool (thư mục claude-kit/): cài vào ~/.claude (dùng cho mọi phiên Claude trên máy)
/// hoặc .claude của workspace (đi theo repo). Đường dẫn ~/.claude trong nội dung được lưu dạng {{CLAUDE_HOME}}
/// và thay bằng đường dẫn thật lúc cài, để bộ cài không mang tên người dùng của máy đóng gói.
/// </summary>
public sealed partial class SkillKit(IWebHostEnvironment env, SettingsService settings)
{
    private const string Placeholder = "{{CLAUDE_HOME}}";
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".md", ".yml", ".yaml", ".json", ".ps1", ".psm1", ".sh", ".txt", ".py", ".js", ".mjs", ".sql", ".cs" };
    private readonly object _lock = new();

    private string KitDir => Path.Combine(env.ContentRootPath, "claude-kit");
    private string ManifestFile => Path.Combine(KitDir, "kit.json");
    public static string UserHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    private string ProjectHome => Path.Combine(settings.Pipeline.WorkspaceRoot, ".claude");
    private string Home(string target) => target == "project" ? ProjectHome : UserHome;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$")] private static partial Regex SafeName();

    public List<KitItem> Manifest()
    {
        if (!File.Exists(ManifestFile)) return [];
        return JsonSerializer.Deserialize<List<KitItem>>(File.ReadAllText(ManifestFile), EventHub.Json) ?? [];
    }

    private void SaveManifest(List<KitItem> items)
    {
        Directory.CreateDirectory(KitDir);
        File.WriteAllText(ManifestFile, JsonSerializer.Serialize(items.OrderBy(i => i.Kind).ThenByDescending(i => i.Required).ThenBy(i => i.Name),
            new JsonSerializerOptions(EventHub.Json) { WriteIndented = true }), new UTF8Encoding(false));
    }

    /// <summary>Các file của một mục, đường dẫn tương đối trong gốc (skills/x/..., agents/x.md).</summary>
    private static List<string> FilesOf(string root, KitItem it)
    {
        if (it.Kind == "agent")
        {
            var f = Path.Combine(root, "agents", it.Name + ".md");
            return File.Exists(f) ? [Path.Combine("agents", it.Name + ".md")] : [];
        }
        var dir = Path.Combine(root, "skills", it.Name);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "node_modules" or "__pycache__" or ".git"))
            .OrderBy(f => f).ToList();
    }

    private static string Fwd(string p) => p.Replace('\\', '/');

    /// <summary>Nội dung file trong bộ cài sau khi thay {{CLAUDE_HOME}} bằng thư mục đích.</summary>
    private static byte[] Render(string kitFile, string home)
    {
        var bytes = File.ReadAllBytes(kitFile);
        if (!TextExt.Contains(Path.GetExtension(kitFile))) return bytes;
        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains(Placeholder) ? new UTF8Encoding(false).GetBytes(text.Replace(Placeholder, Fwd(home))) : bytes;
    }

    /// <summary>Nội dung file trên máy chuyển về dạng bộ cài: đường dẫn ~/.claude thật → {{CLAUDE_HOME}}.</summary>
    private static byte[] Templatize(string file, string home)
    {
        var bytes = File.ReadAllBytes(file);
        if (!TextExt.Contains(Path.GetExtension(file))) return bytes;
        var text = Encoding.UTF8.GetString(bytes);
        var t = Regex.Replace(text, Regex.Escape(Fwd(home)), Placeholder, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, Regex.Escape(home), Placeholder, RegexOptions.IgnoreCase);
        return t == text ? bytes : new UTF8Encoding(false).GetBytes(t);
    }

    private static string Hash(byte[] b) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\r\n", "\n"))));

    /// <summary>Trạng thái từng mục so với thư mục đích: missing | same | different (kèm file khác).</summary>
    public object View(string target)
    {
        var home = Home(target);
        var items = Manifest();
        var list = items.Select(it =>
        {
            var kitFiles = FilesOf(KitDir, it);
            var diff = new List<string>();
            var missing = 0;
            foreach (var f in kitFiles)
            {
                var dest = Path.Combine(home, f);
                if (!File.Exists(dest)) { missing++; diff.Add(Fwd(f)); continue; }
                if (Hash(Render(Path.Combine(KitDir, f), home)) != Hash(File.ReadAllBytes(dest))) diff.Add(Fwd(f));
            }
            var status = kitFiles.Count == 0 ? "empty" : missing == kitFiles.Count ? "missing" : diff.Count == 0 ? "same" : "different";
            DateTime? kitTime = kitFiles.Count > 0 ? kitFiles.Max(f => File.GetLastWriteTime(Path.Combine(KitDir, f))) : null;
            var installed = FilesOf(home, it);
            DateTime? localTime = installed.Count > 0 ? installed.Max(f => File.GetLastWriteTime(Path.Combine(home, f))) : null;
            return new
            {
                id = it.Id, kind = it.Kind, name = it.Name, required = it.Required, note = it.Note,
                description = Describe(kitFiles.Count > 0 ? Path.Combine(KitDir, kitFiles.First(f => f.EndsWith(".md"))) : null),
                status, files = kitFiles.Count, diff, kitTime, localTime,
            };
        }).ToList();
        // Skill/agent có trên máy (~/.claude) mà bộ cài chưa có — để thêm vào bộ cài
        var known = items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = new List<object>();
        var skillsDir = Path.Combine(UserHome, "skills");
        if (Directory.Exists(skillsDir))
            foreach (var d in Directory.GetDirectories(skillsDir).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))))
                if (!known.Contains("skill/" + Path.GetFileName(d)))
                    local.Add(new { id = "skill/" + Path.GetFileName(d), kind = "skill", name = Path.GetFileName(d), description = Describe(Path.Combine(d, "SKILL.md")) });
        var agentsDir = Path.Combine(UserHome, "agents");
        if (Directory.Exists(agentsDir))
            foreach (var f in Directory.GetFiles(agentsDir, "*.md"))
                if (!known.Contains("agent/" + Path.GetFileNameWithoutExtension(f)))
                    local.Add(new { id = "agent/" + Path.GetFileNameWithoutExtension(f), kind = "agent", name = Path.GetFileNameWithoutExtension(f), description = Describe(f) });
        return new { target, home, kitDir = KitDir, items = list, local, privateIds = PrivateIds(), standards = StandardsFiles().Count, standardsPrivate = StandardsPrivate() };
    }

    /// <summary>Dòng description trong frontmatter.</summary>
    private static string? Describe(string? file)
    {
        if (file is null || !File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file).Take(15))
        {
            var m = Regex.Match(line, @"^description:\s*""?(.*?)""?\s*$");
            if (!m.Success) continue;
            // Chuỗi YAML trong ngoặc kép có ký tự thoát (\" \\)
            var d = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            return d.Length > 260 ? d[..260] + "…" : d;
        }
        return null;
    }

    private List<KitItem> Pick(IEnumerable<string>? ids)
    {
        var items = Manifest();
        var set = (ids ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? items : items.Where(i => set.Contains(i.Id)).ToList();
    }

    /// <summary>Cài các mục vào thư mục đích. overwrite=false: chỉ cài mục chưa có. Bản cũ bị ghi đè được sao lưu vào .claude/backup/.</summary>
    public (List<string> Done, string? Backup) Install(IEnumerable<string>? ids, string target, bool overwrite)
    {
        lock (_lock)
        {
            var home = Home(target);
            var done = new List<string>();
            string? backup = null;
            foreach (var it in Pick(ids))
            {
                var files = FilesOf(KitDir, it);
                if (files.Count == 0) continue;
                var exists = FilesOf(home, it);
                if (exists.Count > 0 && !overwrite) continue;
                if (exists.Count > 0)
                {
                    backup ??= Path.Combine(home, "backup", "kit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    foreach (var f in exists)
                    {
                        var dst = Path.Combine(backup, f);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(Path.Combine(home, f), dst, overwrite: true);
                    }
                    // Skill: xoá file không còn trong bộ cài để bản cài khớp đúng bộ cài
                    if (it.Kind == "skill")
                        foreach (var stale in exists.Except(files, StringComparer.OrdinalIgnoreCase))
                            File.Delete(Path.Combine(home, stale));
                }
                foreach (var f in files)
                {
                    var dst = Path.Combine(home, f);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.WriteAllBytes(dst, Render(Path.Combine(KitDir, f), home));
                }
                done.Add(it.Id);
            }
            return (done, backup);
        }
    }

    /// <summary>
    /// Lấy bản đang cài trên máy này (~/.claude) đưa vào bộ cài — để cập nhật bộ cài trước khi gửi tool cho người khác.
    /// Mục chưa có trong bộ cài thì thêm vào kit.json.
    /// </summary>
    public List<string> Export(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            var items = Manifest();
            var done = new List<string>();
            foreach (var id in ids)
            {
                var parts = id.Split('/', 2);
                if (parts.Length != 2 || parts[0] is not ("skill" or "agent") || !SafeName().IsMatch(parts[1])) continue;
                var it = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (it is null) { it = new KitItem { Kind = parts[0], Name = parts[1] }; items.Add(it); }
                var src = FilesOf(UserHome, it);
                if (src.Count == 0) continue;
                if (it.Kind == "skill")
                {
                    var dir = Path.Combine(KitDir, "skills", it.Name);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                foreach (var f in src)
                {
                    var dst = Path.Combine(KitDir, f);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.WriteAllBytes(dst, Templatize(Path.Combine(UserHome, f), UserHome));
                }
                done.Add(it.Id);
            }
            SaveManifest(items);
            return done;
        }
    }

    // ---------- Gói .zip: chuyển skill/agent nội bộ (không có trên git) giữa các máy ----------

    private string StandardsDir => Path.Combine(env.ContentRootPath, "standards");
    private const long MaxPackageBytes = 50L * 1024 * 1024;
    private const int MaxPackageEntries = 3000;

    /// <summary>Các mục có file nằm ngoài git (bị .gitignore) — người clone repo sẽ không có, cần chuyển bằng gói.</summary>
    public HashSet<string> PrivateIds()
    {
        var tracked = GitTracked(env.ContentRootPath, "claude-kit");
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Manifest())
        {
            var files = FilesOf(KitDir, it);
            // Không đọc được git → coi như nội bộ hết (chọn sẵn nhiều hơn còn hơn bỏ sót)
            if (files.Count > 0 && (tracked is null || files.Any(f => !tracked.Contains(Fwd(Path.Combine("claude-kit", f))))))
                res.Add(it.Id);
        }
        return res;
    }

    /// <summary>Thư mục standards/ có file nào ngoài git không.</summary>
    public bool StandardsPrivate()
    {
        var files = StandardsFiles();
        if (files.Count == 0) return false;
        var tracked = GitTracked(env.ContentRootPath, "standards");
        return tracked is null || files.Any(f => !tracked.Contains("standards/" + f));
    }

    private List<string> StandardsFiles() =>
        Directory.Exists(StandardsDir)
            ? Directory.GetFiles(StandardsDir).Select(Path.GetFileName).OfType<string>().Where(n => SafeName().IsMatch(n)).OrderBy(n => n).ToList()
            : [];

    private static HashSet<string>? GitTracked(string repo, string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "ls-files", "-z", "--", path }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? o.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // máy không có git
    }

    /// <summary>
    /// Đóng gói các mục đã chọn (nội dung trong bộ cài, đường dẫn ~/.claude đã ở dạng {{CLAUDE_HOME}}) + kit.json tương ứng
    /// (+ standards/ nếu chọn) thành file zip.
    /// </summary>
    public byte[] Package(IEnumerable<string> ids, bool withStandards)
    {
        var items = Pick(ids).Where(i => FilesOf(KitDir, i).Count > 0).ToList();
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var man = zip.CreateEntry("kit.json");
            using (var w = man.Open())
                w.Write(new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(items, new JsonSerializerOptions(EventHub.Json) { WriteIndented = true })));
            foreach (var it in items)
                foreach (var f in FilesOf(KitDir, it))
                    zip.CreateEntryFromFile(Path.Combine(KitDir, f), Fwd(f));
            if (withStandards)
                foreach (var f in StandardsFiles())
                    zip.CreateEntryFromFile(Path.Combine(StandardsDir, f), "standards/" + f);
        }
        return ms.ToArray();
    }

    public sealed record ImportResult(List<string> Done, List<string> Standards, string? Backup);

    /// <summary>
    /// Nhập gói zip do <see cref="Package"/> tạo: ghi skill/agent vào claude-kit/, file chuẩn vào standards/, gộp kit.json.
    /// Chỉ nhận đường dẫn skills/{tên}/…, agents/{tên}.md, standards/{file} của các mục có trong kit.json của gói;
    /// bản cũ bị thay được sao lưu vào data/kit-backup/. Gói sai cấu trúc → PipelineException, không ghi gì.
    /// </summary>
    public ImportResult ImportPackage(Stream zipStream)
    {
        using var zip = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        if (entries.Count > MaxPackageEntries) throw new PipelineException($"Gói có quá nhiều file ({entries.Count}).");
        if (entries.Sum(e => e.Length) > MaxPackageBytes) throw new PipelineException("Gói giải nén quá 50 MB.");
        var manEntry = entries.FirstOrDefault(e => e.FullName == "kit.json")
            ?? throw new PipelineException("Không phải gói skill của tool (thiếu kit.json ở gốc file zip).");
        List<KitItem> incoming;
        try
        {
            using var r = new StreamReader(manEntry.Open(), Encoding.UTF8);
            incoming = JsonSerializer.Deserialize<List<KitItem>>(r.ReadToEnd(), EventHub.Json) ?? [];
        }
        catch (JsonException ex) { throw new PipelineException("kit.json trong gói bị hỏng: " + ex.Message); }
        incoming = incoming.Where(i => i.Kind is "skill" or "agent" && SafeName().IsMatch(i.Name)).ToList();
        var ids = incoming.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Kiểm tra hết trước khi ghi: mỗi file phải thuộc một mục trong kit.json của gói (hoặc standards/)
        var plan = new List<(System.IO.Compression.ZipArchiveEntry Entry, string Dest, string? Id)>();
        foreach (var e in entries)
        {
            if (e == manEntry) continue;
            var parts = e.FullName.Replace('\\', '/').Split('/');
            if (parts.Any(p => p is "" or "." or ".." || p.Contains(':')))
                throw new PipelineException($"Đường dẫn không hợp lệ trong gói: {e.FullName}");
            string? id; string dest;
            switch (parts)
            {
                case ["skills", var n, .. var rest] when rest.Length > 0 && ids.Contains("skill/" + n):
                    id = "skill/" + n; dest = Path.Combine(KitDir, "skills", n, Path.Combine(rest)); break;
                case ["agents", var f] when f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && ids.Contains("agent/" + f[..^3]):
                    id = "agent/" + f[..^3]; dest = Path.Combine(KitDir, "agents", f); break;
                case ["standards", var f] when SafeName().IsMatch(f):
                    id = null; dest = Path.Combine(StandardsDir, f); break;
                default:
                    throw new PipelineException($"File lạ trong gói (không thuộc mục nào trong kit.json): {e.FullName}");
            }
            var full = Path.GetFullPath(dest);
            var root = Path.GetFullPath(id is null ? StandardsDir : KitDir) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new PipelineException($"Đường dẫn ra ngoài thư mục: {e.FullName}");
            plan.Add((e, full, id));
        }
        var withFiles = plan.Where(p => p.Id is not null).Select(p => p.Id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Count == 0) throw new PipelineException("Gói không có skill/agent hay file chuẩn nào.");

        lock (_lock)
        {
            var backup = Path.GetFullPath(Path.Combine(env.ContentRootPath, settings.Pipeline.DataDir, "kit-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            var backedUp = false;
            void Backup(string file, string rel)
            {
                if (!File.Exists(file)) return;
                var dst = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
                backedUp = true;
            }
            var items = Manifest();
            foreach (var it in incoming.Where(i => withFiles.Contains(i.Id)))
            {
                var old = items.FirstOrDefault(i => i.Id.Equals(it.Id, StringComparison.OrdinalIgnoreCase));
                foreach (var f in old is null ? FilesOf(KitDir, it) : FilesOf(KitDir, old)) Backup(Path.Combine(KitDir, f), Path.Combine("claude-kit", f));
                // Skill: thay cả thư mục để không sót file cũ đã bị xoá trong bản mới
                if (it.Kind == "skill") { var d = Path.Combine(KitDir, "skills", it.Name); if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
                if (old is not null) items.Remove(old);
                items.Add(it);
            }
            var stds = new List<string>();
            foreach (var (e, dest, id) in plan)
            {
                if (id is null) { Backup(dest, Path.Combine("standards", Path.GetFileName(dest))); stds.Add(Path.GetFileName(dest)); }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
            SaveManifest(items);
            return new(withFiles.OrderBy(x => x).ToList(), stds, backedUp ? backup : null);
        }
    }

    /// <summary>Bỏ một mục khỏi bộ cài (không đụng bản đã cài trên máy).</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            var items = Manifest();
            var it = items.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (it is null || it.Required) return false;
            if (it.Kind == "skill") { var d = Path.Combine(KitDir, "skills", it.Name); if (Directory.Exists(d)) Directory.Delete(d, true); }
            else { var f = Path.Combine(KitDir, "agents", it.Name + ".md"); if (File.Exists(f)) File.Delete(f); }
            items.Remove(it);
            SaveManifest(items);
            return true;
        }
    }
}
