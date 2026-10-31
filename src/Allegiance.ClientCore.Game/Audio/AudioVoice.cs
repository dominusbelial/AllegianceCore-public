using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Audio;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>A managed voice or an authored ASR voice. Both use the existing
/// MonoGame OpenAL context and the same SoundManager budget.</summary>
public sealed class AudioVoice : IDisposable
{
    private readonly SoundEffectInstance? _sound;
    private readonly IAsrAudioVoice? _asr;
    public AudioVoice(SoundEffectInstance sound) => _sound = sound;
    public AudioVoice(AsrSoundTemplate template) => _asr = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? new DynamicAsrVoice(template) : new OpenAlAsrVoice(template);
    public SoundState State => _sound?.State ?? _asr!.State;
    public bool IsLooped { get => _sound?.IsLooped ?? !_asr!.Releasing; set { if (_sound is { } s) s.IsLooped = value; } }
    public float Volume { get => _sound?.Volume ?? _asr!.Volume; set { if (_sound is { } s) s.Volume = value; else _asr!.Volume = value; } }
    public float Pitch { get => _sound?.Pitch ?? _asr!.Pitch; set { if (_sound is { } s) s.Pitch = value; else _asr!.Pitch = value; } }
    public float Pan { get => _sound?.Pan ?? _asr!.Pan; set { if (_sound is { } s) s.Pan = value; else _asr!.Pan = value; } }
    public void Play() { if (_sound is { } s) s.Play(); else _asr!.Play(); }
    public void Stop() { if (_sound is { } s) s.Stop(); else _asr!.Stop(true); }
    public void Release() { if (_sound is { } s) s.Stop(); else _asr!.Stop(false); }
    public void Pump() => _asr?.Pump();
    public void Dispose() { _sound?.Dispose(); _asr?.Dispose(); }
}

internal interface IAsrAudioVoice : IDisposable
{
    SoundState State { get; }
    bool Releasing { get; }
    float Volume { get; set; }
    float Pitch { get; set; }
    float Pan { get; set; }
    void Play();
    void Stop(bool immediate);
    void Pump();
}

// Mobile heads supply their own MonoGame framework, without an OpenAL context.
// Keep ASR playback on that framework's public streaming API on those heads.
internal sealed class DynamicAsrVoice : IAsrAudioVoice
{
    private readonly DynamicSoundEffectInstance _sound;
    private readonly AsrCursor _cursor;
    private readonly byte[] _buffer;
    private bool _started;
    public DynamicAsrVoice(AsrSoundTemplate template)
    {
        _cursor = new(template.Pcm, template.Region);
        _buffer = new byte[Math.Max(1, template.Pcm.SampleRate / 50) * template.Pcm.FrameBytes];
        _sound = new(template.Pcm.SampleRate, (AudioChannels)template.Pcm.Channels);
    }
    public bool Releasing { get; private set; }
    public SoundState State => _sound.State;
    public float Volume { get => _sound.Volume; set => _sound.Volume = value; }
    public float Pitch { get => _sound.Pitch; set => _sound.Pitch = value; }
    public float Pan { get => _sound.Pan; set => _sound.Pan = value; }
    public void Play() { Pump(); _sound.Play(); _started = true; }
    public void Stop(bool immediate)
    {
        Releasing = true; _cursor.Stop(immediate);
        if (immediate) _sound.Stop();
    }
    public void Pump()
    {
        while (_sound.PendingBufferCount < 3)
        {
            int bytes = _cursor.Read(_buffer);
            if (bytes == 0) break;
            _sound.SubmitBuffer(_buffer, 0, bytes);
        }
        if (_started && _cursor.Finished && _sound.PendingBufferCount == 0) _sound.Stop();
    }
    public void Dispose() => _sound.Dispose();
}

