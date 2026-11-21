namespace Fh6.Player;

/// <summary>
/// 外枠(Fh6.Player の MainWindow)が ViewModel に貸す機能(docs/architecture.md のルール 8)。
/// ViewModel は WPF と WebView2 を知らないので、画面への送信とダイアログはここを通す。
/// テストでは偽物に差し替える
/// </summary>
public interface IShell
{
    /// <summary>画面へメッセージを送る。UI スレッドから呼ぶ</summary>
    void Post(HostMessage message);

    /// <summary>
    /// フォルダを選ぶ画面を出す。選ばなかったとき、ほかの選ぶ画面が開いているときは null。
    /// 画面からのメッセージを処理している途中に呼んでよい(外枠が、その処理を終えてから画面を開く)
    /// </summary>
    Task<string?> PickFolderAsync(string title, string? initialDir);

    /// <summary>ファイルを選ぶ画面を出す(filter は "名前|*.ext|…" の形)。null になるときと、呼んでよいときは PickFolderAsync と同じ</summary>
    Task<string?> PickFileAsync(string title, string filter, string? initialDir);

    /// <summary>保存先を選ぶ画面を出す(fileName は初めの名前。拡張子は足さない)。null になるときと、呼んでよいときは PickFolderAsync と同じ</summary>
    Task<string?> PickSaveFileAsync(string title, string filter, string fileName, string? initialDir);

    /// <summary>
    /// 開いた記録を画面に出す(動画のフォルダを割り当て、ページを読み直す。読み直した画面の準備ができたら、外枠が記録のデータを送る)。
    /// altDir は、最初に表示する割り当てた動画のフォルダ(記録のフォルダ・録画のフォルダの外のときだけ。無ければ null)
    /// </summary>
    void ShowSession(LoadedSession session, string? altDir);

    /// <summary>
    /// 割り当てた動画のフォルダ(記録のフォルダ・録画のフォルダの外)を画面に出せるようにする。割り当てが変われば、ページを読み直して
    /// 動画の sec 秒・race のレースへ戻し true(読み直した画面の準備ができたら、外枠が記録のデータを送る)。同じフォルダなら何もせず false
    /// </summary>
    bool ShowVideoFolder(string dir, double sec, int? race);

    /// <summary>ページの読み込みより前に渡すもの(SettingsViewModel.PageScript)を登録し直す。今のページは読み直さない(次に読み込むページから効く)</summary>
    void RefreshPageData();

    /// <summary>画面からのメッセージの処理を終えてから続ける(ページを読み直す前に待つ)</summary>
    Task AfterMessageAsync();
}
