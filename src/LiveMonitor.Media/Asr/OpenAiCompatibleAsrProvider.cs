using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Core.Models;
using LiveMonitor.Media.Recording;

namespace LiveMonitor.Media.Asr;

/// <summary>
/// 一次 ASR 调用所需的运行时配置；由 Desktop 侧从 AppSettings 读取并用 DPAPI 解密 API Key 后提供。
/// </summary>
/// <param name="PresetId">预设 ID（siliconflow_cn / siliconflow_global / groq / custom），用于决定响应格式偏好。</param>
/// <param name="Endpoint">OpenAI 兼容转写接口完整 URL。</param>
/// <param name="Model">模型名。</param>
/// <param name="ApiKey">明文 API Key（仅存在于内存）。</param>
/// <param name="Language">识别语言；auto 表示自动检测。</param>
public sealed record AsrApiConfig(
    string PresetId,
    string Endpoint,
    string Model,
    string ApiKey,
    string Language = "auto");

/// <summary>
/// OpenAI 兼容云端 ASR 客户端（V0.5-A）：
/// FFmpeg 抽取 16kHz 单声道 64kbps MP3 → multipart 上传 → verbose_json/纯 text 双格式解析 → 本地磁盘缓存。
/// 单次片段硬上限 15 分钟（15 分钟 MP3 约 7.2MB，低于多数服务商 25MB 限制）。
/// </summary>
public sealed class OpenAiCompatibleAsrProvider : IAsrProvider, IDisposable
{
    private static readonly TimeSpan MaxSegmentDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 纯文本响应按句拆分时，单句超过该字符数后允许在逗号/顿号/冒号处再断一次，
    /// 避免一整段没有句号的口播变成一条超长行。
    /// </summary>
    private const int MaxEstimatedSentenceChars = 48;

    /// <summary>没有句末标点的文本按该长度切块，保证长口播仍可点击跳转（约 2~3 秒一块）。</summary>
    private const int FallbackChunkChars = 48;

    private readonly HttpClient _httpClient;
    private readonly string _ffmpegPath;
    private readonly ProcessJobGuard? _jobGuard;
    private readonly Func<Task<AsrApiConfig?>> _configAccessor;
    private readonly string _cacheDirectory;
    private bool _disposed;

