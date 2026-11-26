using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SuperAutomation.Core;

/// <summary>Đọc/ghi appsettings.Local.json (file cấu hình riêng của máy, không commit) — một khoá dùng chung cho mọi nơi ghi.</summary>
public static class LocalConfigFile
{
    private static readonly object Lock = new();

    public static string PathOf(IWebHostEnvironment env) => System.IO.Path.Combine(env.ContentRootPath, "appsettings.Local.json");

    public static void Update(IWebHostEnvironment env, Action<JsonObject> mutate)
    {
        var path = PathOf(env);
        lock (Lock)
        {
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
            mutate(root);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject s) return s;
        s = [];
        root[name] = s;
        return s;
    }
}

/// <summary>Xác thực cho một lần chạy claude: biến môi trường và tham số bổ sung.</summary>
/// <param name="Runner">Đường dẫn sdk-runner/run.mjs khi chạy qua Claude Agent SDK (null = gọi claude CLI).</param>
/// <param name="KeyId">Key trong danh sách xoay tua đang dùng (null = tài khoản đăng nhập hoặc key gõ tay để kiểm tra).</param>
/// <param name="OnKeyError">Báo key hiện tại lỗi (lý do, thời gian tạm nghỉ) → trả xác thực bằng key kế tiếp chưa thử, hoặc null nếu hết key.</param>
public sealed record ClaudeAuth(string Label, IReadOnlyDictionary<string, string> Env, IReadOnlyList<string> Args, string? Runner = null,
    string? KeyId = null, Func<string, TimeSpan, ClaudeAuth?>? OnKeyError = null);

/// <summary>Một dòng key gửi từ form: Id có sẵn = giữ key cũ (chỉ đổi tên/bật tắt); Id trống + Key = key mới.</summary>
public sealed record ApiKeyEdit(string? Id, string? Label, string? Key, bool Enabled = true);

public sealed record SettingsUpdate(bool? AutoCodeWhenClear, List<string>? ExtraRepos, Dictionary<string, List<string>>? LocalFiles,
    string? WorkspaceRoot, string? RepoRelativePath, string? Model,
    string? AuthMode, string? ApiKey, bool ClearApiKey, bool? ChatCanWriteFiles = null, bool? ConfirmCommit = null,
    double? MaxJobCostUsd = null, double? MaxChatCostUsd = null, bool? CostLimitPaidOnly = null,
    List<ApiKeyEdit>? ApiKeys = null, string? KeyStrategy = null, bool? AutoApproveSmall = null, bool? PullBeforeTask = null, string? ReviewMode = null, string? ReviewerModel = null);

/// <summary>
/// Cấu hình chỉnh được lúc chạy: thư mục source, repo được sửa, model, cách xác thực Claude (đăng nhập hoặc API key).
/// Giá trị ban đầu lấy từ appsettings(.Local).json; cập nhật từ UI được ghi lại vào appsettings.Local.json và áp dụng ngay.
/// </summary>
public sealed class SettingsService
{
    private readonly IWebHostEnvironment _env;
    private volatile PipelineOptions _pipeline;
    private volatile string _authMode;
    private readonly ApiKeyPool _pool = new();
    // Dự án AMIS (mã rút gọn, ví dụ DEF) → từ khoá nhánh git, phân tách bằng dấu phẩy. Dùng để cảnh báo giao task khi repo đang ở nhánh của dự án khác.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _projectBranches = new(StringComparer.OrdinalIgnoreCase);

    public SettingsService(IOptions<PipelineOptions> popt, IConfiguration config, IWebHostEnvironment env)
    {
        _env = env;
        _pipeline = popt.Value.Clone();
        var keys = config.GetSection("Claude:ApiKeys").GetChildren()
            .Select(c => new ApiKeyEntry
            {
                Id = c["Id"] is { Length: > 0 } id ? id : NewKeyId(),
                Label = c["Label"] ?? "",
                Key = (c["Key"] ?? "").Trim(),
                Enabled = !string.Equals(c["Enabled"], "false", StringComparison.OrdinalIgnoreCase),
            })
            .Where(k => k.Key.Length > 0).ToList();
        // Cấu hình cũ chỉ có một key
        if (keys.Count == 0 && NullIfEmpty(config["Claude:ApiKey"]) is { } legacy)
            keys.Add(new ApiKeyEntry { Id = NewKeyId(), Label = "Key 1", Key = legacy.Trim() });
        for (var i = 0; i < keys.Count; i++) if (keys[i].Label.Length == 0) keys[i].Label = $"Key {i + 1}";
        _pool.Set(keys, config["Claude:KeyStrategy"] ?? "failover");
        _authMode = config["Claude:AuthMode"] is "apikey" or "sdk" && keys.Count > 0 ? config["Claude:AuthMode"]! : "login";
        foreach (var kv in config.GetSection("ProjectBranches").GetChildren())
            if (!string.IsNullOrWhiteSpace(kv.Value)) _projectBranches[kv.Key] = kv.Value;
    }

