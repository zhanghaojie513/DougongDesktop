using System.Text;
using System.Text.Json;
using DougongDesktop.Ipc;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 读取循环的容错检查：一条"认不出"的消息不该让整条连接失效。
/// </summary>
/// <remarks>
/// <b>这条检查的由来</b>：我自己在给协议换代时实测到——发一条判别器未知的
/// 事件过去，<c>ReadLoopAsync</c> 直接抛出、连接断掉，**之后所有正常事件都收不到**。
/// 而那个方法的注释当时写着"一条坏行不该让整条连接失效"——
/// 它只管住了 JSON 语法错误，没管住"语法合法但类型认不出"。
/// <para>
/// <b>为什么这种缺陷值得一条专门的检查</b>：它的表现是"引擎挂了"。
/// 用户看到连接断开、消息停住，会去查引擎日志——而引擎那边一切正常，
/// 它只是在正常地发消息。排查方向从一开始就是错的。
/// </para>
/// <para>
/// <b>用真实 <see cref="IpcConnection"/> 与真实序列化配置</b>：不走模拟。
/// 未知判别器是否抛异常取决于 <c>System.Text.Json</c> 的多态行为与
/// <see cref="DesktopProtocol.Json"/> 的配置，模拟会验证一个不存在的实现。
/// </para>
/// </remarks>
internal static class ReadLoopChecks
{
    public static int Run(string? breakMode)
    {
        var failures = 0;

        failures += Case("readloop-unknown-event-keeps-connection",
            slug => UnknownEventKeepsConnection(breakMode == slug));
        failures += Case("readloop-unknown-command-keeps-connection",
            slug => UnknownCommandKeepsConnection(breakMode == slug));
        failures += Case("readloop-bug-is-not-swallowed",
            slug => HandlerBugIsNotSwallowed(breakMode == slug));
        failures += Case("readloop-missing-field-is-not-skipped",
            slug => MissingFieldIsNotSkipped(breakMode == slug));

        return failures;
    }

    /// <summary>
    /// 收到未知判别器的**事件**后，后续正常事件仍必须能收到。
    /// </summary>
    /// <remarks>
    /// <b>为什么用"后续事件仍收到"作为断言，而不是"没抛异常"</b>：
    /// 只断言不抛异常的话，一个"catch 了但把整批后续消息丢掉"的实现也能通过。
    /// 这里断言的是真正要保住的东西——连接的其余部分照常工作。
    /// </remarks>
    private static string? UnknownEventKeepsConnection(bool inject)
    {
        var payload =
            // 未知判别器：模拟"新版引擎发来旧客户端还没实现的事件"
            """{"method":"future/unknownEvent","sequence":1}""" + "\n" +
            // 之后一条完全正常的事件。它能不能收到，就是这条检查的结论。
            """{"method":"engine/status","sequence":2,"state":"ready"}""" + "\n";

        var received = new List<string>();
        var malformed = new List<string>();
        Exception? thrown = null;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            var connection = IpcConnection.FromStream(stream);

            connection.ReadLoopAsync(
                element =>
                {
                    // 这一行故意不加 try/catch：**被测的就是读取循环自己会不会接住**。
                    // 若它不接，异常从这里冒出去，下面的 thrown 会记下。
                    var evt = IpcSerializer.ReadEvent(element);
                    received.Add(evt?.GetType().Name ?? "null");
                    return Task.CompletedTask;
                },
                (line, ex) => malformed.Add(ex.GetType().Name))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (inject) return "反证注入：未知事件让读取循环中断了";

        if (thrown is not null)
        {
            return $"未知判别器让读取循环抛出 {thrown.GetType().Name}，整条连接失效。"
                 + "\n  未知类型是正常演进（新版引擎发新事件），不是错误行——"
                 + "\n  它应当走 onMalformed 并继续读，否则后续事件全部收不到。"
                 + $"\n  实际异常：{thrown.Message}";
        }

        if (malformed.Count == 0)
            return "未知判别器没有回报给 onMalformed——它被静默吞掉了，诊断时看不到。";

        if (!received.Contains(nameof(EngineStatusEvent)))
        {
            return "未知事件之后的那条正常事件没有收到（连接在暗处失效了）。"
                 + $"\n  实际收到：[{string.Join(", ", received)}]";
        }

        return null;
    }

    /// <summary>
    /// 未知判别器的**命令**同样不该让宿主的读取循环中断。
    /// </summary>
    /// <remarks>
    /// <b>两个方向都要测</b>：客户端读 <see cref="ServerEvent"/>、宿主读
    /// <see cref="ClientCommand"/>，两者用的是同一个读取循环但不同的多态根。
    /// 只测一个方向时，另一边的多态根出问题就没人知道。
    /// </remarks>
    private static string? UnknownCommandKeepsConnection(bool inject)
    {
        var payload =
            """{"method":"future/unknownCommand","requestId":"r1"}""" + "\n" +
            """{"method":"session/list","requestId":"r2"}""" + "\n";

        var received = new List<string>();
        var malformed = new List<string>();
        Exception? thrown = null;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
            var connection = IpcConnection.FromStream(stream);

            connection.ReadLoopAsync(
                element =>
                {
                    var cmd = IpcSerializer.ReadCommand(element);
                    received.Add(cmd?.GetType().Name ?? "null");
                    return Task.CompletedTask;
                },
                (line, ex) => malformed.Add(ex.GetType().Name))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        if (inject) return "反证注入：未知命令让读取循环中断了";

        if (thrown is not null)
        {
            return $"未知判别器的命令让读取循环抛出 {thrown.GetType().Name}。"
                 + $"\n  实际异常：{thrown.Message}";
        }

        if (!received.Contains(nameof(ListSessionsCommand)))
        {
            return "未知命令之后的那条正常命令没有收到。"
                 + $"\n  实际收到：" + (received.Count == 0 ? "（空）" : string.Join(", ", received));
        }

        return null;
    }

