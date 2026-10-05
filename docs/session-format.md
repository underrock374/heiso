# 記録データの形式(session-format)

HEISO の記録アプリ(`csharp/Fh6.Recorder`、書き出し処理は `csharp/Fh6.Core`)が書き出し、再生アプリ(`csharp/Fh6.Player`)が読む
データの約束事。**形式を変えるときは、先にこの文書を直す。**

- 形式の識別子: `session.json` の `format` = `fh6-recorder/1`
  (アプリ名の変更に合わせて `heiso-session/1` へ改める予定。読み込み側は両方を受け付けること)
- 後方互換を壊す変更(列の削除・意味の変更)は識別子の数字を上げる。項目の追加は数字を変えない

## セッションフォルダ

保存先(既定は「ビデオ\FH6Recorder」、`OutputDir` で変更可)に、記録1回ごとに `yyyyMMdd_HHmmss` のフォルダを作る。

フォルダ名は、先頭が `yyyyMMdd_HHmmss`(記録を始めた日時)なら、後ろにコメントを付けてよい(`SessionFolderName`)。

- 記録アプリは、記録を始めるときにメモを入れていれば `yyyyMMdd_HHmmss#メモ` にする(フォルダ名に使えない文字は `_` に置き換え、40 文字まで)
- 利用者が後から名前を変えてもよい。日時とコメントの間の記号は無くても、`#` 以外(`＃` `_` `-` 空白など)でもよい。例: `20260927_211710#夏_鳥野山`、`20260927_211710夏_鳥野山`
- ファイルの場所は `session.json` の中でフォルダからの相対で書くので、名前を変えても開ける。フォルダ名を記録の識別に使うときは先頭の日時の部分だけを使う

| ファイル | 内容 |
|---|---|
| `telemetry.csv.gz` | テレメトリー(gzip 圧縮の CSV) |
| `telemetry.raw.gz` | 受信した生パケット全件(`SaveRawPackets` が true のとき) |
| `session.json` | メタデータ・動画との同期情報・統計 |
| 動画(例: `2026-09-27_11-01-04.mp4`) | OBS 30 以降なら録画中だけ録画先をこのフォルダに切り替えて保存。古い OBS では OBS の既定フォルダに残り、`video.output_path` にパスが入る |

## telemetry.csv.gz

- 1 行目はヘッダー。列は `recv_time` + パケットの 88 フィールド(計 89 列)
- `recv_time`: 受信した PC の時刻。**epoch 秒(UTC)の小数**。動画との同期はすべてこの時計で行う
- 数値は invariant culture(小数点は `.`)。float の NaN/∞ は空欄
- **アイドル行の圧縮**: `is_race_on == 0` が続く区間は、先頭行と末尾行だけを残す。区間の長さは行数ではなく `recv_time` の差で求めること
- サンプリング間隔は一定ではない(ゲームのフレームレートに等しい。実測 30Hz)。積分・微分は `timestamp_ms` か `recv_time` の実差分で行う

### 列(パケットのフィールド)

パケットは 324 バイト固定、リトルエンディアン。オフセットはパケット内のバイト位置(最終 1 バイトはパディング)。

