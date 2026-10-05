namespace Fh6.Recorder;

public sealed class RecorderOptions
{
    /// <summary>ブラウザ画面の TCP ポート</summary>
    public int HttpPort { get; set; } = 8765;

    /// <summary>FH6 の Data Out を受けるUDPポート(ゲーム側の設定と合わせる)</summary>
    public int UdpPort { get; set; } = 5400;

    /// <summary>保存先。空なら「ビデオ\FH6Recorder」(既存の記録と .access_key を引き継ぐため旧名のまま)</summary>
    public string OutputDir { get; set; } = "";

    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>生パケット(324バイト)も telemetry.raw.gz に残す。パーサー修正後の再変換用</summary>
    public bool SaveRawPackets { get; set; } = true;

    /// <summary>
    /// 受信した UDP をそのまま送り直す先(パススルー)。"127.0.0.1:5301" のように ホスト:ポート を並べる。空なら送らない。
    /// SimHub などほかのテレメトリーのアプリと併用するため(ゲームの Data Out は 1 か所にしか送れない)
    /// </summary>
    public List<string> ForwardTo { get; set; } = new();

    public ObsOptions Obs { get; set; } = new();

    /// <summary>画面の言語: "auto"(Windows の表示言語が日本語なら日本語、それ以外は英語)/ "ja" / "en"。ログは日本語のまま</summary>
    public string Language { get; set; } = "auto";

    /// <summary>画面の速度の単位: "auto"(Windows の地域が米国か英国なら mph、それ以外は km/h)/ "kmh" / "mph"</summary>
    public string SpeedUnit { get; set; } = "auto";

    /// <summary>今使う速度の単位("kmh" か "mph")。自動は再生アプリ(units.js)と同じ決め方</summary>
    public string ResolveSpeedUnit() => SpeedUnit switch
    {
        "kmh" or "mph" => SpeedUnit,
        _ => System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "GB" ? "mph" : "kmh",
    };

    public string ResolveOutputDir()
    {
        if (!string.IsNullOrWhiteSpace(OutputDir))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(OutputDir));

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(videos, "FH6Recorder");
    }
}

public sealed class ObsOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public string Password { get; set; } = "";

    /// <summary>録画ファイルをセッションフォルダに直接書かせる(終了後にOBSの設定は元に戻す)</summary>
    public bool RecordIntoSessionFolder { get; set; } = true;
}
