namespace Fh6.Core;

/// <summary>
/// レース中の一時停止(フォトモード・ポーズメニューなど)。再生時に飛ばせる(クリーン再生)。
/// 停止区間(is_race_on == 0)のうち、明けたときにレースの経過時間がほぼ止まっていて(入る前の値の続き)、
/// 位置もほとんど動いていないもの。フォトモードとポーズメニューはテレメトリーでは区別できない(どちらも同じ形)。
/// 対象にしないもの: リワインド(経過時間が巻き戻る。やり直し区間の方で扱う)、ゴール後のリザルト画面(経過時間が進み続ける)、
/// 路上でスピンして止まっている間(is_race_on は 1 のままで、経過時間も進む)。
/// </summary>
public static class RacePauses
{
    /// <summary>これより短い停止区間は飛ばさない(一瞬の読み込みなどで映像が不自然に飛ばないように)</summary>
    public const double MinSec = 2.0;
    /// <summary>経過時間がこれより進んでいなければ「止まっていた」</summary>
    public const double MaxRaceTimeAdvanceSec = 0.5;
    /// <summary>位置がこれより動いていなければ「同じ場所から続けた」</summary>
    public const double MaxMoveM = 5.0;

    /// <summary>
    /// 行 from 〜 to(レース区間)の中の一時停止。Last は停止区間の直前の走行中の行、Resume は明けた最初の走行中の行
    /// </summary>
    public static List<(int Last, int Resume)> Find(double[] recvTime, double[] isRaceOn, double[] curRaceTime,
                                                     double[] x, double[] z, int from, int to)
    {
        var list = new List<(int, int)>();
        for (int i = from + 1; i <= to; i++)
        {
            if (isRaceOn[i] == 1 || isRaceOn[i - 1] != 1) continue;
            int last = i - 1;
            int j = i;
            while (j <= to && isRaceOn[j] != 1) j++;
            if (j > to) break;   // レースの終わり(リザルト画面など)
            double dur = recvTime[j] - recvTime[last];
            double advance = curRaceTime[j] - curRaceTime[last];
            double move = Math.Sqrt((x[j] - x[last]) * (x[j] - x[last]) + (z[j] - z[last]) * (z[j] - z[last]));
            if (dur >= MinSec && advance >= -0.05 && advance < MaxRaceTimeAdvanceSec && move < MaxMoveM)
                list.Add((last, j));
            i = j;
        }
        return list;
    }
}
