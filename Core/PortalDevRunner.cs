using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SuperAutomation.Core;

/// <summary>
/// Tự bật dev server Portal V3 (vỏ / module) mà bước test cần nhưng chưa chạy: `yarn &lt;script&gt;` chạy nền trong repo Portal,
/// log ra data/portal-dev/&lt;project&gt;.log. Chỉ bật cổng đang TRỐNG — không đụng dev server người dùng tự chạy.
/// Bật xong để chạy tiếp (task sau và lúc người dùng kiểm tay khỏi build lại); nhớ PID trong started.json để tắt bằng nút.
/// </summary>
public sealed class PortalDevRunner
{
    public sealed record Started(string Project, int Port, int Pid, DateTime StartTime, string Log);

    // Dấu hiệu ng serve build xong lần đầu (webpack: "Compiled successfully" / "Build at:"; esbuild: "Application bundle generation complete")
    private static readonly Regex Ready = new(@"compiled successfully|Build at:|bundle generation complete|Compiled with warnings", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Failed = new(@"Failed to compile|Error: .*|An unhandled exception occurred|Port \d+ is already in use", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _dir;
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<int, SemaphoreSlim> _portGates = [];
    private List<Started> _started;

    public PortalDevRunner(IOptions<PipelineOptions> opt, IWebHostEnvironment env)
    {
        _dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.DataDir, "portal-dev"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "started.json");
        _started = Load();
    }

    /// <summary>Dev server do tool bật mà tiến trình vẫn còn sống.</summary>
    public List<Started> Alive()
    {
        lock (_lock)
        {
            _started = _started.Where(IsAlive).ToList();
            Save();
            return [.. _started];
        }
    }

    /// <summary>Bật module nếu cổng đang trống rồi chờ build xong (tối đa timeout). Trả (đã sẵn sàng?, mô tả cho log).</summary>
    public async Task<(bool Ok, string Message)> EnsureRunning(string root, PortalDev.Module m, TimeSpan timeout, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_lock) { if (!_portGates.TryGetValue(m.Port, out gate!)) _portGates[m.Port] = gate = new SemaphoreSlim(1, 1); }
        await gate.WaitAsync(ct);
        try
        {
            if (PortalDev.Listening(m.Port))
            {
                // Tool vừa bật ở lượt trước mà còn đang build: chờ tiếp theo log; còn lại (người dùng tự chạy) coi như sẵn sàng
                var mine = Alive().FirstOrDefault(s => s.Port == m.Port);
                if (mine is null || Ready.IsMatch(Tail(mine.Log, 400))) return (true, $"{m.Project} :{m.Port} đang chạy");
                return await WaitReady(mine, null, timeout, ct);
            }
            var log = Path.Combine(_dir, Safe(m.Project) + ".log");
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c yarn {m.Script} > \"{log}\" 2>&1")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true, // đóng stdin: ng serve không treo ở câu hỏi tương tác
            };
            psi.Environment["NG_CLI_ANALYTICS"] = "false";
            psi.Environment["FORCE_COLOR"] = "0";
            var p = Process.Start(psi) ?? throw new PipelineException("Không bật được yarn.");
            p.StandardInput.Close();
            var s = new Started(m.Project, m.Port, p.Id, p.StartTime, log);
            lock (_lock) { _started.RemoveAll(x => x.Port == m.Port); _started.Add(s); Save(); }
            return await WaitReady(s, p, timeout, ct);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PipelineException)
        {
            return (false, $"không bật được {m.Project}: {ex.Message}");
        }
        finally { gate.Release(); }
    }

    private static async Task<(bool, string)> WaitReady(Started s, Process? p, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.Now + timeout;
        while (DateTime.Now < until)
        {
            await Task.Delay(3000, ct);
            var tail = Tail(s.Log, 4000);
            var exited = p is not null ? p.HasExited : !IsAlive(s);
            if (exited) return (false, $"{s.Project}: yarn đã dừng — {LastLines(tail, 15)}");
            if (PortalDev.Listening(s.Port) && Ready.IsMatch(tail))
                return (true, $"{s.Project} :{s.Port} đã bật và build xong");
            if (Failed.IsMatch(tail) && !Ready.IsMatch(tail))
                return (false, $"{s.Project}: build lỗi — {LastLines(tail, 15)}");
        }
        return (false, $"{s.Project}: quá {timeout.TotalMinutes:0} phút chưa build xong (vẫn để chạy tiếp, log {s.Log})");
    }

    /// <summary>Tắt mọi dev server do tool bật (cả cây tiến trình cmd → yarn → node). Trả số đã tắt.</summary>
    public int StopStarted()
    {
        var n = 0;
        foreach (var s in Alive())
        {
            try
            {
                using var k = Process.Start(new ProcessStartInfo("taskkill", $"/PID {s.Pid} /T /F") { UseShellExecute = false, CreateNoWindow = true });
                k?.WaitForExit(10000);
                n++;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        lock (_lock) { _started = _started.Where(IsAlive).ToList(); Save(); }
        return n;
    }

    // PID có thể bị Windows cấp lại cho tiến trình khác: so cả thời điểm bắt đầu
    private static bool IsAlive(Started s)
    {
        try
        {
            using var p = Process.GetProcessById(s.Pid);
            return !p.HasExited && Math.Abs((p.StartTime - s.StartTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string Tail(string file, int chars)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > chars) fs.Seek(-chars, SeekOrigin.End);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static string LastLines(string text, int n) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));

    private static string Safe(string project) => Regex.Replace(project, @"[^\w-]", "_");

    private List<Started> Load()
    {
        try { return File.Exists(_file) ? JsonSerializer.Deserialize<List<Started>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_started)); } catch (IOException) { }
    }
}
