using System.Text;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetKismet.Experimental;

namespace UAssetKismet;

/// <summary>把单个 KismetExpression 渲染为伪代码字符串（可内嵌，含名称解析）。</summary>
public class ExprRenderer
{
    private static readonly Regex SelfMemberRegex = new(@"\.self\.", RegexOptions.Compiled);
    private readonly UAsset _asset;
    private readonly UhtSignatureIndex? _sigs;

    /// <summary>多行子表达式（大 switch）的提升缓冲：语句发射前统一落到当前语句之前。</summary>
    private List<string>? _hoist;
    private int _hoistSeq;

    private delegate bool FactionProbe(KismetExpression e, out string name);

    /// <summary>开始一次语句渲染；期间产生的提升语句可由 <see cref="TakeHoisted"/> 取走。</summary>
    public void BeginHoist() => _hoist = new List<string>();

    /// <summary>取走本次渲染产生的提升语句（按依赖顺序）。</summary>
    public List<string> TakeHoisted()
    {
        var h = _hoist ?? new List<string>();
        _hoist = null;
        return h;
    }

    public ExprRenderer(UAsset asset, UhtSignatureIndex? signatures = null)
    {
        _asset = asset;
        _sigs = signatures;
    }

    public string CleanSelf(string s) => SelfMemberRegex.Replace(s, ".");

