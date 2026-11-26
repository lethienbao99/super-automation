using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>Thông tin và thao tác git trên repo mà pipeline sửa (mặc định src/HRM9).</summary>
public sealed partial class RepoService(SettingsService settings)
{
    private const char Sep = '\u001f';
    private PipelineOptions O => settings.Pipeline;

    public string Path => O.RepoPath;

    public Task<string> Git(CancellationToken ct, params string[] args) => GitIn(O.RepoPath, ct, args);

    /// <summary>File đã track đang sửa trong repo; local=false bỏ các file cấu hình local đã khai báo.</summary>
    public async Task<List<(string Path, bool Local)>> Modified(string repoRel, string repoPath, CancellationToken ct)
    {
        var lines = (await GitIn(repoPath, ct, "status", "--porcelain", "--untracked-files=no")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(Porcelain.PathOf).Where(p => p.Length > 0).Select(p => (p, O.IsLocalFile(repoRel, p))).ToList();
    }

    /// <summary>Thay đổi chưa commit thực sự (đã bỏ file cấu hình local).</summary>
    public async Task<List<string>> Dirty(string repoRel, string repoPath, CancellationToken ct) =>
        (await Modified(repoRel, repoPath, ct)).Where(m => !m.Local).Select(m => m.Path).ToList();

    /// <summary>Chạy git trong một repo bất kỳ (repo phụ của job).</summary>
    /// <summary>
    /// GIT_OPTIONAL_LOCKS=0: lệnh chỉ đọc (status, diff…) KHÔNG lấy .git/index.lock để làm mới index.
    /// Giao diện gọi git status mỗi khi job có sự kiện — không có cờ này, chính các lệnh status đó tranh khoá với
    /// git switch/commit của pipeline (và với Visual Studio). Lệnh ghi vẫn lấy khoá như bình thường.
    /// </summary>
    private static readonly Dictionary<string, string> GitEnv = new() { ["GIT_OPTIONAL_LOCKS"] = "0" };

    public async Task<string> GitIn(string repoPath, CancellationToken ct, params string[] args)
    {
        // Visual Studio / VS Code / TortoiseGit giữ .git/index.lock trong chốc lát: gặp khoá thì chờ rồi thử lại (~40s).
        for (var attempt = 1; ; attempt++)
        {
            var (code, output, err) = await Proc.Run("git", repoPath, ["-c", "core.quotepath=false", .. args], ct, GitEnv);
            if (code == 0) return output;
            var msg = err.Trim().Length > 0 ? err.Trim() : output.Trim();
            if (msg.Contains("index.lock") && attempt == 3 && RemoveStaleLock(repoPath)) continue;
            if (msg.Contains("index.lock") && attempt < 13)
            {
                await Task.Delay(500 * attempt, ct);
                continue;
            }
            if (msg.Contains("index.lock"))
                throw new PipelineException($"git {string.Join(' ', args)} lỗi: repo {repoPath} đang bị khoá (.git/index.lock) quá 40 giây. " +
                                            "Có thể một chương trình git khác đang chạy (Visual Studio, VS Code, TortoiseGit…) hoặc git bị tắt ngang trước đó. " +
                                            "Đóng/đợi chương trình đó; nếu không còn git nào chạy thì xoá file .git/index.lock rồi bấm chạy tiếp.");
            throw new PipelineException($"git {string.Join(' ', args)} lỗi: {Trunc(msg, 600)}");
        }
    }

    public async Task<object> GetInfo(CancellationToken ct)
    {
        var branch = (await Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        var head = (await Git(ct, "log", "-1", $"--format=%H{Sep}%h{Sep}%s{Sep}%an{Sep}%cI")).Trim().Split(Sep);
        var status = (await Git(ct, "status", "--porcelain=v1", "--branch")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string? upstream = null;
        int ahead = 0, behind = 0;
        if (status.Length > 0 && status[0].StartsWith("## "))
        {
            var m = BranchLine().Match(status[0]);
            if (m.Success)
            {
                upstream = m.Groups["up"].Success ? m.Groups["up"].Value : null;
                if (m.Groups["ahead"].Success) ahead = int.Parse(m.Groups["ahead"].Value);
                if (m.Groups["behind"].Success) behind = int.Parse(m.Groups["behind"].Value);
            }
        }
        var all = status.Where(l => !l.StartsWith("## ")).ToList();
        // File cấu hình local luôn sửa: không tính là thay đổi
        var files = all.Where(l => l.StartsWith("??") || !O.IsLocalFile(O.RepoRelativePath, Porcelain.PathOf(l))).ToList();
        var localChanged = all.Count - files.Count;
        var untracked = files.Count(l => l.StartsWith("??"));

        var remoteUrl = await TryGit(ct, "config", "--get", "remote.origin.url");
        var name = string.IsNullOrWhiteSpace(remoteUrl)
            ? new DirectoryInfo(O.RepoPath).Name
            : remoteUrl.Trim().TrimEnd('/').Split('/', '\\')[^1].Replace(".git", "");

        return new
        {
            name,
            path = O.RepoPath,
            remoteUrl = remoteUrl?.Trim(),
            branch,
            detached = branch == "HEAD",
            sha = head.ElementAtOrDefault(0),
            shortSha = head.ElementAtOrDefault(1),
            subject = head.ElementAtOrDefault(2),
            author = head.ElementAtOrDefault(3),
            date = head.ElementAtOrDefault(4),
            upstream,
            ahead,
            behind,
            changed = files.Count - untracked,
            localChanged,
            untracked,
            changedFiles = files.Where(l => !l.StartsWith("??")).Take(50).Select(l => l.Trim()),
            isAiBranch = branch.StartsWith(O.BranchPrefix),
        };
    }

    public async Task<List<object>> GetBranches(CancellationToken ct)
    {
        var current = (await Git(ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        var raw = await Git(ct, "for-each-ref", "--sort=-committerdate",
            $"--format=%(refname){Sep}%(refname:short){Sep}%(committerdate:iso-strict){Sep}%(subject){Sep}%(upstream:short)",
            "refs/heads", "refs/remotes/origin");

        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(Sep)).Where(p => p.Length >= 5).ToList();
        foreach (var p in rows.Where(p => p[0].StartsWith("refs/heads/"))) local.Add(p[1]);

        var result = new List<object>();
        var remoteCount = 0;
        foreach (var p in rows)
        {
            var isRemote = p[0].StartsWith("refs/remotes/");
            if (isRemote)
            {
                if (p[1].EndsWith("/HEAD") || p[1] == "origin") continue;
                var shortName = p[1]["origin/".Length..];
                if (local.Contains(shortName)) continue; // đã có nhánh local tương ứng
                if (++remoteCount > 400) continue;
                result.Add(new { name = shortName, remote = true, date = p[2], subject = p[3], upstream = p[1], current = false, ai = shortName.StartsWith(O.BranchPrefix) });
            }
            else
            {
                result.Add(new { name = p[1], remote = false, date = p[2], subject = p[3], upstream = p[4], current = p[1] == current, ai = p[1].StartsWith(O.BranchPrefix) });
            }
        }
        return result;
    }

    /// <summary>
    /// Đổi nhánh. Từ chối khi có thay đổi chưa commit ở file đã track, để không mang code đang sửa dở sang nhánh khác.
    /// Tên nhánh chỉ có ở remote thì git switch tự tạo nhánh local theo dõi origin/&lt;tên&gt;.
    /// </summary>
    public async Task Switch(string branch, CancellationToken ct)
    {
        branch = branch.Trim();
        if (!SafeBranch().IsMatch(branch)) throw new PipelineException("Tên nhánh không hợp lệ.");
        var dirty = await Dirty(O.RepoRelativePath, O.RepoPath, ct);
        if (dirty.Count > 0)
            throw new PipelineException($"Đang có {dirty.Count} file thay đổi chưa commit. Commit hoặc stash trước khi đổi nhánh (file cấu hình local đã khai báo thì không tính).");
        // File cấu hình local đang sửa được git mang theo sang nhánh mới
        await Git(ct, "switch", branch);
    }

    public Task<string> Fetch(CancellationToken ct) => Git(ct, "fetch", "--prune", "origin");

    private async Task<string?> TryGit(CancellationToken ct, params string[] args)
    {
        var (code, output, _) = await Proc.Run("git", O.RepoPath, ["-c", "core.quotepath=false", .. args], ct, GitEnv);
        return code == 0 ? output : null;
    }

    /// <summary>Khoá mà git đã tắt ngang bỏ lại: chỉ khoá đã cũ hơn 60 giây VÀ máy không còn tiến trình git nào.</summary>
    public static Action<string>? OnStaleLock { get; set; }

    private static bool RemoveStaleLock(string repoPath)
    {
        try
        {
            var lockFile = System.IO.Path.Combine(GitDir(repoPath), "index.lock");
            if (!File.Exists(lockFile)) return true; // vừa được nhả — thử lại ngay
            var age = DateTime.Now - File.GetLastWriteTime(lockFile);
            if (age < TimeSpan.FromSeconds(60)) return false;
            if (System.Diagnostics.Process.GetProcessesByName("git").Length > 0) return false; // có git khác đang chạy: không đụng
            File.Delete(lockFile);
            OnStaleLock?.Invoke($"Đã xoá khoá mồ côi {lockFile} (cũ {age.TotalMinutes:0} phút, không còn tiến trình git nào chạy).");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Thư mục .git (repo thường) hoặc thư mục trỏ tới trong file .git (worktree/submodule).</summary>
    private static string GitDir(string repoPath)
    {
        var dotGit = System.IO.Path.Combine(repoPath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var t && t.StartsWith("gitdir:"))
        {
            var p = t["gitdir:".Length..].Trim();
            return System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, p));
        }
        return dotGit;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    [GeneratedRegex(@"^## (?<br>[^.\s]+(?:\.(?!\.)[^.\s]*)*)(?:\.\.\.(?<up>\S+))?(?: \[(?:ahead (?<ahead>\d+))?(?:, )?(?:behind (?<behind>\d+))?\])?")]
    private static partial Regex BranchLine();

    [GeneratedRegex(@"^(?!-)(?!.*\.\.)[\w./@+-]+$")]
    private static partial Regex SafeBranch();
}
