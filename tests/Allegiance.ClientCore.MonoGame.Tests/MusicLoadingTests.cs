using Allegiance.ClientCore.MonoGame.Audio;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class MusicLoadingTests
{
    [Fact]
    public void Missing_beds_stay_silent_even_when_music_is_enabled_and_ship_docks()
    {
        using var content = new MissingMusicContent();
        using var music = new Music(content);
        music.Load();
        Assert.Equal(new[] { Music.FlightAsset, Music.StationAsset }, content.Requested);
        music.Toggle();
        music.Update(isDocked: false);
        Assert.Equal(MusicTrackKind.Flight, music.Current);
        Assert.Equal(SoundState.Stopped, music.FlightState);
        Assert.Equal(0f, music.FlightActualVolume);
        music.Update(isDocked: true);
        Assert.Equal(MusicTrackKind.Station, music.Current);
        Assert.Equal(SoundState.Stopped, music.StationState);
        Assert.Equal(0f, music.StationActualVolume);
    }

    private sealed class MissingMusicContent() : ContentManager(new EmptyServices())
    {
        public List<string> Requested { get; } = [];
        public override T Load<T>(string assetName)
        {
            Requested.Add(assetName);
            throw new ContentLoadException("No real bed in the pinned artwork");
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
