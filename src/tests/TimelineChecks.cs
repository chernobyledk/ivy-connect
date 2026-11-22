using System;
using System.Linq;

internal static class TimelineChecks
{
    private static ReplyRow Row(string id, string body)
    {
        return new ReplyRow(id, new[] { body });
    }
    private static void Require(bool pass, string label)
    {
        if (!pass) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
    private static void Main()
    {
        var first = Row("a", "historical");
        var second = Row("b", "historical again");
        var one = Row("c", "same new text");
        var two = Row("d", "same new text");
        var timeline = new ReplyTimeline();
        timeline.Seed(new[] { first, second }, new[] { first, second });
        Require(timeline.Advance(new[] { first, second }).Length == 0, "history ignored");
        var fresh = timeline.Advance(new[] { first, second, one, two });
        Require(fresh.Length == 2 && fresh[0].Digest == fresh[1].Digest, "identical content, distinct occurrences");
        Require(timeline.Advance(new[] { first, second, one, two }).Length == 0, "repeat poll does not enqueue again");
        var added = timeline.Advance(new[] { one, two, Row("e", "appended") });
        Require(added.Length == 1 && added.Single().Key == "e", "sliding window with anchored overlap");
        bool blocked = false;
        try { timeline.Advance(new[] { Row("f", "unanchored") }); }
        catch (InvalidOperationException) { blocked = true; }
        Require(blocked, "missing anchor stops rather than replaying");
        Require(timeline.Advance(new[] { one, two, Row("e", "appended") }).Length == 0, "recovered old anchor does not replay");

        var protocol = new SelfTestProtocol("EXAMPLE");
        var otherRun = new SelfTestProtocol("OTHER");
        protocol.Arm(1);
        Require(!protocol.Consume(new ReplyRow("test-other-run", new[] { otherRun.Input(1) }, true)), "another run is ignored");
        Require(!protocol.Consume(new ReplyRow("test-output", new[] { protocol.Output(1) }, true)), "TEST_OUT cannot trigger");
        Require(!protocol.Consume(new ReplyRow("test-nonself", new[] { protocol.Input(1) }, false)), "nonself input is ignored");
        var controlled = new ReplyRow("test-self", new[] { protocol.Input(1) }, true);
        Require(protocol.Consume(controlled) && !protocol.Consume(controlled), "armed TEST_IN accepted exactly once");
        protocol.Arm(2);
        protocol.Disable();
        Require(!protocol.Consume(new ReplyRow("test-disabled", new[] { protocol.Input(2) }, true)), "disabling removes armed input");
    }
}
