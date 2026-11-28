using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ntc.Unity
{
    // Query inside the Player: DXGI CurrentUsage belongs to the calling process.
    public static class NtcProcessMemory
    {
        [Serializable]
        public sealed class Snapshot
        {
            public long workingSetBytes, privateBytes;
            public bool cpuAvailable;
            public bool gpuAvailable;
            public string gpuStatus;
            public string adapter;
            public ulong localUsageBytes, localBudgetBytes, nonLocalUsageBytes;
        }

        public static Snapshot Capture(int vendorId, int deviceId)
        {
            var s = new Snapshot();
            using (var p = Process.GetCurrentProcess())
            {
                s.workingSetBytes = p.WorkingSet64;
                s.privateBytes = p.PrivateMemorySize64;
                s.cpuAvailable = s.workingSetBytes > 0;
            }
            s.gpuStatus = "DXGI unavailable on this platform";
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var processMemory = new ProcessMemory { size = (uint)Marshal.SizeOf(typeof(ProcessMemory)) };
            if (GetProcessMemoryInfo(new IntPtr(-1), ref processMemory, processMemory.size))
            {
                s.workingSetBytes = (long)processMemory.workingSet.ToUInt64();
                s.privateBytes = (long)processMemory.privateUsage.ToUInt64();
                s.cpuAvailable = true;
            }
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, adapter3 = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out factory));
                var enumerate = Method<EnumAdapters>(factory, 12);
                for (uint i = 0; ; i++)
                {
                    int hr = enumerate(factory, i, out adapter);
                    if (hr == unchecked((int)0x887a0002)) break;
                    Marshal.ThrowExceptionForHR(hr);
                    AdapterDesc desc;
                    Marshal.ThrowExceptionForHR(Method<GetDesc>(adapter, 10)(adapter, out desc));
                    if (desc.vendorId == vendorId && desc.deviceId == deviceId)
                    {
                        iid = new Guid("645967a4-1392-4310-a798-8053ce3e93fd");
                        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(adapter, ref iid, out adapter3));
                        VideoMemory local, nonLocal;
                        var query = Method<QueryMemory>(adapter3, 14);
                        Marshal.ThrowExceptionForHR(query(adapter3, 0, 0, out local));
                        Marshal.ThrowExceptionForHR(query(adapter3, 0, 1, out nonLocal));
                        s.adapter = desc.description;
                        s.localUsageBytes = local.usage;
                        s.localBudgetBytes = local.budget;
                        s.nonLocalUsageBytes = nonLocal.usage;
                        s.gpuAvailable = true;
                        s.gpuStatus = "DXGI process video memory, node 0; includes render targets and driver allocations";
                        return s;
                    }
                    Marshal.Release(adapter); adapter = IntPtr.Zero;
                }
                s.gpuStatus = "No DXGI adapter matching Unity vendor/device IDs";
            }
            catch (Exception e) { s.gpuStatus = e.GetType().Name + ": " + e.Message; }
            finally
            {
                if (adapter3 != IntPtr.Zero) Marshal.Release(adapter3);
                if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
#endif
            return s;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        struct ProcessMemory
        {
            public uint size, faults;
            public UIntPtr peakWorkingSet, workingSet, peakPagedPool, pagedPool, peakNonPagedPool, nonPagedPool, pagefile, peakPagefile, privateUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemory memory, uint size);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct AdapterDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
            public int vendorId, deviceId, subsystemId, revision;
            public UIntPtr dedicatedVideoMemory, dedicatedSystemMemory, sharedSystemMemory;
            public uint luidLow; public int luidHigh; public uint flags;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct VideoMemory { public ulong budget, usage, availableReservation, reservation; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetDesc(IntPtr self, out AdapterDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int QueryMemory(IntPtr self, uint node, int segment, out VideoMemory memory);
        [DllImport("dxgi.dll", ExactSpelling = true)]
        static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        static T Method<T>(IntPtr instance, int slot) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T)) as T;
        }
#endif
    }
}
