using System;

namespace DougongDesktop.Ui.Core;

/// <summary>
/// 回合计时：扣掉暂停与失败时段。
/// </summary>
/// <remarks>
/// <b>语义对齐 Arc 的 <c>AgentStatusRunClock</c></b>（那个类实测是
/// <c>internal</c>，外部用不了，所以按它的文档语义自己实现）。
/// <para>
/// <b>哪些段要扣</b>：
/// <list type="bullet">
/// <item><c>Paused</c> / <c>Failed</c>：不在引擎掌控内，算进去会让人误判引擎慢</item>
/// <item><c>NeedsInput</c>：<b>不扣</b>——Arc 的文档明确写 "Waiting for input counts"，
/// 等用户确认也算这一段在工作</item>
/// </list>
/// </para>
/// <para>
/// <b>纯记账，不碰时钟</b>：所有时刻由调用方传入。这样测试可以注入固定时间，
/// 不用 sleep。
/// </para>
/// </remarks>
public sealed class RunClock
{
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;

    /// <summary>当前是否正处在一个被排除的时段里。</summary>
    private DateTimeOffset? _excludedSince;

    /// <summary>累计被排除的时长。</summary>
    private TimeSpan _excluded;

    public DateTimeOffset? StartedAt => _startedAt;

    public DateTimeOffset? EndedAt => _endedAt;

    /// <summary>开一段新的计时。上一段的排除记录随之作废。</summary>
    public void SetStartedAt(DateTimeOffset now)
    {
        _startedAt = now;
        _endedAt = null;
        _excludedSince = null;
        _excluded = TimeSpan.Zero;
    }

    /// <summary>记录一个状态。负责开关"排除段"并标记结束。</summary>
    public void Observe(RunOutcome outcome, DateTimeOffset now)
    {
        // **Failed 是终态，不是"一段失败期"**——这是实现时踩到的坑：
        // 一开始把 Failed 同时当成"排除段起点"和"结束点"，
        // 于是 Elapsed 算的是"到失败那一刻"而不是"到当前"，20 秒的工作
        // 被判成 5 秒。检查 run-clock-excludes-failure 抓到了这个错。
        //
        // 现在只有一个"排除段"：Paused。
        // 而 Failed / Done / Stopped 都表示回合终止，记终点并结掉排除段。
        if (outcome == RunOutcome.Paused)
        {
            _excludedSince ??= now;
            return;
        }

        if (outcome is RunOutcome.Done or RunOutcome.Stopped or RunOutcome.Failed)
        {
            if (_excludedSince is { } since) _excluded += now - since;
            _excludedSince = null;
            _endedAt = now;
        }
        else if (_excludedSince is { } open)
        {
            // 恢复工作：结掉暂停段
            _excluded += now - open;
            _excludedSince = null;
        }
    }

    /// <summary>当前活跃时长。未开始时返回 0。</summary>
    public TimeSpan Elapsed(DateTimeOffset now)
    {
        if (_startedAt is not { } start) return TimeSpan.Zero;

        var end = _endedAt ?? now;
        var total = end - start - _excluded;

        // 还在暂停里：把当前这段尚未结算的也算上
        if (_excludedSince is { } open)
            total -= end - open;

        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }
}

/// <summary>一次运行的状态，用于计时时判断哪段该排除。</summary>
public enum RunOutcome
{
    /// <summary>空闲，未开始。</summary>
    Idle,

    /// <summary>工作中。</summary>
    Working,

    /// <summary>等待用户输入。<b>算活跃时间</b>——Arc 的文档明确写了这一点。</summary>
    NeedsInput,

    /// <summary>已暂停。<b>不算活跃时间</b>。</summary>
    Paused,

    /// <summary>已失败。<b>不算活跃时间</b>——重试耗时不该算进引擎耗时。</summary>
    Failed,

    /// <summary>已完成。</summary>
    Done,

    /// <summary>已停止。</summary>
    Stopped,
}
