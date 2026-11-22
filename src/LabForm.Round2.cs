using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed partial class LabForm
{
    private void StartFrontBaseline()
    {
        var scope = binding;
        if (scope == null || !scope.Self) throw new InvalidOperationException("请先绑定已核实自聊");
        Write("4 秒内切回正常大小、无遮挡的 QQ 自聊。本项会使用系统键盘。");
        StartJob(async token => {
            await Task.Delay(4000, token);
            await FrontBaseline(scope, token);
        });
    }
    private void StartRound2()
    {
        var scope = binding;
        if (scope == null || !scope.Self) throw new InvalidOperationException("请先绑定已核实自聊");
        if (MessageBox.Show(this, "前台阶段会用系统键盘发送固定自聊测试文字。后台阶段将切到专用便笺。开始？",
            Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var seconds = (int)watch.Value;
        Write("4 秒内切回 QQ 自聊。");
        StartJob(async token => {
            await Task.Delay(4000, token);
            await Round2Matrix(scope, token, seconds, seconds > 0 ? "小窗复测 来信-" + Guid.NewGuid().ToString("N").Substring(0, 6) : null);
        });
    }
    internal void Round2ExistingSelf(long hwnd, bool matrix, bool region, bool watchF, string resumeClip,
        bool msaaOnly, bool msaaWrite, bool msaaDefault, string previousDraft, bool fullOcclusionWitness)
    {
        SelectTarget(hwnd);
        var target = Selected();
        Write("等待上一轮窗体验证：同一 HWND、进程与手机基准编号。");
        StartJob(async token => {
            for (int n = 0; Native.GetForegroundWindow() != target.Hwnd && n < 150; n++)
                await Task.Delay(400, token);
            if (Native.GetForegroundWindow() != target.Hwnd) throw new InvalidOperationException("QQ 前台等待超时");
            await Task.Delay(450, token);
            var scope = await Task.Run(() => EnrollPreviousSelf(target, token), token);
            binding = scope;
            if (fullOcclusionWitness) await BackgroundSendComparison(scope, token, previousDraft, true);
            else if (msaaDefault) await BackgroundSendComparison(scope, token, previousDraft);
            else if (msaaOnly) await DirectMsaaWitness(scope, token, msaaWrite);
            else if (region) await RegionExperiment(scope, token, resumeClip);
            else if (matrix) await Round2Matrix(scope, token, watchF ? 95 : 0, watchF ? "小窗复测 来信-F21" : null);
            else await FrontBaseline(scope, token);
        });
    }
    private Binding EnrollPreviousSelf(Target target, CancellationToken token)
    {
        for (int remaining = 4; ; remaining--)
        {
            token.ThrowIfCancellationRequested();
            if (Native.GetForegroundWindow() != target.Hwnd) throw new InvalidOperationException("绑定前台已变更");
            try
            {
                var candidate = new Binding(target, true);
                if (!candidate.RequireMatch().Messages("窗口测试 基准-01").Contains("窗口测试 基准-01"))
                    throw new InvalidOperationException("上一轮自聊基准编号未命中，停止输入");
                return candidate;
            }
            catch (ElementNotAvailableException)
            {
                if (remaining == 0) throw;
                Write("绑定读取遇到失效节点，重新取树");
                if (token.WaitHandle.WaitOne(350)) token.ThrowIfCancellationRequested();
            }
        }
    }
    private async Task BackgroundSendComparison(Binding scope, CancellationToken token, string previousDraft = null, bool fOnly = false)
    {
        var target = scope.Target;
        CapturePlacement(target);
        var owner = Handle;
        try
        {
            using (var trace = new ForegroundTrace())
            {
                var experiment = new InputExperiments(scope, trace, Write, token);
                if (previousDraft != null)
                    await Task.Run(() => experiment.CleanupKnownDraft(previousDraft), token);
                await OpenScratch(target, token);
                var continued = InputExperiments.BackgroundSender.MsaaDefault;
                if (previousDraft == null && !fOnly)
                {
                    await BackgroundCase(experiment, "R2-ALT-E", owner, false, token, sender: continued);
                    if (!experiment.BackgroundSendPreservedFocus)
                        continued = InputExperiments.BackgroundSender.DraftOnly;
                }
                await EnsureScratchFront(target, token);
                var original = Native.Bounds(target.Hwnd);
                ResizeTo(target, new Rectangle(original.Location, new Size(480, 360)));
                Write("R2-ALT-F " + SizeReport(target));
                ShowCover(target, true);
                await Task.Delay(950, token);
                if (Native.Above(cover.Handle, target.Hwnd) &&
                    Rectangle.Intersect(Native.Bounds(target.Hwnd), Native.Bounds(cover.Handle)) == Native.Bounds(target.Hwnd))
                    await BackgroundCase(experiment, fOnly ? "R2-F-REPEAT" : "R2-ALT-F", owner, false, token,
                        fOnly ? "小窗复测 实时-F22" : null, fOnly ? 70 : 0, continued, fOnly);
                if (fOnly)
                {
                    Write("R2 repeat foreground WinEvents=" + trace.Summary());
                    return;
                }
                if (!experiment.BackgroundSendPreservedFocus) continued = InputExperiments.BackgroundSender.DraftOnly;
                HideCover();
                await EnsureScratchFront(target, token);
                Native.ShowWindowAsync(target.Hwnd, Native.ShowMinimizedNoActivate);
                await Task.Delay(450, token);
                Write("R2-ALT-G iconic=" + Native.IsIconic(target.Hwnd));
                if (Native.IsIconic(target.Hwnd))
                    await BackgroundCase(experiment, "R2-ALT-G", owner, false, token, sender: continued);
                Write("R2 send comparison foreground WinEvents=" + trace.Summary());
            }
        }
        finally { Restore(); }
    }
    private async Task EnsureScratchFront(Target target, CancellationToken token)
    {
        if (scratch != null && Native.GetForegroundWindow() == scratch.Hwnd) return;
        Write("下一项设置阶段重新将专用便笺置前");
        if (scratch != null) scratch.Dispose();
        await OpenScratch(target, token);
    }
    private async Task DirectMsaaWitness(Binding scope, CancellationToken token, bool write)
    {
        try
        {
            using (var trace = new ForegroundTrace())
            {
                await Task.Run(() => MsaaProbe.Describe(scope.Target, scope.RequireMatch(), line => Write("front " + line)), token);
                await OpenScratch(scope.Target, token);
                var pad = scratch.Hwnd;
                if (Native.GetForegroundWindow() != pad) throw new InvalidOperationException("便笺未获得前台");
                var start = trace.Count;
                await Task.Run(() => MsaaProbe.Describe(scope.Target, scope.RequireMatch(), line => Write("back " + line)), token);
                if (write)
                    await BackgroundCase(new InputExperiments(scope, trace, Write, token), "R2-MSAA-E", Handle, false, token);
                Write("MSAA background pad stayed front=" + (Native.GetForegroundWindow() == pad) +
                    " foregroundEvents=" + (trace.Count - start) + " keyboard=no clipboard=no");
            }
        }
        finally { Restore(); }
    }
    private async Task FrontBaseline(Binding scope, CancellationToken token)
    {
        HideCover();
        using (var trace = new ForegroundTrace())
        {
            var target = scope.Target;
            Write("R2 baseline hwnd=" + target.Hwnd.ToInt64() + " pid=" + target.Pid +
                " outer=" + Native.Bounds(target.Hwnd).Size + " client=" + Native.ClientSize(target.Hwnd) +
                " DPI=" + Native.GetDpiForWindow(target.Hwnd));
            var experiment = new InputExperiments(scope, trace, Write, token);
            var ok = await Task.Run(() => experiment.Foreground("R2-A", true), token);
            Write("R2 baseline=" + ok + " foreground WinEvents=" + trace.Summary());
        }
    }
    private async Task Round2Matrix(Binding scope, CancellationToken token, int watchSeconds, string marker)
    {
        var target = scope.Target;
        CapturePlacement(target);
        var owner = Handle;
        try
        {
            using (var trace = new ForegroundTrace())
            {
                var experiment = new InputExperiments(scope, trace, Write, token);
                var normal = Native.Bounds(target.Hwnd);
                Write("R2 matrix original " + SizeReport(target));
                var baseline = await Task.Run(() => experiment.Foreground("R2-A", true), token);
                if (!baseline) { Write("R2 matrix halted: front baseline not confirmed"); return; }
                ResizeTo(target, new Rectangle(normal.Location, new Size(480, 360)));
                var small = Native.Bounds(target.Hwnd);
                Write("R2-B request=480x360 " + SizeReport(target));
                await Task.Run(() => experiment.Foreground("R2-B"), token);
                ResizeTo(target, normal);
                ShowCover(target, true);
                if (Native.Visible(cover.Handle) && Native.Above(cover.Handle, target.Hwnd) &&
                    Rectangle.Intersect(Native.Bounds(target.Hwnd), Native.Bounds(cover.Handle)) == Native.Bounds(target.Hwnd))
                    await Task.Run(() => experiment.Foreground("R2-D"), token);
                else Write("R2-D skipped: full cover not verified");
                HideCover();
                await Task.Run(() => experiment.Foreground("R2-MULTI", false, true), token);
                await OpenScratch(target, token);
                var backendSender = InputExperiments.BackgroundSender.MsaaDefault;
                await BackgroundCase(experiment, "R2-E", owner, true, token, sender: backendSender);
                if (!experiment.BackgroundSendPreservedFocus)
                    backendSender = InputExperiments.BackgroundSender.DraftOnly;
                await EnsureScratchFront(target, token);
                ResizeTo(target, small);
                ShowCover(target, true);
                await Task.Delay(950, token);
                if (Native.Visible(cover.Handle) && Native.Above(cover.Handle, target.Hwnd) &&
                    Rectangle.Intersect(Native.Bounds(target.Hwnd), Native.Bounds(cover.Handle)) == Native.Bounds(target.Hwnd))
                    await BackgroundCase(experiment, "R2-F", owner, false, token, marker, watchSeconds, backendSender);
                else Write("R2-F skipped: full cover not verified");
                if (!experiment.BackgroundSendPreservedFocus)
                    backendSender = InputExperiments.BackgroundSender.DraftOnly;
                HideCover();
                await EnsureScratchFront(target, token);
                Native.ShowWindowAsync(target.Hwnd, Native.ShowMinimizedNoActivate);
                await Task.Delay(450, token);
                if (Native.IsIconic(target.Hwnd)) await BackgroundCase(experiment, "R2-G", owner, false, token, sender: backendSender);
                else Write("R2-G skipped: minimization not verified");
                Write("R2 matrix foreground WinEvents=" + trace.Summary());
            }
        }
        finally { Restore(); }
    }
    private async Task BackgroundCase(InputExperiments experiment, string label, IntPtr owner, bool inspect,
        CancellationToken token, string marker = null, int seconds = 0,
        InputExperiments.BackgroundSender sender = InputExperiments.BackgroundSender.PreferInvoke, bool multiline = false)
    {
        var pad = scratch.Hwnd;
        var edits = scratch.Edits;
        Write(label + " pad start hwnd=" + pad.ToInt64() + " foreground=" + Native.GetForegroundWindow().ToInt64() +
            " keyboardFocus=" + Native.HasChildFocus(pad));
        try { await Task.Run(() => experiment.Background(label, pad, owner, inspect, marker, seconds, sender, multiline), token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Write(label + " stopped: " + Experiment.Failure(ex)); }
        finally
        {
            Write(label + " pad end foreground=" + (Native.GetForegroundWindow() == pad) +
                " keyboardFocus=" + Native.HasChildFocus(pad) + " textChanges=" + (scratch.Edits - edits));
        }
    }
    private static string SizeReport(Target window)
    {
        var dpi = Native.GetDpiForWindow(window.Hwnd);
        return "outer=" + Native.Bounds(window.Hwnd).Size + " client=" + Native.ClientSize(window.Hwnd) +
            " DPI=" + dpi + " scale=" + (dpi * 100 / 96) + "%";
    }
}