| 列 | オフセット | 型 | 単位・意味 |
|---|---|---|---|
| `is_race_on` | 0 | int32 | 0/1。0 の間は全フィールドがゼロ埋め(メニュー・ロード・リワインド演出) |
| `timestamp_ms` | 4 | uint32 | ms。ゲーム内の時計。OFF 区間中も壁時計どおり進む |
| `engine_max_rpm` | 8 | float32 | rpm |
| `engine_idle_rpm` | 12 | float32 | rpm |
| `current_engine_rpm` | 16 | float32 | rpm |
| `acceleration_x` | 20 | float32 | m/s²(車体座標)。±196 付近で頭打ち |
| `acceleration_y` | 24 | float32 | m/s²(車体座標) |
| `acceleration_z` | 28 | float32 | m/s²(車体座標) |
| `velocity_x` | 32 | float32 | m/s(車体座標) |
| `velocity_y` | 36 | float32 | m/s(車体座標) |
| `velocity_z` | 40 | float32 | m/s(車体座標) |
| `angular_velocity_x` | 44 | float32 | rad/s |
| `angular_velocity_y` | 48 | float32 | rad/s。正 = 右旋回 |
| `angular_velocity_z` | 52 | float32 | rad/s |
| `yaw` | 56 | float32 | rad |
| `pitch` | 60 | float32 | rad |
| `roll` | 64 | float32 | rad |
| `norm_suspension_travel_fl/fr/rl/rr` | 68〜80 | float32 | 0.0 = 伸び切り、1.0 = 縮み切り |
| `tire_slip_ratio_fl/fr/rl/rr` | 84〜96 | float32 | 正規化値 |
| `wheel_rotation_speed_fl/fr/rl/rr` | 100〜112 | float32 | rad/s |
| `wheel_on_rumble_strip_fl/fr/rl/rr` | 116〜128 | int32 | 0/1 |
| `wheel_in_puddle_fl/fr/rl/rr` | 132〜144 | float32 | 実測では 0/1。渡河で 1、雨では反応しない |
| `surface_rumble_fl/fr/rl/rr` | 148〜160 | float32 | 路面の振動量。値と路面の対応はフィールド解釈メモ参照 |
| `tire_slip_angle_fl/fr/rl/rr` | 164〜176 | float32 | 正規化値。1.0 = 限界グリップ |
| `tire_combined_slip_fl/fr/rl/rr` | 180〜192 | float32 | 正規化値 |
| `suspension_travel_meters_fl/fr/rl/rr` | 196〜208 | float32 | m |
| `car_ordinal` | 212 | int32 | 車種 ID |
| `car_class` | 216 | int32 | クラス番号。FH6 での番号と表記の対応は未確認(PI 738 で 4 を観測) |
| `car_pi` | 220 | int32 | PI |
| `drivetrain` | 224 | int32 | 0 = FWD、1 = RWD、2 = AWD |
| `num_cylinders` | 228 | int32 | 気筒数 |
| `car_group` | 232 | int32 | FH6 追加。意味は未確認 |
| `smashable_vel_diff` | 236 | float32 | FH6 追加。信頼性が低い |
| `smashable_mass` | 240 | float32 | FH6 追加。信頼性が低い |
| `position_x` | 244 | float32 | m。世界座標、x = 東 |
| `position_y` | 248 | float32 | m。世界座標、y = 上 |
| `position_z` | 252 | float32 | m。世界座標、z = 北(左手系) |
| `speed` | 256 | float32 | m/s |
| `power` | 260 | float32 | W。リミッター付近で負値 |
| `torque` | 264 | float32 | Nm |
| `tire_temp_fl/fr/rl/rr` | 268〜280 | float32 | °F |
| `boost` | 284 | float32 | psi |
| `fuel` | 288 | float32 | 0〜1 |
| `dist_traveled` | 292 | float32 | ゲーム内単位(メートルではない)。レース中だけ非 0、負値から始まる |
| `best_lap` | 296 | float32 | 秒 |
| `last_lap` | 300 | float32 | 秒 |
| `cur_lap` | 304 | float32 | 秒 |
| `cur_race_time` | 308 | float32 | 秒。リワインドで巻き戻る。フリーロームでもリセットされる |
| `lap_no` | 312 | uint16 | 周回番号 |
| `race_pos` | 314 | uint8 | 順位。0 = レース外 |
| `accel` | 315 | uint8 | 0〜255 |
| `brake` | 316 | uint8 | 0〜255 |
| `clutch` | 317 | uint8 | 0〜255 |
| `handbrake` | 318 | uint8 | 0〜255 |
| `gear` | 319 | uint8 | 0 = 停止/ニュートラル相当、11 = 変速動作中(実ギアではない) |
| `steer` | 320 | int8 | -127〜127。正 = 右 |
| `norm_driving_line` | 321 | int8 | -127〜127。意味は未確定 |
| `norm_ai_brake_diff` | 322 | int8 | -127〜127。意味は未確定 |

## telemetry.raw.gz

パーサー修正後に CSV を作り直すための生データ。gzip の中身は次のバイナリ(リトルエンディアン):

```
"FH6RAW01"(8 バイト ASCII) | uint16 パケット長(324)
以降、受信 1 回ごとに: double recv_time | uint16 len | byte[len] パケット
```

長さが 324 未満の不正パケットもそのまま入る(CSV には入らない)。アイドル行の圧縮はしない。

