using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SyncDrive.Infra.Secrets;

/// <summary>
/// Das Nötigste aus CoreFoundation für Aufrufe des Security-Frameworks: Zeichenketten, Daten, ein veränderbares Wörterbuch. Jedes erzeugte
/// Objekt gehört einem <see cref="Owned"/> und wird mit <c>CFRelease</c> freigegeben.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class CoreFoundation
{
    private const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    private static readonly IntPtr Handle = NativeLibrary.Load(Library);
    private static readonly IntPtr KeyCallBacks = NativeLibrary.GetExport(Handle, "kCFTypeDictionaryKeyCallBacks");
    private static readonly IntPtr ValueCallBacks = NativeLibrary.GetExport(Handle, "kCFTypeDictionaryValueCallBacks");

    public static IntPtr True { get; } = Marshal.ReadIntPtr(NativeLibrary.GetExport(Handle, "kCFBooleanTrue"));

    public static Owned String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new Owned(CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8, false));
    }

    public static Owned Data(byte[] bytes) => new(CFDataCreate(IntPtr.Zero, bytes, bytes.Length));

    public static MutableDictionary Dictionary() => new(CFDictionaryCreateMutable(IntPtr.Zero, 0, KeyCallBacks, ValueCallBacks));

    public static byte[] Bytes(IntPtr data)
    {
        var bytes = new byte[CFDataGetLength(data)];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        return bytes;
    }

    [DllImport(Library)]
    private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.U1)] bool external);

    [DllImport(Library)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(Library)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(Library)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(Library)]
    private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(Library)]
    private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

    [DllImport(Library)]
    private static extern void CFRelease(IntPtr value);

    /// <summary>Ein CoreFoundation-Objekt, das freigegeben werden muss.</summary>
    public class Owned(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle != IntPtr.Zero ? handle : throw new InvalidOperationException("CoreFoundation-Objekt fehlt.");

        public void Dispose()
        {
            CFRelease(Handle);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Wörterbuch für Abfragen; es hält eigene Verweise auf Schlüssel und Werte.</summary>
    public sealed class MutableDictionary(IntPtr handle) : Owned(handle)
    {
        public void Set(IntPtr key, IntPtr value) => CFDictionarySetValue(Handle, key, value);

        public void Set(IntPtr key, Owned value) => Set(key, value.Handle);
    }
}
