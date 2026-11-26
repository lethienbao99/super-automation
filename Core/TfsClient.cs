using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperAutomation.Core;

/// <summary>
/// Tạo Pull Request trên TFS / Azure DevOps Server từ remote origin của repo (REST api-version 3.0 — TFS 2017 trở lên).
/// Xác thực: PAT trong appsettings.Local.json (mục Tfs:Pat) nếu có, không thì đăng nhập Windows của máy (NTLM/Kerberos).
/// </summary>
public sealed class TfsClient(IWebHostEnvironment env, RepoService repo)
{
    private const string Api = "api-version=3.0";
    private const int MaxDescription = 3000; // TFS giới hạn mô tả PR 4000 ký tự — chừa lề cho emoji/ký tự nhiều byte

    private readonly HttpClient _windows = new(new HttpClientHandler { UseDefaultCredentials = true }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _plain = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ---------- Cấu hình (đọc thẳng file local mỗi lần — đổi trong giao diện là có hiệu lực ngay) ----------

    private JsonObject? LocalSection()
    {
        try
        {
            var f = LocalConfigFile.PathOf(env);
            return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f))?["Tfs"] as JsonObject : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public string Pat => (string?)LocalSection()?["Pat"] ?? "";
    public bool AutoPr => LocalSection()?["AutoPr"] is { } n ? (bool)n : true;

    public object View() => new
    {
        hasPat = Pat.Length > 0,
        masked = Pat.Length > 8 ? $"{Pat[..4]}…{Pat[^4..]}" : Pat.Length > 0 ? "••••" : null,
        autoPr = AutoPr,
    };

    public void Update(string? pat, bool clearPat, bool? autoPr) =>
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "Tfs");
            if (clearPat) s["Pat"] = "";
            else if (!string.IsNullOrWhiteSpace(pat)) s["Pat"] = pat.Trim();
            if (autoPr is { } a) s["AutoPr"] = a;
        });

    // ---------- Gọi API ----------

    private async Task<JsonNode?> Send(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var pat = Pat;
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (pat.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
        // Body dạng chuỗi có sẵn độ dài: JsonContent gửi chunked và chỉ đọc được một lần — đăng nhập Windows (NTLM) phải gửi lại
        // request sau lần 401 đầu nên body lần hai rỗng, TFS báo "Both a source and target reference is required".
        if (body is not null)
            req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await (pat.Length > 0 ? _plain : _windows).SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized)
            throw new PipelineException(pat.Length > 0
                ? "TFS từ chối PAT (hết hạn hoặc thiếu quyền Code read & write) — tạo PAT mới ở ⚙ Cấu hình › TFS."
                : "TFS không nhận đăng nhập Windows của máy này — nhập PAT ở ⚙ Cấu hình › TFS.");
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
        if (!res.IsSuccessStatusCode)
            throw new PipelineException($"TFS trả HTTP {(int)res.StatusCode}: {(string?)json?["message"] ?? (text.Length > 300 ? text[..300] + "…" : text)}");
        return json;
    }

    /// <summary>Remote kiểu http://host/tfs/Collection[/Project]/_git/Repo → (gốc API, tên repo, URL web của repo).</summary>
    private static (string ApiBase, string Repo, string Web)? ParseRemote(string remote)
    {
        if (!Uri.TryCreate(remote.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        var u = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri; // bỏ user@ trong URL remote
        var i = u.IndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var name = Uri.UnescapeDataString(u[(i + 6)..].TrimEnd('/'));
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return (u[..i], name, u.TrimEnd('/'));
    }

    private async Task<(string ApiBase, string RepoId, string Web)> Repo(string repoPath, CancellationToken ct)
    {
        var remote = (await repo.GitIn(repoPath, ct, "remote", "get-url", "origin")).Trim();
        if (ParseRemote(remote) is not { } r) throw new PipelineException($"Remote origin không phải repo TFS/Azure DevOps: {remote}");
        var list = await Send(HttpMethod.Get, $"{r.ApiBase}/_apis/git/repositories?{Api}", null, ct);
        var match = (list?["value"] as JsonArray ?? []).FirstOrDefault(x =>
            string.Equals(Uri.UnescapeDataString((string?)x?["remoteUrl"] ?? ""), Uri.UnescapeDataString(r.Web), StringComparison.OrdinalIgnoreCase))
            ?? (list?["value"] as JsonArray ?? []).FirstOrDefault(x => string.Equals((string?)x?["name"], r.Repo, StringComparison.OrdinalIgnoreCase));
        if (match?["id"] is not { } id) throw new PipelineException($"Không thấy repo {r.Repo} trên TFS ({r.ApiBase}).");
        return (r.ApiBase, (string)id!, r.Web);
    }

    /// <summary>Kiểm kết nối: đọc được repo không, đang dùng cách xác thực nào.</summary>
    public async Task<string> Test(string repoPath, CancellationToken ct)
    {
        var (_, id, web) = await Repo(repoPath, ct);
        return $"Kết nối được {web} (repo {id[..8]}…) bằng {(Pat.Length > 0 ? "PAT" : "đăng nhập Windows")}.";
    }

    public sealed record PrResult(int Id, string Url, bool Existed);

    /// <summary>Tạo PR source → target; nếu đã có PR đang mở cho cặp nhánh này thì trả PR đó.</summary>
    public async Task<PrResult> CreatePr(string repoPath, string source, string target, string title, string description, CancellationToken ct)
    {
        var (api, id, web) = await Repo(repoPath, ct);
        var src = "refs/heads/" + source;
        var dst = "refs/heads/" + target;
        var open = await Send(HttpMethod.Get,
            $"{api}/_apis/git/repositories/{id}/pullrequests?searchCriteria.sourceRefName={Uri.EscapeDataString(src)}&searchCriteria.targetRefName={Uri.EscapeDataString(dst)}&searchCriteria.status=active&{Api}", null, ct);
        if ((open?["value"] as JsonArray)?.FirstOrDefault()?["pullRequestId"] is { } existing)
            return new((int)existing, $"{web}/pullrequest/{(int)existing}", true);
        if (description.Length > MaxDescription) description = description[..MaxDescription] + "\n…";
        var pr = await Send(HttpMethod.Post, $"{api}/_apis/git/repositories/{id}/pullrequests?{Api}",
            new { sourceRefName = src, targetRefName = dst, title, description }, ct);
        var prId = (int?)pr?["pullRequestId"] ?? throw new PipelineException("TFS không trả mã PR.");
        return new(prId, $"{web}/pullrequest/{prId}", false);
    }
}
