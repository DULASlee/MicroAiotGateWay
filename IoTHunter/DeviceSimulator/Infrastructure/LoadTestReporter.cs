namespace DeviceSimulator.Infrastructure;

internal static class LoadTestReporter
{
    public static void PrintReport(SimulatorSnapshot s, SimulatorOptions o)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("         IoTHunter 全链路压测报告");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  协议: {o.Protocol.ToUpper()}  设备数: {o.DeviceCount}");
        Console.WriteLine($"  IntervalMs: {o.IntervalMs}  并发线程: {o.Concurrency}");
        Console.WriteLine($"  持续: {o.DurationSeconds}s  关键事件比例: {o.CriticalEventRatio}");
        Console.WriteLine();
        Console.WriteLine("【接入层指标】");
        Console.WriteLine($"  总发送: {s.TotalSent:N0}  失败: {s.TotalFailed:N0}");
        Console.WriteLine($"  成功率: {s.SuccessRate:F2}%  实际 QPS: {s.Qps:F1}");
        Console.WriteLine($"  P99 延迟: {s.LatencyP99Ms:F1}ms  Max 延迟: {s.LatencyMaxMs:F1}ms");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }
}
