using System.Reflection;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetKismet;

/// <summary>函数参数 / 返回值 / 局部变量的类型还原信息。</summary>
public sealed class FnVar
{
    public string Name = "";
    public string Type = "auto";
    public bool IsOut;
    public bool IsRef;
    public bool IsConst;
    public bool IsReturn;
    public bool IsParam;
}

public sealed class FnSignature
{
    public string ReturnType = "void";
    public string ReturnName = "";
    public readonly List<FnVar> Params = new();
    public readonly List<FnVar> Locals = new();

    /// <summary>参数列表文本，例如 <c>int32 a, out int32 b</c>。</summary>
    public string ParamList()
    {
        var parts = new List<string>(Params.Count);
        foreach (var p in Params)
        {
            var mod = p.IsOut ? "out " : p.IsRef ? "ref " : p.IsConst ? "const " : "";
            parts.Add($"{mod}{p.Type} {p.Name}");
        }
        return string.Join(", ", parts);
    }
}

/// <summary>
/// 从 <c>FunctionExport.LoadedProperties</c> 还原蓝图函数签名。
/// 参数的 <c>CPF_Parm</c> / <c>CPF_OutParm</c> / <c>CPF_ReturnParm</c> 标志与属性类型都是资产里原生的，
/// 因此签名是精确的（而不是从字节码猜的）。
/// </summary>
public static class FunctionSignatures
{
    private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

    public static FnSignature Analyze(UAsset asset, FunctionExport fn)
    {
        var sig = new FnSignature();
        var props = fn.LoadedProperties;
        if (props is null) return sig;

        foreach (var p in props)
        {
            var name = p.Name.ToString();
            var flags = p.PropertyFlags;
            var v = new FnVar { Name = name, Type = CppType(asset, p) };
            var isParm = flags.HasFlag(EPropertyFlags.CPF_Parm);
            if (isParm && flags.HasFlag(EPropertyFlags.CPF_ReturnParm))
            {
                v.IsReturn = true;
                sig.ReturnType = v.Type;
                sig.ReturnName = name;
                continue;
            }
            if (isParm)
            {
                v.IsParam = true;
                v.IsOut = flags.HasFlag(EPropertyFlags.CPF_OutParm) && !flags.HasFlag(EPropertyFlags.CPF_ConstParm);
                v.IsRef = flags.HasFlag(EPropertyFlags.CPF_ReferenceParm) && !v.IsOut;
                v.IsConst = flags.HasFlag(EPropertyFlags.CPF_ConstParm);
                sig.Params.Add(v);
                continue;
            }
            sig.Locals.Add(v);
        }
        return sig;
    }

    /// <summary>局部变量分组声明（同一类型合并成一行），编译器临时量一并列出以便对照。</summary>
    public static List<string> LocalDeclarations(FnSignature sig)
    {
        var lines = new List<string>();
        foreach (var g in sig.Locals.GroupBy(v => v.Type).OrderBy(g => g.Key))
            lines.Add($"{g.Key} {string.Join(", ", g.Select(v => v.Name))};");
        return lines;
    }

    /// <summary>完整签名行，如 <c>int32 GetTotalDefense(Card* card, bool bIncludeBonus)</c>。</summary>
    public static string SignatureLine(FnSignature sig, string funcName)
    {
        var ret = sig.ReturnType.Length == 0 ? "void" : sig.ReturnType;
        return $"{ret} {funcName}({sig.ParamList()})";
    }

    // ================= 类型还原 =================

