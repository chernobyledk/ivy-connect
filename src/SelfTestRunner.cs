using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

internal sealed class SelfTestResult
{
    internal int InCalls, OutCalls, InConfirmed, Seen, OutConfirmed, Passed, Failed, Duplicate, Missed, Uncertain;
    internal bool Miswrite, FocusChanged, ClipboardChanged, PointerChanged, Recovered, Stopped, KeyboardUsed;
    internal string Error;
    public override string ToString()
    {
        return String.Format("inCalls={0} outCalls={1} inConfirmed={2} listener={3} outConfirmed={4} pass={5} fail={6} duplicate={7} missed={8} uncertain={9} miswrite={10} focusChanged={11} clipboardChanged={12} pointerChanged={13} recoverableRead={14}",
            InCalls, OutCalls, InConfirmed, Seen, OutConfirmed, Passed, Failed, Duplicate, Missed, Uncertain,
            Miswrite, FocusChanged, ClipboardChanged, PointerChanged, Recovered);
    }
}

internal sealed class SelfTestRunner
{
    private readonly SelfTestIdentity identity;
    private readonly Target target;
    private readonly SelfTestProtocol protocol;
    private readonly SelfTestBudget budget;
    private readonly Action<string> log;
    private readonly CancellationToken cancel;
    private readonly bool foregroundKeyboard;
    private readonly bool foregroundEnter;
    private int sequence;
    private string pendingDraft;

