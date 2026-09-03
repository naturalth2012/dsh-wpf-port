using System;
using System.Windows.Threading;

namespace Dsh.Wpf;

/// <summary>
/// 流式节流器（自适应防抖，借鉴 MarkdView / 官方 Web 客户端思路）：
/// 把高频事件（如每个 assistant chunk 触发一次 Transcript 同步）合并为低频 UI 更新，
/// 避免 UI 线程被逐 chunk 全量重建阻塞。
///
/// 行为：
/// - 首次到达（空闲 > <see cref="MaxInterval"/>）→ <b>立即执行</b>（leading edge，快速反馈）。
/// - 在 250ms 批窗口内的连续到达 → <b>合并为单次执行</b>（trailing edge）。
/// - 超过批窗口后仍有新到达 → <b>强制执行</b>（防止持续 burst 累积使 UI 永远不更新）。
/// - <see cref="Flush"/> 强制立即执行（流式暂停/结束）。
/// </summary>
public sealed class UiThrottle
{
    private readonly Action _action;
    private readonly DispatcherTimer _timer;
    private readonly double _maxInterval;
    private DateTime _lastRun = DateTime.MinValue;
    private DateTime _pendingSince = DateTime.MinValue;

    /// <param name="action">需要节流的动作（UI 线程调用）。</param>
    /// <param name="maxInterval">最大间隔 / 批窗口 ms。</param>
    public UiThrottle(Action action, double maxInterval = 250)
    {
        _action = action;
        _maxInterval = maxInterval;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(maxInterval),
            IsEnabled = false,
        };
        _timer.Tick += (_, _) => RunNow();
    }

    /// <summary>报告一次新事件到达（合并调度）。UI 线程调用。</summary>
    public void Schedule()
    {
        var now = DateTime.UtcNow;

        // Leading edge：距上次执行已超过最大间隔 → 立即执行，保证首个 chunk 快速反馈。
        if (_lastRun == DateTime.MinValue || (now - _lastRun).TotalMilliseconds > _maxInterval)
        {
            RunNow();
            return;
        }

        // 若已有 pending 批次，且该批次等待已超过批窗口 → 强制立即执行，
        // 防止持续 burst 时 timer 被反复重置、永不触发（导致 UI 永远不更新）。
        if (_pendingSince != DateTime.MinValue && (now - _pendingSince).TotalMilliseconds > _maxInterval)
        {
            RunNow();
            return;
        }

        // 开启或维持当前 pending 批次：单次启动 trailing timer（不重置，避免高频 burst 时永不触发）。
        if (_pendingSince == DateTime.MinValue)
        {
            _pendingSince = now;
            _timer.Interval = TimeSpan.FromMilliseconds(_maxInterval);
            _timer.Stop();
            _timer.Start();
        }
    }

    /// <summary>强制立即执行（流式暂停/结束收尾）。</summary>
    public void Flush()
    {
        RunNow();
    }

    private void RunNow()
    {
        _timer.Stop();
        _pendingSince = DateTime.MinValue;
        _lastRun = DateTime.UtcNow;
        _action();
    }
}