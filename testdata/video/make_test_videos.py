"""VideoDuration のテスト用 MP4 を作る(ffmpeg なしで、箱の構造だけを組み立てる)。

中身の映像データはダミーで再生はできない。長さの読み取りに使う箱
(mvhd / mdhd / hdlr / mvex / moof / tfhd / tfdt / trun)は OBS の出力と同じ並びにしている。

- normal.mp4     : 通常の MP4。映像 4.5 秒、音声 4.6 秒(mvhd は長い方の 4.6 秒)。期待値は映像の 4.5 秒
- fragmented.mp4 : Fragmented MP4。moov の長さは 0 で、moof 3 個(各 1 秒)。映像 3.0 秒、音声 3.1 秒。期待値は 3.0 秒

使い方: python make_test_videos.py(このフォルダに書き出す)
"""
import struct
from pathlib import Path

HERE = Path(__file__).resolve().parent


def box(typ: str, *payload: bytes) -> bytes:
    body = b"".join(payload)
    return struct.pack(">I", 8 + len(body)) + typ.encode("ascii") + body


def full(typ: str, version: int, flags: int, *payload: bytes) -> bytes:
    return box(typ, struct.pack(">I", (version << 24) | flags), *payload)


def mvhd(timescale: int, duration: int, next_track: int) -> bytes:
    return full(
        "mvhd", 0, 0,
        struct.pack(">IIII", 0, 0, timescale, duration),
        struct.pack(">IH", 0x00010000, 0x0100), b"\0" * 10,
        struct.pack(">9I", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000),
        b"\0" * 24, struct.pack(">I", next_track),
    )


def tkhd(track_id: int, duration: int, video: bool) -> bytes:
    w, h = (1920 << 16, 1080 << 16) if video else (0, 0)
    return full(
        "tkhd", 0, 3,
        struct.pack(">IIIII", 0, 0, track_id, 0, duration), b"\0" * 8,
        struct.pack(">hhhH", 0, 0, 0 if video else 0x0100, 0),
        struct.pack(">9I", 0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000),
        struct.pack(">II", w, h),
    )


def mdhd(timescale: int, duration: int) -> bytes:
    return full("mdhd", 0, 0, struct.pack(">IIIIHH", 0, 0, timescale, duration, 0x55C4, 0))


def hdlr(video: bool) -> bytes:
    kind, name = (b"vide", b"VideoHandler\0") if video else (b"soun", b"SoundHandler\0")
    return full("hdlr", 0, 0, struct.pack(">I", 0), kind, b"\0" * 12, name)


def minf(video: bool, stbl_children: bytes) -> bytes:
    header = full("vmhd", 0, 1, b"\0" * 8) if video else full("smhd", 0, 0, b"\0" * 4)
    dinf = box("dinf", full("dref", 0, 0, struct.pack(">I", 1), full("url ", 0, 1)))
    stsd = full("stsd", 0, 0, struct.pack(">I", 0))
    return box("minf", header, dinf, box("stbl", stsd, stbl_children))


def trak(track_id: int, video: bool, timescale: int, duration: int, movie_duration: int, stbl_children: bytes) -> bytes:
    return box(
        "trak",
        tkhd(track_id, movie_duration, video),
        box("mdia", mdhd(timescale, duration), hdlr(video), minf(video, stbl_children)),
    )


def sample_tables(count: int, delta: int, sample_size: int, chunk_offset: int) -> bytes:
    return b"".join([
        full("stts", 0, 0, struct.pack(">III", 1, count, delta)),
        full("stsc", 0, 0, struct.pack(">IIII", 1, 1, count, 1)),
        full("stsz", 0, 0, struct.pack(">III", sample_size, count, 0)),
        full("stco", 0, 0, struct.pack(">II", 1, chunk_offset)),
    ])


FTYP = box("ftyp", b"isom", struct.pack(">I", 512), b"isomiso2avc1mp41")


def make_normal() -> bytes:
    # 映像: timescale 15360、30fps(1フレーム 512)で 135 フレーム = 4.5 秒
    # 音声: timescale 48000 で 4.6 秒(duration 220800)。mvhd も長い方の 4.6 秒にする
    v_ts, v_frames, v_delta = 15360, 135, 512
    a_ts, a_dur = 48000, 220800
    movie_ts = 1000
    movie_dur = 4600
    v_size, a_size = 120, 16

    def build(mdat_offset: int) -> bytes:
        a_frames = a_dur // 1024
        moov = box(
            "moov",
            mvhd(movie_ts, movie_dur, 3),
            trak(1, True, v_ts, v_frames * v_delta, 4500,
                 sample_tables(v_frames, v_delta, v_size, mdat_offset)),
            trak(2, False, a_ts, a_dur, movie_dur,
                 sample_tables(a_frames, 1024, a_size, mdat_offset + v_frames * v_size)),
        )
        return moov

    moov = build(0)
    mdat_offset = len(FTYP) + len(moov) + 8
    moov = build(mdat_offset)
    payload = b"\x00" * (135 * v_size + (a_dur // 1024) * a_size)
    return FTYP + moov + box("mdat", payload)


def make_fragmented() -> bytes:
    v_ts, a_ts = 15360, 48000
    v_frames, v_delta = 30, 512          # 1 フラグメント = 映像 1 秒
    v_size, a_size = 120, 16

    moov = box(
        "moov",
        mvhd(1000, 0, 3),
        box("mvex",
            full("trex", 0, 0, struct.pack(">IIIII", 1, 1, 0, 0, 0)),
            full("trex", 0, 0, struct.pack(">IIIII", 2, 1, 0, 0, 0))),
        trak(1, True, v_ts, 0, 0, sample_tables(0, 0, 0, 0)),
        trak(2, False, a_ts, 0, 0, sample_tables(0, 0, 0, 0)),
    )

    out = [FTYP, moov]
    a_time = 0
    for i in range(3):
        # 映像: tfhd に既定の長さ(flags 0x08)、trun は data_offset(0x001)のみ
        v_traf = box(
            "traf",
            full("tfhd", 0, 0x08 | 0x10, struct.pack(">III", 1, v_delta, v_size)),
            full("tfdt", 1, 0, struct.pack(">Q", i * v_frames * v_delta)),
            full("trun", 0, 0x001, struct.pack(">Ii", v_frames, 0)),
        )
        # 音声: サンプルごとの長さ(flags 0x100)。最後のフラグメントだけ長くして全体 3.1 秒にする
        a_count = 47 if i < 2 else 52
        durations = [1024] * a_count
        if i == 2:
            durations[-1] = 3 * a_ts + a_ts // 10 - a_time - 1024 * (a_count - 1)
        a_traf = box(
            "traf",
            full("tfhd", 0, 0, struct.pack(">I", 2)),
            full("tfdt", 1, 0, struct.pack(">Q", a_time)),
            full("trun", 0, 0x001 | 0x100, struct.pack(">Ii", a_count, 0), *(struct.pack(">I", d) for d in durations)),
        )
        a_time += sum(durations)
        moof = box("moof", full("mfhd", 0, 0, struct.pack(">I", i + 1)), v_traf, a_traf)
        out.append(moof)
        out.append(box("mdat", b"\x00" * (v_frames * v_size + a_count * a_size)))
    assert a_time == 3 * a_ts + a_ts // 10
    return b"".join(out)


if __name__ == "__main__":
    for name, data in (("normal.mp4", make_normal()), ("fragmented.mp4", make_fragmented())):
        (HERE / name).write_bytes(data)
        print(f"{name}: {len(data)} bytes")
