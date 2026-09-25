using UAssetAPI;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace UAssetKismet.Experimental;

/// <summary>
/// 把 UE Kismet 的「执行流栈」还原为静态跳转目标。
///
/// 语义（与 UObject::execPush/PopExecutionFlow 对齐）：
/// <list type="bullet">
/// <item><c>PushExecutionFlow(a)</c>：把跳转目标 <c>a</c> 压栈（不跳转）。</item>
/// <item><c>PopExecutionFlow()</c>：弹出栈顶 <c>a</c> 并无条件跳转到 <c>a</c>。</item>
/// <item><c>PopExecutionFlowIfNot(c)</c>：仅当 <c>c</c> 为假时弹栈并跳转到 <c>a</c>；为真时不弹栈、顺序执行。</item>
/// </list>
/// 蓝图编译器用它表达宏（ForEach/While）的「continue / break / 作用域出口」。
/// 由于 Push/Pop 严格配对且压入目标是常量，可在 CFG 上做前向不动点求出每个 Pop 的目标，
/// 之后这套指令就退化成普通的 Jump / JumpIfNot，交给通用结构化算法处理。
/// </summary>
public static class FlowStackResolver
{
    /// <summary>解析结果。</summary>
    public sealed class Result
    {
        /// <summary>Pop / PopExecutionFlowIfNot 语句偏移 → 解析出的跳转目标偏移。</summary>
        public readonly Dictionary<long, long> TargetOf = new();

        /// <summary>无法解析（栈为空，或合流点处各路径栈不一致）的 Pop 语句偏移。</summary>
        public readonly HashSet<long> Unresolved = new();

        /// <summary>栈深度不一致的合流块数（诊断用）。</summary>
        public int ConflictBlocks;

        /// <summary>块起始语句偏移 → 该块入口的执行流栈（null = 未知/冲突），诊断用。</summary>
        public readonly Dictionary<long, long[]?> BlockStacks = new();

        /// <summary>合流冲突诊断日志。</summary>
        public readonly List<string> ConflictLog = new();
    }

    private sealed class Block
    {
        public int Start;
        public int End;              // [Start, End)
        public int FallSucc = -1;
        public int JumpSucc = -1;
        public bool PopJumps;        // 结尾是 PopExecutionFlow / PopExecutionFlowIfNot（弹出式跳转）
        public bool CondPop;         // 结尾是 PopExecutionFlowIfNot
        public bool Dead;            // 结尾是 Return / EndOfScript
    }

    /// <summary>解析整段语句序列的执行流栈。</summary>
    public static Result Resolve(IReadOnlyList<(long Off, KismetExpression Expr)> stmts)
    {
        var result = new Result();
        if (stmts.Count == 0) return result;

        // ---- 语句偏移索引 & 块划分 ----
        var idxOf = new Dictionary<long, int>(stmts.Count);
        for (var i = 0; i < stmts.Count; i++) idxOf[stmts[i].Off] = i;

        var starts = new SortedSet<int> { 0, stmts.Count };
        for (var i = 0; i < stmts.Count; i++)
        {
            var e = stmts[i].Expr;
            switch (e)
            {
                case EX_Jump j:
                    AddTarget(starts, idxOf, j.CodeOffset);
                    starts.Add(i + 1);
                    break;
                case EX_JumpIfNot j:
                    AddTarget(starts, idxOf, j.CodeOffset);
                    starts.Add(i + 1);
                    break;
                case EX_Return:
                case EX_EndOfScript:
                case EX_PopExecutionFlow:
                case EX_PopExecutionFlowIfNot:
                    starts.Add(i + 1);
                    break;
                case EX_PushExecutionFlow p:
                    // pop 的跳转目标必然是某个 push 压入的地址：先把它们切成块边界，
                    // 否则解析出的目标落在块中部，栈状态会与顺序路径混在一起产生假冲突。
                    AddTarget(starts, idxOf, p.PushingAddress);
                    break;
            }
        }

        var arr = starts.ToArray();
        var blocks = new List<Block>();
        var blockOfStmt = new int[stmts.Count];
        for (var i = 0; i < arr.Length - 1; i++)
        {
            if (arr[i] >= arr[i + 1]) continue;
            var b = new Block { Start = arr[i], End = arr[i + 1] };
            for (var s = b.Start; s < b.End; s++) blockOfStmt[s] = blocks.Count;
            blocks.Add(b);
        }
        if (blocks.Count == 0) return result;

        int BlockAt(long off) => idxOf.TryGetValue(off, out var si) ? blockOfStmt[si] : -1;

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            switch (stmts[b.End - 1].Expr)
            {
                case EX_Jump j:
                    b.JumpSucc = BlockAt(j.CodeOffset);
                    break;
                case EX_JumpIfNot j:
                    b.JumpSucc = BlockAt(j.CodeOffset);
                    b.FallSucc = i + 1 < blocks.Count ? i + 1 : -1;
                    break;
                case EX_Return:
                case EX_EndOfScript:
                    b.Dead = true;
                    break;
                case EX_PopExecutionFlow:
                    b.PopJumps = true;
                    b.JumpSucc = -1; // 运行期才知道
                    break;
                case EX_PopExecutionFlowIfNot:
                    b.PopJumps = true;
                    b.CondPop = true;
                    b.JumpSucc = -1;
                    b.FallSucc = i + 1 < blocks.Count ? i + 1 : -1;
                    break;
                default:
                    b.FallSucc = i + 1 < blocks.Count ? i + 1 : -1;
                    break;
            }
        }

