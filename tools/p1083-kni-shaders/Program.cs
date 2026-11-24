using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using Microsoft.Xna.Framework.Content.Pipeline.Serialization.Compiler;
using Microsoft.Xna.Framework.Graphics;

if (args is ["--legacy-volumetric", var sourceFile, var desktopFile, var outputFile])
{
    if (Path.GetFileName(sourceFile) != "Volumetric.fx" || Path.GetFileName(desktopFile) != "Volumetric.ogl.mgfxo")
        throw new ArgumentException("The spike bridge only accepts the named Volumetric source and artifact.");
    // Fail explicitly if a bundled pipeline dependency is absent. KNI otherwise
    // skips this assembly's writers and silently chooses ReflectiveWriter.
    _ = typeof(CompiledEffectContent).Assembly.GetTypes();
    byte[] sourceBytes = File.ReadAllBytes(sourceFile);
    string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
    byte[] desktopBytes = File.ReadAllBytes(desktopFile);
    byte[] legacy = Allegiance.Tools.KniShaders.LegacyVolumetric.Convert(sourceBytes, sourceHash,
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(desktopFile))!, "Volumetric.fx.sha256")), desktopBytes);
    using var stream = new MemoryStream();
    new ContentCompiler().Compile(stream, new CompiledEffectContent(legacy), TargetPlatform.BlazorGL,
        GraphicsProfile.HiDef, ContentCompression.Uncompressed, Path.GetFullPath("."), Path.GetFullPath("."));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
    File.WriteAllBytes(outputFile, stream.ToArray());
    File.WriteAllText(outputFile + ".json", JsonSerializer.Serialize(new {
        format = "KNI XNB containing MGFX10; legacy WebGL translation", compilerVersion = "4.3.9001",
        sourceSha256 = sourceHash, desktopSha256 = Convert.ToHexString(SHA256.HashData(desktopBytes)).ToLowerInvariant(),
        outputSha256 = Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant(), outputBytes = stream.Length,
        shaderCodeChanged = false,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Volumetric: {stream.Length} XNB bytes (legacy route, not an HLSL compile).");
    return 0;
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: p1083-kni-shaders SHADER_SOURCE_DIRECTORY OUTPUT_DIRECTORY");
    return 2;
}
string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var rows = new List<object>();
int failures = 0;
foreach (string file in Directory.GetFiles(source, "*.fx").Order(StringComparer.Ordinal))
{
    string stem = Path.GetFileNameWithoutExtension(file);
    string target = Path.Combine(output, stem + ".xnb");
    File.Delete(target); // A failed attempt must never retain a stale successful output.
    File.Delete(target + ".json");
    string? error = null;
    try
    {
        var input = new EffectImporter().Import(file, new ImportContext(output));
        var compiled = new EffectProcessor { DebugMode = EffectProcessorDebugMode.Optimize, Defines = "OPENGL=1" }
            .Process(input, new ProcessorContext(output, target));
        using var stream = new MemoryStream();
        new ContentCompiler().Compile(stream, compiled, TargetPlatform.BlazorGL,
            GraphicsProfile.HiDef, ContentCompression.Uncompressed, output, output);
        File.WriteAllBytes(target, stream.ToArray());
    }
    catch (Exception exception)
    {
        // Keep diagnostics portable; full native loader paths are scratch-only.
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            messages.Add(current.GetType().Name + ": " + current.Message);
        error = string.Join("\n", messages).Replace(source, "<sources>").Replace(AppContext.BaseDirectory, "<tool>/").Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>");
        failures++;
    }
    long bytes = File.Exists(target) ? new FileInfo(target).Length : 0;
    if (error is null)
        File.WriteAllText(target + ".json", JsonSerializer.Serialize(new {
            format = "KNI XNB containing KNIFX12; BlazorGL/GLES", compilerVersion = "4.3.9001",
            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
            outputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant(),
            outputBytes = bytes, sourceChanged = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
    rows.Add(new { shader = stem + ".fx", compiled = error is null, error, outputBytes = bytes,
        sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
        outputSha256 = bytes > 0 ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant() : null });
    Console.WriteLine($"{stem}.fx: {(error is null ? $"{bytes} bytes" : error.Split('\n')[0])}");
}
File.WriteAllText(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(new {
    runtimeVersion = "4.3.9001", platform = "BlazorGL", profile = "HiDef", defines = "OPENGL=1", operatingSystem = Environment.OSVersion.Platform.ToString(), rows
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

sealed class Logger : ContentBuildLogger
{
    public override void LogImportantMessage(string message, params object[] messageArgs) => Console.WriteLine(message, messageArgs);
    public override void LogMessage(string message, params object[] messageArgs) { }
    public override void LogWarning(string helpLink, ContentIdentity contentIdentity, string message, params object[] messageArgs) => Console.Error.WriteLine(message, messageArgs);
}
sealed class ImportContext(string output) : ContentImporterContext
{
    public override string IntermediateDirectory => output;
    public override string OutputDirectory => output;
    public override ContentBuildLogger Logger { get; } = new Logger();
    public override void AddDependency(string filename) { }
}
sealed class ProcessorContext(string output, string filename) : ContentProcessorContext
{
    public override string BuildConfiguration => "Release";
    public override string IntermediateDirectory => output;
    public override string OutputDirectory => output;
    public override string OutputFilename => filename;
    public override ContentBuildLogger Logger { get; } = new Logger();
    public override OpaqueDataDictionary Parameters { get; } = new();
    public override TargetPlatform TargetPlatform => TargetPlatform.BlazorGL;
    public override GraphicsProfile TargetProfile => GraphicsProfile.HiDef;
    public override void AddDependency(string dependency) { }
    public override void AddOutputFile(string file) { }
    public override TOutput BuildAndLoadAsset<TInput, TOutput>(ExternalReference<TInput> asset, string processor, OpaqueDataDictionary parameters, string importer) => throw new NotSupportedException();
    public override ExternalReference<TOutput> BuildAsset<TInput, TOutput>(ExternalReference<TInput> asset, string processor, OpaqueDataDictionary parameters, string importer, string assetName) => throw new NotSupportedException();
    public override TOutput Convert<TInput, TOutput>(TInput input, string processor, OpaqueDataDictionary parameters) => throw new NotSupportedException();
}