## session.json

キーは snake_case。

| キー | 内容 |
|---|---|
| `format` / `app_version` | 形式の識別子と、書き出したアプリの版 |
| `complete` | 記録が正常に終わったら true。記録中は 30 秒ごとに false のまま途中保存される |
| `session` | `label` `note` `is_reference` `autodrive` `weather` `time_of_day` `car_hint` `tune_id` `assists{abs,tcs,stm,line,shift}`(手入力用。未入力は null)、`started_at` / `ended_at`(ローカル時刻 ISO 8601)、`started_recv_time` / `ended_recv_time` |
| `files` | `telemetry_csv`、`telemetry_raw`、`video`(フォルダ内なら相対名、外なら絶対パス) |
| `video` | 録画と同期の情報(次節) |
| `markers[]` | 手動マーカー。`recv_time`、`timestamp_ms`、`label` |
| `events[]` | 記録中の出来事。`recv_time`、`type`、`detail`(人が読む説明。任意)、`data`(出来事ごとの値のオブジェクト。任意。下記) |
| `stats` | `packets_received` `rows_written` `idle_rows_collapsed` `effective_hz` `median_interval_ms` `interval_jumps`(100ms 超の間隔の数) `max_interval_ms` `bad_size_packets` `race_state_transitions` `duration_sec` `cars_seen`(`"car_ordinal|クラスPI|駆動方式"`) |

`events[].type` の値: `logging_started` `obs_quality_applied` `obs_quality_failed` `obs_scene_applied` `obs_scene_failed` `obs_scene_restored` `obs_scene_restore_failed` `obs_record_started` `obs_record_stopped` `obs_record_stopped_externally` `obs_set_record_directory_failed` `obs_stop_failed` `obs_quality_restored` `obs_quality_restore_failed` `video_duration_unavailable` `logging_stopped`、および次の車両・レース・コースの出来事

### 車両・レース・コースの出来事(`events[]`、2026-09-28 から)

記録アプリの判定(`Fh6.Core` の `RaceTracker`)で見つけた出来事。`recv_time` はその出来事を見つけたパケットの受信時刻で、`telemetry.csv.gz` の行の `recv_time` と同じ値になる。
`data` のキーは次のとおり(`timestamp_ms` はそのパケットの値)。

| `type` | いつ | `data` |
|---|---|---|
| `car_detected` | 車両の組(車種・クラス・PI・駆動方式・気筒数)が 30 パケット続けて変わった。記録の開始時には、そのときの車両で1回書く | `timestamp_ms`、`ordinal`、`class_raw`、`pi`、`drivetrain_raw`、`cylinders`、`changed`(何が変わったか。`記録開始時の車両` `車種` `PI` `駆動方式` `気筒数` `セッティング(手動)` の配列)、`name`・`setup_id`・`setup_name`(登録済みなら。無ければ null) |
| `car_confirmed` | 画面で車名・セッティングを確定した | `ordinal`、`name`、`setup_id`、`setup_name`、`class_raw`、`pi`、`drivetrain_raw`、`cylinders`、`note` |
| `race_start` | レースが始まった。(a) `race_pos` が 0 → 1 以上、または (b) 停止区間の明けに `race_pos` が 1 以上のまま `cur_race_time` が 0.5 秒未満に戻った(直前のレース中の値は 3 秒超) | `timestamp_ms`、`restart`((b) で見つけたら true)、`start`(`x`・`z`・`yaw`)、`matched`(登録済みのコースと一致したか)、`course_id`・`course_name`(一致したら)、`grid_offset_m`(一致したら。登録済みのスタート地点から後方への距離 m)、`car_setup_id` |
| `race_end` | `race_pos` が 1 以上 → 0(停止区間の間は見ない) | `timestamp_ms`、`last_speed_kmh`、`course_id` |
| `course_confirmed` | 画面でコースを登録した、または登録済みのコースを選んだ | `course_id`、`course_name`、`course_kind`、`race_start_recv_time`(どのレース開始についてか)、`start` |
| `race_edited` | 記録アプリの「直前のレース」か、再生アプリの「名前を付ける」でコース・車名・セッティングを保存した(何も変えずに保存しても、そのときの名前を書く。`changes` は `["確認"]`) | `race_start_recv_time`(どのレース開始についてか)、`changes`(何を直したか)、`course_id`・`course_name`・`course_kind`、`car_ordinal`・`car_name`、`setup_id`・`setup_name`(直した後の値。分からなければ null)、`source`(再生アプリが書いたものだけ `player`。新しく登録するコース・セッティングは、まだ登録ファイルの id が無いので `course_id`・`setup_id` は null) |

