using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

internal sealed class ContinuousReply
{
    internal const string ReplyPrefix = "联测回执";
    private readonly Binding binding;
    private readonly Target target;
    private readonly Func<bool> paused;
    private readonly Func<IntPtr> permittedForeground;
    private readonly ForegroundTrace trace;
    private readonly Action<string> log, state;
    private readonly CancellationToken cancel;
    private readonly ReplyTimeline timeline = new ReplyTimeline();
    private readonly Queue<Item> queue = new Queue<Item>();
    private readonly HashSet<string> inboundDigests = new HashSet<string>(StringComparer.Ordinal);
    private readonly Regex testInput;
    private readonly string code;
    private readonly int limit;
    private int received, confirmed, ignoredReplies;
    private bool halt;
    private string lastStatus;
    private Item inFlight;
    private int polls;

    private sealed class Item
    {
        internal int Number;
        internal string Case, Reply, Phase;
    }
    internal ContinuousReply(Binding binding, string code, int limit, Func<bool> paused,
        Func<IntPtr> permittedForeground, ForegroundTrace trace, Action<string> log, Action<string> state,
        CancellationToken cancel)
    {
        if (!binding.Self) throw new InvalidOperationException("连续验证需要已核实的本人会话");
        this.binding = binding;
        target = binding.Target;
        this.code = code;
        this.limit = limit;
        this.paused = paused;
        this.permittedForeground = permittedForeground;
        this.trace = trace;
        this.log = log;
        this.state = state;
        this.cancel = cancel;
        testInput = new Regex("^联测" + Regex.Escape(code) + "([0-9]{2})(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    private void Status(string value)
    {
        var next = value + " | 读到 " + received + " | 排队 " + queue.Count + " | 本地确认 " + confirmed;
        if (next == lastStatus) return;
        lastStatus = next;
        state(next);
        log("status " + next);
    }
    private void Wait(int ms)
    {
        if (cancel.WaitHandle.WaitOne(ms)) cancel.ThrowIfCancellationRequested();
    }
    private Controls Scan()
    {
        cancel.ThrowIfCancellationRequested();
        if (!target.SameWindow() || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
            throw new InvalidOperationException("目标窗口已变化、隐藏或最小化");
        var controls = binding.RequireMatch();
        if (!controls.HasMessageArea || controls.Editor == null)
            throw new InvalidOperationException("消息列表或输入节点不可用");
        return controls;
    }
    private T Read<T>(Func<Controls, T> read)
    {
        for (int remaining = 2; ; remaining--)
        {
            try { return read(Scan()); }
            catch (ElementNotAvailableException)
            {
                if (remaining == 0) throw;
                Wait(240);
            }
        }
    }
    private void Discover(ReplyRow[] rows)
    {
        foreach (var row in timeline.Advance(rows))
        {
            if (row.Text.StartsWith(ReplyPrefix, StringComparison.Ordinal))
            {
                ignoredReplies++;
                log("ignored own-response prefix count=" + ignoredReplies);
                continue;
            }
            if (row.Own != false || row.Text.StartsWith("TEST_IN", StringComparison.Ordinal) ||
                row.Text.StartsWith("TEST_OUT", StringComparison.Ordinal)) continue;
            var match = testInput.Match(row.Text);
            if (!match.Success) continue;
            var item = new Item { Number = ++received, Case = match.Groups[1].Value, Phase = "queued" };
            item.Reply = ReplyPrefix + " " + code + " #" + item.Number.ToString("D3") + " 来信" + item.Case + "\n已收到测试消息";
            var duplicateText = !inboundDigests.Add(row.Digest);
            log("read new item=" + item.Number + " case=" + item.Case + " distinctBubble=yes sameTextAsEarlier=" +
                duplicateText + " textSegments=" + row.Segments);
            if (item.Number <= limit)
            {
                queue.Enqueue(item);
                log("queued item=" + item.Number + " case=" + item.Case);
            }
            else log("limit-skipped item=" + item.Number);
        }
    }
    private bool ForegroundOk(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && Native.Pid(hwnd) != target.Pid && permittedForeground() == hwnd;
    }
    private void Guard(IntPtr foreground, int events)
    {
        cancel.ThrowIfCancellationRequested();
        if (paused()) throw new InvalidOperationException("操作员已暂停");
        if (Native.IsIconic(target.Hwnd) || !ForegroundOk(foreground) || trace.Count != events)
            throw new InvalidOperationException("最小化、遮挡条件或其他软件的前台状态已变");
    }
    internal void Run()
    {
        using (var lease = new Mutex(false, @"Local\QQWindowLab.Continuous." + target.Hwnd.ToInt64()))
        {
            bool owned;
            try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("该 QQ 窗口已有连续验证在运行");
            try { RunBound(); }
            finally { lease.ReleaseMutex(); }
        }
    }
    private void RunBound()
    {
        var first = Read(ui => ui.ReplyRows());
        Wait(380);
        var second = Read(ui => ui.ReplyRows());
        timeline.Seed(first, second);
        log("baseline READY rows=" + second.Length + " code=" + code + " cap=" + limit +
            " phone-confirmation=separate");
        Status("已启动，等待新测试消息");
        var heartbeat = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (halt) { Wait(400); continue; }
                try
                {
                    var ui = Read(controls => {
                        Discover(controls.ReplyRows());
                        return controls;
                    });
                    polls++;
                    if (heartbeat.Elapsed.TotalSeconds >= 25)
                    {
                        log("heartbeat polls=" + polls + " read=" + received + " confirmed=" + confirmed);
                        heartbeat.Restart();
                    }
                    if (confirmed >= limit) { Status("达到本轮发送上限"); Wait(600); continue; }
                    if (paused()) { Status("手动暂停，仍记录新气泡"); Wait(500); continue; }
                    if (queue.Count == 0) { Status("等待新测试消息"); Wait(350); continue; }
                    var foreground = permittedForeground();
                    if (!ForegroundOk(foreground)) { Status("暂停：等待指定的其他前台软件"); Wait(400); continue; }
                    if (!ui.EmptyDraft()) { Status("暂停：输入框有草稿或未能确认空白"); Wait(500); continue; }
                    Dispatch(queue.Peek(), foreground, trace.Count);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Status("暂停：" + Experiment.Failure(ex));
                    Wait(600);
                }
            }
        }
        finally
        {
            if (inFlight != null) log("stop item=" + inFlight.Number + " phase=" + inFlight.Phase +
                "；结束本轮，待确认项不重发");
            log("summary polls=" + polls + " read=" + received + " local-confirmed=" + confirmed + " queued=" + queue.Count +
                " own-filtered=" + ignoredReplies + " operator-input-API=no clipboard=no mouse=no");
        }
    }
    private void Dispatch(Item item, IntPtr foreground, int events)
    {
        Guard(foreground, events);
        var ui = Read(controls => {
            timeline.CheckSameConversation(controls.ReplyRows());
            return controls;
        });
        if (!ui.EmptyDraft()) throw new InvalidOperationException("写入前输入框出现草稿");
        var mapped = MsaaProbe.FindMappedEditor(target, ui);
        if (mapped == null) throw new InvalidOperationException("未能复核 MSAA 编辑对象");
        Guard(foreground, events);
        bool dispatched = false;
        inFlight = item;
        try
        {
            item.Phase = "write-attempt";
            log("write-attempt item=" + item.Number + " method=MSAA.put_accValue");
            var hr = MsaaProbe.PutValue(mapped, item.Reply);
            log("write-return item=" + item.Number + " hr=0x" + hr.ToString("X8"));
            if (hr != 0) throw new InvalidOperationException("MSAA 写入返回非 S_OK");
            for (int n = 0; ; n++)
            {
                Wait(240);
                var draft = Read(controls => Tuple.Create(controls, controls.Draft(), controls.EmptyDraft()));
                if (draft.Item2 == item.Reply) { ui = draft.Item1; break; }
                if (n == 3 || !draft.Item3) { ui = null; break; }
            }
            if (ui == null) throw new InvalidOperationException("写入后正文回读不一致");
            item.Phase = "written";
            log("write-confirmed item=" + item.Number);
            var rows = Read(controls => controls.ReplyRows());
            timeline.CheckSameConversation(rows);
            var expected = MessageSnapshot.Normalize(item.Reply);
            if (rows.Any(row => row.Text.IndexOf(expected, StringComparison.Ordinal) >= 0))
                throw new InvalidOperationException("回执编号在发送前已出现");
            var button = ui.Button;
            var action = ui.Legacy(button);
            if (button == null || !button.Current.IsEnabled || action == null || action.CurrentRole() != 0x2B ||
                (action.CurrentState() & 1) != 0 || String.IsNullOrWhiteSpace(action.CurrentDefaultAction()))
                throw new InvalidOperationException("发送按钮默认动作未就绪");
            Guard(foreground, events);
            if (!binding.Matches(ui) || ui.Draft() != item.Reply)
                throw new InvalidOperationException("发送前会话或草稿发生变化");
            Guard(foreground, events);
            item.Phase = "send-intent";
            log("send-intent item=" + item.Number + " method=LegacyIAccessible.DoDefaultAction");
            dispatched = true;
            hr = action.DoDefaultAction();
            log("send-return item=" + item.Number + " hr=0x" + hr.ToString("X8"));
            Confirm(item, foreground, events);
            inFlight = null;
        }
        catch (Exception ex)
        {
            halt = true;
            item.Phase = dispatched ? "pending-confirmation" : "write-review";
            log(item.Phase + " item=" + item.Number + " reason=" + Experiment.Failure(ex) + "；不重发");
            Status(dispatched ? "待确认，已停发" : "待核对测试草稿，已停发");
            if (ex is OperationCanceledException) throw;
        }
    }
    private void Confirm(Item item, IntPtr foreground, int events)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 14000)
        {
            Wait(260);
            try
            {
                var ui = Scan();
                var snapshot = ui.Messages(item.Reply);
                var count = snapshot.MarkerRows(item.Reply);
                if (count > 1) throw new InvalidOperationException("回执编号出现多次");
                if (count != 1 || !ui.EmptyDraft()) continue;
                confirmed++;
                queue.Dequeue();
                var retained = ForegroundOk(foreground) && trace.Count == events;
                log("local-confirmed item=" + item.Number + " exactRows=1 emptyDraft=yes phone=not-confirmed" +
                    " foregroundEvents=" + (trace.Count - events) + " focusRetained=" + retained);
                if (!retained)
                {
                    halt = true;
                    Status("已本地确认，检测到前台变化，暂停后续发送");
                }
                else Status("已本地确认");
                return;
            }
            catch (ElementNotAvailableException) { }
        }
        throw new InvalidOperationException("未在时限内看到唯一回执且确认草稿已清空");
    }
}
