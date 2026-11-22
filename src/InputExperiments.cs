using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

internal sealed partial class InputExperiments
{
    private readonly Binding binding;
    private readonly Target target;
    private readonly ForegroundTrace trace;
    private readonly Action<string> log;
    private readonly CancellationToken cancel;
    private readonly string batch = Guid.NewGuid().ToString("N").Substring(0, 8);
    internal string OutstandingDraft { get; private set; }

    internal InputExperiments(Binding binding, ForegroundTrace trace, Action<string> log, CancellationToken cancel)
    {
        if (!binding.Self) throw new InvalidOperationException("输入实验需要已核实的自聊");
        this.binding = binding;
        this.trace = trace;
        this.log = log;
        this.cancel = cancel;
        target = binding.Target;
    }
    private void Wait(int ms)
    {
        if (cancel.WaitHandle.WaitOne(ms)) cancel.ThrowIfCancellationRequested();
    }
    private static bool ExactDraft(string actual, string expected)
    {
        return actual != null && String.Equals(actual, expected, StringComparison.Ordinal);
    }
    private void AssertForeground()
    {
        cancel.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != target.Hwnd || Native.IsIconic(target.Hwnd))
            throw new InvalidOperationException("QQ 前台条件不成立");
    }
    private void RequireInputFocus(Controls ui)
    {
        AssertForeground();
        if (!ui.EditorFocused()) throw new InvalidOperationException("未能验证焦点在绑定的 QQ 编辑器内");
    }
    private Controls Fresh()
    {
        for (int remaining = 2; ; remaining--)
        {
            try { return binding.RequireMatch(); }
            catch (ElementNotAvailableException)
            {
                if (remaining == 0) throw;
                Wait(180);
            }
        }
    }
    private T ReadFresh<T>(Func<Controls, T> read)
    {
        for (int remaining = 3; ; remaining--)
        {
            try { return read(binding.RequireMatch()); }
            catch (ElementNotAvailableException)
            {
                if (remaining == 0) throw;
                Wait(240);
            }
        }
    }
    internal void FinishKnownDraft(string label, string marker)
    {
        AssertForeground();
        var ui = Fresh();
        if (!ExactDraft(ui.Draft(), marker) || ui.Messages(marker).Contains(marker))
            throw new InvalidOperationException("裁剪下不能复核同一测试草稿或编号已在历史中");
        log(label + " write=confirmed-existing-draft; no retyping; marker=" + marker);
        AssertForeground();
        ui = Fresh();
        if (!ExactDraft(ui.Draft(), marker) || ui.Messages(marker).Contains(marker))
            throw new InvalidOperationException("发送前测试草稿或编号已变化");
        var events = trace.Count;
        bool keyboard = false;
        ActivateSend(label, ui, false, marker, ref keyboard);
        VerifySent(label, marker, marker);
        log(label + " focusDeltaAfterInvoke=" + (trace.Count - events) + " injectedKeys=no");
    }
    internal void CleanupKnownDraft(string marker)
    {
        try
        {
            var ui = Fresh();
            if (!ExactDraft(ui.Draft(), marker))
            {
                log("probe cleanup=" + (ui.EmptyDraft() ? "already-empty" : "not exact; left untouched"));
                return;
            }
            AssertForeground();
            var mapped = MsaaProbe.FindMappedEditor(target, ui);
            if (mapped != null)
            {
                var hr = MsaaProbe.PutValue(mapped, "");
                Wait(350);
                var cleared = ReadFresh(state => state.EmptyDraft());
                log("probe cleanup MSAA hr=0x" + hr.ToString("X8") + " empty=" + cleared);
                if (cleared) return;
                ui = Fresh();
                if (!ExactDraft(ui.Draft(), marker)) return;
            }
            if (!ui.EditorFocused())
            {
                ui.Editor.SetFocus();
                Wait(220);
            }
            ui = Fresh();
            RequireInputFocus(ui);
            if (!ExactDraft(ui.Draft(), marker)) throw new InvalidOperationException("清理前草稿已变化");
            Native.SelectAllText(target.Hwnd);
            Wait(120);
            ui = Fresh();
            RequireInputFocus(ui);
            var ranges = Controls.Pattern<TextPattern>(ui.Editor, TextPattern.Pattern);
            if (ranges == null || !ranges.GetSelection().Any(range =>
                range.GetText(4096).Replace("\r\n", "\n").TrimEnd('\r', '\n', '\u200B') == marker))
                throw new InvalidOperationException("无法确认只选中了测试草稿");
            Native.Backspace(target.Hwnd, 1);
            Wait(250);
            log("probe cleanup exact-known=" + Fresh().EmptyDraft());
        }
        catch (Exception ex) { log("probe cleanup=needs-manual-review " + Experiment.Failure(ex)); }
    }
    internal void Topology(string label, Controls ui)
    {
        var focus = Native.FocusWindow(target.Hwnd);
        log(label + " native-focus hwnd=" + focus.ToInt64() + " class=" + Native.Class(focus));
        int index = 0;
        foreach (var node in ui.EditorTopology())
        {
            var current = node.Current;
            var handle = new IntPtr(current.NativeWindowHandle);
            var relation = node.Equals(ui.Editor) ? "editor" : node.Equals(ui.EditorContainer) ? "container" : "related";
            var value = Controls.Pattern<ValuePattern>(node, ValuePattern.Pattern);
            string legacyDescription = "absent";
            try
            {
                var legacy = ui.Legacy(node);
                if (legacy != null)
                {
                    legacyDescription = String.Format("role=0x{0:X},state=0x{1:X},child={2},writeEligible={3}",
                        legacy.CurrentRole(), legacy.CurrentState(), legacy.CurrentChildId(), LegacyEditor.Writable(legacy));
                    try
                    {
                        var direct = legacy.GetIAccessible();
                        if (direct == null) legacyDescription += ",msaa=null";
                        else
                        {
                            var child = legacy.CurrentChildId();
                            legacyDescription += ",msaa-role=" + direct.get_accRole(child) + ",msaa-state=" + direct.get_accState(child);
                        }
                    }
                    catch (COMException ex) { legacyDescription += ",msaa-hr=0x" + ex.HResult.ToString("X8"); }
                }
            }
            catch (COMException ex) { legacyDescription = "hr=0x" + ex.HResult.ToString("X8"); }
            log(String.Format("{0} topology {1} {2} type={3} class={4} hwnd={5} nativeClass={6} focusable={7} hasFocus={8} value={9} text={10} legacy={11}",
                label, ++index, relation, current.ControlType.ProgrammaticName, current.ClassName,
                handle.ToInt64(), Native.Class(handle), current.IsKeyboardFocusable, current.HasKeyboardFocus,
                value == null ? "missing" : value.Current.IsReadOnly ? "read-only" : "writable",
                Controls.Pattern<TextPattern>(node, TextPattern.Pattern) != null, legacyDescription));
        }
        Native.EnumChildWindows(target.Hwnd, (h, d) => {
            var kind = Native.Class(h);
            if (kind == "Chrome_RenderWidgetHostHWND" || Native.IsDedicatedEdit(target.Hwnd, h))
                log(String.Format("{0} native-child hwnd={1} class={2} focused={3}", label, h.ToInt64(), kind, h == focus));
            return true;
        }, IntPtr.Zero);
    }
    internal bool Foreground(string label, bool inspect = false, bool multiline = false)
    {
        var marker = "小窗复测 " + label + "-" + batch;
        var body = multiline ? marker + "\n中文第二行" : marker;
        var events = trace.Count;
        var effects = new SideEffects();
        bool keyboard = false, written = false, sendAttempted = false;
        string potentialDraft = null;
        try
        {
            AssertForeground();
            var ui = binding.RequireMatch();
            if (inspect) Topology(label + "-before-focus", ui);
            var before = ui.Messages(marker, "窗口测试 基准-01");
            log(label + " stage=foreground-control rows=" + before.TextRows + " seeded=" + before.Evidence("窗口测试 基准-01"));
            if (!before.AreaPresent || before.Contains(marker) || !ui.EmptyDraft())
                throw new InvalidOperationException("无法确认编号尚未出现且输入框为空");
            if (!ui.EditorFocused())
            {
                log(label + " editor-focus setup=UIA.SetFocus (foreground only)");
                ui.Editor.SetFocus();
                Wait(350);
            }
            ui = Fresh();
            RequireInputFocus(ui);
            if (inspect) Topology(label + "-focused", ui);
            if (!ui.EmptyDraft()) throw new InvalidOperationException("输入框在聚焦后不为空");
            log(label + " input_call=SendInput-unicode marker=" + marker);
            keyboard = true;
            potentialDraft = marker;
            Native.TypeUnicode(target.Hwnd, marker);
            Wait(260);
            ui = Fresh();
            log(label + " after-input nativeForeground=" + Native.GetForegroundWindow().ToInt64() +
                " foregroundEvents=" + (trace.Count - events));
            if (!ExactDraft(ui.Draft(), marker))
                throw new InvalidOperationException("普通输入后未读回预期文字");
            if (multiline)
            {
                RequireInputFocus(ui);
                log(label + " newline_call=Shift+Enter");
                Native.PressEnter(target.Hwnd, 0x10);
                Wait(180);
                ui = Fresh();
                RequireInputFocus(ui);
                if (!ExactDraft(ui.Draft(), marker) || ui.Messages(marker).Contains(marker))
                    throw new InvalidOperationException("换行快捷键改变了会话或提交了第一行");
                potentialDraft = body;
                Native.TypeUnicode(target.Hwnd, "中文第二行");
                Wait(240);
                ui = Fresh();
            }
            written = ExactDraft(ui.Draft(), body);
            log(label + " write=" + (written ? "confirmed-exact" : "readback-mismatch") + " expectedLines=" + (multiline ? 2 : 1));
            if (!written) return false;
            AssertForeground();
            ui = Fresh();
            if (!ExactDraft(ui.Draft(), body) || ui.Messages(marker).Contains(marker))
                throw new InvalidOperationException("发送前未能复核编号与草稿");
            sendAttempted = true;
            ActivateSend(label, ui, true, body, ref keyboard);
            var ok = VerifySent(label, marker, body);
            if (ok) potentialDraft = null;
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log(label + " error=" + Experiment.Failure(ex) + " hr=0x" + ex.HResult.ToString("X8"));
            return false;
        }
        finally
        {
            if (!written) log(label + " write=not-confirmed");
            if (!sendAttempted) log(label + " send=not-tested");
            if (potentialDraft != null)
            {
                var residue = InspectResidue(potentialDraft, !sendAttempted);
                if (residue == "inspect-manually") OutstandingDraft = potentialDraft;
                log(label + " residue=" + residue);
            }
            effects.Dispose();
            log(label + " " + effects.Describe(keyboard) + " foregroundEvents=" + (trace.Count - events));
        }
    }
    private string InspectResidue(string expected, bool canErase)
    {
        try
        {
            var ui = binding.RequireMatch();
            var draft = ui.Draft();
            if (draft == "") return "empty";
            if (canErase && ExactDraft(draft, expected) && Native.GetForegroundWindow() == target.Hwnd && ui.EditorFocused())
            {
                Native.Backspace(target.Hwnd, expected.Length);
                Thread.Sleep(180);
                return binding.RequireMatch().Draft() == "" ? "exact-probe-removed" : "inspect-manually";
            }
        }
        catch (Exception) { }
        return "inspect-manually";
    }
    private void ActivateSend(string label, Controls ui, bool allowKeyboard, string expected, ref bool keyboard)
    {
        if (ui.Button == null || !ui.Button.Current.IsEnabled)
            throw new InvalidOperationException("发送按钮未启用");
        var invoke = ui.Invoke;
        if (invoke != null)
        {
            log(label + " send_call=InvokePattern");
            invoke.Invoke();
            return;
        }
        var legacy = ui.Legacy(ui.Button);
        if (legacy != null && !String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()) && (legacy.CurrentState() & 1) == 0)
        {
            log(label + " send_call=MSAA-default");
            Marshal.ThrowExceptionForHR(legacy.DoDefaultAction());
            return;
        }
        if (!allowKeyboard) throw new InvalidOperationException("没有可用的后台发送动作");
        RequireInputFocus(ui);
        if (!ExactDraft(ui.Draft(), expected)) throw new InvalidOperationException("发送前草稿发生变化");
        log(label + " send_call=SendInput-Enter (foreground)");
        keyboard = true;
        Native.PressEnter(target.Hwnd, 0);
    }
    private bool VerifySent(string label, string marker, string body)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            Wait(160);
            try
            {
                var ui = binding.RequireMatch();
                var snapshot = ui.Messages(marker, body);
                if (snapshot.Contains(body))
                {
                    log(label + " send=local-confirmed marker=" + snapshot.Evidence(marker) +
                        " body=" + snapshot.Evidence(body) + " matchingRows=" + snapshot.MarkerRows(body) +
                        " draftEmpty=" + (ui.Draft() == "") + " phone=not-checked");
                    return true;
                }
            }
            catch (ElementNotAvailableException) { }
        } while (timer.ElapsedMilliseconds < 12000);
        log(label + " send=attempted-unconfirmed; no-retry");
        return false;
    }
}
