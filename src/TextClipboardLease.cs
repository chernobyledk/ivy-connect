using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// A narrowly scoped clipboard lease. Preserve only formats whose data we can
// copy and restore exactly, and check the sequence while holding the clipboard.
internal sealed class TextClipboardLease : IDisposable
{
    private const uint UnicodeText = 13;
    private readonly IntPtr owner;
    private readonly byte[] original;
    private readonly uint temporarySequence;
    private readonly Action<string> log;
    internal TextClipboardLease(IntPtr owner, string text, Action<string> log)
    {
        this.owner = owner;
        this.log = log;
        if (owner == IntPtr.Zero || !OpenClipboard(owner)) throw new Win32Exception();
        try
        {
            var first = EnumClipboardFormats(0);
            if (first != 0 && (first != UnicodeText || EnumClipboardFormats(first) != 0))
                throw new InvalidOperationException("剪贴板含有此工具无法无损恢复的格式，实验未调用 WM_PASTE");
            original = first == 0 ? null : CopyUnicodeHandle();
            try { Replace(Encoding.Unicode.GetBytes(text + '\0')); }
            catch
            {
                // If publishing the probe failed, recover under the same exclusive lock.
                Replace(original);
                throw;
            }
            temporarySequence = Native.GetClipboardSequenceNumber();
        }
        finally { CloseClipboard(); }
    }
    internal bool Current { get { return Native.GetClipboardSequenceNumber() == temporarySequence; } }
    public void Dispose()
    {
        if (!OpenClipboard(owner)) { log("clipboard restore=busy; left untouched"); return; }
        try
        {
            if (!Current) { log("clipboard restore=skipped; sequence changed"); return; }
            Replace(original);
            log("clipboard restore=confirmed");
        }
        finally { CloseClipboard(); }
    }
    private static byte[] CopyUnicodeHandle()
    {
        var data = GetClipboardData(UnicodeText);
        var size = GlobalSize(data).ToUInt64();
        if (data == IntPtr.Zero || size < 2 || size > 4194304)
            throw new InvalidOperationException("无法无损保存剪贴板文本");
        var pointer = GlobalLock(data);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var copy = new byte[(int)size];
            Marshal.Copy(pointer, copy, 0, copy.Length);
            return copy;
        }
        finally { GlobalUnlock(data); }
    }
    private static void Replace(byte[] data)
    {
        IntPtr storage = IntPtr.Zero;
        try
        {
            if (data != null)
            {
                storage = GlobalAlloc(2, new UIntPtr((uint)data.Length));
                if (storage == IntPtr.Zero) throw new Win32Exception();
                var destination = GlobalLock(storage);
                if (destination == IntPtr.Zero) throw new Win32Exception();
                try { Marshal.Copy(data, 0, destination, data.Length); }
                finally { GlobalUnlock(storage); }
            }
            if (!EmptyClipboard()) throw new Win32Exception();
            if (storage == IntPtr.Zero) return;
            if (SetClipboardData(UnicodeText, storage) == IntPtr.Zero) throw new Win32Exception();
            storage = IntPtr.Zero;
        }
        finally { if (storage != IntPtr.Zero) GlobalFree(storage); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr data);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr data);
}
