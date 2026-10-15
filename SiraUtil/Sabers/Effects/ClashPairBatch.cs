using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SiraUtil.Sabers.Effects
{
    internal sealed class ClashPairBatch : IDisposable
    {
        private static readonly Lazy<Schema?> shared = new(CreateSchema);
        private readonly Schema schema;
        private Saber[] identities;
        private Work work;
        private Task task;

        private ClashPairBatch(Schema schema, Saber[] identities, Endpoint[] endpoints)
        {
            this.schema = schema;
            this.identities = identities;
            work = new(endpoints, schema.Distance, 1, 0);
            task = Queue(work);
        }

        private static Task Queue(Work work)
        {
            Task task = Task.Factory.StartNew(static state => ((Work)state!).Run(), work,
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        internal bool IsCompleted => task.IsCompleted;

        internal static ClashPairBatch? Start(List<Saber> sabers)
        {
            if (sabers.Count < 64)
            {
                return null;
            }
            try
            {
                Schema? schema = shared.Value;
                if (schema == null || !schema.Unpatched())
                {
                    return null;
                }
                Saber[] identities = [.. sabers];
                Endpoint[] endpoints = new Endpoint[identities.Length];
                for (int i = 0; i < identities.Length; i++)
                {
                    Saber saber = identities[i];
                    if (!ReferenceEquals(saber, null))
                    {
                        endpoints[i] = new(saber._saberBladeBottomPos, saber._saberBladeTopPos);
                    }
                }
                return new(schema, identities, endpoints);
            }
            catch (ThreadInterruptedException)
            {
                Thread.CurrentThread.Interrupt();
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal bool TryTake(int a, int b, Saber saberA, Saber saberB, Vector3 bottomA, Vector3 topA,
            Vector3 bottomB, Vector3 topB, out float distance, out Vector3 point)
        {
            distance = default;
            point = default;
            try
            {
                if (a >= identities.Length || b >= a)
                {
                    return false;
                }
                long pair = ((long)a * (a - 1) / 2) + b;
                if (pair >= work.Offset + work.Distances.Length)
                {
                    Volatile.Write(ref work.Stop, 1);
                    if (!task.IsCompleted)
                    {
                        return false;
                    }
                    work = new(work.Endpoints, schema.Distance, a, b);
                    task = Queue(work);
                }
                long relative = pair - work.Offset;
                if (Volatile.Read(ref work.Stop) != 0 || relative < 0 ||
                    relative >= Volatile.Read(ref work.Completed) ||
                    !ReferenceEquals(identities[a], saberA) || !ReferenceEquals(identities[b], saberB) ||
                    !work.Endpoints[a].Matches(bottomA, topA) || !work.Endpoints[b].Matches(bottomB, topB) ||
                    !schema.Unpatched())
                {
                    return false;
                }
                int index = (int)relative;
                distance = work.Distances[index];
                point = work.Points[index];
                return true;
            }
            catch (ThreadInterruptedException)
            {
                Thread.CurrentThread.Interrupt();
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref work.Stop, 1);
            identities = [];
        }

        private static Schema? CreateSchema()
        {
            try
            {
                return new();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private readonly struct Endpoint(Vector3 bottom, Vector3 top)
        {
            internal readonly Vector3 Bottom = bottom;
            internal readonly Vector3 Top = top;

            internal bool Matches(Vector3 bottom, Vector3 top) => Same(Bottom, bottom) && Same(Top, top);

            private static bool Same(Vector3 a, Vector3 b) => new FloatBits(a.x).Bits == new FloatBits(b.x).Bits &&
                new FloatBits(a.y).Bits == new FloatBits(b.y).Bits && new FloatBits(a.z).Bits == new FloatBits(b.z).Bits;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] private float value;
            [FieldOffset(0)] internal int Bits;

            internal FloatBits(float value)
            {
                this = default;
                this.value = value;
            }
        }

        private delegate float DistanceFunction(SaberClashChecker? unused, Vector3 bottomA, Vector3 topA,
            Vector3 bottomB, Vector3 topB, out Vector3 point);

        private sealed class Work
        {
            internal readonly Endpoint[] Endpoints;
            internal readonly float[] Distances;
            internal readonly Vector3[] Points;
            internal int Completed, Stop;
            private readonly DistanceFunction distance;
            internal readonly long Offset;
            private readonly int firstA, firstB;

            internal Work(Endpoint[] endpoints, DistanceFunction distance, int firstA, int firstB)
            {
                Endpoints = endpoints;
                this.distance = distance;
                this.firstA = firstA;
                this.firstB = firstB;
                Offset = ((long)firstA * (firstA - 1) / 2) + firstB;
                int count = (int)Math.Min(2048, ((long)endpoints.Length * (endpoints.Length - 1) / 2) - Offset);
                Distances = new float[count];
                Points = new Vector3[count];
            }

            internal void Run()
            {
                int index = 0;
                for (int a = firstA; a < Endpoints.Length; a++)
                {
                    for (int b = a == firstA ? firstB : 0; b < a; b++)
                    {
                        if (index == Distances.Length)
                        {
                            Volatile.Write(ref Completed, index);
                            return;
                        }
                        if ((index & 31) == 0 && Volatile.Read(ref Stop) != 0)
                        {
                            return;
                        }
                        Endpoint first = Endpoints[a], second = Endpoints[b];
                        Distances[index] = distance(null, first.Bottom, first.Top, second.Bottom, second.Top, out Points[index]);
                        index++;
                        // A published prefix is immutable even while later pairs are still being computed.
                        if ((index & 31) == 0)
                        {
                            Volatile.Write(ref Completed, index);
                        }
                    }
                }
                Volatile.Write(ref Completed, index);
            }
        }

        private sealed class Schema
        {
            internal readonly DistanceFunction Distance;
            private readonly HashSet<MethodBase> guards = [];
            private readonly Dictionary<MethodInfo, DynamicMethod> copies = [];

            internal Schema()
            {
                if (typeof(Saber).Module.ModuleVersionId != new Guid("96b112aa-517d-4810-93b5-2e466e395c7e") ||
                    typeof(Vector3).Module.ModuleVersionId != new Guid("9bb2d74d-69aa-4d4f-ad80-7ca26a510871"))
                {
                    throw new NotSupportedException();
                }
                Type vector = typeof(Vector3);
                static MethodInfo Find(Type type, string name, params Type[] parameters)
                {
                    return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                        null, parameters, null) ?? throw new MissingMethodException();
                }
                guards.Add(Find(typeof(Saber), "get_saberBladeTopPos"));
                guards.Add(Find(typeof(Saber), "get_saberBladeBottomPos"));
                Copy(Find(vector, "op_Subtraction", vector, vector), 25);
                Copy(Find(vector, "Dot", vector, vector), 18);
                Copy(Find(typeof(Mathf), "Abs", typeof(float)), 7);
                Copy(Find(vector, "op_Multiply", typeof(float), vector), 22);
                Copy(Find(vector, "op_Addition", vector, vector), 25);
                Copy(Find(vector, "op_Multiply", vector, typeof(float)), 22);
                Copy(Find(vector, "get_magnitude"), 21);
                MethodInfo original = Find(typeof(SaberClashChecker), "SegmentToSegmentDist", vector, vector, vector, vector,
                    vector.MakeByRefType());
                List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(original);
                if (instructions.Any(instruction => instruction.opcode == OpCodes.Ldarg_0))
                {
                    throw new NotSupportedException();
                }
                Distance = (DistanceFunction)Copy(original, 194).CreateDelegate(typeof(DistanceFunction));
            }

            internal bool Unpatched() => guards.All(method => Harmony.GetPatchInfo(method)?.Owners.Count is not > 0);

            private DynamicMethod Copy(MethodInfo original, int count)
            {
                MethodBody body = original.GetMethodBody() ?? throw new NotSupportedException();
                List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(original);
                if (instructions.Count != count || body.ExceptionHandlingClauses.Count != 0)
                {
                    throw new NotSupportedException();
                }
                guards.Add(original);
                Type[] parameters = [.. original.GetParameters().Select(parameter => parameter.ParameterType)];
                if (!original.IsStatic)
                {
                    Type receiver = original.DeclaringType!;
                    parameters = [receiver.IsValueType ? receiver.MakeByRefType() : receiver, .. parameters];
                }
                DynamicMethod copy = new("SiraClash" + original.MetadataToken, original.ReturnType, parameters,
                    typeof(ClashPairBatch).Module, true)
                {
                    InitLocals = body.InitLocals
                };
                ILGenerator generator = copy.GetILGenerator();
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
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.blocks.Count != 0)
                    {
                        throw new NotSupportedException();
                    }
                    foreach (Label label in instruction.labels)
                    {
                        generator.MarkLabel(Map(label));
                    }
                    OpCode opcode = LongBranch(instruction.opcode);
                    switch (instruction.operand)
                    {
                        case null: generator.Emit(opcode); break;
                        case Label label: generator.Emit(opcode, Map(label)); break;
                        case LocalBuilder local: generator.Emit(opcode, locals[local.LocalIndex]); break;
                        case LocalVariableInfo local: generator.Emit(opcode, locals[local.LocalIndex]); break;
                        case ParameterInfo parameter:
                            int index = parameter.Position + (original.IsStatic ? 0 : 1);
                            if (opcode.OperandType == OperandType.ShortInlineVar)
                            {
                                generator.Emit(opcode, (byte)index);
                            }
                            else
                            {
                                generator.Emit(opcode, (short)index);
                            }
                            break;
                        case MethodInfo method:
                            if (copies.TryGetValue(method, out DynamicMethod replacement))
                            {
                                generator.Emit(opcode, replacement);
                            }
                            else if (method.DeclaringType == typeof(Math) && (method.Name == "Abs" || method.Name == "Sqrt"))
                            {
                                guards.Add(method);
                                generator.Emit(opcode, method);
                            }
                            else
                            {
                                throw new NotSupportedException();
                            }
                            break;
                        case FieldInfo field when field.DeclaringType == typeof(Vector3) && !field.IsStatic && field.FieldType == typeof(float):
                            generator.Emit(opcode, field); break;
                        case Type type when type == typeof(Vector3): generator.Emit(opcode, type); break;
                        case int value: generator.Emit(opcode, value); break;
                        case float value: generator.Emit(opcode, value); break;
                        case double value: generator.Emit(opcode, value); break;
                        case byte value: generator.Emit(opcode, value); break;
                        case sbyte value: generator.Emit(opcode, value); break;
                        case short value: generator.Emit(opcode, value); break;
                        default: throw new NotSupportedException();
                    }
                }
                copies.Add(original, copy);
                return copy;
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
                _ => opcode
            };
        }
    }
}
