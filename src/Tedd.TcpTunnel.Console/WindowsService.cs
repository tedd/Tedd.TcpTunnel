using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tedd.TcpTunnel.Console;

internal static class WindowsService
{
    private const uint ServiceAcceptStop = 0x1;
    private const uint ServiceAcceptShutdown = 0x4;
    private const uint ServiceControlStop = 0x1;
    private const uint ServiceControlShutdown = 0x5;
    private const uint ServiceStopped = 0x1;
    private const uint ServiceStartPending = 0x2;
    private const uint ServiceStopPending = 0x3;
    private const uint ServiceRunning = 0x4;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ErrorServiceSpecificError = 1066;
    private static readonly ServiceMainCallback MainCallback = ServiceMain;
    private static readonly HandlerCallback ControlCallback = Handler;
    private static Func<CancellationToken, Task<int>>? _run;
    private static CancellationTokenSource? _stop;
    private static nint _statusHandle;
    private static string _serviceName = "Tedd.TcpTunnel";
    private static bool _testMode;
    private static bool _testRegistrationFails;
    private static bool? _dispatcherResultForTest;
    private static uint _lastState;
    private static int _lastExitCode;

    public static int Run(string serviceName, Func<CancellationToken, Task<int>> run)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _serviceName = serviceName;
        _run = run;
        var table = new[] { new ServiceTableEntry(serviceName, MainCallback), new ServiceTableEntry(null, null) };
        if (!(_dispatcherResultForTest ?? StartServiceCtrlDispatcher(table)))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to connect to the Windows Service Control Manager.");
        return 0;
    }

    internal static (uint State, int ExitCode, bool Cancelled) ExerciseLifecycle(bool stop, bool failure, bool registrationFails)
    {
        var cancelled = false;
        _testMode = true;
        _testRegistrationFails = registrationFails;
        _lastState = 0;
        _lastExitCode = 0;
        _run = async token =>
        {
            if (stop) _ = Handler(ServiceControlStop, 0, 0, 0);
            else _ = Handler(999, 0, 0, 0);
            await Task.Yield();
            cancelled = token.IsCancellationRequested;
            if (failure) throw new IOException("service failure");
            return 0;
        };
        try { ServiceMain(0, 0); return (_lastState, _lastExitCode, cancelled); }
        finally { _testMode = false; _testRegistrationFails = false; }
    }

    internal static void SetDispatcherResultForTest(bool? result) => _dispatcherResultForTest = result;

    private static void ServiceMain(uint argumentCount, nint arguments)
    {
        _statusHandle = _testMode ? _testRegistrationFails ? 0 : 1 : RegisterServiceCtrlHandlerEx(_serviceName, ControlCallback, 0);
        if (_statusHandle == 0) return;
        _stop = new CancellationTokenSource();
        Report(ServiceStartPending, 0, 1, 10000);
        Report(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown);
        var exitCode = 1;
        try { exitCode = (_run ?? throw new InvalidOperationException("Service callback is unavailable."))(_stop.Token).GetAwaiter().GetResult(); }
        catch { exitCode = 1; }
        finally
        {
            Report(ServiceStopped, 0, 0, 0, exitCode);
            _stop.Dispose();
            _stop = null;
        }
    }

    private static uint Handler(uint control, uint eventType, nint eventData, nint context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            Report(ServiceStopPending, 0, 1, 30000);
            _stop?.Cancel();
        }
        return 0;
    }

    private static void Report(uint state, uint accepted, uint checkpoint = 0, uint waitHint = 0, int exitCode = 0)
    {
        _lastState = state;
        _lastExitCode = exitCode;
        if (_statusHandle == 0) return;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = accepted,
            Win32ExitCode = exitCode == 0 ? 0u : ErrorServiceSpecificError,
            ServiceSpecificExitCode = (uint)Math.Max(0, exitCode),
            CheckPoint = checkpoint,
            WaitHint = waitHint
        };
        if (!_testMode) _ = SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct ServiceTableEntry(string? name, ServiceMainCallback? callback)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string? Name = name;
        public readonly ServiceMainCallback? Callback = callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(uint argumentCount, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerCallback(uint control, uint eventType, nint eventData, nint context);

#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerEx(string serviceName, HandlerCallback callback, nint context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint statusHandle, ref ServiceStatus status);
#pragma warning restore SYSLIB1054
}