    public OpenAiCompatibleAsrProvider(
        HttpClient httpClient,
        string ffmpegPath,
        ProcessJobGuard? jobGuard,
        Func<Task<AsrApiConfig?>> configAccessor,
        string cacheDirectory)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("FFmpeg 路径不能为空。", nameof(ffmpegPath));
        }

        if (configAccessor is null)
        {
            throw new ArgumentNullException(nameof(configAccessor));
        }

        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("ASR 缓存目录不能为空。", nameof(cacheDirectory));
        }

        _ffmpegPath = ffmpegPath;
        _jobGuard = jobGuard;
        _configAccessor = configAccessor;
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>
    /// 释放构造时注入的 HttpClient 与 Job Object（本类型接管这两个对象的所有权，
    /// 便于组合根随窗口关闭一次性释放）；重复调用无副作用。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _httpClient.Dispose();
        }
        catch
        {
            // 退出阶段释放失败不阻塞关闭。
        }

        try
        {
            _jobGuard?.Dispose();
        }
        catch
        {
            // 同上。
        }
    }

    public async Task<AsrTranscriptResult> TranscribeSegmentAsync(
        string videoPath,
        TimeSpan startTime,
        TimeSpan endTime,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            throw new ArgumentException("录像路径不能为空。", nameof(videoPath));
        }

        if (startTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startTime), "转写片段入点不能为负。");
        }

        if (endTime <= startTime)
        {
            throw new ArgumentException("出点必须大于入点。", nameof(endTime));
        }

        var duration = endTime - startTime;
        if (duration > MaxSegmentDuration)
        {
            throw new InvalidOperationException(
                $"单次语音转写片段不能超过 {(int)MaxSegmentDuration.TotalMinutes} 分钟，请缩小选区后重试。");
        }

        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("找不到待转写的录像文件。", videoPath);
        }

        var config = await _configAccessor().ConfigureAwait(false);
        if (config is null || string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                "尚未配置语音识别 API，请前往「账号与设置 → 语音识别」填写 API Key。");
        }

        config = config with
        {
            PresetId = (config.PresetId ?? string.Empty).Trim(),
            Endpoint = config.Endpoint.Trim(),
            Model = (config.Model ?? string.Empty).Trim(),
            ApiKey = config.ApiKey.Trim(),
            Language = string.IsNullOrWhiteSpace(config.Language) ? "auto" : config.Language.Trim(),
        };

        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "语音识别 Endpoint 不是合法的绝对 URL，请前往「账号与设置 → 语音识别」检查。");
        }

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            throw new InvalidOperationException(
                "尚未填写语音识别模型名称，请前往「账号与设置 → 语音识别」检查。");
        }

        // 1. 命中磁盘缓存时直接返回，不消耗 API 额度、也不启动 ffmpeg。
        string cachePath = BuildCachePath(videoPath, startTime, endTime, config.Model);
        var cached = await TryReadCacheAsync(cachePath, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        string tempAudioPath = Path.Combine(Path.GetTempPath(), $"LimoClip_Asr_{Guid.NewGuid():N}.mp3");
        try
        {
            // 2. 抽取 16kHz 单声道 64kbps MP3 临时文件。
            await ExtractAudioClipAsync(videoPath, tempAudioPath, startTime, duration, ct).ConfigureAwait(false);

            // 3. Multipart 上传；Groq/自定义优先 verbose_json，硅基流动只支持 json。
            bool preferVerbose = !config.PresetId.StartsWith("siliconflow", StringComparison.OrdinalIgnoreCase);
            string responseBody = await SendTranscriptionRequestAsync(config, tempAudioPath, preferVerbose, ct)
                .ConfigureAwait(false);

            var result = ParseResponse(responseBody, startTime, endTime, $"{config.PresetId} ({config.Model})");

            // 4. 结果落盘；缓存写入失败不影响本次展示。
            await TryWriteCacheAsync(cachePath, result, ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            try
            {
                if (File.Exists(tempAudioPath))
                {
                    File.Delete(tempAudioPath);
                }
            }
            catch (IOException)
            {
                // 临时文件清理失败不阻塞结果返回。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    private async Task ExtractAudioClipAsync(
        string videoPath,
        string outputMp3,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("64k");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputMp3);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 FFmpeg 抽音进程。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法启动 FFmpeg 抽音（{_ffmpegPath}）：{ex.Message}", ex);
        }

        using (proc)
        {
            try
            {
                // ProcessJobGuard 的现网 API 是 TryAssign（失败只记录错误码），进程仍会继续执行。
                _jobGuard?.TryAssign(proc);
            }
            catch
            {
                // Job Object 不可用时退回普通进程；取消时仍由下面的 Kill 兜底。
            }

            using var reg = ct.Register(() =>
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // 进程可能刚好退出。
                }
            });

            string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0 || !File.Exists(outputMp3))
            {
                throw new InvalidOperationException(
                    $"提取片段音频失败（FFmpeg 退出码 {proc.ExitCode}）：{Truncate(stderr.Trim(), 500)}");
            }
        }
    }

    private async Task<string> SendTranscriptionRequestAsync(
        AsrApiConfig config,
        string audioPath,
        bool useVerboseJson,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(audioPath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        form.Add(fileContent, "file", "clip.mp3");
        form.Add(new StringContent(config.Model), "model");
        if (useVerboseJson)
        {
            form.Add(new StringContent("verbose_json"), "response_format");
        }

        if (!string.IsNullOrWhiteSpace(config.Language) &&
            !config.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(config.Language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 部分自定义/海外兼容接口不支持 verbose_json，400 时降级为标准 json 重试一次。
        if (!response.IsSuccessStatusCode &&
            useVerboseJson &&
            response.StatusCode == HttpStatusCode.BadRequest)
        {
            return await SendTranscriptionRequestAsync(config, audioPath, useVerboseJson: false, ct)
                .ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"ASR API 返回错误 {(int)response.StatusCode}: {Truncate(body.Trim(), 500)}",
                null,
                response.StatusCode);
        }

        return body;
    }

    private static AsrTranscriptResult ParseResponse(
        string json,
        TimeSpan clipStart,
        TimeSpan clipEnd,
        string providerLabel)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("ASR API 返回了空响应。");
        }

        string fullText;
        var segments = new List<AsrTranscriptSegment>();
        var clipDuration = clipEnd - clipStart;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("ASR 响应不是 JSON 对象。");
            }

            fullText = root.TryGetProperty("text", out var textProperty)
                ? textProperty.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            if (root.TryGetProperty("segments", out var segmentArray) &&
                segmentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in segmentArray.EnumerateArray())
                {
                    double startSeconds = item.TryGetProperty("start", out var startProperty) &&
                                          startProperty.TryGetDouble(out var parsedStart)
                        ? parsedStart
                        : 0;
                    double endSeconds = item.TryGetProperty("end", out var endProperty) &&
                                        endProperty.TryGetDouble(out var parsedEnd)
                        ? parsedEnd
                        : startSeconds;
                    string text = item.TryGetProperty("text", out var segmentText)
                        ? segmentText.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var relativeStart = Clamp(TimeSpan.FromSeconds(startSeconds), TimeSpan.Zero, clipDuration);
                    var relativeEnd = Clamp(TimeSpan.FromSeconds(endSeconds), relativeStart, clipDuration);
                    segments.Add(new AsrTranscriptSegment(relativeStart, relativeEnd, text));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ASR 响应不是合法 JSON，无法解析。", ex);
        }

        if (segments.Count <= 1)
        {
            // 纯文本响应（硅基流动等只支持 json）或只返回一个整段：把文本按句末标点拆成多句，
            // 并按字符数比例估算每句的时间，让右侧列表可以逐句点击跳转。
            string text;
            TimeSpan rangeStart;
            TimeSpan rangeEnd;
            if (segments.Count == 1)
            {
                text = string.IsNullOrWhiteSpace(segments[0].Text) ? fullText : segments[0].Text;
                rangeStart = segments[0].RelativeStart;
                rangeEnd = segments[0].RelativeEnd;
            }
            else
            {
                text = fullText;
                rangeStart = TimeSpan.Zero;
                rangeEnd = clipDuration;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("ASR 未识别出任何文本内容。");
            }

            segments = SplitIntoSentences(text.Trim(), rangeStart, rangeEnd);
        }
        else if (string.IsNullOrWhiteSpace(fullText))
        {
            fullText = string.Join(" ", segments.Select(segment => segment.Text));
        }

        return new AsrTranscriptResult(clipStart, clipEnd, fullText, segments, providerLabel, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 把一整段文本按句末标点切成多句，并按字符数比例把 [rangeStart, rangeEnd] 的时间分配给各句。
    /// 没有标点的短文本保持单段；超长无标点文本按固定长度切块，保证长口播仍可逐句跳转。
    /// </summary>
    private static List<AsrTranscriptSegment> SplitIntoSentences(string text, TimeSpan rangeStart, TimeSpan rangeEnd)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            current.Append(ch);

            // 「……」是同一个省略号，不能在两个字符之间断开。
            if (ch == '…' && i + 1 < text.Length && text[i + 1] == '…')
            {
                continue;
            }

            bool hardBreak = ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '…' or '\n';
            bool softBreak = ch is '，' or ',' or '、' or '：' or ':';
            if (hardBreak ||
                (softBreak && current.Length >= MaxEstimatedSentenceChars) ||
                current.Length >= MaxEstimatedSentenceChars * 2)
            {
                FlushSentencePiece(pieces, current);
            }
        }

        FlushSentencePiece(pieces, current);
        if (pieces.Count == 1 && pieces[0].Length > FallbackChunkChars)
        {
            // 整段口播没有任何标点（部分模型不输出标点）：按固定长度切块，避免只剩一整行。
            var chunked = new List<string>();
            for (int index = 0; index < pieces[0].Length; index += FallbackChunkChars)
            {
                chunked.Add(pieces[0].Substring(index, Math.Min(FallbackChunkChars, pieces[0].Length - index)));
            }

            pieces = chunked;
        }

        if (pieces.Count <= 1)
        {
            return new List<AsrTranscriptSegment>
            {
                new(rangeStart, rangeEnd, text),
            };
        }

        var duration = rangeEnd - rangeStart;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        int totalLength = pieces.Sum(piece => piece.Length);
        var result = new List<AsrTranscriptSegment>(pieces.Count);
        long consumed = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            long startOffset = totalLength == 0
                ? 0
                : (long)Math.Round(duration.Ticks * (double)consumed / totalLength);
            consumed += pieces[i].Length;
            long endOffset = totalLength == 0
                ? 0
                : (long)Math.Round(duration.Ticks * (double)consumed / totalLength);
            var start = rangeStart + TimeSpan.FromTicks(Math.Clamp(startOffset, 0, duration.Ticks));
            var end = rangeStart + TimeSpan.FromTicks(Math.Clamp(endOffset, startOffset, duration.Ticks));
            if (end < start)
            {
                end = start;
            }

            result.Add(new AsrTranscriptSegment(start, end, pieces[i]));
        }

        // 最后一句严格覆盖到片段末尾，避免累计误差留下空白。
        result[^1] = result[^1] with { RelativeEnd = rangeEnd };
        return result;
    }

    private static void FlushSentencePiece(List<string> pieces, StringBuilder buffer)
    {
        string piece = buffer.ToString().Trim();
        buffer.Clear();
        if (piece.Length > 0)
        {
            pieces.Add(piece);
        }
    }

    /// <summary>
    /// 旧缓存里纯文本响应只存了一整段：读取时按句补拆（不重新请求、不消耗额度），
    /// 让历史结果也能逐句点击跳转。
    /// </summary>
    private static AsrTranscriptResult UpgradeCachedResult(AsrTranscriptResult cached)
    {
        if (cached.Segments is { Count: > 1 })
        {
            return cached;
        }

        string text = string.IsNullOrWhiteSpace(cached.FullText)
            ? cached.Segments is { Count: 1 } ? cached.Segments[0].Text : string.Empty
            : cached.FullText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return cached;
        }

        var duration = cached.ClipEnd - cached.ClipStart;
        var expanded = SplitIntoSentences(text.Trim(), TimeSpan.Zero, duration);
        return expanded.Count > 1 ? cached with { Segments = expanded } : cached;
    }

    private async Task<AsrTranscriptResult?> TryReadCacheAsync(string cachePath, CancellationToken ct)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            string cachedJson = await File.ReadAllTextAsync(cachePath, ct).ConfigureAwait(false);
            var cached = JsonSerializer.Deserialize<AsrTranscriptResult>(cachedJson);
            return cached is null ? null : UpgradeCachedResult(cached);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 缓存损坏/被占用按未命中处理，重新请求并覆盖。
            return null;
        }
    }

    private async Task TryWriteCacheAsync(string cachePath, AsrTranscriptResult result, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string json = JsonSerializer.Serialize(result);
            await File.WriteAllTextAsync(cachePath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写入失败只影响下次是否复用，本次结果照常返回。
        }
    }

    private string BuildCachePath(string videoPath, TimeSpan start, TimeSpan end, string model)
    {
        var fileInfo = new FileInfo(videoPath);
        string rawKey =
            $"{Path.GetFullPath(videoPath)}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}|" +
            $"{(long)start.TotalMilliseconds}|{(long)end.TotalMilliseconds}|{model}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        return Path.Combine(_cacheDirectory, $"{hash}.asr.json");
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
        {
            max = min;
        }

        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
