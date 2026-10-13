using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SiraUtil.Zenject.Internal
{
    internal static class OwnedInstructionOrder
    {
        private const int MinimumKeys = 4096;
        private const int MaximumKeys = 65536;
        private static Schema? schema;

        internal static IEnumerable<IInjectableMonoBehaviourInstruction> Enumerate(IOrderedEnumerable<IInjectableMonoBehaviourInstruction> ordered)
        {
            try
            {
                Schema current = schema ??= new Schema(ordered.GetType());
                if (current.Supports(ordered))
                {
                    return Iterate(ordered, current);
                }
            }
            catch { }
            return ordered;
        }

        private static IEnumerable<IInjectableMonoBehaviourInstruction> Iterate(object ordered, Schema current)
        {
            object buffer = Invoke(current.BufferConstructor, null, current.Source.GetValue(ordered)!);
            int count = (int)current.Count.GetValue(buffer)!;
            if (count == 0)
            {
                yield break;
            }
            IInjectableMonoBehaviourInstruction[] items = (IInjectableMonoBehaviourInstruction[])current.Items.GetValue(buffer)!;
            int[] map;
            if (count < MinimumKeys || count > MaximumKeys || !current.Supports(ordered))
            {
                map = (int[])Invoke(current.SortedMap, ordered, buffer);
            }
            else
            {
                object sorter = Invoke(current.GetSorter, ordered);
                map = (int[])Invoke(current.ComputeMap, sorter, items, count);
                if (!TryUse(current, sorter, count, out int[]? ready))
                {
                    Invoke(current.QuickSort, sorter, map, 0, count - 1);
                }
                else
                {
                    map = ready!;
                }
                Observe(current, sorter, count, map);
            }
            for (int i = 0; i < count; i++)
            {
                yield return items[map[i]];
            }
        }

        private static object Invoke(MethodBase method, object? instance, params object[] arguments)
        {
            try
            {
                return method is ConstructorInfo constructor
                    ? constructor.Invoke(arguments)
                    : ((MethodInfo)method).Invoke(instance, arguments)!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static bool TryUse(Schema current, object sorter, int count, out int[]? order)
        {
            order = null;
            try
            {
                if (!current.SupportsSorter(sorter))
                {
                    return false;
                }
                int[] keys = (int[])current.Keys.GetValue(sorter)!;
                bool descending = (bool)current.Descending.GetValue(sorter)!;
                if (keys.Length != count)
                {
                    return false;
                }
                lock (Cache.Gate)
                {
                    Plan? plan = Cache.Ready;
                    if (plan == null || !Same(plan.Input, keys, descending))
                    {
                        return false;
                    }
                    order = plan.Order;
                    return true;
                }
            }
            catch { return false; }
        }

        private static void Observe(Schema current, object sorter, int count, int[] order)
        {
            try
            {
                if (!current.SupportsSorter(sorter))
                {
                    return;
                }
                int[] keys = (int[])current.Keys.GetValue(sorter)!;
                bool descending = (bool)current.Descending.GetValue(sorter)!;
                if (keys.Length != count)
                {
                    return;
                }
                lock (Cache.Gate)
                {
                    if (Cache.Ready != null && Same(Cache.Ready.Input, keys, descending))
                    {
                        return;
                    }
                    Input input = new((int[])keys.Clone(), descending);
                    Cache.Ready = new Plan(input, (int[])order.Clone());
                }
            }
            catch { }
        }

        private static bool Same(Input input, int[] keys, bool descending)
        {
            if (input.Descending != descending || input.Keys.Length != keys.Length)
            {
                return false;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (input.Keys[i] != keys[i])
                {
                    return false;
                }
            }
            return true;
        }

        private sealed class Input
        {
            internal readonly int[] Keys;
            internal readonly bool Descending;
            internal Input(int[] keys, bool descending) { Keys = keys; Descending = descending; }
        }

        private sealed class Plan
        {
            internal readonly Input Input;
            internal readonly int[] Order;
            internal Plan(Input input, int[] order) { Input = input; Order = order; }
        }

        private static class Cache
        {
            internal static readonly object Gate = new();
            internal static Plan? Ready;
        }

        private sealed class Schema
        {
            private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            private readonly Type orderedType;
            private readonly Type sorterType;
            private readonly Type comparerType;
            private readonly FieldInfo parent;
            private readonly FieldInfo orderedComparer;
            private readonly FieldInfo next;
            private readonly FieldInfo sorterComparer;
            private readonly MethodBase[] guards;
            internal readonly ConstructorInfo BufferConstructor;
            internal readonly FieldInfo Source, Items, Count, Keys, Descending;
            internal readonly MethodInfo SortedMap, GetSorter, ComputeMap, QuickSort;

            internal Schema(Type actualOrderedType)
            {
                Assembly core = typeof(Enumerable).Assembly;
                Assembly primitives = typeof(int).Assembly;
                if (core.ManifestModule.ModuleVersionId != new Guid("80564d68-9e48-448f-a2a7-101993ee7201") ||
                    primitives.ManifestModule.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790"))
                {
                    throw new NotSupportedException();
                }
                Type element = typeof(IInjectableMonoBehaviourInstruction);
                orderedType = core.GetType("System.Linq.OrderedEnumerable`2", true)!.MakeGenericType(element, typeof(int));
                if (actualOrderedType != orderedType)
                {
                    throw new NotSupportedException();
                }
                Type orderedBase = orderedType.BaseType!;
                Type bufferType = core.GetType("System.Linq.Buffer`1", true)!.MakeGenericType(element);
                sorterType = core.GetType("System.Linq.EnumerableSorter`2", true)!.MakeGenericType(element, typeof(int));
                Type sorterBase = sorterType.BaseType!;
                comparerType = primitives.GetType("System.Collections.Generic.GenericComparer`1", true)!.MakeGenericType(typeof(int));
                BufferConstructor = bufferType.GetConstructor(Members, null, [typeof(IEnumerable<IInjectableMonoBehaviourInstruction>)], null)!;
                Source = Field(orderedBase, "_source");
                Items = Field(bufferType, "_items");
                Count = Field(bufferType, "_count");
                parent = Field(orderedType, "_parent");
                orderedComparer = Field(orderedType, "_comparer");
                Keys = Field(sorterType, "_keys");
                Descending = Field(sorterType, "_descending");
                next = Field(sorterType, "_next");
                sorterComparer = Field(sorterType, "_comparer");
                SortedMap = Method(orderedBase, "SortedMap", bufferType);
                GetSorter = Method(orderedBase, "GetEnumerableSorter");
                ComputeMap = Method(sorterBase, "ComputeMap", element.MakeArrayType(), typeof(int));
                QuickSort = Method(sorterType, "QuickSort", typeof(int[]), typeof(int), typeof(int));
                List<MethodBase> methods =
                [
                    BufferConstructor, SortedMap, GetSorter, ComputeMap, QuickSort,
                    Method(orderedType, "GetEnumerableSorter", sorterBase),
                    Method(sorterType, "ComputeKeys", element.MakeArrayType(), typeof(int)),
                    Method(sorterType, "CompareAnyKeys", typeof(int), typeof(int)),
                    Method(sorterBase, "Sort", element.MakeArrayType(), typeof(int)),
                    Method(comparerType, "Compare", typeof(int), typeof(int)),
                    typeof(int).GetMethod("CompareTo", [typeof(int)])!
                ];
                foreach (MethodInfo method in typeof(Array).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "Sort" && (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length == 1))
                    {
                        methods.Add(method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(int)) : method);
                    }
                }
                Type arraySorter = primitives.GetType("System.Collections.Generic.ArraySortHelper`1", true)!.MakeGenericType(typeof(int));
                methods.AddRange(arraySorter.GetMethods(Members | BindingFlags.Static | BindingFlags.DeclaredOnly));
                Type[] localTypes = [typeof(OwnedInstructionOrder), typeof(Schema), typeof(Input), typeof(Plan), typeof(Cache)];
                foreach (Type type in localTypes
                    .Concat(typeof(OwnedInstructionOrder).GetNestedTypes(BindingFlags.NonPublic)))
                {
                    methods.AddRange(type.GetMethods(Members | BindingFlags.Static | BindingFlags.DeclaredOnly));
                    methods.AddRange(type.GetConstructors(Members | BindingFlags.Static));
                }
                guards = [.. methods];
            }

            private static FieldInfo Field(Type type, string name) => type.GetField(name, Members) ?? throw new NotSupportedException();
            private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
                type.GetMethod(name, Members, null, parameters, null) ?? throw new NotSupportedException();

            internal bool Unpatched()
            {
                foreach (MethodBase method in guards)
                {
                    if (HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                    {
                        return false;
                    }
                }
                return true;
            }

            internal bool Supports(object ordered) => ordered.GetType() == orderedType && Unpatched() &&
                parent.GetValue(ordered) == null && orderedComparer.GetValue(ordered)?.GetType() == comparerType;

            internal bool SupportsSorter(object sorter) => sorter.GetType() == sorterType && Unpatched() &&
                next.GetValue(sorter) == null && sorterComparer.GetValue(sorter)?.GetType() == comparerType;
        }
    }
}
