using LanSwitch.Agent.Infrastructure;
using LanSwitch.Agent.Services;
using NAudio.Wave;

namespace LanSwitch.Agent.Tests;

public sealed class AudioRelayTests
{
    [Fact]
    public void FloatStereoFormatRoundTripsThroughWireDescriptor()
    {
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        var descriptor = AudioStreamProtocol.FromWaveFormat(source);
        var restored = AudioStreamProtocol.ToWaveFormat(descriptor);

        Assert.Equal("float", descriptor.Encoding);
        Assert.Equal(48000, restored.SampleRate);
        Assert.Equal(32, restored.BitsPerSample);
        Assert.Equal(2, restored.Channels);
        Assert.Equal(WaveFormatEncoding.IeeeFloat, restored.Encoding);
    }

    [Fact]
    public void InvalidOrOversizedFormatIsRejectedBeforePlaybackAllocation()
    {
        var invalid = new AudioStreamFormat(1, "pcm", 384000, 16, 2, 4, 1536000);

        Assert.Throws<InvalidDataException>(() => AudioStreamProtocol.ToWaveFormat(invalid));
    }

    [Fact]
    public void AudioStreamAuthorizationRequiresExactCommittedInputSession()
    {
        var input = new InputCoordinator();

        Assert.True(input.PrepareIncoming(42, "controller"));
        Assert.False(input.IsIncomingCommitted(42, "controller"));
        Assert.True(input.CommitIncoming(42, "controller"));
        Assert.True(input.IsIncomingCommitted(42, "controller"));
        Assert.False(input.IsIncomingCommitted(41, "controller"));
        Assert.False(input.IsIncomingCommitted(42, "other"));

        input.RevokeIncoming("controller", 42);
        Assert.False(input.IsIncomingCommitted(42, "controller"));
    }

    [Fact]
    public void AudioSettingsDefaultDisabledAndClampPersistedVolume()
    {
        var defaults = AgentSettings.CreateDefault("local");
        Assert.False(defaults.AudioForwardingEnabled);
        Assert.Equal(100, defaults.AudioVolume);

        Assert.Equal(100, AudioConfiguration.Normalize(defaults with { AudioVolume = 999 }).AudioVolume);
        Assert.Equal(0, AudioConfiguration.Normalize(defaults with { AudioVolume = -5 }).AudioVolume);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioConfiguration.ValidateVolume(101));
    }
}
