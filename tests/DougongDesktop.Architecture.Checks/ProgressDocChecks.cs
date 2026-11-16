using System.Text.RegularExpressions;

namespace DougongDesktop.Architecture.Checks;

/// <summary>
/// 进度表的自洽检查：表头数字必须等于逐行统计。
/// </summary>
/// <remarks>
/// <b>为什么需要这条</b>：<c>docs/dsh-parity-progress.md</c> 的表头是手填的，
/// 而明细是逐行的。2026-11-16 实测过四处不符（P8e 25%/实 19%、
/// P8f 40%/实 30%、P8g 88%/实 82%、合计 🟡 5/实 4）——原因都是
/// 改了一行状态后手填表头却没重算。
/// <para>
/// 文档里写着"两处对不上说明漏改"，但**没有东西去查**，所以照样漂。
/// 这条检查把那句自律变成可执行的断言。
/// </para>
/// <para>
/// <b>口径</b>：完成度 = <c>(✅ + 🟡×0.5) / (✅+🟡+⬜)</c>，
/// 不含 <c>⏳</c> 与 <c>➖</c>（明确不做的项不该进分母）。
/// </para>
/// </remarks>
internal static class ProgressDocChecks
{
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
        return Case("progress-table-matches-its-rows", () => TableMatchesRows(breakMode == "progress-table-matches-its-rows"));
    }

    private static string? TableMatchesRows(bool breakMode)
    {
        var path = Path.Combine(RepoRoot, "docs", "dsh-parity-progress.md");
        if (!File.Exists(path)) return "找不到 docs/dsh-parity-progress.md";

        var lines = File.ReadAllLines(path);

        // 逐行统计：按当前所在 ### P8x 章节归组
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? current = null;

        foreach (var line in lines)
        {
            var sec = Regex.Match(line, @"^### (P8[a-g])");
            if (sec.Success) { current = sec.Groups[1].Value; continue; }

            var row = Regex.Match(line, @"^\| ([A-FM]\d+) \| (✅|🟡|⬜|⏳|➖) \|");
            if (!row.Success || current is null) continue;

            if (!groups.TryGetValue(current, out var rows))
                groups[current] = rows = new Dictionary<string, string>(StringComparer.Ordinal);

            // 同一编号重复出现视为文档错误（表会被拆成多节，但不该重号）
            if (rows.ContainsKey(row.Groups[1].Value))
                return $"{current} 的 {row.Groups[1].Value} 出现了两次";

            rows[row.Groups[1].Value] = row.Groups[2].Value;
        }

        if (groups.Count == 0) return "没从进度表里解析出任何行——表格式变了？";

        if (breakMode)
        {
            // 反证：用一份人为改坏的表头，确认下面的比对真的会失败
            var fake = groups.ToDictionary(
                kv => kv.Key,
                kv => (Done: 0, Half: 0, Todo: 0, Later: 0, Skip: 0, Total: 1));
            return Validate(fake, groups).Count == 0
                ? "断言抓不到人为改坏的表头"
                : "已确认断言能发现表头与行不符";
        }

        // 从表头读声明值
        var declared = new Dictionary<string, (int Done, int Half, int Todo, int Later, int Skip, int Total)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // 第一列要么是 P8a…P8g，要么是"合计"（带 ** 加粗）
            var m = Regex.Match(line,
                @"^\| \*{0,2}(P8[a-g]|合计)\*{0,2}[^|]*\| \*{0,2}(\d+)\*{0,2} \| \*{0,2}(\d+)\*{0,2} \| \*{0,2}(\d+)\*{0,2} \| \*{0,2}(\d+)\*{0,2} \| \*{0,2}(\d+)\*{0,2} \| \*{0,2}(\d+)\*{0,2} \|");
            if (m.Success)
            {
                declared[m.Groups[1].Value] = (
                    int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
                    int.Parse(m.Groups[6].Value), int.Parse(m.Groups[7].Value));
            }
        }

        if (declared.Count == 0) return "没解析出表头行（P8a…P8g 那七行）";

        var problems = Validate(declared, groups);
        return problems.Count == 0 ? null : string.Join("；", problems);
    }

    private static List<string> Validate(
        Dictionary<string, (int Done, int Half, int Todo, int Later, int Skip, int Total)> declared,
        Dictionary<string, Dictionary<string, string>> groups)
    {
        var problems = new List<string>();

        foreach (var (stage, rows) in groups)
        {
            var done = rows.Values.Count(v => v == "✅");
            var half = rows.Values.Count(v => v == "🟡");
            var todo = rows.Values.Count(v => v == "⬜");
            var later = rows.Values.Count(v => v == "⏳");
            var skip = rows.Values.Count(v => v == "➖");
            var total = rows.Count;

            if (!declared.TryGetValue(stage, out var d))
            {
                problems.Add($"{stage} 有 {total} 行明细但表头没有这一行");
                continue;
            }

            var diffs = new List<string>();
            if (d.Done != done) diffs.Add($"✅ 写 {d.Done} 实为 {done}");
            if (d.Half != half) diffs.Add($"🟡 写 {d.Half} 实为 {half}");
            if (d.Todo != todo) diffs.Add($"⬜ 写 {d.Todo} 实为 {todo}");
            if (d.Later != later) diffs.Add($"⏳ 写 {d.Later} 实为 {later}");
            if (d.Skip != skip) diffs.Add($"➖ 写 {d.Skip} 实为 {skip}");
            if (d.Total != total) diffs.Add($"小计 写 {d.Total} 实为 {total}");

            if (diffs.Count > 0)
                problems.Add($"{stage}: {string.Join("，", diffs)}");
        }

        // 合计行
        var sumDone = groups.Values.Sum(g => g.Values.Count(v => v == "✅"));
        var sumHalf = groups.Values.Sum(g => g.Values.Count(v => v == "🟡"));
        var sumTodo = groups.Values.Sum(g => g.Values.Count(v => v == "⬜"));
        var sumLater = groups.Values.Sum(g => g.Values.Count(v => v == "⏳"));
        var sumSkip = groups.Values.Sum(g => g.Values.Count(v => v == "➖"));
        var sumTotal = groups.Values.Sum(g => g.Count);

        var totalDone = declared.TryGetValue("合计", out var t) ? t : default;

        if (totalDone.Done != sumDone || totalDone.Half != sumHalf || totalDone.Todo != sumTodo
            || totalDone.Later != sumLater || totalDone.Skip != sumSkip || totalDone.Total != sumTotal)
        {
            problems.Add(
                $"合计: ✅ 写 {totalDone.Done} 实为 {sumDone}，🟡 写 {totalDone.Half} 实为 {sumHalf}，"
                + $"⬜ 写 {totalDone.Todo} 实为 {sumTodo}，⏳ 写 {totalDone.Later} 实为 {sumLater}，"
                + $"➖ 写 {totalDone.Skip} 实为 {sumSkip}，小计 写 {totalDone.Total} 实为 {sumTotal}");
        }

        return problems;
    }

    private static int Case(string slug, Func<string?> body)
    {
        string? error;
        try { error = body(); }
        catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; }

        if (error is null) { Console.WriteLine($"PASS  {slug}"); return 0; }
        Console.WriteLine($"FAIL  {slug}");
        Console.WriteLine($"      {error}");
        return 1;
    }
}
