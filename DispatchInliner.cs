using UAssetKismet.Experimental;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetKismet;

namespace UAssetKismet.Experimental;

/// <summary>
/// 实验性功能：把 ExecuteUbergraph 反 dispatch 后的 case 体内联回各事件函数。
/// - 跳转段用栈追踪递归内联（无条件 goto 指向的收尾/共享段）
/// - JumpIfNot 近似转 if { 目标段 }
/// - 简单语义清理：连续重复 return 去重
/// 不影响默认反编译路径。
/// </summary>
public static class DispatchInliner
{
    /// <summary>单函数内联输出的行数上限（保险丝，正常输入远达不到）。</summary>
    private const int MaxLinesPerFunction = 50000;

    public static string Run(UAsset asset, FunctionExport uber,
        IReadOnlyList<(long n, string caller)> calls, IReadOnlyList<FunctionExport> funcs,
        UhtSignatureIndex? signatures = null)
    {
        var sb = new StringBuilder();
        foreach (var fn in funcs)
        {
            if (ReferenceEquals(fn, uber)) continue;
            sb.AppendLine(InlineOne(asset, uber, calls, fn, signatures)).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>内联单个事件函数；无 dispatch 调用的函数返回 null（由调用方走普通结构化）。</summary>
    public static string? InlineOne(UAsset asset, FunctionExport uber,
        IReadOnlyList<(long n, string caller)> calls, FunctionExport fn,
        UhtSignatureIndex? signatures = null)
    {
        var hasUberCall = calls.Any(c => c.caller == fn.ObjectName.ToString());
        if (!hasUberCall) return null;
        return InlineFunction(asset, uber, asset.Exports.IndexOf(uber) + 1, calls, fn, signatures);
    }

    private static string InlineFunction(UAsset asset, FunctionExport uber, int uberIdx,
        IReadOnlyList<(long n, string caller)> calls, FunctionExport fn,
        UhtSignatureIndex? signatures)
    {
        var uberStmts = BuildStmts(asset, uber);
        var uberIdxOf = IndexOf(uberStmts);
        var callMap = calls.ToDictionary(c => c.n, c => c.caller);

        // 被多处引用的收尾/共享段：只展开一次并以 label+goto 引用，
        // 否则每个 case 都会重新展开同一段，输出指数膨胀（曾有单函数展开到数百万行）。
        var refCount = new Dictionary<long, int>();
        foreach (var (_, e) in uberStmts)
        {
            switch (e)
            {
                case EX_Jump j: refCount[j.CodeOffset] = refCount.GetValueOrDefault(j.CodeOffset) + 1; break;
                case EX_JumpIfNot j: refCount[j.CodeOffset] = refCount.GetValueOrDefault(j.CodeOffset) + 1; break;
            }
        }
        var shared = new HashSet<long>(refCount.Where(kv => kv.Value > 1).Select(kv => kv.Key));
        var pendingLabels = new SortedSet<long>();

        var renderer = new ExprRenderer(asset, signatures);
        var exprs = fn.ScriptBytecode ?? Array.Empty<KismetExpression>();
        var lines = new List<string>();

        foreach (var top in exprs)
        {
            if (top is EX_EndOfScript) break;

            // 查找该语句内是否调用 ExecuteUbergraph
            var (n, exprNode) = FindUberCallIn(top, uberIdx);
            if (n is not null && exprNode is not null)
            {
                var callerName = callMap.TryGetValue(n.Value, out var cn) ? cn : "?";
                lines.Add($"// ===== 内联 {callerName} (case {n.Value}) =====");
                EmitSegmentInline(renderer, uberStmts, uberIdxOf, n.Value, lines, new HashSet<long>(), 0,
                    shared, pendingLabels);
                continue; // 用 case 体替换原调用语句
            }

            var line = renderer.Render(top);
            if (string.IsNullOrEmpty(line)) continue;
            lines.Add(line + ";");
        }

        // 展开被 goto 引用、但还没落地成 label 的共享段
        while (pendingLabels.Count > 0)
        {
            var off = pendingLabels.Min;
            pendingLabels.Remove(off);
            lines.Add($"L_{off:X4}: // 共享跳转段（多处引用，只展开一次）");
            EmitSegmentInline(renderer, uberStmts, uberIdxOf, off, lines, new HashSet<long>(), 0,
                shared, pendingLabels);
        }

        // ---- 语义清理：连续重复 return 去重 ----
        var cleaned = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var cur = lines[i].TrimEnd();
            if (cur == "return;" && i + 1 < lines.Count && lines[i + 1].TrimEnd() == "return;")
                continue; // 去掉前一个，保留最后
            cleaned.Add(lines[i]);
        }
        // ---- 语义优化：CallFunc 临时量内联进参数位 ----
        cleaned = SemanticOptimizer.OptimizeLines(cleaned);
        // 清理函数末尾多余 return（最后一个 return 之前的孤立 return 保留语义；此处仅去紧邻重复）

        var sb = new StringBuilder();
        sb.AppendLine($"void {fn.ObjectName}()  // 已内联 dispatch");
        sb.AppendLine("{");
        var level = 1;
        foreach (var raw in cleaned)
        {
            var line = renderer.CleanSelf(raw.TrimEnd());
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("}")) level = Math.Max(0, level - 1);
            sb.Append(' ', level * 4).AppendLine(line);
            if (line.EndsWith("{")) level++;
            else if (line == "}" ) { /* 已减 */ }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>栈式追踪：从 uber 的 EntryPoint 偏移开始展开一个 case（goto 目标段递归内联）。
    /// 被多处引用的段只展开一次，改为 label+goto 引用，避免指数膨胀。</summary>
    private static void EmitSegmentInline(ExprRenderer renderer,
        List<(long off, KismetExpression expr)> uberStmts, Dictionary<long, int> idxOf,
        long startOff, List<string> lines, HashSet<long> stack, int depth,
        HashSet<long> shared, SortedSet<long> pendingLabels)
    {
        if (depth > 32) return;
        if (lines.Count > MaxLinesPerFunction) return; // 保险丝：防止异常输入撑出超大文件
        var curStack = new HashSet<long>(stack) { startOff };
        if (curStack.Count == stack.Count) return; // 已在本链 → 防环
        if (!idxOf.TryGetValue(startOff, out var si)) return;

        var i = si;
        while (i < uberStmts.Count)
        {
            if (lines.Count > MaxLinesPerFunction)
            {
                lines.Add("// [截断] 该函数内联输出过长，已停止展开");
                return;
            }
            var (off, e) = uberStmts[i];
            switch (e)
            {
                case EX_EndOfScript:
                    return;
                case EX_Jump j:
                {
                    if (IsReturnOnly(uberStmts, idxOf, j.CodeOffset))
                    {
                        lines.Add("return;");
                    }
                    else if (shared.Contains(j.CodeOffset))
                    {
                        lines.Add($"goto L_{j.CodeOffset:X4};");
                        pendingLabels.Add(j.CodeOffset);
                    }
                    else
                    {
                        lines.Add($"// goto L_{j.CodeOffset:X4} 收尾段:");
                        EmitSegmentInline(renderer, uberStmts, idxOf, j.CodeOffset, lines, curStack, depth + 1,
                            shared, pendingLabels);
                    }
                    return; // 无条件跳转，本段结束
                }
                case EX_JumpIfNot j:
                {
                    var cond = renderer.Render(j.BooleanExpression);
                    lines.Add($"if (!{cond})");
                    lines.Add("{");
                    if (shared.Contains(j.CodeOffset))
                    {
                        lines.Add($"    goto L_{j.CodeOffset:X4};");
                        pendingLabels.Add(j.CodeOffset);
                    }
                    else
                    {
                        EmitSegmentInline(renderer, uberStmts, idxOf, j.CodeOffset, lines, curStack, depth + 1,
                            shared, pendingLabels);
                    }
                    lines.Add("}");
                    i++;
                    continue; // 顺序继续（条件不成立时执行目标段并返回/收尾；成立时走后续）
                }
                case EX_Return r:
                    lines.Add(r.ReturnExpression is EX_Nothing ? "return;" : $"return {renderer.Render(r.ReturnExpression)};");
                    return;
                default:
                {
                    var line = renderer.Render(e);
                    if (!string.IsNullOrEmpty(line)) lines.Add(line + ";");
                    i++;
                    continue;
                }
            }
        }
    }

    private static bool IsReturnOnly(List<(long off, KismetExpression expr)> stmts,
        Dictionary<long, int> idxOf, long off)
    {
        if (!idxOf.TryGetValue(off, out var si) || si >= stmts.Count) return false;
        var hasReturn = false;
        for (var i = si; i < stmts.Count; i++)
        {
            switch (stmts[i].expr)
            {
                case EX_Return: hasReturn = true; break;
                case EX_Nothing or EX_EndOfScript: break;
                default: return false;
            }
        }
        return hasReturn;
    }

    private static (long? n, KismetExpression? callNode) FindUberCallIn(KismetExpression root, int uberIdx)
    {
        long? found = null;
        KismetExpression? node = null;
        void Walk(KismetExpression e)
        {
            switch (e)
            {
                case EX_FinalFunction f when f.StackNode.Index == uberIdx: // 含 CallMath / LocalFinalFunction
                    found = FirstConst(f.Parameters);
                    node = e;
                    return;
            }
            foreach (var f in e.GetType().GetFields())
            {
                if (found is not null) return;
                if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
                if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                    foreach (var x in arr) { if (found is not null) return; Walk(x); }
            }
        }
        Walk(root);
        return (found, node);
    }

    private static long? FirstConst(KismetExpression[]? ps)
    {
        if (ps is null || ps.Length == 0) return null;
        var raw = ps[0].GetType().GetField("RawValue")?.GetValue(ps[0]);
        return raw is int i ? i : raw is byte b ? (long)b : null;
    }

    private static List<(long off, KismetExpression expr)> BuildStmts(UAsset asset, FunctionExport fn)
    {
        var list = new List<(long, KismetExpression)>();
        long pos = 0;
        foreach (var e in fn.ScriptBytecode ?? Array.Empty<KismetExpression>())
        {
            list.Add((pos, e));
            try { pos += e.GetSize(asset); } catch { pos += 1; }
        }
        return list;
    }

    private static Dictionary<long, int> IndexOf(List<(long off, KismetExpression expr)> stmts)
    {
        var d = new Dictionary<long, int>();
        for (var i = 0; i < stmts.Count; i++) d[stmts[i].off] = i;
        return d;
    }
}
