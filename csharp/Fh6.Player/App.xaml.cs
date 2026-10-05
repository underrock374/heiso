using System.Threading.Tasks;
using System.Windows;

namespace Fh6.Player;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        PlayerLog.Start();

        // 画面(UI スレッド)の処理で拾われなかった例外: 記録して、止めずに続ける(画面に知らせる)
        DispatcherUnhandledException += (_, ev) =>
        {
            PlayerLog.Error("画面の処理で予期しない例外", ev.Exception);
            ev.Handled = true;
            (MainWindow as MainWindow)?.NotifyError($"予期しないエラーが起きました(ログに記録しました): {ev.Exception.Message}");
        };
        // それ以外のスレッドの例外(アプリは終わる)
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            PlayerLog.Error($"予期しない例外(終了する: {ev.IsTerminating})",
                ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject?.ToString()));
        // 待たれなかった非同期の処理の例外
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            PlayerLog.Error("非同期の処理で拾われなかった例外", ev.Exception);
            ev.SetObserved();
        };

        // 使い方: HeisoPlayer [<セッションフォルダ または session.json>]
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    // 正常に終わったときだけ、起動中の印を消す(落ちたときは残り、次の起動で知らせる)
    private void OnExit(object sender, ExitEventArgs e) => PlayerLog.Stop();
}