    /// <summary>属性 → C++ 风格类型名。</summary>
    public static string CppType(UAsset asset, object p, int depth = 0)
    {
        if (depth > 4) return "auto";
        var bySerialized = BySerialized(asset, SerializedTypeOf(p), p, depth);
        if (bySerialized is not null) return bySerialized;

        return p.GetType().Name switch
        {
            "FBoolProperty" => "bool",
            "FByteProperty" => ByteOrEnum(asset, p, depth),
            "FEnumProperty" => Short(RefName(asset, F(p, "Enum"))) ?? "uint8",
            "FInt8Property" => "int8",
            "FInt16Property" => "int16",
            "FUInt16Property" => "uint16",
            "FIntProperty" => "int32",
            "FUInt32Property" => "uint32",
            "FInt64Property" => "int64",
            "FUInt64Property" => "uint64",
            "FFloatProperty" => "float",
            "FDoubleProperty" => "double",
            "FStrProperty" => "FString",
            "FNameProperty" => "FName",
            "FTextProperty" => "FText",
            "FObjectProperty" => Short(RefName(asset, F(p, "PropertyClass"))) is { } c ? $"{c}*" : "UObject*",
            "FWeakObjectProperty" => $"TWeakObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>",
            "FLazyObjectProperty" => $"TLazyObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>",
            "FSoftObjectProperty" => $"TSoftObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>",
            "FClassProperty" => $"UClass* /*{Short(RefName(asset, F(p, "MetaClass") ?? F(p, "PropertyClass"))) ?? "?"}*/",
            "FSoftClassProperty" => $"TSoftClassPtr<{Short(RefName(asset, F(p, "MetaClass") ?? F(p, "PropertyClass"))) ?? "UObject"}>",
            "FInterfaceProperty" => $"TScriptInterface<{Short(RefName(asset, F(p, "InterfaceClass"))) ?? "IInterface"}>",
            "FStructProperty" => Short(RefName(asset, F(p, "Struct"))) is { } st ? StructName(st) : "FStruct",
            "FArrayProperty" => Inner(asset, p, depth) is { } ai ? $"TArray<{ai}>" : "TArray<auto>",
            "FSetProperty" => Inner(asset, p, depth) is { } si ? $"TSet<{si}>" : "TSet<auto>",
            "FMapProperty" => MapType(asset, p, depth),
            "FDelegateProperty" => $"FDelegate /*{Short(RefName(asset, F(p, "SignatureFunction"))) ?? ""}*/",
            "FMulticastDelegateProperty" or "FMulticastInlineDelegateProperty" or "FMulticastSparseDelegateProperty"
                => $"FMulticastDelegate /*{Short(RefName(asset, F(p, "SignatureFunction"))) ?? ""}*/",
            "FFieldPathProperty" => "FFieldPath",
            _ => "auto"
        };
    }

    /// <summary>usmap / 属性标签里带的原始类型名优先（最可靠）。</summary>
    private static string? BySerialized(UAsset asset, string? st, object p, int depth)
    {
        switch (st)
        {
            case null: return null;
            case "BoolProperty": return "bool";
            case "ByteProperty": return ByteOrEnum(asset, p, depth);
            case "EnumProperty": return Short(RefName(asset, F(p, "Enum"))) ?? "uint8";
            case "Int8Property": return "int8";
            case "Int16Property": return "int16";
            case "UInt16Property": return "uint16";
            case "IntProperty": return "int32";
            case "UInt32Property": return "uint32";
            case "Int64Property": return "int64";
            case "UInt64Property": return "uint64";
            case "FloatProperty": return "float";
            case "DoubleProperty": return "double";
            case "StrProperty": return "FString";
            case "NameProperty": return "FName";
            case "TextProperty": return "FText";
            case "ObjectProperty":
                return Short(RefName(asset, F(p, "PropertyClass"))) is { } c ? $"{c}*" : "UObject*";
            case "WeakObjectProperty":
                return $"TWeakObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>";
            case "LazyObjectProperty":
                return $"TLazyObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>";
            case "SoftObjectProperty":
                return $"TSoftObjectPtr<{Short(RefName(asset, F(p, "PropertyClass"))) ?? "UObject"}>";
            case "ClassProperty":
                return $"UClass* /*{Short(RefName(asset, F(p, "MetaClass") ?? F(p, "PropertyClass"))) ?? "?"}*/";
            case "SoftClassProperty":
                return $"TSoftClassPtr<{Short(RefName(asset, F(p, "MetaClass") ?? F(p, "PropertyClass"))) ?? "UObject"}>";
            case "InterfaceProperty":
                return $"TScriptInterface<{Short(RefName(asset, F(p, "InterfaceClass"))) ?? "IInterface"}>";
            case "StructProperty":
                return Short(RefName(asset, F(p, "Struct"))) is { } so ? StructName(so) : "FStruct";
            case "ArrayProperty":
                return Inner(asset, p, depth) is { } ai ? $"TArray<{ai}>" : "TArray<auto>";
            case "SetProperty":
                return Inner(asset, p, depth) is { } si ? $"TSet<{si}>" : "TSet<auto>";
            case "MapProperty":
                return MapType(asset, p, depth);
            case "FieldPathProperty": return "FFieldPath";
            case "DelegateProperty":
                return $"FDelegate /*{Short(RefName(asset, F(p, "SignatureFunction"))) ?? ""}*/";
            case "MulticastDelegateProperty":
            case "MulticastInlineDelegateProperty":
            case "MulticastSparseDelegateProperty":
                return $"FMulticastDelegate /*{Short(RefName(asset, F(p, "SignatureFunction"))) ?? ""}*/";
            default: return null;
        }
    }