    public string Render(KismetExpression? e)
    {
        if (e is null) return "null";
        switch (e)
        {
            // 控制流（独立语句，父级处理）
            case EX_Jump j: return $"goto L_{j.CodeOffset:X4}";
            case EX_JumpIfNot j: return $"if (!{Render(j.BooleanExpression)}) goto L_{j.CodeOffset:X4}";
            case EX_ComputedJump j: return $"// switch ({Render(j.CodeOffsetExpression)})";
            case EX_Return r: return r.ReturnExpression is EX_Nothing ? "return" : $"return {Render(r.ReturnExpression)}";
            case EX_PushExecutionFlow: return "";
            case EX_PopExecutionFlow: return "";
            case EX_PopExecutionFlowIfNot p: return $"if (!{Render(p.BooleanExpression)}) break";
            case EX_Assert a: return $"assert({Render(a.AssertExpression)})";
            case EX_EndOfScript: return "";

            // 赋值
            case EX_Let l: return $"{Render(l.Variable)} = {Render(l.Expression)}";
            case EX_LetBool l: return $"{Render(l.VariableExpression)} = {Render(l.AssignmentExpression)}";
            case EX_LetBase l: return $"{Render(l.VariableExpression)} = {Render(l.AssignmentExpression)}";
            case EX_LetValueOnPersistentFrame l:
                return $"{RenderVariablePath(l.DestinationProperty)} = {Render(l.AssignmentExpression)}";

            // 更多赋值/委托形式（EX_LetObj / EX_LetWeakObjPtr / EX_LetDelegate / EX_LetMulticastDelegate
            // 都派生自 EX_LetBase，由上面的 EX_LetBase 分支统一处理）
            case EX_AddMulticastDelegate l: return $"{Render(l.Delegate)}.Add({Render(l.DelegateToAdd)})";
            case EX_RemoveMulticastDelegate l: return $"{Render(l.Delegate)}.Remove({Render(l.DelegateToAdd)})";
            case EX_ClearMulticastDelegate l: return $"{Render(l.DelegateToClear)}.Clear()";
            case EX_BindDelegate b: return $"{Render(b.Delegate)}.BindUFunction({Render(b.ObjectTerm)}, {Quote(b.FunctionName.ToString())})";
            case EX_CallMulticastDelegate c: return $"{Render(c.Delegate)}.{ResolveStackNode(c.StackNode)}({RenderParams(c.Parameters)})";
            case EX_ArrayGetByRef a: return $"{Render(a.ArrayVariable)}[{Render(a.ArrayIndex)}]";
            case EX_SetSet s: return $"{Render(s.SetProperty)} = {{ {string.Join(", ", (s.Elements ?? Array.Empty<KismetExpression>()).Select(Render))} }}";
            case EX_SetMap m: return $"{Render(m.MapProperty)} = {{ {string.Join(", ", (m.Elements ?? Array.Empty<KismetExpression>()).Select(Render))} }}";
            case EX_InterfaceContext ic: return Render(ic.InterfaceValue);
            case EX_MetaCast mc: return $"Cast<{ResolveObjectName(mc.ClassPtr)}>({Render(mc.Target)})";
            case EX_ObjToInterfaceCast ic: return $"Cast<{ResolveObjectName(ic.ClassPtr)}>({Render(ic.Target)})";
            case EX_CrossInterfaceCast ic: return $"Cast<{ResolveObjectName(ic.ClassPtr)}>({Render(ic.Target)})";
            case EX_InterfaceToObjCast ic: return $"Cast<{ResolveObjectName(ic.ClassPtr)}>({Render(ic.Target)})";
            case EX_Vector3fConst v: return $"({FormatFloat(v.X)}, {FormatFloat(v.Y)}, {FormatFloat(v.Z)})";
            case EX_IntConstByte b: return b.Value.ToString();
            case EX_PropertyConst pc: return RenderVariablePath(pc.Property);
            case EX_BitFieldConst bf: return $"{RenderVariablePath(bf.Property)}:{bf.Value}";

            // 调试/埋点指令：不产生用户可见代码
            case EX_Breakpoint:
            case EX_Tracepoint:
            case EX_WireTracepoint:
            case EX_InstrumentationEvent:
            case EX_DeprecatedOp4A: return "";
            // 复合结构 / 常量
            case EX_SwitchValue s: return RenderSwitch(s);
            case EX_StructMemberContext smc:
                return $"{Render(smc.StructExpression)}.{RenderVariablePath(smc.StructMemberExpression)}";
            case EX_ObjectConst oc: return ResolveObjectName(oc.Value);
            case EX_ArrayConst ac:
                return "[" + string.Join(", ", (ac.Elements ?? Array.Empty<KismetExpression>()).Select(Render)) + "]";
            case EX_StructConst sc:
                return "{" + string.Join(", ", (sc.Value ?? Array.Empty<KismetExpression>()).Select(Render)) + "}";
            case EX_SetArray sa:
                return $"{Render(sa.AssigningProperty)} = {{ {string.Join(", ", (sa.Elements ?? Array.Empty<KismetExpression>()).Select(Render))} }}";
            case EX_DynamicCast dc: return $"Cast<{ResolveObjectName(dc.ClassPtr)}>({Render(dc.Target)})";
            case EX_PrimitiveCast pc: return RenderPrimitiveCast(pc);

            // 函数调用（子类在前）
            case EX_CallMath f: return RenderOperator(f.StackNode, f.Parameters) ?? RenderCall(f.StackNode, f.Parameters);
            case EX_FinalFunction f: return RenderOperator(f.StackNode, f.Parameters) ?? RenderCall(f.StackNode, f.Parameters);
            case EX_LocalVirtualFunction f: return $"{Ident(f.VirtualFunctionName.ToString())}({RenderParams(f.Parameters)})";
            case EX_VirtualFunction f: return $"{Ident(f.VirtualFunctionName.ToString())}({RenderParams(f.Parameters)})";
            case EX_InstanceDelegate d: return d.FunctionName.ToString();

            // 上下文调用（子类在前）
            case EX_Context_FailSilent ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";
            case EX_ClassContext ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";
            case EX_Context ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";

            // 字面量
            case EX_Self: return "self";
            case EX_NoObject: return "null";
            case EX_NoInterface: return "null";
            case EX_True: return "true";
            case EX_False: return "false";
            case EX_IntZero: return "0";
            case EX_IntOne: return "1";
            case EX_Nothing: return "None";
            case EX_EndStructConst: return "";
            case EX_FloatConst fc: return FormatFloat(fc.Value);
            case EX_DoubleConst dc: return FormatDouble(dc.Value);
            case EX_StringConst sc: return Quote(sc.Value ?? "");
            case EX_UnicodeStringConst uc: return Quote(uc.Value ?? "");
            case EX_NameConst nc: return Quote(nc.Value.ToString());
            case EX_TextConst tc: return RenderText(tc.Value);
            case EX_IntConst ic: return ic.Value.ToString();
            case EX_Int64Const i64: return i64.Value.ToString();
            case EX_UInt64Const u64: return u64.Value.ToString();
            case EX_ByteConst bc: return bc.Value.ToString();
            case EX_SkipOffsetConst sk: return $"L_{sk.Value:X4}";
            case EX_VectorConst vc: return $"({FormatDouble(vc.Value.X)}, {FormatDouble(vc.Value.Y)}, {FormatDouble(vc.Value.Z)})";
            case EX_RotationConst rc: return $"({FormatDouble(rc.Value.Pitch)}, {FormatDouble(rc.Value.Yaw)}, {FormatDouble(rc.Value.Roll)})";

            default:
                if (e is EX_VariableBase varBase)
                {
                    var name = RenderVariablePath(varBase.Variable);
                    return e is EX_InstanceVariable or EX_DefaultVariable ? $"self.{name}" : name;
                }
                var raw = e.GetType().GetField("RawValue")?.GetValue(e);
                // KismetExpression<T> 的 RawValue 可能直接就是内层表达式（如 EX_SoftObjectConst），
                // 直接 ToString 只会得到类型名，这里递归渲染内层。
                if (raw is KismetExpression inner) return Render(inner);
                if (raw is KismetExpression[] inners) return "[" + string.Join(", ", inners.Select(Render)) + "]";
                if (raw is not null) return FormatRaw(raw);
                return DumpInline(e);
        }
    }

