using System.Text;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P970 — the OPT-IN "remember this game's password" store. A separate per-user
/// file NEXT TO <c>player.json</c> (<see cref="IntroPreferences"/>), never inside
/// it: <c>player.json</c> is the portable preferences bundle a player may copy
/// between machines, and a secret must not travel with it.
/// <para><b>What native did.</b> Native never remembered a GAME password at all —
/// its <c>PasswordDialog</c> always opened empty (<c>WinTrek/passworddialog.cpp:63</c>)
/// and the intro's logon dialog re-typed it every join
/// (<c>WinTrek/logon.cpp:120-137</c>). Native DID remember the ACCOUNT password
/// (its logon "remember me" checkbox persists user+password, logon.cpp). The
/// port extends the same idea to the game password, opt-in and per server.</para>
/// <para><b>Keying.</b> A server is identified either by its lobby identity
/// (<c>lobby|&lt;lobby URL&gt;|&lt;listing id&gt;</c>) for a lobby/join-link game, or by
/// its connect target (<c>host|&lt;host&gt;:&lt;port&gt;</c>) for Find Server /
/// Direct Connect. The password is therefore never shared between two servers
/// that merely share an address, and a listing id that moves to another address
/// (a re-registered host) does not leak a password to the new one.</para>
/// <para><b>Protection.</b> Unix: the file is written 0600 (user read/write only)
/// — there is no cheap OS secret service on a plain Linux box (libsecret needs a
/// session D-Bus and a running keyring daemon), and 0600 in the user's own profile
/// directory is the same model <c>~/.netrc</c>/<c>~/.ssh</c> use. macOS runs under
/// the same 0600 rule rather than shelling out to <c>security</c>, which would put
/// the password on a command line. Windows: DPAPI (<c>CryptProtectData</c>,
/// user scope) keeps the value encrypted at rest, and a copy taken to another
/// account cannot decrypt it; the file itself already lives in the user profile.</para>
/// </summary>
public sealed class SavedGamePasswords
{
    /// <summary>Per-user file name, next to <c>player.json</c>.</summary>
    public const string FileName = "game-passwords.json";

    /// <summary>A hard cap so a corrupt/hostile file cannot grow without bound.</summary>
    public const int MaxEntries = 64;

    /// <summary>Longest password native's own edit pane accepts
    /// (<c>c_cbGamePassword - 1</c> = 16, <c>WinTrek/passworddialog.cpp:41</c>) —
    /// the port's edit box caps at 15 for the same reason.</summary>
    public const int MaxPasswordLength = 16;

    private const string DpapiPrefix = "dpapi:";
    private const int Version = 1;

    private readonly Dictionary<string, string> _servers = new(StringComparer.Ordinal);

    /// <summary>The directory that also holds <c>player.json</c>.</summary>
    public static string DirectoryPath
        => Path.GetDirectoryName(Path.GetFullPath(IntroPreferences.FilePath)) ?? IntroPreferences.DirectoryPath;

    /// <summary>The store file; overridable with <c>ALLEGIANCE_GAME_PASSWORDS</c> for
    /// an isolated run/test (same convention as <c>ALLEGIANCE_PLAYER_SETTINGS</c>).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("ALLEGIANCE_GAME_PASSWORDS")
        ?? Path.Combine(DirectoryPath, FileName);

    /// <summary>The lobby-scoped key for a listing resolved from a lobby / join link.</summary>
    public static string LobbyKey(string lobbyUrl, string listingId)
        => "lobby|" + NormalizeLobby(lobbyUrl) + "|" + (listingId ?? "");

