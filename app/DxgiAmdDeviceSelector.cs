using System.Runtime.InteropServices;
namespace SheepCode;

internal static class DxgiAmdDeviceSelector
{
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const uint AmdVendorId = 0x1002;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, IntPtr description);
    [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    public static int FindAmdAdapter() => FindAdapter(AmdVendorId, "RX 580");
    public static int FindNvidiaAdapter() => FindAdapter(0x10de, "RTX 2060 SUPER");
    private static int FindAdapter(uint vendorId, string requiredName)
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = Factory1Id;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return -1;
            var factoryTable = Marshal.ReadIntPtr(factory);
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(Marshal.ReadIntPtr(factoryTable, 12 * IntPtr.Size));
            var description = Marshal.AllocHGlobal(1024);
            try
            {
                var bestIndex = -1;
                long bestDedicatedMemory = -1;
                string bestName = "";
                for (uint i = 0; i < 32; i++)
                {
                    if (enumAdapters(factory, i, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        var table = Marshal.ReadIntPtr(adapter);
                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(Marshal.ReadIntPtr(table, 10 * IntPtr.Size));
                        if (getDesc(adapter, description) >= 0 && unchecked((uint)Marshal.ReadInt32(description, 256)) == vendorId)
                        {
                            var name = Marshal.PtrToStringUni(description, 128)?.TrimEnd('\0') ?? "AMD";
                            if (name.Contains(requiredName, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[TTS/GPU] DirectML seleccionará {name} (DXGI {i}).");
                                return (int)i;
                            }
                            var dedicatedMemory = Marshal.ReadInt64(description, 272);
                            if (dedicatedMemory > bestDedicatedMemory)
                            {
                                bestDedicatedMemory = dedicatedMemory;
                                bestIndex = (int)i;
                                bestName = name;
                            }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
                if (bestIndex >= 0) Console.WriteLine($"[TTS/GPU] Encontré {bestName}, pero se solicitó {requiredName}.");
                return -1; // Never silently assign the voice to a different AMD card.
            }
            finally { Marshal.FreeHGlobal(description); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException or ArgumentException)
        {
            Console.WriteLine($"[TTS/GPU] No pude enumerar adaptadores DirectML: {ex.Message}");
            return -1;
        }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
    }
}
