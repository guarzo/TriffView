using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TriffView.Audio;

/// <summary>
/// A stored wormhole-splash reference clip, reduced to a comparable feature patch. Pure BCL
/// (Text.Json, Buffers.Binary) so it links into the cross-platform test project like
/// <see cref="SplashFeatures"/>.
/// </summary>
public sealed class SplashTemplate
{
    public required string Id { get; init; }      // filename stem, e.g. "splash-01"
    public required string Name { get; init; }
    public required bool BuiltIn { get; init; }
    public required float[] Patch { get; init; }   // normalised, BandCount*WindowFrames

    /// <summary>
    /// Builds a template from a template WAV plus its sidecar stats JSON. Returns null (never
    /// throws) for any malformed or unsupported input: wrong format version, non-16kHz-mono-16bit
    /// audio, malformed RIFF/JSON, missing/zero gain, or median/mad arrays of the wrong length.
    /// </summary>
    public static SplashTemplate? FromWav(string id, string name, bool builtIn,
        ReadOnlySpan<byte> wav, ReadOnlySpan<byte> statsJson)
    {
        Stats stats;
        try
        {
            // JsonDocument has no ReadOnlySpan<byte> overload (it retains the buffer beyond this
            // call), so copy to an array first.
            var doc = JsonDocument.Parse(statsJson.ToArray());
            var root = doc.RootElement;

            if (!root.TryGetProperty("version", out var versionEl) || versionEl.GetInt32() != 1)
                return null;

            if (!root.TryGetProperty("gain", out var gainEl))
                return null;
            var gain = gainEl.GetDouble();
            if (gain <= 0.0)
                return null;

            if (!root.TryGetProperty("median", out var medianEl) || medianEl.ValueKind != JsonValueKind.Array)
                return null;
            if (!root.TryGetProperty("mad", out var madEl) || madEl.ValueKind != JsonValueKind.Array)
                return null;

            var median = ReadFloatArray(medianEl);
            var mad = ReadFloatArray(madEl);
            if (median.Length != SplashFeatures.BandCount || mad.Length != SplashFeatures.BandCount)
                return null;

            stats = new Stats(gain, median, mad);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Thrown by GetInt32/GetDouble when the JSON element isn't the expected kind.
            return null;
        }

        float[] samples;
        try
        {
            samples = ReadWavMono16(wav);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException)
        {
            return null;
        }

        // Clips are peak-normalised on write so quiet ones survive 16-bit quantisation; undo
        // that here before computing bands, or class separation collapses entirely.
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(samples[i] / stats.Gain);

        var bands = SplashFeatures.ComputeBands(samples);
        var frameCount = bands.GetLength(1);
        var patch = SplashFeatures.BuildPatch(bands, 0, stats.Median, stats.Mad);

        return new SplashTemplate
        {
            Id = id,
            Name = name,
            BuiltIn = builtIn,
            Patch = patch,
        };
    }

    /// <summary>
    /// Writes a 16 kHz mono 16-bit PCM WAV. Samples are peak-normalised before quantisation so
    /// quiet clips don't collapse into quantisation noise; the applied factor is returned as
    /// <paramref name="gain"/> and must be persisted alongside the file for <see cref="FromWav"/>
    /// to undo it.
    /// </summary>
    public static byte[] WriteWav(ReadOnlySpan<float> samples, out double gain)
    {
        double peak = 0.0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs(s));

        gain = peak > 1e-9 ? 1.0 / peak : 1.0;

        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = samples[i] * gain;
            var clamped = Math.Clamp(scaled, -1.0, 1.0);
            var q = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), q);
        }

        return BuildWav(pcm, sampleRate: SplashFeatures.SampleRate, channels: 1, bitsPerSample: 16);
    }

    public static byte[] WriteStatsJson(double gain, float[] median, float[] mad)
    {
        var payload = new
        {
            version = 1,
            sampleRate = SplashFeatures.SampleRate,
            gain,
            median,
            mad,
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    /// <summary>
    /// Decodes a 16 kHz mono 16-bit PCM WAV into -1..1 samples. Walks the RIFF chunk list
    /// rather than assuming a fixed layout, since a `fact` chunk (or others) may sit between
    /// `fmt ` and `data`. Throws on malformed input; callers that must not throw should catch.
    /// </summary>
    public static float[] ReadWavMono16(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12)
            throw new FormatException("WAV too short.");
        if (wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
            throw new FormatException("Missing RIFF header.");
        if (wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            throw new FormatException("Missing WAVE tag.");

        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        var foundFmt = false;
        var foundData = false;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = wav.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            var bodyStart = offset + 8;
            if (bodyStart + chunkSize > wav.Length)
                throw new FormatException("Chunk overruns buffer.");

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                    throw new FormatException("fmt chunk too short.");
                var body = wav.Slice(bodyStart, (int)chunkSize);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14, 2));
                foundFmt = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = wav.Slice(bodyStart, (int)chunkSize);
                foundData = true;
            }

            // Chunks are word-aligned: a chunk of odd size is followed by a pad byte.
            var advance = (int)chunkSize + (chunkSize % 2 == 1 ? 1 : 0);
            offset = bodyStart + advance;
        }

        if (!foundFmt || !foundData)
            throw new FormatException("Missing fmt or data chunk.");
        if (channels != 1 || sampleRate != SplashFeatures.SampleRate || bitsPerSample != 16)
            throw new FormatException("Unsupported WAV format; expected 16kHz mono 16-bit PCM.");

        var sampleCount = data.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var q = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
            samples[i] = q / (float)short.MaxValue;
        }

        return samples;
    }

    private static float[] ReadFloatArray(JsonElement arrayEl)
    {
        var result = new float[arrayEl.GetArrayLength()];
        var i = 0;
        foreach (var el in arrayEl.EnumerateArray())
            result[i++] = (float)el.GetDouble();
        return result;
    }

    private static byte[] BuildWav(byte[] pcm, int sampleRate, ushort channels, ushort bitsPerSample)
    {
        var blockAlign = (ushort)(channels * (bitsPerSample / 8));
        var byteRate = (uint)(sampleRate * blockAlign);
        var dataSize = (uint)pcm.Length;
        var riffSize = 4 + (8 + 16) + (8 + dataSize);

        var buffer = new byte[8 + riffSize];
        var span = buffer.AsSpan();

        Encoding.ASCII.GetBytes("RIFF", span.Slice(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), riffSize);
        Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));

        Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(34, 2), bitsPerSample);

        Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40, 4), dataSize);
        pcm.CopyTo(span.Slice(44));

        return buffer;
    }

    private readonly record struct Stats(double Gain, float[] Median, float[] Mad);
}
