using HarmonyLib;
using SiraUtil.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

namespace SiraUtil.Zenject.Internal
{
    internal sealed class AutoBindQueryCollections : IDisposable
    {
        private const int Capacity = 16;
        private const int BatchSize = 64;
        private static readonly Lazy<Schema?> shared = new(CreateSchema);
        [ThreadStatic] private static AutoBindQueryCollections? current;
        private readonly Schema schema;
        private readonly AutoBindQueryCollections? parent;
        private int remaining;
        private bool listReady, dictionaryReady, disabled;
        private Task<Batch>? pending;
        private Batch? prepared;
        private int listIndex, dictionaryIndex;

        private AutoBindQueryCollections(Schema schema, int count)
        {
            this.schema = schema;
            remaining = count;
            parent = current;
            current = this;
        }

        internal static AutoBindQueryCollections? Enter(int count)
        {
            if (count < 256)
            {
                return null;
            }
            Schema? schema = shared.Value;
            return schema != null && schema.Unpatched() ? new(schema, count) : null;
        }

        internal static BindAttribute? Query(Type? type)
        {
            AutoBindQueryCollections? scope = current;
            return type is null || scope == null || type.GetType() != scope.schema.RuntimeType || !scope.schema.Unpatched()
                ? type!.GetCustomAttribute<BindAttribute>()
                : (BindAttribute?)scope.schema.Query(type, typeof(BindAttribute), true);
        }

