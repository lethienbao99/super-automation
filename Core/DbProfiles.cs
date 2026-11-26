using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Hồ sơ DB theo dự án (ABC → HRM_ABC_PROD…), dùng lại mẫu cấu hình của VnrConfigTool (Config_v2/user_settings/*.json).
/// Mỗi mẫu có sẵn VnrConnectionString cho từng project web, mật khẩu đã mã hoá — tool chép nguyên chuỗi, KHÔNG giải mã.
/// Hồ sơ = dự án → tên mẫu (hoặc chuỗi kết nối chụp từ WebSettings đang dùng) + login SQL thường cho AI (tuỳ chọn).
/// Áp hồ sơ: ghi VnrConnectionString vào các WebSettings.json khai là file cấu hình local của repo BE (sao lưu trước),
/// chạm Web.config cùng thư mục để IIS nạp lại, và chuyển DB test của AI sang cùng server/DB.
/// Lưu trong appsettings.Local.json (mục DbProfiles, không commit).
/// </summary>
public sealed partial class DbProfiles(IWebHostEnvironment env, SettingsService settings, TestDataService testData, ILogger<DbProfiles> log)
{
    public sealed record Profile(string Project, string Template, string WebConnection, string AiUser, string AiPassword);
    public sealed record Template(string Name, string Server, string Database);

    public const string DefaultTemplateDir = @"D:\Tools\VnrConfigTool\VnrConfigTool\WebApp-Src\Config_v2\user_settings";

    [GeneratedRegex("(\"VnrConnectionString\"\\s*:\\s*\")((?:[^\"\\\\]|\\\\.)*)(\")")]
    private static partial Regex ConnRx();

    private PipelineOptions O => settings.Pipeline;

    public static string KeyOf(string project) => ProjectMatch.Key(project);

    // ---------- Lưu trữ ----------

    private JsonObject? Section()
    {
        try
        {
            var f = LocalConfigFile.PathOf(env);
            return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?["DbProfiles"] as JsonObject : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Thư mục mẫu của VnrConfigTool (DbProfiles._TemplateDir trong appsettings.Local.json, mặc định D:\Tools\VnrConfigTool…).</summary>
    public string TemplateDir => (string?)Section()?["_TemplateDir"] is { Length: > 0 } d ? d : DefaultTemplateDir;

    public List<Profile> All() =>
        Section()?.Where(kv => !kv.Key.StartsWith('_') && kv.Value is JsonObject).Select(kv => new Profile(kv.Key,
            (string?)kv.Value?["Template"] ?? "", (string?)kv.Value?["WebConnection"] ?? "",
            (string?)kv.Value?["AiUser"] ?? "", (string?)kv.Value?["AiPassword"] ?? "")).ToList() ?? [];

    public Profile? Find(string project)
    {
        var k = KeyOf(project);
        return All().FirstOrDefault(p => KeyOf(p.Project) == k);
    }

    /// <summary>Lưu hồ sơ. Tham số null = giữ giá trị cũ.</summary>
    public string? Save(string project, string? template, string? webConnection, string? aiUser, string? aiPassword)
    {
        var k = KeyOf(project);
        if (k.Length == 0) return "Thiếu mã dự án.";
        var old = Find(k);
        template = (template ?? old?.Template ?? "").Trim();
        webConnection = (webConnection ?? old?.WebConnection ?? "").Trim();
        if (template.Length > 0 && LoadTemplate(template) is null) return $"Không đọc được mẫu \"{template}\" trong {TemplateDir}.";
        if (template.Length == 0 && Parse(webConnection) is not { Server.Length: > 0, Database.Length: > 0 })
            return "Cần chọn mẫu VnrConfigTool hoặc có chuỗi kết nối (Data Source + Initial Catalog).";
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "DbProfiles");
            if (old is not null && old.Project != k) s.Remove(old.Project);
            s[k] = new JsonObject
            {
                ["Template"] = template,
                ["WebConnection"] = template.Length > 0 ? "" : webConnection,
                ["AiUser"] = (aiUser ?? old?.AiUser ?? "").Trim(),
                ["AiPassword"] = aiPassword ?? old?.AiPassword ?? "",
            };
        });
        return null;
    }

    public void Delete(string project)
    {
        var k = KeyOf(project);
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "DbProfiles");
            foreach (var key in s.Select(kv => kv.Key).Where(x => !x.StartsWith('_') && KeyOf(x) == k).ToList()) s.Remove(key);
        });
    }

    // ---------- Mẫu VnrConfigTool ----------

    /// <summary>Mọi mẫu trong thư mục VnrConfigTool (tên file bỏ .json) kèm server/DB của project Main.</summary>
    public List<Template> Templates()
    {
        if (!Directory.Exists(TemplateDir)) return [];
        var list = new List<Template>();
        foreach (var f in Directory.GetFiles(TemplateDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (LoadTemplate(name) is { } t && t.Values.FirstOrDefault() is { } any && Parse(t.GetValueOrDefault("HRM.Presentation.Main") ?? any) is { } p)
                list.Add(new(name, p.Server, p.Database));
        }
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mẫu khớp dự án: tên mẫu hoặc tên DB khớp mã dự án (cùng quy tắc nhánh/DB).</summary>
    public List<Template> TemplatesFor(string project) =>
        Templates().Where(t => TestDataService.DbMatchesProject(project, t.Database) == true || TestDataService.DbMatchesProject(project, t.Name) == true).ToList();

    /// <summary>Project web (HRM.Presentation.Main…) → VnrConnectionString trong mẫu. null = không đọc được.</summary>
    private Dictionary<string, string>? LoadTemplate(string name)
    {
        var f = Path.Combine(TemplateDir, name + ".json");
        if (Path.GetFileName(f) != name + ".json" || !File.Exists(f)) return null; // chặn ../
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(f));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in root?["Setting"]?.AsArray() ?? [])
            {
                var proj = (string?)s?["Project"];
                var conn = s?["Configuration"]?.AsArray().FirstOrDefault(c => (string?)c?["Name"] == "ConnectionStrings")?["Value"]?.AsArray()
                    .FirstOrDefault(v => (string?)v?["Name"] == "VnrConnectionString")?["Value"];
                if (proj is not null && conn is JsonValue jv && jv.TryGetValue<string>(out var c) && c.Length > 0) map[proj] = c;
            }
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return null; }
    }

    // ---------- Đọc / áp ----------

    /// <summary>Server/DB/User trong chuỗi kết nối (phần chữ thường; mật khẩu không đọc).</summary>
    public static (string Server, string Database, string User)? Parse(string? conn)
    {
        if (string.IsNullOrWhiteSpace(conn)) return null;
        string V(string key) => Regex.Match(conn, $@"(?:^|;)\s*{key}\s*=\s*([^;]*)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var server = V("(?:Data Source|Server|Addr|Address)");
        var db = V("(?:Initial Catalog|Database)");
        return server.Length == 0 && db.Length == 0 ? null : (server, db, V("(?:User ID|UID|User)"));
    }

    /// <summary>Các WebSettings.json (file cấu hình local của repo BE) đang có file trên đĩa.</summary>
    private List<string> WebSettingsFiles() =>
        O.LocalFilesOf(O.RepoRelativePath)
            .Where(f => f.EndsWith("WebSettings.json", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.Combine(O.RepoPath, f.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists).ToList();

    /// <summary>Chuỗi kết nối web đang dùng (WebSettings.json của Main, không có thì file đầu tiên có khoá).</summary>
    public string? CurrentWebConnection()
    {
        foreach (var f in WebSettingsFiles().OrderByDescending(f => f.Contains("HRM.Presentation.Main", StringComparison.OrdinalIgnoreCase)))
            if (ConnRx().Match(File.ReadAllText(f)) is { Success: true } m) return Unescape(m.Groups[2].Value);
        return null;
    }

    /// <summary>"HRM_ABC_PROD trên 10.0.0.5,1433".</summary>
    public string CurrentText() =>
        Parse(CurrentWebConnection()) is { } p ? $"{p.Database} trên {p.Server}" : "chưa đọc được (không thấy VnrConnectionString trong các WebSettings.json khai là file local)";

    public string ProfileText(Profile p) =>
        p.Template.Length > 0 ? $"mẫu {p.Template}" + (Templates().FirstOrDefault(t => t.Name == p.Template) is { } t ? $" ({t.Database} trên {t.Server})" : " (không còn trong VnrConfigTool!)")
        : Parse(p.WebConnection) is { } x ? $"{x.Database} trên {x.Server}" : "?";

    /// <summary>Lưu DB web đang dùng làm hồ sơ của dự án (login AI lấy từ DB test nếu cùng server/DB).</summary>
    public string Capture(string project)
    {
        if (CurrentWebConnection() is not { } conn || Parse(conn) is not { } p) return "Không đọc được VnrConnectionString trong các WebSettings.json khai là file cấu hình local.";
        var t = testData.Options;
        var same = SameDb(t, p.Server, p.Database);
        var err = Save(project, "", conn, same ? t.User : null, same ? t.Password : null);
        return err ?? $"Đã lưu hồ sơ DB cho {KeyOf(project)}: {p.Database} trên {p.Server}" + (same ? $" (login AI: {t.User})." : ".");
    }

    /// <summary>
    /// Áp hồ sơ của dự án. Không có hồ sơ mà có đúng MỘT mẫu VnrConfigTool khớp dự án → dùng mẫu đó (và ghi nhớ).
    /// Trả mô tả từng việc; null = không có hồ sơ và không đoán được mẫu.
    /// </summary>
    public string? Apply(string project)
    {
        var prof = Find(project);
        var guessed = "";
        if (prof is null)
        {
            if (TemplatesFor(project) is not [var only]) return null;
            if (Save(project, only.Name, null, null, null) is not null) return null;
            prof = Find(project)!;
            guessed = $" (tự chọn mẫu VnrConfigTool \"{only.Name}\" — mẫu duy nhất khớp {KeyOf(project)})";
        }
        Dictionary<string, string>? tpl = null;
        if (prof.Template.Length > 0 && (tpl = LoadTemplate(prof.Template)) is null)
            return $"✘ Không đọc được mẫu \"{prof.Template}\" trong {TemplateDir} — không đổi DB.";
        string? ConnFor(string file)
        {
            if (tpl is null) return prof.WebConnection;
            var proj = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            return tpl.GetValueOrDefault(proj) ?? tpl.GetValueOrDefault("HRM.Presentation.Main") ?? tpl.Values.First();
        }
        var main = tpl is null ? prof.WebConnection : tpl.GetValueOrDefault("HRM.Presentation.Main") ?? tpl.Values.First();
        var p = Parse(main)!.Value;

        var lines = new List<string>();
        var backup = Path.GetFullPath(Path.Combine(env.ContentRootPath, O.DataDir, "db-profiles", "backup", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        int changed = 0, same = 0;
        foreach (var f in WebSettingsFiles())
        {
            try
            {
                var text = File.ReadAllText(f);
                var m = ConnRx().Match(text);
                if (!m.Success || ConnFor(f) is not { Length: > 0 } conn) continue;
                var raw = Escape(conn);
                if (m.Groups[2].Value == raw) { same++; continue; }
                Directory.CreateDirectory(backup);
                File.Copy(f, Path.Combine(backup, Path.GetFileName(Path.GetDirectoryName(f)) + "-" + Path.GetFileName(f)), true);
                var bom = File.ReadAllBytes(f) is [0xEF, 0xBB, 0xBF, ..];
                File.WriteAllText(f, text[..m.Groups[2].Index] + raw + text[(m.Groups[2].Index + m.Groups[2].Length)..], new UTF8Encoding(bom));
                // IIS không theo dõi WebSettings.json: chạm Web.config (không đổi nội dung) để app pool nạp lại
                var webConfig = Path.Combine(Path.GetDirectoryName(f)!, "Web.config");
                if (File.Exists(webConfig)) File.SetLastWriteTime(webConfig, DateTime.Now);
                changed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"✘ {Path.GetFileName(Path.GetDirectoryName(f))}: {ex.Message}");
            }
        }
        lines.Insert(0, changed > 0
            ? $"✔ Web → {p.Database} trên {p.Server}{guessed}: đã ghi {changed} file WebSettings.json{(same > 0 ? $" ({same} file đúng sẵn)" : "")}, bản cũ lưu ở {backup}"
            : $"✔ Web đã dùng {p.Database} trên {p.Server}{guessed}.");

        var t = testData.Options;
        if (SameDb(t, p.Server, p.Database) && (prof.AiUser.Length == 0 || prof.AiUser == t.User))
            lines.Add($"✔ DB của AI đã là {p.Database}.");
        else
        {
            t.Server = p.Server;
            t.Database = p.Database;
            if (prof.AiUser.Length > 0) { t.User = prof.AiUser; t.Password = prof.AiPassword; }
            var err = testData.Update(t, keepPassword: prof.AiUser.Length == 0);
            lines.Add(err is null
                ? $"✔ DB của AI (đọc/ghi dữ liệu test) → {p.Database} trên {p.Server}" +
                  (prof.AiUser.Length == 0 ? $" — vẫn dùng login {t.User}; nếu login này không vào được DB mới, khai login SQL cho {KeyOf(project)} ở ⚙ Cấu hình › Hồ sơ DB." : ".")
                : $"✘ Không chuyển DB của AI: {err}");
        }
        log.LogInformation("Áp hồ sơ DB {Project}: {Lines}", KeyOf(project), string.Join(" | ", lines));
        return string.Join("\n", lines);
    }

    /// <summary>Đổi thư mục mẫu VnrConfigTool (trống = mặc định).</summary>
    public string? SetTemplateDir(string? dir)
    {
        dir = (dir ?? "").Trim();
        if (dir.Length > 0 && !Directory.Exists(dir)) return $"Không thấy thư mục {dir}.";
        LocalConfigFile.Update(env, root => LocalConfigFile.Section(root, "DbProfiles")["_TemplateDir"] = dir);
        return null;
    }

    /// <summary>
    /// Thử login AI của một hồ sơ (chưa lưu cũng được): server/DB lấy từ mẫu (hoặc chuỗi đã chụp), login trống = login DB test hiện tại,
    /// mật khẩu null = mật khẩu đã lưu của hồ sơ (hoặc của DB test khi dùng chung login).
    /// </summary>
    public async Task<(bool Ok, string Info)> CheckLogin(string project, string? template, string? aiUser, string? aiPassword, CancellationToken ct)
    {
        var prof = Find(project);
        string? conn = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            var tpl = LoadTemplate(template.Trim());
            if (tpl is null) return (false, $"Không đọc được mẫu \"{template}\".");
            conn = tpl.GetValueOrDefault("HRM.Presentation.Main") ?? tpl.Values.First();
        }
        else conn = prof?.WebConnection;
        if (Parse(conn) is not { } p) return (false, "Chưa chọn mẫu VnrConfigTool cho dự án này.");
        var o = testData.Options;
        o.Server = p.Server;
        o.Database = p.Database;
        var user = (aiUser ?? "").Trim();
        if (user.Length > 0)
        {
            o.Password = aiPassword ?? (prof is not null && prof.AiUser == user ? prof.AiPassword : "");
            o.User = user;
        }
        else if (aiPassword is not null) o.Password = aiPassword;
        if (o.User.Length == 0) return (false, "Chưa có login SQL (nhập login riêng, hoặc khai login ở DB test).");
        return await testData.Check(o, ct);
    }

    /// <summary>DB của AI (Cấu hình › DB test): "HRM_ABC_PROD trên …".</summary>
    public string AiText() => testData.Options is { Database.Length: > 0 } o ? $"{o.Database} trên {o.Server}" : "chưa cấu hình";

    public object View() => new
    {
        templateDir = TemplateDir,
        current = Parse(CurrentWebConnection()) is { } x ? new { server = x.Server, database = x.Database } : null,
        templates = Templates(),
        profiles = All().Select(p => new
        {
            project = p.Project, template = p.Template, text = ProfileText(p),
            aiUser = p.AiUser, hasAiPassword = p.AiPassword.Length > 0,
        }).ToList(),
    };

    private static bool SameDb(TestDbOptions t, string server, string db) =>
        string.Equals(t.Database, db, StringComparison.OrdinalIgnoreCase) && NormServer(t.Server) == NormServer(server);
    private static string NormServer(string s) => s.Replace(" ", "").Replace(":", ",").ToLowerInvariant();
    private static string Unescape(string raw) => JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? raw;
    private static string Escape(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })[1..^1];
}
