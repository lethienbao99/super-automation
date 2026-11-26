using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SuperAutomation.Core;

/// <summary>Cấu hình môi trường test — lưu ở appsettings.Local.json, mục TestEnv (mật khẩu dạng văn bản, chỉ dùng máy cá nhân).</summary>
public sealed class TestEnvOptions
{
    /// <summary>Có chạy bước test không (tắt = sau review là xong, không test).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Cho bước test chạy Playwright trên web IIS (web phục vụ thẳng từ working copy). Tắt = chỉ kiểm dữ liệu.</summary>
    public bool UiTests { get; set; } = true;
    /// <summary>Trần chi phí riêng bước test (USD); chạm trần thì AI dừng test và ghi kết quả đang có. 0 = không giới hạn.</summary>
    public double MaxTestCostUsd { get; set; } = 8;
    /// <summary>Test FAIL vì lỗi code (có test case FAIL) → AI tự tái hiện lỗi, sửa, review và test lại 1 vòng. Không áp cho task sửa lỗi QC.</summary>
    public bool AutoFixOnTestFail { get; set; } = true;
    /// <summary>Số vòng tự sửa theo test tối đa trong một lượt chạy. Từ vòng 2: chỉ chạy khi vòng trước làm giảm số test case FAIL.</summary>
    public int MaxTestFixRounds { get; set; } = 2;
    /// <summary>Trước test giao diện: vỏ Portal / module task sửa chưa chạy thì tool tự `yarn start…` (chỉ cổng trống), bật xong để chạy tiếp.</summary>
    public bool AutoStartPortal { get; set; } = true;
    /// <summary>Kịch bản cần vai trò không có tài khoản cấu hình → AI được tạo tài khoản TẠM trên DB test (chép hash mật khẩu của tài khoản mặc định,
    /// chỉ gán quyền kịch bản cần, dọn theo script dọn dữ liệu test). Tắt = ghi BLOCKED "cần tài khoản…".</summary>
    public bool TempAccounts { get; set; }
    /// <summary>URL web Main cho Playwright (HRM_BASE_URL). Trống = tự lấy site IIS trỏ vào HRM.Presentation.Main.</summary>
    public string BaseUrl { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Tool tự build các project bị sửa trước bước test (IIS chạy đúng code của nhánh AI).</summary>
    public bool BuildBeforeTest { get; set; } = true;
    /// <summary>Sau khi trả repo về nhánh của người dùng, build lại các project đó để web chạy lại code của người dùng.</summary>
    public bool RebuildAfterReturn { get; set; } = true;
    /// <summary>Web không chạy qua IIS, mỗi dòng "Tên = URL", ví dụ Portal v3 (Angular, ng serve) ở localhost:4200.</summary>
    public string ExtraSites { get; set; } = "Portal v3 (Angular) = http://localhost:4200/";
    /// <summary>Tài khoản test thêm cho vai trò khác (người duyệt, quản trị…) — ngoài tài khoản mặc định User/Password.</summary>
    public List<TestAccount> Accounts { get; set; } = [];
}

/// <summary>Role: ghi chú vai trò/quyền để AI chọn đúng tài khoản cho từng kịch bản. Mật khẩu không bao giờ đưa vào prompt.</summary>
public sealed class TestAccount
{
    public string Role { get; set; } = "";
    public string User { get; set; } = "";
    public string? Password { get; set; } = "";
}

public sealed record IisSite(string Name, string Url, string PhysicalPath, string Project);

public sealed record BuildResult(bool Ok, string Output, List<string> Projects);

/// <summary>Đọc site IIS trỏ vào repo, build project bằng script của workspace, làm nóng site.</summary>
public sealed partial class TestEnvService(SettingsService settings, IWebHostEnvironment env, IConfiguration config, ILogger<TestEnvService> log)
{
    private readonly object _lock = new();
    private TestEnvOptions _opt = Load(config);

    private static TestEnvOptions Load(IConfiguration config)
    {
        var o = new TestEnvOptions();
        config.GetSection("TestEnv").Bind(o);
        return o;
    }

    public TestEnvOptions Options { get { lock (_lock) return Copy(_opt); } }

