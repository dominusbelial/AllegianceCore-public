using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace Allegiance.TestInfra;

/// <summary>Compare JSON omissions to construction, using the production contract.
/// Walk metadata, including nullable sections and collection elements whose defaults
/// are empty, so a new property/type is covered without a hand-maintained list.</summary>
internal static class JsonDefaultsGuard
{
    public static void ContextsIn(Assembly assembly)
    {
        var contexts = assembly.GetTypes().Where(t => !t.IsAbstract && typeof(JsonSerializerContext).IsAssignableFrom(t)).ToArray();
        Assert.NotEmpty(contexts);
        foreach (var type in contexts)
        {
            var context = (JsonSerializerContext)type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            foreach (var root in type.CustomAttributes.Where(a => a.AttributeType == typeof(JsonSerializableAttribute)))
                Graph(context.GetTypeInfo((Type)root.ConstructorArguments[0].Value!)!);
        }
    }

    public static void Graph(JsonTypeInfo root)
    {
        var seen = new HashSet<Type>();
        Visit(root);
        void Visit(JsonTypeInfo info)
        {
            if (!seen.Add(info.Type)) return;
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                var expected = Construct(info.Type);
                Equal(expected, JsonSerializer.Deserialize("{}", info), info.Type.Name + " {}");
                foreach (var property in info.Properties)
                {
                    if (property.Get is null) continue;
                    var child = info.Options.GetTypeInfo(property.PropertyType);
                    if (child.Kind == JsonTypeInfoKind.None) continue;
                    Visit(child);
                    // Start with the parent constructor's siblings, omit this section,
                    // and compare every public property to an independently new parent.
                    var json = ParentJson(expected, info);
                    json.Remove(property.Name);
                    Equal(expected, JsonSerializer.Deserialize(json, info), info.Type.Name + " omits " + property.Name);
                    // Also force optional sections / empty collections to materialize:
                    // {} alone cannot visit an absent (null) child or an empty array.
                    json = ParentJson(expected, info);
                    json[property.Name] = Empty(child);
                    var actual = JsonSerializer.Deserialize(json, info)!;
                    var childExpected = JsonSerializer.Deserialize(Empty(child), child);
                    Equal(childExpected, property.Get(actual), info.Type.Name + "." + property.Name + " empty section");
                }
            }
            else if (info.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
            {
                var element = ElementType(info.Type, info.Kind);
                Visit(info.Options.GetTypeInfo(element));
            }
        }
    }

    private static JsonObject ParentJson(object expected, JsonTypeInfo info)
    {
        var json = new JsonObject();
        foreach (var property in info.Properties.Where(p => p.Get is not null))
        {
            var value = property.Get!(expected);
            // Undefined is the CLR default for a missing raw RPC payload and has
            // no JSON representation. Keep it absent in the parent fixture.
            if (value is JsonElement { ValueKind: JsonValueKind.Undefined }) continue;
            json[property.Name] = JsonSerializer.SerializeToNode(value, info.Options.GetTypeInfo(property.PropertyType));
        }
        return json;
    }

    private static Type ElementType(Type type, JsonTypeInfoKind kind) => type.IsArray
        ? type.GetElementType()!
        : type.GetInterfaces().Append(type).First(t => t.IsGenericType && t.GetGenericTypeDefinition() ==
            (kind == JsonTypeInfoKind.Dictionary ? typeof(IDictionary<,>) : typeof(IEnumerable<>)))
            .GetGenericArguments()[kind == JsonTypeInfoKind.Dictionary ? 1 : 0];

    private static JsonNode Empty(JsonTypeInfo info) => info.Kind switch
    {
        JsonTypeInfoKind.Object => new JsonObject(),
        JsonTypeInfoKind.Enumerable => new JsonArray(EmptyValue(info.Options.GetTypeInfo(ElementType(info.Type, info.Kind)))),
        JsonTypeInfoKind.Dictionary => new JsonObject { ["guard"] = EmptyValue(info.Options.GetTypeInfo(ElementType(info.Type, info.Kind))) },
        _ => throw new InvalidOperationException($"No object fixture for {info.Type}")
    };

    private static JsonNode? EmptyValue(JsonTypeInfo info) => info.Kind == JsonTypeInfoKind.None
        ? JsonSerializer.SerializeToNode(info.Type.IsValueType ? Activator.CreateInstance(info.Type) : null, info)
        : Empty(info);

    // Positional wire records have no parameterless constructor. Invoke their
    // constructor with CLR defaults (or declared optional defaults) just as a caller
    // would; config classes use their actual parameterless constructor.
    private static object Construct(Type type)
    {
        if (type.IsValueType) return Activator.CreateInstance(type)!;
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray());
    }

    public static void Equal(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            Assert.True(Equals(expected, actual), $"{path}: expected {expected ?? "null"}, got {actual ?? "null"}");
            return;
        }
        Assert.True(expected.GetType() == actual.GetType(), $"{path}: types differ");
        if (expected is IDictionary dictionary)
        {
            var other = (IDictionary)actual;
            Assert.True(dictionary.Count == other.Count, $"{path}: dictionary sizes differ");
            foreach (var key in dictionary.Keys)
            {
                Assert.True(other.Contains(key), $"{path}: missing key {key}");
                Equal(dictionary[key], other[key], $"{path}[{key}]");
            }
        }
        else if (expected is IEnumerable sequence && expected is not string)
        {
            var left = sequence.Cast<object?>().ToArray();
            var right = ((IEnumerable)actual).Cast<object?>().ToArray();
            Assert.True(left.Length == right.Length, $"{path}: collection lengths differ");
            for (int i = 0; i < left.Length; i++) Equal(left[i], right[i], $"{path}[{i}]");
        }
        else if (expected.GetType().Namespace?.StartsWith("System", StringComparison.Ordinal) == true || expected.GetType().IsEnum)
            Assert.True(Equals(expected, actual), $"{path}: expected {expected}, got {actual}");
        else
            foreach (var property in expected.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                Equal(property.GetValue(expected), property.GetValue(actual), path + "." + property.Name);
    }
}
