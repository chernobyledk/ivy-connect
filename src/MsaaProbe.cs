using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Accessibility;

internal static class MsaaProbe
{
    private static readonly Guid Accessible = typeof(IAccessible).GUID;
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(Native.Point point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);

    internal static IAccessible FindMappedEditor(Target target, Controls ui, Action<string> structuralLog = null)
    {
        if (ui.Editor == null || !ui.Editor.Current.IsEnabled) return null;
        var rect = ui.Editor.Current.BoundingRectangle;
        var candidates = new List<IAccessible>();
        var roots = new List<IAccessible>();
        Native.EnumChildWindows(target.Hwnd, (hwnd, data) => {
            if (Native.Class(hwnd) != "Chrome_RenderWidgetHostHWND") return true;
            var iid = Accessible;
            IAccessible root;
            if (AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out root) != 0 || root == null)
                return true;
            if (structuralLog != null) roots.Add(root);
            try
            {
                var node = root.accFocus as IAccessible;
                if (node == null || !MatchesBounds(node, 0, rect)) return true;
                var state = node.get_accState(0);
                if (state is int && (((int)state & 0x41) == 0) && node.get_accValue(0) != null)
                    candidates.Add(node);
            }
            catch (COMException) { }
            return true;
        }, IntPtr.Zero);
        if (candidates.Count == 1)
        {
            if (structuralLog != null) structuralLog("MSAA editor source=renderer-retained-focus");
            return candidates[0];
        }
        if (candidates.Count != 0 || structuralLog == null) return null;
        var remaining = 4000;
        foreach (var root in roots) FindInRenderer(root, rect, candidates, 0, ref remaining);
        structuralLog("MSAA editor source=renderer-tree exact-editable-matches=" + candidates.Count +
            " enumeration-complete=" + (remaining > 0));
        return remaining > 0 && candidates.Count == 1 ? candidates[0] : null;
    }
    private static void FindInRenderer(IAccessible node, System.Windows.Rect rect,
        List<IAccessible> matches, int depth, ref int remaining)
    {
        if (depth > 28) { remaining = 0; return; }
        if (remaining-- <= 0) return;
        try
        {
            if (MatchesBounds(node, 0, rect))
            {
                var role = node.get_accRole(0);
                var state = node.get_accState(0);
                if ((Equals(role, 0x14) || Equals(role, 0x2a)) && state is int &&
                    (((int)state & 0x100041) == 0x100000) && node.get_accValue(0) != null &&
                    !matches.Contains(node))
                    matches.Add(node);
            }
            var children = node.accChildCount;
            for (int index = 1; index <= children && remaining > 0; index++)
            {
                IAccessible child;
                try { child = node.get_accChild(index) as IAccessible; }
                catch (COMException) { continue; }
                if (child != null) FindInRenderer(child, rect, matches, depth + 1, ref remaining);
            }
        }
        catch (COMException) { }
    }
    private static bool MatchesBounds(IAccessible node, object child, System.Windows.Rect editor)
    {
        int x, y, w, h;
        node.accLocation(out x, out y, out w, out h, child);
        return !editor.IsEmpty && Math.Abs(editor.X - x) <= 1 && Math.Abs(editor.Y - y) <= 1 &&
            Math.Abs(editor.Width - w) <= 1 && Math.Abs(editor.Height - h) <= 1;
    }
    internal static int PutValue(IAccessible editor, string probe)
    {
        return ((IAccessiblePutValue)editor).PutAccValue(0, probe);
    }

    // IDispatch and IAccessible slots in Windows SDK oleacc.h.
    // Unused declarations preserve their vtable positions.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("618736e0-3c3d-11cf-810c-00aa00389b71")]
    private interface IAccessiblePutValue
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();
        void AccParent();
        void AccChildCount();
        void AccChild();
        void AccName();
        void AccValue();
        void AccDescription();
        void AccRole();
        void AccState();
        void AccHelp();
        void AccHelpTopic();
        void AccKeyboardShortcut();
        void AccFocus();
        void AccSelection();
        void AccDefaultAction();
        void AccSelect();
        void AccLocation();
        void AccNavigate();
        void AccHitTest();
        void DoDefaultAction();
        void PutAccName();
        [PreserveSig]
        int PutAccValue([MarshalAs(UnmanagedType.Struct)] object child,
                        [MarshalAs(UnmanagedType.BStr)] string value);
    }

    internal static void Describe(Target target, Controls ui, Action<string> log)
    {
        var editor = ui.Editor == null ? System.Windows.Rect.Empty : ui.Editor.Current.BoundingRectangle;
        DescribeWindow(target.Hwnd, editor, log);
        Native.EnumChildWindows(target.Hwnd, (hwnd, data) => {
            if (Native.Class(hwnd) == "Chrome_RenderWidgetHostHWND") DescribeWindow(hwnd, editor, log);
            return true;
        }, IntPtr.Zero);
        if (ui.Editor == null || Native.GetForegroundWindow() != target.Hwnd) return;
        var rect = ui.Editor.Current.BoundingRectangle;
        var point = new Native.Point { X = (int)(rect.X + rect.Width / 2), Y = (int)(rect.Y + rect.Height / 2) };
        if (rect.IsEmpty || Native.RootAt(point.X, point.Y) != target.Hwnd) return;
        IAccessible element;
        object child;
        var hr = AccessibleObjectFromPoint(point, out element, out child);
        log("MSAA point hr=0x" + hr.ToString("X8") + " editorRect=" + rect);
        for (int depth = 0; hr == 0 && element != null && depth < 4; depth++)
        {
            DescribeObject("MSAA editor point level " + depth, element, child, editor, log);
            try { element = element.accParent as IAccessible; child = 0; }
            catch (COMException) { break; }
        }
    }
    private static void DescribeWindow(IntPtr hwnd, System.Windows.Rect editor, Action<string> log)
    {
        var iid = Accessible;
        IAccessible root;
        var hr = AccessibleObjectFromWindow(hwnd, unchecked((uint)-4), ref iid, out root); // OBJID_CLIENT
        var label = "MSAA OBJID_CLIENT hwnd=" + hwnd.ToInt64() + " class=" + Native.Class(hwnd);
        log(label + " hr=0x" + hr.ToString("X8"));
        if (hr != 0 || root == null) return;
        DescribeObject(label + " root", root, 0, editor, log);
        try
        {
            var focus = root.accFocus;
            var focusedObject = focus as IAccessible;
            if (focusedObject != null) DescribeObject(label + " focus", focusedObject, 0, editor, log);
            else if (focus is int) DescribeObject(label + " focus-child", root, focus, editor, log);
            else log(label + " focus=none");
        }
        catch (COMException ex) { log(label + " focus hr=0x" + ex.HResult.ToString("X8")); }
    }
    private static void DescribeObject(string label, IAccessible element, object child, System.Windows.Rect editor, Action<string> log)
    {
        try
        {
            var role = element.get_accRole(child);
            var state = element.get_accState(child);
            int x, y, w, h;
            element.accLocation(out x, out y, out w, out h, child);
            var mapped = !editor.IsEmpty && Math.Abs(editor.X - x) <= 1 && Math.Abs(editor.Y - y) <= 1 &&
                Math.Abs(editor.Width - w) <= 1 && Math.Abs(editor.Height - h) <= 1;
            log(label + " role=" + FormatNumber(role) + " state=" + FormatNumber(state) +
                " rect=" + x + "," + y + "," + w + "," + h + " matchesEditorBounds=" + mapped);
            if (mapped)
            {
                try
                {
                    var value = element.get_accValue(child);
                    log(label + " get_accValue=" + (value == null ? "null" : value.Length == 0 ? "empty" : "nonempty"));
                }
                catch (COMException ex) { log(label + " get_accValue hr=0x" + ex.HResult.ToString("X8")); }
            }
        }
        catch (COMException ex) { log(label + " hr=0x" + ex.HResult.ToString("X8")); }
    }
    private static string FormatNumber(object value)
    {
        return value is int ? "0x" + ((int)value).ToString("X") : "[non-numeric]";
    }
}
