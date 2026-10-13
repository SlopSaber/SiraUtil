using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace SiraUtil.Zenject.Internal
{
    internal static class OwnedInstructionOrder<T>
    {
        private const int MinimumKeys = 4096;
        private const int MaximumKeys = 65536;
        private static Schema? schema;

        internal static IEnumerable<T> Enumerate(IEnumerable<T> ordered)
        {
            if (ordered is not IOrderedEnumerable<T>)
            {
                return ordered;
            }
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

        private static IEnumerable<T> Iterate(object ordered, Schema current)
        {
            object buffer = Invoke(current.BufferConstructor, null, current.Source.GetValue(ordered)!);
            int count = (int)current.Count.GetValue(buffer)!;
            if (count == 0)
            {
                yield break;
            }
            T[] items = (T[])current.Items.GetValue(buffer)!;
            int[] map;
            if (count < MinimumKeys || count > MaximumKeys || !current.Supports(ordered))
            {
                map = (int[])Invoke(current.SortedMap, ordered, buffer);
            }
            else
            {
                object sorter = Invoke(current.GetSorter, ordered);
                map = (int[])Invoke(current.ComputeMap, sorter, items, count);
                if (!TryUse(current, sorter, count, map, out int[]? ready))
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

        private static bool TryUse(Schema current, object sorter, int count, int[] originalMap, out int[]? order)
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
                if (AlreadyOrdered(keys, descending))
                {
                    order = originalMap;
                    return true;
                }
                lock (Cache.Gate)
                {
                    Cache.Collect();
                    Plan? plan = Cache.Ready;
                    if (plan == null || !Same(plan.Input, keys, descending))
                    {
                        plan = Cache.Prepared;
                        if (plan == null || !Same(plan.Input, keys, descending))
                        {
                            return false;
                        }
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
                    Cache.Collect();
                    if (Cache.Ready != null && Same(Cache.Ready.Input, keys, descending))
                    {
                        return;
                    }
                    if (Cache.Prepared != null && Same(Cache.Prepared.Input, keys, descending))
                    {
                        Cache.Ready = Cache.Prepared;
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

        private static bool AlreadyOrdered(int[] keys, bool descending)
        {
            for (int i = 1; i < keys.Length; i++)
            {
                if (descending ? keys[i - 1] < keys[i] : keys[i - 1] > keys[i])
                {
                    return false;
                }
            }
            return true;
        }

        internal static void ObserveAppend(ref Producer? producer, ref int observedCount, int order)
        {
            try
            {
                if (observedCount >= MaximumKeys)
                {
                    producer = null;
                    return;
                }
                observedCount++;
                if (producer == null && order == 0)
                {
                    return;
                }
                producer ??= new Producer(observedCount - 1);
                producer.Keys.Add(order);
                if (observedCount < MinimumKeys || (observedCount & (observedCount - 1)) != 0)
                {
                    return;
                }
                RequestForecast(producer);
            }
            catch
            {
                observedCount = MaximumKeys;
                producer = null;
            }
        }

        internal static void ObserveFinalCount(ref Producer? producer, ref int observedCount)
        {
            try
            {
                if (producer != null && observedCount >= MinimumKeys && observedCount <= MaximumKeys)
                {
                    RequestForecast(producer);
                }
            }
            catch
            {
                observedCount = MaximumKeys;
                producer = null;
            }
        }

        internal static Aggregate? CreateAggregate<TProducer>(HashSet<TProducer> producers)
        {
            try
            {
                MethodInfo? countGetter = typeof(HashSet<TProducer>).GetProperty(nameof(HashSet<>.Count))?.GetGetMethod();
                if (countGetter == null || HarmonyLib.Harmony.GetPatchInfo(countGetter) != null)
                {
                    return null;
                }
                int producerCount = producers.Count;
                return producerCount > 1 ? new Aggregate(producerCount) : null;
            }
            catch
            {
                return null;
            }
        }

        internal static void ObserveAggregate(Aggregate? aggregate, Producer? producer, int observedCount)
        {
            if (aggregate == null || aggregate.Remaining <= 0)
            {
                return;
            }
            try
            {
                if (observedCount < 0 || observedCount > MaximumKeys - aggregate.Count ||
                    (producer != null && producer.Keys.Count != observedCount))
                {
                    aggregate.Remaining = 0;
                    aggregate.Keys = null;
                    return;
                }
                if (producer != null)
                {
                    aggregate.Keys ??= new Producer(aggregate.Count);
                    aggregate.Keys.Keys.AddRange(producer.Keys);
                }
                else if (aggregate.Keys != null)
                {
                    for (int i = 0; i < observedCount; i++)
                    {
                        aggregate.Keys.Keys.Add(0);
                    }
                }
                aggregate.Count += observedCount;
                aggregate.Remaining--;
                if (aggregate.Remaining == 0 && aggregate.Count >= MinimumKeys && aggregate.Keys != null)
                {
                    RequestForecast(aggregate.Keys);
                }
            }
            catch
            {
                aggregate.Remaining = 0;
                aggregate.Keys = null;
            }
        }

        internal sealed class Aggregate
        {
            internal int Remaining;
            internal int Count;
            internal Producer? Keys;

            internal Aggregate(int producerCount)
            {
                Remaining = producerCount;
            }
        }

        private static void RequestForecast(Producer producer)
        {
            lock (Cache.Gate)
            {
                Cache.Collect();
                if (Cache.Pending != null)
                {
                    return;
                }
                int[] keys = [.. producer.Keys];
                if (AlreadyOrdered(keys, false) ||
                    (Cache.Ready != null && Same(Cache.Ready.Input, keys, false)) ||
                    (Cache.Prepared != null && Same(Cache.Prepared.Input, keys, false)))
                {
                    return;
                }
                Schema current = schema ??= new Schema(typeof(Enumerable).Assembly
                    .GetType("System.Linq.OrderedEnumerable`2", true)!.MakeGenericType(typeof(T), typeof(int)));
                if (!current.Unpatched())
                {
                    return;
                }
                Input input = new(keys, false);
                if (ExecutionContext.IsFlowSuppressed())
                {
                    Cache.Pending = Start(input);
                }
                else
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        Cache.Pending = Start(input);
                    }
                }
            }
        }

        private static Task<Plan> Start(Input input) => Task.Factory.StartNew(Prepare, input,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

        private static Plan Prepare(object? state)
        {
            Input input = (Input)state!;
            Schema current = schema ?? throw new NotSupportedException();
            if (!current.Unpatched())
            {
                throw new NotSupportedException();
            }
            int[] order = new int[input.Keys.Length];
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }
            Array.Sort(order, 0, order.Length, new IndexComparer(input));
            return current.Unpatched() ? new Plan(input, order) : throw new NotSupportedException();
        }

        internal sealed class Producer
        {
            internal readonly List<int> Keys;
            internal Producer(int precedingZeros)
            {
                Keys = new(precedingZeros + 1);
                for (int i = 0; i < precedingZeros; i++)
                {
                    Keys.Add(0);
                }
            }
        }

        private sealed class IndexComparer : IComparer<int>
        {
            private readonly Input input;
            internal IndexComparer(Input input) { this.input = input; }

            public int Compare(int left, int right)
            {
                int leftKey = input.Keys[left], rightKey = input.Keys[right];
                return leftKey == rightKey ? left - right : (leftKey < rightKey) == input.Descending ? 1 : -1;
            }
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
            internal static Plan? Prepared;
            internal static Task<Plan>? Pending;

            internal static void Collect()
            {
                Task<Plan>? task = Pending;
                if (task == null || !task.IsCompleted)
                {
                    return;
                }
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    Prepared = task.GetAwaiter().GetResult();
                }
                else
                {
                    _ = task.Exception;
                }
                Pending = null;
            }
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
                Type element = typeof(T);
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
                BufferConstructor = bufferType.GetConstructor(Members, null, [typeof(IEnumerable<T>)], null)!;
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
                Type[] localTypes = [typeof(OwnedInstructionOrder<T>), typeof(Schema), typeof(Input), typeof(Plan), typeof(Cache), typeof(Producer), typeof(Aggregate), typeof(IndexComparer)];
                foreach (Type type in localTypes
                    .Concat(typeof(OwnedInstructionOrder<T>).GetNestedTypes(BindingFlags.NonPublic)
                        .Select(type => type.ContainsGenericParameters ? type.MakeGenericType(typeof(T)) : type)))
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
