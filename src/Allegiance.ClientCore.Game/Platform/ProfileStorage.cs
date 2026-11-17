namespace Allegiance.ClientCore.MonoGame.Platform;

/// <summary>Synchronous reads are resident in the browser; persistence is flushed asynchronously by its head.</summary>
public interface IProfileStorage
{
    bool Exists(string path);
    string ReadAllText(string path);
    void CreateDirectory(string path);
    void WriteAllText(string path, string text);
    void Move(string source, string destination, bool overwrite);
}

public static class ProfileStorage
{
#if BROWSER
    public static IProfileStorage Default { get; set; } = new ResidentProfileStorage();
#else
    public static IProfileStorage Default { get; } = new FileProfileStorage();
#endif
}

#if !BROWSER
/// <summary>Direct desktop/Android System.IO calls, preserving existing encoding and atomic-save order.</summary>
public sealed class FileProfileStorage : IProfileStorage
{
    public bool Exists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);
    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
}
#endif
