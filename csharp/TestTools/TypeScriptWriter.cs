using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.TestTools;

/// <summary>
/// C# の型から JS の型定義(.d.ts)を書く。JS は型の検査(tools/dev/typecheck.mjs)でこれを読む。
/// 再生アプリ(Fh6.Player.App.Tests の TypeScriptDefinitions)と記録アプリ(Fh6.Recorder.Tests の ApiTypesTests)が、
/// このファイルを自分のテストに取り込んで使う(テストのプロジェクトどうしは参照しない)
/// </summary>
internal sealed class TypeScriptWriter
{
    private readonly StringBuilder _out = new();
    private readonly Dictionary<string, Type> _names = new(StringComparer.Ordinal);
    private readonly Queue<(Type Type, bool Optional)> _pending = new();
    private readonly NullabilityInfoContext _nullability = new();

    /// <param name="header">ファイルの頭のコメントの行(// は付けない)</param>
    public TypeScriptWriter(params string[] header)
    {
        foreach (var line in header) _out.Append($"// {line}\n");
    }

    /// <summary>type の値で振り分けるメッセージの型。kinds は (型, type の値)</summary>
    public void Union(Type baseType, IReadOnlyList<(Type Type, string Name)> kinds, string comment, bool optional)
    {
        _out.Append($"\n/** {comment} */\ntype {baseType.Name} =\n");
        foreach (var (type, _) in kinds) _out.Append($"  | {type.Name}\n");
        _out.Length--;
        _out.Append(";\n");
        foreach (var (type, name) in kinds) Interface(type, $"type: \"{name}\";", optional);
        Flush();
        if (optional)
        {
            // JS のファイルでは、型に無いプロパティを書いても TypeScript は咎めないので、送る側はこれで止める
            _out.Append($"\n/** {baseType.Name} のうち、type に合う型に無いプロパティ(never にして、書くと型の検査で落ちる) */\n");
            _out.Append($"type ExtraKeys<T extends {baseType.Name}> = Record<Exclude<keyof T, keyof Extract<{baseType.Name}, {{ type: T[\"type\"] }}>>, never>;\n");
        }
    }

    /// <summary>1 つの型と、そこから使っている型</summary>
    public void Root(Type type, string comment, bool optional = false)
    {
        _out.Append($"\n/** {comment} */");
        Interface(type, null, optional);
        Flush();
    }

    private void Flush()
    {
        while (_pending.TryDequeue(out var p)) Interface(p.Type, null, p.Optional);
    }

    private void Interface(Type type, string? first, bool optional)
    {
        if (!Claim(type)) return;
        // 型の印(JsonDerivedType)だけを持つ抽象の型は、派生した型のどれか
        if (type.IsAbstract && DerivedTypes(type) is { Count: > 0 } derived)
        {
            _out.Append($"\ntype {type.Name} =\n");
            foreach (var d in derived) _out.Append($"  | {d.Name}\n");
            _out.Length--;
            _out.Append(";\n");
            foreach (var d in derived) Enqueue(d, optional);
            return;
        }
        _out.Append($"\ninterface {type.Name} {{\n");
        if (first != null) _out.Append($"  {first}\n");
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod != null && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null && p.GetCustomAttribute<JsonExtensionDataAttribute>() == null)
            .Where(p => p.Name != "EqualityContract")
            .OrderBy(p => p.MetadataToken);
        foreach (var p in props)
        {
            var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            var ts = TsType(p.PropertyType, _nullability.Create(p), optional);
            bool nullable = ts.EndsWith(" | null", StringComparison.Ordinal);
            _out.Append($"  {name}{(optional && nullable ? "?" : "")}: {ts};\n");
        }
        _out.Append("}\n");
    }

    private static List<Type> DerivedTypes(Type type) =>
        type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Select(a => a.DerivedType).ToList();

    /// <summary>同じ名前の型が 2 つあると JS の側で区別できないので、作るのをやめる</summary>
    private bool Claim(Type type)
    {
        if (_names.TryGetValue(type.Name, out var known))
        {
            if (known != type) throw new InvalidOperationException($"型の名前 {type.Name} が {known.FullName} と {type.FullName} で重なっている");
            return false;
        }
        _names[type.Name] = type;
        return true;
    }

    private void Enqueue(Type t, bool optional)
    {
        if (!_names.ContainsKey(t.Name) && !_pending.Any(p => p.Type == t)) _pending.Enqueue((t, optional));
        else if (_names.TryGetValue(t.Name, out var known) && known != t)
            throw new InvalidOperationException($"型の名前 {t.Name} が {known.FullName} と {t.FullName} で重なっている");
    }

    private string TsType(Type t, NullabilityInfo n, bool optional)
    {
        if (Nullable.GetUnderlyingType(t) is Type inner) return TsType(inner, n, optional) + " | null";
        var core = CoreType(t, n, optional);
        return !t.IsValueType && n.ReadState == NullabilityState.Nullable ? core + " | null" : core;
    }

    private static readonly HashSet<Type> Numbers =
        [typeof(int), typeof(long), typeof(double), typeof(float), typeof(decimal), typeof(short), typeof(byte), typeof(uint), typeof(ulong), typeof(ushort)];

    private string CoreType(Type t, NullabilityInfo n, bool optional)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "boolean";
        if (Numbers.Contains(t)) return "number";
        if (t.IsArray) return Element(TsType(t.GetElementType()!, n.ElementType!, optional));
        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            if (t.GetGenericTypeDefinition() == typeof(Dictionary<,>) && args[0] == typeof(string))
                return $"Record<string, {TsType(args[1], n.GenericTypeArguments[1], optional)}>";
            if (t.GetGenericTypeDefinition() is var d && (d == typeof(List<>) || d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>)))
                return Element(TsType(args[0], n.GenericTypeArguments[0], optional));
        }
        if (t.IsClass && !t.IsGenericType && t != typeof(object))
        {
            Enqueue(t, optional);
            return t.Name;
        }
        throw new NotSupportedException($"JS の型にできない: {t}");
    }

    private static string Element(string ts) => ts.Contains(' ') ? $"({ts})[]" : $"{ts}[]";

    public override string ToString() => _out.ToString();
}