    /// <summary>EX_TextConst：按 EBlueprintTextLiteralType 还原成文本字面量。</summary>
    private string RenderText(FScriptText t) => t.TextLiteralType switch
    {
        EBlueprintTextLiteralType.Empty => "\"\"",
        EBlueprintTextLiteralType.LocalizedText =>
            $"NSLOCTEXT({Render(t.LocalizedNamespace)}, {Render(t.LocalizedKey)}, {Render(t.LocalizedSource)})",
        EBlueprintTextLiteralType.InvariantText => Render(t.InvariantLiteralString),
        EBlueprintTextLiteralType.LiteralString => Render(t.LiteralString),
        EBlueprintTextLiteralType.StringTableEntry =>
            $"LOCTABLE({ResolveObjectName(t.StringTableAsset)}, {Render(t.StringTableId)}, {Render(t.StringTableKey)})",
        _ => "\"\""
    };

    /// <summary>
    /// EX_PrimitiveCast（动态转换的类型检查部分）。UE 的 ECastToken 只有三种：
    /// ObjectToBool / InterfaceToBool（即 `x != nullptr`）与 ObjectToInterface。
    /// 不同引擎版本编码不一致，这里同时接受 0/1/2 与 0x46/0x47/0x49 两套。
    /// </summary>
    private string RenderPrimitiveCast(EX_PrimitiveCast pc)
    {
        var token = (byte)pc.ConversionType;
        var target = Render(pc.Target);
        // 3/4 是蓝图数值隐式转换（已用 UHT 签名交叉验证：3 = double→float，4 = float→double）。
        if (token is 3) return $"(float){target}";
        if (token is 4) return $"(double){target}";
        if (token is not (70 or 0 or 71 or 1 or 73 or 2))
        {
            var hint = ExpectedArgType();
            if (hint is not null)
            {
                var t = hint.Replace("const ", "").TrimEnd('&', '*', ' ');
                if (t.Length > 0) return $"({t}){target}";
            }
        }
        return token switch
        {
            70 or 0 => $"InterfaceCast({target})",
            71 or 1 or 73 or 2 => $"({target} != None)",
            _ => $"(cast{token}){target}"
        };
    }