記録していない間に起きた出来事は書かない(登録ファイルは記録していなくても更新する)。

**記録を止めた後の追記**: `course_confirmed` と `race_edited` は、そのレースが記録中に始まったものなら、記録を止めた後でもそのセッションの `session.json` の `events` の末尾に足す(`complete: true` のまま。`recv_time` は直した時刻なので、`logging_stopped` より後になる)。

**レースの名前の決め方**(再生アプリ。`Fh6.Core` の `RaceNames`): コース名は、そのレース開始の `race_start` → `course_confirmed` → `race_edited` の順に、後のものほど優先する。車名とセッティング名は、レース開始の時点で最後の `car_detected`(か `car_confirmed`)の値に、同じ車両を確定した `car_confirmed`(レースの後でも、次に車両が変わるか次のレースが始まるまで)、後から直した `race_edited` を重ねる。

### 登録ファイル(`fh6_registry.json`)

車名・セッティング名・コースを、セッションをまたいで覚えておくファイル。保存先フォルダの直下に置く。

| キー | 内容 |
|---|---|
| `registry_version` | 1 |
| `cars` | `{"<car_ordinal>": {"name", "updated"}}` |
| `setups[]` | `id`(`S0001` から)、`ordinal`、`cls`(`car_class` の番号)、`pi`、`drive`(`drivetrain` の番号)、`cyl`、`variant`(同じ組の何番目か)、`name`、`note`、`created` |
| `courses[]` | `id`(`R0001` から)、`name`、`kind`(`road` `dirt` `xc` `street` `drag` `other`)、`x`・`z`・`yaw`(スタート地点)、`note`、`created`、`seen`(一致した回数) |

- コースの照合: 前方ベクトル (sin yaw, cos yaw) に沿って、進行方向 ±110m・横 15m・向き 20° 以内。複数あれば |進行方向のずれ| + 3 × |横のずれ| の小さいほう。グリッドが後ろほど進行方向の真後ろにずれる(8 番手で約 71m)
- 登録済みのスタート地点より 3m を超えて前方のスタートを見たら、スタート地点をそこへ動かす(1 番グリッドに近づける)
- 書くのは記録アプリだけ。再生アプリは読むだけで、登録したいものは下の受け渡し用のファイルに書く

### 登録の受け渡し用のファイル(`fh6_registry_inbox.json`)

再生アプリの「名前を付ける」でレースに付けた名前を、登録ファイルに登録してもらうための頼み(2026-09-28)。保存先フォルダの直下(登録ファイルの隣)に置く。
記録アプリは起動中に登録ファイルを読み書きしているので、再生アプリは登録ファイルを直接書き換えず、ここに頼みを足す(`Fh6.Core` の `RegistryInbox`)。

| キー | 内容 |
|---|---|
| `requests[]` | 頼み 1 つ = 1 つのレースに付けた名前。`created_at`、`source`(`player`)、`session`(記録のフォルダ名)、`race_start_recv_time`(確認用) |
| (コース) | `course_id`(登録済みの id か `new`)、`course_name`、`course_kind`(`new` のとき)、`start_x`・`start_z`・`start_yaw`(レース開始の位置と向き。新しく登録するときに使う) |
| (車) | `car_ordinal`・`class_raw`・`pi`・`drivetrain_raw`・`cylinders`(レース開始の行の車両の組)、`car_name`、`setup_id`(登録済みの id か `new`)、`setup_name` |

- 再生アプリ: 読んで足して、一時ファイルに書いてから置き換える
- 記録アプリ: 起動したときと、動いている間 3 秒ごとに見る。あれば名前を `.taking` に変えてから読み(読んでいる間に足されても失われない)、登録ファイルに取り込んで消す。読めなければ `.broken` にして残す
- 取り込みの決まりは記録アプリの「直前のレース」と同じ: `new` のコースは新しく登録する(スタートの位置で一致する同じ名前のコースがあれば登録しない)、登録済みの id なら名前が違えば変える。車名はその車種の名前にする。セッティングも同じ(同じ組で同じ名前があれば登録しない)。見つからない id は飛ばす
- 書き込みは一時ファイルに書いてから置き換える。読めない・壊れているときは記録を止めず、元のファイルに `.broken` を付けて残し、空として扱う
- 知らないキーは消さずに残す

