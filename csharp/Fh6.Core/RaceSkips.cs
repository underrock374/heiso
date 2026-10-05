namespace Fh6.Core;

/// <summary>スキップ区間(再生時に飛ばせる区間)。時刻は recv_time(epoch 秒)</summary>
/// <param name="Kind">
/// "redo": やり直し区間(リワインド・チェックポイント逃しで捨てた走りの始まりから、戻った先で走り直すまで)、
/// "pause": 一時停止(フォトモード・ポーズメニュー。経過時間が止まった停止区間)
/// </param>
public readonly record struct SkipRange(double FromRecvTime, double ToRecvTime, string Kind)
{
    public double Sec => ToRecvTime - FromRecvTime;
}

/// <summary>
/// レース区間の中のスキップ区間を求める(再生アプリの「スキップ」と、配布パッケージの package.json で同じ結果にする)。
/// やり直しは RaceDistance、一時停止は RacePauses で見つけ、近いものをまとめ、短いものを除く
/// </summary>
public static class RaceSkips
{
    /// <summary>これより短い区間は飛ばさない(位置の小さな飛びなど)</summary>
    public const double MinSec = 0.3;
    /// <summary>区間の間がこれより短ければまとめる(走り直してすぐまたリワインドした所を、続けて 2 回飛ばさないように)</summary>
    public const double MergeSec = 0.5;

    /// <summary>使う列</summary>
    public static readonly string[] Columns = { TelemetryTable.RecvTime, "is_race_on", "cur_race_time", "position_x", "position_z" };

    public static List<SkipRange> Find(TelemetryTable t, IEnumerable<RaceSegment> races)
    {
        var recv = t[TelemetryTable.RecvTime];
        var on = t["is_race_on"];
        var x = t["position_x"];
        var z = t["position_z"];
        var ranges = races.SelectMany(s =>
        {
            var (dist, kept) = RaceDistance.Compute(on, x, z, s.StartIndex, s.EndIndex);
            var redo = RaceDistance.RedoRanges(dist, kept)
                .Select(g => new SkipRange(recv[s.StartIndex + g.Start], recv[s.StartIndex + g.Resume], "redo"));
            var pauses = RacePauses.Find(recv, on, t["cur_race_time"], x, z, s.StartIndex, s.EndIndex)
                .Select(g => new SkipRange(recv[g.Last], recv[g.Resume], "pause"));
            return redo.Concat(pauses);
        });
        return Merge(ranges).Where(g => g.Sec >= MinSec).ToList();
    }

    /// <summary>近い区間をまとめる。種類の違うものをまとめたら、やり直し区間として扱う</summary>
    private static List<SkipRange> Merge(IEnumerable<SkipRange> ranges)
    {
        var list = new List<SkipRange>();
        foreach (var g in ranges.OrderBy(g => g.FromRecvTime))
        {
            if (list.Count > 0 && g.FromRecvTime - list[^1].ToRecvTime < MergeSec)
                list[^1] = new SkipRange(list[^1].FromRecvTime, Math.Max(list[^1].ToRecvTime, g.ToRecvTime),
                                         list[^1].Kind == g.Kind ? g.Kind : "redo");
            else list.Add(g);
        }
        return list;
    }
}
