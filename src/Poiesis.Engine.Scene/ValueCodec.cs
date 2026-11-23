using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brutal.Numerics;

namespace Poiesis.Engine.Scene;

/// <summary>
/// Conversion entre valeurs .NET et JSON pour les scènes et les données de jeu.
/// </summary>
/// <remarks>
/// <para>
/// Pourquoi pas <c>JsonSerializer</c> : sa réflexion garde les types rencontrés dans des caches statiques globaux.
/// Appliquée aux types du jeu, elle empêche le déchargement de l'assembly au hot reload. Ce codec ne garde rien :
/// il n'utilise que <see cref="JsonNode"/> et la réflexion de base.
/// </para>
/// <para>
/// Types pris en charge : booléens, entiers, flottants, chaînes, <see cref="Guid"/>, enums (en camelCase),
/// <c>float3</c> (en <c>[x, y, z]</c>), tableaux et <see cref="List{T}"/> de ces types, et objets (classes, records,
/// structs) dont les propriétés publiques en lecture-écriture sont elles-mêmes prises en charge.
/// </para>
/// </remarks>
public static class ValueCodec
{
    public static JsonNode? ToJson(object? value, Type type)
    {
        if (value is null)
            return null;
        type = Nullable.GetUnderlyingType(type) ?? type;

        switch (value)
        {
            case bool b: return JsonValue.Create(b);
            case int i: return JsonValue.Create(i);
            case long l: return JsonValue.Create(l);
            case float f: return JsonValue.Create(f);
            case double d: return JsonValue.Create(d);
            case string s: return JsonValue.Create(s);
            case Guid g: return JsonValue.Create(g.ToString());
            case float3 v: return new JsonArray(v.X, v.Y, v.Z);
        }

        if (type.IsEnum)
            return JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()!));

        if (ElementType(type) is { } element)
        {
            var array = new JsonArray();
            foreach (var item in (IEnumerable)value)
                array.Add(ToJson(item, element));
            return array;
        }

        var obj = new JsonObject();
        foreach (var p in DataProperties(type))
            obj[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = ToJson(p.GetValue(value), p.PropertyType);
        return obj;
    }

    public static object? FromJson(JsonNode? node, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (node is null)
            return underlying is not null || !type.IsValueType ? null : Activator.CreateInstance(type);
        type = underlying ?? type;

        if (type == typeof(bool)) return node.GetValue<bool>();
        if (type == typeof(int)) return node.GetValue<int>();
        if (type == typeof(long)) return node.GetValue<long>();
        if (type == typeof(float)) return node.GetValue<float>();
        if (type == typeof(double)) return node.GetValue<double>();
        if (type == typeof(string)) return node.GetValue<string>();
        if (type == typeof(Guid)) return Guid.Parse(node.GetValue<string>());
        if (type == typeof(float3))
        {
            var a = node.AsArray();
            if (a.Count != 3)
                throw new JsonException("float3 attendu sous la forme [x, y, z]");
            return new float3(a[0]!.GetValue<float>(), a[1]!.GetValue<float>(), a[2]!.GetValue<float>());
        }
        if (type.IsEnum)
            return ParseEnum(type, node.GetValue<string>());

        if (ElementType(type) is { } element)
        {
            var items = node.AsArray().Select(n => FromJson(n, element)).ToList();
            if (type.IsArray)
            {
                var array = Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++)
                    array.SetValue(items[i], i);
                return array;
            }
            var list = (IList)Activator.CreateInstance(type)!;
            foreach (var item in items)
                list.Add(item);
            return list;
        }

        var obj = node as JsonObject ?? throw new JsonException($"Objet JSON attendu pour {type.Name}");
        var instance = Activator.CreateInstance(type) ?? throw new JsonException($"Impossible d'instancier {type}");
        foreach (var p in DataProperties(type))
        {
            if (obj[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] is { } value)
                p.SetValue(instance, FromJson(value, p.PropertyType));   // boxé : marche aussi pour les structs
        }
        return instance;
    }

    public static T Read<T>(string json) =>
        (T)FromJson(JsonNode.Parse(json), typeof(T))!;

    public static string Write<T>(T value) =>
        ToJson(value, typeof(T))?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    /// <summary>Propriétés publiques en lecture-écriture (init compris), dans l'ordre de déclaration.</summary>
    public static IEnumerable<PropertyInfo> DataProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
            .OrderBy(p => p.MetadataToken);

    static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return type.GetGenericArguments()[0];
        return null;
    }

    static object ParseEnum(Type type, string text)
    {
        foreach (var name in Enum.GetNames(type))
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse(type, name);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Enum.ToObject(type, n);
        throw new JsonException($"Valeur inconnue pour {type.Name} : {text}");
    }
}
