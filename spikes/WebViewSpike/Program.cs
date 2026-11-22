using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebViewSpike;

internal static class Program
{
    private const string Url = "http://127.0.0.1:17890/";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Width = 1000, Height = 700, Text = "WebViewSpike" };
        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);
        form.Shown += async (_, _) => await RunAsync(form, webView);
        Application.Run(form);
    }

    private static async Task RunAsync(Form form, WebView2 webView)
    {
        var log = new StringBuilder();
        try
        {
            var userData = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".cache", "webview2-profile"));
            Directory.CreateDirectory(userData);
            log.AppendLine($"userData={userData}");

            var environment = await CoreWebView2Environment.CreateAsync(
                null, userData,
                new CoreWebView2EnvironmentOptions("--no-sandbox --disable-gpu --enable-logging=stderr --v=1"));
            log.AppendLine($"browserVersion={environment.BrowserVersionString}");
            await webView.EnsureCoreWebView2Async(environment);

            var tcs = new TaskCompletionSource();
            webView.NavigationCompleted += (_, e) =>
            {
                log.AppendLine($"navCompleted success={e.IsSuccess} status={e.WebErrorStatus}");
                tcs.TrySetResult();
            };
            webView.CoreWebView2.Navigate(Url);
            await tcs.Task;
            await Task.Delay(1000);

            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(Url);
            foreach (var cookie in cookies)
            {
                log.AppendLine(
                    $"cookie name={cookie.Name} value={cookie.Value} httpOnly={cookie.IsHttpOnly} " +
                    $"secure={cookie.IsSecure} domain={cookie.Domain} path={cookie.Path}");
            }

            var plain = Encoding.UTF8.GetBytes("SESSDATA=spike-test");
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            log.AppendLine($"dpapi bytes={encrypted.Length} roundtrip={Encoding.UTF8.GetString(decrypted)}");
            log.AppendLine("RESULT=PASS");
        }
        catch (Exception ex)
        {
            log.AppendLine($"RESULT=FAIL {ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.ToString());
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "webview-spike.log"), log.ToString(), Encoding.UTF8);
        Console.WriteLine(log);
        await Task.Delay(1500);
        form.Close();
    }
}