### video(動画との同期)

| キー | 内容 |
|---|---|
| `requested` / `active` | 録画を求めたか / 録画中か(OBS 側で止められると false) |
| `recorded_into_session_folder` | 録画先をセッションフォルダに切り替えられたか |
| `obs_version` / `output_path` / `error` | OBS の版、録画ファイルのパス、失敗時の理由 |
| `sync_samples[]` | `[recv_time, 動画内の秒, 問い合わせ往復 ms]`。録画中に約 2 秒ごと(開始直後は 0.3 秒おきに 5 回)OBS の `GetRecordStatus.outputDuration` を記録したもの。**実際の動画位置より約 1.0 秒遅れて報告される**(下記の実測)ので、時刻の変換には使わない。動画の時間軸の伸び縮み(`clock_rate`)の確認用 |
| `video_zero_recv_time` | 動画の 0 秒に当たる `recv_time`。**録画開始イベントの受信時刻**(`started_event_recv_time`)。それが無い記録だけ `sync_samples` の最初の 5 点から求める(約 1 秒遅い値になる) |
| `video_zero_method` | `video_zero_recv_time` の求め方。`started_event` か `sync_samples`。このキーが無い記録(2026-09-28 より前)は `sync_samples` 由来で約 1 秒遅いので、`--resync` で作り直す |
| `clock_rate` | `sync_samples` の傾き(動画秒 / 実時間秒)。1.0 なら伸び縮みなし |
| `start_requested_recv_time` | 録画の開始を要求した時刻(OBS へ `StartRecord` を送る直前)。2026-09-28 より前の記録には無い |
| `started_event_recv_time` / `stop_requested_recv_time` / `stopped_event_recv_time` | 録画開始イベントの受信、停止要求の送信、停止イベントの受信の時刻 |
| `file_duration_sec` | 停止後に録画ファイル(MP4)から読んだ実際の長さ |
| `file_fps` | 同じく、録画ファイルの映像のフレームレート(サンプル数 ÷ 長さ。MP4 の `stts`、Fragmented MP4 は `moof` から)。再生アプリのコマ送りに使う。2026-09-28 より前の記録には無い(`--resync` で書き足せる。無ければ再生アプリがファイルから読む) |
| `end_check_sec` | 終端の検算: (停止要求の時刻 − `video_zero_recv_time`)− `file_duration_sec`。0 に近いほど動画 0 秒の時刻が正しい。停止要求の時刻が無い古い記録では停止イベントの受信時刻を使う |
| `manual_sync[]` | 手動補正で合わせた点。`recv_time`(テレメトリー時刻)、`video_sec`(その瞬間の動画内の秒)、`note`(何で合わせたか。任意)、`created_at`(ローカル時刻 ISO 8601)。再生アプリが書く。記録アプリは空配列で書き出す |
| `quality` | 録画の画質の段階(下記)。記録アプリが録画を始めるときに書く。録画しなかった記録は null、2026-09-28 より前の記録にはキーが無い |
| `scene` | 録画した OBS のシーン(下記)。記録アプリが録画を始めるときに書く。録画しなかった記録は null、2026-10-05 より前の記録にはキーが無い |

**テレメトリー時刻 → 動画時刻の変換**(2026-09-28 確定): `動画時刻 = recv_time − 録画開始イベントの受信時刻`。
録画開始イベントの受信時刻が無い記録では、`sync_samples` の点列を区分線形補間する(範囲外は傾き 1 で延長、往復 150ms 超の点は使わない)。この場合は約 1 秒ずれる。
手動補正(`manual_sync`)があれば、さらに全体をずらす(下記)。

確定の根拠(2026-09-27・28 の実測):

