using System.Text.RegularExpressions;

namespace UAssetKismet.Experimental;

/// <summary>
/// 结构化发射之后的文本级可读性后处理。
/// 每条规则都只做「语义等价」的局部改写，并给出成立条件。
/// 引擎按行左到右扫描、用重写缓冲替换，单趟线性；外层反复迭代到不再变化。
/// </summary>
public static class ReadabilityPasses
{
    /// <param name="dupPlan">
    /// CFG 层算好的「goto 尾部复制」方案：键是 goto 的行号，
    /// 值是（尾巴起始行, 尾巴结束行（不含）, 缩进偏移）。为 null 或空表示不做任何复制。
    /// </param>
    public static string Run(string text, IReadOnlyDictionary<int, (int Start, int End, int Shift)>? dupPlan = null)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        // 复制要在规则改写之前做：方案里的行号是按装配好的原始文本算的。
        if (dupPlan is { Count: > 0 }) lines = ApplyGotoDup(lines, dupPlan);
        for (var pass = 0; pass < 12; pass++)
        {
            var before = lines.Count;
            var changed = false;

            lines = Rewrite(lines, CollapseEmptyIf, ref changed);
            lines = Rewrite(lines, CollapseElseIf, ref changed);
            lines = Rewrite(lines, FlattenNestedIf, ref changed);
            lines = Rewrite(lines, DropDeadAfterTerminator, ref changed);
            lines = Rewrite(lines, DropDispatchJumpComment, ref changed);
            lines = Rewrite(lines, DropElseAfterTerminator, ref changed);
            lines = Rewrite(lines, FoldGuardPreamble, ref changed);
            lines = Rewrite(lines, RecoverForLoop, ref changed);
            lines = Rewrite(lines, StripTrailingContinue, ref changed);
            lines = Rewrite(lines, WhileTrueGuardFirst, ref changed);
            lines = Rewrite(lines, WhileTrueGuardLast, ref changed);
            lines = Rewrite(lines, DropTrailingReturn, ref changed);
            lines = Rewrite(lines, DropEmptyElse, ref changed);

            var tidy = lines.Select(TidyLine).ToList();
            if (!tidy.SequenceEqual(lines)) { lines = tidy; changed = true; }

            if (!changed && lines.Count == before) break;
        }
        return string.Join("\n", lines).TrimEnd('\n');
    }

    // ================= goto 尾部复制 =================
    private static readonly Regex LabelRe = new(@"^(?<ind>\s*)(?<lbl>L_[0-9A-Fa-f]{4}):$", RegexOptions.Compiled);
    private static readonly Regex GotoRe = new(@"^(?<ind>\s*)goto (?<lbl>L_[0-9A-Fa-f]{4});$", RegexOptions.Compiled);

    /// <summary>
    /// 按 CFG 层给出的方案，把「goto 一个标签」替换成「抄一遍那个标签起、到该区域结束为止的代码」。
    /// 这是结构化后唯一还会产生 goto 的形态：一个分支要汇入兄弟分支内部的共用尾部。
    ///
    /// 等价性由 CFG 层保证（来源区域续接块 == 目标块直接后支配点，且复制范围正好是目标区域）；
    /// 这里只做两件事：把方案里的行号执行掉，以及做最后的文本自检
    /// （尾巴里出现 break/continue/goto/标签就放弃 —— 这些会随嵌套层级改变含义）。
    /// </summary>
    private static List<string> ApplyGotoDup(List<string> lines, IReadOnlyDictionary<int, (int Start, int End, int Shift)> plan)
    {
        var replaced = new HashSet<int>();
        var outp = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!plan.TryGetValue(i, out var p)) { outp.Add(lines[i]); continue; }
            // 尾巴可以在 goto 之前（文本上往回跳）或之后；只要不把 goto 自己圈进去就行
            if (p.Start < 0 || p.End > lines.Count || (p.Start <= i && i < p.End)) { outp.Add(lines[i]); continue; }
            var tail = new List<string>(p.End - p.Start);
            var ok = true;
            for (var k = p.Start; k < p.End; k++)
            {
                var t = lines[k].Trim();
                if (t.StartsWith("goto ") || t.StartsWith("break") || t.StartsWith("continue") || LabelRe.IsMatch(lines[k]))
                {
                    ok = false;
                    break;
                }
                tail.Add(p.Shift >= 0
                    ? Pad(p.Shift) + lines[k]
                    : lines[k][Math.Min(-p.Shift, lines[k].Length - lines[k].TrimStart(' ').Length)..]);
            }
            if (!ok) { outp.Add(lines[i]); continue; }
            outp.AddRange(tail);
            replaced.Add(i);
        }
        if (replaced.Count == 0) return lines;
        lines = outp;

        // 复制完没人再跳的标签删掉（连同它前面的空行，避免留下孤立空行）
        var stillUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            var gm = GotoRe.Match(l);
            if (gm.Success) stillUsed.Add(gm.Groups["lbl"].Value);
        }
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var lm = LabelRe.Match(lines[i]);
            if (!lm.Success || stillUsed.Contains(lm.Groups["lbl"].Value)) continue;
            lines.RemoveAt(i);
            if (i < lines.Count && lines[i].Trim().Length == 0 && i > 0 && lines[i - 1].Trim().Length == 0) lines.RemoveAt(i);
        }
        return lines;
    }

    // ================= 引擎 =================

    /// <summary>规则返回 (被替换区间的末行, 替换文本)；返回 null 表示不改。</summary>
    private delegate (int End, List<string> Repl)? Rule(List<string> lines, int i);

    private static List<string> Rewrite(List<string> lines, Rule rule, ref bool changed)
    {
        var result = new List<string>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var r = rule(lines, i);
            if (r is { } hit)
            {
                if (hit.End < i) { result.Add(lines[i]); i++; continue; }
                result.AddRange(hit.Repl);
                i = hit.End + 1;
                changed = true;
            }
            else { result.Add(lines[i]); i++; }
        }
        return result;
    }

    private static int Indent(string s) => s.Length - s.TrimStart(' ').Length;
    private static string Pad(int n) => new(' ', n);

    /// <summary>lines[open] 必须是「以 { 结尾的块头」，返回配对 } 的行号。</summary>
    private static int MatchBrace(List<string> lines, int open)
    {
        var depth = 0;
        for (var k = open; k < lines.Count; k++)
        {
            var t = lines[k].Trim();
            if (t.EndsWith('{')) depth++;
            if (t == "}" && --depth == 0) return k;
        }
        return -1;
    }

    /// <summary>lines[close] 必须是 '}'，返回配对块头的行号。</summary>
    private static int MatchBraceBack(List<string> lines, int close)
    {
        var depth = 0;
        for (var k = close; k >= 0; k--)
        {
            var t = lines[k].Trim();
            if (t == "}") depth++;
            else if (t.EndsWith('{') && --depth == 0) return k;
        }
        return -1;
    }

    private static int NextNonEmpty(List<string> lines, int from)
    {
        for (var k = from; k < lines.Count; k++)
            if (lines[k].Trim().Length > 0) return k;
        return -1;
    }

    private static int PrevNonEmpty(List<string> lines, int from)
    {
        for (var k = from; k >= 0; k--)
            if (lines[k].Trim().Length > 0) return k;
        return -1;
    }

    /// <summary>i 行是块头（if/while/else/do/…）且紧接一行 <c>{</c> 时，返回该 <c>{</c> 的行号。</summary>
    private static bool BlockBraceAt(List<string> lines, int i, out int brace)
    {
        brace = -1;
        var t = lines[i].Trim();
        if (t.Length == 0 || t == "{" || t == "}") return false;
        if (NextNonEmpty(lines, i + 1) != i + 1) return false;
        if (lines[i + 1].Trim() != "{") return false;
        brace = i + 1;
        return true;
    }

    /// <summary>块头（如 <c>if (...)</c>）的行号 → 其 <c>{</c> 的行号。</summary>
    private static int HeaderOfBrace(List<string> lines, int brace)
    {
        var h = PrevNonEmpty(lines, brace - 1);
        return h;
    }

    private static List<string> Slice(List<string> lines, int from, int to) => lines.GetRange(from, Math.Max(0, to - from));

    private static List<string> Dedent(List<string> lines, int from, int to, int levels = 1)
    {
        var res = new List<string>(Math.Max(0, to - from));
        for (var i = from; i < to; i++)
        {
            var cut = Math.Min(Indent(lines[i]), levels * 4);
            res.Add(lines[i].Substring(cut));
        }
        return res;
    }

    private static bool IsTerminator(string t) =>
        t is "return;" or "break;" or "continue;" ||
        t.StartsWith("goto ") || t.StartsWith("return ");

    /// <summary>把 <c>if (COND)</c> 取反。</summary>
    private static string Negate(string header)
    {
        var m = Regex.Match(header, @"^(?<ind>\s*)if \((?<c>.*)\)$");
        if (!m.Success) return header;
        var ind = m.Groups["ind"].Value;
        var c = m.Groups["c"].Value.Trim();
        if (c.StartsWith('!')) c = c.Substring(1).Trim();
        else c = "!" + c;
        return $"{ind}if ({c})";
    }

    // ================= 规则 =================

    /// <summary><c>if (C) { } else { B }</c> → <c>if (!C) { B }</c>；<c>if (C) { }</c> → 删除。</summary>
    private static (int, List<string>)? CollapseEmptyIf(List<string> lines, int i)
    {
        var t = lines[i].Trim();
        if (!t.StartsWith("if (") || t == "else") return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;
        if (NextNonEmpty(lines, brace + 1) != close) return null;      // 块非空

        var ind = Indent(lines[i]);
        var j = NextNonEmpty(lines, close + 1);
        if (j >= 0 && lines[j].Trim() == "else" && Indent(lines[j]) == ind
            && BlockBraceAt(lines, j, out var elseBrace))
        {
            var elseClose = MatchBrace(lines, elseBrace);
            if (elseClose < 0) return null;
            var body = Slice(lines, elseBrace + 1, elseClose);
            var repl = new List<string> { Negate(lines[i]), Pad(ind) + "{" };
            repl.AddRange(body);
            repl.Add(Pad(ind) + "}");
            return (elseClose, repl);
        }
        // 没有 else：整段删除
        return (close, new List<string>());
    }

    /// <summary>
    /// <c>else { if (C) { … } }</c> → <c>else if (C) { … }</c>。
    /// 条件是那个 <c>if</c> 整条语句（含它自己的 else/else if 链）正好填满 else 块，
    /// 也就是 else 块里除了这条 if 别无他物；否则改写会改变归属。
    /// </summary>
    private static (int, List<string>)? CollapseElseIf(List<string> lines, int i)
    {
        if (lines[i].Trim() != "else") return null;
        var ind = Indent(lines[i]);
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;

        var first = NextNonEmpty(lines, brace + 1);
        if (first < 0 || first >= close) return null;
        if (!lines[first].Trim().StartsWith("if (")) return null;
        if (Indent(lines[first]) != ind + 4) return null;

        var ifEnd = IfStatementEnd(lines, first);
        if (ifEnd < 0) return null;
        if (NextNonEmpty(lines, ifEnd + 1) != close) return null;   // else 块里还有别的东西

        var repl = new List<string>(ifEnd - first + 2) { Pad(ind) + "else " + lines[first].Trim() };
        repl.AddRange(Dedent(lines, first + 1, ifEnd + 1));
        return (close, repl);
    }

    /// <summary>从 <c>if</c> 行出发，返回整条 if / else if / else 语句最后一行的行号。</summary>
    private static int IfStatementEnd(List<string> lines, int ifLine)
    {
        if (!BlockBraceAt(lines, ifLine, out var brace)) return -1;
        var close = MatchBrace(lines, brace);
        if (close < 0) return -1;
        while (true)
        {
            var e = NextNonEmpty(lines, close + 1);
            if (e < 0 || lines[e].Trim() != "else" || Indent(lines[e]) != Indent(lines[ifLine])) return close;
            var nb = NextNonEmpty(lines, e + 1);
            if (nb < 0 || nb != e + 1) return close;
            if (BlockBraceAt(lines, nb, out var b2))
            {
                var c2 = MatchBrace(lines, b2);
                if (c2 < 0) return close;
                close = c2;
                continue;
            }
            if (lines[nb].Trim().StartsWith("if (")) return IfStatementEnd(lines, nb);
            return close;
        }
    }

    /// <summary>
    /// 反 Dispatch 把 <c>ComputedJump</c> 还原成 <c>switch/case</c> 之后，
    /// 每个 case 体开头还留着原来那条 <c>EX_ComputedJump</c> 的渲染注释
    /// （<c>// switch (EntryPoint);</c>）—— case 标签本身已经说明了去向，删掉。
    /// 只删「紧跟在 case 标签后面」的那一条，别处的保留。
    /// </summary>
    private static (int, List<string>)? DropDispatchJumpComment(List<string> lines, int i)
    {
        if (!ComputedJumpCommentRe.IsMatch(lines[i])) return null;
        var prev = PrevNonEmpty(lines, i - 1);
        if (prev < 0 || !lines[prev].Trim().StartsWith("case ")) return null;
        return (i, new List<string>());
    }

    private static readonly Regex ComputedJumpCommentRe =
        new(@"^\s*// switch \(.*\);$", RegexOptions.Compiled);

    /// <summary>
    /// <c>return;</c> / <c>break;</c> / <c>continue;</c> / <c>goto</c> 之后、同一个块里的语句永远执行不到 → 删掉。
    /// 典型来源是反 Dispatch：case 体末尾已经 <c>return;</c>，结构化又补了个 <c>break;</c>。
    /// 遇到 <c>}</c>（块结束）、缩进回退（case 标签、else）或标签/跳转就停手。
    /// </summary>
    private static (int, List<string>)? DropDeadAfterTerminator(List<string> lines, int i)
    {
        if (!IsTerminator(lines[i].Trim())) return null;
        var ind = Indent(lines[i]);
        var k = i + 1;
        var dropped = 0;
        while (k < lines.Count)
        {
            var t = lines[k].Trim();
            if (t.Length == 0) { k++; continue; }          // 空行一并吃掉
            if (t == "}") break;
            if (Indent(lines[k]) < ind) break;             // 回到外层：case/else/}
            if (LabelRe.IsMatch(lines[k]) || GotoRe.IsMatch(lines[k])) break;  // 可能是跳转目标
            dropped++;
            k++;
        }
        if (dropped == 0) return null;
        // [fix] 被替换的区间是 [i, k-1]，含终结语句本身。原实现返回空列表，把 return;/break;/goto 也一起删了，
        // 反 Dispatch 的每个 case 体因此丢掉收尾，读起来像 C 的 fallthrough。
        return (k - 1, new List<string> { lines[i] });
    }

    /// <summary>
    /// <c>if (A) { if (B) { … } }</c> → <c>if (A &amp;&amp; B) { … }</c>。
    ///
    /// 蓝图里 <c>Switch on Int</c> 会编译成一串「不等则继续往里钻」的嵌套 if，
    /// 深度可以到十几层（`CanCardBeBuffed` 就是 9 层）。这种纯合取嵌套没有任何分支语义，
    /// 压成一条 <c>&amp;&amp;</c> 链既等价又好读。
    ///
    /// 成立条件（缺一不可）：外层块里**只有**这一条内层 if；外层 if 没有 else；
    /// 内层 if 也没有 else。任一侧带 else 都不能折叠 —— <c>A</c> 为假时该走 else 的路径会被改掉。
    /// </summary>
    private static (int, List<string>)? FlattenNestedIf(List<string> lines, int i)
    {
        var condA = CondOf(lines[i]);
        if (condA is null) return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;

        var ind = Indent(lines[i]);
        var after = NextNonEmpty(lines, close + 1);
        if (after >= 0 && lines[after].Trim() == "else" && Indent(lines[after]) == ind) return null;

        var first = NextNonEmpty(lines, brace + 1);
        if (first < 0 || first >= close) return null;
        if (Indent(lines[first]) != ind + 4) return null;
        var condB = CondOf(lines[first]);
        if (condB is null) return null;
        if (!BlockBraceAt(lines, first, out var innerBrace)) return null;
        var innerClose = MatchBrace(lines, innerBrace);
        if (innerClose < 0) return null;
        if (innerClose != IfStatementEnd(lines, first)) return null;   // 内层 if 带 else
        if (NextNonEmpty(lines, innerClose + 1) != close) return null; // 外层块里还有别的东西

        var repl = new List<string> { Pad(ind) + $"if ({And(condA, condB)})", Pad(ind) + "{" };
        repl.AddRange(Dedent(lines, innerBrace + 1, innerClose));
        repl.Add(Pad(ind) + "}");
        return (close, repl);
    }

    /// <summary>从 <c>if (COND)</c> 里取出 COND；不是单行 if 头就返回 null。</summary>
    private static string? CondOf(string ifLine)
    {
        var t = ifLine.Trim();
        if (!t.StartsWith("if (") || !t.EndsWith(")")) return null;
        var c = t.Substring(4, t.Length - 5).Trim();
        return c.Length == 0 ? null : c;
    }

    /// <summary>把两个条件用 &amp;&amp; 连起来；含顶层 <c>||</c> / <c>?:</c> 的一侧要加括号。</summary>
    private static string And(string a, string b) => WrapOperand(a) + " && " + WrapOperand(b);

    private static string WrapOperand(string s)
        => HasTopLevelOr(s) ? "(" + s + ")" : s;

    /// <summary>括号深度为 0 处是否出现 <c>||</c> 或三目 <c>?</c>（&amp;&amp; 优先级更高，必须括起来）。</summary>
    private static bool HasTopLevelOr(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (depth == 0 && c == '?') return true;
            else if (depth == 0 && c == '|' && i + 1 < s.Length && s[i + 1] == '|') return true;
        }
        return false;
    }

    /// <summary><c>if (C) { ...终断... } else { B }</c> → <c>if (C) { ...终断... }</c> + B。</summary>
    private static (int, List<string>)? DropElseAfterTerminator(List<string> lines, int i)
    {
        if (lines[i].Trim() != "else") return null;
        var ind = Indent(lines[i]);
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var elseClose = MatchBrace(lines, brace);
        if (elseClose < 0) return null;

        // 找到上面的 if 块
        var ifClose = PrevNonEmpty(lines, i - 1);
        if (ifClose < 0 || lines[ifClose].Trim() != "}") return null;
        var ifBrace = MatchBraceBack(lines, ifClose);
        if (ifBrace < 0) return null;
        var ifHeader = HeaderOfBrace(lines, ifBrace);
        if (ifHeader < 0 || Indent(lines[ifHeader]) != ind) return null;
        if (!lines[ifHeader].Trim().StartsWith("if (")) return null;

        var lastStmt = PrevNonEmpty(lines, ifClose - 1);
        if (lastStmt <= ifHeader) return null;
        if (!IsTerminator(lines[lastStmt].Trim())) return null;

        var body = Dedent(lines, brace + 1, elseClose);
        return (elseClose, body);
    }

    /// <summary>
    /// <c>while (true) { A = e1; B = e2; … if (!C) break; BODY }</c> → <c>while (true) { if (!C') break; BODY }</c>，
    /// 其中 C' 是把前导纯赋值代进 C 的结果（随后由 <see cref="WhileTrueGuardFirst"/> 变成 <c>while (C')</c>）。
    /// 成立条件：被消去的临时量在整个函数里只出现在「赋值行 + 该守卫」两处，
    /// 因此 `continue`/`break`/循环退出等所有路径的求值次数都与原样一致 —— 无需假设被调用函数纯。
    /// </summary>
    private static (int, List<string>)? FoldGuardPreamble(List<string> lines, int i)
    {
        if (lines[i].Trim() != "while (true)") return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;

        var defs = new List<(int Line, string Name, string Rhs)>();
        var k = brace + 1;
        while (k < close)
        {
            var m = SimpleAssign(lines[k]);
            if (!m.HasValue || !IsTempLike(m.Value.Name)) break;
            defs.Add((k, m.Value.Name, m.Value.Rhs));
            k++;
        }
        if (defs.Count == 0) return null;

        var guard = NextNonEmpty(lines, k);
        if (guard != k || guard >= close) return null;
        if (!TryParseGuard(lines[guard].Trim(), out var guardCond)) return null;

        // 代换表（按出现顺序）
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, name, rhs) in defs)
        {
            if (map.ContainsKey(name)) return null;      // 循环内重复赋值 → 不能折叠
            map[name] = rhs;
        }

        var cond = Substitute(guardCond, map);

        // 安全性：这些临时量在函数其它任何地方（声明行除外）都不得出现
        foreach (var name in map.Keys)
        {
            var total = 0;
            foreach (var line in lines)
            {
                if (IsDeclLine(line)) continue;
                total += CountWord(line, name);
            }
            var expected = defs.Sum(d => CountWord(lines[d.Line], name)) + CountWord(lines[guard], name);
            if (total != expected) return null;
        }
        // 代换必须彻底
        foreach (var name in map.Keys)
            if (CountWord(cond, name) > 0) return null;

        var ind = Indent(lines[i]);
        var repl = new List<string>
        {
            lines[i],
            lines[brace],
            Pad(ind + 4) + $"if (!{cond}) break;"
        };
        return (guard, repl);
    }

    private static string Substitute(string expr, Dictionary<string, string> map)
    {
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            foreach (var (name, rhs) in map)
            {
                var pattern = $@"(?<![\w.]){Regex.Escape(name)}(?![\w.])";
                expr = Regex.Replace(expr, pattern, _ =>
                {
                    changed = true;
                    return NeedsParens(rhs) ? "(" + rhs + ")" : rhs;
                });
            }
            if (!changed) break;
        }
        return expr;
    }

    /// <summary>表达式含顶层运算符/逗号时需要加括号才能安全嵌入。</summary>
    private static bool NeedsParens(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && (c == ',' || c == '?' || c == ':')) return true;
            else if (depth == 0 && (c == '+' || c == '*' || c == '/' || c == '%' || c == '<' || c == '>' || c == '=' || c == '&' || c == '|' || c == '^'))
                return true;
            else if (depth == 0 && c == '-' && i > 0) return true;
        }
        return false;
    }

    private static (string Name, string Rhs)? SimpleAssign(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || !t.EndsWith(';')) return null;
        if (t.Contains("==") || t.Contains("!=") || t.Contains("<=") || t.Contains(">=")) return null;
        var eq = t.IndexOf('=');
        if (eq <= 0) return null;
        var name = t[..eq].Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return null;
        var rhs = t[(eq + 1)..^1].Trim();
        return rhs.Length == 0 ? null : (name, rhs);
    }

    private static bool IsTempLike(string name) =>
        name.StartsWith("CallFunc_") || name.Contains("ReturnValue") || name.StartsWith("Temp_");

    /// <summary>形如 <c>int32 a, b;</c> 的局部变量声明行（不是「使用」）。</summary>
    private static bool IsDeclLine(string line)
    {
        var t = line.Trim();
        if (t.Length < 3 || !t.EndsWith(';') || t.Contains('=') || t.Contains('(') || t.Contains(')')) return false;
        if (t.StartsWith("return") || t.StartsWith("break") || t.StartsWith("continue") || t.StartsWith("goto")) return false;
        return Regex.IsMatch(t, @"^[\w:<>*&,\s]+;$");
    }

    private static int CountWord(string line, string word)
    {
        var n = 0;
        var from = 0;
        while (true)
        {
            var idx = line.IndexOf(word, from, StringComparison.Ordinal);
            if (idx < 0) return n;
            var before = idx == 0 || (!char.IsLetterOrDigit(line[idx - 1]) && line[idx - 1] != '_' && line[idx - 1] != '.');
            var end = idx + word.Length;
            var after = end >= line.Length || (!char.IsLetterOrDigit(line[end]) && line[end] != '_');
            if (before && after) n++;
            from = idx + 1;
        }
    }

    /// <summary>循环体末尾多余的 <c>continue;</c>（其后只有闭合括号）删除。</summary>
    private static (int, List<string>)? StripTrailingContinue(List<string> lines, int i)
    {
        if (lines[i].Trim() != "continue;") return null;
        var k = NextNonEmpty(lines, i + 1);
        if (k < 0 || lines[k].Trim() != "}") return null;
        // [fix] 只有当这个 } 一路外推都落在「循环体的最后位置」时 continue 才是多余的。
        // 原实现只看下一行是 }，会把 if 分支里的 continue 也删掉，分支随即穿透到后面的语句。
        if (!IsLoopBodyTail(lines, k)) return null;
        return (i, new List<string>());
    }

    private static int OpenBraceOf(List<string> lines, int close)
    {
        var depth = 0;
        for (var j = close; j >= 0; j--)
        {
            var t = lines[j].Trim();
            if (t == "}") depth++;
            else if (t == "{" && --depth == 0) return j;
        }
        return -1;
    }

    private static bool IsLoopBodyTail(List<string> lines, int close)
    {
        for (var guard = 0; guard < 64; guard++)
        {
            var open = OpenBraceOf(lines, close);
            if (open < 0) return false;
            var h = open - 1;
            while (h >= 0 && lines[h].Trim().Length == 0) h--;
            if (h < 0) return false;
            var hdr = lines[h].Trim();
            if (hdr.StartsWith("while (") || hdr.StartsWith("for (") || hdr == "do") return true;
            if (!(hdr.StartsWith("if (") || hdr.StartsWith("else"))) return false;

            // 跳过整条 if / else if / else 链，链后面必须没有别的语句，只剩外层的 }
            var ind = Indent(lines[h]);
            var end = close;
            while (true)
            {
                var j = NextNonEmpty(lines, end + 1);
                if (j < 0 || !lines[j].Trim().StartsWith("else") || Indent(lines[j]) != ind) break;
                if (!BlockBraceAt(lines, j, out var eb)) return false;
                var ec = MatchBrace(lines, eb);
                if (ec < 0) return false;
                end = ec;
            }
            var k = NextNonEmpty(lines, end + 1);
            if (k < 0 || lines[k].Trim() != "}") return false;
            close = k;
        }
        return false;
    }

    /// <summary><c>while (true) { if (!C) break; BODY }</c> → <c>while (C) { BODY }</c>。</summary>
    private static (int, List<string>)? WhileTrueGuardFirst(List<string> lines, int i)
    {
        if (lines[i].Trim() != "while (true)") return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;
        var first = NextNonEmpty(lines, brace + 1);
        if (first < 0 || first >= close) return null;
        if (!TryParseGuard(lines[first].Trim(), out var cond)) return null;

        var ind = Indent(lines[i]);
        var repl = new List<string> { Pad(ind) + $"while ({cond})", Pad(ind) + "{" };
        repl.AddRange(Slice(lines, first + 1, close));
        repl.Add(Pad(ind) + "}");
        return (close, repl);
    }

    /// <summary><c>while (true) { BODY if (!C) break; }</c> → <c>do { BODY } while (C);</c></summary>
    private static (int, List<string>)? WhileTrueGuardLast(List<string> lines, int i)
    {
        if (lines[i].Trim() != "while (true)") return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;
        var last = PrevNonEmpty(lines, close - 1);
        if (last <= brace) return null;
        if (Indent(lines[last]) != Indent(lines[i]) + 4) return null;
        if (!TryParseGuard(lines[last].Trim(), out var cond)) return null;
        if (HasTopLevelContinue(lines, brace + 1, close)) return null;   // continue 语义会变

        var ind = Indent(lines[i]);
        var repl = new List<string> { Pad(ind) + "do", Pad(ind) + "{" };
        repl.AddRange(Slice(lines, brace + 1, last));
        repl.Add(Pad(ind) + $"}} while ({cond});");
        return (close, repl);
    }

    /// <summary>void 函数体末尾多余的 <c>return;</c> 删除。</summary>
    private static (int, List<string>)? DropTrailingReturn(List<string> lines, int i)
    {
        if (lines[i].Trim() != "return;") return null;
        var k = NextNonEmpty(lines, i + 1);
        if (k < 0 || lines[k].Trim() != "}" || Indent(lines[k]) != 0) return null;
        if (Indent(lines[i]) != 4) return null;
        return (i, new List<string>());
    }

    /// <summary><c>if (C) { B } else { }</c> → <c>if (C) { B }</c>。</summary>
    private static (int, List<string>)? DropEmptyElse(List<string> lines, int i)
    {
        if (lines[i].Trim() != "else") return null;
        if (!BlockBraceAt(lines, i, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;
        if (NextNonEmpty(lines, brace + 1) != close) return null;
        return (close, new List<string>());
    }

    private static bool HasTopLevelContinue(List<string> lines, int from, int to)
    {
        var baseIndent = -1;
        for (var i = from; i < to; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (baseIndent < 0) baseIndent = Indent(lines[i]);
            if (lines[i].Trim() == "continue;" && Indent(lines[i]) == baseIndent) return true;
        }
        return false;
    }

    /// <summary>解析 <c>if (!C) break;</c>（breakWhenTrue 表示 <c>if (C) break;</c>）。</summary>
    internal static bool TryParseGuard(string t, out string cond)
    {
        cond = "";
        if (!t.StartsWith("if (") || !t.EndsWith("break;")) return false;
        var inner = t.Substring(4, t.Length - 4 - "break;".Length).Trim();
        if (!inner.EndsWith(")")) return false;
        inner = inner.Substring(0, inner.Length - 1).Trim();
        inner = StripOuterParens(inner);
        if (inner.StartsWith('!'))
        {
            cond = StripOuterParens(inner.Substring(1).Trim());
            return cond.Length > 0;
        }
        cond = "!" + StripOuterParens(inner);
        return cond.Length > 1;
    }

    // ================= for 循环还原 =================

    private static readonly Regex InitAssign = new(@"^(?<iv>[A-Za-z_]\w*)\s*=\s*(?<init>.+);$", RegexOptions.Compiled);
    private static readonly Regex StepAssign = new(@"^(?<iv>[A-Za-z_]\w*)\s*=\s*(?<iv2>[A-Za-z_]\w*)\s*(?<op>[+\-])\s*(?<step>.+);$", RegexOptions.Compiled);
    private static readonly Regex SimpleConstAssign = new(@"^[A-Za-z_]\w*\s*=\s*[A-Za-z_0-9.]+;$", RegexOptions.Compiled);

    /// <summary>
    /// 经典计数循环还原：
    /// <code>
    /// IV = init;                     for (IV = init; cond; IV++ )
    /// while (cond)          ==>      {
    /// {                                  BODY
    ///     BODY                       }
    ///     IV = IV + step;
    /// }
    /// </code>
    /// 成立条件：步进是该层最后一条语句；循环体内部不再给 IV 赋值；循环体内没有 continue
    /// （continue 会跳过步进，语义就不等价了）；IV 出现在 while 条件里。
    /// </summary>
    private static (int End, List<string> Repl)? RecoverForLoop(List<string> lines, int i)
    {
        // 触发行必须是一条「简单常量赋值」，并且从它往下的 1~4 行内（全是同类赋值）就是 while。
        if (!SimpleConstAssign.IsMatch(lines[i].Trim())) return null;
        var w = i + 1;
        while (w < lines.Count && w - i <= 4 && SimpleConstAssign.IsMatch(lines[w].Trim())) w++;
        if (w >= lines.Count || w - i > 4) return null;
        var t = lines[w].Trim();
        if (!t.StartsWith("while (") || Indent(lines[w]) != Indent(lines[i])) return null;
        var cond = ParenAfter(t, 6);
        if (cond is null) return null;

        // 在这几行里找出「被 while 条件使用」的那个变量，它就是循环变量。
        string? iv = null, init = null;
        for (var k = i; k < w; k++)
        {
            var m2 = InitAssign.Match(lines[k].Trim());
            if (!m2.Success) return null;
            var cand = m2.Groups["iv"].Value;
            if (!Regex.IsMatch(cond, $@"(?<![\w.]){Regex.Escape(cand)}(?![\w.])")) continue;
            if (iv is not null) return null;              // 多个候选 → 不猜
            iv = cand;
            init = m2.Groups["init"].Value.Trim();
        }
        if (iv is null || init is null) return null;

        if (!BlockBraceAt(lines, w, out var brace)) return null;
        var close = MatchBrace(lines, brace);
        if (close < 0) return null;

        var last = PrevNonEmpty(lines, close - 1);
        if (last <= brace) return null;
        var sm = StepAssign.Match(lines[last].Trim());
        if (!sm.Success) return null;
        if (sm.Groups["iv"].Value != iv || sm.Groups["iv2"].Value != iv) return null;

        // 循环体内不能再有对 IV 的赋值，也不能有 continue（continue 会跳过步进）
        for (var k = brace + 1; k < last; k++)
        {
            var b = lines[k].Trim();
            if (b.StartsWith("continue")) return null;
            if (Regex.IsMatch(b, $@"(?<![\w.]){Regex.Escape(iv)}\s*(?:[+\-*/%&|^]|\+\+|--)?=(?!=)")) return null;
        }

        var op = sm.Groups["op"].Value;
        var step = sm.Groups["step"].Value.Trim();
        var inc = step == "1" ? (op == "+" ? "++" : "--") : $"{op}= {step}";
        var header = $"for ({iv} = {init}; {cond}; {iv}{inc})";

        var repl = new List<string> { Pad(Indent(lines[w])) + header, lines[brace] };
        repl.AddRange(lines.GetRange(brace + 1, last - brace - 1));
        repl.Add(lines[close]);
        return (close, repl);
    }

    /// <summary>取 <paramref name="s"/> 中 <paramref name="open"/> 处 '(' 配对括号内的内容（要求它就是收尾括号）。</summary>
    private static string? ParenAfter(string s, int open)
    {
        if (open >= s.Length || s[open] != '(') return null;
        var depth = 0;
        for (var k = open; k < s.Length; k++)
        {
            if (s[k] == '(') depth++;
            else if (s[k] == ')' && --depth == 0)
                return k == s.Length - 1 ? s[(open + 1)..k].Trim() : null;
        }
        return null;
    }

    // ================= 行内整理 =================
    private static readonly Regex DoubleParenCond = new(@"(!\()\(([^()]*)\)(\))", RegexOptions.Compiled);
    private static readonly Regex IfWhileHead = new(@"^(?<ind>\s*)(?<kw>if|while) \((?<c>.*)\)$", RegexOptions.Compiled);
    private static readonly Regex AssignRhs = new(@"^(?<head>\s*[\w.\[\]]+\s*=\s*)(?<rhs>.+);$", RegexOptions.Compiled);

    private static string TidyLine(string s)
    {
        var t = s;
        if (t.Contains("!("))
        {
            string prev;
            do
            {
                prev = t;
                t = DoubleParenCond.Replace(t, "$1$2$3");
            } while (t != prev);
        }
        var m = IfWhileHead.Match(t);
        if (m.Success)
        {
            var c = m.Groups["c"].Value.Trim();
            var stripped = StripOuterParens(c);
            if (stripped != c && stripped.Length > 0)
                t = $"{m.Groups["ind"].Value}{m.Groups["kw"].Value} ({stripped})";
        }
        return TrimLineParens(NegateComparison(t));
    }

    /// <summary>
    /// <c>!(A &lt; B)</c> → <c>A &gt;= B</c>：把一元取反换成反向比较符（只在一个布尔上下文里做）。
    /// </summary>
    private static string NegateComparison(string line)
    {
        var idx = line.IndexOf("!(", StringComparison.Ordinal);
        if (idx < 0) return line;
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        while (idx >= 0)
        {
            sb.Append(line, pos, idx - pos);
            var close = MatchParenForward(line, idx + 1);
            var prev = idx > 0 ? line[idx - 1] : ' ';
            var next = close >= 0 && close + 1 < line.Length ? line[close + 1] : '\0';
            if (close < 0 || char.IsLetterOrDigit(prev) || prev == '_' || prev == '.'
                || (next != '\0' && next != ')' && next != ';' && next != ',' && next != '&' && next != '|' && next != ' '))
            {
                sb.Append("!(");
                pos = idx + 2;
                idx = line.IndexOf("!(", pos, StringComparison.Ordinal);
                continue;
            }
            var inner = line[(idx + 2)..close].Trim();
            if (FlipComparison(inner, out var flipped))
            {
                sb.Append(flipped);
                pos = close + 1;
            }
            else if (SimpleUnaryOperand.IsMatch(inner))
            {
                sb.Append('!').Append(inner);        // !(x) → !x；! 优先级最高，去掉括号是安全的
                pos = close + 1;
            }
            else
            {
                sb.Append("!(");
                pos = idx + 2;
            }
            idx = line.IndexOf("!(", pos, StringComparison.Ordinal);
        }
        sb.Append(line, pos, line.Length - pos);
        return sb.ToString();
    }

    private static readonly Regex SimpleUnaryOperand = new(@"^[\w.]+(\(.*\))?$", RegexOptions.Compiled);

    private static int MatchParenForward(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>把一个「最外层恰好一个比较运算符」的表达式取反。</summary>
    private static bool FlipComparison(string inner, out string flipped)
    {
        flipped = "";
        if (inner.Length == 0 || inner[0] == '(') return false;
        var depth = 0;
        var opAt = -1;
        var opLen = 0;
        var inStr = false;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '"' && (i == 0 || inner[i - 1] != '\\')) { inStr = !inStr; continue; }
            if (inStr) continue;
            if (c is '(' or '[') { depth++; continue; }
            if (c is ')' or ']') { depth--; continue; }
            if (depth != 0) continue;
            // [fix] 最外层有 && / || / ?: 时，!(A || B != C) 不能只翻转其中一个比较（会丢掉 ! 并保留 ||，
            // 违反德摩根）。宁可保留 !(...)。
            if ((c == '&' || c == '|') && i + 1 < inner.Length && inner[i + 1] == c) return false;
            if (c == '?') return false;
            if (i + 1 < inner.Length && (inner[i + 1] == '=' || inner[i + 1] == c) && (c is '<' or '>' or '=' or '!'))
            {
                if (opAt >= 0) return false;                  // 多个比较 → 不猜
                opAt = i;
                opLen = 2;
                i++;
                continue;
            }
            if (c is '<' or '>')
            {
                if (opAt >= 0) return false;
                opAt = i;
                opLen = 1;
            }
        }
        if (opAt < 0) return false;
        var op = inner.Substring(opAt, opLen);
        var nop = op switch
        {
            "==" => "!=", "!=" => "==", "<" => ">=", ">" => "<=", "<=" => ">", ">=" => "<", _ => null
        };
        if (nop is null) return false;
        var a = inner[..opAt].Trim();
        var b = inner[(opAt + opLen)..].Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        flipped = $"{a} {nop} {b}";
        return true;
    }

    private static string TrimLineParens(string line)
    {
        var m = AssignRhs.Match(line);
        if (!m.Success) return line;
        var rhs = m.Groups["rhs"].Value.Trim();
        var stripped = StripOuterParens(rhs);
        if (stripped == rhs || stripped.Length == 0) return line;
        if (HasTopLevelComma(stripped)) return line;
        return m.Groups["head"].Value + stripped + ";";
    }

    private static bool HasTopLevelComma(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>') depth--;
            else if (c == ',' && depth == 0) return true;
        }
        return false;
    }

    /// <summary>若整个串被一对最外层括号包住则去掉它们。</summary>
    internal static string StripOuterParens(string s)
    {
        var t = s.Trim();
        var changed = false;
        while (t.Length >= 2 && t[0] == '(' && t[^1] == ')')
        {
            var depth = 0;
            var wraps = true;
            for (var i = 0; i < t.Length; i++)
            {
                if (t[i] == '(') depth++;
                else if (t[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i != t.Length - 1) { wraps = false; break; }
                }
            }
            if (!wraps) break;
            t = t.Substring(1, t.Length - 2).Trim();
            changed = true;
        }
        return changed ? t : s.Trim();
    }
}
