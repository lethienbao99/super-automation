using System.IO.Compression;
using System.Text.Json;

namespace SuperAutomation.Core;

/// <summary>
/// Gửi tài liệu của job (thư mục specs/* AI soạn: kế hoạch, hướng dẫn PE, thay đổi API cho App; file Word test case) lên task cha AMIS.
/// Chỉ chạy khi người dùng bấm gửi. Định dạng AMIS không nhận thì nén .zip rồi gửi lại; nhớ định dạng đó để lần sau nén sẵn.
/// </summary>
public sealed class AmisDocs(AmisClient amis, JobStore store, SettingsService settings, IWebHostEnvironment env)
{
    public sealed record Candidate(string Key, string Group, string Path, string Name, long Size, bool Zip);
    public sealed record Result(string Name, string Sent, bool Ok, string Note);

    private string ZipExtFile => System.IO.Path.Combine(env.ContentRootPath, settings.Pipeline.DataDir, "amis-zip-ext.json");

    /// <summary>Đuôi file phải nén trước khi gửi (AMIS không nhận). Mặc định .md, .sql; tự thêm khi gặp lỗi định dạng.</summary>
    private HashSet<string> ZipExts()
    {
        try { if (File.Exists(ZipExtFile)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ZipExtFile)) ?? []; }
        catch (JsonException) { }
        return [".md", ".sql"];
    }

    private void RememberZipExt(string ext)
    {
        var s = ZipExts();
        if (s.Add(ext)) File.WriteAllText(ZipExtFile, JsonSerializer.Serialize(s));
    }

    /// <summary>File gợi ý gửi: thư mục specs/* có file sửa từ lúc job bắt đầu (mọi repo) + file Word test case của job.</summary>
    public List<Candidate> Candidates(Job job)
    {
        var o = settings.Pipeline;
        var zip = ZipExts();
        var since = job.CreatedAt.AddHours(-2);
        var list = new List<Candidate>();
        var dir = store.Dir(job.Id);
        var tc = System.IO.Path.Combine(dir, TestCases.DocxFile);
        if (File.Exists(tc)) list.Add(new("job|" + TestCases.DocxFile, "Test case (QC)", tc, TestCases.DocxName(job, dir), new FileInfo(tc).Length, false));
        // Thư mục specs/* sửa từ lúc job bắt đầu; ưu tiên thư mục có nhắc mã task (ABCVN_434 / 4258190) để không lẫn tài liệu task khác
        var code = TestCases.TaskCode(job, dir);
        var recent = new List<(string Rel, string Path, string Dir, List<FileInfo> Files, bool Mentions)>();
        foreach (var (rel, path) in new[] { (o.RepoRelativePath, o.RepoPath) }.Concat(o.ExtraRepoPaths))
        {
            var specs = System.IO.Path.Combine(path, "specs");
            if (!Directory.Exists(specs)) continue;
            foreach (var d in Directory.EnumerateDirectories(specs))
            {
                var files = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).Where(f => f.Length is > 0 and < 30_000_000).ToList();
                if (files.Count == 0 || files.Max(f => f.LastWriteTime) < since) continue;
                var mentions = files.Where(f => f.Extension is ".md" or ".txt" or ".sql" && f.Length < 2_000_000)
                    .Any(f => { var t = File.ReadAllText(f.FullName); return t.Contains(job.TaskId.ToString()) || (!code.StartsWith("AMIS_") && t.Contains(code)); });
                recent.Add((rel, path, d, files, mentions));
            }
        }
        foreach (var (rel, path, d, files, _) in recent.Any(r => r.Mentions) ? recent.Where(r => r.Mentions) : recent)
            foreach (var f in files.OrderBy(f => f.FullName))
            {
                var relPath = System.IO.Path.GetRelativePath(path, f.FullName).Replace('\\', '/');
                list.Add(new($"{rel}|{relPath}", $"{rel} › {System.IO.Path.GetFileName(d)}", f.FullName, f.Name, f.Length, zip.Contains(f.Extension.ToLowerInvariant())));
            }
        return list;
    }

    /// <summary>Task đích: task cha nếu có, không thì chính task của job.</summary>
    public static (long Id, string Name) Target(Job job) =>
        job.ParentTaskId is { } p ? (p, job.ParentTaskName ?? $"#{p}") : (job.TaskId, job.TaskName);

    public async Task<List<Result>> Send(Job job, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        // Chỉ nhận file nằm trong danh sách gợi ý (không cho gửi đường dẫn tuỳ ý)
        var chosen = Candidates(job).Where(c => keys.Contains(c.Key)).ToList();
        if (chosen.Count == 0) throw new PipelineException("Chưa chọn file nào hợp lệ.");
        var (taskId, taskName) = Target(job);
        var existing = await amis.TaskAttachmentNames(taskId, ct);
        var results = new List<Result>();
        var uploaded = new List<AmisClient.UploadedFile>();
        foreach (var c in chosen)
        {
            var bytes = await File.ReadAllBytesAsync(c.Path, ct);
            var (name, data) = c.Zip ? (ZipName(c.Name), ZipOne(c.Name, bytes)) : (c.Name, bytes);
            if (existing.Any(e => e.Name == name && (e.Size == data.LongLength || e.Size < 0)))
            {
                results.Add(new(c.Name, name, true, "đã có trên AMIS — bỏ qua"));
                continue;
            }
            try
            {
                uploaded.Add(await amis.UploadFile(name, data, ct));
                results.Add(new(c.Name, name, true, c.Zip ? "đã nén zip (định dạng AMIS không nhận)" : "đã gửi"));
            }
            catch (AmisException ex) when (ex.Status != 401 && !c.Zip)
            {
                // AMIS từ chối (thường do định dạng) → nén zip, gửi lại một lần; được thì nhớ đuôi này để lần sau nén sẵn
                var zname = ZipName(c.Name);
                try
                {
                    uploaded.Add(await amis.UploadFile(zname, ZipOne(c.Name, bytes), ct));
                    RememberZipExt(System.IO.Path.GetExtension(c.Name).ToLowerInvariant());
                    results.Add(new(c.Name, zname, true, $"AMIS từ chối file gốc ({Trunc(ex.Message, 120)}) — đã nén zip và gửi lại"));
                }
                catch (AmisException ex2) when (ex2.Status != 401)
                {
                    results.Add(new(c.Name, zname, false, "gửi lỗi cả bản gốc lẫn bản zip: " + Trunc(ex2.Message, 200)));
                }
            }
        }
        if (uploaded.Count > 0) await amis.AttachToTask(taskId, uploaded, ct);
        store.Log(job, "amis", "info", $"Gửi {uploaded.Count} file lên task AMIS #{taskId} ({Trunc(taskName, 80)}): " +
                                       string.Join(", ", results.Where(r => r.Ok && r.Note != "đã có trên AMIS — bỏ qua").Select(r => r.Sent)) +
                                       (results.Any(r => !r.Ok) ? $" — lỗi: {string.Join(", ", results.Where(r => !r.Ok).Select(r => r.Name))}" : ""));
        return results;
    }

    /// <summary>"huong-dan-PE.md" → "huong-dan-PE.zip" (giống cách người dùng vẫn nén tay).</summary>
    private static string ZipName(string name) => System.IO.Path.GetFileNameWithoutExtension(name) + ".zip";

    private static byte[] ZipOne(string entryName, byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            s.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
