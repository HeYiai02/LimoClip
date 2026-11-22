# tools

Runtime third-party binaries live here but are intentionally **not committed** to keep the repository small and avoid redistributing large binaries. The CA bundle is tracked because playback/HTTPS depends on it.

Expected layout:

- `yt-dlp.exe` — Twitch stream resolution (`yt-dlp -j`).
- `libmpv-2.dll` — libmpv runtime loaded by `LibMpv.Avalonia`.
- `ffmpeg/ffmpeg.exe` — media probing/fallback.
- `mpv/mpv.exe` and its DLLs — optional standalone mpv runtime.
- `cacert.crt` — CA bundle passed to mpv for HTTPS playback.

The app resolves these paths relative to the application output; keep the same relative layout when publishing. Third-party licenses remain with their upstream projects.
