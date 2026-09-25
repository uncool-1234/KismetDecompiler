using System.Reflection;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetKismet;

namespace UAssetKismet.Experimental;

/// <summary>
/// 开发诊断用：把函数的 <c>ScriptBytecode</c> 转储为「偏移 / 类型 / 渲染文本」表，
/// 并可标注执行流栈的静态解析结果，用于核对 CFG 结构与渲染器行为。生产路径不依赖本文件。
/// </summary>
public static class BytecodeDisassembler
{
    private sealed class Stmt
    {
        public long Off;
        public int Size;
        public KismetExpression Expr = null!;
    }

    private static List<Stmt> Build(UAsset asset, FunctionExport fn)
    {
        var list = new List<Stmt>();
        long pos = 0;
        foreach (var e in fn.ScriptBytecode ?? Array.Empty<KismetExpression>())
        {
            var size = 1;
            try { size = (int)e.GetSize(asset); } catch { }
            list.Add(new Stmt { Off = pos, Size = size, Expr = e });
            pos += size;
        }
        return list;
    }

    /// <summary>转储单个函数（偏移、字节长度、渲染文本、跳转目标）。</summary>
    public static string DumpFunction(UAsset asset, FunctionExport fn, UhtSignatureIndex? sigs = null)
    {
        var renderer = new ExprRenderer(asset, sigs);
        var stmts = Build(asset, fn);
        var flow = FlowStackResolver.Resolve(stmts.Select(s => (s.Off, s.Expr)).ToList());

        var sb = new StringBuilder();
        sb.Append("FUNCTION ").Append(fn.ObjectName).AppendLine();
        sb.AppendLine("  off   size  opcode               rendered / flow-target");
        sb.AppendLine("  ----  ----  -------------------  ------------------------------------------");
        foreach (var s in stmts)
        {
            var flowNote = "";
            if (s.Expr is EX_PushExecutionFlow push)
                flowNote = $"   ; push #{push.PushingAddress:X4}";
            else if (flow.TargetOf.TryGetValue(s.Off, out var tgt))
                flowNote = $"   ; pop -> #{tgt:X4}";
            else if (flow.Unresolved.Contains(s.Off))
                flowNote = "   ; pop -> ???";

            string text;
            try { text = renderer.Render(s.Expr); } catch (Exception ex) { text = "<render error: " + ex.Message + ">"; }
            var jump = s.Expr switch
            {
                EX_Jump j => $"   ; -> #{j.CodeOffset:X4}",
                EX_JumpIfNot j => $"   ; false -> #{j.CodeOffset:X4}",
                _ => ""
            };
            var stackNote = "";
            if (flow.BlockStacks.TryGetValue(s.Off, out var st))
                stackNote = st is null ? "  [stack=?]" : st.Length == 0 ? "" : "  [stack=" + string.Join(",", st.Select(x => x.ToString("X4"))) + "]";
            sb.Append("  ").Append(s.Off.ToString("X4")).Append("  ").Append(s.Size.ToString("D4")).Append("  ")
              .Append(Pad(Opcode(s.Expr), 19)).Append("  ").Append(Pad(text, 42))
              .Append(flowNote).Append(jump).Append(stackNote).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>转储资产内全部含字节码函数。</summary>
    public static string DumpAsset(UAsset asset, UhtSignatureIndex? sigs = null)
    {
        var sb = new StringBuilder();
        foreach (var fn in asset.Exports.OfType<FunctionExport>())
        {
            if (fn.ScriptBytecode is not { Length: > 0 }) continue;
            sb.AppendLine(DumpFunction(asset, fn, sigs));
        }
        return sb.ToString();
    }

    /// <summary>转储执行流栈冲突日志（诊断用）。</summary>
    public static string DumpFlowConflicts(UAsset asset, FunctionExport fn)
    {
        var stmts = Build(asset, fn);
        var flow = FlowStackResolver.Resolve(stmts.Select(s => (s.Off, s.Expr)).ToList());
        var sb = new StringBuilder();
        sb.Append("FUNCTION ").Append(fn.ObjectName)
          .Append("  conflicts=").Append(flow.ConflictBlocks)
          .Append(" unresolved=").Append(flow.Unresolved.Count).AppendLine();
        foreach (var line in flow.ConflictLog) sb.Append("  ").AppendLine(line);
        return sb.ToString();
    }

    private static string Pad(string s, int n) => s.Length >= n ? s : s.PadRight(n);

    private static string Opcode(KismetExpression e) => e.GetType().Name.StartsWith("EX_")
        ? e.GetType().Name[3..]
        : e.GetType().Name;
}