- 録画ファイルの長さとの検算: 3 本の記録(1.5〜7.7 分)で、録画開始イベントを 0 秒とすると終端の差は +0.02〜+0.04 秒、`sync_samples` を使うと −0.97 秒
- 目視: 再生アプリで、ゲーム画面のスピードメーター・ギア・ABS 表示とテレメトリーを比べると、録画開始イベント基準で一致(159 km/h・3 速 ↔ 160 km/h・3 速、ブレーキ中 103 km/h ↔ 101 km/h・ブレーキ 83%)。`sync_samples` 基準では約 1 秒先の値になった
- OBS の `outputDuration` は、実際の動画位置より約 1.0 秒遅れて報告されると考えられる(`sync_samples` から逆算した 0 秒 − 録画開始イベント = 0.98〜1.01 秒)

**手動補正(`manual_sync`)**: 映像で分かる瞬間(着地、レース開始など)に合わせた「このテレメトリー時刻 = 動画のこの秒」の点を保存する。
ずれ幅ではなく点を持つのは、自動の変換方法(上記)が変わってもずれ幅を計算し直せるようにするため。自動で求めた値(`video_zero_recv_time` など)は上書きしない。

- 補正のかけ方: 最後の点について「自動の変換で求めた動画秒」と `video_sec` の差を求め、全体に足す(平行にずらす)。複数の点での補正は将来の拡張で、今は最後の1点だけを使う
- 無い、または空配列なら補正しない
- 再生アプリが書き込むのは `complete: true` の記録だけ(記録中は記録アプリが 30 秒ごとに上書きするため)
- 再生アプリの「同期の手動補正」で保存すると、`session.json` を読み直して末尾に 1 点足して書き戻す(知らないキーや、記録アプリが後から足した出来事は残る)。`recv_time` は、グラフで指した瞬間(その時点の変換での動画の秒)を `VideoTimeMap.ToRecvTime` でテレメトリー時刻に直したもの。「補正を消す」は配列を空にする

**録画の画質(`quality`)**: 記録の開始時に選んだ段階と、録画に使った OBS のプロファイル・実際の設定。

| キー | 内容 |
|---|---|
| `preset` | `minimum`(最小)/ `light`(軽量)/ `medium`(中間)/ `standard`(標準)/ `high`(高画質)/ `obs`(OBS の設定のまま。何も変えない) |
| `applied` | 段階のプロファイルに切り替えられたか。`obs` のときと、切り替えられなかったときは false |
| `profile` | 録画に使った OBS のプロファイル(`HEISO 軽量` など)。切り替えたときだけ |
| `output_width` / `output_height` / `fps` | 切り替えた後に OBS から読んだ、実際の出力解像度と fps |
| `output_mode` | 同じく、OBS の出力モード。`Simple`(基本)/ `Advanced`(詳細)。2026-09-28 以降の記録アプリが書く。`--setup-obs-profiles` で作ったプロファイルは `Advanced`(録画のキーフレーム間隔 1 秒) |
| `rec_quality` | 同じく、OBS「基本」出力モードの録画画質。`Stream`(配信と同じ)/ `Small`(高画質・ファイルサイズ中)/ `HQ`(区別のつかない画質)。`output_mode` が `Advanced` のときは、プロファイルを作ったときの「基本」の値(実際の録画は `recordEncoder.json` の設定で、OBS の画面で直されていれば合わない) |
| `bitrate_kbps` | `rec_quality` が `Stream` のときの映像ビットレート(`rec_quality` と同じ注意) |
| `error` | 切り替えられなかった理由(プロファイルが無い、など) |

記録アプリは、段階ごとに用意した OBS のプロファイル「HEISO ○○」へ録画の前に切り替え、録画が終わったら元のプロファイルに戻す。
OBS の設定値そのものは書き換えない(外から書き換えると OBS が録画用のエンコーダを作り直さず、録画が始まらなくなるため。2026-09-27 に実機で確認)。
元のプロファイル名は保存先フォルダの `.recorder_obs_restore.json` にも書き、アプリが途中で落ちたときは次に OBS へつながった時点で戻す。

**録画したシーン(`scene`)**: 記録アプリの画面の「録画するシーン」で選んだシーンと、実際に録画したシーン(2026-10-05)。
画面キャプチャ用などに OBS で別のシーンを選んだまま戻し忘れても、決めたシーンで録画するため。

