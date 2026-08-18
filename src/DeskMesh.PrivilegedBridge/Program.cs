using System.Security.Principal;

namespace DeskMesh.PrivilegedBridge;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var command = BridgeCommandLine.Parse(args);
            return command.Mode switch
            {
                BridgeMode.Service => PrivilegedWindowsService.Run(command),
                BridgeMode.SessionHelper => PrivilegedSessionHelper.Run(command),
                BridgeMode.Install => PrivilegedBridgeInstaller.Install(command),
                BridgeMode.Uninstall => PrivilegedBridgeInstaller.Uninstall(command),
                _ => throw new InvalidOperationException("A privileged bridge mode is required.")
            };
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine($"DeskMesh privileged bridge failed: {exception.Message}"); } catch { }
            return 1;
        }
    }

    internal static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
