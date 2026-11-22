using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed partial class LabForm
{
    private void StartRegionExperiment()
    {
        var scope = binding;
        if (scope == null || !scope.Self) throw new InvalidOperationException("请先绑定已核实自聊");
        Write("4 秒内切回 QQ。先测尺寸，再临时裁剪并做一次前台自聊收发。");
        StartJob(async token => {
            await Task.Delay(4000, token);
            await RegionExperiment(scope, token);
        });
    }
    private async Task RegionExperiment(Binding scope, CancellationToken token, string resumeDraft = null)
    {
        var target = scope.Target;
        if (Native.GetForegroundWindow() != target.Hwnd || Native.IsZoomed(target.Hwnd) || Native.IsIconic(target.Hwnd))
            throw new InvalidOperationException("尺寸与裁剪需从正常大小、前台 QQ 开始");
        CapturePlacement(target);
        var normal = Native.Bounds(target.Hwnd);
        try
        {
            using (var trace = new ForegroundTrace())
            {
                var experiment = new InputExperiments(scope, trace, Write, token);
                Write("R2 display hwnd=" + target.Hwnd.ToInt64() + " " + SizeReport(target));
                if (resumeDraft == null)
                {
                    foreach (var size in new[] { new Size(960, 660), new Size(720, 500), new Size(480, 360), new Size(1, 1) })
                    {
                        token.ThrowIfCancellationRequested();
                        ResizeTo(target, new Rectangle(normal.Location, size));
                        Write("R2 resize request=" + size.Width + "x" + size.Height + " " + SizeReport(target));
                    }
                    await Task.Run(() => experiment.Foreground("R2-MIN"), token);
                    ResizeTo(target, normal);
                }
                Write("R2 borderless=" + (Native.ClientSize(target.Hwnd) == normal.Size
                    ? "not-applicable; outer and client sizes already equal"
                    : "not-tested; outer differs from client"));
                var kept = await Task.Run(() => ClipBounds(scope, normal), token);
                var point = HiddenHitPoint(normal, kept);
                if (point == Point.Empty) { Write("R2 crop not-applicable: no safe hidden test point"); return; }
                using (var surface = new Cover { TopMost = false, Text = "QQ 裁剪命中见证",
                    Bounds = new Rectangle(point.X - 15, point.Y - 15, 30, 30) })
                {
                    surface.Show();
                    Native.SetWindowPos(surface.Handle, target.Hwnd, point.X - 15, point.Y - 15, 30, 30, Native.NoActivate);
                    var before = Native.RootAt(point.X, point.Y);
                    Write("R2 crop before hidden-test point=" + point + " root=" + before.ToInt64() + " QQ=" + (before == target.Hwnd));
                    kept.Offset(-normal.X, -normal.Y);
                    try
                    {
                        using (var clip = new WindowClip(target, kept))
                        {
                            try
                            {
                                await Task.Delay(350, token);
                                var actual = clip.LocalBounds();
                                var after = Native.RootAt(point.X, point.Y);
                                var inside = Native.RootAt(normal.X + kept.X + kept.Width / 2, normal.Y + kept.Y + kept.Height / 2);
                                Write("R2 crop intended=" + kept + " actual-region=" + actual + " " + SizeReport(target) +
                                    " retained-hit-QQ=" + (inside == target.Hwnd));
                                Write("R2 crop hidden-hit root=" + after.ToInt64() + " underlying-witness=" +
                                    (before == target.Hwnd && after == surface.Handle) + " QQ=" + (after == target.Hwnd));
                                if (actual == kept && inside == target.Hwnd)
                                {
                                    if (resumeDraft == null) await Task.Run(() => experiment.Foreground("R2-CROP"), token);
                                    else
                                    {
                                        try { await Task.Run(() => experiment.FinishKnownDraft("R2-CROP-RESUME", resumeDraft), token); }
                                        catch (OperationCanceledException) { throw; }
                                        catch (Exception ex) { Write("R2-CROP-RESUME stopped: " + Experiment.Failure(ex)); }
                                    }
                                }
                                else Write("R2-CROP send not-tested: crop geometry or hit test unverified");
                            }
                            finally { Write("R2 crop restored=" + clip.Restore()); }
                        }
                    }
                    finally { Write("R2 crop restored hit-QQ=" + (Native.RootAt(point.X, point.Y) == target.Hwnd)); }
                }
                var pending = resumeDraft ?? experiment.OutstandingDraft;
                if (pending != null) await Task.Run(() => experiment.CleanupKnownDraft(pending));
                Write("R2 display foreground WinEvents=" + trace.Summary());
            }
        }
        finally { Restore(); }
    }
    private static Rectangle ClipBounds(Binding scope, Rectangle outer)
    {
        var ui = scope.RequireMatch();
        var bubble = ui.Bubbles.LastOrDefault(b => !b.Current.IsOffscreen);
        if (bubble == null || ui.Editor == null || ui.Button == null)
            throw new InvalidOperationException("无法定位需保留的消息、输入框和发送控件");
        var rect = Rectangle.Union(ElementBounds(ui.Editor), ElementBounds(ui.Button));
        rect = Rectangle.Union(rect, ElementBounds(bubble));
        rect.Inflate(12, 12);
        return Rectangle.Intersect(outer, rect);
    }
    private static Rectangle ElementBounds(System.Windows.Automation.AutomationElement node)
    {
        var box = node.Current.BoundingRectangle;
        if (box.IsEmpty) throw new InvalidOperationException("裁剪所需的控件矩形为空");
        return Rectangle.FromLTRB((int)box.Left, (int)box.Top, (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));
    }
    private static Point HiddenHitPoint(Rectangle outer, Rectangle kept)
    {
        foreach (var x in new[] { outer.Left + 45, outer.Right - 45 })
            foreach (var y in new[] { outer.Top + 80, outer.Bottom - 45 })
                if (!kept.Contains(x, y)) return new Point(x, y);
        return Point.Empty;
    }
}