        private static Schema? CreateSchema()
        {
            try
            {
                return new Schema();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<object> NewList(int capacity)
        {
            AutoBindQueryCollections? scope = current;
            if (scope != null && capacity == Capacity && scope.listReady && scope.dictionaryReady &&
                scope.TryTake(false, out object? value))
            {
                return (List<object>)value!;
            }
            List<object> result = new(capacity);
            if (scope != null && capacity == Capacity)
            {
                scope.listReady = true;
                scope.Queue();
            }
            return result;
        }

        private static object NewDictionary(int capacity)
        {
            AutoBindQueryCollections? scope = current;
            Schema schema = scope?.schema ?? shared.Value ?? throw new NotSupportedException();
            if (scope != null && capacity == Capacity && scope.listReady && scope.dictionaryReady &&
                scope.TryTake(true, out object? value))
            {
                return value!;
            }
            object result = schema.Dictionary(capacity);
            if (scope != null && capacity == Capacity)
            {
                scope.dictionaryReady = true;
                scope.Queue();
            }
            return result;
        }

        private bool TryTake(bool dictionary, out object? value)
        {
            value = null;
            if (disabled || !schema.Unpatched())
            {
                return false;
            }
            if (prepared == null || (dictionary ? dictionaryIndex : listIndex) == prepared.Count)
            {
                Queue();
                Task<Batch>? request = pending;
                if (request == null)
                {
                    return false;
                }
                try
                {
                    prepared = request.GetAwaiter().GetResult();
                    pending = null;
                    listIndex = dictionaryIndex = 0;
                    if (!schema.Unpatched())
                    {
                        disabled = true;
                        prepared = null;
                        return false;
                    }
                    Queue();
                }
                catch (ThreadInterruptedException)
                {
                    disabled = true;
                    pending = null;
                    Thread.CurrentThread.Interrupt();
                    return false;
                }
                catch (Exception)
                {
                    disabled = true;
                    pending = null;
                    return false;
                }
            }
            // A completed batch publishes each fresh graph exactly once.
            Batch batch = prepared!;
            object?[] items = dictionary ? batch.Dictionaries : batch.Lists;
            int index = dictionary ? dictionaryIndex++ : listIndex++;
            value = items[index];
            items[index] = null;
            if (value == null)
            {
                disabled = true;
                return false;
            }
            return true;
        }

        private void Queue()
        {
            if (disabled || pending != null || remaining == 0 || !listReady || !dictionaryReady)
            {
                return;
            }
            try
            {
                int count = Math.Min(BatchSize, remaining);
                pending = Task.Factory.StartNew(Allocate, new Request(schema, count),
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                _ = pending.ContinueWith(ObserveFault, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                remaining -= count;
            }
            catch (Exception)
            {
                disabled = true;
            }
        }

        private static void ObserveFault(Task<Batch> task) => _ = task.Exception;

        private static Batch Allocate(object? state)
        {
            Request request = (Request)state!;
            if (!request.Schema.Unpatched())
            {
                throw new NotSupportedException();
            }
            Batch result = new(request.Count);
            for (int i = 0; i < request.Count; i++)
            {
                result.Dictionaries[i] = request.Schema.Dictionary(Capacity);
                result.Lists[i] = new List<object>(Capacity);
            }
            return request.Schema.Unpatched() ? result : throw new NotSupportedException();
        }

        public void Dispose()
        {
            disabled = true;
            prepared = null;
            pending = null;
            if (ReferenceEquals(current, this))
            {
                current = parent;
            }
        }

        private sealed class Request(Schema schema, int count)
        {
            internal readonly Schema Schema = schema;
            internal readonly int Count = count;
        }

        private sealed class Batch(int count)
        {
            internal readonly int Count = count;
            internal readonly object?[] Dictionaries = new object?[count];
            internal readonly object?[] Lists = new object?[count];
        }

        private sealed class Schema
        {
            private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            private readonly MethodBase[] guards;
            internal readonly Type RuntimeType;
            internal readonly Func<int, object> Dictionary;
            internal readonly Func<MemberInfo, Type, bool, Attribute?> Query;

            internal Schema()
            {
                Assembly core = typeof(Attribute).Assembly;
                if (core.ManifestModule.ModuleVersionId != new Guid("41d29b35-2f6a-475a-b1bf-7c6628b82790"))
                {
                    throw new NotSupportedException();
                }
                RuntimeType = core.GetType("System.RuntimeType", true)!;
                Type attributes = core.GetType("System.MonoCustomAttrs", true)!;
                Type[] parameters = [typeof(MemberInfo), typeof(Type), typeof(bool)];
                MethodInfo single = typeof(Attribute).GetMethod(nameof(Attribute.GetCustomAttribute), parameters)!;
                MethodInfo plural = typeof(Attribute).GetMethod(nameof(Attribute.GetCustomAttributes), parameters)!;
                MethodInfo runtime = RuntimeType.GetMethod(nameof(MemberInfo.GetCustomAttributes), [typeof(Type), typeof(bool)])!;
                MethodInfo mono = attributes.GetMethod("GetCustomAttributes", Members, null,
                    [typeof(ICustomAttributeProvider), typeof(Type), typeof(bool)], null)!;
                if (single.GetMethodBody()!.ExceptionHandlingClauses.Count != 0 || plural.GetMethodBody()!.ExceptionHandlingClauses.Count != 0 ||
                    runtime.GetMethodBody()!.ExceptionHandlingClauses.Count != 0 || mono.GetMethodBody()!.ExceptionHandlingClauses.Count != 0)
                {
                    throw new NotSupportedException();
                }
                List<CodeInstruction> source = PatchProcessor.GetOriginalInstructions(mono);
                ConstructorInfo dictionary = source.Select(item => item.operand).OfType<ConstructorInfo>()
                    .Single(ctor => ctor.DeclaringType!.IsGenericType &&
                        ctor.DeclaringType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                        ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType == typeof(int));
                Type dictionaryType = dictionary.DeclaringType!;
                if (dictionaryType.GetGenericArguments()[0] != typeof(Type) ||
                    dictionaryType.GetGenericArguments()[1].FullName != "System.MonoCustomAttrs+AttributeInfo")
                {
                    throw new NotSupportedException();
                }
                DynamicMethod factory = new("SiraNewQueryDictionary", typeof(object), [typeof(int)], typeof(AutoBindQueryCollections).Module, true);
                ILGenerator generator = factory.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Newobj, dictionary);
                generator.Emit(OpCodes.Ret);
                Dictionary = (Func<int, object>)factory.CreateDelegate(typeof(Func<int, object>));
                guards =
                [
                    single, plural, runtime, mono, dictionary,
                    dictionaryType.GetConstructor([typeof(int), typeof(IEqualityComparer<Type>)])!,
                    dictionaryType.GetMethod("Initialize", Members)!,
                    typeof(List<object>).GetConstructor([typeof(int)])!,
                    typeof(List<object>).TypeInitializer!,
                    core.GetType("System.Collections.HashHelpers", true)!.GetMethod("GetPrime", Members)!,
                    typeof(EqualityComparer<Type>).GetProperty("Default")!.GetMethod!,
                    typeof(CustomAttributeExtensions).GetMethods().Single(method => method.Name == nameof(CustomAttributeExtensions.GetCustomAttribute) &&
                        method.IsGenericMethodDefinition && method.GetParameters().Length == 1 &&
                        method.GetParameters()[0].ParameterType == typeof(MemberInfo)).MakeGenericMethod(typeof(BindAttribute))
                ];
                if (!Unpatched())
                {
                    throw new NotSupportedException();
                }
                MethodInfo listFactory = typeof(AutoBindQueryCollections).GetMethod(nameof(NewList), Members)!;
                MethodInfo dictionaryFactory = typeof(AutoBindQueryCollections).GetMethod(nameof(NewDictionary), Members)!;
                int collections = 0;
                DynamicMethod monoCopy = Copy(mono, [typeof(ICustomAttributeProvider), typeof(Type), typeof(bool)], instruction =>
                {
                    if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, dictionary))
                    {
                        collections++;
                        return [new(OpCodes.Call, dictionaryFactory), new(OpCodes.Castclass, dictionaryType)];
                    }
                    if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor &&
                        ctor.DeclaringType == typeof(List<object>) && ctor.GetParameters().Length == 1)
                    {
                        collections++;
                        return [new(OpCodes.Call, listFactory)];
                    }
                    return [instruction];
                });
                if (collections != 3)
                {
                    throw new NotSupportedException();
                }
                DynamicMethod runtimeCopy = Redirect(runtime, parameters, mono, monoCopy);
                MethodInfo virtualQuery = typeof(MemberInfo).GetMethod(nameof(MemberInfo.GetCustomAttributes), [typeof(Type), typeof(bool)])!;
                DynamicMethod pluralCopy = Redirect(plural, parameters, virtualQuery, runtimeCopy);
                DynamicMethod singleCopy = Redirect(single, parameters, plural, pluralCopy);
                Query = (Func<MemberInfo, Type, bool, Attribute?>)singleCopy.CreateDelegate(typeof(Func<MemberInfo, Type, bool, Attribute?>));
                if (!Unpatched())
                {
                    throw new NotSupportedException();
                }
            }

            internal bool Unpatched() => guards.All(method => HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Count is not > 0);

            private static DynamicMethod Redirect(MethodInfo original, Type[] parameters, MethodInfo target, DynamicMethod replacement)
            {
                int count = 0;
                DynamicMethod result = Copy(original, parameters, instruction =>
                {
                    if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && Equals(instruction.operand, target))
                    {
                        count++;
                        return [new(OpCodes.Call, replacement)];
                    }
                    return [instruction];
                });
                return count == 1 ? result : throw new NotSupportedException();
            }

