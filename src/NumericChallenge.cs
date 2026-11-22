using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

internal sealed class NumericChallenge
{
    internal const int LifetimeMs = 180000;
    internal const int MaxAttempts = 5;
    internal readonly string Code;
    private readonly Func<long> elapsed;
    private int wrong;

    internal NumericChallenge(string code, Func<long> elapsed = null)
    {
        if (!IsCode(code)) throw new ArgumentException("验证码须为 5 位数字");
        Code = code;
        var clock = Stopwatch.StartNew();
        this.elapsed = elapsed ?? (() => clock.ElapsedMilliseconds);
    }

    internal int SecondsLeft { get { return (int)Math.Ceiling(Math.Max(0, LifetimeMs - elapsed()) / 1000.0); } }

    internal static string Generate(Func<string, bool> used)
    {
        const uint count = 90000;
        const uint ceiling = UInt32.MaxValue / count * count;
        var bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create())
        {
            for (int attempt = 0; attempt < 512; attempt++)
            {
                rng.GetBytes(bytes);
                var value = BitConverter.ToUInt32(bytes, 0);
                if (value >= ceiling) continue;
                var code = (10000 + value % count).ToString(CultureInfo.InvariantCulture);
                if (!used(code)) return code;
            }
        }
        throw new InvalidOperationException("未能生成新的验证码，请重试");
    }

    internal static bool IsCode(string text)
    {
        return text != null && text.Length == 5 && text[0] >= '1' && text[0] <= '9' && text.All(c => c >= '0' && c <= '9');
    }

    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SecondsLeft == 0) throw new PrivateRouteLostException("验证码已过期。请重新生成");
        if (wrong >= MaxAttempts) throw new PrivateRouteLostException("错误验证码已达 5 次。请重新生成");
    }

    internal ReplyRow Match(ReplyRow[] additions, CancellationToken token, Action<int> failures)
    {
        Check(token);
        foreach (var row in additions)
        {
            var value = (row.RawText ?? "").Trim();
            if (row.Own != false || row.Segments != 1 || !IsCode(value)) continue;
            Check(token);
            if (value == Code)
            {
                if (additions.Count(r => r.Own == false && r.Segments == 1 && (r.RawText ?? "").Trim() == Code) != 1)
                    throw new PrivateRouteLostException("本次看到了多条相同验证码，无法唯一确认。请重新生成");
                return row;
            }
            failures(++wrong);
            Check(token);
        }
        return null;
    }
}
