using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace STS2Mobile;

/// <summary>
/// Reads the current iOS process physical footprint without allocating a managed buffer.
/// </summary>
/// <remarks>
/// This is the value reported by Darwin's <c>ri_phys_footprint</c>, rather than the
/// virtual address-space or resident-size counters. The native call is unavailable
/// on non-iOS targets, where this type returns <see langword="false"/>.
/// </remarks>
public static class IosPhysicalFootprint
{
    private const int RusageInfoFlavorV0 = 0;
    private const int BindingAvailable = 1;
    private const int BindingUnavailable = -1;

    // A missing libproc binding is a permanent condition for a process, so avoid
    // throwing an exception on every sampling tick after the first failed attempt.
    private static int _bindingState;

    /// <summary>
    /// Attempts to read this process's physical footprint in bytes.
    /// </summary>
    /// <param name="bytes">The physical footprint when the call succeeds.</param>
    /// <returns><see langword="true"/> when iOS returned a representable value.</returns>
    public static bool TryGetPhysicalFootprintBytes(out long bytes)
    {
        bytes = 0;

        if (!OperatingSystem.IsIOS() || Volatile.Read(ref _bindingState) == BindingUnavailable)
        {
            return false;
        }

        try
        {
            var usage = default(RusageInfoV0);
            int result = ProcPidRusage(Environment.ProcessId, RusageInfoFlavorV0, ref usage);
            Volatile.Write(ref _bindingState, BindingAvailable);

            if (result != 0 || usage.PhysFootprint > long.MaxValue)
            {
                return false;
            }

            bytes = (long)usage.PhysFootprint;
            return true;
        }
        catch (DllNotFoundException)
        {
            Volatile.Write(ref _bindingState, BindingUnavailable);
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref _bindingState, BindingUnavailable);
            return false;
        }
        catch (BadImageFormatException)
        {
            Volatile.Write(ref _bindingState, BindingUnavailable);
            return false;
        }
        catch (MarshalDirectiveException)
        {
            Volatile.Write(ref _bindingState, BindingUnavailable);
            return false;
        }
    }

    // libproc.h declares proc_pid_rusage as:
    // int proc_pid_rusage(int pid, int flavor, rusage_info_t *buffer);
    // rusage_info_t is a pointer-sized opaque type; ref RusageInfoV0 supplies the
    // address of the blittable v0 buffer expected by the native implementation.
    [DllImport(
        "libproc.dylib",
        EntryPoint = "proc_pid_rusage",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int ProcPidRusage(int pid, int flavor, ref RusageInfoV0 buffer);

    // Mirrors Darwin's struct rusage_info_v0 from bsd/sys/resource.h. Its first
    // member is uint8_t ri_uuid[16], represented by two ulongs so the struct stays
    // blittable and the call remains allocation-free. The PhysFootprint field is
    // therefore at offset 72 and the complete v0 record is 96 bytes on arm64.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RusageInfoV0
    {
        public ulong UuidPart0;
        public ulong UuidPart1;
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PackageIdleWakeups;
        public ulong InterruptWakeups;
        public ulong Pageins;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysFootprint;
        public ulong ProcessStartAbsoluteTime;
        public ulong ProcessExitAbsoluteTime;
    }
}
