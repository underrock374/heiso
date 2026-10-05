using Fh6.Core;

namespace Fh6.Recorder;

/// <summary>
/// 記録済みセッションの session.json に、録画ファイルの長さと終端の検算を書き足す。
/// 使い方: HeisoRecorder --resync <セッションフォルダ> [<セッションフォルダ> ...]
/// </summary>
public static class Resync
{
    public static int Run(IEnumerable<string> folders)
    {
        int failed = 0;
        foreach (var folderArg in folders)
        {
            var folder = Path.GetFullPath(folderArg);
            Console.WriteLine($"[{Path.GetFileName(folder)}]");
            try
            {
                var jsonPath = Path.Combine(folder, SessionMeta.FileName);
                var meta = SessionMeta.Load(folder);
                var v = meta.Video;

                var video = meta.FindVideo(folder);
                if (video == null) throw new FileNotFoundException(Strings.T("録画ファイルが見つかりません"));
                var dur = VideoDuration.TryRead(video) ?? throw new InvalidDataException(Strings.T("長さを読めません(MP4 以外は未対応): {path}", ("path", video)));

                v.FileDurationSec = Math.Round(dur, 3);
                v.FileFps = VideoDuration.TryReadFrameRate(video) is double fps ? Math.Round(fps, 3) : null;
                SyncCalc.Finalize(v);

                var end = v.StopRequestedRecvTime ?? v.StoppedEventRecvTime;
                Console.WriteLine("  " + Strings.T("動画の長さ          {sec:F3} 秒{fps}", ("sec", dur), ("fps", v.FileFps is double f ? $"({f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} fps)" : "")));
                if (v.StartedEventRecvTime is double se)
                    Console.WriteLine("  " + Strings.T("開始イベント        {t:F4}", ("t", se)));
                if (v.VideoZeroRecvTime is double z)
                    Console.WriteLine($"  video_zero_recv_time {z:F4}");
                if (end is double e)
                {
                    Console.WriteLine("  " + Strings.T("終端から逆算した0秒 {t:F4}{note}", ("t", e - dur), ("note", v.StopRequestedRecvTime == null ? Strings.T("(旧版の記録なので停止完了時刻から逆算。実際はこれより少し前)") : "")));
                }
                if (v.EndCheckSec is double c)
                    Console.WriteLine("  " + Strings.T("終端の検算          {sec} 秒", ("sec", c.ToString("+0.000;-0.000", System.Globalization.CultureInfo.InvariantCulture))));

                var bak = jsonPath + ".bak";
                if (!File.Exists(bak)) File.Copy(jsonPath, bak);
                meta.Save(folder);
                Console.WriteLine("  " + Strings.T("session.json を更新しました(元のファイルは session.json.bak)"));
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("  " + Strings.T("失敗: {error}", ("error", ex.Message)));
            }
        }
        return failed == 0 ? 0 : 1;
    }
}
