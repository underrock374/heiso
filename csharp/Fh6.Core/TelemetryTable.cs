using System.Globalization;
using System.IO.Compression;

namespace Fh6.Core;

/// <summary>
/// telemetry.csv.gz を列ごとの配列で読む。列はヘッダーの名前で引く(位置に依存しない)。
/// 空欄は NaN。必要な列だけ読める(1時間の記録で全列は約 1,000 万値になるため)。
/// </summary>
public sealed class TelemetryTable
{
    public const string RecvTime = "recv_time";

    private readonly Dictionary<string, double[]> _columns;

    public int RowCount { get; }
    public IReadOnlyCollection<string> Columns => _columns.Keys;

    private TelemetryTable(Dictionary<string, double[]> columns, int rows)
    {
        _columns = columns;
        RowCount = rows;
    }

    public bool Contains(string name) => _columns.ContainsKey(name);

    public double[] this[string name] =>
        _columns.TryGetValue(name, out var c) ? c : throw new KeyNotFoundException($"列 {name} を読んでいません");

    /// <summary>
    /// columns が null なら全列を読む。指定した列がファイルに無ければ、その列は含めない(Contains で確かめる)。
    /// .gz ならそのまま展開して読む。
    /// </summary>
    public static TelemetryTable Load(string path, IEnumerable<string>? columns = null)
    {
        using var file = File.OpenRead(path);
        using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(stream, bufferSize: 1 << 16);
        return Load(reader, columns);
    }

    public static TelemetryTable Load(TextReader reader, IEnumerable<string>? columns = null)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException(Strings.T("CSV が空です"));
        var names = header.TrimStart('﻿').Split(',');
        var wanted = columns == null ? null : new HashSet<string>(columns);

        // 読む列: ファイル内の位置 → 出力先の番号
        var targetOf = new int[names.Length];
        var picked = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            var n = names[i].Trim();
            if ((wanted == null || wanted.Contains(n)) && !picked.Contains(n))
            {
                targetOf[i] = picked.Count;
                picked.Add(n);
            }
            else
            {
                targetOf[i] = -1;
            }
        }

        var lists = picked.Select(_ => new List<double>(1 << 16)).ToArray();
        int rows = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var span = line.AsSpan();
            int col = 0;
            while (col < names.Length)
            {
                int comma = span.IndexOf(',');
                var field = comma < 0 ? span : span[..comma];
                int t = targetOf[col];
                if (t >= 0) lists[t].Add(ParseField(field));
                col++;
                if (comma < 0) break;
                span = span[(comma + 1)..];
            }
            // 列が足りない行は NaN で埋める
            for (; col < names.Length; col++)
                if (targetOf[col] >= 0) lists[targetOf[col]].Add(double.NaN);
            rows++;
        }

        var dict = new Dictionary<string, double[]>(picked.Count);
        for (int i = 0; i < picked.Count; i++) dict[picked[i]] = lists[i].ToArray();
        return new TelemetryTable(dict, rows);
    }

    private static double ParseField(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        if (s.IsEmpty) return double.NaN;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}
