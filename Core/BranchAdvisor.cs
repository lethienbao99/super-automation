using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Đề xuất + đổi nhánh gốc dự án cho BE (HRM9) và FE (Portal) từ lệnh chat AMIS ("nhánh ABC" → "đổi nhánh 1").
/// Nhánh gốc theo mẫu của team: BE HRM9-BRANCH/v8.12.48.01/ABCVN_v8.12.48.01, FE projects/8.12/8.12.48/ABCVN.
/// Ghép BE/FE cùng phiên bản (8.12.48). Không có nhánh đúng mẫu thì đưa nhánh khớp dự án mới nhất (đánh dấu "không đúng mẫu").
/// Chỉ đổi khi người dùng chọn; từ chối khi AI đang chạy, có job giữ repo, hoặc có file sửa dở.
/// </summary>
public sealed class BranchAdvisor(SettingsService settings, RepoService repo, JobStore store, JobRunner runner, DbProfiles dbProfiles, TestEnvService testEnv)
{
    public sealed record Candidate(string Name, DateTimeOffset Date, (int, int, int)? Ver, bool Official);
    public sealed record RepoInfo(string Label, string Rel, string Path, string Current, List<Candidate> List);
    /// <summary>Một phương án: mỗi repo một nhánh (null = repo đó không có nhánh cho phiên bản này).</summary>
    public sealed record Option((int, int, int)? Ver, List<Candidate?> Picks);
    public sealed record Offer(string Project, List<RepoInfo> Repos, List<Option> Options, DateTime At);

    // {P} = từ khoá dự án trong tên nhánh (ABCVN). BE: HRM9-BRANCH/v8.12.48.01/ABCVN_v8.12.48.01; FE: projects/8.12/8.12.48/ABCVN
    private const string BePattern = @"^HRM9-BRANCH/v[\d.]+/(?:{P})_v[\d.]+$";
    private const string FePattern = @"^projects/[\d.]+/[\d.]+/(?:{P})$";
    private static readonly Regex VerRx = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private PipelineOptions O => settings.Pipeline;

    private Task<string>? _build;
    /// <summary>Đang build lại BE sau khi đổi nhánh (chưa nên giao task / đổi nhánh tiếp).</summary>
    public bool Building => _build is { IsCompleted: false };

    /// <summary>Build lại các project web IIS chạy nền (sau khi đổi nhánh BE). Trả mô tả kết quả khi xong.</summary>
    public Task<string> StartBuild(string branch)
    {
        if (Building) return _build!;
        return _build = Task.Run(async () =>
        {
            var started = DateTime.Now;
            var b = await testEnv.BuildSites(TimeSpan.FromMinutes(Math.Max(30, O.StageTimeoutMinutes)), CancellationToken.None);
            var mins = (DateTime.Now - started).TotalMinutes;
            return b.Ok
                ? $"🔨 Build BE xong ({mins:0} phút) trên {branch} — web IIS đã chạy code mới. Giao task được rồi.\n{b.Output}"
                : $"🔨 Build BE LỖI trên {branch} ({mins:0} phút) — web có thể đang chạy DLL cũ, mở Visual Studio build lại:\n{(b.Output.Length > 1200 ? b.Output[..1200] + "…" : b.Output)}";
        });
    }

