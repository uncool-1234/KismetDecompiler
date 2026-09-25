using System.Text;
using System.Text.RegularExpressions;

namespace UAssetKismet.Experimental;

/// <summary>
/// 行级语义优化器（实验性，作用于渲染后的伪代码行）。
/// P2 值流：CallFunc_* 临时量若"赋值一次 + 只作为函数参数整体使用一次"→ 直接内联进参数，删赋值行。
/// P1 化简：!(!x) → x；已知恒量条件折叠 if(true)/if(false)。
/// 保守原则：只在无副作用搬移风险处替换（参数位置求值顺序与原执行顺序一致）；普通复制链不做搬移。
/// </summary>
public static class SemanticOptimizer
{
    // 形如  "Temp = expr;"  且左值是纯标识符（临时量）
    private static readonly Regex AssignRegex = new(
        @"^(?<indent>\s*)(?<lhs>[A-Za-z_]\w*)\s*=\s*(?<rhs>.+?);\s*$", RegexOptions.Compiled);

    /// <summary>树改写入口：按括号树分块做 copy-prop，再做常量折叠与命名简化。</summary>
    public static List<string> OptimizeLines(List<string> lines)
    {
        var result = new List<string>(lines);
        for (var pass = 0; pass < 24; pass++)
        {
            if (!PassTree(result)) break;
        }
        // 删除被内联消除的赋值行（置空标记）
        result.RemoveAll(l => string.IsNullOrWhiteSpace(l.Trim('\r', '\n', ' ')));
        result = FoldConstants(result);
        result = SimplifyNames(result);
        result = RenameTemps(result);
        return result;
    }

    // ================= P5 临时量命名 =================

    /// <summary>蓝图编译器固定临时量 → 简短且达意的名字（按整函数统一替换，先保证不冲突）。</summary>
    private static readonly (string Prefix, string NewName)[] TempRenames =
    {
        ("Temp_int_Loop_Counter_Variable", "loopCounter"),
        ("Temp_int_Array_Index_Variable", "arrayIndex"),
        ("Temp_int_Variable", "loopIndex"),
        ("Temp_bool_True_if_break_was_hit_Variable", "breakFlag"),
        ("Temp_bool_Variable", "tmpBool"),
        ("Temp_byte_Variable", "tmpByte"),
        ("Temp_int_Variable_", "loopIndex"),
        ("Temp_float_Variable", "tmpNum"),
        ("Temp_double_Variable", "tmpNum"),
        ("Temp_string_Variable", "tmpString"),
        ("Temp_text_Variable", "tmpText"),
        ("Temp_name_Variable", "tmpName"),
        ("Temp_object_Variable", "tmpObject"),
        ("Temp_softobject_Variable", "tmpSoftObject"),
        ("Temp_weakobject_Variable", "tmpWeakObject"),
        ("Temp_delegate_Variable", "tmpDelegate"),
        ("Temp_vector_Variable", "tmpVector"),
        ("Temp_rotator_Variable", "tmpRotator"),
        ("Temp_transform_Variable", "tmpTransform"),
        ("Temp_color_Variable", "tmpColor"),
        ("Temp_class_Variable", "tmpClass"),
        ("Temp_interface_Variable", "tmpInterface"),
        ("Temp_struct_Variable", "tmpStruct"),
        ("Temp_wildcard_Variable", "tmpAny"),
        ("K2Node_SwitchEnum_CmpSuccess", "switchMatched"),
        ("K2Node_SwitchString_CmpSuccess", "switchMatched"),
        ("K2Node_SwitchName_CmpSuccess", "switchMatched"),
        ("K2Node_SwitchInteger_CmpSuccess", "switchMatched"),
        ("K2Node_DynamicCast_bSuccess", "castOk"),
        ("K2Node_MakeArray_Array", "tmpArray"),
        ("K2Node_Select_Default", "selectDefault"),
        ("K2Node_MakeStruct_FormatArgumentData", "formatArg"),
    };

    private static readonly Regex IdentRe = new(@"(?<![\w.])([A-Za-z_]\w*)", RegexOptions.Compiled);

