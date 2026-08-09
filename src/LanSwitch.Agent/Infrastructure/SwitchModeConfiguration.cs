namespace LanSwitch.Agent.Infrastructure;

public static class SwitchModeConfiguration
{
    public const string DirectSignal = "directSignal";
    public const string SeamlessRemote = "seamlessRemote";

    public static AgentSettings Normalize(AgentSettings settings) => settings with
    {
        SwitchMode = NormalizeValue(settings.SwitchMode)
    };

    public static string NormalizeValue(string? mode) => mode switch
    {
        DirectSignal => DirectSignal,
        SeamlessRemote => SeamlessRemote,
        _ => DirectSignal
    };

    public static string Validate(string? mode) => mode switch
    {
        DirectSignal => DirectSignal,
        SeamlessRemote => SeamlessRemote,
        _ => throw new ArgumentException(
            $"切换模式必须是 {DirectSignal} 或 {SeamlessRemote}。",
            nameof(mode))
    };

    public static bool IsSeamlessRemote(string? mode) =>
        string.Equals(mode, SeamlessRemote, StringComparison.Ordinal);
}

public sealed record SwitchModeSettingsView(string Mode);

public sealed record SwitchModeSettingsUpdateRequest(string Mode);