        // ---- 前向不动点：求每个块入口的栈 ----
        var state = new List<long>?[blocks.Count];
        var conflict = new bool[blocks.Count];
        var queue = new Queue<int>();

        state[0] = new List<long>();
        queue.Enqueue(0);

        void Offer(int succ, List<long>? outStack, long fromOff)
        {
            if (succ < 0 || succ >= blocks.Count) return;
            if (conflict[succ]) return;
            if (outStack is null)
            {
                conflict[succ] = true; state[succ] = null; queue.Enqueue(succ);
                result.ConflictLog.Add($"#{fromOff:X4} -> #{stmts[blocks[succ].Start].Off:X4}: 未知栈");
                return;
            }
            if (state[succ] is null) { state[succ] = new List<long>(outStack); queue.Enqueue(succ); return; }
            if (!state[succ]!.SequenceEqual(outStack))
            {
                result.ConflictLog.Add(
                    $"#{fromOff:X4} -> #{stmts[blocks[succ].Start].Off:X4}: [{ShowStack(state[succ]!)}] vs [{ShowStack(outStack)}]");
                conflict[succ] = true;
                state[succ] = null;
                queue.Enqueue(succ);
            }
        }

        while (queue.Count > 0)
        {
            var bi = queue.Dequeue();
            var b = blocks[bi];
            var cur = conflict[bi] ? null : new List<long>(state[bi] ?? new List<long>());

            // 本块内逐语句模拟（Pop/PopIfNot 必为块尾，Push 可在块中）
            var resolvedJump = -1L;
            var blockUnresolved = false;
            for (var i = b.Start; i < b.End; i++)
            {
                switch (stmts[i].Expr)
                {
                    case EX_PushExecutionFlow p:
                        cur?.Add(p.PushingAddress);
                        break;
                    case EX_PopExecutionFlow:
                        if (cur is { Count: > 0 })
                        {
                            resolvedJump = cur[^1];
                            cur.RemoveAt(cur.Count - 1);
                        }
                        else blockUnresolved = true;
                        break;
                    case EX_PopExecutionFlowIfNot:
                        if (cur is { Count: > 0 }) resolvedJump = cur[^1];
                        else blockUnresolved = true;
                        break;
                }
            }

            if (blockUnresolved) result.Unresolved.Add(stmts[b.End - 1].Off);
            else if (b.PopJumps && resolvedJump >= 0)
            {
                result.TargetOf[stmts[b.End - 1].Off] = resolvedJump;
                b.JumpSucc = BlockAt(resolvedJump);
            }

            // 计算两条出边的栈。注意 PopExecutionFlowIfNot 只有「跳转」路径弹栈，
            // 顺序路径保持原栈 —— 必须复制，不能共用同一个 List 实例。
            List<long>? jumpOut;
            List<long>? fallOut;
            if (cur is null) { jumpOut = null; fallOut = null; }
            else if (b.CondPop)
            {
                fallOut = cur;
                if (resolvedJump >= 0 && cur.Count > 0)
                {
                    var popped = new List<long>(cur);
                    popped.RemoveAt(popped.Count - 1);
                    jumpOut = popped;
                }
                else jumpOut = null;
            }
            else
            {
                fallOut = cur;
                jumpOut = blockUnresolved ? null : cur;
            }

            if (b.PopJumps)
            {
                // 无条件弹出跳转只有跳转一条后继；条件弹出另有顺序后继（且顺序路径不弹栈）
                Offer(b.JumpSucc, jumpOut, stmts[b.End - 1].Off);
                if (b.CondPop) Offer(b.FallSucc, fallOut, stmts[b.End - 1].Off);
                continue;
            }
            if (b.Dead) continue;
            if (b.JumpSucc >= 0) Offer(b.JumpSucc, fallOut, stmts[b.End - 1].Off);   // EX_Jump
            if (b.FallSucc >= 0) Offer(b.FallSucc, fallOut, stmts[b.End - 1].Off);   // 顺序 / JumpIfNot 另一分支
        }

        result.ConflictBlocks = conflict.Count(x => x);
        for (var i = 0; i < blocks.Count; i++)
            result.BlockStacks[stmts[blocks[i].Start].Off] = conflict[i] ? null : state[i]?.ToArray();
        return result;    }

    private static void AddTarget(SortedSet<int> starts, Dictionary<long, int> idxOf, long off)
    {
        if (idxOf.TryGetValue(off, out var si)) starts.Add(si);
    }

    private static string ShowStack(IEnumerable<long> stack) => string.Join(",", stack.Select(x => x.ToString("X4")));
}