    /// <summary>The connect-target key for Find Server / Direct Connect.</summary>
    public static string HostKey(string host, int port)
        => "host|" + (host ?? "").Trim().ToLowerInvariant() + ":" + (port > 0 ? port : 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string NormalizeLobby(string? lobbyUrl)
        => (lobbyUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    public int Count => _servers.Count;

    /// <summary>The saved password for <paramref name="key"/>, or null when the
    /// player never opted in for that server (or the value cannot be decrypted —
    /// e.g. a Windows DPAPI blob read on another account, which fails CLOSED).</summary>
    public string? Get(string key) => _servers.TryGetValue(key ?? "", out var stored) ? Unprotect(stored) : null;

    public bool Has(string key) => !string.IsNullOrEmpty(key) && _servers.ContainsKey(key);

    /// <summary>Remember (or replace) a password for one server. An empty/oversized
    /// password is not stored.</summary>
    public bool Set(string key, string? password)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            return false;
        if (!_servers.ContainsKey(key) && _servers.Count >= MaxEntries)
            return false;
        _servers[key] = Protect(password);
        return true;
    }

    /// <summary>Forget one server's password (the "refused password clears its saved
    /// entry" rule, and the explicit Forget action).</summary>
    public bool Remove(string key) => !string.IsNullOrEmpty(key) && _servers.Remove(key);

    public void Clear() => _servers.Clear();

    public IReadOnlyCollection<string> Keys => _servers.Keys;

    // ── persistence ──────────────────────────────────────────────────────────

    public static SavedGamePasswords Load(string? path = null)
    {
        var store = new SavedGamePasswords();
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return store;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("servers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
                return store;
            foreach (var entry in servers.EnumerateObject())
            {
                if (store._servers.Count >= MaxEntries) break;
                if (entry.Name.Length == 0 || entry.Value.ValueKind != JsonValueKind.String) continue;
                store._servers[entry.Name] = entry.Value.GetString() ?? "";
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable store degrades to "nothing remembered"; it
            // never blocks a join (same discipline as IntroPreferences.Load).
            store._servers.Clear();
        }
        return store;
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var payload = new Dictionary<string, object>
            {
                ["version"] = Version,
                ["servers"] = _servers,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
            RestrictToUser(path);
        }
        catch (Exception)
        {
            // Best-effort, like every other preference save.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Make the file user-only. Unix: 0600 (owner read/write, nothing for group or
    /// other). Windows: DPAPI already protects the VALUE, and the file sits in the
    /// user's own profile, whose ACL already excludes other standard users — so the
    /// cheap, correct move is to keep the inherited profile ACL (no explicit
    /// <c>FileSecurity</c> edit, which needs the file-system ACL APIs and is easy to
    /// get subtly wrong). Other platforms get the Unix rule when supported.
    /// </summary>
    internal static void RestrictToUser(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // A filesystem without Unix modes (a FAT mount) can refuse this; the
            // value on Windows is DPAPI-protected and elsewhere the file still
            // lives in the user's own directory.
        }
    }

    // ── Windows DPAPI ────────────────────────────────────────────────────────

    /// <summary>Encrypt with DPAPI at user scope on Windows; plaintext elsewhere
    /// (tagged by the <c>dpapi:</c> prefix so a file moved between machines is read
    /// correctly: a blob without the prefix is plaintext, a blob WITH it on a
    /// non-Windows/other-account box fails closed).</summary>
    private static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows()) return plaintext;
        // Only a SUCCESSFUL DPAPI call gets the prefix: a failed call leaves the
        // value plaintext (and un-prefixed), so it round-trips instead of becoming
        // an undecryptable "dpapi:" string.
        return TryDpapi(plaintext, protect: true) is { } blob ? DpapiPrefix + blob : plaintext;
    }

    private static string? Unprotect(string stored)
    {
        if (stored is null) return null;
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return null;   // cannot decrypt there
        return TryDpapi(stored[DpapiPrefix.Length..], protect: false);
    }

    private static string? TryDpapi(string value, bool protect)
    {
        try
        {
            return protect ? Dpapi.Protect(value) : Dpapi.Unprotect(value);
        }
        catch (Exception)
        {
            // Never lose the feature to an unavailable crypt32: fall back to the
            // 0600/profile-ACL rule (plaintext), observable via DpapiFailed so a
            // test can assert which path ran without WINE/Windows.
            DpapiFailed = true;
            return null;
        }
    }

    /// <summary>True when a DPAPI call failed and the plaintext fallback was used
    /// (observable so the behaviour is testable without WINE/Windows).</summary>
    public static bool DpapiFailed { get; private set; }

    private static class Dpapi
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [System.Runtime.InteropServices.DllImport("crypt32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [System.Runtime.InteropServices.DllImport("crypt32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        // CRYPTPROTECT_UI_FORBIDDEN — never prompt (there is no UI thread here).
        private const int UiForbidden = 0x1;

        public static string Protect(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var blob = Transform(bytes, true);
            if (blob is null) throw new InvalidOperationException("CryptProtectData failed");
            return Convert.ToBase64String(blob);
        }

        public static string? Unprotect(string base64)
        {
            byte[] blob;
            try { blob = Convert.FromBase64String(base64); }
            catch (FormatException) { return null; }
            var plain = Transform(blob, false);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }

        private static byte[]? Transform(byte[] input, bool protect)
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(input, System.Runtime.InteropServices.GCHandleType.Pinned);
            var pout = default(DataBlob);
            try
            {
                var bin = new DataBlob { Size = input.Length, Data = pin.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref bin, "AllegianceCore game password", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref pout)
                    : CryptUnprotectData(ref bin, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref pout);
                if (!ok || pout.Data == IntPtr.Zero) return null;
                var result = new byte[pout.Size];
                System.Runtime.InteropServices.Marshal.Copy(pout.Data, result, 0, pout.Size);
                return result;
            }
            finally
            {
                if (pout.Data != IntPtr.Zero) LocalFree(pout.Data);
                pin.Free();
            }
        }
    }
}