    public IReadOnlyDictionary<string, string> ProjectBranches => _projectBranches;

    /// <summary>Ghi nhớ từ khoá nhánh cho một dự án; keywords rỗng thì xoá (quay về mặc định: tên mã dự án).</summary>
    public string? SetProjectBranch(string project, string? keywords)
    {
        project = project.Trim();
        if (project.Length == 0 || project.Length > 100) return "Mã dự án không hợp lệ.";
        var value = string.Join(", ", (keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (value.Length > 300) return "Danh sách từ khoá quá dài.";
        if (value.Length == 0) _projectBranches.TryRemove(project, out _);
        else _projectBranches[project] = value;
        LocalConfigFile.Update(_env, root =>
        {
            var sec = LocalConfigFile.Section(root, "ProjectBranches");
            if (value.Length == 0) sec.Remove(project);
            else sec[project] = value;
        });
        return null;
    }

    public PipelineOptions Pipeline => _pipeline;

    /// <summary>Đang trả tiền thật theo token (API key hoặc Agent SDK), không phải gói đăng nhập.</summary>
    public bool IsPaid => _authMode is "apikey" or "sdk";

    /// <summary>Trần chi phí đang áp (USD) — null nếu không giới hạn hoặc không áp cho cách xác thực hiện tại.</summary>
    public double? CostCap(double cap) => cap > 0 && (IsPaid || !_pipeline.CostLimitPaidOnly) ? cap : null;

    /// <summary>
    /// Cách xác thực cho tiến trình claude. Máy đã đăng nhập Claude Code thì biến ANTHROPIC_API_KEY bị bỏ qua,
    /// nên chế độ API key dùng apiKeyHelper (qua --settings) — nguồn này được Claude Code ưu tiên hơn tài khoản đăng nhập.
    /// Key nằm trong biến môi trường của tiến trình con, không nằm trên dòng lệnh.
    /// </summary>
    public ClaudeAuth ClaudeAuth()
    {
        if (!IsPaid) return Build("login", null);
        var key = _pool.Pick() ?? throw new PipelineException("Chưa có API key nào đang bật — thêm key ở ⚙ Cấu hình › Claude.");
        return ForKey(_authMode, key, []);
    }

    /// <summary>Xác thực bằng một key trong danh sách, kèm cách chuyển sang key kế tiếp khi key này lỗi.</summary>
    private ClaudeAuth ForKey(string mode, ApiKeyEntry key, HashSet<string> tried)
    {
        tried.Add(key.Id);
        return Build(mode, key.Key, key.Label) with
        {
            KeyId = key.Id,
            OnKeyError = (reason, cooldown) =>
            {
                _pool.MarkBad(key.Id, reason, cooldown);
                return _pool.Pick(tried) is { } next ? ForKey(mode, next, tried) : null;
            },
        };
    }

    /// <summary>Key chạy thành công: bỏ trạng thái tạm nghỉ (nếu có).</summary>
    public void KeyOk(string? keyId) { if (keyId is not null) _pool.MarkOk(keyId); }

    /// <summary>Key kiểm tra thất bại: cho tạm nghỉ để pipeline không chọn nó.</summary>
    public void KeyBad(string? keyId, string reason, TimeSpan duration) { if (keyId is not null) _pool.MarkBad(keyId, reason, duration); }

    /// <summary>Xác thực cho lần kiểm tra kết nối: key gõ trên form (chưa lưu), một key cụ thể trong danh sách, hoặc key sẽ được chọn.</summary>
    public ClaudeAuth ClaudeAuthFor(string? mode, string? typedKey, string? keyId = null)
    {
        var m = mode ?? _authMode;
        if (m is not ("apikey" or "sdk")) return Build("login", null);
        if (!string.IsNullOrWhiteSpace(typedKey)) return Build(m, typedKey.Trim(), "key vừa nhập");
        var key = keyId is null ? _pool.Pick() : _pool.Keys.FirstOrDefault(k => k.Id == keyId);
        if (key is null) throw new PipelineException(keyId is null ? "Chọn API key nhưng chưa có key nào đang bật." : "Không thấy key này (đã xoá?).");
        return Build(m, key.Key, key.Label) with { KeyId = key.Id };
    }

    /// <summary>Các key đang bật (để kiểm tra lần lượt).</summary>
    public List<(string Id, string Label)> EnabledKeys() => _pool.Keys.Where(k => k.Enabled).Select(k => (k.Id, k.Label)).ToList();

    private ClaudeAuth Build(string mode, string? key, string? label = null)
    {
        if (mode is not ("apikey" or "sdk") || key is null) return new ClaudeAuth("Tài khoản đăng nhập Claude Code", new Dictionary<string, string>(), []);
        var name = label is null ? "" : $" \"{label}\"";
        if (mode == "sdk")
        {
            // Credit chỉ áp cho Agent SDK/API (không áp cho Claude Code) → chạy qua Agent SDK bằng bộ chuyển tiếp Node
            var runner = System.IO.Path.Combine(_env.ContentRootPath, "sdk-runner", "run.mjs");
            if (!Directory.Exists(System.IO.Path.Combine(_env.ContentRootPath, "sdk-runner", "node_modules", "@anthropic-ai", "claude-agent-sdk")))
                throw new PipelineException($"Chưa cài Agent SDK: chạy \"npm install\" trong {System.IO.Path.GetDirectoryName(runner)}.");
            return new ClaudeAuth($"API key{name} qua Agent SDK", new Dictionary<string, string> { ["SA_ANTHROPIC_KEY"] = key }, [], runner);
        }
        var helperFile = System.IO.Path.Combine(_env.ContentRootPath, _pipeline.DataDir, "claude-apikey.settings.json");
        if (!File.Exists(helperFile))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(helperFile)!);
            File.WriteAllText(helperFile, """
                { "apiKeyHelper": "node -e \"process.stdout.write(process.env.SA_ANTHROPIC_KEY||'')\"" }
                """);
        }
        return new ClaudeAuth($"API key{name}", new Dictionary<string, string> { ["SA_ANTHROPIC_KEY"] = key }, ["--settings", helperFile]);
    }

