using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using LanSwitch.Core.Privileged;

namespace DeskMesh.PrivilegedBridge;

internal sealed class PrivilegedWindowsService : IDisposable
{
    private readonly BridgeCommandLine _command;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<uint, SessionBridge> _sessionBridges = new();
    private readonly SemaphoreSlim _clientSlots = new(4, 4);
    private readonly BridgeNativeMethods.ServiceMainDelegate _serviceMain;
    private readonly BridgeNativeMethods.ServiceControlHandlerDelegate _controlHandler;
    private nint _statusHandle;
    private Task? _serverTask;
    private int _disposed;

    private PrivilegedWindowsService(BridgeCommandLine command)
    {
        _command = command;
        _serviceMain = ServiceMain;
        _controlHandler = HandleControl;
    }

    internal static int Run(BridgeCommandLine command)
    {
        if (!Program.IsLocalSystem()) throw new UnauthorizedAccessException("The privileged bridge service must run as LocalSystem.");
        using var service = new PrivilegedWindowsService(command);
        var entries = new[]
        {
            new BridgeNativeMethods.ServiceTableEntry
            {
                ServiceName = PrivilegedBridgeContract.GetServiceName(command.OwnerSid!, command.InstanceName!),
                ServiceMain = service._serviceMain
            },
            new BridgeNativeMethods.ServiceTableEntry()
        };
        if (!BridgeNativeMethods.StartServiceCtrlDispatcher(entries))
            throw BridgeNativeMethods.CreateError("StartServiceCtrlDispatcherW");
        return 0;
    }

    private void ServiceMain(uint argumentCount, nint arguments)
    {
        _ = argumentCount;
        _ = arguments;
        var serviceName = PrivilegedBridgeContract.GetServiceName(_command.OwnerSid!, _command.InstanceName!);
        _statusHandle = BridgeNativeMethods.RegisterServiceCtrlHandlerEx(serviceName, _controlHandler, 0);
        if (_statusHandle == 0) return;

        ReportStatus(BridgeNativeMethods.ServiceStartPending, controlsAccepted: 0, waitHint: 5000);
        try
        {
            _serverTask = RunServerAsync(_stopping.Token);
            ReportStatus(BridgeNativeMethods.ServiceRunning, BridgeNativeMethods.ServiceAcceptStop, waitHint: 0);
            _serverTask.GetAwaiter().GetResult();
            ReportStatus(BridgeNativeMethods.ServiceStopped, controlsAccepted: 0, waitHint: 0);
        }
        catch
        {
            ReportStatus(BridgeNativeMethods.ServiceStopped, controlsAccepted: 0, waitHint: 0, exitCode: 1);
        }
    }

