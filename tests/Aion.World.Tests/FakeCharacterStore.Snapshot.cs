using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Aion.World.Tests;

public sealed partial class FakeCharacterStore
{
    /// <summary>
    /// A deep copy of this store's rows, so one played-through character can seed several tests
    /// without one test's writes reaching another. Static state (mail inboxes, economy time) stays shared.
    /// </summary>
    public FakeCharacterStore Snapshot() => (FakeCharacterStore)StoreCopier.Copy(this, new(ReferenceEqualityComparer.Instance))!;

    static class StoreCopier
    {
        static readonly MethodInfo MemberwiseCloneMethod =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public static object? Copy(object? value, Dictionary<object, object> seen)
        {
            if (value is null) return null;
            var type = value.GetType();
            if (type.IsValueType || value is string || value is Type) return value;
            if (seen.TryGetValue(value, out var done)) return done;
            if (type == typeof(object)) return seen[value] = new object();
            if (value is Delegate) return null;
            if (value is Array array)
            {
                var arrayCopy = (Array)array.Clone();
                seen[value] = arrayCopy;
                for (var i = 0; i < arrayCopy.Length; i++) arrayCopy.SetValue(Copy(array.GetValue(i), seen), i);
                return arrayCopy;
            }
            if (type.IsGenericType && type.Namespace == "System.Collections.Generic")
                return CopyCollection(value, type, seen);
            if (!Owned(type)) return value;

            var copy = MemberwiseCloneMethod.Invoke(value, null)!;
            seen[value] = copy;
            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                        field.SetValue(copy, Copy(field.GetValue(value), seen));
            return copy;
        }

        static bool Owned(Type type) =>
            type.Assembly == typeof(FakeCharacterStore).Assembly
            || type.Assembly.GetName().Name is { } name && name.StartsWith("Aion.", StringComparison.Ordinal)
               && type != typeof(GameData) && !typeof(GameData).IsAssignableFrom(type);

        static object CopyCollection(object value, Type type, Dictionary<object, object> seen)
        {
            var comparer = type.GetProperty("Comparer")?.GetValue(value);
            var copy = comparer is not null && type.GetConstructor([comparer.GetType().GetInterfaces().FirstOrDefault(i =>
                           i.IsGenericType && i.GetGenericTypeDefinition() is var d && (d == typeof(IEqualityComparer<>) || d == typeof(IComparer<>))) ?? comparer.GetType()]) is { } withComparer
                ? withComparer.Invoke([comparer])
                : Activator.CreateInstance(type)!;
            seen[value] = copy;
            switch (value)
            {
                case IDictionary dictionary:
                    var target = (IDictionary)copy;
                    foreach (DictionaryEntry e in dictionary) target[e.Key] = Copy(e.Value, seen);
                    break;
                case IList list:
                    var targetList = (IList)copy;
                    foreach (var e in list) targetList.Add(Copy(e, seen));
                    break;
                default:
                    var add = type.GetMethod("Add", [type.GetGenericArguments()[0]])
                              ?? throw new NotSupportedException($"store snapshot: cannot copy {type}");
                    foreach (var e in (IEnumerable)value) add.Invoke(copy, [Copy(e, seen)]);
                    break;
            }
            return copy;
        }
    }
}