    internal SelfTestRunner(SelfTestIdentity identity, SelfTestProtocol protocol, SelfTestBudget budget,
        Action<string> log, CancellationToken cancel, bool foregroundKeyboard, bool foregroundEnter)
    {
        this.identity = identity;
        target = identity.Scope.Target;
        this.protocol = protocol;
        this.budget = budget;
        this.log = log;
        this.cancel = cancel;
        this.foregroundKeyboard = foregroundKeyboard;
        this.foregroundEnter = foregroundEnter;
    }
    private void Wait(int ms)
    {
        if (cancel.WaitHandle.WaitOne(ms)) cancel.ThrowIfCancellationRequested();
    }
    private T Read<T>(Func<Controls, T> query, SelfTestResult result)
    {
        for (int n = 0; ; n++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!target.SameWindow() || Native.IsIconic(target.Hwnd) || !Native.Visible(target.Hwnd))
                throw new SelfTestHalt("QQ 绑定失效或未展开，停止发送");
            try
            {
                Controls ui;
                try { ui = identity.Scope.RequireMatch(); }
                catch (InvalidOperationException) { throw new SelfTestHalt("QQ 会话无法复核，停止发送"); }
                return query(ui);
            }
            catch (ElementNotAvailableException)
            {
                if (n == 3) throw;
                result.Recovered = true;
                Wait(200);
            }
        }
    }
    private ReplyRow[] ReadHistory(Controls ui)
    {
        var rows = ui.ReplyRows();
        identity.Validate(rows);
        return rows;
    }
    internal SelfTestResult Stage(string label, int pairs, Func<bool> permitted, ForegroundTrace trace,
        string foreignMarker, bool foregroundControl)
    {
        var result = new SelfTestResult();
        var timeline = new ReplyTimeline();
        var foregroundStart = trace.Count;
        var effects = new SideEffects();
        try
        {
            if (!permitted()) throw new InvalidOperationException("场景前台或覆盖条件未稳定");
            var before = Read(ReadHistory, result);
            Wait(230);
            timeline.Seed(before, Read(ReadHistory, result));
            log(label + " baseline rows=" + before.Length);
            for (int i = 0; i < pairs; i++)
            {
                if (budget.Used > SelfTestBudget.Cap - 2)
                {
                    log(label + " insufficient remaining budget; unscheduled pairs skipped");
                    break;
                }
                CheckFocus(permitted, trace, foregroundStart);
                int seq = ++sequence;
                var input = protocol.Input(seq);
                var output = protocol.Output(seq);
                protocol.Arm(seq);
                var readback = Send(label, input, true, timeline, result, permitted, trace, foregroundStart, foregroundControl);
                if (foregroundControl)
                {
                    log(label + " positive control locally confirmed; no automatic reply attempted");
                    result.Passed++;
                    break;
                }
                var added = timeline.Advance(readback);
                var eligible = added.Count(protocol.Consume);
                if (eligible != 1)
                {
                    result.Missed++;
                    log(label + " listener count=" + eligible + " directions=" +
                        String.Join(",", added.Select(row => row.Own.HasValue ? row.Own.Value.ToString() : "unknown")));
                    throw new InvalidOperationException("新测试输入未被监听器唯一接纳");
                }
                result.Seen++;
                log(label + " listener new TEST_IN accepted; fixed TEST_OUT queued seq=" + seq);
                readback = Send(label, output, false, timeline, result, permitted, trace, foregroundStart, false);
                if (timeline.Advance(readback).Any(protocol.Consume) || timeline.Advance(readback).Length != 0)
                {
                    result.Duplicate++;
                    throw new InvalidOperationException("重复采样或回执错误触发监听器");
                }
                if (foreignMarker != null && readback.Any(row => row.Text.Contains(foreignMarker)))
                {
                    result.Miswrite = true;
                    throw new InvalidOperationException("QQ 消息中发现便笺专用标记");
                }
                CheckFocus(permitted, trace, foregroundStart);
                result.Passed++;
                log(label + " pair-confirmed seq=" + seq);
            }
        }
        catch (OperationCanceledException) { result.Error = "已停止"; }
        catch (Exception ex)
        {
            result.Failed++;
            result.Stopped = ex is SelfTestHalt;
            result.Error = Experiment.Failure(ex);
            log(label + " failure " + result.Error);
        }
        finally
        {
            if (pendingDraft != null)
            {
                try { identity.ClearUnsubmittedDraft(pendingDraft, log); }
                catch (Exception ex) { log(label + " draft recovery unavailable: " + Experiment.Failure(ex)); }
                finally { pendingDraft = null; }
            }
            effects.Dispose();
            result.FocusChanged = trace.Count != foregroundStart || effects.FocusChanged || !permitted();
            result.ClipboardChanged = effects.ClipboardChanged;
            result.PointerChanged = effects.CursorChanged;
            log(label + " result " + result + " foregroundEvents=" + (trace.Count - foregroundStart) +
                " events-all=" + trace.Summary() + " API:activate=no mouse=no keyboard=" +
                (result.KeyboardUsed ? "yes" : "no") + " clipboard=no");
        }
        return result;
    }
    private void CheckFocus(Func<bool> permitted, ForegroundTrace trace, int start)
    {
        cancel.ThrowIfCancellationRequested();
        if (!permitted() || trace.Count != start) throw new InvalidOperationException("前台或覆盖条件在测量中变化");
    }
    private ReplyRow[] Send(string label, string body, bool input, ReplyTimeline timeline, SelfTestResult result,
        Func<bool> permitted, ForegroundTrace trace, int start, bool foregroundControl)
    {
        var expected = MessageSnapshot.Normalize(body);
        var mapped = Read(ui => {
            var rows = ReadHistory(ui);
            timeline.CheckSameConversation(rows);
            if (!ui.EmptyDraft()) throw new SelfTestHalt("输入框有草稿，停止发送");
            if (Count(rows, expected) != 0) throw new InvalidOperationException("发送前发现同编号");
            if (foregroundControl && foregroundKeyboard)
            {
                if (!ui.EditorFocused()) throw new SelfTestHalt("前台对照未确认输入焦点");
                return null;
            }
            return MsaaProbe.FindMappedEditor(target, ui);
        }, result);
        var keyboard = foregroundControl && foregroundKeyboard;
        if (!keyboard && mapped == null) throw new InvalidOperationException("MSAA 编辑对象无法复核");
        CheckFocus(permitted, trace, start);
        log(label + " write-intent " + Encoded(body));
        pendingDraft = body;
        int hr;
        if (keyboard)
        {
            result.KeyboardUsed = true;
            Native.TypeUnicode(target.Hwnd, body);
            log(label + " write-return foreground-only Unicode SendInput");
        }
        else
        {
            hr = MsaaProbe.PutValue(mapped, body);
            log(label + " write-return hr=0x" + hr.ToString("X8"));
        }
        bool draftVerified = false;
        for (int n = 0; n < 4 && !draftVerified; n++)
        {
            Wait(240);
            draftVerified = Read(ui => {
                if (ui.EmptyDraft()) return false;
                if (ui.Draft() != body) throw new SelfTestHalt("回读草稿变化，停止发送");
                return true;
            }, result);
        }
        if (!draftVerified) throw new InvalidOperationException("未确认精确草稿");
        CheckFocus(permitted, trace, start);
        log(label + " write-confirmed " + Encoded(body));
        var button = Read(ui => {
            var rows = ReadHistory(ui);
            timeline.CheckSameConversation(rows);
            if (ui.Draft() != body) throw new SelfTestHalt("提交前草稿已变化，停止发送");
            if (Count(rows, expected) != 0) throw new InvalidOperationException("提交前编号已出现");
            var node = ui.Button;
            if (foregroundControl && foregroundEnter)
            {
                if (Native.GetForegroundWindow() != target.Hwnd || !ui.EditorFocused())
                    throw new SelfTestHalt("前台 Enter 对照未确认 QQ 编辑器焦点");
                return new Func<int>(() => { Native.PressEnter(target.Hwnd, 0); return 0; });
            }
            if (foregroundControl)
            {
                if (Native.GetForegroundWindow() != target.Hwnd || node == null || !node.Current.IsEnabled)
                    throw new SelfTestHalt("前台对照的 QQ 前台或发送按钮不可用");
                var invoke = ui.Invoke;
                if (invoke == null) throw new InvalidOperationException("发送按钮未公开 InvokePattern");
                return new Func<int>(() => { invoke.Invoke(); return 0; });
            }
            var action = ui.Legacy(node);
            if (node == null || !node.Current.IsEnabled || action == null || action.CurrentRole() != 0x2B ||
                (action.CurrentState() & 1) != 0 || String.IsNullOrWhiteSpace(action.CurrentDefaultAction()))
                throw new InvalidOperationException("发送按钮默认动作不可用");
            log(label + " button role=0x" + action.CurrentRole().ToString("X") +
                " state=0x" + action.CurrentState().ToString("X") + " editorStyledEmpty=" +
                ui.Editor.Current.ClassName.Split(' ').Contains("is-empty"));
            return new Func<int>(action.DoDefaultAction);
        }, result);
        CheckFocus(permitted, trace, start);
        budget.Reserve(body);
        if (input) result.InCalls++; else result.OutCalls++;
        var method = !foregroundControl ? "LegacyIAccessible.DoDefaultAction" :
            foregroundEnter ? "foreground-only Enter SendInput" : "foreground-only InvokePattern";
        log(label + " send-intent " + Encoded(body) + " budget=" + budget.Used + " method=" + method);
        try
        {
            hr = button();
            log(label + (foregroundControl ? " send-return " + method + " completed" : " send-return hr=0x" + hr.ToString("X8")));
        }
        catch (Exception ex)
        {
            log(label + " send-return exception=" + Experiment.Failure(ex) + "；只查记录");
        }
        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.ElapsedMilliseconds < 11000)
            {
                Wait(220);
                try
                {
                    var observation = Read(ui => Tuple.Create(ReadHistory(ui), ui.EmptyDraft()), result);
                    int count = Count(observation.Item1, expected);
                    if (count > 1)
                    {
                        result.Duplicate++;
                        throw new InvalidOperationException("同编号在消息列表中多次出现");
                    }
                    if (count == 1 && observation.Item2)
                    {
                        pendingDraft = null;
                        if (input) result.InConfirmed++; else result.OutConfirmed++;
                        log(label + " message-confirmed rows=1 draftEmpty=yes " + Encoded(body));
                        return observation.Item1;
                    }
                }
                catch (ElementNotAvailableException) { }
            }
            throw new InvalidOperationException("未确认消息列表和清空草稿，待确认且不重发");
        }
        catch
        {
            result.Uncertain++;
            throw;
        }
    }
    private static int Count(ReplyRow[] rows, string expected)
    {
        return rows.Count(row => row.Text == expected);
    }
    private static string Encoded(string body) { return body.Replace("\n", "\\n"); }
}
