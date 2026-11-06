using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AIAnnounce;

// VRAM via NVML (ships with the NVIDIA driver in System32). No NVIDIA GPU means no samples, and the
// overlay simply hides the readout.
sealed class GpuMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlUtilization { public uint Gpu, Memory; }

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    readonly List<(IntPtr Handle, string Name)> devices = new();

    public GpuMonitor()
    {
        try
        {
            if (nvmlInit_v2() != 0 || nvmlDeviceGetCount_v2(out uint count) != 0) return;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr h) != 0) continue;
                var buffer = new byte[96];
                string name = "GPU";
                if (nvmlDeviceGetName(h, buffer, (uint)buffer.Length) == 0)
                {
                    int z = Array.IndexOf(buffer, (byte)0);
                    name = Encoding.ASCII.GetString(buffer, 0, z < 0 ? buffer.Length : z)
                        .Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();
                }
                devices.Add((h, name));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
    }

    public IReadOnlyList<GpuSample> Sample()
    {
        var list = new List<GpuSample>(devices.Count);
        foreach (var (handle, name) in devices)
        {
            try
            {
                if (nvmlDeviceGetMemoryInfo(handle, out var mem) != 0) continue;
                uint util = nvmlDeviceGetUtilizationRates(handle, out var u) == 0 ? u.Gpu : 0;
                list.Add(new GpuSample(name, mem.Used, mem.Total, util));
            }
            catch (EntryPointNotFoundException) { }
        }
        return list;
    }
}
