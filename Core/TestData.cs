using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SuperAutomation.Core;

/// <summary>Cấu hình kết nối DB test có quyền ghi — chỉ tool dùng, không truyền cho Claude. Lưu ở appsettings.Local.json, mục TestDb.</summary>
public sealed class TestDbOptions
{
    /// <summary>Cho phép tool chạy script tạo/dọn dữ liệu test do AI soạn.</summary>
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    /// <summary>Tên DB được phép ghi — tool kiểm DB_NAME() thực tế phải trùng tên này trước mỗi lần ghi.</summary>
    public string Database { get; set; } = "";
    /// <summary>Trống = Windows authentication.</summary>
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>AI cũng đọc DB này: khi lưu, tool cập nhật tools/db/ro.local.json (file q.ps1/sp.ps1 dùng) theo server/DB/login ở đây.</summary>
    public bool SyncReadDb { get; set; } = true;
    /// <summary>DB dùng chung cho mọi dự án — tắt kiểm tra tên DB khớp dự án của task.</summary>
    public bool AnyProject { get; set; }
    /// <summary>Tổng số dòng tối đa một script được ghi; vượt thì rollback.</summary>
    public int MaxRows { get; set; } = 500;
    /// <summary>Cho script gọi EXEC stored procedure (vẫn cấm xp_*, sp_executesql, SQL động).</summary>
    public bool AllowExec { get; set; }
    /// <summary>Không bao giờ tự dọn dữ liệu test (kể cả khi duyệt/từ chối/lỗi) — người dùng tự chạy test-cleanup.sql hoặc bấm "Dọn dữ liệu test".</summary>
    public bool KeepData { get; set; }
    /// <summary>
    /// Bước test tự deploy store/function/view mà nhánh AI sửa (Updates/Stores) lên DB này, lưu bản cũ để khôi phục
    /// khi từ chối/huỷ job hoặc bấm "Khôi phục store".
    /// </summary>
    public bool DeploySql { get; set; }
    /// <summary>Cho AI ghi DB test trong lúc code/test (cấp quyền tài khoản test, EXEC Generate, dữ liệu kịch bản) — qua tool, có kiểm và nhật ký.</summary>
    public bool AllowAiWrite { get; set; }
    /// <summary>Số dòng tối đa một script AI được ghi; vượt thì rollback.</summary>
    public int AiMaxRows { get; set; } = 5000;
}

public sealed record SqlRunResult(bool Ok, int Rows, string Message);

/// <summary>Một định nghĩa store/function/view/trigger lấy từ file SQL của repo.</summary>
public sealed record SqlObject(string Name, string Kind, string Body);

public sealed record SqlDeployResult(bool Ok, List<string> Done, List<string> Same, string Message);

/// <summary>
/// Chạy script dữ liệu test do AI soạn, với các lớp bảo vệ: kiểm tra cú pháp được phép (seed chỉ INSERT, cleanup chỉ DELETE/UPDATE có WHERE),
/// xác nhận đúng DB được phép ghi, chạy trong một transaction, giới hạn số dòng, lỗi thì rollback.
/// </summary>
public sealed partial class TestDataService(IWebHostEnvironment env, IConfiguration config, SettingsService settings, ILogger<TestDataService> log)
{
    private readonly object _lock = new();
    private TestDbOptions _opt = Load(config);

    private static TestDbOptions Load(IConfiguration config)
    {
        var o = new TestDbOptions();
        config.GetSection("TestDb").Bind(o);
        return o;
    }

    public TestDbOptions Options { get { lock (_lock) return Copy(_opt); } }

    private static TestDbOptions Copy(TestDbOptions o) => new()
    {
        Enabled = o.Enabled, Server = o.Server, Database = o.Database, User = o.User, Password = o.Password,
        TrustServerCertificate = o.TrustServerCertificate, MaxRows = o.MaxRows, AllowExec = o.AllowExec, KeepData = o.KeepData,
        AnyProject = o.AnyProject, SyncReadDb = o.SyncReadDb, DeploySql = o.DeploySql, AllowAiWrite = o.AllowAiWrite, AiMaxRows = o.AiMaxRows,
    };

