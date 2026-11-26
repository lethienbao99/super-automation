using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperAutomation.Core;

/// <summary>Một diễn biến trong phiên Claude: init | ai | tool | tool-error | stderr | out.</summary>
public sealed record ClaudeEvent(string Kind, string Text);

public sealed record ClaudeRun(string? Result, bool IsError, string? Subtype, string? SessionId, double CostUsd, int ExitCode, bool TimedOut)
{
    public string? Model { get; init; }
    public long InTokens { get; init; }
    public long OutTokens { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    /// <summary>Xác thực đã dùng (sau khi xoay key nếu có) và có phải trả tiền thật không.</summary>
    public string? AuthLabel { get; init; }
    public bool Paid { get; init; }
}

/// <summary>Chạy `claude -p --output-format stream-json`, prompt qua stdin, đẩy từng diễn biến qua callback.</summary>
public static class ClaudeCli
{
    public static List<string> BaseArgs(string permissionMode, string? model, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> disallowed)
    {
        List<string> args = ["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", permissionMode];
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model]);
        if (allowed.Count > 0) args.AddRange(["--allowedTools", string.Join(",", allowed)]);
        if (disallowed.Count > 0) args.AddRange(["--disallowedTools", string.Join(",", disallowed)]);
        return args;
    }

    /// <summary>
    /// Chạy một phiên. Khi dùng danh sách API key: key lỗi ngay từ đầu (hết credit, sai key, chạm giới hạn) thì cho key đó
    /// tạm nghỉ và chạy lại phiên bằng key kế tiếp — chỉ khi AI chưa làm gì, để không lặp lại việc đã làm.
    /// </summary>
    public static async Task<ClaudeRun> Run(
        string exe, string cwd, IReadOnlyList<string> args, string prompt, string rawLogPath,
        Action<ClaudeEvent> onEvent, TimeSpan timeout, CancellationToken ct,
        ClaudeAuth? auth = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (run, worked, apiStatus) = await RunOnce(exe, cwd, args, prompt, rawLogPath, onEvent, timeout, ct, auth, append: attempt > 0);
            run = run with { AuthLabel = auth?.Label ?? "Tài khoản đăng nhập Claude Code", Paid = auth?.Env.ContainsKey("SA_ANTHROPIC_KEY") == true };
            if (auth?.OnKeyError is null || !run.IsError || run.TimedOut || KeyProblem(run, apiStatus) is not { } problem) return run;
            var next = auth.OnKeyError(problem.Reason, problem.Cooldown);
            if (worked || next is null)
            {
                if (next is null) onEvent(new ClaudeEvent("tool-error", $"{auth.Label}: {problem.Reason} — không còn key nào khác dùng được."));
                return run;
            }
            onEvent(new ClaudeEvent("tool-error", $"{auth.Label}: {problem.Reason} — chuyển sang {next.Label} và chạy lại."));
            // Giữ biến môi trường riêng của bước (test…) từ lần trước, chỉ thay key
            var env = new Dictionary<string, string>(auth.Env);
            foreach (var (k, v) in next.Env) env[k] = v;
            auth = next with { Env = env };
        }
    }

    /// <summary>Lỗi do key (không phải do AI/mạng): lý do và thời gian cho key tạm nghỉ.</summary>
    public static (string Reason, TimeSpan Cooldown)? KeyProblem(ClaudeRun run, int? apiStatus)
    {
        var text = run.Result ?? "";
        if (run.Subtype == "authentication_failed" || apiStatus is 401 or 403)
            return ($"key bị từ chối (HTTP {apiStatus?.ToString() ?? "401/403"})", TimeSpan.FromHours(24));
        if (text.Contains("credit balance", StringComparison.OrdinalIgnoreCase) || apiStatus == 402)
            return ("hết credit", TimeSpan.FromHours(6));
        // Chạm hạn mức chi tiêu đặt cho key/workspace (HTTP 400 "reached your specified API usage limits … regain access on 2026-12-01 at 00:00 UTC"):
        // cho key nghỉ tới đúng ngày đó
        if (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || text.Contains("regain access", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"regain access on (\d{4}-\d{2}-\d{2})(?: at (\d{1,2}:\d{2}))?");
            var wait = TimeSpan.FromHours(24);
            if (m.Success && DateTime.TryParse($"{m.Groups[1].Value} {(m.Groups[2].Success ? m.Groups[2].Value : "00:00")}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var until)
                && until > DateTime.UtcNow)
                wait = until - DateTime.UtcNow;
            return ($"hết hạn mức sử dụng API (mở lại {(m.Success ? m.Groups[1].Value : "sau 24 giờ")})", wait);
        }
        if (apiStatus == 429 || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
            return ("chạm giới hạn tốc độ (HTTP 429)", TimeSpan.FromMinutes(2));
        return null;
    }

    private static async Task<(ClaudeRun Run, bool Worked, int? ApiStatus)> RunOnce(
        string exe, string cwd, IReadOnlyList<string> args, string prompt, string rawLogPath,
        Action<ClaudeEvent> onEvent, TimeSpan timeout, CancellationToken ct, ClaudeAuth? auth, bool append)
    {
        if (!Directory.Exists(cwd)) throw new PipelineException($"Không tìm thấy thư mục workspace {cwd} — kiểm tra lại Cấu hình.");
        // Chế độ Agent SDK: chạy bộ chuyển tiếp Node với cùng tham số, nó in ra cùng định dạng stream-json
        var runner = auth?.Runner;
        var psi = new ProcessStartInfo(runner is null ? exe : "node")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (runner is not null) psi.ArgumentList.Add(runner);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (auth is not null)
        {
            foreach (var a in auth.Args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in auth.Env) psi.Environment[k] = v;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        Process? started;
        try { started = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (runner is not null)
        {
            throw new PipelineException($"Chế độ Agent SDK cần Node.js (lệnh node) trên máy: {ex.Message}");
        }
        using var p = started ?? throw new PipelineException($"Không chạy được {psi.FileName}.");
        using var killOnCancel = limit.Token.Register(() => Proc.KillTree(p));
        // Prompt đi qua stdin để không vướng giới hạn/escape của dòng lệnh Windows
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();

        var stderr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(CancellationToken.None) is { } l)
                if (l.Trim().Length > 0) onEvent(new ClaudeEvent("stderr", l));
        }, CancellationToken.None);

        string? result = null, subtype = null, sessionId = null;
        var isError = false;
        string? authError = null;
        double cost = 0;
        int? apiStatus = null;
        string? model = null;
        long inTok = 0, outTok = 0, cacheR = 0, cacheW = 0;
        var toolUsed = false;
        await using (var raw = new StreamWriter(rawLogPath, append, new UTF8Encoding(false)))
        {
            while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                await raw.WriteLineAsync(line);
                JsonNode? n;
                try { n = JsonNode.Parse(line); }
                catch (JsonException)
                {
                    onEvent(new ClaudeEvent("out", line));
                    continue;
                }
                switch (S(n, "type"))
                {
                    case "system" when S(n, "subtype") == "init":
                        sessionId = S(n, "session_id");
                        model = S(n, "model");
                        onEvent(new ClaudeEvent("init", $"Phiên Claude {sessionId} — model {S(n, "model")} — xác thực: {S(n, "apiKeySource") ?? "?"}"));
                        break;
                    case "system" when S(n, "subtype") == "api_retry":
                        var status = S(n, "error_status");
                        if (status is "401" or "403" && authError is null)
                        {
                            // Lỗi xác thực không tự hết khi retry — dừng ngay thay vì chờ hết 10 lần thử
                            authError = $"Claude API từ chối xác thực (HTTP {status}, {S(n, "error")}). Kiểm tra API key hoặc đăng nhập lại Claude Code.";
                            onEvent(new ClaudeEvent("tool-error", authError));
                            Proc.KillTree(p);
                        }
                        else if (authError is null)
                        {
                            onEvent(new ClaudeEvent("stderr", $"Claude API lỗi tạm thời (HTTP {status ?? "?"}, {S(n, "error")}) — thử lại lần {S(n, "attempt")}/{S(n, "max_retries")}"));
                        }
                        break;
                    case "assistant":
                        foreach (var c in n?["message"]?["content"] as JsonArray ?? [])
                        {
                            if (S(c, "type") == "text" && S(c, "text") is { Length: > 0 } t) onEvent(new ClaudeEvent("ai", t.Trim()));
                            else if (S(c, "type") == "tool_use")
                            {
                                toolUsed = true;
                                onEvent(new ClaudeEvent("tool", DescribeTool(S(c, "name"), c?["input"])));
                            }
                        }
                        break;
                    case "user":
                        foreach (var c in n?["message"]?["content"] as JsonArray ?? [])
                            if (S(c, "type") == "tool_result" && c?["is_error"]?.GetValue<bool>() == true)
                                onEvent(new ClaudeEvent("tool-error", Trunc(ToolResultText(c["content"]), 600)));
                        break;
                    case "result":
                        result = S(n, "result");
                        isError = n?["is_error"]?.GetValue<bool>() == true;
                        subtype = S(n, "subtype");
                        sessionId = S(n, "session_id") ?? sessionId;
                        if (n?["total_cost_usd"] is JsonValue v && v.TryGetValue<double>(out var usd)) cost = usd;
                        if (n?["usage"] is JsonObject u)
                        {
                            inTok = L(u["input_tokens"]); outTok = L(u["output_tokens"]);
                            cacheR = L(u["cache_read_input_tokens"]); cacheW = L(u["cache_creation_input_tokens"]);
                        }
                        if (n?["api_error_status"] is JsonValue st && st.TryGetValue<int>(out var code)) apiStatus = code;
                        break;
                }
            }
        }
        await p.WaitForExitAsync(CancellationToken.None);
        await stderr;
        ct.ThrowIfCancellationRequested();
        // "Đã làm việc" = có gọi công cụ hoặc đã tốn tiền — khi đó không chạy lại bằng key khác để tránh làm lặp
        var worked = toolUsed || cost > 0;
        var run = authError is not null
            ? new ClaudeRun(authError, true, "authentication_failed", sessionId, cost, p.ExitCode, false)
            : new ClaudeRun(result, isError, subtype, sessionId, cost, p.ExitCode, limit.IsCancellationRequested);
        return (run with { Model = model, InTokens = inTok, OutTokens = outTok, CacheRead = cacheR, CacheWrite = cacheW }, worked, apiStatus);
    }

    private static string DescribeTool(string? name, JsonNode? input)
    {
        string? detail = name switch
        {
            "Bash" or "PowerShell" => S(input, "command"),
            "Read" or "Edit" or "Write" or "NotebookEdit" => S(input, "file_path"),
            "Grep" => $"{S(input, "pattern")} {S(input, "path")}",
            "Glob" => S(input, "pattern"),
            "Skill" => S(input, "skill"),
            "Agent" or "Task" => $"{S(input, "subagent_type")}: {S(input, "description")}",
            _ => input?.ToJsonString(),
        };
        return $"{name}: {Trunc(detail ?? "", 220)}";
    }

    private static string ToolResultText(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray a => string.Join("\n", a.Select(x => S(x, "text")).Where(x => x is not null)),
        _ => content?.ToJsonString() ?? "",
    };

    private static string? S(JsonNode? n, string key) => AmisClient.Str(n, key);
    private static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;

    public static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
