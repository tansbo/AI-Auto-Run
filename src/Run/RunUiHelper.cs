using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace CombatSolver.Run;

/// <summary>
/// 跑局 AI 的 UI 交互助手：移植游戏 AutoSlay 的 UiHelper/WaitHelper 配方。
/// 点击走 <see cref="NClickableControl.ForceClick"/>，直接发 Released 信号，绕过
/// hover/focus/pause 检查，保证自动化环境下可用。所有等待都在主线程轮询，不自旋。
/// </summary>
internal static class RunUiHelper
{
    private const int DefaultPollIntervalMilliseconds = 50;

    /// <summary>点击一个可点击控件并等待其副作用落地。</summary>
    public static async Task ClickAsync(NClickableControl button, int delayMs = 100)
    {
        button.ForceClick();
        await Task.Delay(delayMs);
    }

    /// <summary>递归查找 start 下的所有 T 节点。</summary>
    public static List<T> FindAll<T>(Node start) where T : Node
    {
        List<T> found = [];
        if (GodotObject.IsInstanceValid(start))
            FindAllRecursive(start, found);
        return found;
    }

    /// <summary>递归查找 start 下第一个 T 节点，找不到返回 null。</summary>
    public static T? FindFirst<T>(Node start) where T : Node
    {
        if (!GodotObject.IsInstanceValid(start))
            return null;
        if (start is T result)
            return result;
        foreach (Node child in start.GetChildren())
        {
            T? val = FindFirst<T>(child);
            if (val != null)
                return val;
        }
        return null;
    }

    /// <summary>等待 condition 变为 true，超时抛 <see cref="RunAutoTimeoutException"/>。</summary>
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        string? timeoutMessage = null)
    {
        TimeSpan actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(actualTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (!condition())
                await Task.Delay(DefaultPollIntervalMilliseconds, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new RunAutoTimeoutException(timeoutMessage ?? $"条件在 {actualTimeout.TotalSeconds:0.#}s 内未满足。");
        }
    }

    /// <summary>等待 task 完成；若其内部又打开了覆盖层，可结合 <see cref="RunAutoController"/> 排空。</summary>
    public static async Task WaitForTaskAsync(
        Task task,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        string? timeoutMessage = null)
    {
        TimeSpan actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(actualTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (!task.IsCompleted)
                await Task.WhenAny(task, Task.Delay(DefaultPollIntervalMilliseconds, linkedCts.Token));
            await task;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new RunAutoTimeoutException(timeoutMessage ?? $"任务在 {actualTimeout.TotalSeconds:0.#}s 内未完成。");
        }
    }

    private static void FindAllRecursive<T>(Node node, List<T> found) where T : Node
    {
        if (!GodotObject.IsInstanceValid(node))
            return;
        if (node is T item)
            found.Add(item);
        foreach (Node child in node.GetChildren())
            FindAllRecursive(child, found);
    }
}

/// <summary>跑局 AI 等待超时。驱动任务捕获后记录日志并安全返回，不向上抛。</summary>
internal sealed class RunAutoTimeoutException : TimeoutException
{
    public RunAutoTimeoutException(string message)
        : base(message)
    {
    }
}