    public object View() => new
    {
        workspaceRoot = _pipeline.WorkspaceRoot,
        repoRelativePath = _pipeline.RepoRelativePath,
        repoPath = _pipeline.RepoPath,
        extraRepos = _pipeline.ExtraRepos,
        localFiles = _pipeline.LocalFiles,
        model = _pipeline.Model ?? "",
        authMode = _authMode,
        hasApiKey = _pool.Keys.Any(k => k.Enabled),
        keyStrategy = _pool.Strategy,
        apiKeys = _pool.Keys.Select(k => new
        {
            id = k.Id, label = k.Label, masked = Mask(k.Key), enabled = k.Enabled,
            cooldownUntil = _pool.Cooldown(k.Id)?.Until, cooldownReason = _pool.Cooldown(k.Id)?.Reason,
        }).ToList(),
        runTests = _pipeline.RunTests,
        maxFixRounds = _pipeline.MaxFixRounds,
        reviewMode = _pipeline.ReviewMode,
        reviewerModel = _pipeline.ReviewerModel,
        reviewLightMaxLines = _pipeline.ReviewLightMaxLines,
        reviewLightMaxFiles = _pipeline.ReviewLightMaxFiles,
        autoCodeWhenClear = _pipeline.AutoCodeWhenClear,
        autoApproveSmall = _pipeline.AutoApproveSmall,
        pullBeforeTask = _pipeline.PullBeforeTask,
        autoApproveMaxFiles = _pipeline.AutoApproveMaxFiles,
        autoApproveMaxLines = _pipeline.AutoApproveMaxLines,
        chatCanWriteFiles = _pipeline.ChatCanWriteFiles,
        confirmCommit = _pipeline.ConfirmCommit,
        maxJobCostUsd = _pipeline.MaxJobCostUsd,
        maxChatCostUsd = _pipeline.MaxChatCostUsd,
        costLimitPaidOnly = _pipeline.CostLimitPaidOnly,
        projectBranches = _projectBranches.ToDictionary(kv => kv.Key, kv => kv.Value),
        workspaceHasClaudeMd = File.Exists(System.IO.Path.Combine(_pipeline.WorkspaceRoot, "CLAUDE.md")),
    };

