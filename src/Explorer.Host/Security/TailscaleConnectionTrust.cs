using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Win32.SafeHandles;

namespace Explorer.Host.Security;

/// <summary>Checks who owns the client end of an accepted IPv4 loopback connection.</summary>
public sealed class TailscaleConnectionTrust
{
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const int ErrorInsufficientBuffer = 122;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const int ServiceRunning = 4;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const int SystemProcessInformation = 5;
    private const string DaemonName = "tailscaled.exe";
    private readonly ConditionalWeakTable<IConnectionItemsFeature, ConnectionDecision> _decisions = new();

    public bool IsTrusted(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Connection;
        if (!OperatingSystem.IsWindows() || !IPAddress.Loopback.Equals(connection.LocalIpAddress) ||
            !IPAddress.Loopback.Equals(connection.RemoteIpAddress) ||
            connection.LocalPort is < 1 or > ushort.MaxValue || connection.RemotePort is < 1 or > ushort.MaxValue)
            return false;

        // The feature belongs to the transport connection and disappears on disconnect.
        // A new connection using a recycled port must be checked independently.
        var transport = context.Features.Get<IConnectionItemsFeature>();
        if (transport is null)
            return VerifyPeer(connection.RemotePort, connection.LocalPort);
        return _decisions.GetValue(transport, _ => new ConnectionDecision(
            VerifyPeer(connection.RemotePort, connection.LocalPort))).Trusted;
    }

    private static bool VerifyPeer(int clientPort, int serverPort)
    {
        var owner = FindSocketOwner(clientPort, serverPort);
        if (owner == 0) return false;

        using var manager = OpenSCManager(null, null, 1 /* SC_MANAGER_CONNECT */);
        if (manager.IsInvalid) return false;
        using var service = OpenService(manager, "Tailscale", 4 /* SERVICE_QUERY_STATUS */);
        if (!GetServicePid(service, out var servicePid)) return false;
        if (owner == servicePid) return true;

        // Windows Tailscale launches a worker using the service executable. A
        // normal desktop user cannot query this SYSTEM process via OpenProcess.
        // Match both processes in one kernel snapshot, including start times so
        // an unrelated parent PID reused by the service cannot satisfy the test.
        if (!IsDaemonChild(owner, servicePid)) return false;
        return GetServicePid(service, out var currentServicePid) && currentServicePid == servicePid;
    }

    private static bool GetServicePid(ServiceHandle service, out uint processId)
    {
        processId = 0;
        if (service.IsInvalid ||
            !QueryServiceStatusEx(service, 0 /* SC_STATUS_PROCESS_INFO */, out var status,
                (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) ||
            status.CurrentState != ServiceRunning || (status.ServiceType & ServiceWin32OwnProcess) == 0 ||
            status.ProcessId == 0)
            return false;
        processId = status.ProcessId;
        return true;
    }

    private static uint FindSocketOwner(int clientPort, int serverPort)
    {
        var length = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref length, false, AfInet, TcpTableOwnerPidConnections, 0) != ErrorInsufficientBuffer)
            return 0;
        var client = BinaryPrimitives.ReverseEndianness((ushort)clientPort);
        var server = BinaryPrimitives.ReverseEndianness((ushort)serverPort);
        const uint loopback = 0x0100007f; // 127.0.0.1 in the IPv4 table's byte order.
        var rowSize = Marshal.SizeOf<TcpRow>();
        for (var attempt = 0; attempt < 3 && length is > sizeof(int) and <= MaximumTableBytes; attempt++)
        {
            var capacity = length;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref length, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return 0;
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (capacity - sizeof(int)) / rowSize) return 0;
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(buffer + sizeof(int) + index * rowSize);
                    if (row.LocalAddress == loopback && row.RemoteAddress == loopback &&
                        (ushort)row.LocalPort == client && (ushort)row.RemotePort == server)
                        return row.ProcessId;
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return 0;
    }

    private static bool IsDaemonChild(uint childId, uint serviceId)
    {
        var length = 256 * 1024;
        var rowSize = Marshal.SizeOf<ProcessInfoPrefix>();
        for (var attempt = 0; attempt < 4 && length <= MaximumTableBytes; attempt++)
        {
            var capacity = length;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                var result = NtQuerySystemInformation(SystemProcessInformation, buffer, capacity, out var used);
                if (result == StatusInfoLengthMismatch)
                {
                    if (used < 0 || used > MaximumTableBytes) return false;
                    length = Math.Max(capacity * 2, used);
                    continue;
                }
                if (result != 0 || used < rowSize || used > capacity) return false;

                ProcessInfoPrefix? child = null;
                ProcessInfoPrefix? parent = null;
                for (var offset = 0; offset <= used - rowSize;)
                {
                    var row = Marshal.PtrToStructure<ProcessInfoPrefix>(buffer + offset);
                    if (row.ProcessId == childId) child = row;
                    if (row.ProcessId == serviceId) parent = row;
                    if (child is not null && parent is not null) break;
                    if (row.NextEntryOffset < rowSize || row.NextEntryOffset > used - offset - rowSize) break;
                    offset += (int)row.NextEntryOffset;
                }

                return child is { SessionId: 0 } worker && parent is { SessionId: 0 } daemon &&
                    worker.ParentProcessId == serviceId && daemon.CreatedAt > 0 && worker.CreatedAt > daemon.CreatedAt &&
                    HasDaemonName(worker.ImageName, buffer, used) && HasDaemonName(daemon.ImageName, buffer, used);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    private static bool HasDaemonName(UnicodeString name, IntPtr buffer, int length)
    {
        if (name.Length != DaemonName.Length * sizeof(char)) return false;
        var offset = name.Buffer.ToInt64() - buffer.ToInt64();
        return offset >= 0 && offset <= length - name.Length &&
            string.Equals(Marshal.PtrToStringUni(name.Buffer, DaemonName.Length), DaemonName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ConnectionDecision(bool Trusted);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // SYSTEM_PROCESS_INFORMATION prefix through SessionId. The Windows kernel
    // supplies process start times even when the process cannot be opened.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfoPrefix
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreatedAt;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nuint ProcessId;
        public nuint ParentProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint access);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel,
        out ServiceStatusProcess status, uint bufferSize, out uint needed);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int used);
}
