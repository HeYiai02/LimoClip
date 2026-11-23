using LiveMonitor.Core.Enums;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WinForms = System.Windows.Forms;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 在独立的 STA 线程上打开 WinForms + WebView2 登录窗。
/// 平台配置来自 <see cref="PlatformLoginProfiles"/>：B 站/Twitch/YouTube 会自动捕获命名 Cookie，
/// 虎牙/斗鱼/抖音在用户点击「采集 Cookie」后捕获登录域下的全部 Cookie。
/// 返回可直接用于 HTTP 请求的 Cookie 头；窗口关闭或超时返回 null。
/// </summary>
public sealed class WebViewLoginService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    public static string DisplayName(PlatformType platform)
        => PlatformLoginProfiles.TryGet(platform, out var profile) ? profile.DisplayName : platform.ToString();

    public async Task<string?> LoginAsync(PlatformType platform, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => Run(platform, tcs, ct))
        {
            IsBackground = true,
            Name = $"WebViewLogin-{platform}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static void Run(PlatformType platform, TaskCompletionSource<string?> tcs, CancellationToken ct)
    {
        var profile = PlatformLoginProfiles.Get(platform);

        var userDataFolder = Path.Combine(
            ResolveUserDataRoot(),
            "LiveMonitor",
            "webview2-login-" + Guid.NewGuid().ToString("N"));

        var logPath = Path.Combine(AppContext.BaseDirectory, "webview-login.log");
        void Log(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 登录窗不应因日志失败而中断。
            }
        }

        WinForms.Form? form = null;
        WebView2? webView = null;
        WinForms.Timer? pollTimer = null;
        WinForms.Timer? timeoutTimer = null;
        var finished = 0;

        void Finish(string? cookie)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0)
            {
                return;
            }

            tcs.TrySetResult(cookie);
            try
            {
                form?.BeginInvoke(new Action(() => form.Close()));
            }
            catch
            {
                // 窗口可能已经销毁。
            }
        }

        try
        {
            WinForms.Application.EnableVisualStyles();

            form = new WinForms.Form
            {
                Text = $"登录 {profile.DisplayName}",
                Width = 980,
                Height = 760,
                StartPosition = WinForms.FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = true
            };

            webView = new WebView2 { Dock = WinForms.DockStyle.Fill };

            var captureButton = new WinForms.Button
            {
                Text = "我已登录，采集 Cookie",
                Dock = WinForms.DockStyle.Right,
                Width = 170,
            };
            var hintLabel = new WinForms.Label
            {
                Dock = WinForms.DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Text = profile.CaptureAllCookies
                    ? "登录完成后点击右侧按钮采集 Cookie。"
                    : "登录完成后会自动捕获；若未自动完成，可点击右侧按钮手动采集。",
                Padding = new WinForms.Padding(10, 0, 10, 0),
            };
            var bottomPanel = new WinForms.Panel
            {
                Dock = WinForms.DockStyle.Bottom,
                Height = 46,
            };
            bottomPanel.Controls.Add(hintLabel);
            bottomPanel.Controls.Add(captureButton);

            form.Controls.Add(webView);
            form.Controls.Add(bottomPanel);

            form.FormClosed += (_, _) =>
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                {
                    tcs.TrySetResult(null);
                }
            };

            async Task<string?> CaptureCookiesAsync()
            {
                // 多个 CookieUri 依次采集（如 YouTube 需要 youtube.com 与 accounts.google.com），
                // 同名 Cookie 以先出现的为准。
                var collected = new List<CoreWebView2Cookie>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var uri in profile.CookieUris)
                {
                    var cookies = await webView!.CoreWebView2.CookieManager.GetCookiesAsync(uri);
                    foreach (var cookie in cookies)
                    {
                        if (string.IsNullOrEmpty(cookie.Value) || !seen.Add(cookie.Name))
                        {
                            continue;
                        }

                        collected.Add(cookie);
                    }
                }

                var selected = profile.CaptureAllCookies
                    ? collected
                    : collected
                        .Where(cookie => profile.CookieNames.Contains(cookie.Name, StringComparer.Ordinal))
                        .ToList();

                if (selected.Count == 0)
                {
                    return null;
                }

                var header = string.Join("; ", selected.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                Log($"captured {selected.Count} cookies: {string.Join(",", selected.Select(cookie => cookie.Name))}");
                return header;
            }

            form.Shown += async (_, _) =>
            {
                try
                {
                    Log($"creating environment {userDataFolder}");
                    var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await webView.EnsureCoreWebView2Async(environment);
                    Log("webview ready; navigating " + profile.LoginUrl);
                    webView.CoreWebView2.Navigate(profile.LoginUrl);

                    captureButton.Click += async (_, _) =>
                    {
                        if (Volatile.Read(ref finished) != 0)
                        {
                            return;
                        }

                        try
                        {
                            var header = await CaptureCookiesAsync();
                            if (string.IsNullOrWhiteSpace(header))
                            {
                                hintLabel.Text = "未找到登录 Cookie，请确认已登录后重试。";
                                return;
                            }

                            pollTimer?.Stop();
                            Finish(header);
                        }
                        catch (Exception ex)
                        {
                            Log("manual capture failed: " + ex.Message);
                            hintLabel.Text = "采集 Cookie 失败：" + ex.Message;
                        }
                    };

                    // 命名 Cookie 的平台：轮询自动捕获所需的登录标记。
                    var markers = profile.AutoCaptureMarkers;
                    if (markers is { Count: > 0 })
                    {
                        pollTimer = new WinForms.Timer { Interval = 1500 };
                        pollTimer.Tick += async (_, _) =>
                        {
                            if (Volatile.Read(ref finished) != 0)
                            {
                                return;
                            }

                            try
                            {
                                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(profile.CookieUris[0]);
                                var hasMarker = cookies.Any(cookie =>
                                    markers.Contains(cookie.Name, StringComparer.Ordinal) &&
                                    !string.IsNullOrEmpty(cookie.Value));
                                if (!hasMarker)
                                {
                                    return;
                                }

                                var header = await CaptureCookiesAsync();
                                if (string.IsNullOrWhiteSpace(header))
                                {
                                    return;
                                }

                                pollTimer?.Stop();
                                Finish(header);
                            }
                            catch (Exception ex)
                            {
                                Log("poll failed: " + ex.Message);
                            }
                        };
                        pollTimer.Start();
                    }

                    timeoutTimer = new WinForms.Timer { Interval = (int)LoginTimeout.TotalMilliseconds };
                    timeoutTimer.Tick += (_, _) =>
                    {
                        Log("timeout");
                        timeoutTimer?.Stop();
                        Finish(null);
                    };
                    timeoutTimer.Start();
                }
                catch (Exception ex)
                {
                    Log("startup failed: " + ex);
                    tcs.TrySetException(ex);
                    form.Close();
                }
            };

            WinForms.Application.Run(form);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            try
            {
                form?.Close();
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            pollTimer?.Stop();
            timeoutTimer?.Stop();
            pollTimer?.Dispose();
            timeoutTimer?.Dispose();
            webView?.Dispose();
            form?.Dispose();

            try
            {
                if (Directory.Exists(userDataFolder))
                {
                    Directory.Delete(userDataFolder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Log("cleanup failed: " + ex.Message);
            }
        }
    }

    private static string ResolveUserDataRoot()
    {
        // 某些环境把 TEMP 指到 ACL 拒绝的目录，WebView2 会因此报 0x8000FFFF。
        // 优先用本地应用数据目录；不可写时退回程序目录。
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var probe = Path.Combine(candidate, "LiveMonitor", ".write-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // 试下一个候选。
            }
        }

        return AppContext.BaseDirectory;
    }
}