    /// <summary>Kiểm tra và áp dụng cấu hình mới. Trả về thông báo lỗi, hoặc null nếu thành công.</summary>
    public string? Apply(SettingsUpdate u)
    {
        var next = _pipeline.Clone();
        if (u.WorkspaceRoot is not null)
        {
            var root = u.WorkspaceRoot.Trim().Trim('"');
            if (!Directory.Exists(root)) return $"Không tìm thấy thư mục workspace: {root}";
            next.WorkspaceRoot = System.IO.Path.GetFullPath(root);
        }
        if (u.RepoRelativePath is not null) next.RepoRelativePath = u.RepoRelativePath.Trim().Trim('"', '\\', '/');
        if (!IsGitRepo(next.RepoPath)) return $"{next.RepoPath} không phải git repo.";
        if (next.RepoRelativePath is "" or ".") return "Repo chính phải là một repo con (ví dụ src\\HRM9), không phải chính thư mục workspace.";
        if (u.ExtraRepos is not null)
        {
            var extra = u.ExtraRepos.Select(r => r.Trim().Trim('"', '\\', '/')).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var r in extra)
            {
                if (r == ".") return "Repo phụ không được là chính thư mục workspace.";
                if (string.Equals(r, next.RepoRelativePath, StringComparison.OrdinalIgnoreCase)) return $"{r} đang là repo chính.";
                if (!IsGitRepo(Path.Combine(next.WorkspaceRoot, r))) return $"{r} không phải git repo.";
            }
            next.ExtraRepos = extra;
        }
        if (u.LocalFiles is not null)
        {
            next.LocalFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (repoRel, files) in u.LocalFiles)
            {
                var key = repoRel.Trim().Trim('"', '\\', '/');
                var list = (files ?? []).Select(f => f.Trim().Trim('"').Replace('\\', '/').TrimStart('/'))
                    .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (list.Any(f => f.Contains("..") || f.Contains('*'))) return $"Đường dẫn file cấu hình local không hợp lệ trong {key} (không dùng .. hoặc *).";
                if (key.Length > 0 && list.Count > 0) next.LocalFiles[key] = list;
            }
        }
        if (u.Model is not null) next.Model = NullIfEmpty(u.Model.Trim());
        if (u.AutoCodeWhenClear is { } auto) next.AutoCodeWhenClear = auto;
        if (u.AutoApproveSmall is { } aas) next.AutoApproveSmall = aas;
        if (u.ReviewMode is { } rm)
        {
            if (rm is not ("auto" or "full" or "light")) return "Mức review không hợp lệ.";
            next.ReviewMode = rm;
        }
        if (u.ReviewerModel is { } rvm)
        {
            if (rvm is not ("" or "sonnet" or "opus")) return "Model cho reviewer không hợp lệ.";
            next.ReviewerModel = rvm;
        }
        if (u.PullBeforeTask is { } pbt) next.PullBeforeTask = pbt;
        if (u.ChatCanWriteFiles is { } cw) next.ChatCanWriteFiles = cw;
        if (u.ConfirmCommit is { } cc) next.ConfirmCommit = cc;
        if (u.MaxJobCostUsd is { } mj) { if (mj < 0 || mj > 10000) return "Giới hạn chi phí job phải từ 0 đến 10000 USD."; next.MaxJobCostUsd = Math.Round(mj, 2); }
        if (u.MaxChatCostUsd is { } mc) { if (mc < 0 || mc > 1000) return "Giới hạn chi phí mỗi tin chat phải từ 0 đến 1000 USD."; next.MaxChatCostUsd = Math.Round(mc, 2); }
        if (u.CostLimitPaidOnly is { } po) next.CostLimitPaidOnly = po;

        var keys = _pool.Keys;
        if (u.ClearApiKey) keys = [];
        if (u.ApiKeys is not null)
        {
            // Form gửi cả danh sách theo thứ tự mong muốn: key cũ theo Id, key mới kèm Key
            var old = keys.ToDictionary(k => k.Id);
            keys = [];
            foreach (var e in u.ApiKeys)
            {
                var label = (e.Label ?? "").Trim();
                if (label.Length > 60) return "Tên key tối đa 60 ký tự.";
                if (e.Id is { Length: > 0 } id && old.TryGetValue(id, out var prev))
                    keys.Add(new ApiKeyEntry { Id = id, Label = label.Length > 0 ? label : prev.Label, Key = prev.Key, Enabled = e.Enabled });
                else if (!string.IsNullOrWhiteSpace(e.Key))
                {
                    var k = e.Key.Trim();
                    if (!k.StartsWith("sk-ant-")) return "API key Anthropic phải bắt đầu bằng \"sk-ant-\".";
                    if (keys.Any(x => x.Key == k)) continue;
                    keys.Add(new ApiKeyEntry { Id = NewKeyId(), Label = label.Length > 0 ? label : $"Key {keys.Count + 1}", Key = k, Enabled = e.Enabled });
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(u.ApiKey))
        {
            var k = u.ApiKey.Trim();
            if (!k.StartsWith("sk-ant-")) return "API key Anthropic phải bắt đầu bằng \"sk-ant-\".";
            if (keys.All(x => x.Key != k)) keys.Add(new ApiKeyEntry { Id = NewKeyId(), Label = $"Key {keys.Count + 1}", Key = k });
        }
        var strategy = u.KeyStrategy ?? _pool.Strategy;
        if (strategy is not ("failover" or "roundrobin")) return "Cách xoay tua key không hợp lệ.";
        var mode = u.AuthMode ?? _authMode;
        if (mode is not ("login" or "apikey" or "sdk")) return "Cách xác thực không hợp lệ.";
        if (mode is "apikey" or "sdk" && !keys.Any(k => k.Enabled)) return "Chọn xác thực bằng API key thì cần ít nhất một key đang bật.";

        LocalConfigFile.Update(_env, root =>
        {
            var p = LocalConfigFile.Section(root, "Pipeline");
            p["WorkspaceRoot"] = next.WorkspaceRoot;
            p["RepoRelativePath"] = next.RepoRelativePath;
            p["Model"] = next.Model ?? "";
            p["ExtraRepos"] = new System.Text.Json.Nodes.JsonArray(next.ExtraRepos.Select(r => (System.Text.Json.Nodes.JsonNode?)r).ToArray());
            var lf = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in next.LocalFiles) lf[k] = new System.Text.Json.Nodes.JsonArray(v.Select(f => (System.Text.Json.Nodes.JsonNode?)f).ToArray());
            p["LocalFiles"] = lf;
            p["AutoCodeWhenClear"] = next.AutoCodeWhenClear;
            p["AutoApproveSmall"] = next.AutoApproveSmall;
            p["ReviewMode"] = next.ReviewMode;
            p["ReviewerModel"] = next.ReviewerModel;
            p["PullBeforeTask"] = next.PullBeforeTask;
            p["ChatCanWriteFiles"] = next.ChatCanWriteFiles;
            p["ConfirmCommit"] = next.ConfirmCommit;
            p["MaxJobCostUsd"] = next.MaxJobCostUsd;
            p["MaxChatCostUsd"] = next.MaxChatCostUsd;
            p["CostLimitPaidOnly"] = next.CostLimitPaidOnly;
            var c = LocalConfigFile.Section(root, "Claude");
            c["AuthMode"] = mode;
            c["ApiKey"] = ""; // cấu hình cũ một key — giờ nằm trong ApiKeys
            c["KeyStrategy"] = strategy;
            c["ApiKeys"] = new JsonArray(keys.Select(k => (JsonNode?)new JsonObject
            {
                ["Id"] = k.Id, ["Label"] = k.Label, ["Key"] = k.Key, ["Enabled"] = k.Enabled,
            }).ToArray());
        });
        _pipeline = next;
        _pool.Set(keys, strategy);
        _authMode = mode;
        return null;
    }

    /// <summary>Các git repo trong workspace (tối đa 3 cấp), để chọn repo cho AI sửa.</summary>
    public static List<string> FindRepos(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".git", ".vs", "packages", "dist" };
        void Walk(string dir, int depth)
        {
            if (IsGitRepo(dir)) result.Add(System.IO.Path.GetRelativePath(root, dir));
            if (depth == 3) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch (UnauthorizedAccessException) { return; } // thư mục hệ thống không có quyền đọc — bỏ qua
            foreach (var s in subs)
                if (!skip.Contains(System.IO.Path.GetFileName(s))) Walk(s, depth + 1);
        }
        Walk(root, 0);
        return result;
    }

    public static bool IsGitRepo(string dir) =>
        Directory.Exists(dir) && (Directory.Exists(System.IO.Path.Combine(dir, ".git")) || File.Exists(System.IO.Path.Combine(dir, ".git")));

    private static string? Mask(string? key) => key is null ? null : key.Length <= 14 ? "••••" : $"{key[..10]}…{key[^4..]}";

    private static string NewKeyId() => Guid.NewGuid().ToString("N")[..8];

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
