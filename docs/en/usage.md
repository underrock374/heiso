# Using HEISO Solo

> **About this document:** This is a machine translation of the Japanese [usage.md](../usage.md). The screenshots and animations show the Japanese versions of the apps (and of the game), so the labels in the images are in Japanese. In the text, the English label shown by the app is followed by the Japanese label in parentheses where that helps you find it in a screenshot.
> If this translation and the Japanese original differ, the Japanese original is correct.

Installation and setup are described in [install.md](install.md).

1. In the Recorder, record the telemetry and the video while you drive
2. In the Player, watch the video and graphs side by side
3. Export a race you want to show as a package (`.heiso.zip`) and give it to others

## 1. Record (Recorder)

Start OBS, then start `HeisoRecorder.exe`. A black window (the Recorder itself) opens, and the Recorder page appears in the browser on your PC.
The black window shows the addresses to open on the PC and the smartphone, the port that receives telemetry, and where recordings are saved. When it connects to OBS, it says so.

![The window shown when the Recorder starts](../images/usage/01-recorder-console.png)

To use it from a smartphone, scan the QR code on the PC page (on the same Wi-Fi as the PC). If you bookmark the page on the smartphone, you can open it directly next time.

![The Recorder page](../images/usage/02-recorder.png)

### Starting and stopping a recording

