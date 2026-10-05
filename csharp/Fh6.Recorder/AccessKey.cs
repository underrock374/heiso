using System.Security.Cryptography;

namespace Fh6.Recorder;

/// <summary>URL に付ける合言葉(k=...)。一度作ったら保存して使い回す(スマホのブックマークが使えるように)</summary>
public sealed class AccessKey
{
    public string Value { get; }

    public AccessKey(RecorderOptions opt)
    {
        var dir = opt.ResolveOutputDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".access_key");
        if (File.Exists(path))
        {
            var s = File.ReadAllText(path).Trim();
            if (s.Length >= 6) { Value = s; return; }
        }
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        Value = new string(Enumerable.Range(0, 10).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
        File.WriteAllText(path, Value);
    }
}
