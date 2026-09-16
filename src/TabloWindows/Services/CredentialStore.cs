using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TabloWindows.Services;

/// <summary>
/// Stores the Tablo account email/password encrypted at rest with Windows DPAPI
/// (per-user). Mirrors tablo2plex's creds.bin idea but uses the OS keystore so no
/// hard-coded key is needed.
/// </summary>
public sealed class CredentialStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabloWindows");
    private static readonly string File_ = Path.Combine(Dir, "creds.bin");

    public sealed record Creds(string Email, string Password, string? PreferredServerId);

    public Creds? Load()
    {
        try
        {
            if (!File.Exists(File_)) return null;
            var enc = File.ReadAllBytes(File_);
            var raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Creds>(Encoding.UTF8.GetString(raw));
        }
        catch
        {
            return null;
        }
    }

    public void Save(Creds creds)
    {
        Directory.CreateDirectory(Dir);
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(creds));
        var enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(File_, enc);
    }

    public void Clear()
    {
        try { if (File.Exists(File_)) File.Delete(File_); } catch { /* ignore */ }
    }
}