// MonoGame DesktopGL ignores loop points and Stop(false). Its dynamic audio
// creates an OALSoundBuffer on EVERY submission. Reuse three native buffers
// instead: no managed/native buffer allocations in the frame/refill path.
internal sealed class OpenAlAsrVoice : IAsrAudioVoice
{
    private readonly AsrCursor _cursor;
    private readonly SoundPcm _pcm;
    private readonly byte[] _scratch;
    private readonly uint[] _buffers = new uint[3];
    private uint _source;
    private DynamicSoundEffectInstance? _sourceLease;
    private bool _disposed, _playing;
    private float _volume = 1, _pitch, _pan;
    public bool Releasing { get; private set; }
    public SoundState State
    {
        get
        {
            if (!_playing || _disposed) return SoundState.Stopped;
            Al.Get(_source, Al.SourceState, out int state);
            return state == Al.Playing || !_cursor.Finished ? SoundState.Playing : SoundState.Stopped;
        }
    }
    public float Volume { get => _volume; set { _volume = value; Al.Float(_source, Al.Gain, value * SoundEffect.MasterVolume); } }
    public float Pitch { get => _pitch; set { _pitch = value; Al.Float(_source, Al.Pitch, MathF.Pow(2, value)); } }
    public float Pan
    {
        get => _pan;
        set { _pan = value; float angle = value * MathF.PI / 3; Al.Position(_source, 0x1004, MathF.Sin(angle), 0, -MathF.Cos(angle)); }
    }
    public OpenAlAsrVoice(AsrSoundTemplate template)
    {
        _pcm = template.Pcm; _cursor = new(_pcm, template.Region);
        // Three 20 ms chunks accommodate the 60 Hz game loop. Stop has at most
        // 60 ms of submitted audio, just like any buffered sound device.
        _scratch = new byte[Math.Max(1, _pcm.SampleRate / 50) * _pcm.FrameBytes];
        try
        {
            // MonoGame preallocates the device's source pool. Borrow through
            // its public dynamic voice, and return the source on disposal.
            // This one field accessor is tied to the pinned 3.8.5.1 backend;
            // it avoids reflection and per-refill OALSoundBuffer allocation.
            _sourceLease = new DynamicSoundEffectInstance(_pcm.SampleRate, (AudioChannels)_pcm.Channels);
            _source = (uint)SourceId(_sourceLease);
            for (int i = 0; i < _buffers.Length; i++) Al.GenBuffers(1, out _buffers[i]);
            Al.Integer(_source, 0x202, 1); // source-relative, already mixed by SoundManager
            Al.Float(_source, 0x1021, 0); // no second distance rolloff
            Pan = 0; Volume = 1; Pitch = 0;
            Al.Check();
        }
        catch { Dispose(); throw; }
    }
    public void Play()
    {
        if (_playing) return;
        foreach (uint buffer in _buffers) Submit(buffer);
        Al.Play(_source); Al.Check(); _playing = true;
    }
    private void Submit(uint buffer)
    {
        int bytes = _cursor.Read(_scratch);
        if (bytes == 0) return;
        Al.Data(buffer, _pcm.Channels == 1 ? 0x1101 : 0x1103, _scratch, bytes, _pcm.SampleRate);
        Al.Queue(_source, 1, ref buffer);
    }
    public void Pump()
    {
        if (!_playing || _disposed) return;
        Al.Get(_source, 0x1016, out int processed);
        for (int i = 0; i < processed; i++)
        {
            Al.Unqueue(_source, 1, out uint buffer);
            Submit(buffer);
        }
        Al.Get(_source, 0x1015, out int queued);
        Al.Get(_source, Al.SourceState, out int state);
        if (queued > 0 && state != Al.Playing) Al.Play(_source);
        Al.Check();
    }
    public void Stop(bool immediate)
    {
        Releasing = true; _cursor.Stop(immediate);
        if (immediate) { Al.Stop(_source); _playing = false; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source != 0)
        {
            Al.Stop(_source);
            Al.Integer(_source, 0x1009, 0); // detach queued buffers
        }
        for (int i = 0; i < _buffers.Length; i++)
            if (_buffers[i] != 0) Al.DeleteBuffers(1, ref _buffers[i]);
        _sourceLease?.Dispose();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "SourceId")]
    private static extern ref int SourceId(SoundEffectInstance instance);

    private static class Al
    {
        public const int SourceState = 0x1010, Playing = 0x1012, Gain = 0x100A, Pitch = 0x1003;
        private static readonly nint Library = Load();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Gen(int count, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Delete(int count, ref uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SetInt(uint source, int key, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SetFloat(uint source, int key, float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SetPosition(uint source, int key, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void GetInt(uint source, int key, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Source(uint source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void Buffer(uint id, int format, [In] byte[] data, int bytes, int rate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void QueueBuffers(uint source, int count, ref uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void UnqueueBuffers(uint source, int count, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetError();
        public static readonly Gen GenBuffers = Bind<Gen>("alGenBuffers");
        public static readonly Delete DeleteBuffers = Bind<Delete>("alDeleteBuffers");
        public static readonly SetInt Integer = Bind<SetInt>("alSourcei");
        public static readonly SetFloat Float = Bind<SetFloat>("alSourcef");
        public static readonly SetPosition Position = Bind<SetPosition>("alSource3f");
        public static readonly GetInt Get = Bind<GetInt>("alGetSourcei");
        public static readonly Source Play = Bind<Source>("alSourcePlay"), Stop = Bind<Source>("alSourceStop");
        public static readonly Buffer Data = Bind<Buffer>("alBufferData");
        public static readonly QueueBuffers Queue = Bind<QueueBuffers>("alSourceQueueBuffers");
        public static readonly UnqueueBuffers Unqueue = Bind<UnqueueBuffers>("alSourceUnqueueBuffers");
        private static readonly GetError Error = Bind<GetError>("alGetError");
        private static T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
        private static nint Load()
        {
            string[] names = OperatingSystem.IsWindows() ? ["soft_oal.dll", "openal32.dll"]
                : OperatingSystem.IsMacOS() ? ["libopenal.dylib", "OpenAL.framework/OpenAL"]
                : ["libopenal.so.1", "libopenal.so"];
            foreach (string name in names)
                if (NativeLibrary.TryLoad(name, typeof(SoundEffect).Assembly, null, out nint handle)) return handle;
            throw new DllNotFoundException("OpenAL library unavailable");
        }
        public static void Check()
        {
            int error = Error();
            if (error != 0) throw new InvalidOperationException($"OpenAL ASR error {error:X}");
        }
    }
}
