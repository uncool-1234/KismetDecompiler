using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetKismet.Experimental;

namespace UAssetKismet;

/// <summary>
/// Kismet 字节码 → 结构化伪代码。
///
/// 流程：
/// <list type="number">
/// <item>语句切分：把 <c>ScriptBytecode</c> 展开为「字节偏移 + 表达式」序列。</item>
/// <item>执行流栈求解：<see cref="FlowStackResolver"/> 把 Push/PopExecutionFlow 还原成静态跳转。</item>
/// <item>基本块 + CFG：在跳转目标与终结语句之后切块，建立前后继边。</item>
/// <item>可达性分析：不可达块（死代码）直接丢弃。</item>
/// <item>支配 / 后支配：支配树用于识别自然循环，后支配用于求 if/else 的汇合点。</item>
/// <item>结构化发射：if / if-else / while / do-while / break / continue；
///       无法结构化的边退化为 label + goto（label 一定会被补出）。</item>
/// </list>
/// </summary>
public class StructuredKismetDecompiler
{
    // ================= 数据模型 =================

    private sealed class Stmt
    {
        public long Off;
        public int Size;
        public KismetExpression Expr = null!;
    }

    private enum Term { Fall, Jump, JumpIfNot, Return, Halt }

    private sealed class Block
    {
        public int Id;
        public int Start, End;          // 语句下标区间 [Start, End)
        public long Off;                // 起始字节偏移
        public Term Term = Term.Fall;
        public int JumpSucc = -1;       // Jump 目标 / JumpIfNot 的 false 目标
        public int FallSucc = -1;
        public long JumpOff;            // 跳转目标字节偏移（用于 label）
        public KismetExpression? CondExpr;   // JumpIfNot 的条件表达式（惰性渲染，便于提升多行子结构）
        public string? CondCache;
        public List<string> CondPrelude = new();
        public readonly List<int> Succ = new();
        public readonly List<int> Pred = new();
        public bool Reachable;
        public bool Emitted;
    }

    private sealed class Loop
    {
        public int Header;
        public HashSet<int> Blocks = new();
        public HashSet<int> Exits = new();
        public int Follow = -1;
    }

    private sealed class LoopCtx
    {
        public int Header;
        public HashSet<int> Blocks = new();
        /// <summary>从循环体直接跳出去的边目标块（loop.Exits）。</summary>
        public HashSet<int> Exits = new();
        public int Follow = -1;
    }

    private enum JumpAction { Follow, Stop, Break, Continue, Goto }

    // ================= 状态 =================

    private readonly UAsset _asset;
    private readonly ExprRenderer _renderer;
    private readonly UhtSignatureIndex? _sigs;
    private readonly string _funcName;
    private List<string> _locals = new();
    private int _declInsertAt = -1;
    private int _declIndent = 1;

    private List<Stmt> _stmts = new();
    private Dictionary<long, int> _idxOfOff = new();
    private List<Block> _blocks = new();

    private ulong[][] _dom = Array.Empty<ulong[]>();
    private ulong[][] _pdom = Array.Empty<ulong[]>();
    private int[] _ipdom = Array.Empty<int>();

    private readonly List<Loop> _loops = new();
    private readonly Dictionary<int, int> _innermostLoop = new();   // 块 → 最内层循环头
    private readonly HashSet<int> _emittedLoops = new();
    private readonly List<LoopCtx> _loopStack = new();

    private readonly List<string> _out = new();
    private readonly List<int> _outBlock = new();
    private readonly Dictionary<int, int> _firstLine = new();
    private readonly HashSet<int> _gotoTargets = new();
    /// <summary>块 → 它所属区域在 _out 里的结束行（不含）。</summary>
    private readonly Dictionary<int, int> _regionEnd = new();
    /// <summary>发射中的区域嵌套栈，每层记录本层新发射的块。</summary>
    private readonly Stack<List<int>> _regionTouched = new();
    /// <summary>每个 goto 所在 _out 行 → (目标块, 该 goto 所在区域的续接块)。</summary>
    private readonly Dictionary<int, (int Target, int Follow)> _gotoFollow = new();
    private int _ind;

    public StructuredKismetDecompiler(UAsset asset, string funcName, UhtSignatureIndex? signatures = null)
    {
        _asset = asset;
        _renderer = new ExprRenderer(asset, signatures);
        _sigs = signatures;
        _funcName = ExprRenderer.Ident(funcName);
    }

    // ================= 公共入口 =================

    /// <summary>反编译单个函数。</summary>
    public string Decompile(FunctionExport fn)
    {
        var hdr = UhtHeaderComment(fn);
        var body = DecompileBody(fn);
        return hdr.Length > 0 ? hdr + body : body;
    }

    /// <summary>收集该函数体内命中的 import 调用，输出为函数头注释（真实 UHT 签名，含 out/ref 修饰）。</summary>
    public string UhtHeaderComment(FunctionExport fn)
    {
        if (_sigs is null || fn.ScriptBytecode is null) return "";
        var paths = new HashSet<string>();
        ImportSignatures.CollectImportFuncs(_asset, fn.ScriptBytecode, paths);
        var sb = new StringBuilder();
        foreach (var p in paths.OrderBy(x => x))
            if (_sigs.ByFullKey.TryGetValue(p, out var s))
                sb.Append("// ").AppendLine(s.ToString());
        return sb.ToString();
    }

    /// <summary>函数体（不含 UHT 头注释）。</summary>
    public string DecompileBody(FunctionExport fn)
    {
        Reset(fn);
        BuildStatements(fn);
        var flow = ResolveFlow();
        _flowTargets = flow.TargetOf;
        _flowUnresolved = flow.Unresolved;
        SplitBlocks(flow);
        BuildEdges();
        MarkReachable();
        Compact();

        var sig = FunctionSignatures.Analyze(_asset, fn);
        _locals = FunctionSignatures.LocalDeclarations(sig);
        var head = FunctionSignatures.SignatureLine(sig, _funcName);

        if (_blocks.Count == 0)
        {
            var empty = new StringBuilder();
            empty.AppendLine(head);
            empty.AppendLine("{");
            EmitLocalDecls(empty, 1);
            empty.AppendLine("}");
            return empty.ToString();
        }
        ComputeDominators();
        ComputePostDominators();
        FindLoops();

        _out.Clear(); _outBlock.Clear(); _firstLine.Clear(); _gotoTargets.Clear(); _regionEnd.Clear(); _gotoFollow.Clear(); _regionTouched.Clear();
        _emittedLoops.Clear(); _loopStack.Clear();
        _ind = 0;
        EmitLine(head);
        EmitLine("{");
        _ind = 1;
        _declInsertAt = _out.Count; _declIndent = _ind;
        EmitRegion(0, new HashSet<int>(), -1);
        EmitPendingGotoTargets();
        _ind = 0;
        EmitLine("}");

        return Assemble();
    }