    /// <summary>把任意 FName 收敛成合法 C++ 标识符（蓝图里存在带空格的函数名）。</summary>
    public static string Ident(string s)
    {
        if (s.Length == 0) return "_";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string FormatFloat(float f) =>
        float.IsNaN(f) ? "NaN" : float.IsPositiveInfinity(f) ? "INFINITY"
        : float.IsNegativeInfinity(f) ? "-INFINITY" : f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "f";

    private static string FormatDouble(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "INFINITY"
        : double.IsNegativeInfinity(d) ? "-INFINITY" : d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string FormatRaw(object raw) => raw switch
    {
        float f => f.ToString("0.######"),
        bool b => b ? "true" : "false",
        FName n => n.ToString(),
        FString s => $"\"{s.Value}\"",
        string s => $"\"{s}\"",
        _ => raw.ToString() ?? "null"
    };

    /// <summary>EFactionEnum 值表（KardsCore/Public/EFactionEnum.h），用于给 switch 的 case 标注国家名。</summary>
    private static readonly string[] FactionEnumNames =
    {
        "NotAvailable", "Germany", "Britain", "Japan", "Soviet", "USA", "France",
        "Italy", "Poland", "Finland", "Anzac", "Allies", "Neutral"
    };

    /// <summary>
    /// EX_SwitchValue（蓝图 Select / switch 节点）→ 表达式。
    /// 分支少时写成三元链（单行、可嵌进实参）；分支多时提升为一个 <c>switch</c> 语句块 + 临时量，
    /// 避免多行结构挤在表达式中间破坏缩进。
    /// </summary>
    private string RenderSwitch(EX_SwitchValue s)
    {
        var cases = s.Cases ?? Array.Empty<FKismetSwitchCase>();
        var hasDefault = s.DefaultTerm is not null and not EX_Nothing;
        var index = Render(s.IndexTerm);

        if (cases.Length <= MaxTernaryCases)
        {
            var parts = new List<string>(cases.Length + 1);
            foreach (var c in cases)
                parts.Add($"{index} == {Render(c.CaseIndexValueTerm)} ? {Wrap(c.CaseTerm)}");
            parts.Add(hasDefault ? Wrap(s.DefaultTerm!) : "None");
            return "(" + string.Join(" : ", parts) + ")";
        }

        var tmp = HoistSwitch(index, cases, hasDefault ? s.DefaultTerm : null, TryFactionName);
        return tmp;
    }

    /// <summary>
    /// 赋值的右值是分支很多的 switch 时，展开为直接给左值赋值的 switch 语句（省掉临时量）。
    /// 返回 null 表示不适用，调用方按普通语句渲染。
    /// </summary>
    public List<string>? TryExpandSwitchAssignment(KismetExpression e)
    {
        var (target, rhs) = e switch
        {
            EX_Let l => (Render(l.Variable), l.Expression),
            EX_LetBool l => (Render(l.VariableExpression), l.AssignmentExpression),
            EX_LetBase l => (Render(l.VariableExpression), l.AssignmentExpression),
            _ => (null, null)
        };
        if (target is null || rhs is not EX_SwitchValue s) return null;
        var cases = s.Cases ?? Array.Empty<FKismetSwitchCase>();
        if (cases.Length <= MaxTernaryCases) return null;
        if (target.Length == 0) return null;

        var index = Render(s.IndexTerm);
        var lines = new List<string> { $"switch ({index})", "{" };
        foreach (var c in cases)
            lines.Add($"    case {Render(c.CaseIndexValueTerm)}: {target} = {Render(c.CaseTerm)}; break;");
        if (s.DefaultTerm is not null and not EX_Nothing)
            lines.Add($"    default: {target} = {Render(s.DefaultTerm)}; break;");
        lines.Add("}");
        return lines;
    }

    /// <summary>把 switch 提升为语句块写入 hoist 缓冲，返回临时量名。</summary>
    private string HoistSwitch(string index, FKismetSwitchCase[] cases, KismetExpression? def,
        FactionProbe factionProbe)
    {
        _hoist ??= new List<string>();
        var tmp = $"select_{++_hoistSeq}";
        var annotate = index.Contains("faction", StringComparison.OrdinalIgnoreCase)
                       || index.Contains("nation", StringComparison.OrdinalIgnoreCase);
        _hoist.Add($"auto {tmp};");
        _hoist.Add($"switch ({index})");
        _hoist.Add("{");
        foreach (var c in cases)
        {
            var note = annotate && factionProbe(c.CaseIndexValueTerm, out var fn) ? $" /* {fn} */" : "";
            _hoist.Add($"    case {Render(c.CaseIndexValueTerm)}{note}: {tmp} = {Render(c.CaseTerm)}; break;");
        }
        if (def is not null) _hoist.Add($"    default: {tmp} = {Render(def)}; break;");
        _hoist.Add("}");
        return tmp;
    }

    /// <summary>expr 含顶层三元/逗号时加括号，保证嵌进三元链或实参后语义不变。</summary>
    private string Wrap(KismetExpression e)
    {
        var t = Render(e);
        if (t.Length == 0) return "None";
        if (NeedsWrap(t)) return "(" + t + ")";
        return t;
    }

    private static bool NeedsWrap(string t)
    {
        var depth = 0;
        foreach (var c in t)
        {
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && (c == '?' || c == ',')) return true;
        }
        return false;
    }

    private const int MaxTernaryCases = 3;

    private static bool TryFactionName(KismetExpression e, out string name)
    {
        name = "";
        var t = e.GetType();
        var raw = t.GetField("RawValue")?.GetValue(e) ?? t.GetProperty("Value")?.GetValue(e);
        int? v = raw switch { byte b => b, sbyte sb => sb, int i => i, long l => (int)l, _ => null };
        if (v is null || v < 0 || v >= FactionEnumNames.Length) return false;
        name = FactionEnumNames[v.Value];
        return true;
    }

    /// <summary>FPackageIndex → 对象名（export 优先，其次 import）。<c>Default__X</c>（CDO）去掉前缀更易读。</summary>
    public string ResolveObjectName(FPackageIndex? idx)
    {
        if (idx is null) return "null";
        if (idx.Index > 0 && idx.Index - 1 < _asset.Exports.Count)
            return StripCdo(_asset.Exports[idx.Index - 1].ObjectName.ToString());
        if (idx.Index < 0 && -idx.Index - 1 < _asset.Imports.Count)
            return StripCdo(_asset.Imports[-idx.Index - 1].ObjectName.ToString());
        return idx.Index == 0 ? "None" : $"<obj:{idx.Index}>";
    }

    /// <summary>对象名末尾的 <c>_C</c>（蓝图类）与 <c>Default__</c>（CDO）前缀去掉。</summary>
    private static string StripCdo(string name)
    {
        if (name.StartsWith("Default__", StringComparison.Ordinal)) name = name.Substring("Default__".Length);
        if (name.EndsWith("_C", StringComparison.Ordinal) && name.Length > 2) name = name[..^2];
        return name;
    }

    public string RenderVariablePath(KismetPropertyPointer? kp)
    {
        if (kp is null) return "?";
        if (kp.New?.Path is { Length: > 0 } path)
            return string.Join(".", path.Select(p => p.ToString()));
        if (kp.Old.Index > 0)
            return _asset.Exports[kp.Old.Index - 1].ObjectName.ToString();
        return "?";
    }

    public string RenderParams(KismetExpression[]? ps)
    {
        if (ps is null || ps.Length == 0) return "";
        return string.Join(", ", ps.Select(Render));
    }

    public string ResolveStackNode(FPackageIndex idx)
    {
        if (idx.Index > 0 && idx.Index - 1 < _asset.Exports.Count)
            return Ident(StripCdo(_asset.Exports[idx.Index - 1].ObjectName.ToString()));
        if (idx.Index < 0 && -idx.Index - 1 < _asset.Imports.Count)
            return Ident(StripCdo(_asset.Imports[-idx.Index - 1].ObjectName.ToString()));
        return $"<fn:{idx.Index}>";
    }

    // ================= 运算符还原 =================

    /// <summary>引擎数学/比较库函数 → C 运算符（前缀形式，后缀接参数个数）。</summary>
    private static readonly Dictionary<string, (string Op, int Arity)> Operators = new(StringComparer.Ordinal)
    {
        ["Add_IntInt"] = ("+", 2), ["Add_FloatFloat"] = ("+", 2), ["Add_DoubleDouble"] = ("+", 2),
        ["Subtract_IntInt"] = ("-", 2), ["Subtract_FloatFloat"] = ("-", 2), ["Subtract_DoubleDouble"] = ("-", 2),
        ["Multiply_IntInt"] = ("*", 2), ["Multiply_FloatFloat"] = ("*", 2), ["Multiply_DoubleDouble"] = ("*", 2),
        ["Divide_IntInt"] = ("/", 2), ["Divide_FloatFloat"] = ("/", 2), ["Divide_DoubleDouble"] = ("/", 2),
        ["Percent_IntInt"] = ("%", 2),
        ["Greater_IntInt"] = (">", 2), ["Greater_FloatFloat"] = (">", 2), ["Greater_DoubleDouble"] = (">", 2),
        ["Less_IntInt"] = ("<", 2), ["Less_FloatFloat"] = ("<", 2), ["Less_DoubleDouble"] = ("<", 2),
        ["GreaterEqual_IntInt"] = (">=", 2), ["GreaterEqual_FloatFloat"] = (">=", 2), ["GreaterEqual_DoubleDouble"] = (">=", 2),
        ["LessEqual_IntInt"] = ("<=", 2), ["LessEqual_FloatFloat"] = ("<=", 2), ["LessEqual_DoubleDouble"] = ("<=", 2),
        ["EqualEqual_IntInt"] = ("==", 2), ["EqualEqual_FloatFloat"] = ("==", 2), ["EqualEqual_DoubleDouble"] = ("==", 2),
        ["EqualEqual_BoolBool"] = ("==", 2), ["EqualEqual_ByteByte"] = ("==", 2), ["EqualEqual_StrStr"] = ("==", 2),
        ["EqualEqual_NameName"] = ("==", 2), ["EqualEqual_ObjectObject"] = ("==", 2),
        ["EqualEqual_ClassClass"] = ("==", 2), ["EqualEqual_InterfaceInterface"] = ("==", 2),
        ["NotEqual_IntInt"] = ("!=", 2), ["NotEqual_FloatFloat"] = ("!=", 2), ["NotEqual_DoubleDouble"] = ("!=", 2),
        ["NotEqual_BoolBool"] = ("!=", 2), ["NotEqual_ByteByte"] = ("!=", 2), ["NotEqual_StrStr"] = ("!=", 2),
        ["NotEqual_NameName"] = ("!=", 2), ["NotEqual_ObjectObject"] = ("!=", 2),
        ["NotEqual_ClassClass"] = ("!=", 2), ["NotEqual_InterfaceInterface"] = ("!=", 2),
        ["BooleanAND"] = ("&&", 2), ["BooleanOR"] = ("||", 2), ["BooleanNAND"] = ("!&&", 2), ["BooleanNOR"] = ("!||", 2),
        ["Not_PreBool"] = ("!", 1), ["Not_Int"] = ("~", 1),
        ["BitwiseAND_IntInt"] = ("&", 2), ["BitwiseOR_IntInt"] = ("|", 2), ["BitwiseXOR_IntInt"] = ("^", 2),
        ["ShiftLeft_IntInt"] = ("<<", 2), ["ShiftRight_IntInt"] = (">>", 2),
    };

    private const int PrecOr = 1, PrecAnd = 2, PrecCmp = 3, PrecAdd = 4, PrecMul = 5, PrecUnary = 6, PrecAtom = 7;

    private static int PrecOf(string op) => op switch
    {
        "||" or "!||" => PrecOr,
        "&&" or "!&&" => PrecAnd,
        "==" or "!=" or "<" or ">" or "<=" or ">=" => PrecCmp,
        "+" or "-" => PrecAdd,
        "*" or "/" or "%" => PrecMul,
        "!" or "~" => PrecUnary,
        _ => PrecAtom
    };

    /// <summary>文本的顶层运算符优先级（用于最小括号化）。</summary>
    public static int TopLevelPrec(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return PrecAtom;
        if (t[0] == '!' && t.Length > 1) return PrecUnary;
        if (t[0] == '(' && IsWrapped(t)) return PrecAtom;
        var depth = 0;
        var best = PrecAtom;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (depth != 0) continue;
            else if (c == '|' || c == '&') best = Math.Min(best, c == '|' ? PrecOr : PrecAnd);
            else if (c is '=' or '<' or '>') best = Math.Min(best, PrecCmp);
            else if ((c == '+' || c == '-') && i > 0) best = Math.Min(best, PrecAdd);
            else if (c is '*' or '/' or '%') best = Math.Min(best, PrecMul);
            else if (c == '?') best = Math.Min(best, PrecOr);
        }
        return best;
    }

