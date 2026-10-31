using Dougong.Agent;
using DougongDesktop.Framework;
using DougongDesktop.Ipc;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// P5 的检查：工具两段耗时、推理流隔离、计划全量替换。
/// </summary>
/// <remarks>
/// <b>为什么这些要检查而不是靠看</b>：§6.6 的耗时映射有四行规则，
/// 其中"都是 null 时不要显示 0"这条错了界面上看不出来——一个假的"瞬间完成"
/// 比不显示更糟。这类缺陷只能靠断言抓。
/// <para>
/// 推理流的隔离也是同理：它若并进正文，界面上表现为"模型答完了"，
/// 而那正是把推理当结论的误导。
/// </para>
/// </remarks>
internal static class ToolChecks
{
    /// <summary>仓库根：从检查程序所在目录往上找 slnx。</summary>
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DougongDesktop.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("tool-durations-passed-through", slug => DurationsPassedThrough(breakMode == slug));
        failures += Case("tool-null-duration-stays-null", slug => NullDurationStaysNull(breakMode == slug));
        failures += Case("reasoning-separate-from-text", slug => ReasoningSeparate(breakMode == slug));
        failures += Case("plan-replaces-wholesale", slug => PlanReplacesWholesale(breakMode == slug));
        failures += Case("plan-tool-comes-from-sdk", slug => PlanToolFromSdk(breakMode == slug));
        failures += Case("caps-table-beats-guess", slug => CapsBeatsGuess(breakMode == slug));
        failures += Case("max-output-never-eats-context", slug => MaxOutputNeverEatsContext(breakMode == slug));
        failures += Case("budget-check-comes-from-sdk", slug => BudgetCheckFromSdk(breakMode == slug));
        failures += Case("caps-no-pathological-out-ge-ctx", slug => NoPathologicalData(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 两段耗时必须原样透传到 IPC 条目，不能合并成一个数。
    /// </summary>
    /// <remarks>
    /// <b>合并会丢掉"慢在哪一段"</b>：审批等待取决于人多快点确认，
    /// 执行耗时取决于工具本身多慢，优化方向相反。
    /// </remarks>
    private static string? DurationsPassedThrough(bool inject)
    {
        var existing = new ToolCallItem
        {
            // 测试用固定时刻：依赖"现在"会让断言随执行时间变化。
            At = DateTimeOffset.UnixEpoch,
            Id = "tool-inv-1",
            InvocationId = "inv-1",
            ToolName = "read_file",
            Status = ToolCallStatus.Running,
        };

        var result = new ToolDispatchResult(
            "inv-1", "read_file", ToolCallOutcome.Succeeded,
            Result: null,
            Diagnostic: null,
            Permission: null,
            ApprovalElapsed: TimeSpan.FromMilliseconds(1200),
            ExecutionElapsed: TimeSpan.FromMilliseconds(45));

        var updated = EventProjection.ApplyToolResult(existing, result);

        var expectedApproval = inject ? (TimeSpan?)null : TimeSpan.FromMilliseconds(1200);
        var expectedExecution = inject ? (TimeSpan?)null : TimeSpan.FromMilliseconds(45);

        if (updated.ApprovalElapsed != expectedApproval)
            return $"ApprovalElapsed 应透传 {expectedApproval}，实际 {updated.ApprovalElapsed}";

        return updated.ExecutionElapsed == expectedExecution
            ? null
            : $"ExecutionElapsed 应透传 {expectedExecution}，实际 {updated.ExecutionElapsed}";
    }

    /// <summary>
    /// 没有发生的耗时必须是 null，不能变成 0。
    /// </summary>
    /// <remarks>
    /// <b>0 与 null 是两件事</b>：0 表示"审批瞬间通过"，null 表示"根本没有这一阶段"。
    /// 用 0 冒充会让统计里混进一批假的"审批很快"，而且界面上会显示一个
    /// 假的"瞬间完成"。
    /// </remarks>
    private static string? NullDurationStaysNull(bool inject)
    {
        var existing = new ToolCallItem
        {
            At = DateTimeOffset.UnixEpoch,
            Id = "tool-inv-2",
            InvocationId = "inv-2",
            ToolName = "write_file",
            Status = ToolCallStatus.Running,
        };

        // 被拒绝：只有审批等待，没有执行
        var rejected = new ToolDispatchResult(
            "inv-2", "write_file", ToolCallOutcome.RejectedNotApproved,
            Result: null, Diagnostic: "用户拒绝", Permission: null,
            ApprovalElapsed: TimeSpan.FromMilliseconds(3400),
            ExecutionElapsed: null);

        var updated = EventProjection.ApplyToolResult(existing, rejected);

        if (inject)
        {
            // 反证：把 null 变成 0
            updated = updated with { ExecutionElapsed = TimeSpan.Zero };
        }

        if (updated.ExecutionElapsed is not null)
            return $"被拒绝的调用不该有执行耗时（显示 {updated.ExecutionElapsed} 会造出假的\"瞬间完成\"）";

        return updated.ApprovalElapsed == TimeSpan.FromMilliseconds(3400)
            ? null
            : $"审批耗时应为 3400ms，实际 {updated.ApprovalElapsed}";
    }

    /// <summary>
    /// 推理增量必须映射成独立投影，不能并进文本增量。
    /// </summary>
    /// <remarks>
    /// <b>并进去的后果是误导</b>：界面会把推理显示成正文，用户以为模型已经答完；
    /// 而推理里常带未定稿的自我纠错。
    /// </remarks>
    private static string? ReasoningSeparate(bool inject)
    {
        var reasoning = EventProjection.Map(new AgentEvent.ReasoningDelta("等等，这里算错了"));
        var text = EventProjection.Map(new AgentEvent.TextDelta("答案是 42"));

        if (inject)
        {
            // 反证：把推理当成正文处理
            reasoning = new EventProjection.Projection.TextDelta("等等，这里算错了");
        }

        if (reasoning is not EventProjection.Projection.ReasoningDelta { Text: "等等，这里算错了" })
            return $"ReasoningDelta 应映射为独立投影，实际 {reasoning.GetType().Name}";

        return text is EventProjection.Projection.TextDelta
            ? null
            : "TextDelta 应仍然映射为文本增量";
    }

    /// <summary>
    /// 每次计划更新都是全量替换（新条目），不是就地合并。
    /// </summary>
    /// <remarks>
    /// <b>就地合并会让"删掉的步骤"残留</b>：模型重新规划时可能去掉某一步，
    /// 合并会让它永远留在界面上。
    /// </remarks>
    private static string? PlanReplacesWholesale(bool inject)
    {
        var first = EventProjection.Map(new AgentEvent.PlanUpdated(
            [new PlanEntry("读文件", "high", "completed")]));

        var second = EventProjection.Map(new AgentEvent.PlanUpdated(
        [
            new PlanEntry("读文件", "high", "completed"),
            new PlanEntry("写文件", "high", "in_progress"),
        ]));

        if (first is not EventProjection.Projection.Added { Item: PlanItem a })
            return "计划更新应产出新的 PlanItem";

        if (second is not EventProjection.Projection.Added { Item: PlanItem b })
            return "第二次计划更新也应产出新的 PlanItem";

        if (inject) return "反证注入：第二次计划不产新条目";

        // 全量替换：两条不同的条目（新 Id），内容各自完整
        if (a.Id == b.Id) return "每次计划更新都该是新条目（新 Id），不是就地合并";
        if (b.Steps.Count != 2) return $"第二次计划应有 2 步，实际 {b.Steps.Count}";

        return a.Steps.Count == 1 ? null : $"第一次计划应有 1 步，实际 {a.Steps.Count}";
    }

    /// <summary>
    /// 计划工具必须是 SDK 的 <see cref="TodoWriteTool"/>，不能手写一份。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是"重复实现看起来更好"</b>：手写一份 schema 当时更简单，
    /// 但 SDK 改 schema 时我们这份不会跟着变——模型按新 schema 传参被拒，
    /// 而错误信息看起来像模型的问题。
    /// <para>
    /// 更严重的是：手写版只是"一个名字叫 todo_write 的普通工具"，
    /// 运行时不会把它识别成计划来源，<c>PlanUpdated</c> 永远不会发出，
    /// 界面上一条计划都看不到。
    /// </para>
    /// </remarks>
    private static string? PlanToolFromSdk(bool inject)
    {
        var tool = PlanTools.Create();

        if (inject) return "反证注入：计划工具不是 SDK 实现";

        if (tool is not TodoWriteTool)
            return $"计划工具应为 SDK 的 TodoWriteTool，实际 {tool.GetType().Name}";

        return tool.Descriptor.Id == "todo_write"
            ? null
            : $"工具 Id 应为 todo_write，实际 {tool.Descriptor.Id}";
    }

    /// <summary>
    /// 能力表必须优先于（已删除的）按名推断。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是一个具体错误</b>：手写表曾对 <c>deepseek</c> 一律给 128k，
    /// 而能力表里 <c>deepseek-flash</c> 是 1M。差 8 倍意味着压缩在 128k 就触发，
    /// 白摘掉四分之三历史，而用户完全看不出原因。
    /// <para>
    /// 所以断言两件事：能查到就用查到的值（不是 128k），查不到才走回退。
    /// </para>
    /// </remarks>
    private static string? CapsBeatsGuess(bool inject)
    {
        var caps = EngineOptions.LookupCapabilities("deepseek-flash");
        if (caps is null) return "能力表应能查到 deepseek-flash（否则这条检查本身失效了）";

        var actual = EngineOptions.ReadContextWindow("deepseek-flash");

        if (inject) return "反证注入：能力表查到了却不用";

        // 128000 是已删除的那张表会给的值，出现它说明回退了
        if (actual == 128_000)
            return $"读到了旧推断表的 128k，而能力表是 {caps.ContextWindow}";

        if (actual != caps.ContextWindow)
            return $"应取能力表的 {caps.ContextWindow}，实际 {actual}";

        // 查不到的模型必须走回退，且回退值不是 0（0 会让压缩永不启用）
        var unknown = EngineOptions.ReadContextWindow("definitely-not-a-real-model-xyz");
        return unknown > 0
            ? null
            : "查不到的模型应走回退估算（>0），返回 0 会让压缩永不启用";
    }

    /// <summary>
    /// 模型输出容量不能进压缩预算。
    /// </summary>
    /// <remarks>
    /// <b>这条守的是一个升级陷阱</b>：能力表的 <c>MaxOutputTokens</c> 是
    /// "模型最多能输出多少"（实测占窗口 32%–38%），我们早先拿它当压缩预算里的
    /// 请求预留。后果是预算被吃掉三分之一，压缩过早触发——200k 窗口按 64k 预留
    /// 只剩 135k 可用输入，白扔三分之二的历史。
    /// <para>
    /// SDK 0.1.10 把 <c>ContextLimits</c> 的这个参数正式改名成
    /// <c>RequestOutputReserve</c>，就是在强调这个区分。改名会强制调用方改代码，
    /// 但改完仍可能"用错值"——所以这条断言数值，不靠类型检查。
    /// </para>
    /// </remarks>
    private static string? MaxOutputNeverEatsContext(bool inject)
    {
        var reserve = EngineOptions.ReadRequestReserve();

        if (inject) return "反证注入：请求预留跟着模型容量走";

        if (reserve != EngineOptions.RequestOutputReserveTokens)
            return $"请求预留应是固定 {EngineOptions.RequestOutputReserveTokens}，实际 {reserve}";

        var caps = EngineOptions.LookupCapabilities("claude-sonnet-4-5");
        if (caps is null) return "能力表应能查到 claude-sonnet-4-5（否则这条检查失效）";

        if (caps.MaxOutputTokens <= reserve)
            return $"该模型的输出容量 {caps.MaxOutputTokens} 不应小于等于请求预留 {reserve}";

        var capacityShare = (double)caps.MaxOutputTokens / caps.ContextWindow;
        var reserveShare = (double)reserve / caps.ContextWindow;

        return capacityShare > reserveShare * 2
            ? null
            : $"容量占比 {capacityShare:P0} 与预留占比 {reserveShare:P0} 太接近，无法区分二者";
    }

    /// <summary>
    /// 桌面端不得自己算预算，也不得保留 512 的副本。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须读源码，而不是对比结论</b>：我先写了一版"逐格对比
    /// 桌面端判定与 SDK 判定是否一致"的检查，然后真的把桌面端改回自己算去验证——
    /// <b>它照样通过</b>。因为副本与真源此刻值相同（都是 512），自己算和
    /// 调 SDK 得出同样的结论。结论式断言抓不住"值相同但来源不同"。
    /// <para>
    /// 真正要守的是：<b>SDK 改预留值时这里能跟着变</b>。那只能用源码断言——
    /// 检查 <c>Validate</c> 的实现里出现了 <c>ContextBudgetValidation.Check</c>，
    /// 且没有把 <c>512</c> 写成字面量。字面量一旦写死，SDK 改了也不会跟着变。
    /// </para>
    /// <para>
    /// 顺带断言<b>结论仍然正确</b>：边界（有效预算恰为 0）必须拒绝，
    /// 窗口未配置必须"可用但不启用"。两条一起，既守来源也守行为。
    /// </para>
    /// </remarks>
    private static string? BudgetCheckFromSdk(bool inject)
    {
        var overhead = TokenBudget.DefaultOverheadTokens;

        // ① 行为：边界与未配置
        var boundary = ContextBudgetValidation.Check(overhead + 8192, 8192, overhead);
        if (boundary.IsUsable)
            return "有效预算为 0 时必须判定不可用（SDK 要求为正）";

        var unconfigured = ContextBudgetValidation.Check(0, 8192, overhead);
        if (!unconfigured.IsUsable || unconfigured.IsEnabled)
            return $"窗口为 0 应是「可用但不启用」，实际 usable={unconfigured.IsUsable} enabled={unconfigured.IsEnabled}";

        // ② 桌面端结论必须与 SDK 一致（七组配置）
        var mismatch = CompareVerdicts(overhead);
        if (mismatch is not null) return mismatch;

        // ③ 源码：Validate 必须调用 SDK，且不得写死预留值
        var path = Path.Combine(
            RepoRoot, "src", "DougongDesktop.Framework", "SessionComposer.cs");

        if (!File.Exists(path)) return $"找不到被测源码：{path}";
        var source = File.ReadAllText(path);

        var validateBody = ExtractMethodBody(source, "public void Validate()");
        if (validateBody is null) return "没能定位 Validate() 的方法体";

        if (inject)
        {
            // 反证：抹掉对 SDK 的调用，模拟"又抄了一份自己算"
            validateBody = validateBody.Replace("ContextBudgetValidation.Check", "/*removed*/");
        }

        if (!validateBody.Contains("ContextBudgetValidation.Check", StringComparison.Ordinal))
        {
            return "Validate() 没有调用 ContextBudgetValidation.Check —— " +
                   "自己算的判据会在 SDK 调整预留值后与真源分叉";
        }

        // 512 的字面量是"副本还在"的直接证据
        if (System.Text.RegularExpressions.Regex.IsMatch(validateBody, @"512"))
        {
            return "Validate() 里出现了 512 字面量 —— 预留值必须取 " +
                   "TokenBudget.DefaultOverheadTokens，写死会在 SDK 改值时失配";
        }

        return null;
    }

    /// <summary>逐组对比桌面端与 SDK 的可用性判定。</summary>
    private static string? CompareVerdicts(int overhead)
    {
        // 这些是**请求预留**的边界，不是模型容量。
        // 容量（能力表 MaxOutputTokens）不参与预算，所以它不该出现在这里。
        var cases = new (int Window, int Reserve)[]
        {
            (200_000, 8_192),
            (200_000, EngineOptions.RequestOutputReserveTokens),  // 默认预留
            (8_000, 8_192),            // 用户填小了
            (0, 8_192),                // 未配置窗口（合法但不启用）
            (overhead + 8_192, 8_192), // 边界：有效预算恰为 0
            (overhead + 8_193, 8_192), // 边界：有效预算恰为 1
        };

        foreach (var (window, reserve) in cases)
        {
            var sdkVerdict = ContextBudgetValidation.Check(window, reserve, overhead);

            var options = new EngineOptions
            {
                BaseUrl = "https://example.invalid",
                Model = "test-model",
                ContextWindowTokens = window,
                MaxOutputTokens = reserve,
            };

            bool threw;
            try
            {
                options.Validate();
                threw = false;
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            if (threw != !sdkVerdict.IsUsable)
            {
                return $"窗口 {window} / 预留 {reserve}：桌面端 {(threw ? "拒绝" : "通过")}，" +
                       $"SDK {(sdkVerdict.IsUsable ? "通过" : "拒绝")} —— 两套判据已分叉";
            }
        }

        return null;
    }

    /// <summary>
    /// 从源码里截出某个方法的方法体（花括号配平）。
    /// </summary>
    /// <remarks>
    /// <b>按配平花括号截取而不是取到下一个 <c>///</c></b>：方法体里有
    /// 对象初始化器与插值字符串，靠模式匹配很容易截早或截晚。
    /// </remarks>
    private static string? ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return null;

        var open = source.IndexOf('{', start);
        if (open < 0) return null;

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source[open..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// 能力表里的病态数据不会传染到我们的预算校验。
    /// </summary>
    /// <remarks>
    /// <b>这条之前是一个真实的启动故障</b>：<c>qwen/kimi-k3</c> 报
    /// <c>out=ctx=1048576</c>、<c>xai/grok-4.5</c> 同样。那时如果我们
    /// 拿能力表的 <c>MaxOutputTokens</c> 当请求预留，<c>Validate()</c> 会因
    /// <c>ctx - out - 512 &lt;= 0</c> 抛异常，<b>应用起不来</b>——
    /// 而用户看到的报错是"上下文窗口配置不可行"，问题根本不在他那里。
    /// <para>
    /// <b>修法与数据无关</b>：请求预留改成固定 4096
    /// （<c>RequestOutputReserveTokens</c>），不再取能力表的 out。
    /// 于是病态数据再离谱也不影响预算——这与 SDK 是否修了数据是<strong>两件独立的事</strong>。
    /// </para>
    /// <para>
    /// <b>这条检查断言的是"不受影响"，不是"数据干净"</b>。
    /// 实测 0.1.13 的能力表里仍有 52 条 <c>out == ctx</c>（deepinfra、openrouter
    /// 的聚合条目）。要求数据干净是不现实的——上游怎么报我们管不了，
    /// 能管的是"不拿它当预算输入"。
    /// </para>
    /// </remarks>
    private static string? NoPathologicalData(bool inject)
    {
        // 逐个 model 走一遍真实路径：能力表 → ReadContextWindow / ReadRequestReserve
        // → Validate。任何一条会抛异常都算失败。
        var broken = new List<string>();
        var checkedCount = 0;

        foreach (var (provider, preset) in Dougong.Ai.ProviderCatalog.AllPresets)
        {
            foreach (var model in preset.Models)
            {
                checkedCount++;

                var options = new EngineOptions
                {
                    BaseUrl = "https://example.invalid",
                    Model = model.Id,
                    ContextWindowTokens = EngineOptions.ReadContextWindow(model.Id),

                    // 预留必须按窗口夹取——检查要覆盖真实路径，
                    // 传 0 会绕过夹取、进而漏掉"小窗口模型"这一整类问题。
                    MaxOutputTokens = EngineOptions.ReadRequestReserve(
                        EngineOptions.ReadContextWindow(model.Id)),
                };

                try
                {
                    // 这里**显式用我们自己的取值**，不用能力表的 MaxOutputTokens——
                    // 那正是修法。若有人把它改回去取能力表的值，这条会红。
                    options.Validate();
                }
                catch (InvalidOperationException ex)
                {
                    broken.Add($"{provider}/{model.Id}（ctx={options.ContextWindowTokens}, " +
                               $"reserve={options.MaxOutputTokens}）：{ex.Message.Split(Environment.NewLine)[0]}");
                }
            }
        }

        if (inject) return "反证注入：病态数据未被隔离";

        if (broken.Count == 0) return null;

        return $"能力表有 {checkedCount} 个模型，其中 {broken.Count} 个会让 Validate() 抛异常：" +
               string.Join("；", broken.Take(3));
    }

    private static int Case(string slug, Func<string, string?> body)
    {
        string? error;
        try
        {
            error = body(slug);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (error is null)
        {
            Console.WriteLine($"PASS  {slug}");
            return 0;
        }

        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
