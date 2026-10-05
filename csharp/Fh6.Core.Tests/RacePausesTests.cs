using Fh6.Core;

namespace Fh6.Core.Tests;

public class RacePausesTests
{
    private sealed class Rows
    {
        public readonly List<double> T = new(), On = new(), Rt = new(), X = new(), Z = new();
        private double _t, _rt, _z;

        /// <summary>走る(1/60 秒ごと、経過時間も進む)</summary>
        public Rows Drive(double sec, double kmh = 100)
        {
            for (int i = 0; i < sec * 60; i++) { _t += 1 / 60.0; _rt += 1 / 60.0; _z += kmh / 3.6 / 60; Add(true); }
            return this;
        }

        /// <summary>止まっている(走行中のまま。スピンなど)。経過時間は進む</summary>
        public Rows Still(double sec)
        {
            for (int i = 0; i < sec * 60; i++) { _t += 1 / 60.0; _rt += 1 / 60.0; Add(true); }
            return this;
        }

        /// <summary>停止区間。raceTimeAfter: 明けたときの経過時間の変化、moveAfter: 明けたときの位置の変化(m)</summary>
        public Rows Off(double sec, double raceTimeAfter = 0.02, double moveAfter = 0)
        {
            _t += 1 / 60.0; Add(false);
            _t += sec; Add(false);
            _rt += raceTimeAfter; _z += moveAfter;
            return this;
        }

        private void Add(bool on)
        {
            T.Add(_t); On.Add(on ? 1 : 0); Rt.Add(on ? _rt : 0); X.Add(on ? 0 : 0); Z.Add(on ? _z : 0);
        }

        public List<(int Last, int Resume)> Find() =>
            RacePauses.Find(T.ToArray(), On.ToArray(), Rt.ToArray(), X.ToArray(), Z.ToArray(), 0, T.Count - 1);
    }

    [Fact]
    public void フォトモードやメニューは一時停止()
    {
        var r = new Rows().Drive(3);
        int last = r.T.Count - 1;
        r.Off(34.7).Drive(5);   // 173311 のフォトモード: 34.7 秒、経過時間 +0.02、位置そのまま
        var p = Assert.Single(r.Find());
        Assert.Equal(last, p.Last);
        Assert.Equal(last + 3, p.Resume);
    }

    [Fact]
    public void 短い停止区間とリワインドとリザルト画面とスピンは対象外()
    {
        Assert.Empty(new Rows().Drive(3).Off(1.0).Drive(3).Find());                                 // 2 秒未満
        Assert.Empty(new Rows().Drive(3).Off(4, raceTimeAfter: -2.5, moveAfter: -80).Drive(3).Find()); // リワインド
        Assert.Empty(new Rows().Drive(3).Off(15, raceTimeAfter: 12.5, moveAfter: 139).Drive(3).Find()); // 経過時間が進む(リザルト画面)
        Assert.Empty(new Rows().Drive(3).Still(3.6).Drive(3).Find());                               // スピンで停止(走行中のまま)
        Assert.Empty(new Rows().Drive(3).Off(5).Find());                                            // 停止区間のままレースが終わる
    }
}