            private static DynamicMethod Copy(MethodInfo original, Type[] parameters, Func<CodeInstruction, CodeInstruction[]> rewrite)
            {
                MethodBody body = original.GetMethodBody()!;
                DynamicMethod result = new("SiraQuery" + original.MetadataToken, original.ReturnType, parameters,
                    typeof(AutoBindQueryCollections).Module, true)
                {
                    InitLocals = body.InitLocals
                };
                ILGenerator generator = result.GetILGenerator();
                LocalBuilder[] locals = [.. body.LocalVariables.Select(local => generator.DeclareLocal(local.LocalType, local.IsPinned))];
                Dictionary<Label, Label> labels = [];
                Label Map(Label label)
                {
                    if (!labels.TryGetValue(label, out Label mapped))
                    {
                        labels.Add(label, mapped = generator.DefineLabel());
                    }
                    return mapped;
                }
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(original))
                {
                    if (instruction.blocks.Count != 0)
                    {
                        throw new NotSupportedException();
                    }
                    foreach (Label label in instruction.labels)
                    {
                        generator.MarkLabel(Map(label));
                    }
                    foreach (CodeInstruction emitted in rewrite(instruction))
                    {
                        Emit(generator, LongBranch(emitted.opcode), emitted.operand, locals, Map, original.IsStatic);
                    }
                }
                return result;
            }