    /// <summary>把局部变量声明写进当前输出（空函数用）。</summary>
    private void EmitLocalDecls(StringBuilder sb, int indent)
    {
        var pad = new string(' ', indent * 4);
        foreach (var l in _locals) sb.Append(pad).AppendLine(l);
        if (_locals.Count > 0) sb.AppendLine();
    }

    /// <summary>反 Dispatch：把 ExecuteUbergraph 的 EntryPoint 跳转表还原为 switch-case（每个 case 内做结构化）。</summary>
    public static string DecompileDispatch(UAsset asset, FunctionExport uber,
        IEnumerable<(long n, string caller)> calls, UhtSignatureIndex? signatures = null)
    {
        var d = new StructuredKismetDecompiler(asset, uber.ObjectName.ToString(), signatures);
        var body = d.EmitDispatch(uber, calls.OrderBy(c => c.n).ToList());
        var hdr = d.UhtHeaderComment(uber);
        return hdr.Length > 0 ? hdr + body : body;
    }

    /// <summary>反 Dispatch：把 ExecuteUbergraph 的 EntryPoint 跳转表还原为 switch-case，每个 case 内做结构化。</summary>
    private string EmitDispatch(FunctionExport uber, List<(long n, string caller)> calls)
    {
        Reset(uber);
        BuildStatements(uber);
        var flow = ResolveFlow(calls.Select(c => c.n).ToList());
        _flowTargets = flow.TargetOf;
        _flowUnresolved = flow.Unresolved;
        SplitBlocks(flow);
        BuildEdges();
        // ubergraph 的每个 case 入口只能通过 ComputedJump 到达，CFG 里没有这条边。
        // 必须把它们也当成可达性根，否则 case 体会被当成死代码整段删掉（switch 变空壳）。
        var entryRoots = new List<int>();
        foreach (var (n, _) in calls)
            if (_idxOfOff.TryGetValue(n, out var si) && si < _blockOfStmt.Length && _blockOfStmt[si] >= 0)
                entryRoots.Add(_blockOfStmt[si]);
        MarkReachable(entryRoots);
        Compact();

        _out.Clear(); _outBlock.Clear(); _firstLine.Clear(); _gotoTargets.Clear(); _regionEnd.Clear(); _gotoFollow.Clear(); _regionTouched.Clear();
        _emittedLoops.Clear(); _loopStack.Clear();

        var sig = FunctionSignatures.Analyze(_asset, uber);
        _locals = FunctionSignatures.LocalDeclarations(sig);
        var retType = sig.ReturnType.Length == 0 ? "void" : sig.ReturnType;

        if (_blocks.Count == 0 || calls.Count == 0)
        {
            _ind = 0;
            EmitLine($"{retType} {_funcName}({sig.ParamList()})");
            EmitLine("{");
            _ind = 1;
            _declInsertAt = _out.Count; _declIndent = _ind;
            if (_blocks.Count > 0) EmitRegion(0, new HashSet<int>(), -1);
            EmitPendingGotoTargets();
            _ind = 0;
            EmitLine("}");
            return Assemble();
        }

        ComputeDominators();
        ComputePostDominators();
        FindLoops();

        // case 入口块
        var caseEntries = new List<(int Block, long N, string Caller)>();
        foreach (var (n, caller) in calls)
            if (_idxOfOff.TryGetValue(n, out var si) && si < _blockOfStmt.Length && _blocks.Count > 0)
            {
                var bid = _blockOfStmt[si];
                if (bid >= 0 && _blocks[bid].Reachable) caseEntries.Add((bid, n, caller));
            }
        caseEntries = caseEntries.OrderBy(c => c.N).ToList();

        _ind = 0;
        EmitLine($"{retType} {_funcName}({sig.ParamList()})");
        EmitLine("{");
        _ind = 1;
        _declInsertAt = _out.Count; _declIndent = _ind;
        EmitLine("// 反 Dispatch：ComputedJump 按 EntryPoint 字节偏移跳转");
        EmitLine("switch (EntryPoint)");
        EmitLine("{");
        _ind = 1;
        foreach (var (blockId, n, caller) in caseEntries)
        {
            var stops = new HashSet<int>(caseEntries.Select(c => c.Block));
            stops.Remove(blockId);
            EmitLine($"case {n}: // ← {caller}");
            _ind++;
            if (!_blocks[blockId].Emitted) EmitRegion(blockId, stops, -1);
            EmitLine("break;");
            _ind--;
        }
        _ind = 1;
        EmitLine("}");          // switch 的 }
        _ind = 0;
        EmitLine("}");          // 函数的 }
        EmitPendingGotoTargets();
        return Assemble();
    }

