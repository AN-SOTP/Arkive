using System.Buffers.Binary;

namespace Arkive.Core.Audio;

/// <summary>What a .wem file's RIFF header says about its audio.</summary>
public sealed record WemFormat(ushort FormatTag, int Channels, int SampleRate, long SampleCount)
{
    public const ushort WwiseVorbis = 0xFFFF;
    public const ushort Extensible = 0xFFFE;
    public const ushort Pcm = 0x0001;

    public TimeSpan? Duration => SampleRate > 0 && SampleCount > 0
        ? TimeSpan.FromSeconds((double)SampleCount / SampleRate)
        : null;

    public string Codec => FormatTag switch
    {
        WwiseVorbis => "Vorbis",
        Extensible or Pcm => "PCM",
        0x0002 => "ADPCM",
        0x3040 or 0x3041 => "Opus",
        _ => $"0x{FormatTag:X4}",
    };

    public bool IsPcm => FormatTag is Extensible or Pcm;
}

/// <summary>Reads Wwise .wem headers and writes the uncompressed (PCM) .wem files Arkive uses as replacements.</summary>
public static class WemAudio
{
    /// <summary>Enough of a .wem file's start to read its format.</summary>
    public const int HeaderProbeLength = 512;

    /// <summary>16-bit interleaved samples.</summary>
    public const int BitsPerSample = 16;

    public static WemFormat? ReadFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            return null;
        int? fmt = null;
        long? dataSize = null;
        var at = 12;
        while (at + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (data.Slice(at, 4).SequenceEqual("fmt "u8)) fmt = at + 8;
            else if (data.Slice(at, 4).SequenceEqual("data"u8)) dataSize = size;
            if (size > int.MaxValue) break;
            at += 8 + (int)size + (int)(size & 1);
        }
        if (fmt is not { } f || f + 16 > data.Length) return null;
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(data[f..]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(f + 2)..]);
        var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(f + 4)..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[(f + 12)..]);
        long samples = 0;
        if (tag == WemFormat.WwiseVorbis && f + 0x1C <= data.Length)
            samples = BinaryPrimitives.ReadUInt32LittleEndian(data[(f + 0x18)..]);
        else if (tag is WemFormat.Extensible or WemFormat.Pcm && blockAlign > 0 && dataSize is { } bytes)
            samples = bytes / blockAlign;
        return new WemFormat(tag, channels, rate, samples);
    }

    /// <summary>
    /// A Wwise PCM .wem holding <paramref name="samples"/> (16-bit, interleaved). The layout matches what Wwise itself
    /// writes for PCM: a 24-byte extensible format chunk with the channel mask, then the samples.
    /// </summary>
    public static byte[] WritePcmWem(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var dataBytes = samples.Length * 2;
        var pad = dataBytes & 1;
        var bytes = new byte[12 + 8 + 24 + 8 + dataBytes + pad];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(bytes.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 24);
        var fmt = span[20..];
        var blockAlign = channels * 2;
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, WemFormat.Extensible);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[2..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[8..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[12..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[14..], BitsPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[16..], 6);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[18..], BitsPerSample);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[20..], ChannelMask(channels));
        "data"u8.CopyTo(span[44..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], (uint)dataBytes);
        var data = span.Slice(52, dataBytes);
        for (var index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(data[(index * 2)..], samples[index]);
        return bytes;
    }

    /// <summary>A standard .wav (playable anywhere) from a PCM .wem.</summary>
    public static byte[] PcmWemToWav(ReadOnlySpan<byte> wem)
    {
        var format = ReadFormat(wem) ?? throw new InvalidDataException("The audio is not a RIFF file.");
        if (!format.IsPcm) throw new InvalidDataException($"The audio is {format.Codec}, not PCM.");
        var samples = ReadPcmData(wem);
        var bytes = new byte[44 + samples.Length];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(bytes.Length - 8));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], WemFormat.Pcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(format.SampleRate * format.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(format.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)samples.Length);
        samples.CopyTo(span[44..]);
        return bytes;
    }

    private static ReadOnlySpan<byte> ReadPcmData(ReadOnlySpan<byte> wem)
    {
        var at = 12;
        while (at + 8 <= wem.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wem[(at + 4)..]);
            if (wem.Slice(at, 4).SequenceEqual("data"u8))
                return wem.Slice(at + 8, Math.Min(size, wem.Length - at - 8));
            at += 8 + size + (size & 1);
        }
        throw new InvalidDataException("The audio has no data chunk.");
    }

    /// <summary>
    /// Fits <paramref name="samples"/> to exactly <paramref name="frames"/> frames: longer audio is cut with a short
    /// fade-out, shorter audio is padded with silence. Music keeps its original timing this way.
    /// </summary>
    public static short[] FitLength(ReadOnlySpan<short> samples, int channels, long frames, int sampleRate)
    {
        var target = checked((int)(frames * channels));
        var result = new short[target];
        var copied = Math.Min(samples.Length, target);
        samples[..copied].CopyTo(result);
        if (samples.Length > target)
        {
            var fadeFrames = Math.Min(frames, Math.Max(1, sampleRate / 20));
            for (var frame = 0L; frame < fadeFrames; frame++)
            {
                var gain = (double)frame / fadeFrames;
                var start = (int)((frames - 1 - frame) * channels);
                for (var channel = 0; channel < channels; channel++)
                    result[start + channel] = (short)(result[start + channel] * gain);
            }
        }
        return result;
    }

    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        4 => 0x33,
        6 => 0x3F,
        8 => 0x63F,
        _ => 0,
    };
}
