using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

internal static class WhisperRuntime
{
    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int SetUcrtEnvironment(string name, string value);
    [DllImport("msvcrt.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int SetLegacyEnvironment(string name, string value);

    private static void SetNativeEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        // ggml reads the C runtime environment. Updating only the Win32/.NET
        // environment after process startup can leave getenv() seeing an old value.
        if (SetUcrtEnvironment(name, value) != 0 || SetLegacyEnvironment(name, value) != 0)
            throw new InvalidOperationException("No se pudo seleccionar la RX para Whisper.");
    }

    public static void Configure()
    {
        // Keep transcription on AMD. Vulkan can see both vendors, so hide every
        // adapter except the RX 580 before whisper.cpp creates its GPU context.
        // If the AMD adapter is not discoverable, use CPU rather than stealing
        // VRAM/compute from the RTX 2060 S that serves vision and depth.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        if (VulkanAmdDeviceSelector.TryFindAmdAdapter(out var adapterIndex, out var adapterName))
        {
            SetNativeEnvironment("GGML_VK_VISIBLE_DEVICES", adapterIndex.ToString());
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
            Console.WriteLine($"[WHISPER/GPU] RX asignada a Vulkan: {adapterName} (adaptador {adapterIndex}); la RTX queda libre para visión/profundidad.");
        }
        else
        {
            SetNativeEnvironment("GGML_VK_VISIBLE_DEVICES", "");
            Console.WriteLine("[WHISPER/GPU] No pude identificar una Radeon por Vulkan; Whisper usará CPU para no cargar la RTX.");
        }

        // CUDA libraries are still kept available to the other integrated
        // components, but Whisper's runtime order deliberately excludes CUDA.
        string[] directories =
        [
            Path.Combine(AppContext.BaseDirectory, "cuda"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Ollama", "lib", "ollama", "cuda_v12")
        ];
        string[] libraries = ["cublasLt64_12.dll", "cublas64_12.dll", "cudart64_12.dll"];
        foreach (var directory in directories)
        {
            if (!libraries.All(name => File.Exists(Path.Combine(directory, name)))) continue;
            try
            {
                // Keep these native modules loaded for the lifetime of Whisper. Full paths
                // allow Windows to resolve CUDA dependencies without changing the system PATH.
                foreach (var name in libraries) NativeLibrary.Load(Path.Combine(directory, name));
                Console.WriteLine("[CUDA] Bibliotecas CUDA 12 disponibles para Whisper.");
                return;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                Console.WriteLine($"[CUDA] No se pudieron cargar las bibliotecas: {ex.Message}");
            }
        }
    }
}

/// <summary>Finds AMD's Vulkan physical-device index so Whisper can target RX 580, not NVIDIA.</summary>
internal static class VulkanAmdDeviceSelector
{
    private const int AmdVendorId = 0x1002;
    private const int VkStructureTypeApplicationInfo = 0;
    private const int VkStructureTypeInstanceCreateInfo = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int StructureType;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int StructureType;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int vkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr physicalDevice, IntPtr properties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    public static bool TryFindAmdAdapter(out int adapterIndex, out string adapterName)
    {
        adapterIndex = -1;
        adapterName = "";
        IntPtr appName = IntPtr.Zero;
        IntPtr engineName = IntPtr.Zero;
        IntPtr appInfoPointer = IntPtr.Zero;
        IntPtr deviceList = IntPtr.Zero;
        IntPtr properties = IntPtr.Zero;
        IntPtr instance = IntPtr.Zero;
        try
        {
            appName = Marshal.StringToCoTaskMemUTF8("SheepGPT Whisper");
            engineName = Marshal.StringToCoTaskMemUTF8("SheepGPT");
            var appInfo = new VkApplicationInfo
            {
                StructureType = VkStructureTypeApplicationInfo,
                ApplicationName = appName,
                ApplicationVersion = 1,
                EngineName = engineName,
                EngineVersion = 1,
                ApiVersion = 1u << 22 // Vulkan 1.0
            };
            appInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(appInfo, appInfoPointer, false);
            var createInfo = new VkInstanceCreateInfo
            {
                StructureType = VkStructureTypeInstanceCreateInfo,
                ApplicationInfo = appInfoPointer
            };
            if (vkCreateInstance(ref createInfo, IntPtr.Zero, out instance) != 0 || instance == IntPtr.Zero)
                return false;

            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != 0 || count == 0) return false;
            deviceList = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            if (vkEnumeratePhysicalDevices(instance, ref count, deviceList) != 0) return false;
            properties = Marshal.AllocHGlobal(4096);
            var bestAmdIndex = -1;
            var bestAmdName = "";
            var bestAmdIsDiscrete = false;
            for (var i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(deviceList, checked((int)i * IntPtr.Size));
                Marshal.WriteByte(properties, 0);
                vkGetPhysicalDeviceProperties(device, properties);
                var vendorId = unchecked((uint)Marshal.ReadInt32(properties, 8));
                if (vendorId != AmdVendorId) continue;
                var name = Marshal.PtrToStringAnsi(IntPtr.Add(properties, 20), 256)?.TrimEnd('\0') ?? "AMD Radeon";
                if (name.Contains("RX 580", StringComparison.OrdinalIgnoreCase))
                {
                    adapterIndex = (int)i;
                    adapterName = name;
                    return true;
                }
                var isDiscrete = Marshal.ReadInt32(properties, 16) == 2; // VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU
                if (bestAmdIndex < 0 || (isDiscrete && !bestAmdIsDiscrete))
                {
                    bestAmdIndex = (int)i;
                    bestAmdName = name;
                    bestAmdIsDiscrete = isDiscrete;
                }
            }
            adapterIndex = bestAmdIndex;
            adapterName = bestAmdName;
            return bestAmdIndex >= 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or ExternalException)
        {
            Console.WriteLine($"[WHISPER/GPU] No se pudo consultar Vulkan: {ex.Message}");
            return false;
        }
        finally
        {
            if (instance != IntPtr.Zero) vkDestroyInstance(instance, IntPtr.Zero);
            if (properties != IntPtr.Zero) Marshal.FreeHGlobal(properties);
            if (deviceList != IntPtr.Zero) Marshal.FreeHGlobal(deviceList);
            if (appInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(appInfoPointer);
            if (engineName != IntPtr.Zero) Marshal.FreeCoTaskMem(engineName);
            if (appName != IntPtr.Zero) Marshal.FreeCoTaskMem(appName);
        }
    }
}