    private uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        _ = eventType;
        _ = eventData;
        _ = context;
        if (control != BridgeNativeMethods.ServiceControlStop) return 0;
        ReportStatus(BridgeNativeMethods.ServiceStopPending, controlsAccepted: 0, waitHint: 5000);
        _stopping.Cancel();
        return 0;
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        var tasks = new ConcurrentBag<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            await _clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateAgentPipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connectedPipe = pipe;
                pipe = null;
                var task = HandleClientAsync(connectedPipe, cancellationToken)
                    .ContinueWith(
                        _ => _clientSlots.Release(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                tasks.Add(task);
            }
            catch
            {
                pipe?.Dispose();
                _clientSlots.Release();
                if (cancellationToken.IsCancellationRequested) break;
                throw;
            }
        }

        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private NamedPipeServerStream CreateAgentPipe()
    {
        var owner = new SecurityIdentifier(_command.OwnerSid!);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            PrivilegedBridgeContract.GetPipeName(_command.OwnerSid!, _command.InstanceName!),
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            16 * 1024,
            16 * 1024,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serviceToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            string? clientSid = null;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                clientSid = identity?.User?.Value;
            });
            if (!string.Equals(clientSid, _command.OwnerSid, StringComparison.OrdinalIgnoreCase)) return;

            if (!BridgeNativeMethods.GetNamedPipeClientProcessId(
                    pipe.SafePipeHandle.DangerousGetHandle(),
                    out var clientProcessId) ||
                !BridgeNativeMethods.ProcessIdToSessionId(clientProcessId, out var sessionId))
                return;

            while (pipe.IsConnected && !serviceToken.IsCancellationRequested)
            {
                PrivilegedBridgeRequest request;
                try { request = await PrivilegedBridgeContract.ReadAsync<PrivilegedBridgeRequest>(pipe, serviceToken); }
                catch (EndOfStreamException) { return; }
                catch (IOException) { return; }

                var response = await HandleRequestAsync(sessionId, request, serviceToken).ConfigureAwait(false);
                await PrivilegedBridgeContract.WriteAsync(pipe, response, serviceToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PrivilegedBridgeResponse> HandleRequestAsync(
        uint sessionId,
        PrivilegedBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Version != PrivilegedBridgeContract.Version)
            return Failure("The privileged bridge protocol version is not supported.");
        if (request.Operation == PrivilegedBridgeContract.StatusOperation)
            return new PrivilegedBridgeResponse(
                PrivilegedBridgeContract.Version,
                Available: true,
                SecureDesktopActive: false,
                Attempted: 0,
                Succeeded: 0);
        if (request.Operation is not (PrivilegedBridgeContract.InjectOperation or PrivilegedBridgeContract.ReleaseOperation))
            return Failure("The privileged bridge operation is not allowed.");
        if (request.Operation == PrivilegedBridgeContract.InjectOperation &&
            (request.Events is null || request.Events.Count is <= 0 or > PrivilegedBridgeContract.MaximumEventsPerBatch))
            return Failure("The privileged input batch is invalid.");

        try
        {
            var bridge = _sessionBridges.GetOrAdd(sessionId, id => new SessionBridge(id));
            return await bridge.ForwardAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_sessionBridges.TryRemove(sessionId, out var failed)) failed.Dispose();
            return Failure(exception.Message);
        }
    }

    private static PrivilegedBridgeResponse Failure(string error) => new(
        PrivilegedBridgeContract.Version,
        Available: false,
        SecureDesktopActive: false,
        Attempted: 0,
        Succeeded: 0,
        Error: error);

    private void ReportStatus(uint state, uint controlsAccepted, uint waitHint, uint exitCode = 0)
    {
        if (_statusHandle == 0) return;
        var status = new BridgeNativeMethods.ServiceStatus
        {
            ServiceType = BridgeNativeMethods.ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = controlsAccepted,
            Win32ExitCode = exitCode,
            WaitHint = waitHint
        };
        _ = BridgeNativeMethods.SetServiceStatus(_statusHandle, ref status);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        foreach (var bridge in _sessionBridges.Values) bridge.Dispose();
        _sessionBridges.Clear();
        _clientSlots.Dispose();
        _stopping.Dispose();
    }

    private sealed class SessionBridge(uint sessionId) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private NamedPipeServerStream? _pipe;
        private nint _processHandle;
        private int _disposed;

        internal async Task<PrivilegedBridgeResponse> ForwardAsync(
            PrivilegedBridgeRequest request,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await PrivilegedBridgeContract.WriteAsync(_pipe!, request, cancellationToken).ConfigureAwait(false);
                return await PrivilegedBridgeContract.ReadAsync<PrivilegedBridgeResponse>(_pipe!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                Reset();
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_pipe?.IsConnected == true && IsHelperAlive()) return;
            Reset();
            var pipeName = $"DeskMesh.PrivilegedHelper.{sessionId}.{Guid.NewGuid():N}";
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            _pipe = NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                16 * 1024,
                16 * 1024,
                security);
            _processHandle = LaunchSessionHelper(sessionId, pipeName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }

        private bool IsHelperAlive() =>
            _processHandle != 0 &&
            BridgeNativeMethods.GetExitCodeProcess(_processHandle, out var exitCode) &&
            exitCode == BridgeNativeMethods.StillActive;

        private static nint LaunchSessionHelper(uint targetSessionId, string pipeName)
        {
            if (!BridgeNativeMethods.OpenProcessToken(
                    BridgeNativeMethods.GetCurrentProcess(),
                    BridgeNativeMethods.TokenAssignPrimary |
                    BridgeNativeMethods.TokenDuplicate |
                    BridgeNativeMethods.TokenQuery |
                    BridgeNativeMethods.TokenAdjustSessionId,
                    out var serviceToken))
                throw BridgeNativeMethods.CreateError("OpenProcessToken");
            try
            {
                if (!BridgeNativeMethods.DuplicateTokenEx(
                        serviceToken,
                        BridgeNativeMethods.MaximumAllowed,
                        0,
                        BridgeNativeMethods.SecurityImpersonation,
                        BridgeNativeMethods.TokenPrimary,
                        out var sessionToken))
                    throw BridgeNativeMethods.CreateError("DuplicateTokenEx");
                try
                {
                    var mutableSessionId = targetSessionId;
                    if (!BridgeNativeMethods.SetTokenSessionId(
                            sessionToken,
                            BridgeNativeMethods.TokenSessionId,
                            ref mutableSessionId,
                            sizeof(uint)))
                        throw BridgeNativeMethods.CreateError("SetTokenInformation(TokenSessionId)");

                    var executablePath = Environment.ProcessPath
                                         ?? throw new InvalidOperationException("The bridge executable path is unavailable.");
                    var commandLine = $"\"{executablePath}\" --session-helper --pipe \"{pipeName}\"";
                    var startup = new BridgeNativeMethods.StartupInfo
                    {
                        Size = (uint)Marshal.SizeOf<BridgeNativeMethods.StartupInfo>(),
                        Desktop = @"winsta0\winlogon"
                    };
                    if (!BridgeNativeMethods.CreateProcessAsUser(
                            sessionToken,
                            executablePath,
                            commandLine,
                            0,
                            0,
                            false,
                            BridgeNativeMethods.CreateNoWindow,
                            0,
                            Path.GetDirectoryName(executablePath),
                            ref startup,
                            out var process))
                        throw BridgeNativeMethods.CreateError("CreateProcessAsUserW");
                    BridgeNativeMethods.CloseHandle(process.Thread);
                    return process.Process;
                }
                finally
                {
                    BridgeNativeMethods.CloseHandle(sessionToken);
                }
            }
            finally
            {
                BridgeNativeMethods.CloseHandle(serviceToken);
            }
        }

        private void Reset()
        {
            _pipe?.Dispose();
            _pipe = null;
            if (_processHandle == 0) return;
            if (IsHelperAlive()) _ = BridgeNativeMethods.TerminateProcess(_processHandle, 0);
            BridgeNativeMethods.CloseHandle(_processHandle);
            _processHandle = 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _gate.Wait();
            try { Reset(); }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
