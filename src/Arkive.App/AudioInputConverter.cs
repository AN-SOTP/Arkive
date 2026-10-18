using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Arkive.App;

/// <summary>
/// Reads a user's audio file (.wav, .mp3, .flac, .m4a/.aac and .wma through Windows Media Foundation; .ogg through
/// NVorbis) and converts it to 16-bit samples with the channel count and sample rate a game clip uses.
/// </summary>
internal static class AudioInputConverter
{
    public static short[] Decode(string path, int sampleRate, int channels)
    {
        using var reader = Open(path);
        var provider = MatchChannels(reader.ToSampleProvider(), channels);
        if (provider.WaveFormat.SampleRate != sampleRate)
            provider = new WdlResamplingSampleProvider(provider, sampleRate);

        var estimate = reader.TotalTime > TimeSpan.Zero
            ? (int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * sampleRate * channels + 4096)
            : sampleRate * channels * 10;
        var samples = new List<short>(estimate);
        var buffer = new float[sampleRate * channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
                samples.Add((short)Math.Round(Math.Clamp(buffer[index], -1f, 1f) * short.MaxValue));
            if (samples.Count > 30 * 60 * 48000 * 2)
                throw new InvalidDataException("The audio is longer than 30 minutes.");
        }
        return [.. samples];
    }

    private static WaveStream Open(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".ogg") return new VorbisWaveReader(path);
        if (extension == ".wav")
        {
            var wave = new WaveFileReader(path);
            if (wave.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible)
                return wave;
            // Compressed .wav variants (ADPCM, mu-law...) go through Media Foundation instead.
            wave.Dispose();
        }
        return new MediaFoundationReader(path);
    }

    private static ISampleProvider MatchChannels(ISampleProvider source, int channels)
    {
        var have = source.WaveFormat.Channels;
        if (have == channels) return source;
        if (have == 1 && channels == 2) return new MonoToStereoSampleProvider(source);
        if (have == 2 && channels == 1) return new StereoToMonoSampleProvider(source);
        return new ChannelMixer(source, channels);
    }

    /// <summary>Any other channel change: average down to mono, then spread to every output channel.</summary>
    private sealed class ChannelMixer(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _input = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var have = source.WaveFormat.Channels;
            var frames = count / channels;
            if (_input.Length < frames * have) _input = new float[frames * have];
            var read = source.Read(_input, 0, frames * have) / have;
            for (var frame = 0; frame < read; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < have; channel++) sum += _input[frame * have + channel];
                var value = sum / have;
                for (var channel = 0; channel < channels; channel++) buffer[offset + frame * channels + channel] = value;
            }
            return read * channels;
        }
    }
}
