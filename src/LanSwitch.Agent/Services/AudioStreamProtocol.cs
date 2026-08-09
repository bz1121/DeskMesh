using System.Text.Json;
using NAudio.Wave;

namespace LanSwitch.Agent.Services;

internal static class AudioStreamProtocol
{
    internal const int Version = 1;
    internal const int MaximumHeaderBytes = 4096;
    internal const int MaximumFrameBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static AudioStreamFormat FromWaveFormat(WaveFormat source)
    {
        var format = source is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : source;
        var encoding = format.Encoding switch
        {
            WaveFormatEncoding.Pcm => "pcm",
            WaveFormatEncoding.IeeeFloat => "float",
            _ => throw new NotSupportedException($"不支持的远端音频格式：{format.Encoding}。")
        };
        return Validate(new AudioStreamFormat(
            Version,
            encoding,
            format.SampleRate,
            format.BitsPerSample,
            format.Channels,
            format.BlockAlign,
            format.AverageBytesPerSecond));
    }

    internal static WaveFormat ToWaveFormat(AudioStreamFormat value)
    {
        var format = Validate(value);
        return format.Encoding switch
        {
            "float" => WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels),
            "pcm" => new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels),
            _ => throw new InvalidDataException("远端返回了未知音频编码。")
        };
    }

    internal static AudioStreamFormat Validate(AudioStreamFormat value)
    {
        if (value.Version != Version) throw new InvalidDataException("远端音频协议版本不兼容。");
        if (value.Encoding is not ("pcm" or "float")) throw new InvalidDataException("远端音频编码无效。");
        if (value.SampleRate is < 8000 or > 192000) throw new InvalidDataException("远端音频采样率超出限制。");
        if (value.Channels is < 1 or > 8) throw new InvalidDataException("远端音频声道数超出限制。");
        if (value.BitsPerSample is not (8 or 16 or 24 or 32)) throw new InvalidDataException("远端音频位深无效。");
        if (value.Encoding == "float" && value.BitsPerSample != 32) throw new InvalidDataException("浮点音频必须为 32 位。");
        var expectedBlockAlign = checked(value.Channels * value.BitsPerSample / 8);
        var expectedBytesPerSecond = checked(value.SampleRate * expectedBlockAlign);
        if (value.BlockAlign != expectedBlockAlign || value.AverageBytesPerSecond != expectedBytesPerSecond)
            throw new InvalidDataException("远端音频格式的帧大小不一致。");
        return value;
    }
}

internal sealed record AudioStreamFormat(
    int Version,
    string Encoding,
    int SampleRate,
    int BitsPerSample,
    int Channels,
    int BlockAlign,
    int AverageBytesPerSecond);
