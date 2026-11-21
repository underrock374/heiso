namespace Fh6.Player;

/// <summary>
/// コース図(2D・3D)の線のための計算(前は画面の JS の COURSE_JUMP_M と buildSurfaceColors)。
/// 結果は BuildSeries で系列に入れて画面へ送る。色の指定は画面に残す
/// </summary>
public static class CourseLine
{
    /// <summary>1 行でこれ(m)を超えて動いたら、つながっていない(ファストトラベル・復帰・リワインド)。線を切る</summary>
    public const double JumpM = 50;

    /// <summary>路面の分類の「混在」(4 輪の路面が違う)。0〜7 は系列の路面の数値の下 3 ビット(7 = 縁石)</summary>
    public const int Mixed = 8;

    /// <summary>これより短い(行の数)路面の区間は、前の路面にならす</summary>
    public const int MinRun = 3;

    /// <summary>
    /// 線を切る行(系列の中の位置。小さい順): 位置のある前の行から JumpM を超えて動いた行。
    /// x・z は系列の値(m、小数 1 桁)。位置の無い行(停止区間など)は飛ばして、その前の行と比べる
    /// </summary>
    public static int[] BreakRows(IReadOnlyList<double?> x, IReadOnlyList<double?> z)
    {
        var rows = new List<int>();
        int prev = -1;
        for (int i = 0; i < x.Count; i++)
        {
            if (x[i] is not { } xi || z[i] is not { } zi) continue;
            if (prev >= 0 && double.Hypot(xi - x[prev]!.Value, zi - z[prev]!.Value) > JumpM) rows.Add(i);
            prev = i;
        }
        return rows.ToArray();
    }

    /// <summary>
    /// 行ごとの路面の分類: 4 輪の路面が同じならその路面(0〜7)、違えば Mixed、停止区間などは null。
    /// 空中の間は直前の路面を続け、MinRun 行未満の短い区間は前の路面にならす
    /// </summary>
    public static int?[] SurfaceKinds(SurfaceSeries s)
    {
        const int None = -1, Air = -2, AirCode = 6;
        int n = s.Fl.Length;
        var kind = new int[n];
        for (int i = 0; i < n; i++)
        {
            kind[i] = None;
            if (s.Fl[i] is not { } fl || s.Fr[i] is not { } fr || s.Rl[i] is not { } rl || s.Rr[i] is not { } rr) continue;
            int[] k = [fl & 7, fr & 7, rl & 7, rr & 7];
            if (k.Contains(AirCode)) { kind[i] = Air; continue; }
            kind[i] = k.All(v => v == k[0]) ? k[0] : Mixed;
        }
        // 空中は直前の路面を続ける
        int last = None;
        for (int i = 0; i < n; i++)
        {
            if (kind[i] == Air) kind[i] = last;
            else last = kind[i] >= 0 ? kind[i] : None;
        }
        // 短い区間は、前の路面にならす(ならした値を次の区間の「前」に使う)
        int runStart = 0;
        for (int i = 1; i <= n; i++)
        {
            if (i < n && kind[i] == kind[runStart]) continue;
            if (i - runStart < MinRun && runStart > 0 && kind[runStart] >= 0 && kind[runStart - 1] >= 0)
                for (int j = runStart; j < i; j++) kind[j] = kind[runStart - 1];
            runStart = i;
        }
        return kind.Select(k => k < 0 ? (int?)null : k).ToArray();
    }
}
