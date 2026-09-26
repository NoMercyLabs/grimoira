using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Aitm.Server.Data;

/// <summary>
/// Writes a secret file that only the current user can read, atomically: a temp file in the same
/// directory, created user-only (Unix 0600 at creation; on Windows an ACL with only the current user and
/// inheritance removed, applied before the move), then moved over the target. There is no window where the
/// file is readable by anyone else, and a reader never sees a half-written file. Used for
/// <see cref="ServerToken"/>'s server.token and <see cref="Handover.IdPTokenTool"/>'s token file.
/// </summary>
internal static class UserOnlySecretFile
{
    public static void Write(string filePath, Action<Stream> writeContent)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        string tempPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");

        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (FileStream stream = new(tempPath, options))
        {
            writeContent(stream);
        }
        if (OperatingSystem.IsWindows()) RestrictToCurrentUserOnWindows(tempPath);

        File.Move(tempPath, filePath, overwrite: true);
    }

    // Grants exactly one access rule — the current Windows user, full control, no inheritance — and
    // removes every inherited rule from the parent directory. .NET has no Unix mode-at-creation
    // equivalent for Windows; this is the ACL analogue, applied to the temp file before the move.
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
