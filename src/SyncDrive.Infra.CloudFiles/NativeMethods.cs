using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SyncDrive.Infra.CloudFiles;

/// <summary>
/// Cloud-Files-API (<c>cfapi.h</c>, <c>cldapi.dll</c>, ab Windows 10 1709). Strukturen in der Anordnung von x64; Felder, die SyncDrive nicht
/// braucht, sind nur als Platzhalter für die richtigen Abstände da.
/// </summary>
internal static unsafe class NativeMethods
{
    private const string CldApi = "cldapi.dll";

    public const int CallbackFetchData = 0;
    public const int CallbackCancelFetchData = 2;
    public const int CallbackNone = unchecked((int)0xFFFFFFFF);

    public const int OperationTransferData = 0;

    public const ushort HydrationFull = 2;
    public const ushort PopulationAlwaysFull = 3;
    public const uint InSyncTrackAll = 0x00FFFFFF;

    public const uint RegisterUpdate = 0x1;
    public const uint RegisterMarkInSyncOnRoot = 0x4;

    public const uint ConnectRequireFullFilePath = 0x4;
    public const uint ConnectBlockSelfImplicitHydration = 0x8;

    public const uint PlaceholderMarkInSync = 0x2;
    public const uint PlaceholderSupersede = 0x4;

    public const uint ConvertMarkInSync = 0x1;

    public const int InSyncStateNotInSync = 0;
    public const int InSyncStateInSync = 1;

    public const int StatusUnsuccessful = unchecked((int)0xC0000001);

    /// <summary><c>ERROR_NOT_A_CLOUD_FILE</c> als HRESULT: die Datei ist (noch) kein Platzhalter.</summary>
    public const int NotACloudFile = unchecked((int)0x80070178);

    public const uint FileAttributeNormal = 0x80;

    public const uint ReadAttributes = 0x80;
    public const uint WriteDac = 0x40000;
    public const uint FlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackInfo
    {
        public uint StructSize;
        public long ConnectionKey;
        public IntPtr CallbackContext;
        public IntPtr VolumeGuidName;
        public IntPtr VolumeDosName;
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public IntPtr SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public IntPtr NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public IntPtr CorrelationVector;
        public IntPtr ProcessInfo;
        public long RequestKey;
    }

    /// <summary><c>CF_CALLBACK_PARAMETERS</c> für <c>FETCH_DATA</c> (Union ab Abstand 8).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct FetchDataParameters
    {
        [FieldOffset(0)]
        public uint ParamSize;

        [FieldOffset(8)]
        public uint Flags;

        [FieldOffset(16)]
        public long RequiredFileOffset;

        [FieldOffset(24)]
        public long RequiredLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackRegistration
    {
        public int Type;
        public IntPtr Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OperationInfo
    {
        public uint StructSize;
        public int Type;
        public long ConnectionKey;
        public long TransferKey;
        public IntPtr CorrelationVector;
        public IntPtr SyncStatus;
        public long RequestKey;
    }

    /// <summary><c>CF_OPERATION_PARAMETERS</c> für <c>TRANSFER_DATA</c>; <see cref="ParamSize"/> ist Abstand plus Größe des Teils (40).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct TransferDataParameters
    {
        [FieldOffset(0)]
        public uint ParamSize;

        [FieldOffset(8)]
        public uint Flags;

        [FieldOffset(12)]
        public int CompletionStatus;

        [FieldOffset(16)]
        public IntPtr Buffer;

        [FieldOffset(24)]
        public long Offset;

        [FieldOffset(32)]
        public long Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncRegistration
    {
        public uint StructSize;
        public IntPtr ProviderName;
        public IntPtr ProviderVersion;
        public IntPtr SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SyncPolicies
    {
        public uint StructSize;
        public ushort HydrationPrimary;
        public ushort HydrationModifier;
        public ushort PopulationPrimary;
        public ushort PopulationModifier;
        public uint InSync;
        public uint HardLink;
        public uint PlaceholderManagement;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PlaceholderCreateInfo
    {
        public IntPtr RelativeFileName;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public long FileSize;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [DllImport(CldApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfRegisterSyncRoot(string syncRootPath, in SyncRegistration registration, in SyncPolicies policies, uint flags);

    [DllImport(CldApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfUnregisterSyncRoot(string syncRootPath);

    [DllImport(CldApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfConnectSyncRoot(
        string syncRootPath, CallbackRegistration* callbackTable, IntPtr callbackContext, uint flags, out long connectionKey);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfExecute(in OperationInfo operation, ref TransferDataParameters parameters);

    [DllImport(CldApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfCreatePlaceholders(
        string baseDirectoryPath, PlaceholderCreateInfo* placeholders, uint count, uint flags, out uint entriesProcessed);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfSetInSyncState(SafeFileHandle file, int inSyncState, uint flags, IntPtr inSyncUsn);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfConvertToPlaceholder(
        SafeFileHandle file, IntPtr fileIdentity, uint fileIdentityLength, uint flags, IntPtr convertUsn, IntPtr overlapped);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfHydratePlaceholder(SafeFileHandle file, long startingOffset, long length, uint flags, IntPtr overlapped);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfDehydratePlaceholder(SafeFileHandle file, long startingOffset, long length, uint flags, IntPtr overlapped);

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfRevertPlaceholder(SafeFileHandle file, uint flags, IntPtr overlapped);

    public const int PlaceholderInfoBasic = 0;

    /// <summary>Liefert auch bei zu kleinem Puffer den Anfang (ERROR_MORE_DATA); für die Länge der Kennung genügt der.</summary>
    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfGetPlaceholderInfo(SafeFileHandle file, int infoClass, byte* info, uint length, out uint returned);

    public const uint UpdateVerifyInSync = 0x1;
    public const uint UpdateDehydrate = 0x4;

    /// <summary>Hebt „immer vollständig“ auf, das Windows an umgewandelte Dateien hängen kann; sonst scheitert das Freigeben.</summary>
    public const uint UpdateAllowPartial = 0x400;

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfUpdatePlaceholder(
        SafeFileHandle file, IntPtr fsMetadata, IntPtr fileIdentity, uint fileIdentityLength, IntPtr dehydrateRanges, uint dehydrateRangeCount,
        uint flags, IntPtr updateUsn, IntPtr overlapped);

    public const uint PinStatePinned = 1;
    public const uint PinStateUnpinned = 2;
    public const uint SetPinRecurse = 0x1;

    [DllImport(CldApi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int CfSetPinState(SafeFileHandle file, uint pinState, uint flags, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flags, IntPtr template);

    /// <summary>Handle für die Platzhalter-Aufrufe; liest keine Daten, holt also auch keinen Inhalt.</summary>
    public static SafeFileHandle OpenForCloudOperation(string path, FileShare share = FileShare.ReadWrite | FileShare.Delete)
    {
        var handle = CreateFileW(
            path, ReadAttributes | WriteDac, share, IntPtr.Zero, FileMode.Open,
            Directory.Exists(path) ? FlagBackupSemantics : 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"{path} lässt sich nicht öffnen (Fehler {error}).", Marshal.GetHRForLastWin32Error());
        }

        return handle;
    }

    /// <summary>Wirft bei einem Fehler-HRESULT eine <see cref="IOException"/>, die der Sync als vorübergehend behandelt.</summary>
    public static void Check(int hresult, string what)
    {
        if (hresult < 0)
        {
            throw new IOException($"{what}: 0x{hresult:X8} {Marshal.GetExceptionForHR(hresult)?.Message}", hresult);
        }
    }
}