1. Turn on **Record video with OBS** (OBSで録画する), and choose **Recording quality** (録画の画質) and **Scene to record** (録画するシーン) (to record only telemetry without video, turn it off)
2. Enter a note if you like (for example `Mecha My Day`). It is added to the end of the recording's folder name
3. Start recording with the round button. A **red** border means video and telemetry, **blue** means telemetry only
4. Drive. You can drive as many races in a row as you like without stopping (the Player tells the races apart)
5. To stop, press the round button and press it again within 3 seconds (two presses, so you don't stop by accident)

- **Add marker** (マーカーを打つ) puts a mark at that moment
- The lower the quality step, the smaller the file. If you plan to give packages to others, "Light" or "Medium" is recommended (about 200–300 MB for a 4-minute race). You can also record at higher quality and shrink to 720p with ffmpeg when exporting (the same 720p as "Light")
- Recordings are created in "Videos\FH6Recorder" (or the folder you set) as folders named by date and time, such as `20260928_153147`. To make it easier to tell races apart later, you may add a comment to the end of the folder name by hand (for example `20260928_153147#summer_Toriyama`)

### Scene to record (automatic switching in OBS)

**The Recorder switches OBS to the HEISO scene and profile only while recording, and switches back when recording ends.**
If you already use OBS for streaming or other things, you can keep your usual scenes and profiles and just add one HEISO scene (how to create it: "Create a scene for HEISO" in [install.md](install.md)).

1. Create a HEISO scene with Game Capture in OBS (for example `HEISO`)
2. Choose that scene in **Scene to record** in the Recorder. The description says: Switches OBS to the scene "HEISO" while recording, then back
3. Then just start and stop recording as usual. Even if another scene is selected in OBS, it switches to the HEISO scene while recording and back to the previous scene when you stop

| **Scene to record** | What is recorded |
|---|---|
| The HEISO scene (for example `HEISO`) | Always the HEISO scene. Switches no matter what is selected in OBS (**recommended**) |
| **OBS's current scene** (OBS で今選んでいるシーン) | The scene selected in OBS when recording starts. Nothing is switched |

- The chosen scene is used again next time. **Recording quality** (the OBS profile) is also switched only while recording and switched back the same way
- If the chosen scene doesn't exist in OBS (renamed or deleted), the current scene is recorded without switching, and the page tells you
- **Don't record while streaming.** The OBS scene changes while recording, so your stream also changes

### Car and course names

The telemetry doesn't include car or course names, so you name them in the Recorder. It watches the game even when not recording.

- **Car check**: appears when you change cars. For a new car, enter the car name. Even for the same car, if the PI or other values change with the setup, you can name the setup. Once a combination is registered, its name appears automatically next time
- **Course check**: appears when a race starts on a new course. Enter the race name and type. Next time, the course is recognized automatically by its start position
- You may close them with **Later** (あとで). You can name them after the race or after stopping the recording with **Fix course / car names** (コース名・車名を直す) under **Last race** (直前のレース). The names appear in the Player's list of races
- You can also name cars and courses with **Name it** (名前を付ける) in the Player (see "Choosing a race" below)

### Quitting

Press **Ctrl+C** in the Recorder's black window.

## 2. Watch (Player)

Start `HeisoPlayer.exe` and choose a recording folder (such as `20260928_153147`) with **Open session folder** (セッションフォルダを開く).

**If playback stutters, quit the game.** The game keeps drawing on the GPU even behind other windows, so the video freezes for a moment or stays frozen.

### The screen

![The Player screen](../images/usage/03-player.png)

- Top: the video. To the right are the controls and the course map, the values at the pointer position, and what was detected
- Bottom: the graphs. The X axis is the video position. The **yellow vertical line** is the current position
- Marks on the graphs: a light-blue ▼ is a start, a white ◇ is a lap start (filled: finish), an orange dotted line is a contact, a purple band is a jump, red is where you left the road and it led to a slip or a rewind, and a gray band is a section that can be skipped

When you play, the yellow line moves along with the video.

![The yellow line on the graphs moving with playback](../images/usage/demo-graphs.gif)

### Common controls

| Control | Action |
|---|---|
| Space | Play / pause |
| Mouse wheel over the graphs or video | Back/forward 1 second (1 frame while holding Shift) |
| `,` / `.` | Back/forward 1 frame |
| Ctrl+← / → | Back/forward 10 seconds (30 seconds with Ctrl+Alt) |
| Click a graph | Go to that moment |
| Ctrl+wheel (over the graphs) | Zoom the graphs horizontally |
| **◀ Prev** / **Next ▶** | Go to the previous/next mark (3 seconds before a start, 1 second before a lap, etc.) |
| **Rate** (速さ) | 0.25× / 0.5× / 1× |
| Click the course map | Go to the moment you passed that place |

### Choosing a race

Use **Race** (レース) at the top to choose a race in the recording (the course and car names and whether it was likely finished or abandoned are shown).
Choosing one moves to 3 seconds before its start and sets the graphs to that race.

- With **Name it** (名前を付ける) you can name the race's course, car and setup, in case you forgot in the Recorder. The names are registered the next time the Recorder starts, and appear automatically from then on

  ![Name it](../images/usage/04-player-names.png)

- Setting **X axis** (横軸) to **Distance** (距離) lines up the graphs by distance from the start. Runs redone with rewind are left out, and the race is shown as the one run that made it to the finish
- With **Skip ON** (スキップ ON), playback skips redo sections (rewinds, missed checkpoints) and pauses (photo mode, menus)

### Course map

- Switch with **2D / 3D**. 3D has elevation, with **Top / Angled / Chase** (真上 / 斜め / 後ろから) views. You can rotate (left drag), move (right drag) and zoom (wheel) with the mouse
- Zooming in with **All / 2 km / 1 km / 500 m / 200 m** follows the current position. With **Sections** (区切り), the view moves section by section, split in the middle of straights (the map doesn't spin when you spin)
- **Surface** (路面の色) colors where you drove by road surface (paved, dirt, grass, etc.)

![Course map in 2D and 3D, zooming, and switching views](../images/usage/demo-coursemap.gif)

### Settings

**Settings** (設定) at the top right lets you change the following. The same settings are used for every recording you open (changes take effect immediately). Each item is also explained in the settings screen.

| Tab | What you can do |
|---|---|
| **Graphs** (グラフ) | The rows of graphs (speed, pedals, steering, slip, surface, elevation, etc.). Choose a preset, or drag the tags to rearrange them and save as your own layout. Whether to show contact and launch marks |
| **Display** (画面) | Display language (日本語 / English), units (km/h or mph, m or ft, etc.), what to show when a recording opens (all or per race, time or distance), where to put the course map and its size, whether to remember the window size |
| **Playback** (再生) | Start playing from 3 seconds before the first race's start when a recording opens; skip redo sections and pauses automatically |
| **Export** (書き出し) | The ffmpeg location and encoder for exporting at 720p ("8." in [install.md](install.md)) |
| **Sync** (同期) | The method for aligning the video and graphs (for checking; normally don't change it), and the replay video shift |

#### Graphs

![Settings → Graphs](../images/usage/05-settings-graph.png)

**Rows of graphs**: decide how many rows the graphs at the bottom have, and what is drawn in each row.

| Item | Description |
|---|---|
| **Preset** (プリセット) | Choose a fixed combination: **3 rows** (start with this), **2 rows**, **4 rows (classic)**, **Detailed** (6 rows: speed and gear, pedals, steering and turning, slip, surface, elevation). **My layout** is the combination you saved |
| **Save as my layout** (自分の設定として保存) | Remembers the current combination as **My layout**. You can always go back to it even after choosing another preset |
| Row height | **Large / Medium / Small** at the left of each row changes the row's height |
| Tags (speed, gear, etc.) | What is drawn in a row. Drag a tag to move it to another row. × removes it, **+ Add** adds one |
| ▲ ▼ / **Delete** / **+ Add row** | Reorder rows, delete a row, add a row |

What can be drawn: speed, gear, throttle, brake, steering, turning, front slip, rear slip, grade, elevation, surface (bands for the 4 wheels), contact marks, jumps.
In a row, the first unit has its scale on the left and the second on the right. Changes appear in the graphs immediately and are used the next time too.

**Marks**

| Item | Description |
|---|---|
| **Show possible contacts** (接触の候補の印を出す) | Shows orange dotted marks where you seem to have hit a wall or a car. Turn it off if there are too many |
| Also mark launches outside races (レース以外の発進も印にする) | Marks launches outside races too (traffic lights, etc.), and **◀ Prev / Next ▶** stops at them. Useful for reviewing free-roam practice |

#### Display

![Settings → Display](../images/usage/06-settings-screen.png)

| Item | Description |
|---|---|
| **Language** (言語) | **Auto** is Japanese if the Windows display language is Japanese, otherwise English. You can also fix it to 日本語 or English. Changing it reloads the screen (the open recording and the playback position stay) |
| **Units** (単位) | Choose speed (km/h, mph), distance (m and km / ft and mi) and elevation (m / ft) separately. **Auto** uses miles if the Windows region is the US or the UK, otherwise meters. Only the display changes; the recorded data does not |
| **When a recording opens** (記録を開いたときの表示) | When a recording opens, whether to show all of it or per race (selecting the first race), and whether the X axis is time or distance. After opening, you can switch at any time with **Race** and **X axis** at the top |
| **Course map** (コース図) | Where to put it: **Right panel (next to the video)** or **Next to the graphs (bottom right)**. Next to the graphs, you can also set its size (width and height). 0 means a square matching the graph height. You can also drag the bottom-left corner of the course map |
| **Window** (ウィンドウ) | Remembers the window size and position (including maximized) and restores them next time |

#### Playback

| Item | Description |
|---|---|
| When a recording opens, play from 3 s before the first race start | Starts playing right before the race as soon as a recording opens |
| Automatically skip redo sections and pauses | Turns **Skip** at the top ON when a recording opens. Playback skips redo sections (rewinds, missed checkpoints) and pauses for photo mode and menus (gray bands in the graphs) |

#### Export

![Settings → Export](../images/usage/07-settings-export.png)

| Item | Description |
|---|---|
| **Location** (場所) | Where ffmpeg is. If empty, it is searched for automatically (the PATH, and an `ffmpeg` folder inside the HEISO folder). Choose with **Browse** (参照); **Check** (確かめる) shows the ffmpeg found and the usable encoders |
| **Encoder** (変換) | The encoder for shrinking to 720p. **Auto** picks what works on this PC in the order NVIDIA → AMD → Intel → CPU. The CPU is slow, so use a GPU if available |

How to install ffmpeg is in "8." of [install.md](install.md). If you only export at the original quality, no settings are needed here.

#### Sync

The method for aligning the video and graphs. It is for checking, so **normally leave it at "Recording-start event"** (録画開始イベント).
If the video and graphs look out of sync, don't change this; use **Manual sync** (同期の手動補正) in the right panel (see "If the video and graphs are out of sync" below).

**Shift when a replay is assigned** (リプレイを割り当てたときのずらし量): when a replay is newly assigned, the video is moved earlier by this amount with **Fine sync adjustment** (frames of that video; at 30 fps, 1 frame = 33.3 ms). The default is 5 frames (167 ms at 30 fps), the amount matched on an actual replay by comparing the brake lights and gear-shift sounds with the graphs.
When you **Use** an automatically found replay, or align one with **This frame = replay start** / **= Finish**, it starts shifted by this amount, and the Fine sync adjustment window shows "Video shifted 5 frames (167 ms) earlier". Replays already assigned don't change.
If every replay looks off in the same direction, change this (increase it if the brake lights in the video are already on at the start of braking on the graph; decrease it if they are not on yet). **Reset to default** (既定に戻す) sets it back to 5 frames.
Both the video and the telemetry change only once per frame (about 33 ms at 30 fps), so shifting by less than that makes almost no visible difference.

### If the video and graphs are out of sync

They are normally in sync, but if they look slightly off, you can align them with **Manual sync** (同期の手動補正) in the right panel.

1. Stop at a frame where a moment is clear, such as a landing impact or the start, and click **Use this frame** (このコマを使う)
2. Click **Pick on graph** (グラフで指す), and click the same moment on the graph
3. Click **Save** (保存). It stays aligned the next time you open it

### Watching a replay or a separately recorded video

Besides the recording, you can assign **the game's replay recorded after a race** or **a separately recorded video** (Game Bar, etc.) to a race, and watch it with the same graphs and course map.
Switch between the recording and assigned videos with **Video** (動画) at the top. The same moment is shown when you switch.

- In a replay, the parts redone with rewind are gone, and only the run that remained to the finish plays. While you watch a replay, the graphs also show only that run (there are no sections to skip)
- To record a replay, keep recording after the race's finish, and play the replay from the results screen **with the chase camera, at normal speed, to the end** (don't pause or fast-forward)

**Assigning the replay in the recording**

1. Choose a race with **Race** at the top, then **Add video** (動画を追加) → **Find the replay in the recording** (録画の中のリプレイを自動で探す). ffmpeg is needed ("8." in [install.md](install.md)). Every finished race in the recording is searched, so it doesn't matter which race is selected

   ![Add video](../images/usage/08-player-add-video.png)

2. While searching, the progress is shown at the bottom of the screen. It takes tens of seconds per race (you can cancel with **Stop** (中止))

   ![Searching for the replay](../images/usage/09-player-replay-searching.png)

3. The result is shown for each race. A replay is searched for only between a race's finish and the start of the next race (so even if you drove the same course several times and recorded the replay at the end, you can tell which race it belongs to). For a race where it was found, the video time where the replay starts and the confidence (high or low) are shown. Click **Use** (採用), and **Video** at the top switches to the replay

   ![The replay was found](../images/usage/10-player-replay-found.png)

4. If the confidence is low, or nothing was found, click **Match race N manually** (○ レース目に手動で合わせる; the race selected in **Race** at the top). At the first frame of the replay click **This frame = replay start** (いまのコマ = リプレイの先頭), or at the moment of the finish (just before the screen goes black) click **= Finish** (= ゴール)

**Assigning a separately recorded video**

1. Choose a race with **Race** at the top, then **Add video** → **Choose a video of the live run…** (ライブ走行の動画を選ぶ…; a video of you driving) or **Choose a replay video…** (リプレイの動画を選ぶ…; a video of only the replay)
2. For a video of the live run, align one point with **Manual sync** in the right panel (the same steps as in "If the video and graphs are out of sync" above). For a replay video, align it with **This frame = replay start**
3. If the video's frame spacing isn't constant (variable frame rate), the timing can drift little by little. If a warning appears, check that it still matches near the finish

**Fine adjustment (Fine sync adjustment)**

While you watch an assigned video, a small **Fine sync adjustment** (同期の微調整) window appears over the video (you can drag it). The graph stays put; you step the video frames back or forward to match.
When a replay is newly assigned, this window opens with the video already moved earlier by the **Shift when a replay is assigned** setting ("Video shifted 5 frames (167 ms) earlier"). The window shows the current shift of that video.

1. With **Graph** (グラフを) **Brake ▶** (ブレーキ ▶), put the graph (the yellow line) at the start of braking (**Shift ▶** (変速 ▶) for the moment of a gear shift).
   In the example below, the graph is at the start of braking, but the brake lights are already on in the video. The video is slightly ahead

   ![Before aligning: the graph is at the start of braking, the brake lights are on in the video](../images/usage/11-player-sync-before.png)

2. With **Video** (映像を) **◀ 1 frame / 1 frame ▶** (◀ 1コマ / 1コマ ▶), step only the video one frame at a time (keys `[` `]`; 0.1 s while holding Shift). The yellow line on the graph doesn't move.
   Step back to the frame where the brake lights go off: the lights come on in the next frame, which is the same moment as the start of braking on the graph. The amount shifted is shown at the bottom of the window (133 ms earlier in this example)

   ![After aligning: the frame where the brake lights are off](../images/usage/12-player-sync-after.png)

3. Play from a little before; when the brake lights and the shift sounds match the graph, you're done. The shift is saved immediately

Assignments are saved in the recording (`session.json`), and appear in **Video** the next time you open it. If you move a video and it can no longer be found, choose it again with **Choose video again…** (動画を選び直す…) under **This video** (この動画) in the right panel. **Unassign** (割り当てを外す) removes an assignment (the video file is not deleted).

If you assigned a video to the wrong race, or no longer need it, choose that video with **Video** at the top, click **Unassign** under **This video** in the right panel, and click "OK" in the confirmation dialog. After unassigning, **Video** at the top goes back to the recording.

![Unassign](../images/usage/14-player-remove-video.png)

## 3. Share (distribution package)

### Exporting

1. Open a recording in the Player and choose the race to export with **Race** at the top
2. Click the green **Export** (書き出す) at the top right and choose how to export
   - **Original quality** (元の画質のまま): cuts without re-encoding. The quality is as recorded, and it takes a few seconds
   - **Shrink to 720p** (720p に縮める): re-encodes with ffmpeg. The file becomes smaller (around 140 MB for a 4-minute race) and easier to share. It takes tens of seconds with a GPU, or a few minutes with the CPU only. ffmpeg is needed ("8." in [install.md](install.md))
3. Choose where to save with **Choose where to save and export** (保存先を選んで書き出す), and a `.heiso.zip` is created. For 720p, the progress is shown at the bottom of the screen (you can cancel with **Stop**)

- The video runs from 5 seconds before the race's start to 5 seconds after the finish (the ranking screen after the finish is not included)
- The package contains the video, the telemetry for the same range, the course and car names, the race time and more. The note you entered in the Recorder is also included
- You can't export from a recording made without video, or from a recording that is still in progress
- If a replay or a separately recorded video is selected in **Video** at the top, **that video** is exported. For a replay, the package contains the video from the start of the replay to the finish (where the screen goes black), and the telemetry of the run that remained to the finish. When exporting at the original quality, the start may be about 1 second late (the first part of a replay overlaps with a screen transition, so none of the driving is lost)
- **No audio** (音声を入れない) makes a video without sound (when you don't want the music in it)

### Opening a package you received

Choose the `.heiso.zip` with **Open package** (パッケージを開く) in the Player. You don't need to extract it. It plays just like a recording.

### Notes on sharing

- Packages contain game footage. Share them within Microsoft's "Game Content Usage Rules" (non-commercially)
- **If you record with the intention of sharing, we recommend turning on the game's "Streamer Mode".** In the game, Settings → Audio → Streamer Mode. The game describes it as enabling live streaming of the in-game music, switching to music suitable for streaming and sharing.
  Set it before recording (the music in videos already recorded doesn't change). To share a video recorded without it, use **No audio** when exporting

  ![Streamer Mode in the game's settings (Japanese game screen)](../images/usage/13-game-streamer-mode.png)

- While driving, other players' names appear above their cars. HEISO can't remove them automatically, so if you mind, blur them with video-editing software.
  **Other players' names don't appear in the game's replay.** To share, we recommend assigning the replay and exporting it (see "Watching a replay or a separately recorded video" above)
- An easy way to share a package is to put the file on Google Drive, OneDrive or similar and share a link

## Known limitations

- Only Forza Horizon 6 (PC) and Windows are supported
- OBS Studio is required for recording
- The game sends telemetry about 30–60 times per second (depending on the game's fps)
- Whether a race was finished or abandoned is estimated from the telemetry
- Comparing two runs on the same course side by side ("HEISO") will come in a later version

---

Forza Horizon 6 © Microsoft Corporation. HEISO was created under Microsoft's "[Game Content Usage Rules](https://www.xbox.com/en-US/developers/rules)" using assets from Forza Horizon 6, and it is not endorsed by or affiliated with Microsoft.
