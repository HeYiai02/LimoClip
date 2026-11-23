using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveMonitor.Media.Recording;

/// <summary>
/// Windows Job Object 守卫：把 ffmpeg 子进程挂到同一个带 KILL_ON_JOB_CLOSE 的 Job 上。
/// 宿主进程正常退出、崩溃或被任务管理器强杀时，句柄随之关闭，内核会强制结束 Job 内所有进程，
/// 避免录制结束后残留 ffmpeg 僵尸进程。非 Windows 平台自动降级为无操作。
/// </summary>
public sealed class ProcessJobGuard : IDisposable
{
    /// <summary>宿主退出时一并终止 Job 内全部进程。</summary>
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly object _gate = new();
    private nint _jobHandle;
    private bool _disposed;

    public ProcessJobGuard()
    {
        if (!OperatingSystem.IsWindows())
        {
            _jobHandle = nint.Zero;
            return;
        }

        var handle = NativeMethods.CreateJobObject(nint.Zero, null);
        if (handle == nint.Zero)
        {
            throw new InvalidOperationException(
                $"创建 Job Object 失败：Win32Error={Marshal.GetLastWin32Error()}");
        }

        var information = new JobObjectExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        if (!NativeMethods.SetInformationJobObject(
                handle,
                NativeMethods.JobObjectInfoTypeExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(handle);
            throw new InvalidOperationException(
                $"设置 Job Object KILL_ON_JOB_CLOSE 失败：Win32Error={error}");
        }

        _jobHandle = handle;
    }

    /// <summary>当前平台是否持有可用的 Job 句柄。</summary>
    public bool IsAvailable => Volatile.Read(ref _jobHandle) != nint.Zero;

    /// <summary>最近一次 <see cref="TryAssign"/> 失败的 Win32 错误码；成功为 null。</summary>
    public int? LastAssignError { get; private set; }

    /// <summary>
    /// 尝试把进程挂到 Job 上。失败（例如进程已在别的 Job 里、权限不足）只记录错误码，
    /// 不阻断录制流程，由调用方决定是否记录告警。
    /// </summary>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_jobHandle == nint.Zero)
            {
                LastAssignError = null;
                return false;
            }

            try
            {
                if (NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle))
                {
                    LastAssignError = null;
                    return true;
                }

                LastAssignError = Marshal.GetLastWin32Error();
                return false;
            }
            catch (InvalidOperationException)
            {
                // Process 尚未启动 / 已退出时访问 Handle 会抛异常。
                LastAssignError = -1;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_jobHandle != nint.Zero)
            {
                // 关闭句柄会触发 KILL_ON_JOB_CLOSE，终止所有仍在运行的成员进程。
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = nint.Zero;
            }
        }
    }

    private static class NativeMethods
    {
        public const int JobObjectInfoTypeExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateJobObject(nint jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(
            nint jobHandle,
            int jobObjectInformationClass,
            ref JobObjectExtendedLimitInformation jobObjectInformation,
            uint jobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(nint jobHandle, nint processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