    private static string ByteOrEnum(UAsset asset, object p, int depth)
    {
        var e = F(p, "Enum");
        var n = Short(RefName(asset, e));
        return string.IsNullOrEmpty(n) || n == "None" ? "uint8" : n;
    }

    private static string? Inner(UAsset asset, object p, int depth)
    {
        var inner = F(p, "InnerType") ?? F(p, "Inner");
        return inner is null ? null : CppType(asset, inner, depth + 1);
    }

    private static string MapType(UAsset asset, object p, int depth)
    {
        var k = F(p, "KeyType") ?? F(p, "KeyProp");
        var v = F(p, "ValueType") ?? F(p, "ValueProp");
        var kt = k is null ? "auto" : CppType(asset, k, depth + 1);
        var vt = v is null ? "auto" : CppType(asset, v, depth + 1);
        return $"TMap<{kt}, {vt}>";
    }

    /// <summary>结构体名加上 <c>F</c> 前缀（UE 惯例），已经是 F 开头或蓝图结构体则原样。</summary>
    private static string StructName(string n)
    {
        if (n.Length == 0) return "FStruct";
        if (n.StartsWith("F") && n.Length > 1 && char.IsUpper(n[1])) return n;
        if (n.StartsWith("BP_") || n.StartsWith("S_") || n.StartsWith("E")) return n;
        return "F" + n;
    }

    private static string? SerializedTypeOf(object p)
    {
        var v = F(p, "SerializedType");
        if (v is null) return null;
        var s = v.ToString();
        return string.IsNullOrEmpty(s) || s == "None" ? null : s;
    }

    /// <summary>反射读公共字段/属性（UAssetAPI 各属性类的字段名在不同版本间略有差异）。</summary>
    private static object? F(object o, string name)
    {
        var t = o.GetType();
        var fi = t.GetField(name, Pub);
        if (fi is not null) return fi.GetValue(o);
        var pi = t.GetProperty(name, Pub);
        return pi?.GetValue(o);
    }

    private static string? RefName(UAsset asset, object? idxObj)
    {
        var idx = idxObj switch
        {
            FPackageIndex i => i,
            null => null,
            _ => null
        };
        if (idx is null) return null;
        if (idx.Index > 0 && idx.Index - 1 < asset.Exports.Count)
            return asset.Exports[idx.Index - 1].ObjectName.ToString();
        if (idx.Index < 0 && -idx.Index - 1 < asset.Imports.Count)
            return asset.Imports[-idx.Index - 1].ObjectName.ToString();
        return null;
    }

    /// <summary>去掉 <c>Default__</c> / <c>_C</c> / 包路径，只留短名。</summary>
    private static string? Short(string? n)
    {
        if (string.IsNullOrEmpty(n)) return n;
        var slash = n.LastIndexOf('/');
        if (slash >= 0) n = n[(slash + 1)..];
        var dot = n.LastIndexOf('.');
        if (dot >= 0) n = n[(dot + 1)..];
        if (n.StartsWith("Default__", StringComparison.Ordinal)) n = n["Default__".Length..];
        if (n.EndsWith("_C", StringComparison.Ordinal) && n.Length > 2) n = n[..^2];
        return n;
    }
}
