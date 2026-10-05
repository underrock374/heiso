namespace Fh6.Core;

public enum StartKind
{
    /// <summary>レースのスタート(race_pos >= 1)</summary>
    Race,
    /// <summary>レース以外の発進(信号待ち、フリーロームなど)</summary>
    Launch,
    /// <summary>チェックポイント逃しの強制復帰の後の発進(レース中に位置が飛んで止まった所から)</summary>
    Respawn,
}

/// <summary>見つけたスタート。Index は動き出す直前(止まっていた最後の行)</summary>
public readonly record struct StartEvent(int Index, StartKind Kind)
{
    /// <summary>レース中の出来事か(レースのスタートと復帰)</summary>
    public bool IsRace => Kind != StartKind.Launch;
}

/// <summary>
/// 止まった状態から速度が上がり始めた瞬間(スタート)を見つける。
/// is_race_on == 1 の区間で、1 km/h 未満が 1.0 秒以上続いた後に 5 km/h を超えたらスタート。
/// ただし停止区間(is_race_on == 0)の明けに止まった状態から始まった場合は、止まっていた時間が短くてもスタートとする。
/// FH6 のレースはグリッドのカウントダウン中が停止区間で、グリーンフラッグの瞬間に速度 0 で始まり、すぐ加速するため
/// (実測 2026-09-28)。リワインド明けは走っている状態から始まるので、スタートにはならない。
/// 順位(race_pos)が 0 でも、動き出した後に dist_traveled が増えていくならイベント(タイムアタックなど)のスタートとしてレースに含める。
/// レース中に停止区間を挟まず位置が 1 サンプルに 20m 超飛び、そこで止まってから動き出したものは「復帰」
/// (チェックポイント逃しの強制復帰。docs/telemetry-field-notes.md セクション 9.5)。
/// 時間は受信時刻(recv_time)の差で測る(送信レートは一定でなく、timestamp_ms は高い fps で刻みが粗い)。停止区間をまたいで止まっていた時間は足さない。
/// </summary>
public static class StartDetector
{
    public const double StillKmh = 1.0;
    public const double StillSec = 1.0;
    public const double MovingKmh = 5.0;
    /// <summary>1 サンプルでこれを超えて動いたら瞬間移動(復帰の判定)</summary>
    public const double TeleportM = 20.0;
    /// <summary>動き出してからこの秒数以内に dist_traveled が増えれば、イベント(タイムアタックなど)のスタート</summary>
    public const double EventSec = 3.0;

    /// <param name="timeMs">行ごとの時刻(ミリ秒)。recv_time × 1000 を渡す。timestamp_ms は 60fps 近くでは約 32ms 刻みで、約半分の行が直前と同じ値になるため使わない</param>
    /// <param name="speed">m/s(CSV の speed)</param>
    /// <param name="x">position_x(復帰の判定に使う。無ければ復帰は見分けない)</param>
    /// <param name="z">position_z</param>
    /// <param name="distTraveled">dist_traveled。渡すと、順位が 0 でもイベントが進行中(動き出した後に増えていく)ならレースとする
    /// (タイムアタックは race_pos が 0 のまま。フリーロームは dist_traveled が 0 のまま)</param>
    public static List<StartEvent> Detect(double[] timeMs, double[] isRaceOn, double[] speed, double[] racePos,
                                          double[]? x = null, double[]? z = null, double[]? distTraveled = null)
    {
        int n = timeMs.Length;
        if (isRaceOn.Length != n || speed.Length != n || racePos.Length != n)
            throw new ArgumentException("列の長さが揃っていません");

        var list = new List<StartEvent>();
        double? stillFrom = null;   // 止まり始めた時刻(ms)
        int lastStill = -1;         // 止まっていた最後の行
        bool fromGap = false;       // 停止区間の明けから止まっていた
        bool fromTeleport = false;  // レース中の瞬間移動の直後から止まっていた(チェックポイント逃しの復帰)
        bool afterGap = false;      // 直前が停止区間(記録の先頭は含まない。止まったまま記録を始めたときは 1 秒を求める)
        double prevTs = double.NaN;

        for (int i = 0; i < n; i++)
        {
            double ts = timeMs[i];
            bool on = isRaceOn[i] == 1 && double.IsFinite(speed[i]) && double.IsFinite(ts);
            // 停止区間、または時計が戻った(ゲームの再起動など)ら数え直す
            if (!on || (double.IsFinite(prevTs) && ts < prevTs))
            {
                stillFrom = null;
                lastStill = -1;
                fromGap = false;
                fromTeleport = false;
                afterGap = true;
                prevTs = on ? ts : double.NaN;
                if (!on) continue;
            }
            prevTs = ts;

            double kmh = speed[i] * 3.6;
            if (kmh < StillKmh)
            {
                if (stillFrom == null)
                {
                    stillFrom = ts;
                    fromGap = afterGap;
                    fromTeleport = !afterGap && racePos[i] >= 1 && Teleported(i);
                }
                lastStill = i;
            }
            else if (kmh > MovingKmh)
            {
                if (stillFrom is double from && lastStill >= 0 &&
                    (fromGap || fromTeleport || timeMs[lastStill] - from >= StillSec * 1000))
                    list.Add(new StartEvent(lastStill,
                        fromTeleport ? StartKind.Respawn
                        : racePos[lastStill] >= 1 || racePos[i] >= 1 || EventRunning(lastStill) ? StartKind.Race : StartKind.Launch));
                stillFrom = null;
                lastStill = -1;
                fromGap = false;
                fromTeleport = false;
            }
            afterGap = false;
            // 1〜5 km/h のじわじわ動きは、止まっていた記録を保ったまま次を待つ
        }
        return list;

        // 動き出してから EventSec 秒以内に dist_traveled が 0 より大きくなる(イベントが進行中)
        bool EventRunning(int from)
        {
            if (distTraveled == null) return false;
            for (int j = from; j < n && timeMs[j] - timeMs[from] <= EventSec * 1000; j++)
                if (isRaceOn[j] == 1 && distTraveled[j] > 0) return true;
            return false;
        }

        // 前の行から 1 サンプルで TeleportM を超えて動いた(停止区間を挟まない)
        bool Teleported(int i)
        {
            if (x == null || z == null || i == 0 || isRaceOn[i - 1] != 1) return false;
            double dx = x[i] - x[i - 1], dz = z[i] - z[i - 1];
            return Math.Sqrt(dx * dx + dz * dz) > TeleportM;
        }
    }
}
