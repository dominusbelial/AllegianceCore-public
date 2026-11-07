using System.Buffers.Binary;
using NVorbis;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>The artwork's training voices are Ogg Vorbis, whereas MonoGame's
/// SoundEffect.FromFile accepts RIFF/WAVE. Decode to the same signed 16-bit PCM
/// used by compiled SoundEffect content, using MonoGame's existing Vorbis dependency.</summary>
internal static class TrainingVoicePcm
{
    internal static SoundPcm Read(string path)
    {
        using var vorbis = new VorbisReader(path);
        if (vorbis.Channels is < 1 or > 2 || vorbis.SampleRate is < 8000 or > 48000)
            throw new InvalidDataException("Training voice must be mono/stereo at 8–48 kHz.");
        byte[] pcm = new byte[checked((int)(vorbis.TotalSamples * vorbis.Channels * 2))];
        float[] samples = new float[4096 * vorbis.Channels];
        int offset = 0, count;
        while ((count = vorbis.ReadSamples(samples, 0, samples.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                short value = (short)Math.Clamp((int)(samples[i] * 32768), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, 2), value);
                offset += 2;
            }
        }
        if (offset != pcm.Length) throw new InvalidDataException("Incomplete training voice.");
        return new SoundPcm(pcm, vorbis.SampleRate, vorbis.Channels);
    }
}
