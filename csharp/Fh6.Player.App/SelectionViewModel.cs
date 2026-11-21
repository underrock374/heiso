using System.Diagnostics;

namespace Fh6.Player;

/// <summary>
/// 画面で選んでいるレースと、それで決まる摩擦円・荷重移動(FrictionDynamic)。docs/architecture.md のルール 4 で、選んだレースは C# の状態。
/// 画面は選択を ViewSelectRace で伝え、C# は HostRaceSelected で返してから、摩擦円・荷重移動を作り直して HostFriction で送る。
/// 作る元は、最後に画面へ送った系列と印(VideoViewModel が送るたびに Shown で渡す)。ならしの幅は画面の設定(PlayerSettings.View の LtSmooth)
/// </summary>
public sealed class SelectionViewModel(IShell shell, PlayerSettings settings)
{
    /// <summary>レースの選択が変わった理由(画面はこれで、選んだ後の動き(シーク・知らせ)を決める)</summary>
    public const string CausePick = "pick", CauseFollow = "follow", CauseAxis = "axis", CauseRestore = "restore", CauseLeave = "leave";

    private SessionSeries? _series;
    private SessionMarks? _marks;
    private double? _sentSec;   // 最後に摩擦円を作った幅

    /// <summary>選んでいるレース(記録の中の番号。null なら「全体」)</summary>
    public int? Race { get; private set; }

    private int RaceCount => _marks?.Races.Length ?? 0;

    private double SmoothSec => FrictionDynamic.SmoothSec(settings.View?.LtSmooth);

    /// <summary>
    /// 画面へ送る系列と印(グラフを出せないときは null)を受け取り、選ぶレースを決めて返す。
    /// keep(同じ記録を送り直すとき)なら今の選択を残す。新しく開いたときは、設定の「記録を開いたときの表示」がレース別か、横軸が距離なら 1 レース目
    /// </summary>
    public int? Shown(SessionSeries? series, SessionMarks? marks, bool keep)
    {
        _series = series;
        _marks = marks;
        var view = settings.View;
        Race = keep ? (Race < RaceCount ? Race : null)
            : RaceCount > 0 && (view?.View == "race" || view?.Axis == "dist") ? 0 : null;
        return Race;
    }

    /// <summary>画面からのメッセージのうち、レースの選択を扱う。扱わないメッセージなら null</summary>
    public Task? Handle(ViewMessage msg)
    {
        if (msg is not ViewSelectRace m) return null;
        int? race = m.Race is int r && r >= 0 && r < RaceCount ? r : null;
        string cause = m.Cause is CauseFollow or CauseAxis or CauseRestore or CauseLeave ? m.Cause : CausePick;
        Race = race;
        shell.Post(new HostRaceSelected(race, cause));
        // 表示幅やグラフの拡大で選択が外れたときは、摩擦円・荷重移動はそのまま(前と同じ。グラフの操作の途中で作り直さない)
        if (cause != CauseLeave) SendFriction();
        return Task.CompletedTask;
    }

    /// <summary>画面の設定が保存されたとき: ならしの幅が変わっていれば作り直す</summary>
    public void SettingsSaved()
    {
        if (_series != null && _sentSec != SmoothSec) SendFriction();
    }

    /// <summary>今の選択と幅で摩擦円・荷重移動を作って送る。系列を送っていなければ何もしない(画面はグラフを出していない)</summary>
    public void SendFriction()
    {
        if (_series is not { } s || _marks is not { } marks) return;
        double sec = SmoothSec;
        var sw = Stopwatch.StartNew();
        var result = FrictionDynamic.Build(s, marks, Race, sec);
        long built = sw.ElapsedMilliseconds;
        shell.Post(result);
        _sentSec = sec;
        PlayerLog.Info($"摩擦円・荷重移動を作り直した: レース {Race?.ToString() ?? "全体"}、幅 {sec} 秒、{s.T.Length} 行(計算 {built} ms、送るまで {sw.ElapsedMilliseconds} ms)");
    }
}
