using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveMonitor.Core.Enums;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WinForms = System.Windows.Forms;

namespace LiveMonitor.Desktop.Services;

/// <summary>
/// 在独立的 STA 线程上打开 WinForms + WebView2 登录窗。
/// 登录成功后从 CookieManager 枚举 HttpOnly Cookie（SESSDATA / auth-token），
/// 返回可直接用于 HTTP 请求的 Cookie 头；窗口关闭或超时返回 null。
/// </summary>
public sealed class WebViewLoginService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

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
        var loginUrl = platform switch
        {
            PlatformType.Bilibili => "https://passport.bilibili.com/login",
            PlatformType.Twitch => "https://www.twitch.tv/login",
            _ => throw new NotSupportedException($"平台 {platform} 不支持 WebView2 登录。")
        };

        var cookieUri = platform switch
        {
            PlatformType.Bilibili => "https://www.bilibili.com",
            PlatformType.Twitch => "https://www.twitch.tv",
            _ => throw new NotSupportedException()
        };

        var cookieNames = platform switch
        {
            PlatformType.Bilibili => new[] { "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid" },
            PlatformType.Twitch => new[] { "auth-token", "persistent", "unique_id", "twilight-user" },
            _ => Array.Empty<string>()
        };

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
                Text = platform == PlatformType.Bilibili ? "登录 B 站" : "登录 Twitch",
                Width = 980,
                Height = 760,
                StartPosition = WinForms.FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = true
            };

            webView = new WebView2 { Dock = WinForms.DockStyle.Fill };
            form.Controls.Add(webView);
            form.FormClosed += (_, _) =>
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                {
                    tcs.TrySetResult(null);
                }
            };

            form.Shown += async (_, _) =>
            {
                try
                {
                    Log($"creating environment {userDataFolder}");
                    var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await webView.EnsureCoreWebView2Async(environment);
                    Log("webview ready; navigating " + loginUrl);
                    webView.CoreWebView2.Navigate(loginUrl);

                    pollTimer = new WinForms.Timer { Interval = 1500 };
                    pollTimer.Tick += async (_, _) =>
                    {
                        if (Volatile.Read(ref finished) != 0)
                        {
                            return;
                        }

                        try
                        {
                            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(cookieUri);
                            var matched = cookies
                                .Where(cookie => cookieNames.Contains(cookie.Name, StringComparer.Ordinal))
                                .Where(cookie => !string.IsNullOrEmpty(cookie.Value))
                                .ToList();
                            if (matched.Count == 0)
                            {
                                return;
                            }

                            var header = string.Join("; ", matched.Select(cookie => $"{cookie.Name}={cookie.Value}"));
                            Log($"captured {matched.Count} cookies: {string.Join(",", matched.Select(cookie => cookie.Name))}");
                            pollTimer?.Stop();
                            Finish(header);
                        }
                        catch (Exception ex)
                        {
                            Log("poll failed: " + ex.Message);
                        }
                    };
                    pollTimer.Start();

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
