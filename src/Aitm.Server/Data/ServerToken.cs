using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Aitm.Server.Data;

/// <summary>
/// The bearer token every route but <c>/health</c> requires (RESTRUCTURE.md slice 25: "the token is
/// created once in the AITM data dir as server.token with user-only access [...] Unix 0600 at creation;
/// Windows ACL with only the current user, inheritance removed"). Slice 23c
/// (<see cref="Handover.IdPTokenTool"/>) landed the same shape for the IdP handover token file
/// while this slice was in flight; its version is private to that class, so there is nothing public to
/// call from here yet. Note for later merging: the two should fold into one shared "write a secret file
/// user-only, atomically" helper once a slice touches both.
/// </summary>
public static class ServerToken
{
    public const string FileName = "server.token";

    /// <summary>Creates the token once and reuses it on every later call (a restart never rotates it).</summary>
    public static string EnsureToken(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        string path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length > 0) return existing;
        }

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string tempPath = Path.Combine(dataDir, $".{Guid.NewGuid():N}.tmp");

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        // User-only from the moment of creation, not chmod'd after the fact, so there is no window
        // where the file is briefly readable by anyone else.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (FileStream stream = new(tempPath, options))
        using (StreamWriter writer = new(stream))
        {
            writer.Write(token);
        }
        if (OperatingSystem.IsWindows()) RestrictToCurrentUserOnWindows(tempPath);

        File.Move(tempPath, path, overwrite: true);
        return token;
    }

    // Grants exactly one access rule — the current Windows user, full control, no inheritance — and
    // removes every inherited rule from the parent directory. Applied to the temp file before the
    // atomic move into place, the same shape IdPTokenTool uses for its token file.
    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUserOnWindows(string filePath)
    {
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(filePath).SetAccessControl(security);
    }
}