            private static OpCode LongBranch(OpCode opcode) => opcode.Value switch
            {
                0x2b => OpCodes.Br,
                0x2c => OpCodes.Brfalse,
                0x2d => OpCodes.Brtrue,
                0x2e => OpCodes.Beq,
                0x2f => OpCodes.Bge,
                0x30 => OpCodes.Bgt,
                0x31 => OpCodes.Ble,
                0x32 => OpCodes.Blt,
                0x33 => OpCodes.Bne_Un,
                0x34 => OpCodes.Bge_Un,
                0x35 => OpCodes.Bgt_Un,
                0x36 => OpCodes.Ble_Un,
                0x37 => OpCodes.Blt_Un,
                0xde => OpCodes.Leave,
                _ => opcode
            };

            private static void Emit(ILGenerator generator, OpCode opcode, object? operand, LocalBuilder[] locals, Func<Label, Label> label, bool isStatic)
            {
                switch (operand)
                {
                    case null: generator.Emit(opcode); break;
                    case Label target: generator.Emit(opcode, label(target)); break;
                    case Label[] targets: generator.Emit(opcode, [.. targets.Select(label)]); break;
                    case LocalBuilder local: generator.Emit(opcode, locals[local.LocalIndex]); break;
                    case LocalVariableInfo local: generator.Emit(opcode, locals[local.LocalIndex]); break;
                    case ParameterInfo parameter:
                        int index = parameter.Position + (isStatic ? 0 : 1);
                        if (opcode.OperandType == OperandType.ShortInlineVar)
                        {
                            generator.Emit(opcode, (byte)index);
                        }
                        else
                        {
                            generator.Emit(opcode, (short)index);
                        }
                        break;
                    case ConstructorInfo ctor: generator.Emit(opcode, ctor); break;
                    case MethodInfo method: generator.Emit(opcode, method); break;
                    case FieldInfo field: generator.Emit(opcode, field); break;
                    case Type type: generator.Emit(opcode, type); break;
                    case string text: generator.Emit(opcode, text); break;
                    case byte number: generator.Emit(opcode, number); break;
                    case sbyte number: generator.Emit(opcode, number); break;
                    case short number: generator.Emit(opcode, number); break;
                    case int number:
                        if (opcode.OperandType == OperandType.ShortInlineVar)
                        {
                            generator.Emit(opcode, (byte)number);
                        }
                        else if (opcode.OperandType == OperandType.InlineVar)
                        {
                            generator.Emit(opcode, (short)number);
                        }
                        else
                        {
                            generator.Emit(opcode, number);
                        }
                        break;
                    case long number: generator.Emit(opcode, number); break;
                    case float number: generator.Emit(opcode, number); break;
                    case double number: generator.Emit(opcode, number); break;
                    default: throw new NotSupportedException();
                }
            }
        }
    }
}