    /// <summary>诊断用：输出基本块 / 边 / 支配信息。</summary>
    public string DumpCfg(FunctionExport fn)
    {
        Reset(fn);
        BuildStatements(fn);
        var flow = ResolveFlow();
        _flowTargets = flow.TargetOf;
        _flowUnresolved = flow.Unresolved;
        SplitBlocks(flow);
        BuildEdges();
        MarkReachable();
        Compact();
        ComputeDominators();
        ComputePostDominators();
        FindLoops();

        var sb = new StringBuilder();
        sb.Append("FUNCTION ").Append(fn.ObjectName)
          .Append("  语句=").Append(_stmts.Count)
          .Append("  块=").Append(_blocks.Count)
          .AppendLine();
        foreach (var b in _blocks)
        {
            var term = b.Term switch
            {
                Term.Jump => $"Jump #{b.JumpOff:X4}",
                Term.JumpIfNot => $"JumpIfNot(!{CondText(b, false)}) -> #{b.JumpOff:X4}",
                Term.Return => "Return",
                Term.Halt => "Halt",
                _ => "Fall"
            };
            sb.Append($"  B{b.Id,-4} #{b.Off:X4}  succ=[{string.Join(",", b.Succ.Select(x => "B" + x))}]")
              .Append("  pred=[").Append(string.Join(",", b.Pred.Select(x => "B" + x))).Append(']')
              .Append("  ipdom=B").Append(_ipdom.Length > b.Id ? _ipdom[b.Id].ToString() : "?")
              .Append("  pdom={").Append(string.Join(",", Bits(_pdom[b.Id]))).Append('}')
              .Append("  dom={").Append(string.Join(",", Bits(_dom[b.Id]))).Append('}')
              .Append("  ").Append(term);
            if (_innermostLoop.TryGetValue(b.Id, out var h)) sb.Append("  [loop B").Append(h).Append(']');
            sb.AppendLine();
        }
        foreach (var l in _loops)
            sb.Append("  LOOP header=B").Append(l.Header)
              .Append(" blocks=[").Append(string.Join(",", l.Blocks.OrderBy(x => x).Select(x => "B" + x))).Append(']')
              .Append(" exits=[").Append(string.Join(",", l.Exits.OrderBy(x => x).Select(x => "B" + x))).Append(']')
              .Append(" follow=B").Append(l.Follow).AppendLine();
        return sb.ToString();
    }

    // ================= 1. 语句 =================

    private void Reset(FunctionExport fn)
    {
        _stmts = new List<Stmt>();
        _idxOfOff = new Dictionary<long, int>();
        _blocks = new List<Block>();
        _loops.Clear();
        _innermostLoop.Clear();
    }

    private void BuildStatements(FunctionExport fn)
    {
        long pos = 0;
        foreach (var e in fn.ScriptBytecode ?? Array.Empty<KismetExpression>())
        {
            var size = 1;
            try { size = (int)e.GetSize(_asset); } catch { }
            _idxOfOff[pos] = _stmts.Count;
            _stmts.Add(new Stmt { Off = pos, Size = size, Expr = e });
            pos += size;
        }
    }

    private FlowStackResolver.Result ResolveFlow(IReadOnlyList<long>? dispatchEntries = null) =>
        FlowStackResolver.Resolve(_stmts.Select(s => (s.Off, s.Expr)).ToList(), dispatchEntries);

    // ================= 2. 基本块 =================

    private void SplitBlocks(FlowStackResolver.Result flow)
    {
        var n = _stmts.Count;
        var starts = new SortedSet<int> { 0, n };
        for (var i = 0; i < n; i++)
        {
            var e = _stmts[i].Expr;
            switch (e)
            {
                case EX_Jump j:
                    if (_idxOfOff.TryGetValue(j.CodeOffset, out var tj)) starts.Add(tj);
                    starts.Add(i + 1);
                    break;
                case EX_JumpIfNot j:
                    if (_idxOfOff.TryGetValue(j.CodeOffset, out var tn)) starts.Add(tn);
                    starts.Add(i + 1);
                    break;
                case EX_PushExecutionFlow p:
                    if (_idxOfOff.TryGetValue(p.PushingAddress, out var tp)) starts.Add(tp);
                    break;
                case EX_PopExecutionFlow:
                case EX_PopExecutionFlowIfNot:
                    if (flow.TargetOf.TryGetValue(_stmts[i].Off, out var tpop)
                        && _idxOfOff.TryGetValue(tpop, out var tpi)) starts.Add(tpi);
                    starts.Add(i + 1);
                    break;
                case EX_Return:
                case EX_EndOfScript:
                    starts.Add(i + 1);
                    break;
                case EX_ComputedJump:
                    // 分发器之后是各 case 体，不属于静态顺序路径：单独切块。
                    starts.Add(i + 1);
                    break;
            }
        }

        var arr = starts.ToArray();
        for (var i = 0; i < arr.Length - 1; i++)
        {
            if (arr[i] >= arr[i + 1]) continue;
            _blocks.Add(new Block
            {
                Id = _blocks.Count,
                Start = arr[i],
                End = arr[i + 1],
                Off = _stmts[arr[i]].Off
            });
        }
    }

    private void BuildEdges()
    {
        var blockOfStmt = new int[_stmts.Count];
        foreach (var b in _blocks)
            for (var i = b.Start; i < b.End; i++) blockOfStmt[i] = b.Id;
        _blockOfStmt = blockOfStmt;

        int BlockAtOffset(long off) =>
            _idxOfOff.TryGetValue(off, out var si) && si < _stmts.Count ? blockOfStmt[si] : -1;

        for (var i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            var last = _stmts[b.End - 1].Expr;
            switch (last)
            {
                case EX_Jump j:
                    b.Term = Term.Jump;
                    b.JumpOff = j.CodeOffset;
                    b.JumpSucc = BlockAtOffset(j.CodeOffset);
                    break;
                case EX_JumpIfNot j:
                    b.Term = Term.JumpIfNot;
                    b.JumpOff = j.CodeOffset;
                    b.JumpSucc = BlockAtOffset(j.CodeOffset);
                    b.FallSucc = i + 1 < _blocks.Count ? i + 1 : -1;
                    b.CondExpr = j.BooleanExpression;
                    break;
                case EX_PopExecutionFlow:
                    b.Term = Term.Jump;
                    if (_flowTargets.TryGetValue(_stmts[b.End - 1].Off, out var t1))
                    {
                        b.JumpOff = t1;
                        b.JumpSucc = BlockAtOffset(t1);
                    }
                    else b.Term = Term.Halt;
                    break;
                case EX_PopExecutionFlowIfNot p:
                    b.Term = Term.JumpIfNot;
                    b.CondExpr = p.BooleanExpression;
                    if (_flowTargets.TryGetValue(_stmts[b.End - 1].Off, out var t2))
                    {
                        b.JumpOff = t2;
                        b.JumpSucc = BlockAtOffset(t2);
                    }
                    else b.Term = Term.Halt;
                    b.FallSucc = i + 1 < _blocks.Count ? i + 1 : -1;
                    break;
                case EX_Return:
                    b.Term = Term.Return;
                    break;
                case EX_ComputedJump:
                    // ubergraph 分发器：运行期按 EntryPoint 跳转，静态没有 fallthrough 后继。
                    // 落进 default 会造出一条假的顺序边，把分发器前言和 case 体串成直线，
                    // 污染 case 入口的流栈。各 case 入口由 EmitDispatch 单独登记为可达性根。
                    b.Term = Term.Halt;
                    break;
                case EX_EndOfScript:
                    b.Term = Term.Halt;
                    break;
                default:
                    b.Term = Term.Fall;
                    b.FallSucc = i + 1 < _blocks.Count ? i + 1 : -1;
                    break;
            }

            switch (b.Term)
            {
                case Term.Jump:
                    if (b.JumpSucc >= 0) b.Succ.Add(b.JumpSucc);
                    break;
                case Term.JumpIfNot:
                    if (b.JumpSucc >= 0) b.Succ.Add(b.JumpSucc);
                    if (b.FallSucc >= 0) b.Succ.Add(b.FallSucc);
                    break;
                case Term.Fall:
                    if (b.FallSucc >= 0) b.Succ.Add(b.FallSucc);
                    break;
            }
        }

        foreach (var b in _blocks)
            foreach (var s in b.Succ)
                _blocks[s].Pred.Add(b.Id);
    }

