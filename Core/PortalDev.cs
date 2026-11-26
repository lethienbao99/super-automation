using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace SuperAutomation.Core;

/// <summary>
/// Portal V3 chạy dev server theo Module Federation: vỏ (yarn start:demo, :4200) + mỗi module một server riêng
/// (yarn start-att:demo → attendance :4202, …). Module chưa chạy thì Portal mở màn vẫn chỉ thấy dashboard,
/// nên trước bước test tool kiểm xem module task sửa đã chạy chưa và báo cho người dùng/AI.
/// </summary>
public static class PortalDev
{
    public sealed record Module(string Project, string Script, int Port, bool Running);

    /// <summary>Repo Portal: có webpack.config.js khai remotes và package.json có script start:demo.</summary>
    public static bool IsPortalRepo(string path) =>
        File.Exists(Path.Combine(path, "webpack.config.js")) && File.Exists(Path.Combine(path, "package.json"))
        && File.ReadAllText(Path.Combine(path, "package.json")).Contains("\"start:demo\"");

    /// <summary>Danh sách vỏ + module (lấy cổng từ webpack.config.js, tên script từ package.json) kèm trạng thái đang chạy.</summary>
    public static List<Module> Modules(string root, int shellPort = 4200)
    {
        var pkg = File.ReadAllText(Path.Combine(root, "package.json"));
        var webpack = File.ReadAllText(Path.Combine(root, "webpack.config.js"));
        var scripts = Regex.Matches(pkg, @"""(start-[\w-]+:demo)""\s*:\s*""ng serve[^""]*?--project\s+([\w-]+)")
            .GroupBy(m => m.Groups[2].Value).ToDictionary(g => g.Key, g => g.First().Groups[1].Value);
        var list = new List<Module> { new("(vỏ Portal)", "start:demo", shellPort, Listening(shellPort)) };
        foreach (Match m in Regex.Matches(webpack, @"(\w+)\s*:\s*`\$\{host\}:(\d+)/remoteEntry\.js`"))
        {
            var project = Regex.Replace(m.Groups[1].Value, "([a-z])([A-Z])", "$1-$2").ToLowerInvariant(); // humanResources → human-resources
            var port = int.Parse(m.Groups[2].Value);
            list.Add(new(project, scripts.GetValueOrDefault(project, $"ng serve --project {project}"), port, Listening(port)));
        }
        return list;
    }

    /// <summary>Repo Portal trong cấu hình (repo phụ đầu tiên có webpack.config.js + start:demo), null nếu không có.</summary>
    public static string? FindRoot(PipelineOptions o) => o.ExtraRepoPaths.Select(p => p.Path).FirstOrDefault(IsPortalRepo);

    /// <summary>Module bị sửa: file đổi nằm dưới projects/&lt;module&gt;/.</summary>
    public static HashSet<string> TouchedProjects(IEnumerable<string> changed) =>
        changed.Select(f => Regex.Match(f.Replace('\\', '/'), @"^projects/([\w-]+)/")).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Listening(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(400) && c.Connected;
        }
        catch { return false; }
    }
}
