using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SyncDrive.Client.Tray;

/// <summary>
/// Menüleisten-Symbol unter macOS direkt über AppKit (<c>NSStatusItem</c>). Avalonias TrayIcon meldet dort keinen Klick, ein Menü ginge
/// nur als Liste; SyncDrive öffnet aber wie unter Windows beim Klick das Panel. Ein kleines Objective-C-Ziel (zur Laufzeit angelegte
/// Klasse) leitet die Aktion des Knopfs an <see cref="Clicked"/> weiter. Muss im Hauptthread angelegt und benutzt werden (AppKit).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacStatusItem
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const double VariableLength = -1; // NSVariableStatusItemLength
    private const ulong LeftAndRightMouseUp = (1UL << 2) | (1UL << 4); // NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp

    private static MacStatusItem? _current;
    private readonly IntPtr _item;
    private readonly IntPtr _button;

    public MacStatusItem()
    {
        var bar = Send(Class("NSStatusBar"), "systemStatusBar");
        _item = Send(SendDouble(bar, Selector("statusItemWithLength:"), VariableLength), "retain");
        _button = Send(_item, "button");
        var target = Send(Send(TargetClass(), "alloc"), "init");
        SendPtr(_button, Selector("setTarget:"), target);
        SendPtr(_button, Selector("setAction:"), Selector("clicked:"));
        SendULong(_button, Selector("sendActionOn:"), LeftAndRightMouseUp);
        _current = this;
    }

    /// <summary>Klick (links oder rechts) auf das Symbol.</summary>
    public event Action? Clicked;

    /// <summary>Bild als PNG; als Vorlage (template) färbt macOS es passend zur Menüleiste; 18 Punkt hoch.</summary>
    public void SetImage(byte[] png)
    {
        var data = DataFrom(png);
        var image = SendPtr(Send(Class("NSImage"), "alloc"), Selector("initWithData:"), data);
        SendBool(image, Selector("setTemplate:"), true);
        SendSize(image, Selector("setSize:"), new CGSize(18, 18));
        SendPtr(_button, Selector("setImage:"), image);
        Send(image, "release");
    }

    public void SetToolTip(string text) => SendPtr(_button, Selector("setToolTip:"), NsString(text));

    private static IntPtr TargetClass()
    {
        var existing = Class("SyncDriveStatusTarget");
        if (existing != IntPtr.Zero)
        {
            return existing;
        }

        var type = objc_allocateClassPair(Class("NSObject"), Utf8("SyncDriveStatusTarget"), 0);
        unsafe
        {
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> handler = &OnClicked;
            class_addMethod(type, Selector("clicked:"), (IntPtr)handler, Utf8("v@:@"));
        }

        objc_registerClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly]
    private static void OnClicked(IntPtr self, IntPtr selector, IntPtr sender) => _current?.Clicked?.Invoke();

    private static IntPtr DataFrom(byte[] bytes)
    {
        unsafe
        {
            fixed (byte* p = bytes)
            {
                return SendPtrULong(Class("NSData"), Selector("dataWithBytes:length:"), (IntPtr)p, (ulong)bytes.Length);
            }
        }
    }

    private static IntPtr NsString(string text)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text + "\0");
        unsafe
        {
            fixed (byte* p = utf8)
            {
                return SendPtr(Class("NSString"), Selector("stringWithUTF8String:"), (IntPtr)p);
            }
        }
    }

    private static IntPtr Class(string name) => objc_getClass(Utf8(name));

    private static IntPtr Selector(string name) => sel_registerName(Utf8(name));

    /// <summary>C-Zeichenkette, wie die Objective-C-Laufzeit sie erwartet: UTF-8 mit abschließender Null.</summary>
    private static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text + "\0");

    private static IntPtr Send(IntPtr receiver, string selector) => objc_msgSend(receiver, Selector(selector));

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtrULong(IntPtr receiver, IntPtr selector, IntPtr argument, ulong length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendDouble(IntPtr receiver, IntPtr selector, double argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSize(IntPtr receiver, IntPtr selector, CGSize argument);

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(byte[] name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(byte[] name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, byte[] name, nuint extraBytes);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(IntPtr type);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr type, IntPtr selector, IntPtr implementation, byte[] types);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);
}