    /// <summary>整个串被一对最外层括号包住。</summary>
    internal static bool IsWrapped(string t)
    {
        if (t.Length < 2 || t[0] != '(' || t[^1] != ')') return false;
        var depth = 0;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '(') depth++;
            else if (t[i] == ')')
            {
                depth--;
                if (depth == 0) return i == t.Length - 1;
            }
        }
        return false;
    }

    /// <summary>二元/一元运算符渲染，按优先级补最少括号。</summary>
    private string? RenderOperator(FPackageIndex idx, KismetExpression[]? ps)
    {
        if (ps is null) return null;
        var name = ResolveStackNode(idx);
        if (!Operators.TryGetValue(name, out var op) || op.Arity != ps.Length) return null;
        if (op.Arity == 1)
        {
            var inner = Render(ps[0]);
            var nop = op.Op.TrimStart('!');
            if (nop.Length == 0) return TopLevelPrec(inner) < PrecUnary ? $"!({inner})" : "!" + inner;
            return $"~{inner}";
        }
        var a = Render(ps[0]);
        var b = Render(ps[1]);
        var p = PrecOf(op.Op);
        var negated = op.Op is "!&&" or "!||";        // 注意："!=" 不是取反标记
        var core = negated ? op.Op[1..] : op.Op;
        if (negated) p = PrecOf(core) + 1;             // NAND/NOR 整体视作更紧
        var left = TopLevelPrec(a) < p ? "(" + a + ")" : a;
        var right = TopLevelPrec(b) <= p ? "(" + b + ")" : b;
        var expr = $"{left} {core} {right}";
        return negated ? "!(" + expr + ")" : expr;
    }

    /// <summary>渲染函数调用；命中 UHT 签名时按位置给 out 实参加 out 前缀（调用名不限定类）。</summary>
    public string RenderCall(FPackageIndex idx, KismetExpression[]? ps)
    {
        var name = ResolveStackNode(idx);
        if (ps is null || ps.Length == 0) return $"{name}()";
        var types = ResolveParamTypes(idx, ps.Length);

        var mods = ResolveParamMods(idx);
        _hint.Push(types);
        try
        {
            if (mods is not null && mods.Length == ps.Length)
            {
                var parts = new string[ps.Length];
                for (var i = 0; i < ps.Length; i++)
                {
                    _argIdx = i;
                    parts[i] = mods[i] == "out" ? $"out {Render(ps[i])}" : Render(ps[i]);
                }
                return $"{name}({string.Join(", ", parts)})";
            }
            var parts2 = new string[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                _argIdx = i;
                parts2[i] = Render(ps[i]);
            }
            return $"{name}({string.Join(", ", parts2)})";
        }
        finally
        {
            _hint.Pop();
            _argIdx = -1;
        }
    }

    /// <summary>当前实参的期望 C++ 类型（供未知数值转换使用），来自被调函数的形参表。</summary>
    private int _argIdx = -1;
    private readonly Stack<string?[]> _hint = new();
    private string? ExpectedArgType()
    {
        if (_argIdx < 0 || _hint.Count == 0) return null;
        var t = _hint.Peek();
        return _argIdx < t.Length ? t[_argIdx] : null;
    }

    private string?[] ResolveParamTypes(FPackageIndex idx, int n)
    {
        var res = new string?[n];
        if (idx.Index >= 0 && idx.Index - 1 < _asset.Exports.Count
            && _asset.Exports[idx.Index - 1] is FunctionExport fe
            && fe.LoadedProperties is { Length: > 0 } props)
        {
            var i = 0;
            foreach (var p in props)
            {
                if (!p.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm)) continue;
                if (p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
                if (i < n) res[i] = FunctionSignatures.CppType(_asset, p);
                i++;
            }
            return res;
        }
        if (_sigs is not null)
        {
            var path = ImportSignatures.ResolveImportPath(_asset, idx);
            if (path is not null && _sigs.ByFullKey.TryGetValue(path, out var s))
                return ParamTypes(s.Params, n);
        }
        return res;
    }

    /// <summary>从 UHT 形参表字符串里取出每个参数的类型部分。</summary>
    private static string?[] ParamTypes(string? parms, int n)
    {
        var res = new string?[n];
        if (string.IsNullOrWhiteSpace(parms)) return res;
        var parts = new List<string>();
        var depth = 0;
        var cur = new StringBuilder();
        foreach (var c in parms)
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            if (c == ',' && depth == 0) { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        for (var i = 0; i < Math.Min(n, parts.Count); i++)
        {
            var t = parts[i].Trim();
            var sp = t.LastIndexOf(' ');
            res[i] = sp > 0 ? t[..sp].Trim() : t;
        }
        return res;
    }

    private string[]? ResolveParamMods(FPackageIndex idx)
    {
        if (idx.Index >= 0)
            return _asset.Exports[idx.Index - 1] is FunctionExport fe ? LocalMods(fe.ObjectName.ToString()) : null;
        if (_sigs is not null)
        {
            var path = ImportSignatures.ResolveImportPath(_asset, idx);
            if (path is not null && _sigs.ByFullKey.TryGetValue(path, out var s)) return s.ParamModifiers();
        }
        var n = ResolveStackNode(idx);
        return LocalMods(n);
    }

    /// <summary>本资产内蓝图函数（含本地函数）的形参修饰符表，来自各自的 <c>LoadedProperties</c>。</summary>
    private string[]? LocalMods(string fnName)
    {
        var map = LocalModCache.GetValue(_asset, BuildLocalMods);
        return map.TryGetValue(fnName, out var m) ? m : null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UAsset, Dictionary<string, string[]>> LocalModCache = new();

    private static Dictionary<string, string[]> BuildLocalMods(UAsset asset)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var fn in asset.Exports.OfType<FunctionExport>())
        {
            if (fn.LoadedProperties is not { Length: > 0 } props) continue;
            var mods = new List<string>();
            foreach (var p in props)
            {
                var f = p.PropertyFlags;
                if (!f.HasFlag(EPropertyFlags.CPF_Parm)) continue;
                if (f.HasFlag(EPropertyFlags.CPF_ReturnParm)) continue;
                if (f.HasFlag(EPropertyFlags.CPF_OutParm)) mods.Add("out");
                else if (f.HasFlag(EPropertyFlags.CPF_ReferenceParm)) mods.Add("ref");
                else mods.Add("in");
            }
            map[Ident(fn.ObjectName.ToString())] = mods.ToArray();
        }
        return map;
    }

    /// <summary>
    /// 只有一个 out 形参、且该实参是普通变量时，改写成赋值形式：<c>F(a, out v)</c> → <c>v = F(a)</c>。
    /// 这是蓝图里数组/映射取值的常见形态，赋值形式可读性明显更好。
    /// </summary>
    public bool TryRenderOutCallAssign(KismetExpression e, out string text)
    {
        text = "";
        var prefix = "";
        // 静态库调用形如 EX_Context(obj, FinalFunction(...))：剥掉外层，调用名带上对象前缀。
        if (e is EX_Context ctx) { prefix = Render(ctx.ObjectExpression) + "."; e = ctx.ContextExpression; }
        FPackageIndex? idx = null;
        string? byName = null;
        KismetExpression[]? ps = null;
        switch (e)
        {
            case EX_CallMath c: idx = c.StackNode; ps = c.Parameters; break;
            case EX_LocalFinalFunction lf: idx = lf.StackNode; ps = lf.Parameters; break;
            case EX_FinalFunction f: idx = f.StackNode; ps = f.Parameters; break;
            case EX_LocalVirtualFunction lv: byName = lv.VirtualFunctionName.ToString(); ps = lv.Parameters; break;
            case EX_VirtualFunction v: byName = v.VirtualFunctionName.ToString(); ps = v.Parameters; break;
            default: return false;
        }
        if (ps is null || ps.Length == 0) return false;
        var mods = idx is not null ? ResolveParamMods(idx) : LocalMods(byName!);
        if (mods is null || mods.Length != ps.Length) return false;
        var outAt = -1;
        for (var i = 0; i < mods.Length; i++)
        {
            if (mods[i] != "out") continue;
            if (outAt >= 0) return false;                 // 多 out 形参：保持原样
            outAt = i;
        }
        if (outAt < 0) return false;
        if (ps[outAt] is not (EX_LocalVariable or EX_LocalOutVariable or EX_InstanceVariable or EX_DefaultVariable))
            return false;

        var target = Render(ps[outAt]);
        var name = idx is not null ? ResolveStackNode(idx) : Ident(byName!);
        var args = ps.Where((_, i) => i != outAt).Select(Render);
        text = $"{target} = {prefix}{name}({string.Join(", ", args)})";
        return true;
    }

    private static string DumpInline(KismetExpression e)
    {
        var bits = e.GetType().GetFields()
            .Where(f => f.Name is not ("Tag" or "RawValue"))
            .Where(f => f.GetValue(e) is not null)
            .Select(f =>
            {
                var v = f.GetValue(e)!;
                return v is KismetExpression || v is KismetExpression[]
                    ? $"<{f.Name}>"
                    : $"{f.Name}={v}";
            });
        return $"{e.GetType().Name[3..]}({string.Join(", ", bits)})";
    }
}
