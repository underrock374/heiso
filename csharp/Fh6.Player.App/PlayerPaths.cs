using System.IO;

namespace Fh6.Player;

/// <summary>
/// 再生アプリが書き込む場所(設定・ログ・車のプロファイル・展開したパッケージ・WebView2 の保存領域)。既定は %LOCALAPPDATA%\HEISO\Player。
/// 確認用の起動(tools/dev)では、環境変数 HEISO_PLAYER_DATA_DIR で全部を別のフォルダにする(利用者の設定とログに触れないため)。
/// Environment.GetFolderPath は環境変数 LOCALAPPDATA を見ないので、LOCALAPPDATA を上書きしても切り離せない。
/// </summary>
public static class PlayerPaths
{
    public const string DataDirVariable = "HEISO_PLAYER_DATA_DIR";

    public static string DataDir { get; } = ResolveDataDir(
        Environment.GetEnvironmentVariable(DataDirVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string SettingsFile => Path.Combine(DataDir, "player.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ProfilesDir => Path.Combine(DataDir, "profiles");
    public static string PackagesDir => Path.Combine(DataDir, "packages");
    public static string WebViewDir => Path.Combine(DataDir, "WebView2");

    /// <summary>環境変数の指定があればそのフォルダ、無ければ %LOCALAPPDATA%\HEISO\Player</summary>
    public static string ResolveDataDir(string? variable, string localAppData) =>
        string.IsNullOrWhiteSpace(variable)
            ? Path.Combine(localAppData, "HEISO", "Player")
            : Path.GetFullPath(variable);
}