    private static TestEnvOptions Copy(TestEnvOptions o) => new()
    {
        Enabled = o.Enabled, UiTests = o.UiTests, MaxTestCostUsd = o.MaxTestCostUsd, AutoFixOnTestFail = o.AutoFixOnTestFail, MaxTestFixRounds = o.MaxTestFixRounds, AutoStartPortal = o.AutoStartPortal, TempAccounts = o.TempAccounts, BaseUrl = o.BaseUrl, User = o.User, Password = o.Password,
        BuildBeforeTest = o.BuildBeforeTest, RebuildAfterReturn = o.RebuildAfterReturn, ExtraSites = o.ExtraSites,
        Accounts = o.Accounts.Select(a => new TestAccount { Role = a.Role, User = a.User, Password = a.Password }).ToList(),
    };

    /// <summary>Tài khoản thêm đủ tài khoản + mật khẩu (dùng được để đăng nhập).</summary>
    public List<TestAccount> UsableAccounts() => Options.Accounts.Where(a => a.User.Length > 0 && !string.IsNullOrEmpty(a.Password)).ToList();

    public object View()
    {
        var o = Options;
        return new
        {
            enabled = o.Enabled, uiTests = o.UiTests, maxTestCostUsd = o.MaxTestCostUsd, autoFixOnTestFail = o.AutoFixOnTestFail, maxTestFixRounds = o.MaxTestFixRounds, autoStartPortal = o.AutoStartPortal, tempAccounts = o.TempAccounts, baseUrl = o.BaseUrl, effectiveBaseUrl = BaseUrl(), user = o.User,
            hasPassword = o.Password.Length > 0, buildBeforeTest = o.BuildBeforeTest, rebuildAfterReturn = o.RebuildAfterReturn,
            extraSites = o.ExtraSites, extraSiteList = ExtraSiteList(),
            accounts = o.Accounts.Select(a => new { role = a.Role, user = a.User, hasPassword = !string.IsNullOrEmpty(a.Password) }),
            sites = Sites(), buildScript = BuildScript(), buildScriptExists = File.Exists(BuildScript()),
        };
    }

