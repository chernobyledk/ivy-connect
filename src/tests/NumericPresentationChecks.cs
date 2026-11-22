using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

internal static class NumericPresentationChecks
{
    private static void Check(bool test, string description)
    {
        if (!test) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private static ReplyRow Row(string key, string text, bool? own)
    {
        return new ReplyRow(key, new[] { text }, own);
    }

    private static bool Blocks(Action evaluate)
    {
        try { evaluate(); return false; }
        catch (PrivateRouteLostException) { return true; }
        catch (OperationCanceledException) { return true; }
    }

    [STAThread]
    private static int Main()
    {
        long ms = 0;
        var token = CancellationToken.None;
        Action<int> discard = _ => { };
        try
        {
            var code = new NumericChallenge("12345", () => ms);
            var past = Row("1", "12345", false);
            var timeline = new ReplyTimeline();
            timeline.Seed(new[] { past }, new[] { past });
            Check(code.Match(timeline.Advance(new[] { past }), token, discard) == null, "historical matching numeric row cannot verify");
            Check(code.Match(new[] { Row("2", "12345", true), Row("3", "12345", null), Row("4", "code 12345", false) }, token, discard) == null,
                "outgoing, unknown direction and additional text cannot verify");
            Check(code.Match(new[] { Row("5", " 12345 \n", false) }, token, discard).Key == "5", "exact new inbound five digits allow outer whitespace");
            Check(Blocks(() => code.Match(new[] { Row("6", "12345", false), Row("7", "12345", false) }, token, discard)), "ambiguous matching rows rejected");
            var response = new NumericChallenge("11111", () => ms);
            Check(response.Match(new[] { Row("7", "12345", false) }, token, discard) == null, "old code cannot verify regenerated attempt");
            Check(Blocks(() => response.Match(Enumerable.Range(0, 4).Select(i => Row(i.ToString(), "22222", false)).ToArray(), token, discard)),
                "five wrong inbound numbers cause lockout");
            ms = 180000;
            Check(code.SecondsLeft == 0 && Blocks(() => code.Match(new[] { Row("8", "12345", false) }, token, discard)), "matching code expired at 180 seconds");
            ms = 0;
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Check(Blocks(() => code.Match(new[] { Row("9", "12345", false) }, cancel.Token, discard)), "cancelled attempt cannot accept late digits");
            }
            var scope = new ReplyTimeline();
            scope.Seed(new[] { past }, new[] { past });
            Check(Blocks(() => { try { scope.Advance(new[] { Row("other", "12345", false) }); }
                catch (InvalidOperationException) { throw new PrivateRouteLostException("lost route"); } }), "different conversation timeline cannot accept digits");

            var path = Path.Combine(Path.GetTempPath(), "qq-numeric-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            try
            {
                using (var store = new PrivateStore(path)) store.ReserveEnrollmentCode("12345");
                using (var store = new PrivateStore(path))
                {
                    Check(store.IsEnrollmentCode(" 12345 ") && !store.IsEnrollmentCode("12346"), "reserved digits persist under DPAPI and are excluded from reply input");
                    Check(NumericChallenge.Generate(store.IsEnrollmentCode) != "12345", "generator excludes already issued codes");
                }
                var asset = Path.Combine(Directory.GetCurrentDirectory(), "assets", "cover.jpg");
                using (var detached = PresentationAssets.ReadCover(asset))
                using (File.Open(asset, FileMode.Open, FileAccess.Read, FileShare.None))
                    Check(detached.Width == 1794 && detached.Height == 877, "original cover pixels load without holding source file open");
                var clip = Cover.FillRect(new Size(300, 300), new Size(1000, 500));
                Check(clip.Height == 300 && clip.Width == 600 && clip.X == -150, "aspect-cover crops rather than stretches");
                string status = "";
                using (var controller = new PictureCoverController(path, value => status = value, Path.Combine(path, "missing.jpg")))
                {
                    Check(status.Contains("纯色"), "missing default reports solid fallback");
                    controller.UseCustom(asset);
                    Check(status.Contains("自选图片") && File.Exists(Path.Combine(path, "qq-cover.user.png")), "custom asset is copied to profile");
                    controller.Reset();
                    Check(status.Contains("纯色") && !File.Exists(Path.Combine(path, "qq-cover.user.png")), "restore default removes custom copy and reports fallback");
                }
                var corrupted = Path.Combine(path, "bad.jpg");
                File.WriteAllText(corrupted, "invalid data");
                using (var controller = new PictureCoverController(path, value => status = value, corrupted))
                    Check(status.Contains("纯色"), "invalid image reports solid fallback");
                File.Delete(corrupted);
            }
            finally
            {
                File.Delete(Path.Combine(path, "private-state.dpapi"));
                File.Delete(Path.Combine(path, "state.lock"));
                Directory.Delete(path);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
