# RVC Streaming Client

Avalonia/.NET client for the RVC Streaming Server. The client uses protocol v2 and automatically opens two WebSocket connections to the configured server:

- `/audio` for realtime audio and inference configuration.
- `/control` for models, files, uploads, logs, and other management traffic.

## Connection

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

The configured client network slice is automatically capped at the current server inference block duration. This prevents a packet size larger than one inference block from creating a deterministic processing backlog; the server also defensively handles oversized/legacy packets.

## Latency metrics

The UI reports separate values for:

- total media-age estimate,
- network RTT (monotonic ping/pong),
- server input/output queue delay,
- inference processing time.

These measurements are no longer conflated with the server sender queue.
