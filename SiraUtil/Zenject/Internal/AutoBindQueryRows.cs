using SiraUtil.Attributes;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SiraUtil.Zenject.Internal
{
    internal sealed class AutoBindQueryRows(AutoBindQueryRows.Layout layout, Assembly assembly) : IDisposable
    {
        private const int MaximumRows = 131072;
        private Task<bool[]>? pending;
        private bool[]? present;
        private IntPtr image;
        private bool started, disabled;

        internal bool IsNegative(ICustomAttributeProvider provider, Type attributeType, bool pseudo)
        {
            if (disabled || pseudo || attributeType != typeof(BindAttribute) || provider is null || provider.GetType() != layout.RuntimeType || provider is not Type type)
            {
                return false;
            }
            try
            {
                if (!layout.AttributeReady())
                {
                    return false;
                }
                if (present == null)
                {
                    Task<bool[]>? request = pending;
                    if (request == null || !request.IsCompleted)
                    {
                        return false;
                    }
                    present = request.GetAwaiter().GetResult();
                    pending = null;
                }
                IntPtr klass = Layout.Class(type);
                if (klass == IntPtr.Zero || Marshal.ReadIntPtr(klass, 0x40) != image)
                {
                    return false;
                }
                uint token = unchecked((uint)Marshal.ReadInt32(klass, 0x58));
                int row = (int)(token & 0xffffff);
                return (token & 0xff000000) == 0x02000000 && row > 0 && row < present.Length && !present[row];
            }
            catch (ThreadInterruptedException)
            {
                disabled = true;
                Thread.CurrentThread.Interrupt();
                return false;
            }
            catch (Exception)
            {
                disabled = true;
                return false;
            }
        }

        internal void Observe(ICustomAttributeProvider provider, Type attributeType, bool pseudo)
        {
            if (started || disabled || pseudo || attributeType != typeof(BindAttribute) || provider is null ||
                provider.GetType() != layout.RuntimeType || provider is not Type type)
            {
                return;
            }
            try
            {
                if (!ReferenceEquals(type.Assembly, assembly))
                {
                    return;
                }
                IntPtr klass = Layout.Class(type);
                if (klass == IntPtr.Zero)
                {
                    return;
                }
                StartSnapshot(Marshal.ReadIntPtr(klass, 0x40), type);
            }
            catch (ThreadInterruptedException)
            {
                disabled = true;
                Thread.CurrentThread.Interrupt();
            }
            catch (Exception)
            {
                disabled = true;
            }
        }

        private void StartSnapshot(IntPtr sourceImage, object witness)
        {
            if (sourceImage == IntPtr.Zero || (Marshal.ReadByte(sourceImage, 0x1c) & 1) != 0)
            {
                return;
            }
            IntPtr table = IntPtr.Add(sourceImage, 0x1b0);
            int rows = Marshal.ReadInt32(table, 8) & 0xffffff;
            int types = Marshal.ReadInt32(sourceImage, 0x118) & 0xffffff;
            int stride = Marshal.ReadByte(table, 11);
            int flags = Marshal.ReadInt32(table, 12);
            int parentWidth = (flags & 3) + 1;
            int typeWidth = ((flags >> 2) & 3) + 1;
            int blobWidth = ((flags >> 4) & 3) + 1;
            if (rows < 256 || rows > MaximumRows || types < 256 || types > MaximumRows ||
                (flags >> 24) != 3 || parentWidth is not (2 or 4) || typeWidth is not (2 or 4) ||
                blobWidth is not (2 or 4) || stride != parentWidth + typeWidth + blobWidth)
            {
                return;
            }
            IntPtr data = Marshal.ReadIntPtr(table);
            int length = checked(rows * stride);
            if (data == IntPtr.Zero || !Readable(data, length))
            {
                return;
            }
            byte[] copy = new byte[length];
            bool[] output = new bool[types + 1];
            // No borrowed metadata pointer escapes the synchronous owner copy.
            Marshal.Copy(data, copy, 0, copy.Length);
            GC.KeepAlive(witness);
            GC.KeepAlive(assembly);
            image = sourceImage;
            started = true;
            pending = Task.Factory.StartNew(Decode, new Input(copy, output, rows, stride, parentWidth),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _ = pending.ContinueWith(ObserveFault, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static bool[] Decode(object? state)
        {
            Input input = (Input)state!;
            for (int row = 0, offset = 0; row < input.Rows; row++, offset += input.Stride)
            {
                uint parent = (uint)(input.Bytes[offset] | (input.Bytes[offset + 1] << 8));
                if (input.Width == 4)
                {
                    parent |= (uint)input.Bytes[offset + 2] << 16;
                    parent |= (uint)input.Bytes[offset + 3] << 24;
                }
                uint type = parent >> 5;
                if ((parent & 31) == 3 && type > 0 && type < input.Output.Length)
                {
                    input.Output[(int)type] = true;
                }
            }
            return input.Output;
        }

        private static void ObserveFault(Task<bool[]> task) => _ = task.Exception;

        private static bool Readable(IntPtr address, int length)
        {
            ulong cursor = unchecked((ulong)address.ToInt64());
            ulong end = checked(cursor + (uint)length);
            while (cursor < end)
            {
                if (VirtualQuery(new IntPtr(unchecked((long)cursor)), out MemoryRegion region, new UIntPtr(48)).ToUInt64() != 48 ||
                    region.State != 0x1000 || (region.Protect & 0x100) != 0 || (region.Protect & 0xee) == 0)
                {
                    return false;
                }
                ulong next = checked(unchecked((ulong)region.BaseAddress.ToInt64()) + region.RegionSize.ToUInt64());
                if (next <= cursor)
                {
                    return false;
                }
                cursor = next;
            }
            return true;
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryRegion region, UIntPtr length);

        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct MemoryRegion
        {
            [FieldOffset(0)] internal IntPtr BaseAddress;
            [FieldOffset(24)] internal UIntPtr RegionSize;
            [FieldOffset(32)] internal uint State;
            [FieldOffset(36)] internal uint Protect;
        }

        public void Dispose()
        {
            disabled = true;
            pending = null;
            present = null;
        }

        private sealed class Input(byte[] bytes, bool[] output, int rows, int stride, int width)
        {
            internal readonly byte[] Bytes = bytes;
            internal readonly bool[] Output = output;
            internal readonly int Rows = rows, Stride = stride, Width = width;
        }

        internal sealed class Layout
        {
            internal readonly Type RuntimeType;
            internal readonly MethodBase[] Guards;
            private readonly IntPtr attributeClass;

            private Layout(Type runtimeType)
            {
                RuntimeType = runtimeType;
                Guards =
                [
                    runtimeType.GetProperty(nameof(Type.TypeHandle))!.GetMethod!,
                    runtimeType.GetProperty(nameof(Type.Assembly))!.GetMethod!,
                    typeof(RuntimeTypeHandle).GetProperty(nameof(RuntimeTypeHandle.Value))!.GetMethod!,
                    typeof(Marshal).GetMethod(nameof(Marshal.ReadByte), [typeof(IntPtr), typeof(int)])!,
                    typeof(Marshal).GetMethod(nameof(Marshal.ReadInt16), [typeof(IntPtr), typeof(int)])!,
                    typeof(Marshal).GetMethod(nameof(Marshal.ReadInt32), [typeof(IntPtr), typeof(int)])!,
                    typeof(Marshal).GetMethod(nameof(Marshal.ReadIntPtr), [typeof(IntPtr), typeof(int)])!,
                    typeof(Marshal).GetMethod(nameof(Marshal.ReadIntPtr), [typeof(IntPtr)])!,
                    typeof(Marshal).GetMethod(nameof(Marshal.Copy), [typeof(IntPtr), typeof(byte[]), typeof(int), typeof(int)])!
                ];
                if (!Guards.All(method => HarmonyLib.Harmony.GetPatchInfo(method)?.Owners.Count is not > 0))
                {
                    throw new NotSupportedException();
                }
                using Process process = Process.GetCurrentProcess();
                ProcessModule? module = process.Modules.Cast<ProcessModule>()
                    .SingleOrDefault(item => string.Equals(item.ModuleName, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase));
                if (module == null || module.ModuleMemorySize != 10756096)
                {
                    throw new NotSupportedException();
                }
                IntPtr address = module.BaseAddress;
                int peOffset = Marshal.ReadInt32(address, 0x3c);
                if (Marshal.ReadInt16(address, 0) != 0x5a4d || peOffset is < 0x40 or > 0x1000)
                {
                    throw new NotSupportedException();
                }
                IntPtr pe = IntPtr.Add(address, peOffset);
                if (Marshal.ReadInt32(pe, 0) != 0x4550 || unchecked((ushort)Marshal.ReadInt16(pe, 4)) != 0x8664 ||
                    Marshal.ReadInt32(pe, 8) != 1770675974 || Marshal.ReadInt16(pe, 24) != 0x20b ||
                    Marshal.ReadInt32(pe, 80) != 10756096 ||
                    !Matches(IntPtr.Add(address, 0x6caa78), "525344536b9fc91a3afdc04d93e7782ca1b4be7d01000000") ||
                    !Matches(IntPtr.Add(address, 0xcb816), "f64720010f854e080000") ||
                    !Matches(IntPtr.Add(address, 0xcc06e), "8b4728c1e814f7d083e001") ||
                    !Matches(IntPtr.Add(address, 0x192970), "0fbe410ac3") ||
                    !Matches(IntPtr.Add(address, 0xba951), "488b0b488bc14883c4205bc3") ||
                    !Matches(IntPtr.Add(address, 0xc1120), "488b4140c3") ||
                    !Matches(IntPtr.Add(address, 0xc11f0), "8b4158c3") ||
                    !Matches(IntPtr.Add(address, 0x14d470), "0fb6411c83e001c3") ||
                    !Matches(IntPtr.Add(address, 0x14d410), "83fa37770f4863c24883c00f48c1e0044803c1c333c0c3") ||
                    !Matches(IntPtr.Add(address, 0x14d450), "8b410825ffffff00c3") ||
                    !Matches(IntPtr.Add(address, 0x180fe0), "33c0c3") ||
                    !Matches(IntPtr.Add(address, 0x257140), "c702000000004c8bca80791b03750a488b81f0000000488b08488b4140f6401c01740b488bd1488bc8e922a3ffff0fb691c200000080fa13741c80fa1e74178b5158488bc881e2ffffff00c1e20583ca03e9faf8ffff8b9190000000488bc881e2ffffff00c1e20583ca13e9e0f8ffff") ||
                    !Matches(IntPtr.Add(address, 0x184110), "48895c240848896c24104889742418574883ec208b4108418bf08b590c25ffffff008bea488bf93bd072204c8d0d363b500041b8fb050000488d1591385000488d0d7aea4e00e8658dedff8bc3c1e8183bf072204c8d0db53b500041b8fc050000488d1568385000488d0d51ea4e00e83c8dedff440fb6470b4533c9440fafc58bc383e003458bd1ffc04c0307418bf983fe0272438d4efed1e9418d5104ffc1448bd98d3c0966660f1f8400000000008bc88bc34c03c98d4afed3e88bca83e00383c204ffc04c03d08bc3d3e883e003ffc04983eb0175d83bfe73158bc88bc34c03c18d0c7d02000000d3e883e003ffc04b8d140a83e801742983e801741d83f8027412ba0c060000488d0dc0375000e82b8dedffcc418b0410eb0c410fb70410eb05410fbe0410488b5c2430488b6c2438488b7424404883c4205fc3"))
                {
                    throw new NotSupportedException();
                }
                attributeClass = Class(typeof(BindAttribute));
                if (attributeClass == IntPtr.Zero)
                {
                    throw new NotSupportedException();
                }
            }

            internal static Layout? TryCreate(Type runtimeType, Assembly assembly)
            {
                if (IntPtr.Size != 8 || !BitConverter.IsLittleEndian || Environment.OSVersion.Platform != PlatformID.Win32NT ||
                    !AppDomain.CurrentDomain.IsDefaultAppDomain() || assembly.GetType() != typeof(Attribute).Assembly.GetType())
                {
                    return null;
                }
                try
                {
                    return new Layout(runtimeType);
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

            // Recheck both native flags; earlier initialization does not rule out a later failure.
            internal bool AttributeReady() => (Marshal.ReadByte(attributeClass, 0x20) & 1) != 0 &&
                (Marshal.ReadInt32(attributeClass, 0x28) & 0x100000) == 0;

            internal static IntPtr Class(Type type)
            {
                IntPtr handle = type.TypeHandle.Value;
                if (handle == IntPtr.Zero || Marshal.ReadByte(handle, 10) != 0x12)
                {
                    return IntPtr.Zero;
                }
                IntPtr klass = Marshal.ReadIntPtr(handle);
                return klass == IntPtr.Zero || Marshal.ReadByte(klass, 0x1b) == 3 ||
                    Marshal.ReadByte(klass, 0xc2) is 0x13 or 0x1e ? IntPtr.Zero : klass;
            }

            private static bool Matches(IntPtr address, string expected)
            {
                for (int i = 0; i < expected.Length / 2; i++)
                {
                    if (Marshal.ReadByte(address, i) != Convert.ToByte(expected.Substring(i * 2, 2), 16))
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}
