using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class Cover : Form
{
    internal Cover()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(35, 92, 130);
        TopMost = true;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
    }
    internal void CoverRect(Rectangle rect)
    {
        Bounds = rect;
        if (!Visible) Show();
        if (!Native.SetWindowPos(Handle, Native.Topmost, rect.X, rect.Y, rect.Width, rect.Height, Native.NoActivate))
            throw new System.ComponentModel.Win32Exception();
    }
}

internal sealed class SideEffects : IDisposable
{
    private readonly Thread observer;
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly IntPtr foreground = Native.GetForegroundWindow();
    private readonly uint clipboard = Native.GetClipboardSequenceNumber();
    private readonly Native.Point cursor;
    private bool changedForeground, movedCursor, changedClipboard;
    internal SideEffects()
    {
        Native.GetCursorPos(out cursor);
        observer = new Thread(() => { while (!stop.WaitOne(12)) Observe(); }) { IsBackground = true };
        observer.Start();
    }
    private void Observe()
    {
        Native.Point p;
        changedForeground |= Native.GetForegroundWindow() != foreground;
        movedCursor |= Native.GetCursorPos(out p) && (p.X != cursor.X || p.Y != cursor.Y);
        changedClipboard |= Native.GetClipboardSequenceNumber() != clipboard;
    }
    internal bool FocusChanged { get { return changedForeground || Native.GetForegroundWindow() != foreground; } }
    internal bool ClipboardChanged { get { return changedClipboard; } }
    internal bool CursorChanged { get { return movedCursor; } }
    public void Dispose()
    {
        stop.Set();
        observer.Join();
        stop.Dispose();
        Observe();
    }
    public override string ToString()
    {
        return Describe(false);
    }
    internal string Describe(bool keyboard)
    {
        return String.Format("foregroundChanged={0} cursorChanged={1} clipboardChanged={2} injectedKeys={3}",
            changedForeground, movedCursor, changedClipboard, keyboard ? "yes" : "no");
    }
}