    private Dictionary<long, long> _flowTargets = new();
    private HashSet<long> _flowUnresolved = new();
    private int[] _blockOfStmt = Array.Empty<int>();

    // ================= 3. 可达性 + 压缩 =================

    /// <param name="extraRoots">
    /// 额外的可达性根。反 Dispatch 时 ubergraph 的各 case 入口只能经 <c>ComputedJump</c> 到达，
    /// CFG 里没有这条边；不把它们当根，整个 case 体会被当成不可达删掉，switch 就空了。
    /// </param>
    private void MarkReachable(IEnumerable<int>? extraRoots = null)
    {
        var stack = new Stack<int>();
        if (_blocks.Count > 0) { _blocks[0].Reachable = true; stack.Push(0); }
        if (extraRoots is not null)
            foreach (var r in extraRoots)
                if (r >= 0 && r < _blocks.Count && !_blocks[r].Reachable)
                {
                    _blocks[r].Reachable = true;
                    stack.Push(r);
                }
        while (stack.Count > 0)
        {
            var b = _blocks[stack.Pop()];
            foreach (var s in b.Succ)
                if (!_blocks[s].Reachable) { _blocks[s].Reachable = true; stack.Push(s); }
        }
    }

    /// <summary>丢弃不可达块并重新编号（支配/循环分析只需面对活跃子图）。</summary>
    private void Compact()
    {
        var map = new int[_blocks.Count];
        var kept = new List<Block>();
        for (var i = 0; i < _blocks.Count; i++) map[i] = -1;
        foreach (var b in _blocks)
        {
            if (!b.Reachable) continue;
            map[b.Id] = kept.Count;
            b.Id = kept.Count;
            kept.Add(b);
        }
        foreach (var b in kept)
        {
            b.Succ.Clear();
            b.Pred.Clear();
        }
        _blocks = kept;
        foreach (var b in _blocks)
        {
            if (b.JumpSucc >= 0) b.JumpSucc = map[b.JumpSucc];
            if (b.FallSucc >= 0) b.FallSucc = map[b.FallSucc];
        }
        foreach (var b in _blocks)
        {
            switch (b.Term)
            {
                case Term.Jump:
                    if (b.JumpSucc >= 0) b.Succ.Add(b.JumpSucc);
                    break;
                case Term.JumpIfNot:
                    if (b.JumpSucc >= 0) b.Succ.Add(b.JumpSucc);
                    if (b.FallSucc >= 0) b.Succ.Add(b.FallSucc);
                    break;
                case Term.Fall:
                    if (b.FallSucc >= 0) b.Succ.Add(b.FallSucc);
                    break;
            }
        }
        foreach (var b in _blocks)
            foreach (var s in b.Succ)
                _blocks[s].Pred.Add(b.Id);
        for (var i = 0; i < _blockOfStmt.Length; i++)
            _blockOfStmt[i] = _blockOfStmt[i] >= 0 && _blockOfStmt[i] < map.Length ? map[_blockOfStmt[i]] : -1;
    }

    // ================= 4. 支配 / 后支配 =================

    private void ComputeDominators()
    {
        var n = _blocks.Count;
        var preds = new IReadOnlyList<int>[n];
        for (var i = 0; i < n; i++) preds[i] = _blocks[i].Pred;
        _dom = ComputeDomSets(n, preds, 0);
    }

    private void ComputePostDominators()
    {
        var n = _blocks.Count;
        var total = n + 1;                       // 虚拟出口
        var succ = new List<int>[total];
        for (var i = 0; i < total; i++) succ[i] = new List<int>();
        // 反图：b 的前驱 = 原图 b 的后继；无后继的块接到虚拟出口上。
        for (var i = 0; i < n; i++)
        {
            if (_blocks[i].Succ.Count == 0) succ[i].Add(n);
            else foreach (var s in _blocks[i].Succ) succ[i].Add(s);
        }
        succ[n].Clear();                         // 虚拟出口是反图的根，没有前驱

        _pdom = ComputeDomSets(total, succ, n);
        _ipdom = new int[n];
        for (var i = 0; i < n; i++)
        {
            var ip = ImmediateOf(_pdom, i, n);
            // 只有虚拟出口后支配它 ⇒ 各条路径都直接离开函数，没有真实汇合点。
            // 必须归一成 -1：调用方一律把「负值」当作「离开函数」，而 n 是越界的假块号。
            _ipdom[i] = ip >= n ? -1 : ip;
        }
    }