| キー | 内容 |
|---|---|
| `selected` | 記録アプリで選んだシーンの名前。null なら「OBS で今選んでいるシーン」(切り替えない) |
| `name` | 録画したシーン(録画を始めたときの OBS のプログラムのシーン) |
| `applied` | 選んだシーンに切り替えたか。もともとそのシーンだったときと、`selected` が null のとき、切り替えられなかったときは false |
| `previous` | 切り替える前のシーン(切り替えたときだけ。録画の後にこのシーンに戻す) |
| `error` | 切り替えられなかった理由(そのシーンが OBS に無い、など)。このときは `name` のシーン(切り替えずに今のシーン)で録画している |

シーンの切り替えは obs-websocket の `SetCurrentProgramScene`(スタジオモードでもプログラムのシーンを切り替える)。シーンの中身(ソース)は変えない。
元のシーン名は保存先フォルダの `.recorder_obs_scene_restore.json` にも書き、アプリが途中で落ちたときは次に OBS へつながった時点で戻す。

**知らないキーの扱い**: C# のアプリ(記録アプリ、`--resync`、再生アプリ)は、`session.json` を読んで書き戻すとき、自分の知らないキーも消さずに残す。
新しいキーを足すときは、古いアプリで一度保存されても消えないことを前提にしてよい。

## 配布パッケージ(`.heiso.zip`)

記録の中のレース 1 本を、動画・テレメトリー・メタデータの 1 つの Zip にしたもの(2026-09-28、形式 `heiso-package/1`)。
再生アプリの「書き出す」で作り(`Fh6.Core` の `PackageWriter`)、「パッケージを開く」で開く(`PackageReader` が展開する)。
中身は普通の Zip で、フォルダを含まない次の 4 つのファイル。

| ファイル | 内容 | Zip の圧縮 |
|---|---|---|
| `package.json` | パッケージのメタデータ(下記) | あり |
| `session.json` | 元の `session.json` を、切り出した範囲に合わせて直したもの(下記) | あり |
| `telemetry.csv.gz` | recv_time が切り出した範囲に入る行。見出しと各行の文字は元のまま(列もそのまま) | なし(gzip 済み) |
| `video.mp4` | 切り出した動画(作り直さずに、サンプルをそのままコピー) | なし |

展開すると、普通の記録のフォルダと同じに読める(`package.json` が増えるだけ)。

**範囲**: 動画は、レース開始(`RaceSegment` の開始の行)の 5 秒前より前で一番近いキーフレームから、ゴールの行(`RaceSegment.FinishIndex`)の 5 秒後まで。
ゴール直後の断片(リザルト画面の後に一瞬出るレース中の行)から数えると、リザルト画面のランキング(自分や他のプレイヤーの名前)がまるごと入るため、断片は含めない。
ゴールの 5 秒後まではランキングの画面に入らず、スタート側もカウントダウンの途中からで名前の出る画面は入らない(2026-09-28 に実際のパッケージで確認)。
作り直さないので、頭はキーフレームにしか置けない(HEISO の OBS プロファイルはキーフレーム 1 秒)。テレメトリーは、その動画の範囲に入る行。

**720p に作り直すとき**(再生アプリの「720p に縮める」、2026-09-28): ffmpeg で作り直す(`Fh6.Core` の `Ffmpeg`)。頭をキーフレームに合わせる必要が無いので、
レース開始のちょうど 5 秒前のコマから(`cut_from_sec` はそのコマの時刻)。高さ 720(元がそれより小さければ元のまま)、H.264(high)、キーフレーム 1 秒、
映像は平均 4 Mbps(30fps)/ 6 Mbps(60fps)・最大その 1.5 倍、音声は AAC 128 kbps。`session.json` の直し方は同じ(動画の 0 秒 = `cut_from_sec`)。
001930(4 分のレース)で、NVENC で 16 秒・137MB(元の画質のままなら 202MB)。元の動画と同じ瞬間のコマを比べて、コマがずれていないことを確かめた(PSNR: 同じコマ 40.5、1 コマずらすと 22)。

**`session.json` の直し方**: 動画の 0 秒を切り出した頭(元の動画の `cut_from_sec` 秒)に合わせる。同じテレメトリー時刻は、元の記録と同じコマを指す。