    /// <summary>
    /// 处理逻辑自己的 bug 必须冒出去，不能被当成"坏行"吞掉。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是过度兜底</b>：给读取循环加 try/catch 时，最顺手的写法是
    /// <c>catch (Exception)</c>。那会把 <c>NullReferenceException</c>、
    /// <c>InvalidOperationException</c> 这类**真实缺陷**变成"跳过这一行"，
    /// 于是界面少一条消息、日志里什么都没有——比直接崩溃难查得多。
    /// <para>
    /// 只接 <see cref="JsonException"/> 与 <see cref="NotSupportedException"/>，
    /// 其余照常冒泡。这条检查就是钉住那个边界。
    /// </para>
    /// </remarks>
    private static string? HandlerBugIsNotSwallowed(bool inject)
    {
        var payload = """{"method":"engine/status","sequence":1,"state":"ready"}""" + "\n";

        var malformed = new List<string>();
        var swallowed = false;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var connection = IpcConnection.FromStream(stream);

        try
        {
            connection.ReadLoopAsync(
                element =>
                {
                    // 模拟"处理逻辑自己有 bug"
                    throw new InvalidOperationException("处理逻辑的缺陷");
                },
                (line, ex) => malformed.Add(ex.GetType().Name))
                .GetAwaiter().GetResult();

            // 走到这里说明异常没冒出来——被读取循环吞了。
            swallowed = true;
        }
        catch (InvalidOperationException)
        {
            // 期望路径：处理逻辑的 bug 正常冒出去。
        }

        if (inject) return "反证注入：处理逻辑的异常被读取循环吞掉了";

        if (swallowed)
        {
            return "处理逻辑抛出的 InvalidOperationException 被读取循环吞掉了，"
                 + "\n  那条消息被静默丢弃（onMalformed 收到：" + string.Join(", ", malformed) + "）。"
                 + "\n  读取循环只该接 JsonException 与 NotSupportedException——"
                 + "\n  接 Exception 会把真实缺陷变成丢数据。";
        }

        return null;
    }

    /// <summary>
    /// "缺必需字段"必须抛出去，不能被当成"未知类型"跳过。
    /// </summary>
    /// <remarks>
    /// <b>这条防的是判据过宽</b>：未知判别器与缺必需字段都是 <see cref="JsonException"/>。
    /// 用 <c>catch (JsonException)</c> 一把接住时，两者都会被走 onMalformed 跳过——
    /// 而它们的性质完全不同：
    /// <list type="bullet">
    /// <item><b>未知判别器</b>是正常演进，跳过是对的。</item>
    /// <item><b>缺必需字段</b>是真不兼容（对端删了字段），必须让人看见。
    /// 静默跳过会让用户看到"少了一条消息"，而日志里只有一条"坏行"。</item>
    /// </list>
    /// <para>
    /// <b>载荷用真实形状</b>：<c>engine/status</c> 缺 <c>state</c>（required）。
    /// 这条消息判别器合法、JSON 也合法，只有必需字段缺失。
    /// </para>
    /// </remarks>
    private static string? MissingFieldIsNotSkipped(bool inject)
    {
        // 判别器认识（engine/status），但缺了 required 的 State。
        var payload = """{"method":"engine/status","sequence":1}""" + "\n";

        var malformed = new List<string>();
        var swallowed = false;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var connection = IpcConnection.FromStream(stream);

        try
        {
            connection.ReadLoopAsync(
                element =>
                {
                    var evt = IpcSerializer.ReadEvent(element);
                    return Task.CompletedTask;
                },
                (line, ex) => malformed.Add(ex.GetType().Name))
                .GetAwaiter().GetResult();

            // 走到这里说明它被当成"坏行"跳过了。
            swallowed = true;
        }
        catch (JsonException)
        {
            // 期望路径：真错误冒出去。
        }

        if (inject) return "反证注入：缺必需字段的消息被静默跳过了";

        if (swallowed)
        {
            return "缺必需字段的消息被当成坏行跳过了（onMalformed 收到：" + string.Join(", ", malformed) + "）。"
                 + "\n  未知判别器可以跳过，缺字段不行——那是真不兼容，"
                 + "\n  静默跳过让用户看到消息无声消失。判据要窄到只认"
                 + "\n  \"unrecognized type discriminator\"。";
        }

        return null;
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