internal sealed class Experiment
{
    private const string Seed = "窗口测试 基准-01";
    internal const string LiveMarker = "窗口测试 F-收02";
    private readonly Target target;
    private readonly Binding binding;
    private readonly Action<string> log;
    private readonly CancellationToken cancel;
    private readonly string runId = Guid.NewGuid().ToString("N").Substring(0, 4);
    private int sequence;
    private string remainingProbe;
    internal Experiment(Target target, Binding binding, Action<string> log, CancellationToken cancel)
    {
        this.target = target;
        this.binding = binding;
        this.log = log;
        this.cancel = cancel;
    }
    internal void Check(string step, Action assertState, bool send, int watchSeconds, string incomingMarker = null)
    {
        cancel.ThrowIfCancellationRequested();
        var r = Native.Bounds(target.Hwnd);
        log(String.Format("{0} start rect={1},{2},{3},{4} iconic={5} foreground={6}",
            step, r.X, r.Y, r.Width, r.Height, Native.IsIconic(target.Hwnd), Native.ForegroundLabel(target.Hwnd)));
        var effects = new SideEffects();
        try
        {
            assertState();
            var controls = Controls.Scan(target);
            log(step + " controls " + controls.Describe());
            if (binding == null || !binding.Matches(controls))
            {
                log(step + " read=not-tested; new=not-tested; write=not-tested; send=not-tested; reason=" +
                    (binding == null ? "no authorized chat binding" : "chat identity unavailable in current tree"));
                return;
            }
            var marker = incomingMarker ?? "窗口测试 收-" + step + "-" + runId;
            var text = String.Format("窗口测试 {0}-{1:D2}-{2}", step, ++sequence, runId);
            var first = controls.Messages(Seed, marker, text);
            log(String.Format("{0} read={1} rows={2} textRows={3} seededMarker={4}", step,
                first.TextRows != 0 ? "confirmed" : first.AreaPresent ? "no text exposed" : "message area missing",
                first.Rows, first.TextRows, first.Evidence(Seed)));
            if (watchSeconds > 0)
            {
                log(step + " incoming marker: " + marker);
                Watch(step, assertState, first, marker, watchSeconds);
            }
            else log(step + " independent-incoming=not-tested; no second-device message");
            if (!send)
            {
                log(step + " write=not-tested send=not-tested");
                return;
            }
            AssertStable(assertState, effects);
            Transmit(step, first, text, assertState, effects);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log(String.Format("{0} error={1} (0x{2:X8})", step, Failure(ex), ex.HResult));
        }
        finally
        {
            effects.Dispose();
            log(step + " operation " + effects);
        }
    }
    private void Watch(string step, Action state, MessageSnapshot first, string marker, int seconds)
    {
        if (first.Contains(marker))
        {
            log(step + " independent-incoming=not-tested; marker already present");
            return;
        }
        int added = 0;
        int stale = 0;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < seconds)
        {
            Wait(350);
            state();
            MessageSnapshot sample;
            try { sample = binding.RequireMatch().Messages(marker); }
            catch (ElementNotAvailableException)
            {
                stale++;
                continue;
            }
            added = Math.Max(added, sample.AddedComparedTo(first));
            if (sample.Contains(marker))
            {
                log(step + " independent-incoming=confirmed marker=" + sample.Evidence(marker) + " transient-stale=" + stale);
                return;
            }
        }
        log(String.Format("{0} independent-incoming=marker not observed; snapshot additions={1}; transient-stale={2}", step, added, stale));
    }
    private void Transmit(string step, MessageSnapshot first, string text, Action state, SideEffects effects)
    {
        if (!binding.Self || (remainingProbe != null && !ClearProbe()))
        {
            log(step + " write=blocked send=not-tested; reason=self scope or previous probe not cleared");
            return;
        }
        var phase = "write";
        var invoked = false;
        try
        {
            var ui = binding.RequireMatch();
            var input = ui.Input();
            if (!ui.HasMessageArea || !input.Writable)
            {
                log(step + " write=skipped; call=no; reason=message area or writable pattern unavailable");
                log(step + " send=not-attempted");
                return;
            }
            if (input.Read() != "" || first.Contains(text))
            {
                log(step + " write=skipped; call=no; reason=empty draft or unique marker unavailable");
                log(step + " send=not-attempted");
                return;
            }
            AssertStable(state, effects);
            log(step + " probe=" + text);
            remainingProbe = text;
            input.Set(text);
            Wait(240);
            ui = binding.RequireMatch();
            input = ui.Input();
            if (input.Read() != text)
                throw new InvalidOperationException("SetValue 后回读与测试文本不一致");
            log(step + " write=confirmed via " + input.Method);
            phase = "send";
            var invoke = ui.Invoke;
            if (invoke == null || !ui.Button.Current.IsEnabled)
                throw new InvalidOperationException("发送按钮不可用或无 InvokePattern");
            AssertStable(state, effects);
            if (input.Read() != text) throw new InvalidOperationException("待发送文本已变更");
            invoked = true;
            invoke.Invoke();
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 3600)
            {
                Wait(225);
                AssertStable(state, effects);
                ui = binding.RequireMatch();
                if (ui.Input().Read() == "" && ui.Messages(text).Contains(text))
                {
                    remainingProbe = null;
                    log(step + " send=confirmed; new-local-message=confirmed");
                    return;
                }
            }
            log(step + " send=invoked-unconfirmed; no retry");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log(String.Format("{0} {1}={2}; reason={3} (0x{4:X8})", step, phase,
                invoked ? "invoked-unconfirmed" : "failed", Failure(ex), ex.HResult));
            if (phase == "write") log(step + " send=not-attempted");
        }
        finally
        {
            if (remainingProbe != null)
                log(step + " test draft cleanup=" + (ClearProbe() ? "checked" : "pending; use stop/restore and inspect QQ"));
        }
    }
    private void AssertStable(Action state, SideEffects effects)
    {
        cancel.ThrowIfCancellationRequested();
        state();
        if (effects.FocusChanged) throw new InvalidOperationException("采样期间前台窗口发生变化");
    }
    internal bool ClearProbe()
    {
        if (remainingProbe == null) return true;
        try
        {
            var ui = binding.RequireMatch();
            var input = ui.Input();
            var current = input.Read();
            if (current == null) return false;
            if (current == remainingProbe)
            {
                if (!input.Writable) return false;
                input.Set("");
                if (binding.RequireMatch().Input().Read() != "") return false;
            }
            remainingProbe = null;
            return true;
        }
        catch (Exception) { return false; }
    }
    internal void Wait(int milliseconds)
    {
        if (cancel.WaitHandle.WaitOne(milliseconds)) cancel.ThrowIfCancellationRequested();
    }
    internal static string Failure(Exception ex)
    {
        return ex.GetType() == typeof(InvalidOperationException) ? ex.Message : ex.GetType().Name;
    }
}
