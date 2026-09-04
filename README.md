# RVC Streaming Client

Avalonia/.NET client for the RVC Streaming Server. The client uses protocol v2 and automatically opens two WebSocket connections to the configured server:

- `/audio` for realtime audio and inference configuration.
- `/control` for models, files, uploads, logs, and other management traffic.

## Connection

The connection panel supports two modes:

- **远程** connects to the configured `ws://` or `wss://` endpoint and keeps the existing dual-WebSocket transport.
- **本地** opens **设置** for an installed `RVC_Streaming_Server` folder. It defaults to `localServer` beside the client executable. **从 GitHub 下载** installs the `main` branch source archive without a `.git` directory or any Git history, and refuses to overwrite a non-empty target. At startup the client checks the recorded source commit against GitHub; an available update adds a green dot to **设置**, where **检查更新** changes to **更新源码**. Updating preserves `.pixi` and requires dependency installation/checking again. **下载依赖** runs `pixi install --environment default --locked -vv`; its buffered live output is shown in the settings window and the environment is checked automatically afterward. **检查依赖** invokes Pixi with `--no-install --frozen`, so the check neither installs packages nor changes the lock file. **启动并连接** remains disabled until the check succeeds and the settings are saved. The client then starts that folder's Pixi Python with `server.py --stdio` and carries both logical channels over private anonymous process pipes. No listening port is created, and the server child is stopped when the client disconnects or exits.

The local folder must contain `server.py`, `pixi.toml`, and `.pixi/envs/default/python.exe` (or the corresponding Unix environment interpreter). Run `pixi install` in the server folder first if the environment is missing.

All client-owned runtime data is portable and stays under `data` beside the executable. This includes `settings.json`, client logs, local Server files and registries, training jobs, Server logs, Python bytecode, and model/download caches. The client does not use AppData for these files.

Local development works without credentials:

```text
ws://127.0.0.1:8765/
```

For a remote server, set the same bearer token used by the server before starting the client:

```powershell
$env:RVC_STREAMING_TOKEN = "use-a-long-random-secret"
```

Remote connections are expected to use `wss://`. Clear-text `ws://` to a non-loopback host is rejected unless `RVC_ALLOW_INSECURE_WS=1` is explicitly set for a trusted private network.

## Build

```bash
dotnet build Client.Avalonia.csproj -c Release
```

The realtime audio backend uses NAudio/WASAPI. Capture and playback are configured for event-driven, low-latency operation when a selected Windows audio endpoint is available.

## Realtime transport

Each little-endian float32 mono audio input frame contains protocol version, stream session ID, sequence number, sample rate, and a monotonic media timestamp. Stop/start creates a new session so late output from the previous stream is ignored.

Capture timestamps are assigned from the complete WASAPI callback timeline before the callback is sliced for the network. This avoids assigning future timestamps when one device callback contains multiple network slices.

The send queue and playback buffer are latest-wins: when they overflow, the oldest audio is discarded. The client also reacts to server discontinuity markers by clearing stale playback data and resynchronizing latency/jitter statistics.

Automatic playout uses RFC 3550 adjacent transit variation plus an exponentially decayed 95th-percentile late-tail histogram. Its base target is the larger of the playback-device period and one paced server packet plus scheduler slack; measured network protection is added above that base and may fall to zero on a stable connection. Late packets or a real underrun raise the target immediately, while recovery releases it gradually. A pending underrun enters rebuffering instead of allowing the audio provider to create repeated short zero-filled gaps.

The configured client network slice is automatically capped at the current server inference block duration. This prevents a packet size larger than one inference block from creating a deterministic processing backlog; the server also defensively handles oversized/legacy packets.

## Latency metrics

The UI reports separate values for:

- total media-age estimate,
- network RTT (monotonic ping/pong),
- server input/output queue delay,
- inference processing time.

These measurements are no longer conflated with the server sender queue.
