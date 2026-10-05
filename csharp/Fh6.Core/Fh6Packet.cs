using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Fh6.Core;

public enum FieldType { I32, U32, F32, U16, U8, S8 }

public readonly record struct FieldDef(string Name, int Offset, FieldType Type);

/// <summary>
/// FH6 Data Out (324 バイト, 88 フィールド)。
/// Sled 部 232 バイト + FH6 追加 12 バイト(car_group / smashable_vel_diff / smashable_mass) + Dash 部。
/// 最終 1 バイトはパディング。
/// </summary>
public static class Fh6Packet
{
    public const int Size = 324;

    public static readonly FieldDef[] Fields = Build();
    private static readonly Dictionary<string, int> IndexOf =
        Fields.Select((f, i) => (f.Name, i)).ToDictionary(t => t.Name, t => t.i);

    public static readonly int IsRaceOn = Idx("is_race_on");
    public static readonly int TimestampMs = Idx("timestamp_ms");
    public static readonly int CarOrdinal = Idx("car_ordinal");
    public static readonly int CarClass = Idx("car_class");
    public static readonly int CarPi = Idx("car_pi");
    public static readonly int Drivetrain = Idx("drivetrain");
    public static readonly int NumCylinders = Idx("num_cylinders");
    public static readonly int Speed = Idx("speed");
    public static readonly int DistTraveled = Idx("dist_traveled");
    public static readonly int CurRaceTime = Idx("cur_race_time");
    public static readonly int RacePos = Idx("race_pos");
    public static readonly int PositionX = Idx("position_x");
    public static readonly int PositionZ = Idx("position_z");
    public static readonly int Yaw = Idx("yaw");

    private static int Idx(string name) => IndexOf[name];

    private static FieldDef[] Build()
    {
        var list = new List<FieldDef>(88);
        int off = 0;
        void Add(string name, FieldType t)
        {
            list.Add(new FieldDef(name, off, t));
            off += t switch { FieldType.U16 => 2, FieldType.U8 or FieldType.S8 => 1, _ => 4 };
        }
        void Add4(string prefix, FieldType t)
        {
            foreach (var w in new[] { "fl", "fr", "rl", "rr" }) Add($"{prefix}_{w}", t);
        }

        // ---- Sled ----
        Add("is_race_on", FieldType.I32);
        Add("timestamp_ms", FieldType.U32);
        Add("engine_max_rpm", FieldType.F32);
        Add("engine_idle_rpm", FieldType.F32);
        Add("current_engine_rpm", FieldType.F32);
        Add("acceleration_x", FieldType.F32); Add("acceleration_y", FieldType.F32); Add("acceleration_z", FieldType.F32);
        Add("velocity_x", FieldType.F32); Add("velocity_y", FieldType.F32); Add("velocity_z", FieldType.F32);
        Add("angular_velocity_x", FieldType.F32); Add("angular_velocity_y", FieldType.F32); Add("angular_velocity_z", FieldType.F32);
        Add("yaw", FieldType.F32); Add("pitch", FieldType.F32); Add("roll", FieldType.F32);
        Add4("norm_suspension_travel", FieldType.F32);
        Add4("tire_slip_ratio", FieldType.F32);
        Add4("wheel_rotation_speed", FieldType.F32);
        Add4("wheel_on_rumble_strip", FieldType.I32);
        Add4("wheel_in_puddle", FieldType.F32);
        Add4("surface_rumble", FieldType.F32);
        Add4("tire_slip_angle", FieldType.F32);
        Add4("tire_combined_slip", FieldType.F32);
        Add4("suspension_travel_meters", FieldType.F32);
        Add("car_ordinal", FieldType.I32);
        Add("car_class", FieldType.I32);
        Add("car_pi", FieldType.I32);
        Add("drivetrain", FieldType.I32);
        Add("num_cylinders", FieldType.I32);

        // ---- FH6 追加 (offset 232) ----
        Add("car_group", FieldType.I32);
        Add("smashable_vel_diff", FieldType.F32);
        Add("smashable_mass", FieldType.F32);

        // ---- Dash (offset 244) ----
        Add("position_x", FieldType.F32); Add("position_y", FieldType.F32); Add("position_z", FieldType.F32);
        Add("speed", FieldType.F32);
        Add("power", FieldType.F32);
        Add("torque", FieldType.F32);
        Add4("tire_temp", FieldType.F32);
        Add("boost", FieldType.F32);
        Add("fuel", FieldType.F32);
        Add("dist_traveled", FieldType.F32);
        Add("best_lap", FieldType.F32);
        Add("last_lap", FieldType.F32);
        Add("cur_lap", FieldType.F32);
        Add("cur_race_time", FieldType.F32);
        Add("lap_no", FieldType.U16);
        Add("race_pos", FieldType.U8);
        Add("accel", FieldType.U8);
        Add("brake", FieldType.U8);
        Add("clutch", FieldType.U8);
        Add("handbrake", FieldType.U8);
        Add("gear", FieldType.U8);
        Add("steer", FieldType.S8);
        Add("norm_driving_line", FieldType.S8);
        Add("norm_ai_brake_diff", FieldType.S8);

        if (list.Count != 88 || off != Size - 1)
            throw new InvalidOperationException($"packet layout mismatch: {list.Count} fields, {off} bytes");
        return list.ToArray();
    }

    /// <summary>values は Fields.Length 以上の長さ。data は Size バイト以上</summary>
    public static void Parse(ReadOnlySpan<byte> data, double[] values)
    {
        var fields = Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var s = data.Slice(f.Offset);
            values[i] = f.Type switch
            {
                FieldType.I32 => BinaryPrimitives.ReadInt32LittleEndian(s),
                FieldType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                FieldType.F32 => BinaryPrimitives.ReadSingleLittleEndian(s),
                FieldType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                FieldType.U8 => s[0],
                FieldType.S8 => (sbyte)s[0],
                _ => 0
            };
        }
    }

    public static string CsvHeader()
    {
        var sb = new StringBuilder("recv_time");
        foreach (var f in Fields) sb.Append(',').Append(f.Name);
        return sb.ToString();
    }

    public static void AppendCsvRow(StringBuilder sb, double recvTime, double[] values)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append(recvTime.ToString("F6", inv));
        var fields = Fields;
        for (int i = 0; i < fields.Length; i++)
        {
            sb.Append(',');
            if (fields[i].Type == FieldType.F32)
            {
                float v = (float)values[i];
                // NaN/Inf はそのまま書くと pandas が文字列扱いになるので空欄にする
                if (float.IsFinite(v)) sb.Append(v.ToString(inv));
            }
            else
            {
                sb.Append(((long)values[i]).ToString(inv));
            }
        }
    }

    public static string ClassName(int c) => c switch
    {
        0 => "D", 1 => "C", 2 => "B", 3 => "A", 4 => "S1", 5 => "S2", 6 => "X", _ => $"?{c}"
    };

    public static string DrivetrainName(int d) => d switch
    {
        0 => "FWD", 1 => "RWD", 2 => "AWD", _ => $"?{d}"
    };
}
