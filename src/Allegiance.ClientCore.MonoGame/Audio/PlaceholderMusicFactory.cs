namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P304 — generates the placeholder background-music beds as raw 16-bit mono
/// PCM byte buffers, following this client's established "generated, not
/// loaded" convention (<see cref="Allegiance.ClientCore.MonoGame.Rendering.RockTextureFactory"/>,
/// <c>WarpMeshFactory</c>): there are deliberately no real music assets in the
/// port's corpus yet (a future AI-generation pass is planned separately — see
/// the <c>allegiance-music-system</c> wiki page), so the two tracks are
/// synthesized at runtime so the real toggle/volume/switch plumbing can be
/// exercised and tested NOW.
/// </summary>
/// <remarks>
/// <para><b>Honest placeholder.</b> This is explicitly a stand-in, never passed
/// off as real music: the docs above, the <see cref="Music"/> wrapper's asset
/// constants, and this type's own remarks all say so. The two beds are simple,
/// distinct, clearly-generative tones (a low flight hum vs a higher, warmer
/// station pad) so dock/undock is audibly and measurably distinguishable, and
/// so a real device can be shown switching between them.</para>
/// <para><b>Determinism is the contract</b> (same discipline as
/// <see cref="Allegiance.ClientCore.MonoGame.Rendering.RockTextureFactory"/>):
/// fixed parameters, no <c>System.Random</c>, no time — two calls return
/// byte-identical buffers (unit-tested), and the two tracks are guaranteed to
/// differ.</para>
/// <para><b>Seamless loop.</b> Every frequency's cycle count over the buffer
/// length is an integer (chosen so), and the amplitude envelope is also
/// integer-cyclic over the loop, so <c>IsLooped=true</c> playback crosses the
/// buffer seam without a click.</para>
/// <para><b>Replaced, not removed, when real tracks arrive.</b> Dropping real
/// <c>.ogg</c> assets at the paths in <see cref="Music.FlightAsset"/> /
/// <see cref="Music.StationAsset"/> (and registering them in Content.mgcb)
/// makes <c>Content.Load</c> succeed first, so these generators are simply
/// never called — no code change required (see <see cref="Music"/>).</para>
/// </remarks>
public static class PlaceholderMusicFactory
{
    /// <summary>Sample rate for the generated beds. Plenty for a simple tone
    /// loop while keeping the buffer tiny.</summary>
    public const int SampleRate = 22050;

    /// <summary>Buffer length in seconds. Short (4 s) so the loop cycles are
    /// audible and the buffer stays small; integer-cycle (see remarks).</summary>
    public const double Seconds = 4.0;

    /// <summary>Total PCM buffer length in bytes (16-bit mono × samples).</summary>
    public static int BufferBytes => SampleRate * (int)Seconds * 2;

    // Frequencies chosen (and commented) so base×Seconds is an integer → the
    // loop crosses its seam cleanly. Flight sits in the low A3/G3 region (a
    // sub-audible-to-there rumble a fighter's engine hum evokes); Station sits
    // a fourth/triad higher (262/330) so the docked pad reads warmer/brighter.
    private const double FlightBaseHz = 196.0;    // G3 — ×4 = 784 (integer)
    private const double FlightAltHz = 220.0;     // A3 — ×4 = 880 (integer)
    private const double StationBaseHz = 262.0;   // C4 — ×4 = 1048 (integer)
    private const double StationAltHz = 330.0;    // E4 — ×4 = 1320 (integer)

    // Slow shared tremolo (amplitude wobble) at 0.5 Hz → 2 full cycles over
    // the 4 s buffer, so it too loops seamlessly. Distinct per-bed so the two
    // tracks differ in rhythm as well as pitch.
    private const double FlightTremoloHz = 0.5;
    private const double StationTremoloHz = 0.75;   // 3 cycles over the buffer (integer)

    /// <summary>The flight bed: a low two-tone hum (G3↔A3) with a slow swell.
    /// Plays while the local ship is free-flying (music enabled, not docked).</summary>
    public static byte[] Flight() =>
        Generate(FlightBaseHz, FlightAltHz, FlightTremoloHz, altMix: 0.35f, amplitude: 0.085f);

    /// <summary>The station bed: a higher, warmer two-tone pad (C4↔E4) with a
    /// quicker swell. Plays while the local ship is docked (music enabled).</summary>
    public static byte[] Station() =>
        Generate(StationBaseHz, StationAltHz, StationTremoloHz, altMix: 0.45f, amplitude: 0.075f);

    /// <summary>
    /// Synthesise one bed as a 16-bit little-endian mono PCM buffer of
    /// <see cref="Seconds"/> seconds. The wave is a two-tone mix (dominant
    /// tone + a quieter alternative a step up), shaped by a slow tremolo
    /// envelope. Quiet on purpose — it is a background bed, and deliberately
    /// lower than the game's live SFX so real tracks that eventually replace
    /// it can sit at the same sane default.
    /// </summary>
    private static byte[] Generate(double baseHz, double altHz, double tremoloHz, float altMix, float amplitude)
    {
        int n = SampleRate * (int)Seconds;
        var buffer = new byte[n * 2];

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate;

            double tremolo = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * tremoloHz * t);   // 0..1 slow swell
            double wave =
                Math.Sin(2.0 * Math.PI * baseHz * t) * (1.0 - altMix) +
                Math.Sin(2.0 * Math.PI * altHz * t) * altMix;
            double sample = wave * amplitude * (0.7 + 0.3 * tremolo);

            short v = (short)(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue);
            buffer[i * 2] = (byte)(v & 0xFF);
            buffer[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        return buffer;
    }
}
