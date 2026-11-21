using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fh6.Player;

/// <summary>画面とのメッセージの JSON(ViewMessages.cs と HostMessages.cs)</summary>
public static class MessageJson
{
    /// <summary>プロパティはキャメルケース。type は先頭でなくても読む</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true,
    };

    /// <summary>
    /// 画面からのメッセージを読む。読めなければ(type が無い・知らない、値の型が違う)null を返し、理由を error に入れる。
    /// 画面の不具合でアプリを落とさないため、例外は投げない
    /// </summary>
    public static ViewMessage? Parse(string json, out string? error)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ViewMessage>(json, Options);
            error = message == null ? "null" : null;
            return message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>画面へ送るメッセージの JSON(type が先頭)</summary>
    public static string Serialize(HostMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>メッセージの type の値(ログに出す)</summary>
    public static string TypeName(object message) =>
        Names.TryGetValue(message.GetType(), out var name) ? name : message.GetType().Name;

    /// <summary>メッセージの型と type の値の一覧(base は ViewMessage か HostMessage)</summary>
    public static IReadOnlyList<(Type Type, string Name)> Kinds(Type baseType) =>
        baseType.GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => (a.DerivedType, (string)a.TypeDiscriminator!)).ToList();

    private static readonly Dictionary<Type, string> Names =
        Kinds(typeof(ViewMessage)).Concat(Kinds(typeof(HostMessage))).ToDictionary(k => k.Type, k => k.Name);
}