    /// <summary>DB mà AI truy vấn (chỉ đọc) qua tools/db — đọc từ tools/db/ro.local.json của workspace, không lấy mật khẩu.</summary>
    public (string Server, string Database, string User)? ReadDb()
    {
        var file = Path.Combine(settings.Pipeline.WorkspaceRoot, "tools", "db", "ro.local.json");
        try
        {
            if (!File.Exists(file)) return null;
            var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            return ((string?)j?["server"] ?? "", (string?)j?["database"] ?? "", (string?)j?["user"] ?? "");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogInformation("Không đọc được {File}: {Message}", file, ex.Message);
            return null;
        }
    }

    /// <summary>DB ghi có trùng DB AI đọc không (cùng server và tên DB).</summary>
    public bool SameAsReadDb()
    {
        var r = ReadDb();
        var o = Options;
        return r is { } x && Norm(x.Server) == Norm(o.Server) && string.Equals(x.Database, o.Database, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string server) => server.Replace(" ", "").Replace(":", ",").ToLowerInvariant();

    /// <summary>
    /// Tên DB có khớp dự án không — cùng quy tắc với nhánh: từ đầu của mã dự án là đầu một đoạn tên DB (XYZ ↔ HRM12_XYZ), từ khác trùng một đoạn,
    /// hoặc cả mã liền nằm trong tên DB. null = không xác định (không có dự án / tên DB).
    /// </summary>
    public static bool? DbMatchesProject(string? projectName, string database)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(database)) return null;
        var key = ProjectMatch.Key(projectName);
        if (key.Length > 0 && ProjectMatch.Norm(database).Contains(key)) return true;
        var tokens = ProjectMatch.Tokens(database);
        return ProjectMatch.Words(projectName).Select((w, i) => (w, i))
            .Any(x => tokens.Any(t => t == x.w || (x.i == 0 && t.StartsWith(x.w, StringComparison.Ordinal))));
    }

    /// <summary>Đủ thông tin để ghi (đã bật, có server và tên DB).</summary>
    public bool Ready => Options is { Enabled: true } o && o.Server.Length > 0 && o.Database.Length > 0;

    public object View()
    {
        var o = Options;
        return new
        {
            enabled = o.Enabled, server = o.Server, database = o.Database, user = o.User, hasPassword = o.Password.Length > 0, anyProject = o.AnyProject,
            syncReadDb = o.SyncReadDb,
            readDb = ReadDb() is { } r ? new { server = r.Server, database = r.Database, user = r.User } : null,
            sameAsReadDb = SameAsReadDb(),
            trustServerCertificate = o.TrustServerCertificate, maxRows = o.MaxRows, allowExec = o.AllowExec, keepData = o.KeepData, deploySql = o.DeploySql, allowAiWrite = o.AllowAiWrite,
        };
    }

    /// <summary>Password null = giữ mật khẩu đã lưu.</summary>
    public string? Update(TestDbOptions o, bool keepPassword)
    {
        o.Server = (o.Server ?? "").Trim();
        o.Database = (o.Database ?? "").Trim();
        o.User = (o.User ?? "").Trim();
        if (o.MaxRows is < 1 or > 100_000) return "Giới hạn số dòng phải từ 1 đến 100000.";
        if (o.Enabled && (o.Server.Length == 0 || o.Database.Length == 0)) return "Bật ghi dữ liệu test thì cần nhập server và tên DB.";
        if (o.SyncReadDb && o.Server.Length > 0 && o.User.Length == 0)
            return "Để AI đọc DB này cần nhập login SQL (tools/db/q.ps1 không hỗ trợ Windows authentication).";
        if (o.Database.Length > 0 && !SafeName().IsMatch(o.Database)) return "Tên DB không hợp lệ.";
        lock (_lock)
        {
            if (keepPassword) o.Password = _opt.Password;
            o.Password ??= "";
            _opt = Copy(o);
        }
        LocalConfigFile.Update(env, root =>
        {
            var s = LocalConfigFile.Section(root, "TestDb");
            s["Enabled"] = o.Enabled;
            s["Server"] = o.Server;
            s["Database"] = o.Database;
            s["User"] = o.User;
            s["Password"] = o.Password;
            s["TrustServerCertificate"] = o.TrustServerCertificate;
            s["MaxRows"] = o.MaxRows;
            s["AllowExec"] = o.AllowExec;
            s["KeepData"] = o.KeepData;
            s["AnyProject"] = o.AnyProject;
            s["SyncReadDb"] = o.SyncReadDb;
            s["DeploySql"] = o.DeploySql;
            s["AllowAiWrite"] = o.AllowAiWrite;
            s["AiMaxRows"] = o.AiMaxRows;
        });
        if (o.SyncReadDb && o.Server.Length > 0 && o.Database.Length > 0) return WriteReadDb(o);
        return null;
    }

    /// <summary>
    /// Ghi server/DB/login vào tools/db/ro.local.json để AI truy vấn đúng DB (file đã nằm trong .gitignore của workspace).
    /// Giữ các trường khác của file; lần đầu ghi đè thì sao lưu bản cũ thành ro.local.json.bak.
    /// </summary>
    private string? WriteReadDb(TestDbOptions o)
    {
        var dir = Path.Combine(settings.Pipeline.WorkspaceRoot, "tools", "db");
        var file = Path.Combine(dir, "ro.local.json");
        try
        {
            if (!Directory.Exists(dir)) return $"Không thấy thư mục {dir} — workspace không có tools/db.";
            System.Text.Json.Nodes.JsonObject json = [];
            if (File.Exists(file))
            {
                json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) as System.Text.Json.Nodes.JsonObject ?? [];
                var same = (string?)json["server"] == o.Server && (string?)json["database"] == o.Database
                           && (string?)json["user"] == o.User && (string?)json["password"] == o.Password;
                if (same) return null;
                var bak = file + ".bak";
                if (!File.Exists(bak)) File.Copy(file, bak);
            }
            json["server"] = o.Server;
            json["database"] = o.Database;
            json["user"] = o.User;
            json["password"] = o.Password;
            File.WriteAllText(file, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            log.LogInformation("Đã cập nhật {File}: {Db} trên {Server}", file, o.Database, o.Server);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"Đã lưu cấu hình nhưng không cập nhật được {file}: {ex.Message}";
        }
    }

    private static string ConnectionString(TestDbOptions o)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = o.Server,
            InitialCatalog = o.Database,
            TrustServerCertificate = o.TrustServerCertificate,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = "SuperAutomation-TestData",
        };
        if (o.User.Length > 0) { b.UserID = o.User; b.Password = o.Password; }
        else b.IntegratedSecurity = true;
        return b.ConnectionString;
    }

    /// <summary>Kiểm tra kết nối: DB thực tế, login, quyền INSERT/DELETE. o = cấu hình đang nhập trên form (chưa lưu) hoặc null = đã lưu.</summary>
    public async Task<(bool Ok, string Info)> Check(TestDbOptions? o, CancellationToken ct)
    {
        o ??= Options;
        if (o.Server.Length == 0 || o.Database.Length == 0) return (false, "Chưa nhập server hoặc tên DB.");
        try
        {
            await using var cn = new SqlConnection(ConnectionString(o));
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT DB_NAME(), SUSER_SNAME(), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'), HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'), @@VERSION", cn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var db = r.GetString(0);
            var login = r.GetString(1);
            var canInsert = !r.IsDBNull(2) && r.GetInt32(2) == 1;
            var canDelete = !r.IsDBNull(3) && r.GetInt32(3) == 1;
            var version = r.GetString(4).Split('\n')[0].Trim();
            var ok = string.Equals(db, o.Database, StringComparison.OrdinalIgnoreCase) && canInsert && canDelete;
            return (ok, $"DB {db}, login {login}, quyền INSERT: {(canInsert ? "có" : "KHÔNG")}, DELETE: {(canDelete ? "có" : "KHÔNG")} — {version}");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    // ---------- Kiểm tra script ----------

    private static readonly Regex Forbidden = new(
        @"\b(DROP|TRUNCATE|ALTER|GRANT|REVOKE|DENY|BACKUP|RESTORE|SHUTDOWN|DBCC|MERGE|OPENROWSET|OPENDATASOURCE|OPENQUERY|OPENXML|BULK|RECONFIGURE|KILL|USE|xp_\w+|sp_executesql|sp_configure|sp_addlogin|sp_addrolemember|sp_\w*user\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Các từ mở đầu câu lệnh — dùng để cắt script thành từng câu và kiểm UPDATE/DELETE có WHERE
    private static readonly Regex StatementStart = new(
        @"\b(INSERT|UPDATE|DELETE|DECLARE|IF|WHILE|BEGIN|END|EXEC|EXECUTE|PRINT|RETURN|CREATE|SELECT|WITH)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Bỏ comment và nội dung chuỗi để quét từ khoá không bị đánh lừa (tên chứa "drop", chuỗi '…DELETE…').</summary>
    public static string Strip(string sql)
    {
        var s = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"--[^\n]*", " ");
        s = Regex.Replace(s, @"N?'(?:[^']|'')*'", "''");
        return s;
    }

    public static List<string> Batches(string sql) =>
        Regex.Split(sql, @"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(b => b.Trim().Length > 0).ToList();

    /// <summary>Lỗi đầu tiên tìm thấy, null nếu script được phép chạy. cleanup=false: seed (chỉ thêm); true: dọn (xoá/sửa có WHERE).</summary>
    public string? Validate(string sql, bool cleanup, string marker)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "Script trống.";
        if (sql.Length > 500_000) return "Script quá dài (>500 KB).";
        if (!sql.Contains(marker, StringComparison.OrdinalIgnoreCase)) return $"Script không có dấu {marker} — mọi dữ liệu test phải được đánh dấu để dọn được.";
        var s = Strip(sql);
        if (Forbidden.Match(s) is { Success: true } f) return $"Có lệnh bị cấm: {f.Value}.";
        if (Regex.IsMatch(s, @"\bCREATE\s+(?!TABLE\s+#)", RegexOptions.IgnoreCase)) return "Chỉ được CREATE TABLE #bảng_tạm.";
        if (Regex.IsMatch(s, @"\bEXEC(UTE)?\s*\(", RegexOptions.IgnoreCase)) return "Không được chạy SQL động EXEC(…).";
        if (!Options.AllowExec && Regex.IsMatch(s, @"\bEXEC(UTE)?\b", RegexOptions.IgnoreCase)) return "Không được gọi EXEC (chưa bật \"Cho gọi stored procedure\").";
        if (Regex.IsMatch(s, @"\bINTO\s+(?!#)\w", RegexOptions.IgnoreCase) && Regex.IsMatch(s, @"\bSELECT\b[^;]*\bINTO\b", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            return "Không được SELECT … INTO bảng thật (chỉ #bảng_tạm).";

        foreach (var stmt in Statements(s))
        {
            var head = stmt.TrimStart();
            var kw = Regex.Match(head, @"^\w+").Value.ToUpperInvariant();
            if (!cleanup && kw is "UPDATE" or "DELETE")
                return $"Script tạo dữ liệu chỉ được INSERT — có {kw}: \"{Short(head)}\". Cần sửa dữ liệu có sẵn thì tạo bản ghi mới thay vì sửa bản ghi thật.";
            if (cleanup && kw is "UPDATE" or "DELETE" && !Regex.IsMatch(stmt, @"\bWHERE\b", RegexOptions.IgnoreCase))
                return $"{kw} không có WHERE: \"{Short(head)}\".";
        }
        return null;
    }

    /// <summary>
    /// Cắt câu lệnh theo ";" và từ mở đầu câu (kể cả SELECT/WITH) — để WHERE của câu sau không bị tính cho DELETE/UPDATE đứng trước.
    /// Cắt mạnh tay nên đôi khi từ chối thừa (UPDATE … SET x = (SELECT …) WHERE …) nhưng không để lọt câu thiếu WHERE.
    /// </summary>
    private static IEnumerable<string> Statements(string stripped)
    {
        foreach (var piece in stripped.Split(';'))
        {
            var starts = StatementStart.Matches(piece).Select(m => m.Index).Append(piece.Length).ToList();
            for (var i = 0; i < starts.Count - 1; i++)
                yield return piece[starts[i]..starts[i + 1]];
        }
    }

    private static string Short(string s) => Regex.Replace(s, @"\s+", " ").Trim() is var t && t.Length > 120 ? t[..120] + "…" : Regex.Replace(s, @"\s+", " ").Trim();

    // ---------- Chạy script ----------

    /// <summary>Chạy script trong một transaction: xác nhận DB, chạy từng batch, đếm dòng; lỗi hoặc vượt giới hạn thì rollback.</summary>
    public async Task<SqlRunResult> Run(string sql, bool cleanup, string marker, CancellationToken ct)
    {
        var o = Options;
        if (!Ready) return new(false, 0, "Chưa bật hoặc chưa cấu hình DB ghi dữ liệu test.");
        if (Validate(sql, cleanup, marker) is { } err) return new(false, 0, "Script bị từ chối: " + err);

        var messages = new StringBuilder();
        try
        {
            await using var cn = new SqlConnection(ConnectionString(o));
            cn.InfoMessage += (_, e) => messages.AppendLine(e.Message);
            await cn.OpenAsync(ct);

            await using (var check = new SqlCommand("SELECT DB_NAME()", cn))
            {
                var db = (string?)await check.ExecuteScalarAsync(ct);
                if (!string.Equals(db, o.Database, StringComparison.OrdinalIgnoreCase))
                    return new(false, 0, $"Kết nối vào DB {db}, không phải DB được phép ghi {o.Database} — dừng, không ghi gì.");
            }

            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            var rows = 0;
            try
            {
                foreach (var batch in Batches(sql))
                {
                    await using var cmd = new SqlCommand(batch, cn, tx) { CommandTimeout = 180 };
                    cmd.StatementCompleted += (_, e) => rows += e.RecordCount;
                    await cmd.ExecuteNonQueryAsync(ct);
                    if (rows > o.MaxRows)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        return new(false, rows, $"Script ghi {rows} dòng, vượt giới hạn {o.MaxRows} — đã rollback, không ghi gì.");
                    }
                }
                await tx.CommitAsync(ct);
                return new(true, rows, $"{(cleanup ? "Đã dọn" : "Đã tạo")} dữ liệu test: {rows} dòng ảnh hưởng.{(messages.Length > 0 ? " " + Short(messages.ToString()) : "")}");
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rex) when (rex is SqlException or InvalidOperationException) { log.LogWarning(rex, "Rollback lỗi"); }
                return new(false, rows, $"Lỗi SQL, đã rollback: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return new(false, 0, $"Không kết nối được DB test: {ex.Message}");
        }
    }

    // ---------- AI ghi DB test (người dùng bật "Cho AI ghi DB test") ----------

    // Chặn lệnh phá cấu trúc/DB/bảo mật; sửa store đi đường deploy (có sao lưu), không qua đây
    private static readonly Regex AiForbidden = new(
        @"\b(DROP|TRUNCATE|BACKUP|RESTORE|SHUTDOWN|DBCC|KILL|RECONFIGURE|GRANT|REVOKE|DENY|USE|OPENROWSET|OPENDATASOURCE|OPENQUERY|BULK|xp_\w+|sp_configure|sp_add\w*|sp_drop\w*|sp_change\w*|sp_rename|sp_executesql)\b" +
        @"|\bALTER\s+(TABLE|DATABASE|LOGIN|USER|ROLE|SERVER|SCHEMA|PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER|INDEX)\b" +
        @"|\bCREATE\s+(?!TABLE\s+#)(OR\s+ALTER\s+)?\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lỗi đầu tiên nếu script AI không được chạy, null nếu được.</summary>
    public static string? ValidateAi(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "Script trống.";
        if (sql.Length > 200_000) return "Script quá dài (>200 KB).";
        var s = Strip(sql);
        if (AiForbidden.Match(s) is { Success: true } f)
            return $"Lệnh bị chặn: \"{f.Value.Trim()}\". Không được đổi cấu trúc DB/bảng, quyền server, hay sửa store (store do tool tự deploy lên DB test).";
        if (Regex.IsMatch(s, @"\bEXEC(UTE)?\s*\(", RegexOptions.IgnoreCase)) return "Không được chạy SQL động EXEC(…).";
        foreach (var stmt in Statements(s))
        {
            var kw = Regex.Match(stmt.TrimStart(), @"^\w+").Value.ToUpperInvariant();
            if (kw is "UPDATE" or "DELETE" && !Regex.IsMatch(stmt, @"\bWHERE\b", RegexOptions.IgnoreCase))
                return $"{kw} không có WHERE: \"{Short(stmt)}\".";
        }
        return null;
    }

    /// <summary>
    /// Chạy script AI soạn trên DB test trong một transaction: xác nhận đúng DB, đếm dòng ghi (vượt giới hạn thì rollback),
    /// trả kết quả SELECT (tối đa 100 dòng mỗi bảng) cùng thông điệp — để AI đọc như chạy sqlcmd.
    /// </summary>
    public async Task<(bool Ok, string Output)> RunAi(string sql, CancellationToken ct)
    {
        var o = Options;
        if (!Ready || !o.AllowAiWrite) return (false, "Chưa bật \"Cho AI ghi DB test\" trong ⚙ Cấu hình › DB dữ liệu test.");
        if (ValidateAi(sql) is { } err) return (false, "Script bị từ chối: " + err);
        var output = new StringBuilder();
        try
        {
            await using var cn = new SqlConnection(ConnectionString(o));
            cn.InfoMessage += (_, e) => output.AppendLine(e.Message);
            await cn.OpenAsync(ct);
            await using (var check = new SqlCommand("SELECT DB_NAME()", cn))
            {
                var db = (string?)await check.ExecuteScalarAsync(ct);
                if (!string.Equals(db, o.Database, StringComparison.OrdinalIgnoreCase))
                    return (false, $"Kết nối vào DB {db}, không phải DB test {o.Database} — dừng, không ghi gì.");
            }
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            var rows = 0;
            try
            {
                foreach (var batch in Batches(sql))
                {
                    await using var cmd = new SqlCommand(batch, cn, tx) { CommandTimeout = 300 };
                    cmd.StatementCompleted += (_, e) => rows += e.RecordCount;
                    await using (var r = await cmd.ExecuteReaderAsync(ct))
                    {
                        do
                        {
                            if (r.FieldCount == 0) continue;
                            output.AppendLine(string.Join("\t", Enumerable.Range(0, r.FieldCount).Select(r.GetName)));
                            var n = 0;
                            while (await r.ReadAsync(ct))
                            {
                                if (++n > 100) continue;
                                output.AppendLine(string.Join("\t", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i))?.Replace("\t", " ").Replace("\r", " ").Replace("\n", " "))));
                            }
                            output.AppendLine(n > 100 ? $"({n} dòng, hiện 100)" : $"({n} dòng)");
                        } while (await r.NextResultAsync(ct));
                    }
                    if (rows > o.AiMaxRows)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        return (false, $"Script ảnh hưởng {rows} dòng, vượt giới hạn {o.AiMaxRows} — đã rollback, không ghi gì.");
                    }
                }
                await tx.CommitAsync(ct);
                return (true, $"OK — đã commit, {rows} dòng bị ảnh hưởng.\n{output}");
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rex) when (rex is SqlException or InvalidOperationException) { log.LogWarning(rex, "Rollback lỗi"); }
                return (false, $"Lỗi SQL, đã rollback (không ghi gì): {ex.Message}\n{output}");
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return (false, $"Không kết nối được DB test: {ex.Message}");
        }
    }

    // ---------- Deploy store/function/view của nhánh AI lên DB test ----------

    private static readonly Regex DefHeader = new(
        @"^[ \t]*(CREATE|ALTER)(\s+OR\s+ALTER)?\s+(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Đọc file SQL theo BOM (repo có file UTF-16 LE lẫn UTF-8).</summary>
    public static string ReadAnyText(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return new UTF8Encoding(false).GetString(b);
    }

    /// <summary>
    /// Tách các định nghĩa store/function/view/trigger (schema dbo) trong một file — mỗi batch (ngăn bằng GO) tối đa một định nghĩa,
    /// lấy từ dòng CREATE/ALTER. Batch không phải định nghĩa (ALTER TABLE, INSERT dữ liệu…) không tự deploy.
    /// </summary>
    public static List<SqlObject> ParseObjects(string text)
    {
        var result = new List<SqlObject>();
        // "$" của .NET (Multiline) chỉ khớp trước "\n" — file CRLF phải cho phép "\r" trước, không thì dòng "GO --END--" không được tách
        foreach (var batch in Regex.Split(text, @"^[ \t]*GO\b[^\r\n]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var m = DefHeader.Match(Regex.Replace(batch, @"/\*.*?\*/", m2 => new string(' ', m2.Length), RegexOptions.Singleline));
            if (!m.Success) continue;
            if (m.Groups[4].Success && !m.Groups[4].Value.Equals("dbo", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = m.Groups[3].Value.ToUpperInvariant() is "PROC" ? "PROCEDURE" : m.Groups[3].Value.ToUpperInvariant();
            result.Add(new SqlObject(m.Groups[5].Value, kind, batch[m.Index..].Trim()));
        }
        return result;
    }

    /// <summary>Định nghĩa với dòng đầu đổi thành "{verb} {kind} [dbo].[name]".</summary>
    private static string WithHeader(string body, string verb) =>
        DefHeader.Replace(body, m => $"{verb} {(m.Groups[3].Value.ToUpperInvariant() is "PROC" ? "PROCEDURE" : m.Groups[3].Value.ToUpperInvariant())} [dbo].[{m.Groups[5].Value}]", 1);

    /// <summary>So sánh định nghĩa bỏ qua CREATE/ALTER, khoảng trắng cuối dòng, kiểu xuống dòng (như tools/db/sp.ps1 -Diff).</summary>
    private static string NormDef(string def)
    {
        var m = DefHeader.Match(def);
        var body = m.Success ? WithHeader(def[m.Index..], "CREATE") : def;
        return string.Join("\n", body.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
    }

    private sealed record SqlBackup(bool Existed, string Kind);

    private static Dictionary<string, SqlBackup> LoadManifest(string dir)
    {
        var f = Path.Combine(dir, "manifest.json");
        if (!File.Exists(f)) return new(StringComparer.OrdinalIgnoreCase);
        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SqlBackup>>(File.ReadAllText(f));
        return new(d ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deploy định nghĩa lên DB test trong một transaction: bỏ qua đối tượng đã giống DB; lần đầu đụng tới một đối tượng thì lưu định nghĩa cũ
    /// vào backupDir (để <see cref="RestoreSql"/> trả lại); store Generate_SP_* (sinh SP động từ bảng cấu hình) được EXEC sau khi deploy.
    /// </summary>
    public async Task<SqlDeployResult> DeploySql(List<SqlObject> objects, string backupDir, CancellationToken ct)
    {
        var o = Options;
        if (!Ready) return new(false, [], [], "Chưa bật hoặc chưa cấu hình DB ghi.");
        Directory.CreateDirectory(backupDir);
        var manifest = LoadManifest(backupDir);
        var deployed = new List<string>();
        var same = new List<string>();
        try
        {
            await using var cn = new SqlConnection(ConnectionString(o));
            await cn.OpenAsync(ct);
            await using (var check = new SqlCommand("SELECT DB_NAME()", cn))
            {
                var db = (string?)await check.ExecuteScalarAsync(ct);
                if (!string.Equals(db, o.Database, StringComparison.OrdinalIgnoreCase))
                    return new(false, [], [], $"Kết nối vào DB {db}, không phải DB được phép ghi {o.Database} — dừng, không deploy.");
            }
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            var current = "";
            try
            {
                foreach (var obj in objects)
                {
                    current = obj.Name;
                    await using var get = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", cn, tx);
                    get.Parameters.AddWithValue("@n", "dbo." + obj.Name);
                    var cur = await get.ExecuteScalarAsync(ct) as string;
                    if (cur is not null && NormDef(cur) == NormDef(obj.Body)) { same.Add(obj.Name); continue; }
                    if (!manifest.ContainsKey(obj.Name))
                    {
                        if (cur is not null) await File.WriteAllTextAsync(Path.Combine(backupDir, obj.Name + ".sql"), cur, new UTF8Encoding(false), ct);
                        manifest[obj.Name] = new SqlBackup(cur is not null, obj.Kind);
                    }
                    await using (var cmd = new SqlCommand(WithHeader(obj.Body, cur is null ? "CREATE" : "ALTER"), cn, tx) { CommandTimeout = 180 })
                        await cmd.ExecuteNonQueryAsync(ct);
                    if (obj.Kind == "PROCEDURE" && obj.Name.StartsWith("Generate_SP_", StringComparison.OrdinalIgnoreCase))
                        await using (var gen = new SqlCommand($"EXEC [dbo].[{obj.Name}]", cn, tx) { CommandTimeout = 180 })
                            await gen.ExecuteNonQueryAsync(ct);
                    deployed.Add(obj.Name);
                }
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rex) when (rex is SqlException or InvalidOperationException) { log.LogWarning(rex, "Rollback lỗi"); }
                return new(false, [], [], $"Deploy {current} lỗi, đã rollback (DB giữ nguyên bản cũ): {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return new(false, [], [], $"Không kết nối được DB test: {ex.Message}");
        }
        await File.WriteAllTextAsync(Path.Combine(backupDir, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(manifest), CancellationToken.None);
        return new(true, deployed, same, "");
    }

    /// <summary>Trả các đối tượng đã deploy về bản cũ (đối tượng mới thì DROP) trong một transaction; xong thì đổi tên manifest.</summary>
    public async Task<SqlDeployResult> RestoreSql(string backupDir, CancellationToken ct)
    {
        var manifest = LoadManifest(backupDir);
        if (manifest.Count == 0) return new(true, [], [], "Không có store nào cần khôi phục.");
        var o = Options;
        if (!Ready) return new(false, [], [], "Chưa bật hoặc chưa cấu hình DB ghi.");
        var done = new List<string>();
        try
        {
            await using var cn = new SqlConnection(ConnectionString(o));
            await cn.OpenAsync(ct);
            await using (var check = new SqlCommand("SELECT DB_NAME()", cn))
            {
                var db = (string?)await check.ExecuteScalarAsync(ct);
                if (!string.Equals(db, o.Database, StringComparison.OrdinalIgnoreCase))
                    return new(false, [], [], $"Kết nối vào DB {db}, không phải DB {o.Database} — dừng, không khôi phục.");
            }
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            try
            {
                foreach (var (name, b) in manifest)
                {
                    await using var exists = new SqlCommand("SELECT OBJECT_ID(@n)", cn, tx);
                    exists.Parameters.AddWithValue("@n", "dbo." + name);
                    var present = await exists.ExecuteScalarAsync(ct) is int;
                    string sql;
                    if (b.Existed)
                    {
                        var file = Path.Combine(backupDir, name + ".sql");
                        if (!File.Exists(file)) throw new InvalidOperationException($"Thiếu bản sao lưu {file}.");
                        sql = WithHeader(await File.ReadAllTextAsync(file, ct), present ? "ALTER" : "CREATE");
                    }
                    else if (present) sql = $"DROP {b.Kind} [dbo].[{name}]";
                    else continue;
                    await using (var cmd = new SqlCommand(sql, cn, tx) { CommandTimeout = 180 })
                        await cmd.ExecuteNonQueryAsync(ct);
                    if (b.Existed && b.Kind == "PROCEDURE" && name.StartsWith("Generate_SP_", StringComparison.OrdinalIgnoreCase))
                        await using (var gen = new SqlCommand($"EXEC [dbo].[{name}]", cn, tx) { CommandTimeout = 180 })
                            await gen.ExecuteNonQueryAsync(ct);
                    done.Add(name);
                }
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rex) when (rex is SqlException or InvalidOperationException) { log.LogWarning(rex, "Rollback lỗi"); }
                return new(false, [], [], $"Khôi phục lỗi, đã rollback: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return new(false, [], [], $"Không kết nối được DB test: {ex.Message}");
        }
        var mf = Path.Combine(backupDir, "manifest.json");
        File.Move(mf, Path.Combine(backupDir, $"manifest.restored-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        foreach (var f in Directory.GetFiles(backupDir, "*.sql")) File.Move(f, f + ".restored", overwrite: true);
        return new(true, done, [], "");
    }

    [GeneratedRegex(@"^[\w.\-]+$")] private static partial Regex SafeName();
}
