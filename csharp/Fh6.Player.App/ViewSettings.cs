using System.Text.Json;

namespace Fh6.Player;

/// <summary>
/// 画面の設定(印・再生・コース図・荷重移動・グラフの段・単位など。どの記録にも共通)。PlayerSettings.View として player.json に保存する
/// (前は画面の localStorage)。ページの読み込みより前に window.HEISO_SETTINGS(PageSettings)として渡し、画面は変えるたびに丸ごと
/// ViewSaveSettings で送り返す。画面から届く値は null のことがあるので、全部 null を許し、WithDefaults で既定の値を埋める。
/// 値の意味は画面(wwwroot/settings.js と、それを使う所)のもの
/// </summary>
public sealed record ViewSettings
{
    /// <summary>接触の候補の印</summary>
    public bool? Contacts { get; init; }
    /// <summary>レース以外の発進も印にする</summary>
    public bool? Launches { get; init; }
    /// <summary>開いたら最初のレースのスタート 3 秒前から再生</summary>
    public bool? AutoStart { get; init; }
    /// <summary>やり直し区間を自動で飛ばす</summary>
    public bool? SkipRedo { get; init; }
    /// <summary>同期の方式(VideoSyncMethod の名前)</summary>
    public string? Method { get; init; }
    /// <summary>再生の速さ(最後に使ったもの)</summary>
    public double? Rate { get; init; }
    /// <summary>表示幅(秒。0 = 全体。最後に使ったもの)</summary>
    public double? Span { get; init; }
    /// <summary>コース図の位置: "side"(右の欄)/ "graph"(グラフの横。右下)</summary>
    public string? MapPos { get; init; }
    /// <summary>グラフの横のときのコース図の幅・高さ(px。0 = グラフの高さに合わせた正方形)</summary>
    public double? MapW { get; init; }
    public double? MapH { get; init; }
    /// <summary>表示のモード: "vis"(見える化)/ "perf"(性能)/ "measure"(計測)</summary>
    public string? ViewMode { get; init; }
    /// <summary>コース図の場所に出すもの("2d" / "3d" / "load" / "gg")。見える化のときと性能モードのとき</summary>
    public string? MapMode { get; init; }
    public string? MapModePerf { get; init; }
    /// <summary>コース図の色: "lap"(今の周を青)/ "surface"(路面の色)</summary>
    public string? MapColor { get; init; }
    /// <summary>3D の視点("top" / "tilt" / "chase")と高さの強調("auto" / "1" / "3" / "5")</summary>
    public string? Map3dView { get; init; }
    public string? Map3dExag { get; init; }
    /// <summary>拡大中の追従: "live"(いつも今の位置)/ "step"(直線で区切った区間ごと)</summary>
    public string? MapFollow { get; init; }
    /// <summary>コース図の範囲(一辺の m。0 = 全体。最後に使ったもの)</summary>
    public double? MapZoom { get; init; }
    /// <summary>摩擦円の軌跡の長さ(秒。0 = 出さない)と、点群を重ねるか</summary>
    public double? GgTrail { get; init; }
    public bool? GgCloud { get; init; }
    /// <summary>荷重移動・摩擦円をならす幅(前後の秒)</summary>
    public double? LtSmooth { get; init; }
    /// <summary>記録を開いたときの範囲("all" / "race")と横軸("time" / "dist")</summary>
    public string? View { get; init; }
    public string? Axis { get; init; }
    /// <summary>単位("auto" か、速度は "kmh" / "mph"、距離は "m" / "ft"、標高は "m" / "ft")</summary>
    public string? UnitSpeed { get; init; }
    public string? UnitDist { get; init; }
    public string? UnitElev { get; init; }
    /// <summary>設定のダイアログで最後に開いていたタブ</summary>
    public string? SettingsTab { get; init; }
    /// <summary>グラフの段の組み合わせ(見える化・性能のモードごと)。無ければ画面の既定の組み合わせ</summary>
    public GraphLayouts? Layouts { get; init; }
    /// <summary>「自分の組み合わせ」として保存した段(モードごと)</summary>
    public CustomLanes? CustomLanes { get; init; }
    /// <summary>同期の微調整の小窓の位置と、閉じたか</summary>
    public SyncFloatState? SyncFloat { get; init; }

    /// <summary>既定の値(前は画面の DEFAULT_SETTINGS)。段の組み合わせと小窓は既定を持たない(画面が決める)</summary>
    public static readonly ViewSettings Defaults = new()
    {
        Contacts = true, Launches = false, AutoStart = false, SkipRedo = false, Method = "StartedEvent", Rate = 1, Span = 20,
        MapPos = "side", MapW = 0, MapH = 0, ViewMode = "vis", MapMode = "2d", MapModePerf = "load", MapColor = "lap",
        Map3dView = "tilt", Map3dExag = "auto", MapFollow = "live", MapZoom = 0, GgTrail = 3, GgCloud = true, LtSmooth = 0.25,
        View = "all", Axis = "time", UnitSpeed = "auto", UnitDist = "auto", UnitElev = "auto", SettingsTab = "graph",
    };

    /// <summary>値の無い所(null)を既定の値で埋めたもの</summary>
    public ViewSettings WithDefaults()
    {
        var node = JsonSerializer.SerializeToNode(this, MessageJson.Options)!.AsObject();
        foreach (var (key, value) in JsonSerializer.SerializeToNode(Defaults, MessageJson.Options)!.AsObject())
            if (node[key] is null && value is not null) node[key] = value.DeepClone();
        return node.Deserialize<ViewSettings>(MessageJson.Options)!;
    }
}

/// <summary>モード(見える化 vis・性能 perf)ごとのグラフの段の組み合わせ</summary>
public sealed record GraphLayouts(GraphLayout? Vis, GraphLayout? Perf);

/// <summary>モードごとの「自分の組み合わせ」</summary>
public sealed record CustomLanes(GraphLane[]? Vis, GraphLane[]? Perf);

/// <summary>グラフの段の組み合わせ。preset は選んだ組み合わせの名前(自分で変えたら null)</summary>
public sealed record GraphLayout(string? Preset, GraphLane[]? Lanes);

/// <summary>グラフの 1 段。h は高さ("s" / "m" / "l")、s は出す系列の名前</summary>
public sealed record GraphLane(string? H, string[]? S);

/// <summary>同期の微調整の小窓: 閉じたか、位置(px)</summary>
public sealed record SyncFloatState(bool? Closed, double? X, double? Y);

/// <summary>
/// ページの読み込みより前に window.HEISO_SETTINGS として渡す画面の設定。saved が偽なら、まだ C# に保存が無い
/// (画面が、前の版の localStorage の値を一度だけ引き継いで送る)。autoImperial は、単位の「自動」がマイルになるか
/// (Windows の地域で決まる。決め方は記録アプリと共通で、Fh6.Core の DisplayUnits)
/// </summary>
public sealed record PageSettings(bool Saved, ViewSettings Settings, bool AutoImperial);