    /// <summary>Fetch mọi repo rồi lập danh sách nhánh gốc của dự án và các phương án ghép theo phiên bản (mới nhất trước, tối đa 3).</summary>
    public async Task<Offer> Suggest(string project, CancellationToken ct)
    {
        var key = ProjectMatch.Norm(project);
        var kw = settings.ProjectBranches.FirstOrDefault(kv => ProjectMatch.Norm(kv.Key) == key).Value?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k != "*").ToList() ?? [];
        // Từ khoá đã ghi nhớ (ABC → ABCVN) hoặc mã dự án + ký tự chữ/số tiếp theo (ABC → ABCVN, ABC1…)
        var p = kw.Count > 0 ? string.Join("|", kw.Select(Regex.Escape)) : Regex.Escape(key) + @"[A-Za-z0-9]*";
        var repos = new List<RepoInfo>();
        foreach (var (label, rel, path, pattern) in Repos())
        {
            try { await repo.GitIn(path, ct, "fetch", "--prune", "origin"); } catch (PipelineException) { /* offline: dùng ref đang có */ }
            var current = (await repo.GitIn(path, ct, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
            var refs = await Refs(path, ct);
            var official = new Regex(pattern.Replace("{P}", p), RegexOptions.IgnoreCase);
            var list = refs.Where(r => official.IsMatch(r.Name)).Select(r => r with { Official = true })
                .OrderByDescending(r => r.Ver).ThenByDescending(r => r.Date).ToList();
            if (list.Count == 0)
                list = refs.Where(r => !r.Name.StartsWith(O.BranchPrefix) && ProjectMatch.Matches(project, r.Name, settings.ProjectBranches))
                    .OrderByDescending(r => r.Date).Take(3).ToList();
            repos.Add(new(label, rel, path, current, list));
        }
        // Phương án theo phiên bản: mỗi repo lấy nhánh đúng mẫu cùng phiên bản (nhánh mới nhất nếu không có phiên bản)
        var vers = repos.SelectMany(r => r.List).Select(c => c.Ver).Distinct().OrderByDescending(v => v).Take(3).ToList();
        var options = vers.Select(v => new Option(v, repos.Select(r => r.List.FirstOrDefault(c => c.Ver == v)).ToList()))
            .Where(o => o.Picks.Any(x => x is not null)).ToList();
        return new(project, repos, options, DateTime.Now);
    }

    /// <summary>Đổi các repo sang nhánh đã chọn (null = giữ nguyên). Kiểm hết điều kiện trước, rồi mới đổi.</summary>
    public async Task<string> Switch(Offer offer, List<Candidate?> picks, CancellationToken ct)
    {
        if (runner.IsBusy) return "AI đang chạy job trên repo — đợi xong hoặc huỷ job rồi mới đổi nhánh.";
        if (Building) return "Đang build lại BE sau lần đổi nhánh trước — đợi build xong rồi đổi tiếp.";
        if (store.All().FirstOrDefault(j => j.Held) is { } held) return $"Job #{held.TaskId} đang giữ repo (code chưa commit trên nhánh AI) — tạo commit hoặc huỷ job đó trước.";
        var todo = offer.Repos.Zip(picks).Where(x => x.Second is not null && x.Second.Name != x.First.Current).ToList();
        if (todo.Count == 0) return "Các repo đã ở đúng nhánh này rồi.";
        foreach (var (r, _) in todo)
        {
            var dirty = await repo.Dirty(r.Rel, r.Path, ct);
            if (dirty.Count > 0)
                return $"{r.Label} có {dirty.Count} file sửa dở chưa commit ({string.Join(", ", dirty.Take(3))}{(dirty.Count > 3 ? "…" : "")}) — commit/stash trước rồi đổi lại. Chưa đổi repo nào.";
        }
        var lines = new List<string>();
        foreach (var (r, c) in todo)
        {
            try
            {
                var note = await SwitchKeepLocal(r, c!.Name, ct);
                var ff = "";
                try { await repo.GitIn(r.Path, ct, "merge", "--ff-only", "@{u}"); ff = ", đã kéo code mới nhất"; }
                catch (PipelineException) { ff = ""; } // không có upstream / không fast-forward được: giữ nguyên
                lines.Add($"✔ {r.Label}: {r.Current} → {c.Name}{ff}{note}");
            }
            catch (PipelineException ex) { lines.Add($"✘ {r.Label}: không đổi được sang {c!.Name} — {ex.Message}"); }
        }
        // Đổi nhánh BE xong → web + AI dùng DB của dự án (hồ sơ DB / mẫu VnrConfigTool)
        if (lines.Any(l => l.StartsWith("✔ BE")))
        {
            var k = DbProfiles.KeyOf(offer.Project);
            lines.Add(dbProfiles.Apply(offer.Project) ??
                      $"ℹ Chưa có hồ sơ DB cho {k} — web vẫn dùng {dbProfiles.CurrentText()}. Gõ \"db {k}\" để chọn mẫu VnrConfigTool cho {k}.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// git switch giữ nguyên file cấu hình local đang sửa. Nếu git từ chối (nhánh đích có bản khác của file đó): cất riêng các file local
    /// vào stash, đổi nhánh, rồi lấy lại; lấy lại bị xung đột thì dùng bản của nhánh mới và để nguyên stash cho người dùng tự xem.
    /// </summary>
    private async Task<string> SwitchKeepLocal(RepoInfo r, string branch, CancellationToken ct)
    {
        try { await repo.GitIn(r.Path, ct, "switch", branch); return ""; } // chỉ có trên origin: git tự tạo nhánh local theo dõi origin/<tên>
        catch (PipelineException ex) when (ex.Message.Contains("would be overwritten"))
        {
            var local = (await repo.Modified(r.Rel, r.Path, ct)).Where(m => m.Local).Select(m => m.Path).ToList();
            if (local.Count == 0) throw;
            await repo.GitIn(r.Path, ct, ["stash", "push", "-m", $"superautomation: cấu hình local trước khi đổi sang {branch}", "--", .. local]);
            try { await repo.GitIn(r.Path, ct, "switch", branch); }
            catch (PipelineException) { await repo.GitIn(r.Path, ct, "stash", "pop"); throw; }
            try { await repo.GitIn(r.Path, ct, "stash", "pop"); return $" (giữ {local.Count} file cấu hình local)"; }
            catch (PipelineException)
            {
                await repo.GitIn(r.Path, ct, ["checkout", "HEAD", "--", .. local]);
                await repo.GitIn(r.Path, ct, ["reset", "-q", "--", .. local]);
                return $" — ⚠ file cấu hình local xung đột với nhánh mới: đang dùng bản của nhánh mới (chuỗi kết nối sẽ được ghi lại theo hồ sơ DB), bản của bạn vẫn nằm trong git stash";
            }
        }
    }

    /// <summary>BE = repo chính; FE = repo phụ là Portal V3; repo phụ khác dùng mẫu FE nếu là Portal, không thì chỉ so khớp dự án.</summary>
    private IEnumerable<(string Label, string Rel, string Path, string Pattern)> Repos()
    {
        yield return ("BE", O.RepoRelativePath, O.RepoPath, BePattern);
        foreach (var (rel, path) in O.ExtraRepoPaths)
            yield return (PortalDev.IsPortalRepo(path) ? "FE" : rel, rel, path, PortalDev.IsPortalRepo(path) ? FePattern : "^$");
    }

    private async Task<List<Candidate>> Refs(string path, CancellationToken ct)
    {
        const char sep = '\u001f';
        var raw = await repo.GitIn(path, ct, "for-each-ref", "--sort=-committerdate", $"--format=%(refname:short){sep}%(committerdate:iso-strict)", "refs/heads", "refs/remotes/origin");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Candidate>();
        foreach (var l in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = l.Split(sep);
            if (parts.Length < 2) continue;
            var name = parts[0].StartsWith("origin/") ? parts[0]["origin/".Length..] : parts[0];
            if (name is "HEAD" or "origin" || !seen.Add(name)) continue;
            DateTimeOffset.TryParse(parts[1], out var d);
            var m = VerRx.Match(name);
            list.Add(new(name, d, m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null, false));
        }
        return list;
    }

    public static string VerText((int, int, int)? v) => v is { } x ? $"{x.Item1}.{x.Item2}.{x.Item3}" : "không rõ phiên bản";
}
