// ============================================================================
// KismetDecompiler — UE5 蓝图 Kismet 字节码 → C++ 风格伪代码
//
// 用法:
//   KismetDecompiler --input <BP.uasset|BP.json|目录> --usmap <m.usmap>
//                    [--inline] [--opt] [--uhtdump <dir>] [--out <dir>]
//                    [--ext .cpp] [--jobs n]
//
//   --input    单文件（.uasset / UAssetAPI 导出的 .json）或目录（递归批量）
//   --inline   [实验] dispatch case 体内联回事件函数
//   --opt      [实验] 语义优化：临时量值流内联 / 常量折叠 / 命名简化
//   --uhtdump  UE4SS UHTHeaderDump 目录：import 调用签名（含 out 修饰）
//   --out      输出根目录（默认 ./Decompiled）；镜像输入目录结构
//   --ext      输出扩展名（默认 .cpp）
//   --jobs     批量并发度（默认 1）
//
// 输出：每个资产一个文件（全部函数），不再是每函数一个 txt。
// ============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetKismet;
using UAssetKismet.Experimental;

string GetArg(params string[] names)
{
    foreach (var n in names)
    {
        var i = Array.IndexOf(args, n);
        if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    }
    return "";
}

var inputArg = GetArg("--input", "--uasset");        // 文件或目录（目录 = 批量）
var usmapPath = GetArg("--usmap");
var uhtDump = GetArg("--uhtdump");
var outRoot = GetArg("--out") is { Length: > 0 } o ? o : "Decompiled";
var extArg = GetArg("--ext");
var outExt = extArg is { Length: > 0 } ? (extArg.StartsWith('.') ? extArg : "." + extArg) : ".cpp";
var experimentalInline = args.Contains("--inline");
var optimize = args.Contains("--opt");
var jobs = int.TryParse(GetArg("--jobs"), out var jn) && jn > 0 ? jn : 1;
// --disasm [函数名子串]：转储原始字节码（开发诊断用），不做反编译
var disasmFilter = GetArg("--disasm");
var disasmMode = args.Contains("--disasm");
var flowDebug = args.Contains("--flowdebug");
var cfgDebug = args.Contains("--cfg");

if (inputArg.Length == 0)
{
    Console.WriteLine("用法: KismetDecompiler --input <蓝图.uasset|蓝图.json|目录> --usmap <mapping.usmap> [--inline] [--opt] [--uhtdump <dir>] [--out <dir>] [--ext .cpp] [--jobs n]");
    return;
}

string ApplyOpt(string code)
{
    if (!optimize) return code;
    var lines = code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
    var text = string.Join("\n", SemanticOptimizer.OptimizeLines(lines));
    // 优化会引入括号包裹与新的空块/冗余守卫，再过一遍可读性后处理。
    return UAssetKismet.Experimental.ReadabilityPasses.Run(text) + "\n";
}

// ============================ 1. 收集输入资产 ============================
var assets = new List<(string Full, string Rel)>();
var skipped = new List<(string Rel, string Reason)>();
if (File.Exists(inputArg))
{
    var full = Path.GetFullPath(inputArg);
    assets.Add((full, Path.GetFileName(full)));
}
else if (Directory.Exists(inputArg))
{
    var root = Path.GetFullPath(inputArg);
    foreach (var f in EnumerateFiles(root).OrderBy(x => x, StringComparer.Ordinal))
    {
        var rel = Path.GetRelativePath(root, f);
        var ext = Path.GetExtension(f);
        if (ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase)) { assets.Add((f, rel)); continue; }
        if (!ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
        if (LooksLikeAssetJson(f)) assets.Add((f, rel));
        else if (File.ReadAllText(f).Contains("\"ScriptBytecode\""))
            skipped.Add((rel, "JSON 是导出数组片段（无 NameMap/Imports 包装），UAssetAPI 无法加载"));
    }
}
else
{
    Console.WriteLine($"[错误] 输入不存在: {inputArg}");
    return;
}

if (assets.Count == 0)
{
    Console.WriteLine($"[错误] 未找到可反编译的资产（*.uasset / *.json）: {inputArg}");
    return;
}

Directory.CreateDirectory(outRoot);

// ============================ 2. 共享上下文 ============================
Usmap? usmap = usmapPath.Length > 0 ? new Usmap(usmapPath) : null;   // local: --usmap optional

UhtSignatureIndex? sigIndex = null;
if (uhtDump.Length > 0)
{
    Console.WriteLine($"[签名] 解析 UHTHeaderDump: {uhtDump}");
    var swIdx = Stopwatch.StartNew();
    sigIndex = UhtSignatureIndex.Load(uhtDump);
    Console.WriteLine($"[签名] 索引到 {sigIndex.ByFullKey.Count} 个 UFUNCTION（{swIdx.ElapsedMilliseconds} ms）");
}