    /// <summary>Password null = giữ nguyên, "" = xoá.</summary>
    public string? Update(TestEnvOptions o, bool keepPassword)
    {
        o.BaseUrl = (o.BaseUrl ?? "").Trim();
        if (o.BaseUrl.Length > 0 && !Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _)) return "URL web test không hợp lệ.";
        if (o.BaseUrl.Length > 0 && !o.BaseUrl.EndsWith('/')) o.BaseUrl += "/";
        o.User = (o.User ?? "").Trim();
        if (o.MaxTestCostUsd < 0 || o.MaxTestCostUsd > 200) return "Trần chi phí bước test phải từ 0 đến 200 USD (0 = không giới hạn).";
        o.ExtraSites = (o.ExtraSites ?? "").Trim();
        o.Accounts = (o.Accounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a.User))
            .Select(a => new TestAccount { Role = (a.Role ?? "").Trim(), User = a.User.Trim(), Password = a.Password }).ToList();
        if (o.Accounts.GroupBy(a => a.User, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            return $"Tài khoản {dup.Key} bị nhập trùng.";
        foreach (var line in o.ExtraSites.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = line.Contains('=') ? line[(line.LastIndexOf('=') + 1)..].Trim() : line;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return $"Dòng web khác không hợp lệ: \"{line}\" (dạng: Tên = http://…).";
        }
        lock (_lock)
        {
            if (keepPassword) o.Password = _opt.Password;
            o.Password ??= "";
            // Mật khẩu null của tài khoản thêm = giữ mật khẩu đã lưu của cùng tài khoản
            foreach (var a in o.Accounts.Where(a => a.Password is null))
                a.Password = _opt.Accounts.FirstOrDefault(x => x.User.Equals(a.User, StringComparison.OrdinalIgnoreCase))?.Password ?? "";
            _opt = Copy(o);
        }
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "TestEnv");
            s["Enabled"] = o.Enabled;
            s["UiTests"] = o.UiTests;
            s["MaxTestCostUsd"] = o.MaxTestCostUsd;
            s["AutoFixOnTestFail"] = o.AutoFixOnTestFail;
            s["MaxTestFixRounds"] = o.MaxTestFixRounds;
            s["AutoStartPortal"] = o.AutoStartPortal;
            s["TempAccounts"] = o.TempAccounts;
            s["BaseUrl"] = o.BaseUrl;
            s["User"] = o.User;
            s["Password"] = o.Password;
            s["BuildBeforeTest"] = o.BuildBeforeTest;
            s["RebuildAfterReturn"] = o.RebuildAfterReturn;
            s["ExtraSites"] = o.ExtraSites;
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var a in o.Accounts)
                arr.Add(new System.Text.Json.Nodes.JsonObject { ["Role"] = a.Role, ["User"] = a.User, ["Password"] = a.Password ?? "" });
            s["Accounts"] = arr;
        });
        return null;
    }

    public string BuildScript() => Path.Combine(settings.Pipeline.WorkspaceRoot, "tools", "claude-hooks", "stop-build.ps1");

    /// <summary>URL cho Playwright: cấu hình riêng, không có thì site IIS trỏ vào HRM.Presentation.Main.</summary>
    public string BaseUrl()
    {
        var o = Options;
        if (o.BaseUrl.Length > 0) return o.BaseUrl;
        var main = Sites().FirstOrDefault(s => s.Project.Equals("HRM.Presentation.Main", StringComparison.OrdinalIgnoreCase));
        return main is null ? "" : main.Url;
    }

    /// <summary>Site IIS có thư mục nằm trong repo đang cấu hình (đọc applicationHost.config; không có quyền đọc thì trả rỗng).</summary>
    public List<IisSite> Sites()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "config", "applicationHost.config");
        var repo = Path.GetFullPath(settings.Pipeline.RepoPath).TrimEnd('\\') + "\\";
        var result = new List<IisSite>();
        try
        {
            var doc = XDocument.Load(file);
            foreach (var site in doc.Descendants("site"))
            {
                var path = site.Descendants("virtualDirectory").FirstOrDefault(v => (string?)v.Attribute("path") == "/")?.Attribute("physicalPath")?.Value;
                if (path is null) continue;
                path = Environment.ExpandEnvironmentVariables(path);
                if (!Path.GetFullPath(path).StartsWith(repo, StringComparison.OrdinalIgnoreCase)) continue;
                var bindings = site.Descendants("binding")
                    .Select(b => (Proto: (string?)b.Attribute("protocol") ?? "", Info: (string?)b.Attribute("bindingInformation") ?? ""))
                    .ToList();
                // Ưu tiên https có host (đúng chứng chỉ), rồi tới http
                var pick = bindings.FirstOrDefault(b => b.Proto == "https" && b.Info.Split(':').Last().Length > 0);
                if (pick.Info is null) pick = bindings.FirstOrDefault(b => b.Proto == "http");
                if (pick.Info is null) continue;
                var parts = pick.Info.Split(':');
                var host = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "localhost";
                result.Add(new IisSite((string?)site.Attribute("name") ?? "", $"{pick.Proto}://{host}:{parts[1]}/", path, Path.GetFileName(path.TrimEnd('\\'))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            log.LogInformation("Không đọc được cấu hình IIS: {Message}", ex.Message);
        }
        return result;
    }

/// <summary>Web ngoài IIS khai báo trong cấu hình (Project = "(ngoài IIS)").</summary>
    public List<IisSite> ExtraSiteList() =>
        Options.ExtraSites.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Contains('=') ? (Name: l[..l.LastIndexOf('=')].Trim(), Url: l[(l.LastIndexOf('=') + 1)..].Trim()) : (Name: l, Url: l))
            .Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out _))
            .Select(x => new IisSite(x.Name, x.Url.EndsWith('/') ? x.Url : x.Url + "/", "", "(ngoài IIS)"))
            .ToList();

    /// <summary>csproj gần nhất của từng file (đường dẫn tương đối repo) — chỉ file .cs mới cần build.</summary>
    public List<string> ProjectsFor(IEnumerable<string> repoRelativeFiles)
    {
        var repo = settings.Pipeline.RepoPath;
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in repoRelativeFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(repo, rel)));
            while (dir is not null && dir.StartsWith(repo, StringComparison.OrdinalIgnoreCase))
            {
                var proj = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault() : null;
                if (proj is not null) { projects.Add(proj); break; }
                dir = Path.GetDirectoryName(dir);
            }
        }
        return projects.ToList();
    }

    /// <summary>Site IIS bị ảnh hưởng: site có thư mục chứa file bị sửa, hoặc site của project được build kèm phụ thuộc.</summary>
    public List<IisSite> AffectedSites(IEnumerable<string> repoRelativeFiles, IEnumerable<string> builtProjects)
    {
        var repo = settings.Pipeline.RepoPath;
        var paths = repoRelativeFiles.Select(f => Path.GetFullPath(Path.Combine(repo, f)))
            .Concat(builtProjects.Select(p => Path.GetDirectoryName(p)! + "\\")).ToList();
        return Sites().Where(s => paths.Any(p => p.StartsWith(s.PhysicalPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// Build bằng stop-build.ps1 của workspace (MSBuild, Debug, không build lại reference — nhanh).
    /// Truyền thẳng csproj: script tìm csproj gần nhất từ thư mục của "file" nên nhận được cả csproj.
    /// </summary>
    public async Task<BuildResult> Build(IReadOnlyCollection<string> projects, bool withDependents, TimeSpan timeout, CancellationToken ct)
    {
        if (projects.Count == 0) return new BuildResult(true, "Không có project C# nào cần build.", []);
        var script = BuildScript();
        if (!File.Exists(script)) return new BuildResult(false, $"Không thấy script build {script}.", [.. projects]);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = settings.Pipeline.WorkspaceRoot,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // Không dùng -WithDependents của script: nó quét mọi csproj trong thư mục, kể cả project cũ không nằm trong solution
        // (vd Main\Source\HRM.Business.Payroll.Domain bản cũ) → lỗi CS0006 thiếu DLL. Tool tự tìm project phụ thuộc trong .sln.
        var all = withDependents ? WithSolutionDependents(projects) : [.. projects];
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Files", string.Join(",", all) })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new PipelineException("Không chạy được powershell để build.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var outT = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errT = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Proc.KillTree(p);
            if (ct.IsCancellationRequested) throw;
            return new BuildResult(false, $"Build quá {timeout.TotalMinutes:0} phút, đã dừng.", [.. projects]);
        }
        var output = ((await outT) + "\n" + (await errT)).Trim();
        return new BuildResult(p.ExitCode == 0, output, all);
    }

    /// <summary>
    /// Build lại các project web mà site IIS trỏ vào (Main, EmpPortal, Hr/Sys Service, SC.Service.Api…), KÈM project tham chiếu —
    /// dùng sau khi đổi nhánh: code khác nhiều nên không build lẻ được. MSBuild tự xếp thứ tự và bỏ qua project đã mới (incremental).
    /// </summary>
    public async Task<BuildResult> BuildSites(TimeSpan timeout, CancellationToken ct)
    {
        var repo = Path.GetFullPath(settings.Pipeline.RepoPath).TrimEnd('\\') + "\\";
        var projects = Sites().Where(x => x.PhysicalPath.StartsWith(repo, StringComparison.OrdinalIgnoreCase) && Directory.Exists(x.PhysicalPath))
            .Select(x => Directory.GetFiles(x.PhysicalPath, "*.csproj").FirstOrDefault()).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count == 0) return new(true, "Không có site IIS nào trỏ vào repo — không cần build.", []);
        var msbuild = FindMsBuild();
        if (msbuild is null) return new(false, "Không tìm thấy MSBuild (Visual Studio) qua vswhere.", projects);
        var errors = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        foreach (var proj in projects)
        {
            var psi = new ProcessStartInfo(msbuild)
            {
                WorkingDirectory = Path.GetDirectoryName(proj)!,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { proj, "/t:Build", "/p:Configuration=Debug", "/m", "/nologo", "/v:q", "/nr:false", "/clp:ErrorsOnly;NoSummary" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new PipelineException("Không chạy được MSBuild.");
            var outT = p.StandardOutput.ReadToEndAsync(cts.Token);
            var errT = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                Proc.KillTree(p);
                if (ct.IsCancellationRequested) throw;
                return new(false, $"Build quá {timeout.TotalMinutes:0} phút, đã dừng ở {Path.GetFileName(proj)}.", projects);
            }
            if (p.ExitCode != 0)
            {
                var lines = ((await outT) + "\n" + (await errT)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var errs = lines.Where(l => l.Contains(": error ")).Distinct().Take(8).ToList();
                errors.Add($"{Path.GetFileNameWithoutExtension(proj)}: {string.Join(" | ", errs.Count > 0 ? errs : lines.TakeLast(5))}");
            }
        }
        return errors.Count == 0
            ? new(true, $"Build OK {projects.Count} project web: {string.Join(", ", projects.Select(Path.GetFileNameWithoutExtension))}", projects)
            : new(false, string.Join("\n", errors), projects);
    }

    private static string? FindMsBuild()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var psi = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var path = p?.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        p?.WaitForExit(10000);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static readonly Regex SlnProject = new(@"^Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.csproj)""", RegexOptions.Multiline);
    private static readonly Regex ProjectRef = new(@"ProjectReference\s+Include=""([^""]+)""");

    /// <summary>
    /// Thêm các project trong cùng solution có ProjectReference trực tiếp tới project bị sửa (để DLL mới được chép sang web).
    /// Chỉ lấy project có trong .sln chứa project bị sửa — bỏ qua bản sao cũ/unit test không thuộc solution.
    /// </summary>
    public List<string> WithSolutionDependents(IReadOnlyCollection<string> projects)
    {
        var result = projects.Select(Path.GetFullPath).ToList();
        var seen = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
        foreach (var changed in result.ToList())
        {
            foreach (var cand in SolutionProjectsOf(changed))
            {
                if (seen.Contains(cand) || !File.Exists(cand)) continue;
                string xml;
                try { xml = File.ReadAllText(cand); } catch (IOException) { continue; }
                foreach (Match m in ProjectRef.Matches(xml))
                {
                    var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cand)!, m.Groups[1].Value));
                    if (string.Equals(target, changed, StringComparison.OrdinalIgnoreCase)) { seen.Add(cand); result.Add(cand); break; }
                }
            }
        }
        return result;
    }

    /// <summary>Các csproj của mọi .sln (tìm từ thư mục project đi lên tới gốc workspace) có chứa project này.</summary>
    private IEnumerable<string> SolutionProjectsOf(string csproj)
    {
        var root = Path.GetFullPath(settings.Pipeline.WorkspaceRoot).TrimEnd('\\');
        var dir = Path.GetDirectoryName(csproj);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (dir is not null && dir.Length >= root.Length)
        {
            foreach (var sln in Directory.EnumerateFiles(dir, "*.sln"))
            {
                string text;
                try { text = File.ReadAllText(sln); } catch (IOException) { continue; }
                var projs = SlnProject.Matches(text).Select(m => Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value))).ToList();
                if (projs.Any(x => string.Equals(x, csproj, StringComparison.OrdinalIgnoreCase))) found.UnionWith(projs);
            }
            dir = Path.GetDirectoryName(dir);
        }
        return found;
    }

    /// <summary>Gọi thử site sau khi build để IIS nạp lại app (lần đầu sau recycle thường chậm). Trả về mô tả kết quả.</summary>
    public async Task<string> WarmUp(IEnumerable<IisSite> sites, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            // Chứng chỉ tự ký của môi trường dev nội bộ
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = false,
        }) { Timeout = TimeSpan.FromMinutes(3) };
        var notes = new List<string>();
        foreach (var s in sites)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var res = await http.GetAsync(s.Url, ct);
                notes.Add($"{s.Project} {s.Url} → HTTP {(int)res.StatusCode} ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"{s.Project} {s.Url} → không phản hồi ({ex.Message})");
            }
        }
        return string.Join("; ", notes);
    }

}
