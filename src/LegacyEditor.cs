using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// The managed UIA wrapper predates LegacyIAccessiblePattern. Match the selected
// managed element to UIA COM by runtime ID at each level of the same raw tree.
internal static class LegacyEditor
{
    private static readonly Guid AutomationClass = new Guid("e22ad333-b25f-460c-83d0-0581107395c9");
    private const int LegacyPatternId = 10018;
    private const uint EditableTextRole = 0x2a, UnavailableOrReadOnly = 0x41;

    internal static ILegacy Find(AutomationElement origin, AutomationElement editor)
    {
        if (origin == null || editor == null) return null;
        var path = new Stack<int[]>();
        var managed = TreeWalker.RawViewWalker;
        var originId = origin.GetRuntimeId();
        for (var node = editor; node != null && path.Count < 24; node = managed.GetParent(node))
        {
            var id = node.GetRuntimeId();
            if (id.SequenceEqual(originId))
            {
                var automation = (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(AutomationClass, true));
                var element = automation.ElementFromHandle(new IntPtr(origin.Current.NativeWindowHandle));
                var walk = automation.RawViewWalker();
                foreach (var childId in path)
                {
                    var child = walk.FirstChild(element);
                    int scanned = 0;
                    while (child != null && scanned++ < 300 && !child.RuntimeId().SequenceEqual(childId))
                        child = walk.NextSibling(child);
                    if (child == null || !child.RuntimeId().SequenceEqual(childId)) return null;
                    element = child;
                }
                return element.CurrentPattern(LegacyPatternId) as ILegacy;
            }
            path.Push(id);
        }
        return null;
    }
    internal static bool Writable(ILegacy legacy)
    {
        return legacy != null && legacy.CurrentRole() == EditableTextRole &&
            (legacy.CurrentState() & UnavailableOrReadOnly) == 0;
    }

    // Slots follow the Windows SDK UIAutomationClient IDL. Unused slots only
    // preserve ordering and are never called.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    private interface IAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void RootElement();
        IElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void FocusedElement();
        void RootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void FocusedElementBuildCache();
        void CreateTreeWalker();
        void ControlViewWalker();
        void ContentViewWalker();
        IWalker RawViewWalker();
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("4042c624-389c-4afc-a630-9df854a541fc")]
    private interface IWalker
    {
        void Parent();
        IElement FirstChild(IElement parent);
        void LastChild();
        IElement NextSibling(IElement previous);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    private interface IElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[] RuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void CurrentPropertyValue();
        void CurrentPropertyValueEx();
        void CachedPropertyValue();
        void CachedPropertyValueEx();
        void CurrentPatternAs();
        void CachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object CurrentPattern(int id);
    }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("828055ad-355b-4435-86d5-3b51c14a9b1b")]
    internal interface ILegacy
    {
        void Select();
        [PreserveSig]
        int DoDefaultAction();
        [PreserveSig]
        int SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
        int CurrentChildId();
        void CurrentName();
        [return: MarshalAs(UnmanagedType.BStr)]
        string CurrentValue();
        void CurrentDescription();
        uint CurrentRole();
        uint CurrentState();
        void CurrentHelp();
        void CurrentKeyboardShortcut();
        void CurrentSelection();
        [return: MarshalAs(UnmanagedType.BStr)]
        string CurrentDefaultAction();
        void CachedChildId();
        void CachedName();
        void CachedValue();
        void CachedDescription();
        void CachedRole();
        void CachedState();
        void CachedHelp();
        void CachedKeyboardShortcut();
        void CachedSelection();
        void CachedDefaultAction();
        [return: MarshalAs(UnmanagedType.Interface)]
        Accessibility.IAccessible GetIAccessible();
    }
}