// ---- 诊断模式：直接转储字节码 / 执行流栈 / CFG，不做反编译 ----
if (disasmMode || flowDebug || cfgDebug || args.Contains("--fnsig"))
{
    foreach (var (full, rel) in assets)
    {
        var asset = LoadAsset(full);
        Console.WriteLine($"// ===== {rel} =====");
        foreach (var fn in asset.Exports.OfType<FunctionExport>())
        {
            if (fn.ScriptBytecode is not { Length: > 0 }) continue;
            var name = fn.ObjectName.ToString();
            if (disasmFilter.Length > 0 && !name.Contains(disasmFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (args.Contains("--fnsig")) { DumpFnSig(asset, fn); continue; }
            Console.WriteLine(cfgDebug
                ? new StructuredKismetDecompiler(asset, name, sigIndex).DumpCfg(fn)
                : flowDebug
                    ? BytecodeDisassembler.DumpFlowConflicts(asset, fn)
                    : BytecodeDisassembler.DumpFunction(asset, fn, sigIndex));
        }
    }
    return;
}

var modeText = string.Join(" + ", new[]
{
    "file-level",
    experimentalInline ? "inline" : null,
    optimize ? "opt" : null,
    sigIndex is not null ? "uht" : null
}.Where(x => x is not null));

Console.WriteLine($"[批量] {assets.Count} 个资产 | 模式: {modeText} | 输出: {Path.GetFullPath(outRoot)} | jobs={jobs}");
Console.WriteLine();

// ============================ 3. 逐个资产反编译 ============================
var sigSections = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var failures = new ConcurrentBag<(string Rel, string Error)>();
var okCount = 0;
var totalFuncs = 0;
var totalInlined = 0;
var swAll = Stopwatch.StartNew();

void ProcessOne((string Full, string Rel) item, int ordinal)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var asset = LoadAsset(item.Full);
        var (text, sigText, funcCount, inlinedCount) = BuildAssetFile(asset, item.Full, item.Rel);
        sigSections[item.Rel] = sigText;

        var path = Path.Combine(outRoot,
            item.Rel.Substring(0, item.Rel.Length - Path.GetExtension(item.Rel).Length) + outExt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);

        Interlocked.Increment(ref okCount);
        Interlocked.Add(ref totalFuncs, funcCount);
        Interlocked.Add(ref totalInlined, inlinedCount);
        Console.WriteLine($"[{ordinal,3}/{assets.Count}] OK   {item.Rel}  函数 {funcCount}  内联 {inlinedCount}  {sw.ElapsedMilliseconds} ms");
    }
    catch (Exception ex)
    {
        failures.Add((item.Rel, ex.Message));
        Console.WriteLine($"[{ordinal,3}/{assets.Count}] FAIL {item.Rel}  {ex.GetType().Name}: {ex.Message}");
        if (Environment.GetEnvironmentVariable("KISMETD_TRACE") == "1")
            Console.WriteLine(ex.ToString());
    }
}

if (jobs <= 1)
{
    for (var i = 0; i < assets.Count; i++) ProcessOne(assets[i], i + 1);
}
else
{
    var counter = 0;
    Parallel.ForEach(assets, new ParallelOptions { MaxDegreeOfParallelism = jobs },
        item => ProcessOne(item, Interlocked.Increment(ref counter)));
}

// ---- 签名清单（单文件）----
var sigPath = Path.Combine(outRoot, "_signatures.txt");
var sigAll = new StringBuilder();
foreach (var key in sigSections.Keys.OrderBy(x => x, StringComparer.Ordinal))
    sigAll.AppendLine(sigSections[key]);
File.WriteAllText(sigPath, sigAll.ToString());

// ---- 汇总 ----
Console.WriteLine();
Console.WriteLine("================ 汇总 ================");
Console.WriteLine($"资产: {okCount} 成功 / {failures.Count} 失败（共 {assets.Count}）");
Console.WriteLine($"函数: {totalFuncs} 个（dispatch 内联 {totalInlined} 个）");
Console.WriteLine($"输出: {Path.GetFullPath(outRoot)}");
Console.WriteLine($"签名: {Path.GetFullPath(sigPath)}");
Console.WriteLine($"用时: {swAll.Elapsed.TotalSeconds:0.0}s");
foreach (var (rel, err) in failures.OrderBy(f => f.Rel, StringComparer.Ordinal))
    Console.WriteLine($"  [失败] {rel}: {err}");
