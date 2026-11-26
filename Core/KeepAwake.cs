using System.Runtime.InteropServices;

namespace SuperAutomation.Core;

/// <summary>
/// Giữ Windows không tự ngủ khi còn job đang chạy / đang xếp hàng (chạy qua đêm), hết việc thì trả lại bình thường.
/// Không chặn được việc gập máy, bấm Sleep tay hay chính sách công ty ép ngủ.
/// </summary>
public sealed class KeepAwake(JobStore store, ILogger<KeepAwake> log) : BackgroundService
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    public bool Active { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        // Cờ gắn với luồng gọi: dùng một luồng riêng sống suốt vòng đời tool
        new Thread(() => Loop(stop)) { IsBackground = true, Name = "KeepAwake" }.Start();
        return Task.CompletedTask;
    }

    private void Loop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var busy = store.All().Any(j => j.IsRunning || j.Status == JobStatus.Queued || j.RetryPending);
            if (busy != Active)
            {
                SetThreadExecutionState(busy ? EsContinuous | EsSystemRequired : EsContinuous);
                Active = busy;
                log.LogInformation(busy ? "Có job đang chạy — giữ máy không tự ngủ" : "Hết job — cho máy ngủ bình thường");
            }
            stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
        }
        SetThreadExecutionState(EsContinuous);
    }
}
