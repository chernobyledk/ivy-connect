using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal sealed partial class InputExperiments
{
    internal enum BackgroundSender { PreferInvoke, MsaaDefault, DraftOnly }

    internal bool BackgroundSendPreservedFocus { get; private set; }
    internal void Background(string label, IntPtr pad, IntPtr clipboardOwner, bool inspect, string incoming = null,
        int watchSeconds = 0, BackgroundSender sender = BackgroundSender.PreferInvoke, bool multiline = false)
    {
        BackgroundSendPreservedFocus = false;
        var events = trace.Count;
        var effects = new SideEffects();
        Action guard = () => {
            cancel.ThrowIfCancellationRequested();
            if (pad == IntPtr.Zero || Native.GetForegroundWindow() != pad || !Native.HasChildFocus(pad) ||
                trace.Count != events)
                throw new InvalidOperationException("专用便笺的前台或编辑焦点已变化");
        };
        try
        {
            guard();
            var ui = Fresh();
            if (inspect)
            {
                Topology(label, ui);
                MsaaProbe.Describe(target, ui, line => log(label + " " + line));
            }
            var baseline = incoming == null ? ui.Messages("窗口测试 基准-01") : ui.Messages("窗口测试 基准-01", incoming);
            log(label + " stage=background rows=" + baseline.TextRows + " seeded=" + baseline.Evidence("窗口测试 基准-01") +
                " editorNativeHwnd=" + (ui.Editor == null ? 0 : ui.Editor.Current.NativeWindowHandle));
            if (watchSeconds > 0 && incoming != null)
            {
                WatchIncoming(label, incoming, watchSeconds, baseline, guard);
                guard();
                ui = Fresh();
            }
            var legacy = ui.Legacy(ui.Editor);
            if (LegacyEditor.Writable(legacy))
            {
                if (TryBackgroundWrite(label, "Legacy.SetValue", guard, marker => {
                    Marshal.ThrowExceptionForHR(LogLegacyResult(label, legacy.SetValue(marker)));
                }, sender, multiline)) return;
            }
            else log(label + " Legacy.SetValue=not-applicable; no-call; editor has no eligible accessibility write role");
            if (ui.EmptyDraft() && MsaaProbe.FindMappedEditor(target, ui) != null)
            {
                if (TryBackgroundWrite(label, "MSAA.put_accValue", guard, marker => {
                    var mapped = MsaaProbe.FindMappedEditor(target, Fresh());
                    if (mapped == null) throw new InvalidOperationException("MSAA 焦点对象已不匹配编辑器");
                    guard();
                    log(label + " direct MSAA.put_accValue call=mapped stable editor marker=" + marker.Replace("\n", "\\n"));
                    var hr = MsaaProbe.PutValue(mapped, marker);
                    log(label + " direct MSAA.put_accValue hr=0x" + hr.ToString("X8"));
                }, sender, multiline)) return;
            }
            else log(label + " direct MSAA.put_accValue=not-applicable; mapped accessible object or empty draft unavailable");
            var receiver = ui.NativeEditor(target);
            if (receiver == IntPtr.Zero)
            {
                log(label + " WM_PASTE=not-applicable; no dedicated native editor HWND; clipboard untouched");
                log(label + " WM_CHAR=not-applicable; no dedicated native editor HWND");
            }
            else
            {
                log(label + " nativeReceiver=" + receiver.ToInt64() + " class=" + Native.Class(receiver));
                if (TryBackgroundWrite(label, "WM_PASTE", guard, marker => {
                    using (var clipboard = new TextClipboardLease(clipboardOwner, marker, log))
                    {
                        guard();
                        if (!clipboard.Current || binding.RequireMatch().NativeEditor(target) != receiver)
                            throw new InvalidOperationException("剪贴板或接收 HWND 已变化");
                        log(label + " WM_PASTE " + Native.TimedMessage(receiver, 0x0302, 0));
                    }
                }, sender, multiline)) return;
                if (TryBackgroundWrite(label, "WM_CHAR", guard, marker => {
                    if (binding.RequireMatch().NativeEditor(target) != receiver)
                        throw new InvalidOperationException("接收 HWND 已变化");
                    foreach (char c in marker)
                    {
                        guard();
                        log(label + " WM_CHAR " + Native.TimedMessage(receiver, 0x0102, c));
                    }
                }, sender, multiline)) return;
            }
            log(label + " SendInput=not-applicable-in-background; send=not-tested");
        }
        finally
        {
            effects.Dispose();
            log(label + " " + effects.Describe(false) + " foregroundEvents=" + (trace.Count - events));
        }
    }
    private int LogLegacyResult(string label, int hr)
    {
        log(label + " Legacy.SetValue hr=0x" + hr.ToString("X8"));
        return hr;
    }
    private bool TryBackgroundWrite(string label, string method, Action guard, Action<string> write,
        BackgroundSender sender, bool multiline)
    {
        var marker = "小窗复测 " + label + "-" + batch;
        var expected = multiline ? marker + "\n后台中文第二行" : marker;
        var ui = Fresh();
        if (!ui.EmptyDraft() || ui.Messages(marker).Contains(marker))
        {
            log(label + " " + method + "=skipped; no-call; draft not verified empty");
            return true;
        }
        guard();
        try { write(expected); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log(label + " " + method + " error=0x" + ex.HResult.ToString("X8") + " " + Experiment.Failure(ex)); }
        Wait(240);
        var readback = ReadFresh(state => Tuple.Create(state.Draft(), state.EmptyDraft()));
        var draft = readback.Item1;
        if (readback.Item2)
        {
            log(label + " " + method + " write=not-observed; send=not-tested");
            return false;
        }
        if (!ExactDraft(draft, expected))
        {
            log(label + " " + method + " write=not-confirmed; draft changed or unknown; send=not-tested");
            if (multiline && !String.IsNullOrEmpty(draft) &&
                (draft == expected.Replace("\n", "") || draft == expected.Replace("\n", " ")))
            {
                log(label + " explicit newline not preserved by this path");
                ClearExactMsaaDraft(label, draft, guard);
            }
            return true;
        }
        guard();
        log(label + " " + method + " write=confirmed-exact lines=" + (multiline ? 2 : 1));
        if (sender == BackgroundSender.DraftOnly)
        {
            log(label + " send=not-tested; draft-only control");
            ClearExactMsaaDraft(label, expected, guard);
            return true;
        }
        ui = ReadFresh(state => ExactDraft(state.Draft(), expected) && !state.Messages(marker).Contains(marker) ? state : null);
        if (ui == null) return true;
        guard();
        bool keyboard = false, sent = false;
        try
        {
            if (sender == BackgroundSender.MsaaDefault)
            {
                var button = ui.Button;
                var legacy = ui.Legacy(button);
                if (button == null || !button.Current.IsEnabled || legacy == null ||
                    legacy.CurrentRole() != 0x2B || (legacy.CurrentState() & 1) != 0 ||
                    String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()))
                {
                    log(label + " send=not-tested; MSAA default button action unavailable");
                    ClearExactMsaaDraft(label, expected, guard);
                    return true;
                }
                guard();
                log(label + " send_call=LegacyIAccessible.DoDefaultAction");
                var hr = legacy.DoDefaultAction();
                log(label + " MSAA default hr=0x" + hr.ToString("X8"));
                Marshal.ThrowExceptionForHR(hr);
            }
            else ActivateSend(label, ui, false, expected, ref keyboard);
            sent = VerifySent(label, marker, expected);
            guard();
            BackgroundSendPreservedFocus = sent;
            log(label + " local-send-confirmed=" + sent + " focus-preserved=True");
            if (!sent) ClearExactMsaaDraft(label, expected, guard);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log(label + " local-send-confirmed=" + sent + " background-focus-not-verified: " + Experiment.Failure(ex));
        }
        return true;
    }
    private void ClearExactMsaaDraft(string label, string marker, Action guard)
    {
        try
        {
            guard();
            var ui = binding.RequireMatch();
            if (!ExactDraft(ui.Draft(), marker)) return;
            var mapped = MsaaProbe.FindMappedEditor(target, ui);
            if (mapped == null) return;
            guard();
            var hr = MsaaProbe.PutValue(mapped, "");
            Wait(240);
            log(label + " background probe cleanup hr=0x" + hr.ToString("X8") +
                " empty=" + binding.RequireMatch().EmptyDraft());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log(label + " background probe cleanup unavailable: " + Experiment.Failure(ex)); }
    }
    private void WatchIncoming(string label, string marker, int seconds, MessageSnapshot baseline, Action guard)
    {
        if (baseline.Contains(marker)) { log(label + " incoming=already-present"); return; }
        log(label + " watch-ready marker=" + marker + " seconds=" + seconds);
        var timer = Stopwatch.StartNew();
        int stale = 0;
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            Wait(400);
            guard();
            try
            {
                var current = binding.RequireMatch().Messages(marker);
                if (current.Contains(marker))
                {
                    guard();
                    log(label + " incoming=new-local-marker evidence=" + current.Evidence(marker) +
                        " matchingRows=" + current.MarkerRows(marker) + " stale=" + stale);
                    return;
                }
            }
            catch (ElementNotAvailableException) { stale++; }
        }
        log(label + " incoming=not-observed stale=" + stale);
    }
}
