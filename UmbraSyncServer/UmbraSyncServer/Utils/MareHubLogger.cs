using MareSynchronosServer.Hubs;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UmbraSync.API.Data;

namespace MareSynchronosServer.Utils;

public class MareHubLogger
{
    private const int MaxStringLength = 64;
    private static readonly string[] IdentifierNames = ["UID", "GID", "Id"];

    // Borné par le nombre de types passés aux logs : un accesseur par type, jamais par utilisateur.
    private static readonly ConcurrentDictionary<Type, (string Label, Func<object, object> Getter)[]> IdentifierAccessors = new();

    private readonly MareHub _hub;
    private readonly ILogger<MareHub> _logger;

    public MareHubLogger(MareHub hub, ILogger<MareHub> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public ILogger Logger => _logger;

    public static object[] Args(params object[] args)
    {
        return args;
    }

    public void LogCallInfo(object[] args = null, [CallerMemberName] string methodName = "")
    {
        _logger.LogInformation("{uid}:{method}{args}", _hub.UserUID, methodName, FormatArgs(args));
    }

    public void LogCallWarning(object[] args = null, [CallerMemberName] string methodName = "")
    {
        _logger.LogWarning("{uid}:{method}{args}", _hub.UserUID, methodName, FormatArgs(args));
    }

    public void LogWarning(Exception ex, string message, params object[] args)
    {
        _logger.LogWarning(ex, $"{{uid}}:{message}", [_hub.UserUID, .. args]);
    }

    // Les journaux ne doivent contenir ni contenu personnel ni secret : seuls les types simples
    // et les identifiants techniques des DTO sont écrits, jamais le ToString() d'un objet.
    public static string FormatArgs(object[] args)
    {
        if (args == null || args.Length == 0) return string.Empty;
        return "|" + string.Join(":", args.Select(FormatArg));
    }

    public static string FormatArg(object arg)
    {
        switch (arg)
        {
            case null:
                return "null";
            case string s:
                return Truncate(s);
            case Enum or Guid or decimal or DateTime or DateTimeOffset or TimeSpan:
                return Convert.ToString(arg, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var type = arg.GetType();
        if (type.IsPrimitive)
            return Convert.ToString(arg, CultureInfo.InvariantCulture) ?? string.Empty;

        if (arg is ICollection collection)
            return $"{type.Name}[{collection.Count}]";

        // Une séquence paresseuse n'est pas énumérée : on n'écrit que son type.
        if (arg is IEnumerable)
            return type.Name;

        return FormatObject(arg, type);
    }

    private static string FormatObject(object arg, Type type)
    {
        var accessors = IdentifierAccessors.GetOrAdd(type, BuildAccessors);
        if (accessors.Length == 0) return type.Name;

        List<string> parts = new(accessors.Length);
        foreach (var (label, getter) in accessors)
        {
            object value;
            try
            {
                value = getter(arg);
            }
            catch
            {
                // Un accesseur calculé peut lever sur un DTO incomplet : l'identifiant est simplement omis.
                continue;
            }

            if (value == null) continue;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(text)) continue;
            parts.Add(label + "=" + Truncate(text));
        }

        return parts.Count == 0 ? type.Name : $"{type.Name}({string.Join(",", parts)})";
    }

    private static (string Label, Func<object, object> Getter)[] BuildAccessors(Type type)
    {
        List<(string Label, Func<object, object> Getter)> accessors = [];
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToList();

        foreach (var name in IdentifierNames)
        {
            var property = properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal) && IsIdentifierType(p.PropertyType));
            if (property != null)
                accessors.Add((name, property.GetValue));
        }

        if (!accessors.Exists(a => string.Equals(a.Label, "UID", StringComparison.Ordinal)))
        {
            var userProperty = properties.FirstOrDefault(p => p.PropertyType == typeof(UserData));
            if (userProperty != null)
                accessors.Add(("UID", o => (userProperty.GetValue(o) as UserData)?.UID));
        }

        if (!accessors.Exists(a => string.Equals(a.Label, "GID", StringComparison.Ordinal)))
        {
            var groupProperty = properties.FirstOrDefault(p => p.PropertyType == typeof(GroupData));
            if (groupProperty != null)
                accessors.Add(("GID", o => (groupProperty.GetValue(o) as GroupData)?.GID));
        }

        return [.. accessors];
    }

    private static bool IsIdentifierType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string) || underlying == typeof(Guid) || underlying.IsPrimitive;
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxStringLength ? value : value[..MaxStringLength] + "…";
    }
}
