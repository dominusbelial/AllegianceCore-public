using System.Net.Http;
using Allegiance.Update;

// feedbuild — P810's release-feed builder (see the csproj header for usage).
//
//   feedbuild --root <dir> --out <dir> --base-url <url> --key <private.pem> --version <v>
//             [--index-only <manifest.json> <signature.b64>]   (release job splits the steps)
//             [--stamp]                                        (write <root>/autoupdate/installed.json)
//             [--verify]                                       (self-check the produced feed over HTTP)
//
// The signature covers the EXACT manifest bytes; the manifest excludes the
// feed's own index/manifest files and the HD-pack subtrees (packs/, hd/ — the
// Meshy rule: the updater must never pull the HD texture pack implicitly).

return await FeedBuild.MainAsync(args);

internal static class FeedBuild
{
    public static async Task<int> MainAsync(string[] args)
    {
        string? root = null, outDir = null, baseUrl = null, keyPath = null, version = null, assetPrefix = null;
        bool stamp = false, verify = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root": root = Next(args, ref i, "--root"); break;
                case "--out": outDir = Next(args, ref i, "--out"); break;
                case "--base-url": baseUrl = Next(args, ref i, "--base-url"); break;
                case "--key": keyPath = Next(args, ref i, "--key"); break;
                case "--version": version = Next(args, ref i, "--version"); break;
                case "--asset-prefix": assetPrefix = Next(args, ref i, "--asset-prefix"); break;
                case "--stamp": stamp = true; break;
                case "--verify": verify = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
            }
        }

        try
        {
            if (root is null || outDir is null || version is null)
            {
                Console.Error.WriteLine("usage: feedbuild --root <dir> --out <dir> --version <v> [--key <pem>] [--base-url <url>] [--stamp] [--verify]");
                return 2;
            }
            if (!Version.TryParse(version.TrimStart('v', 'V'), out _))
            {
                Console.Error.WriteLine($"--version must be a numeric release version (got '{version}')");
                return 2;
            }

            if (stamp)
            {
                var stamped = new UpdateManifest { Version = version, GeneratedAtUtc = DateTimeOffset.UtcNow, Files = [] };
                UpdateState.WriteInstalled(root, stamped);
                Console.WriteLine($"[feedbuild] stamped {root}/autoupdate/installed.json -> {version}");
            }

            var manifest = UpdateManifestBuilder.Build(root, version, null, assetPrefix);
            Console.WriteLine($"[feedbuild] manifest for {version}: {manifest.Files.Count} file(s)");

            Directory.CreateDirectory(outDir);
            byte[] manifestBytes = UpdateManifestBuilder.Serialize(manifest);
            string manifestName = (assetPrefix ?? "") + $"manifest-{version}.json";
            string manifestPath = Path.Combine(outDir, manifestName);
            File.WriteAllBytes(manifestPath, manifestBytes);

            string signature;
            if (keyPath is not null)
            {
                signature = UpdateSignature.Sign(manifestBytes, File.ReadAllText(keyPath));
            }
            else
            {
                // Unsigned builds are allowed for local dry-runs only: latest.json
                // then carries an empty signature and every verifier refuses it.
                signature = "";
                Console.WriteLine("[feedbuild] WARNING: no --key given; the feed is UNSIGNED and every verifier will refuse it");
            }

            var index = new UpdateFeedIndex(version, manifestName, signature);
            File.WriteAllBytes(Path.Combine(outDir, UpdateFeed.IndexFile), UpdateManifestBuilder.SerializeIndex(index));

            // Mirror the application files into the feed (skipping the feed's
            // own artifacts and the excluded subtrees).
            var feedRoot = Path.GetFullPath(outDir);
            int copied = 0;
            foreach (var entry in manifest.Files)
            {
                var src = Path.Combine(Path.GetFullPath(root), entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var dst = Path.Combine(feedRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                UpdateManifestBuilder.ApplyMode(dst, entry.UnixMode);
                copied++;
            }
            Console.WriteLine($"[feedbuild] feed at {outDir}: {UpdateFeed.IndexFile} + {manifestName} + {copied} file(s)");

            if (verify)
            {
                if (baseUrl is null)
                {
                    Console.Error.WriteLine("--verify requires --base-url");
                    return 2;
                }
                if (keyPath is null)
                {
                    Console.Error.WriteLine("--verify requires --key (the public half is derived from it)");
                    return 2;
                }
                await VerifyFeedAsync(baseUrl, outDir, keyPath, version);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"feedbuild failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>End-to-end self-check: download the produced feed the way the
    /// client does (signature + one file's SHA-256) against a local static
    /// server. Used by the release workflow's feed job and by hand.</summary>
    private static async Task VerifyFeedAsync(string baseUrl, string outDir, string keyPath, string version)
    {
        string publicKey = DerivePublicKey(File.ReadAllText(keyPath));
        using var http = new HttpClient();
        var result = await UpdateClient.CheckAndStageAsync(new UpdateClientOptions
        {
            FeedBaseUrl = baseUrl,
            PublicKeyPem = publicKey,
            AppRoot = CreateEmptyAppRoot(),
            InstalledVersion = "0.0.0",
        }, http);
        if (result.Status != UpdateCheckStatus.Pending || result.VersionOrSource != version)
            throw new InvalidOperationException(
                $"feed self-check did not stage {version}: {result.Status} {result.Error}");
        Console.WriteLine($"[feedbuild] self-check OK: staged {result.VersionOrSource} ({result.ChangedFiles} file(s))");
    }

    private static string CreateEmptyAppRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "feedbuild-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Read the public half back out of a PKCS#8 private PEM, so the
    /// self-check verifies with the same pair the pipeline signed with.</summary>
    private static string DerivePublicKey(string privatePem)
    {
        using var rsa = System.Security.Cryptography.ECDsa.Create();
        rsa.ImportFromPem(privatePem);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static string Next(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{name} needs a value");
        return args[++i];
    }
}