foreach (var (rel, reason) in skipped.OrderBy(f => f.Rel, StringComparer.Ordinal))
    Console.WriteLine($"  [跳过] {rel}: {reason}");

Environment.ExitCode = failures.IsEmpty ? 0 : 2;

// ============================ 辅助函数 ============================

UAsset LoadAsset(string path) =>
    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        ? UAsset.DeserializeJson(File.ReadAllText(path))
        : new UAsset(path, Enum.Parse<EngineVersion>(Environment.GetEnvironmentVariable("KD_VER") ?? "VER_UE5_6"), usmap, CustomSerializationFlags.None);

/// <summary>反编译单个资产 → 文件级伪代码（资产头 + 函数索引 + 全部函数）。</summary>
(string Text, string SigText, int FuncCount, int InlinedCount)
    BuildAssetFile(UAsset asset, string fullPath, string rel)
{
    var funcs = asset.Exports.OfType<FunctionExport>()
        .Where(f => f.ScriptBytecode is { Length: > 0 }).ToList();
    var uber = funcs.FirstOrDefault(f => f.ObjectName.ToString().StartsWith("ExecuteUbergraph"));
    var uberIdx = uber is null ? -1 : asset.Exports.IndexOf(uber) + 1;

    // 收集 ExecuteUbergraph 调用点: (EntryPoint, 来源函数)
    var calls = new List<(long n, string caller)>();
    if (uber is not null)
    {
        foreach (var fn in funcs)
        {
            if (ReferenceEquals(fn, uber)) continue;
            var n = FindUberCall(fn, uberIdx);
            if (n is not null) calls.Add((n.Value, fn.ObjectName.ToString()));
        }
    }

    var sections = new List<(string Name, string Code)>();
    var inlinedCount = 0;
    foreach (var fn in funcs)
    {
        if (ReferenceEquals(fn, uber)) continue;
        var name = fn.ObjectName.ToString();
        string code;
        string? note = null;
        if (experimentalInline && uber is not null)
        {
            var inlined = DispatchInliner.InlineOne(asset, uber, calls, fn, sigIndex);
            if (inlined is not null) { code = inlined; note = "已内联 dispatch"; inlinedCount++; }
            else code = ApplyOpt(new StructuredKismetDecompiler(asset, name, sigIndex).Decompile(fn));
        }
        else
        {
            code = ApplyOpt(new StructuredKismetDecompiler(asset, name, sigIndex).Decompile(fn));
        }
        sections.Add((name, SectionHeader("Function", name, note) + code));
    }

    // ExecuteUbergraph 反 Dispatch 放最后（inline 模式下作为参考保留完整跳转表）
    if (uber is not null)
    {
        var dispatch = ApplyOpt(StructuredKismetDecompiler.DecompileDispatch(asset, uber, calls, sigIndex));
        sections.Add((uber.ObjectName.ToString(),
            SectionHeader("Function", uber.ObjectName.ToString(),
                experimentalInline ? "参考: 原始反 Dispatch，case 已内联到上方各事件函数" : "反 Dispatch")
            + dispatch));
    }

    var sb = new StringBuilder();
    sb.AppendLine("// " + new string('=', 76));
    sb.AppendLine("// KismetDecompiler — 文件级伪代码");
    sb.AppendLine($"// 资产    : {rel}");
    sb.AppendLine($"// 源文件  : {fullPath}");
    sb.AppendLine($"// usmap   : {usmapPath}");
    if (sigIndex is not null) sb.AppendLine($"// UHT     : {uhtDump} ({sigIndex.ByFullKey.Count} 签名)");
    sb.AppendLine($"// 模式    : {modeText}");
    sb.AppendLine($"// 统计    : 导出 {asset.Exports.Count} 对象 / 含字节码函数 {funcs.Count} / 内联 {inlinedCount}");
    sb.AppendLine($"// 生成    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    sb.AppendLine("// " + new string('=', 76));
    sb.AppendLine();
    if (sections.Count > 0)
    {
        sb.AppendLine("// 函数索引:");
        foreach (var (name, _) in sections) sb.AppendLine($"//   - {name}");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("// （该资产没有含字节码的函数）");
        sb.AppendLine();
    }
    foreach (var (_, code) in sections) sb.AppendLine(code.TrimEnd()).AppendLine();

    return (sb.ToString(), BuildSigText(asset, rel, funcs), funcs.Count, inlinedCount);
}

static string SectionHeader(string kind, string name, string? note)
{
    var sb = new StringBuilder();
    sb.AppendLine("// " + new string('-', 76));
    sb.AppendLine($"// {kind}: {name}{(note is null ? "" : $"  // {note}")}");
    sb.AppendLine("// " + new string('-', 76));
    return sb.ToString();
}

string BuildSigText(UAsset asset, string rel, List<FunctionExport> funcs)
{
    var sb = new StringBuilder();
    sb.AppendLine($"// ===== {rel} =====");
    foreach (var fn in funcs)
        sb.AppendLine("// " + ImportSignatures.BuildLocalSig(fn));

    if (sigIndex is not null)
    {
        var paths = new HashSet<string>();
        foreach (var fn in funcs)
            if (fn.ScriptBytecode is not null)
                ImportSignatures.CollectImportFuncs(asset, fn.ScriptBytecode, paths);

        var hit = paths.Where(p => sigIndex.ByFullKey.ContainsKey(p)).OrderBy(x => x).ToList();
        sb.AppendLine($"// -- import 调用 {paths.Count} 个，命中 UHT dump {hit.Count} 个 --");
        foreach (var p in hit) sb.AppendLine($"// {sigIndex.ByFullKey[p]}");
    }
    sb.AppendLine();
    return sb.ToString();
}

/// <summary>递归枚举非隐藏文件（跳过 .reasonix 等隐藏目录）。</summary>
static IEnumerable<string> EnumerateFiles(string root)
{
    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, f);
        if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith('.')))
            continue;
        yield return f;
    }
}

