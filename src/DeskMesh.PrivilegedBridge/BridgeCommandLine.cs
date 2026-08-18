using System.Security.Principal;

namespace DeskMesh.PrivilegedBridge;

internal enum BridgeMode
{
    None,
    Service,
    SessionHelper,
    Install,
    Uninstall
}

internal sealed record BridgeCommandLine(
    BridgeMode Mode,
    string? OwnerSid,
    string? InstanceName,
    string? PipeName)
{
    internal static BridgeCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var mode = BridgeMode.None;
        string? ownerSid = null;
        string? instanceName = null;
        string? pipeName = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--service": mode = SetMode(mode, BridgeMode.Service); break;
                case "--session-helper": mode = SetMode(mode, BridgeMode.SessionHelper); break;
                case "--install": mode = SetMode(mode, BridgeMode.Install); break;
                case "--uninstall": mode = SetMode(mode, BridgeMode.Uninstall); break;
                case "--owner-sid": ownerSid = ReadValue(args, ref index, "--owner-sid"); break;
                case "--instance": instanceName = ReadValue(args, ref index, "--instance"); break;
                case "--pipe": pipeName = ReadValue(args, ref index, "--pipe"); break;
                default: throw new ArgumentException($"Unknown privileged bridge argument: {args[index]}");
            }
        }

        if (mode == BridgeMode.None) throw new ArgumentException("A privileged bridge mode is required.");
        if (mode == BridgeMode.SessionHelper)
        {
            if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Session helper pipe is required.");
            return new BridgeCommandLine(mode, null, null, ValidatePipeName(pipeName));
        }

        if (string.IsNullOrWhiteSpace(ownerSid) || !IsValidSid(ownerSid))
            throw new ArgumentException("A valid owner SID is required.");
        if (!IsValidInstanceName(instanceName)) throw new ArgumentException("The instance name is invalid.");
        return new BridgeCommandLine(mode, ownerSid, instanceName, null);
    }

    private static BridgeMode SetMode(BridgeMode current, BridgeMode next) =>
        current == BridgeMode.None ? next : throw new ArgumentException("Only one privileged bridge mode may be selected.");

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"{name} requires a value.");
        return args[index];
    }

    private static bool IsValidInstanceName(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsValidSid(string value)
    {
        try
        {
            _ = new SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ValidatePipeName(string value)
    {
        if (value.Length is < 16 or > 180 || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("The helper pipe name is invalid.");
        return value;
    }
}
