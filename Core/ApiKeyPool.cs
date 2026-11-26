namespace SuperAutomation.Core;

/// <summary>Một API key Anthropic trong danh sách xoay tua.</summary>
public sealed class ApiKeyEntry
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Nhiều API key dùng xoay tua:
/// - "failover": luôn dùng key đầu tiên còn dùng được; key lỗi (hết tiền, sai key, chạm giới hạn) thì tạm nghỉ và chuyển key sau.
/// - "roundrobin": mỗi phiên AI lấy key kế tiếp trong các key còn dùng được (cũng tạm nghỉ key lỗi).
/// Trạng thái tạm nghỉ chỉ nằm trong bộ nhớ — khởi động lại tool thì thử lại mọi key.
/// </summary>
public sealed class ApiKeyPool
{
    private readonly object _lock = new();
    private List<ApiKeyEntry> _keys = [];
    private readonly Dictionary<string, (DateTime Until, string Reason)> _cool = [];
    private int _next;

    public string Strategy { get; private set; } = "failover";

    public List<ApiKeyEntry> Keys { get { lock (_lock) return [.. _keys]; } }

    public void Set(List<ApiKeyEntry> keys, string strategy)
    {
        lock (_lock)
        {
            _keys = keys;
            Strategy = strategy == "roundrobin" ? "roundrobin" : "failover";
            foreach (var id in _cool.Keys.Where(id => keys.All(k => k.Id != id)).ToList()) _cool.Remove(id);
            if (_next >= keys.Count) _next = 0;
        }
    }

    /// <summary>
    /// Chọn key cho một phiên. exclude = các key đã thử trong lần chạy này (khi chuyển key vì lỗi) — khi đó chỉ lấy key đang sẵn sàng.
    /// Lần chọn đầu mà mọi key đều đang tạm nghỉ thì vẫn thử key sắp hết nghỉ nhất, thay vì báo lỗi ngay.
    /// </summary>
    public ApiKeyEntry? Pick(IReadOnlySet<string>? exclude = null)
    {
        lock (_lock)
        {
            var enabled = _keys.Where(k => k.Enabled && k.Key.Length > 0 && (exclude is null || !exclude.Contains(k.Id))).ToList();
            if (enabled.Count == 0) return null;
            var now = DateTime.Now;
            var ready = enabled.Where(k => !_cool.TryGetValue(k.Id, out var c) || c.Until <= now).ToList();
            if (ready.Count == 0) return exclude is { Count: > 0 } ? null : enabled.MinBy(k => _cool[k.Id].Until);
            if (Strategy != "roundrobin") return ready[0];
            var pick = ready[_next % ready.Count];
            _next = (_next + 1) % ready.Count;
            return pick;
        }
    }

    public void MarkBad(string id, string reason, TimeSpan duration)
    {
        lock (_lock) _cool[id] = (DateTime.Now + duration, reason);
    }

    public void MarkOk(string id)
    {
        lock (_lock) _cool.Remove(id);
    }

    public (DateTime Until, string Reason)? Cooldown(string id)
    {
        lock (_lock) return _cool.TryGetValue(id, out var c) && c.Until > DateTime.Now ? c : null;
    }
}