static bool LooksLikeAssetJson(string path)
{
    try
    {
        var text = File.ReadAllText(path);
        return text.Contains("\"Exports\"") && text.Contains("\"ScriptBytecode\"");
    }
    catch { return false; }
}

// ---- 诊断：函数签名的可用信息（参数属性表 / 局部变量读写） ----
static void DumpFnSig(UAssetAPI.UAsset asset, FunctionExport fn)
{
    Console.WriteLine($"FUNCTION {fn.ObjectName}  flags={fn.FunctionFlags}");
    var props = fn.LoadedProperties;
    Console.WriteLine($"  LoadedProperties: {(props is null ? "null" : props.Length.ToString())}");
    if (props is not null)
        foreach (var p in props)
        {
            var t = p.GetType();
            var fields = t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => $"{f.Name}={f.GetValue(p)}");
            Console.WriteLine($"    - {p.Name} : {t.Name}  flags={p.PropertyFlags}  {string.Join("  ", fields)}");
        }

    var reads = new List<string>();
    var writes = new HashSet<string>(StringComparer.Ordinal);
    void Walk(KismetExpression e)
    {
        switch (e)
        {
            case EX_LocalVariable lv: reads.Add(Path(lv.Variable)); break;
        }
        foreach (var f in e.GetType().GetFields())
        {
            if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
            if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                foreach (var x in arr) Walk(x);
        }
    }
    foreach (var e in fn.ScriptBytecode!) Walk(e);
    var onlyRead = reads.Where(r => !r.StartsWith("out:") && !writes.Contains(r)).Distinct().ToList();
    Console.WriteLine($"  先读后写(疑似参数): {string.Join(", ", onlyRead)}");
    Console.WriteLine($"  被赋值(疑似局部): {string.Join(", ", writes)}");

    static string Path(KismetPropertyPointer? kp) =>
        kp?.New?.Path is { Length: > 0 } p ? string.Join(".", p.Select(x => x.ToString())) : "?";
}

// ---- 查找事件函数里对 ExecuteUbergraph 的调用 ----
static int? FindUberCall(FunctionExport fn, int uberExportIdx){
    int? found = null;
    void Walk(KismetExpression e)
    {
        switch (e)
        {
            case EX_FinalFunction f when f.StackNode.Index == uberExportIdx:
                found = FirstConst(f.Parameters); return;
            case EX_CallMath c when c.StackNode.Index == uberExportIdx:
                found = FirstConst(c.Parameters); return;
            case EX_LocalVirtualFunction l when l.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph"):
                found = FirstConst(l.Parameters); return;
            case EX_VirtualFunction v when v.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph"):
                found = FirstConst(v.Parameters); return;
        }
        foreach (var f in e.GetType().GetFields())
        {
            if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
            if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                foreach (var x in arr) { if (found is not null) return; Walk(x); }
        }
    }
    foreach (var e in fn.ScriptBytecode!) { Walk(e); if (found is not null) break; }
    return found;

    static int? FirstConst(KismetExpression[]? ps)
    {
        if (ps is null || ps.Length == 0) return null;
        var raw = ps[0].GetType().GetField("RawValue")?.GetValue(ps[0]);
        return raw is int i ? i : raw is byte b ? (int)b : null;
    }
}