    private static List<string> RenameTemps(List<string> lines)
    {
        var text = string.Join("\n", lines);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentRe.Matches(text)) present.Add(m.Groups[1].Value);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in present)
        {
            var target = TempTarget(id);
            if (target is null) continue;
            // `..._1` / `..._2` 这类重复实例：保留数字后缀，拼成 loopCounter1
            var baseName = target;
            var suffix = "";
            var um = Regex.Match(id, @"_(?<n>\d+)$");
            if (um.Success && TempTarget(id[..um.Index]) is { } b2) { baseName = b2; suffix = um.Groups["n"].Value; }
            var cand = baseName + suffix;
            var n = 1;
            while (present.Contains(cand) && !IsRenamedSource(cand)) cand = baseName + suffix + "_" + n++;
            map[id] = cand;
        }
        if (map.Count == 0) return lines;

        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
            outLines.Add(IdentRe.Replace(l, m => map.TryGetValue(m.Groups[1].Value, out var r) ? r : m.Groups[1].Value));
        return outLines;
    }

    private static bool IsRenamedSource(string id) => TempTarget(id) is not null;

    private static string? TempTarget(string id)
    {
        foreach (var (prefix, name) in TempRenames)
            if (id == prefix || id.StartsWith(prefix + "_", StringComparison.Ordinal)) return name;
        var dc = Regex.Match(id, @"^K2Node_DynamicCast_As(.+)$");
        if (dc.Success) return "as" + dc.Groups[1].Value;
        // 事件节点的实参暂存：K2Node_Event_targetCard → eventTargetCard
        // （小卡牌资产里这类名字占比极高，5k+ 处）
        var ev = Regex.Match(id, @"^K2Node_Event_(.+)$");
        if (ev.Success && ev.Groups[1].Value.Length > 0)
            return "event" + char.ToUpperInvariant(ev.Groups[1].Value[0]) + ev.Groups[1].Value.Substring(1);
        var mk = Regex.Match(id, @"^K2Node_(?:MakeStruct|MakeSet|MakeMap|MakeArray|SetFieldsInStruct|BreakStruct)_(.+)$");
        if (mk.Success) return (id.StartsWith("K2Node_SetFieldsInStruct") ? "set" : "make") + mk.Groups[1].Value;
        return null;
    }

    /// <summary>树版 pass：每个 { } 块内的叶子语句序列独立做 copy-prop（跨块/跨 if 天然隔离）。</summary>
    private static bool PassTree(List<string> lines)
    {
        var root = PseudoTreeParser.Parse(lines);
        var changed = false;

        // 遍历所有"块容器"（root 与每个 '{'），对每个容器内同父叶子序列处理
        void VisitBlock(StmtNode block)
        {
            var seq = block.Children
                .Where(c => c.Text != "{" && c.LineIndex >= 0 && !c.Text.TrimStart().StartsWith("//"))
                .ToList();

            // 收集单次赋值临时量（叶子）
            var tempDefs = new Dictionary<string, (int nodeIdx, int defLine, string rhs)>();
            var defLines = new HashSet<int>();
            for (var k = 0; k < seq.Count; k++)
            {
                var m = AssignRegex.Match(seq[k].Text);
                if (!m.Success) continue;
                var lhs = m.Groups["lhs"].Value;
                if (!IsTempLike(lhs)) continue;
                if (tempDefs.ContainsKey(lhs)) tempDefs.Remove(lhs);
                else tempDefs[lhs] = (k, seq[k].LineIndex, m.Groups["rhs"].Value);
                defLines.Add(seq[k].LineIndex);
            }

            foreach (var (temp, info) in tempDefs)
            {
                if (info.nodeIdx + 1 >= seq.Count) continue;
                int useIdx = -1, useCount = 0;
                for (var k = info.nodeIdx + 1; k < seq.Count; k++)
                {
                    if (CountToken(seq[k].Text, temp) > 0)
                    {
                        useCount++;
                        useIdx = k;
                        if (useCount > 1) break;
                    }
                }
                if (useCount != 1 || useIdx < 0) continue;
                // 不能把定义搬进另一条「本身的定义」行：同趟里那行也会被改/删，会留下悬空引用。
                if (defLines.Contains(seq[useIdx].LineIndex)) continue;
                // 循环头会被反复求值：把带副作用的赋值搬进去会改变调用次数，
                // 把依赖循环体的纯计算搬进去也可能读到新值。整类情况一律不内联
                // （合法的「循环条件临时量」由 ReadabilityPasses.FoldGuardPreamble 用全函数检查来折叠）。
                if (IsLoopHeader(seq[useIdx].Text)) continue;

                // 安全约束：def 与 use 之间只能隔无副作用的行（纯复制行 / 已知纯函数调用）
                var betweenOk = true;
                for (var k = info.nodeIdx + 1; k < useIdx; k++)
                {
                    if (!IsPure(seq[k].Text)) { betweenOk = false; break; }
                }
                if (!betweenOk) continue;

                if (!TryInlineIntoLine(seq[useIdx].Text, temp, info.rhs, out var newLine)) continue;
                lines[seq[useIdx].LineIndex] = newLine; // 行直改（无渲染往返）
                lines[info.defLine] = "";                // 标记删除
                changed = true;
            }

            foreach (var c in block.Children)
                if (c.Text == "{") VisitBlock(c);
        }

        VisitBlock(root);
        return changed;
    }

    private static bool Pass(List<string> lines)
    {
        var changed = false;
        var toRemove = new List<int>();

        // 1) 收集单次赋值临时量
        var tempDefs = new Dictionary<string, (int lineIdx, string rhs)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = AssignRegex.Match(lines[i]);
            if (!m.Success) continue;
            var lhs = m.Groups["lhs"].Value;
            if (!IsTempLike(lhs)) continue;
            if (tempDefs.ContainsKey(lhs)) tempDefs.Remove(lhs); // 二次赋值 → 不优化
            else tempDefs[lhs] = (i, m.Groups["rhs"].Value);
        }

        // 2) 先做所有参数内联替换（删除尚未发生，索引基于原表）
        foreach (var (temp, (defIdx, rhs)) in tempDefs)
        {
            if (defIdx >= lines.Count) continue;
            int useIdx = -1, useCount = 0;
            for (var i = defIdx + 1; i < lines.Count; i++)
            {
                if (IsComment(lines[i])) continue;
                if (CountToken(lines[i], temp) > 0)
                {
                    useCount++;
                    useIdx = i;
                    if (useCount > 1) break;
                }
            }
            if (useCount != 1 || useIdx < 0) continue;

            // 安全约束：赋值行与使用行之间只能隔"纯标识符复制行"（无调用/无副作用），保证求值顺序不变
            var betweenOk = true;
            for (var k = defIdx + 1; k < useIdx; k++)
            {
                if (!IsPureCopy(lines[k])) { betweenOk = false; break; }
            }
            if (!betweenOk) continue;

            if (!TryInlineIntoLine(lines[useIdx], temp, rhs, out var newLine)) continue;
            lines[useIdx] = newLine;
            toRemove.Add(defIdx);
            changed = true;
        }

        // 3) 倒序删除赋值行，避免索引错位
        foreach (var idx in toRemove.OrderByDescending(i => i))
            lines.RemoveAt(idx);

        return changed;
    }

    private static bool TryInlineIntoLine(string line, string temp, string rhs, out string newLine)
    {
        newLine = line;
        // 找每个独立出现；若恰好 1 处（非属性访问）则整体替换为最适合的括号形式
        // 允许位置：参数、if 条件、赋值右值等任意表达式上下文（相邻约束由调用方保证）
        var sb = new StringBuilder();
        var idx = 0;
        var matchedAny = false;
        foreach (Match m in Regex.Matches(line, $@"(?<![\w.]){Regex.Escape(temp)}(?![\w.])"))
        {
            sb.Append(line, idx, m.Index - idx);
            if (!matchedAny)
            {
                sb.Append(ParenFor(line, m.Index, m.Length, rhs));
                matchedAny = true;
            }
            else
            {
                newLine = line;
                return false; // 多次出现 → 放弃
            }
            idx = m.Index + temp.Length;
        }
        if (!matchedAny)
        {
            newLine = line;
            return false;
        }
        sb.Append(line, idx, line.Length - idx);
        newLine = sb.ToString();
        return true;
    }

    /// <summary>按插入位置的父运算符优先级决定要不要给 rhs 加括号（最小括号化）。</summary>
    private static string ParenFor(string line, int start, int len, string rhs)
    {
        var prec = ExprRenderer.TopLevelPrec(rhs);
        if (prec >= 7) return rhs;                  // 原子/调用：永远不用括号
        var parent = ParentPrec(line, start, start + len);
        return prec < parent ? "(" + rhs + ")" : rhs;
    }

    /// <summary>左/右最近的同层运算符的优先级；找不到（如整个实参、整条右值）返回最低。</summary>
    private static int ParentPrec(string line, int start, int end)
    {
        var best = 0;
        var depth = 0;
        for (var i = start - 1; i >= 0; i--)
        {
            var c = line[i];
            if (c == ')') depth++;
            else if (c == '(') { if (depth == 0) break; depth--; }
            else if (depth == 0 && c == '!')
            {
                if (i + 1 < line.Length && line[i + 1] == '=') continue;   // != 交给 '=' 处理
                best = Math.Max(best, 6);                                   // 一元取反：结合极紧
                break;
            }
            else if (depth == 0 && OpPrec(c) is { } p)
            {
                // '-'/'+' 出现在表达式开头是一元号，不算父运算符
                if ((c == '-' || c == '+') && (i == 0 || "([,=<>+-*/%&|!".IndexOf(line[i - 1]) >= 0)) continue;
                best = Math.Max(best, p);
                break;
            }
        }
        depth = 0;
        for (var i = end; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            else if (depth == 0 && OpPrec(c) is { } p)
            {
                best = Math.Max(best, p);
                break;
            }
        }
        return best;
    }

    private static int? OpPrec(char c) => c switch
    {
        '&' or '|' => 2,
        '=' or '<' or '>' => 3,
        '+' or '-' => 4,
        '*' or '/' or '%' => 5,
        _ => null
    };

    private static char PrevChar(string s, int idx)
    {
        for (var i = idx - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(s[i])) return s[i];
        return '\0';
    }

    private static char NextChar(string s, int idx)
    {
        for (var i = idx; i < s.Length; i++)
            if (!char.IsWhiteSpace(s[i])) return s[i];
        return '\0';
    }

    private static int CountToken(string line, string token)
    {
        if (IsComment(line)) return 0;
        return Regex.Matches(line, $@"(?<![\w.]){Regex.Escape(token)}(?![\w.])").Count;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//");

    /// <summary>纯标识符复制行（形如 `K2Node_Event_x = param;`，无函数调用/无副作用）。</summary>
    private static readonly Regex PureCopyRegex = new(
        @"^\s*[A-Za-z_]\w*\s*=\s*[A-Za-z_][\w.]*\s*;\s*$", RegexOptions.Compiled);
    private static bool IsPureCopy(string line)
    {
        if (IsComment(line)) return false;
        return PureCopyRegex.IsMatch(line) && !line.Contains("(");
    }

    /// <summary>
    /// 已知无副作用的纯函数：数学/比较/逻辑/构造/取长度/校验。
    /// 只有当一行的所有调用都是纯函数、且该行是赋值或声明时，才允许把别的表达式挪到它后面求值。
    /// </summary>
    private static readonly string[] PureCallees =
    {
        "Add_", "Subtract_", "Multiply_", "Divide_", "Percent_", "Abs", "Max", "Min", "Clamp", "FMax", "FMin", "FClamp",
        "Greater_", "Less_", "EqualEqual_", "NotEqual_", "BooleanAND", "BooleanOR", "BooleanNAND", "BooleanNOR",
        "Not_PreBool", "Not_Int", "Bitwise", "ShiftLeft", "ShiftRight",
        "Conv_", "MakeLiteral", "MakeStruct", "Array_Length", "Map_Length", "Set_Length",
        "IsValid", "IsValidClass", "GetObjectClass", "Select", "GetDisplayName", "ToText", "ToDisplayString",
    };

    private static bool IsPure(string line)
    {
        if (IsComment(line)) return false;
        var t = line.Trim();
        if (t.Length == 0) return true;
        if (IsPureCopy(t)) return true;
        if (t.EndsWith(';') && (t.Contains(" = ") || IsDeclLike(t)) && NoImpureCall(t)) return true;
        return false;
    }

    private static bool IsDeclLike(string t) =>
        !t.Contains('=') && !t.Contains('(') && System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\w:<>*&,\s]+;$");

    /// <summary>循环头：其条件会被反复求值，内联需要额外的全函数生命周期保证。</summary>
    private static bool IsLoopHeader(string t)
    {
        var s = t.TrimStart();
        return s.StartsWith("while (") || s.StartsWith("for (") || s == "do" || s.StartsWith("do ");
    }

    private static bool NoImpureCall(string t)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"(?<![\w.>])([A-Za-z_]\w*)\s*\("))
        {
            var name = m.Groups[1].Value;
            if (name is "if" or "while" or "for" or "switch" or "return" or "Cast" or "sizeof") continue;
            if (!PureCallees.Any(p => name.Contains(p, StringComparison.Ordinal))) return false;
        }
        return true;              // 无调用，或全部是已知纯函数
    }

    /// <summary>P4 命名简化：去掉编译器临时量的统一前缀 CallFunc_（所有引用同步去掉，保持唯一性）。</summary>
    private static List<string> SimplifyNames(List<string> lines)
    {
        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (IsComment(l)) { outLines.Add(l); continue; }
            outLines.Add(Regex.Replace(l, @"(?<![\w.])CallFunc_", ""));
        }
        return outLines;
    }

    /// <summary>
    /// 哪些左值算「编译器临时量」（可以安全 copy-prop 到唯一使用点）。
    /// 除了原始名，还要认 Switch/Cast 的成功标志位：它们改名后是
    /// `K2Node_SwitchEnum_CmpSuccess_1` → `switchMatched1`、`K2Node_DynamicCast_bSuccess` → `castOk`，
    /// 只按 `CallFunc_`/`ReturnValue` 判断就会整套漏掉 —— 而它们正是嵌套最深的那批。
    ///
    /// 注意**不能**在这里放行 <see cref="TempTarget"/> 认出的所有名字：循环计数器 / 数组下标
    /// （`Temp_int_Loop_Counter_Variable_3`）也在那张表里，而它们的「使用点」本身又是一条赋值
    /// （`loopCounter = Add_IntInt_ReturnValue`），一旦把它们算作临时量，
    /// `defLines.Contains(useLine)` 这道保护就会把 `Add_IntInt_ReturnValue` 的内联一起挡掉，
    /// 循环步进变回两行，后面 `RecoverForLoop` 就再也认不出 `for` 了。
    /// </summary>
    private static bool IsTempLike(string lhs)
    {
        if (lhs.StartsWith("CallFunc_") || lhs.Contains("ReturnValue")) return true;
        if (lhs.StartsWith("K2Node_Switch") || lhs.StartsWith("K2Node_DynamicCast_bSuccess")) return true;
        var stem = Regex.Replace(lhs, @"_?\d+$", "");                     // switchMatched1 → switchMatched
        return RenamedInlineable.Contains(stem);
    }

    /// <summary>改名后才认得出来的临时量名字（只列「一次性标志位」，不含跨迭代变量）。</summary>
    private static readonly HashSet<string> RenamedInlineable = new(StringComparer.Ordinal)
    {
        "switchMatched", "castOk",
    };

    /// <summary>P1/P3 常量折叠与简单化简（行内模式）</summary>
    private static List<string> FoldConstants(List<string> lines)
    {
        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (IsComment(l)) { outLines.Add(l); continue; }
            var s = l;
            var prev = "";
            while (prev != s)
            {
                prev = s;
                s = Regex.Replace(s, @"!\(\s*!(\s*\(?[^;()]+\)?)\s*\)", "$1"); // !(!x) → x
            }
            outLines.Add(s);
        }
        return outLines;
    }
}
