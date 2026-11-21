using System.Globalization;
using System.IO.Compression;
using System.Text;
using Fh6.Core;

namespace Fh6.Player.App.Tests;

/// <summary>
/// 固定の入力になる合成の記録。実データは使わない(公開リポジトリでも同じテストが動くように)。
/// 1 本のレースに、発進、全開の加速と変速、周回、コーナー、ブレーキ、ジャンプと着地、路面の逸脱、接触、一時停止を入れる。
/// 値は小数 4 桁に丸めて書く(環境による三角関数のごくわずかな違いを持ち込まないため)
/// </summary>
internal static class SyntheticSession
{
    public const double Zero = 1_790_000_000;
    private const double Hz = 30;
    private const double GridSec = 6;     // スタート前に止まっている時間
    private const double RaceSec = 50;
    private const double LapAt = 25;      // 2 周目に入る時刻(レースの開始から)
    private const double PauseAt = 47;    // 一時停止する時刻(同じ)
    private const double PauseSec = 2;
    private static readonly string[] Wheels = { "fl", "fr", "rl", "rr" };

    /// <summary>folder の下に、session.json と telemetry.csv.gz を書く</summary>
    public static void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var columns = Fh6Packet.CsvHeader().Split(',');
        var sb = new StringBuilder(string.Join(",", columns)).Append('\n');
        var row = new Dictionary<string, double>();

        // 記録の頭の、レースの外の行
        Line(sb, columns, Row(row, on: false, t: -2));
        for (int i = 0; i < GridSec * Hz; i++) Line(sb, columns, Row(row, on: true, t: i / Hz));
        double x = 0, z = 0, y = 10, heading = 0, dist = -50;
        for (int i = 0; i < RaceSec * Hz; i++)
        {
            double t = i / Hz;
            // 一時停止: is_race_on = 0 の行が続き、再開すると位置とレースタイムは止まる前の続き
            if (i == (int)(PauseAt * Hz))
                for (int k = 0; k < PauseSec * Hz; k++) Line(sb, columns, Row(row, on: false, t: GridSec + t + k / Hz));
            double clock = GridSec + t + (t >= PauseAt ? PauseSec : 0);
            var p = Phase(t);
            heading += p.YawRate / Hz;
            x += Math.Sin(heading) * p.Speed / Hz;
            z += Math.Cos(heading) * p.Speed / Hz;
            y += p.Climb / Hz;
            dist += p.Speed / Hz;
            var r = Row(row, on: true, t: clock);
            r["race_pos"] = 1;
            r["lap_no"] = t < LapAt ? 0 : 1;
            r["cur_race_time"] = t;
            r["cur_lap"] = t < LapAt ? t : t - LapAt;
            r["dist_traveled"] = dist;
            r["speed"] = p.Speed;
            r["accel"] = p.Accel;
            r["brake"] = p.Brake;
            r["steer"] = p.Steer;
            r["gear"] = p.Gear;
            r["current_engine_rpm"] = p.Rpm;
            r["power"] = p.Power;
            r["position_x"] = x;
            r["position_y"] = y;
            r["position_z"] = z;
            r["yaw"] = heading;
            r["angular_velocity_y"] = p.YawRate;
            r["acceleration_x"] = p.LatG * 9.81;
            r["acceleration_z"] = p.LongG * 9.81;
            foreach (var w in Wheels)
            {
                r["norm_suspension_travel_" + w] = p.Susp;
                r["tire_combined_slip_" + w] = p.Slip;
                r["tire_slip_ratio_" + w] = 0.02;
                r["tire_slip_angle_" + w] = p.LatG * 0.1;
                r["surface_rumble_" + w] = p.Rumble;
            }
            Line(sb, columns, r);
        }
        // 記録の終わりの、レースの外の行
        Line(sb, columns, Row(row, on: false, t: GridSec + RaceSec + PauseSec));

        using (var gz = new GZipStream(File.Create(Path.Combine(folder, SessionWriter.CsvName)), CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(sb.ToString()));

        new SessionMeta
        {
            Complete = true,
            Video = new VideoInfo { StartedEventRecvTime = Zero, VideoZeroRecvTime = Zero, VideoZeroMethod = "started_event", FileFps = 30, FileDurationSec = GridSec + RaceSec + PauseSec + 4 },
            Stats = new WriterStats(),
        }.Save(folder);
    }

