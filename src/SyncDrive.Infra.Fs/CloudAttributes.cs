using System.Runtime.InteropServices;

namespace SyncDrive.Infra.Fs;

/// <summary>
/// Erkennt Platzhalter der Cloud-Files-API (Windows). Sie sind Reparse-Punkte, aber keine Verknüpfungen: Der Scan muss sie wie Dateien
/// behandeln, nicht als Link überspringen. Ohne Inhalt tragen sie „Abruf bei Datenzugriff“ (oder, älter, „offline“).
/// </summary>
public static class CloudAttributes
{
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    public const FileAttributes Pinned = (FileAttributes)0x00080000;
    public const FileAttributes Unpinned = (FileAttributes)0x00100000;
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Alle Varianten <c>IO_REPARSE_TAG_CLOUD_*</c>: Bits 12–15 tragen die Variante.</summary>
    private const uint CloudTagMask = 0xFFFF0FFF;
    private const uint CloudTag = 0x9000001A;

    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    public static bool IsPlaceholder(FileSystemInfo info)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var attributes = info.Attributes;
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return IsCloudOnly(attributes); // je nach Kompatibilitätsmodus blendet Windows den Reparse-Punkt aus
        }

        return ReparseTag(info.FullName) is { } tag && (tag & CloudTagMask) == CloudTag;
    }

    private static uint? ReparseTag(string path)
    {
        var handle = FindFirstFileExW(path, 1 /* FindExInfoBasic */, out var data, 0, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        FindClose(handle);
        return data.Reserved0; // bei Reparse-Punkten der Tag
    }

    /// <summary><c>FILETIME</c> ist nur auf 4 Byte ausgerichtet; ohne <c>Pack = 4</c> lägen alle folgenden Felder 4 Byte zu weit hinten.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FindFirstFileExW(
        string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr searchFilter, int flags);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool FindClose(IntPtr handle);
}