    /// <summary>迭代式支配集：dom[b] = {b} ∪ ∩ dom[preds[b]]，入口的支配集只含自身。</summary>
    private static ulong[][] ComputeDomSets(int n, IReadOnlyList<int>[] preds, int entry)
    {
        var words = (n + 63) / 64;
        var all = new ulong[words];
        for (var i = 0; i < words; i++) all[i] = ulong.MaxValue;
        if (n % 64 != 0) all[^1] = (1UL << (n % 64)) - 1;

        var dom = new ulong[n][];
        for (var i = 0; i < n; i++)
        {
            dom[i] = new ulong[words];
            if (i == entry) SetBit(dom[i], i);
            else Array.Copy(all, dom[i], words);
        }
        if (n == 0) return dom;

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var b = 0; b < n; b++)
            {
                if (b == entry) continue;
                var pl = preds[b];
                if (pl.Count == 0) continue;
                var acc = new ulong[words];
                var first = true;
                foreach (var p in pl)
                {
                    if (first) { Array.Copy(dom[p], acc, words); first = false; }
                    else for (var w = 0; w < words; w++) acc[w] &= dom[p][w];
                }
                SetBit(acc, b);
                if (!SameSet(acc, dom[b])) { dom[b] = acc; changed = true; }
            }
        }
        return dom;
    }

    /// <summary>支配树上的直接支配者 = 支配集中除自身外“支配集最大”的那个。</summary>
    private static int ImmediateOf(ulong[][] dom, int b, int virtualExit)
    {
        var best = -1;
        long bestCount = -1;
        for (var x = 0; x <= virtualExit; x++)
        {
            if (x == b || !GetBit(dom[b], x)) continue;
            var c = PopCount(dom[x]);
            if (c > bestCount) { bestCount = c; best = x; }
        }
        return best;
    }

    // ================= 5. 自然循环 =================

    private void FindLoops()
    {
        var n = _blocks.Count;
        var merged = new Dictionary<int, HashSet<int>>();
        for (var u = 0; u < n; u++)
        {
            foreach (var v in _blocks[u].Succ)
            {
                if (!GetBit(_dom[u], v)) continue;         // v 支配 u ⇒ 回边 u→v
                if (!merged.TryGetValue(v, out var set))
                    merged[v] = set = new HashSet<int> { v };
                // 自然循环：v 加上所有「不经过 v 也能到达 u」的节点
                var stack = new Stack<int>();
                stack.Push(u);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    if (!set.Add(x)) continue;
                    foreach (var p in _blocks[x].Pred)
                        if (!set.Contains(p)) stack.Push(p);
                }
            }
        }

        foreach (var (header, blocks) in merged.OrderBy(kv => kv.Key))
        {
            var loop = new Loop { Header = header, Blocks = blocks };
            foreach (var b in blocks)
                foreach (var s in _blocks[b].Succ)
                    if (!blocks.Contains(s)) loop.Exits.Add(s);
            loop.Follow = ComputeFollow(header, loop);
            _loops.Add(loop);
        }

        // 最内层循环 = 规模最小的那个
        foreach (var loop in _loops)
            foreach (var b in loop.Blocks)
            {
                if (!_innermostLoop.TryGetValue(b, out var cur)) { _innermostLoop[b] = loop.Header; continue; }
                var curSize = _loops.First(l => l.Header == cur).Blocks.Count;
                if (loop.Blocks.Count < curSize) _innermostLoop[b] = loop.Header;
            }
    }

    private int ComputeFollow(int header, Loop loop)
    {
        var ip = _ipdom.Length > header ? _ipdom[header] : -1;
        if (ip >= 0 && !loop.Blocks.Contains(ip)) return ip;
        if (loop.Exits.Count == 1) return loop.Exits.First();
        // 多出口：优先取后支配所有出口块的那个（多数情况是唯一汇合点）
        foreach (var e in loop.Exits.OrderBy(x => x))
            if (GetBit(_pdom[header], e)) return e;
        return loop.Exits.Count > 0 ? loop.Exits.OrderBy(x => x).First() : -1;
    }

    /// <summary>这条分支是不是「什么都没发射、直接离开本层循环」——那样必须写成 break。</summary>
    private bool LeavesLoopEmittingNothing(int blk, LoopCtx loop, HashSet<int> stops, int follow)
        => blk == follow
           || loop.Exits.Contains(blk)          // 边缘块：区域到它就停，else 会空掉
           || EmptyTrampolineTo(blk, follow, stops);

    /// <summary>从 blk 出发只经过「没有语句、只做无条件跳转」的块，最终是否落到 target。</summary>
    private bool EmptyTrampolineTo(int blk, int target, HashSet<int> stops)
    {
        var seen = new HashSet<int>();
        var cur = blk;
        while (cur >= 0 && cur < _blocks.Count && seen.Add(cur))
        {
            if (cur == target) return true;
            var b = _blocks[cur];
            // 只有「没有语句 + 无条件跳转」的块才能被安全跳过；其余一律认为这条路会留下代码
            if (b.Emitted || b.Start != b.End || b.Term != Term.Jump) return false;
            if (cur != blk && stops.Contains(cur)) return false;
            cur = b.JumpSucc;
        }
        return false;
    }

    private Loop LoopOfHeader(int header) => _loops.First(l => l.Header == header);

    // ================= 6. 结构化发射 =================

    private void EmitRegion(int id, HashSet<int> stops, int follow)
    {
        // 记录本次调用里新发射的块，调用结束时它们的「区域结束行」都是此刻的 _out.Count。
        // 嵌套调用先结束先记录，所以每个块拿到的是真正包住它的那一层的结束位置。
        var touched = new List<int>();
        _regionTouched.Push(touched);
        try { EmitRegionCore(id, stops, follow); }
        finally
        {
            _regionTouched.Pop();
            foreach (var bId in touched)
                if (!_regionEnd.ContainsKey(bId)) _regionEnd[bId] = _out.Count;
        }
    }

    private void EmitRegionCore(int id, HashSet<int> stops, int follow)
    {
        while (id >= 0 && id < _blocks.Count)
        {
            if (stops.Contains(id)) return;
            var b = _blocks[id];
            if (b.Emitted) return;

            if (_innermostLoop.TryGetValue(id, out var h) && h == id && !_emittedLoops.Contains(id))
            {
                id = EmitLoop(id, stops, follow);
                continue;
            }

            b.Emitted = true;
            if (!_firstLine.ContainsKey(id)) _firstLine[id] = _out.Count;
            if (_regionTouched.Count > 0) _regionTouched.Peek().Add(id);
            EmitStatements(b);

            switch (b.Term)
            {
                case Term.Return:
                    EmitLine(ReturnText(b), id);
                    return;
                case Term.Halt:
                    return;
                case Term.Jump:
                {
                    var act = ResolveJump(b, stops);
                    if (act == JumpAction.Stop) return;
                    if (act == JumpAction.Break) { EmitLine("break;"); return; }
                    if (act == JumpAction.Continue) { EmitLine("continue;"); return; }
                    if (act == JumpAction.Goto) { EmitGoto(b.JumpSucc, follow); return; }
                    id = b.JumpSucc;
                    continue;
                }
                case Term.JumpIfNot:
                    id = EmitIf(b, stops, follow);
                    continue;
                default:
                    if (b.FallSucc < 0) return;
                    id = b.FallSucc;
                    continue;
            }
        }
    }

    private JumpAction ResolveJump(Block b, HashSet<int> stops)
    {
        var t = b.JumpSucc;
        if (t < 0) return JumpAction.Stop;

        // 回到（最内层）循环头 → continue
        if (_loopStack.Count > 0 && _loopStack[^1].Header == t) return JumpAction.Continue;
        // 跳出最内层循环且目标是它的汇合点 → break
        if (_loopStack.Count > 0)
        {
            var inner = _loopStack[^1];
            if (!inner.Blocks.Contains(t) && inner.Follow == t) return JumpAction.Break;
        }
        if (stops.Contains(t)) return JumpAction.Stop;
        if (!_blocks[t].Emitted) return JumpAction.Follow;
        // 目标已发射过（非常规回边）→ 用 label + goto 保真
        return JumpAction.Goto;
    }

    private void EmitGoto(int target, int follow)
    {
        _gotoTargets.Add(target);
        _gotoFollow[_out.Count] = (target, follow);
        EmitLine($"goto L_{_blocks[target].Off:X4};");
    }

    private int EmitIf(Block b, HashSet<int> stops, int follow)
    {
        // b.Cond 为真 → 顺序后继 n；为假 → 跳转目标 t
        var t = b.JumpSucc;
        var n = b.FallSucc;
        if (t < 0 && n < 0) return -1;
        if (t < 0) { EmitIfBlock(CondText(b), () => EmitRegion(n, With(stops, -1), follow)); return -1; }
        if (n < 0) { EmitIfBlock("!" + Paren(CondText(b)), () => EmitRegion(t, With(stops, -1), follow)); return -1; }

        var cond = CondText(b);
        var f = _ipdom[b.Id];

        // ---- 汇合点就是循环汇合点：写成守卫语句 if (!cond) break; 更贴近源码 ----
        if (_loopStack.Count > 0)
        {
            var inner = _loopStack[^1];
            if (f == t && inner.Follow == t && !inner.Blocks.Contains(t))
            {
                EmitLine($"if (!{cond}) break;");
                EmitRegion(n, With(stops, t), t);
                return t;
            }
            // 出口块往往是一串「没有语句、只做无条件跳转」的空壳块（B6: Jump #092C → 循环出口）。
            // 这种分支渲染成空的 else {}，而文本层又会把空 else 删掉 —— 在循环里那就变成
            // 「条件不成立时什么都不做、回到循环头」，也就是死循环。必须写成 break。
            if (f == inner.Follow && !inner.Blocks.Contains(f))
            {
                if (LeavesLoopEmittingNothing(t, inner, stops, f))
                {
                    EmitLine($"if (!{cond}) break;");
                    EmitRegion(n, With(stops, t), t);
                    return t;
                }
                if (LeavesLoopEmittingNothing(n, inner, stops, f))
                {
                    EmitLine($"if ({cond}) break;");
                    EmitRegion(t, With(stops, n), n);
                    return n;
                }
            }
        }

        if (f == n)
        {
            EmitIfBlock("!" + Paren(cond), () => EmitRegion(t, With(stops, n), n));
            return n;
        }
        if (f == t)
        {
            EmitIfBlock(cond, () => EmitRegion(n, With(stops, t), t));
            return t;
        }
        if (f >= 0)
        {
            EmitIfElse(cond,
                () => EmitRegion(n, With(stops, f), f),
                () => EmitRegion(t, With(stops, f), f));
            return f;
        }

        // 两侧都直接离开函数：若两棵子树不重叠，用 if-then 后接另一支更贴近源码
        var both = With(stops, -1);
        if (RegionsDisjoint(t, n, both))
        {
            EmitIfBlock(cond, () => EmitRegion(n, both, t));
            EmitRegion(t, both, -1);
            return -1;
        }
        EmitIfElse(cond,
            () => EmitRegion(n, both, -1),
            () => EmitRegion(t, both, -1));
        return -1;
    }

    /// <summary>条件表达式文本；首次调用时把其中的多行子结构（大 switch）以语句形式发射出来。</summary>
    private string CondText(Block b, bool emitPrelude = true)
    {
        if (b.CondCache is null)
        {
            _renderer.BeginHoist();
            b.CondCache = b.CondExpr is null ? "" : _renderer.Render(b.CondExpr);
            b.CondPrelude = _renderer.TakeHoisted();
        }
        if (emitPrelude)
            foreach (var pre in b.CondPrelude) EmitLine(pre, b.Id);
        return b.CondCache;
    }

    private void EmitIfBlock(string cond, Action body)
    {
        EmitLine($"if ({cond})");
        EmitLine("{");
        _ind++;
        body();
        _ind--;
        EmitLine("}");
    }

    private void EmitIfElse(string cond, Action thenBody, Action elseBody)
    {
        EmitLine($"if ({cond})");
        EmitLine("{");
        _ind++;
        thenBody();
        _ind--;
        EmitLine("}");
        EmitLine("else");
        EmitLine("{");
        _ind++;
        elseBody();
        _ind--;
        EmitLine("}");
    }

    private int EmitLoop(int header, HashSet<int> stops, int outerFollow)
    {
        var loop = LoopOfHeader(header);
        _emittedLoops.Add(header);
        var follow = loop.Follow;

        EmitLine("while (true)");
        EmitLine("{");
        _ind++;
        var bodyStops = new HashSet<int>(stops);
        bodyStops.UnionWith(loop.Exits);
        _loopStack.Add(new LoopCtx { Header = header, Blocks = loop.Blocks, Exits = loop.Exits, Follow = follow });
        EmitRegion(header, bodyStops, header);
        _loopStack.RemoveAt(_loopStack.Count - 1);
        _ind--;
        EmitLine("}");
        return follow >= 0 && follow < _blocks.Count && !_blocks[follow].Emitted ? follow : -1;
    }

    /// <summary>两棵子树是否可能共享块（用于 F=虚拟出口 时选择 if-then 还是 if-else）。</summary>
    private bool RegionsDisjoint(int a, int b, HashSet<int> stops)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(a);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (x < 0 || x >= _blocks.Count || !seen.Add(x) || stops.Contains(x)) continue;
            foreach (var s in _blocks[x].Succ) stack.Push(s);
        }
        stack.Push(b);
        var seen2 = new HashSet<int>();
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (x < 0 || x >= _blocks.Count || !seen2.Add(x) || stops.Contains(x)) continue;
            if (seen.Contains(x)) return false;
            foreach (var s in _blocks[x].Succ) stack.Push(s);
        }
        return true;
    }

    private static HashSet<int> With(HashSet<int> set, int add)
    {
        var copy = new HashSet<int>(set);
        if (add >= 0) copy.Add(add);
        return copy;
    }

    // ================= 语句 / 文本 =================

    private void EmitStatements(Block b)
    {
        _firstLine.TryAdd(b.Id, _out.Count);
        for (var i = b.Start; i < b.End; i++)
        {
            var e = _stmts[i].Expr;
            if (e is EX_Jump or EX_JumpIfNot or EX_Return or EX_EndOfScript
                or EX_PushExecutionFlow or EX_PopExecutionFlow or EX_PopExecutionFlowIfNot)
                continue;
            _renderer.BeginHoist();
            var expanded = _renderer.TryExpandSwitchAssignment(e);
            if (expanded is not null)
            {
                foreach (var pre in _renderer.TakeHoisted()) EmitLine(pre, b.Id);
                foreach (var l in expanded) EmitLine(l, b.Id);
                continue;
            }
            if (_renderer.TryRenderOutCallAssign(e, out var outCall))
            {
                foreach (var pre in _renderer.TakeHoisted()) EmitLine(pre, b.Id);
                EmitLine(outCall + ";", b.Id);
                continue;
            }
            var line = _renderer.Render(e);
            foreach (var pre in _renderer.TakeHoisted()) EmitLine(pre, b.Id);
            if (string.IsNullOrEmpty(line)) continue;
            EmitLine(line + ";", b.Id);
        }
    }

    private string ReturnText(Block b)
    {
        var r = (EX_Return)_stmts[b.End - 1].Expr;
        if (r.ReturnExpression is EX_Nothing) return "return;";
        _renderer.BeginHoist();
        var t = _renderer.Render(r.ReturnExpression);
        foreach (var pre in _renderer.TakeHoisted()) EmitLine(pre, b.Id);
        return $"return {t};";
    }

    private void EmitLine(string s, int blockId = -1)
    {
        _out.Add(new string(' ', _ind * 4) + _renderer.CleanSelf(s));
        _outBlock.Add(blockId);
        if (blockId >= 0 && !_firstLine.ContainsKey(blockId)) _firstLine[blockId] = _out.Count - 1;
    }

    /// <summary>把被 goto 引用却未被主遍历发射的块补到末尾，并给所有 goto 目标插入 label。</summary>
    private void EmitPendingGotoTargets()
    {
        // 这些块是「没人发射过、只能补在函数末尾」的 goto 目标。它们首尾相接排在一起，
        // 所以每块的区域就是从它的首行到下一块的首行（最后一块到函数末尾）—— 这正是
        // goto 尾部复制需要的精确范围。
        var emittedHere = new List<int>();
        var guard = 0;
        while (guard++ < 64)
        {
            var pending = _gotoTargets.Where(t => !_blocks[t].Emitted).OrderBy(t => t).ToList();
            if (pending.Count == 0) break;
            foreach (var t in pending)
            {
                var b = _blocks[t];
                if (b.Emitted) continue;
                b.Emitted = true;
                _firstLine[t] = _out.Count;
                emittedHere.Add(t);
                EmitStatements(b);
                switch (b.Term)
                {
                    case Term.Return: EmitLine(ReturnText(b), t); break;
                    case Term.Jump:
                        if (b.JumpSucc >= 0) EmitGoto(b.JumpSucc, -1);
                        break;
                    case Term.JumpIfNot:
                        EmitLine($"if (!{CondText(b)})");
                        EmitLine("{");
                        _ind++;
                        if (b.JumpSucc >= 0) EmitGoto(b.JumpSucc, b.FallSucc);
                        _ind--;
                        EmitLine("}");
                        if (b.FallSucc >= 0 && !_blocks[b.FallSucc].Emitted) EmitGoto(b.FallSucc, -1);
                        break;
                }
            }
        }
        for (var i = 0; i < emittedHere.Count; i++)
        {
            var end = i + 1 < emittedHere.Count ? _firstLine[emittedHere[i + 1]] : _out.Count;
            if (!_regionEnd.ContainsKey(emittedHere[i])) _regionEnd[emittedHere[i]] = end;
        }
    }

    /// <summary>插入 label、按实际使用过滤局部变量声明，并做可读性后处理。</summary>
    private static int IndentOf(string s) => s.Trim().Length == 0 ? -1 : s.Length - s.TrimStart(' ').Length;

    private string Assemble()
    {
        var final = new List<string>(_out.Count + _gotoTargets.Count);
        string LabelText(int t, int at)
        {
            // 标签与它后面的语句同缩进（0 缩进会让 goto 尾部复制等文本级改写无法判断层次）
            var ind = at >= 0 && at < _out.Count ? _out[at].Length - _out[at].TrimStart(' ').Length : 0;
            return new string(' ', ind) + $"L_{_blocks[t].Off:X4}:";
        }
        for (var i = 0; i < _out.Count; i++)
        {
            foreach (var t in _gotoTargets.Where(t => _firstLine.GetValueOrDefault(t, -1) == i).OrderBy(t => _blocks[t].Off))
                final.Add(LabelText(t, i));
            final.Add(_out[i]);
        }
        foreach (var t in _gotoTargets.Where(t => _firstLine.GetValueOrDefault(t, -1) >= _out.Count).OrderBy(t => _blocks[t].Off))
            final.Add(LabelText(t, _out.Count - 1));

        // 计算「goto 尾部复制」方案。这一步在 CFG 层判定，因为有两条硬条件只有这里知道：
        //   1) 该 goto 所在区域的续接块必须等于目标块的直接后支配点 —— 复制出来的尾巴执行完
        //      落到 target 的后续，而 goto 掉出去落到所在区域的后续，两者必须是同一个块，
        //      否则会重复执行或少执行中间的语句；
        //   2) 复制的行范围必须正好是「从目标标签开始到该区域结束」这段 —— 由 _regionEnd 给出，
        //      绝不能多抄（多抄的代码原路径根本不会执行）。
        // 键是 final 里的行号（在规则改写之前算好，规则改写在复制之后进行）。
        var outToFinal = new int[_out.Count];
        {
            var fi = 0;
            for (var i = 0; i < _out.Count; i++)
            {
                foreach (var _ in _gotoTargets.Where(t => _firstLine.GetValueOrDefault(t, -1) == i)) fi++;
                outToFinal[i] = fi;
                fi++;
            }
        }
        var dupPlan = new Dictionary<int, (int Start, int End, int Shift)>();
        var dupCount = new Dictionary<int, int>();
        var dupBudget = 900;
        foreach (var kv in _gotoFollow.OrderByDescending(kv => _regionEnd.GetValueOrDefault(kv.Value.Target, 0) - _firstLine.GetValueOrDefault(kv.Value.Target, 0)))
        {
            var gotoOut = kv.Key;
            var (tgt, follow) = kv.Value;
            if (tgt < 0 || tgt >= _blocks.Count) continue;
            if (_ipdom.Length <= tgt || _ipdom[tgt] != follow) { continue; }      // 续接点不一致
            if (!_firstLine.TryGetValue(tgt, out var tStart)) continue;
            if (!_regionEnd.TryGetValue(tgt, out var tEnd) || tEnd <= tStart) continue;
            var cost = tEnd - tStart;
            if (cost > dupBudget) continue;
            dupCount.TryGetValue(tgt, out var n);
            if (n >= 16) continue;

            var gInd = IndentOf(_out[gotoOut]);
            var tInd = IndentOf(_out[tStart]);
            if (gInd < 0 || tInd < 0) continue;

            dupBudget -= cost;
            dupCount[tgt] = n + 1;
            dupPlan[outToFinal[gotoOut]] = (outToFinal[tStart], outToFinal[tEnd - 1] + 1, gInd - tInd);
        }

        var text = ReadabilityPasses.Run(string.Join("\n", final), dupPlan);

        // 可读性改写可能又消掉了一批临时量（如前导赋值折进循环条件），最后再过滤一次声明。
        if (_declInsertAt >= 0 && _locals.Count > 0 && _declInsertAt <= text.Length)
        {
            var lines = text.Split('\n').ToList();
            if (_declInsertAt < lines.Count)
            {
                var used = FilterUnusedLocals(lines);
                if (used.Count > 0)
                {
                    var pad = new string(' ', _declIndent * 4);
                    lines.InsertRange(_declInsertAt, used.Select(l => pad + l).Append(""));
                    text = string.Join("\n", lines);
                }
            }
        }
        return text + "\n";
    }

    /// <summary>只保留函数体里真正出现过的局部变量声明（死代码消除后很多临时量已无引用）。</summary>
    private List<string> FilterUnusedLocals(List<string> body)
    {
        var usedTypes = new Dictionary<string, List<string>>();
        var order = new List<string>();
        foreach (var line in _locals)
        {
            var semi = line.IndexOf(';', StringComparison.Ordinal);
            var head = semi < 0 ? line : line[..semi];
            var sp = head.IndexOf(' ');
            if (sp <= 0) continue;
            var type = head[..sp];
            var names = head[(sp + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var n in names)
            {
                if (!Mentioned(body, n)) continue;
                if (!usedTypes.TryGetValue(type, out var list)) { usedTypes[type] = list = new List<string>(); order.Add(type); }
                list.Add(n);
            }
        }
        return order.OrderBy(t => t, StringComparer.Ordinal)
                    .Select(t => $"{t} {string.Join(", ", usedTypes[t])};").ToList();
    }

    private static bool Mentioned(List<string> body, string name)
    {
        foreach (var line in body)
        {
            var from = 0;
            while (true)
            {
                var idx = line.IndexOf(name, from, StringComparison.Ordinal);
                if (idx < 0) break;
                var before = idx == 0 || (!char.IsLetterOrDigit(line[idx - 1]) && line[idx - 1] != '_');
                var end = idx + name.Length;
                var after = end >= line.Length || (!char.IsLetterOrDigit(line[end]) && line[end] != '_');
                if (before && after) return true;
                from = idx + 1;
            }
        }
        return false;
    }

    private static string Paren(string s) => s.StartsWith('(') && s.EndsWith(')') ? s : "(" + s + ")";

    // ================= 位集工具 =================

    private static void SetBit(ulong[] s, int i) => s[i >> 6] |= 1UL << (i & 63);
    private static bool GetBit(ulong[] s, int i) => (s[i >> 6] & (1UL << (i & 63))) != 0;

    private static bool SameSet(ulong[] a, ulong[] b)
    {
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static int PopCount(ulong[] s)
    {
        var c = 0;
        foreach (var w in s) c += System.Numerics.BitOperations.PopCount(w);
        return c;
    }

    private static IEnumerable<int> Bits(ulong[] s)
    {
        for (var i = 0; i < s.Length * 64; i++)
            if (GetBit(s, i)) yield return i;
    }
}