- `video.video_zero_recv_time`・`video.started_event_recv_time` に `cut_from_sec` を足す
- `video.sync_samples` の 2 つ目(動画の秒)と、`video.manual_sync[].video_sec` から `cut_from_sec` を引く(丸めない)
- `video.file_duration_sec` は切り出した長さ。`video.end_check_sec` は null(元の録画の検算のため)。`video.file_fps` が無い古い記録は、切り出した動画から読んで入れる
- 利用者の PC のパスを書かない: `video.output_path` は null、`files.video` は `video.mp4`、`files.telemetry_csv` は `telemetry.csv.gz`、`files.telemetry_raw` は null(生パケットは入れない)
- `events` と `markers` は、recv_time が範囲に入るものと、そのレースを後から直した `race_edited` / `course_confirmed`(`race_start_recv_time` がそのレースの開始)だけ
- `stats` は記録全体の値なので null
- `recv_time` は元の epoch 秒のまま(元の記録と突き合わせられるように)。`complete` は true のまま。知らないキーは残す

手動補正は、展開した先の `session.json` に保存する(元の Zip は変えない)。

**`package.json`**(キーは snake_case。分からない値は null):

| キー | 内容 |
|---|---|
| `format` | `heiso-package/1`。互換を壊す変更で数字を上げる。再生アプリは数字が違うパッケージを開かない |
| `app_version` / `created_at` | 作ったアプリの版と日時(`yyyy-MM-ddTHH:mm:sszzz`) |
| `game` | `fh6` |
| `position_kind` | `world`(ゲーム内の世界座標。x = 東、y = 上、z = 北の左手系) |
| `source_session` | 元の記録のフォルダ名 |
| `course` | `name`(登録名。記録アプリで一致・登録・直したもの)、`start_x`・`start_z`(m)・`start_yaw`(ラジアン): レース開始の位置と向き(コースの照合用) |
| `car` | `name`・`setup`(記録アプリで確定・直したもの)、`ordinal`・`class_raw`・`pi`・`drivetrain_raw`・`cylinders`(レース開始の行のテレメトリーの値) |
| `autodrive` / `assists` | `session.autodrive` と `session.assists` の写し |
| `view` | 視点。今は記録していないので null |
| `capture` | `live`(ライブ録画)/ `replay`(リプレイ録画)。今は記録していないので null |
| `race` | `start_recv_time`・`end_recv_time`(レース開始の行とゴールの行 = ゴール直後の断片を除いたレース中の最後の行)、`restart`、`finished`(完走したか。推定)、`finished_by`(何で決めたか: `lap` = ゴールで周回数が増えた / `results` = 直後のリザルト画面で時計が進んだ / `menu` = 直後の停止区間で時計が止まっていた(中断)/ `log_end` = リザルト画面のまま記録が終わった / `speed` = 終えた瞬間の速度と停止区間の長さ)、`end_reason`(`race_end` / `next_start` / `log_end`)、`race_time_sec`(完走ならゴールの行の `cur_race_time`。ゴール直後の断片の行は、リザルト画面の分だけ時計が進んでいるので使わない。それ以外は null)、`laps`(走り終えた周の数。レース中の `lap_no` の最大。完走で、ゴールで周回数が増えなかったときは最後の周の分を 1 足す) |
| `video` | `file`(`video.mp4`)、`duration_sec`、`fps`、`cut_from_sec`(元の録画の何秒から切り出したか)、`sync_method`(`started_event` / 手動補正があれば `manual`)、`manual_shift_sec`(手動補正のずらし量。無ければ 0)、`reencoded`(ffmpeg で作り直したときだけ。元の画質のまま切り出したなら null: `encoder`(`h264_nvenc` / `h264_amf` / `h264_qsv` / `libx264`)、`height`(720)、`video_kbps`、`audio_kbps`、`ffmpeg_version`) |
| `skips` | スキップ区間(クリーン再生用。再生アプリの「スキップ」と同じ計算 `RaceSkips`)。`count`、`total_sec`、`clean_sec`(レースの長さからスキップ区間を除いた長さ)、`ranges[]`: `kind`(`redo` = やり直し / `pause` = 一時停止)、`from_recv_time`・`to_recv_time`、`from_video_sec`・`to_video_sec`(パッケージの動画の秒) |

再生アプリは、パッケージのレースを `race` の範囲と `course.name`・`car.name` から作る(切り出したテレメトリーで判定をやり直さない)。