    private sealed record State(double Speed, double Accel, double Brake, double Steer, double Gear, double Rpm, double Power,
        double YawRate, double LatG, double LongG, double Susp, double Slip, double Rumble, double Climb);

    /// <summary>レースの開始から t 秒の、車の状態</summary>
    private static State Phase(double t)
    {
        const double Susp = 0.5;
        // 全開の加速: 4 秒ごとに変速。回転は 3000 → 6500 rpm。変速の直前の 1 行は gear = 11
        if (t < 20)
        {
            int gear = 1 + (int)(t / 4);
            double f = t / 4 - (gear - 1);
            double rpm = 3000 + 3500 * f;
            bool shifting = f > 0.985 && gear < 5;
            return new(5 + 3.0 * t, 255, 0, 0, shifting ? 11 : gear, rpm, Watts(rpm), 0, 0, 0.6, Susp, 0.1, 0, 0);
        }
        const double Cruise = 65;
        if (t < 28) return new(Cruise, 120, 0, 60, 4, 5000, Watts(5000) / 2, 0.3, 1.2 * Math.Sin((t - 20) * Math.PI / 8), 0, Susp, 0.6, 0, 0);   // 右コーナー
        if (t < 31) return new(Cruise - 12 * (t - 28), 0, 200, 0, 3, 4000, 0, 0, 0, -1.2, 0.7, 0.3, 0, 0);                                        // ブレーキ
        const double After = 29;
        if (t < 33) return new(After, 200, 0, -40, 3, 4500, Watts(4500), -0.2, -0.8, 0, Susp, 0.4, 0, 0);                                          // 左へ
        if (t < 34) return new(After, 150, 0, 0, 3, 4500, Watts(4500), 0, 0, 0, 0, 0.1, 0, t < 33.5 ? 3 : -3);                                     // ジャンプ
        if (t < 34.1) return new(After, 150, 0, 0, 3, 4500, Watts(4500), 0, 0, 3.5, 1, 0.5, 0, 0);                                                 // 着地
        if (t < 40) return new(After, 150, 0, 0, 3, 4500, Watts(4500), 0, 0, 0.1, Susp, 0.2, 0, 0);
        if (t < 43) return new(After, 120, 0, 30, 3, 4500, Watts(4500) / 2, 0.1, 0.3, 0, Susp, 1.5, 0.8, 0);                                       // 路面の逸脱とスリップ
        if (t < 46) return new(After, 150, 0, 0, 3, 4500, Watts(4500), 0, 0, 0.1, Susp, 0.2, 0, 0);
        if (t < 46.1) return new(After - 8, 0, 0, 0, 3, 3500, 0, 0, 0, -5, Susp, 0.5, 0, 0);                                                      // 接触
        return new(After - 8, 150, 0, 0, 3, 3500, Watts(3500), 0, 0, 0.1, Susp, 0.2, 0, 0);
    }

    /// <summary>エンジンの出力(W)。5000 rpm が頂点</summary>
    private static double Watts(double rpm) => 745.7 * (400 - 40 * Math.Pow((rpm - 5000) / 1000, 2));

    private static Dictionary<string, double> Row(Dictionary<string, double> r, bool on, double t)
    {
        r.Clear();
        r[TelemetryTable.RecvTime] = Zero + 2 + t;
        r["timestamp_ms"] = 1000 + Math.Round(t * 1000);
        r["is_race_on"] = on ? 1 : 0;
        if (!on) return r;
        r["car_ordinal"] = 1234;
        r["car_class"] = 5;
        r["car_pi"] = 800;
        r["drivetrain"] = 2;
        r["num_cylinders"] = 6;
        r["dist_traveled"] = -50;
        r["gear"] = 1;
        foreach (var w in Wheels) r["norm_suspension_travel_" + w] = 0.5;
        return r;
    }

    private static void Line(StringBuilder sb, string[] columns, Dictionary<string, double> r)
    {
        for (int c = 0; c < columns.Length; c++)
        {
            if (c > 0) sb.Append(',');
            var v = r.GetValueOrDefault(columns[c]);
            sb.Append((columns[c] == TelemetryTable.RecvTime ? v : Math.Round(v, 4)).ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append('\n');
    }
}
