using System.Net;
using System.Text;
using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Desktop.Services;
using LiveMonitor.Desktop.ViewModels;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

/// <summary>
/// 设置页「语音识别（ASR）」子块 ViewModel 的无 UI 单测：使用真实 <see cref="AsrSettingsStore"/>、
/// 内存版设置仓储与假 HTTP 处理器，覆盖快照映射、自定义预设校验、保存持久化、加载/忙状态与连通性测试分支。
/// 无法在此覆盖、需真机冒烟验证的部分：AXAML 控件绑定是否生效、状态文本画刷（View 按 StatusKind 映射）、
/// 按钮 IsEnabled 的实际禁用效果、窗口 DataContext 的挂载时机与 Opened 事件顺序。
/// </summary>
public sealed class AsrSettingsViewModelTests
{
    private const string GroqEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    private const string GroqModel = "whisper-large-v3-turbo";

    [Fact]
    public async Task LoadAsync_maps_snapshot_to_form()
    {
        var settings = new TestAppSettingsRepository();
        var store = CreateStore(settings);
        // 用仓储预置一份已保存配置（含加密 Key），再验证设置页回填。
        await store.SaveAsync(new AsrSettingsDraft("groq", GroqEndpoint, GroqModel, "ja", "sk-saved"));

        var vm = new AsrSettingsViewModel(store);
        var propertyChanges = new List<string?>();
        vm.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        await vm.LoadAsync();

        Assert.Equal("groq", vm.SelectedPreset!.Id);
        Assert.Equal(GroqEndpoint, vm.Endpoint);
        Assert.Equal(GroqModel, vm.Model);
        Assert.Equal(3, vm.LanguageIndex); // ja
        Assert.Equal(string.Empty, vm.ApiKey); // 明文 Key 绝不回填
        Assert.True(vm.HasApiKey);
        Assert.Equal("已保存，留空保持不变", vm.ApiKeyWatermark);
        Assert.Equal(AsrSettingsStatusKind.Neutral, vm.StatusKind);
        Assert.Equal("已读取语音识别设置。", vm.StatusText);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsNotBusy);
        Assert.Contains(nameof(AsrSettingsViewModel.ApiKeyWatermark), propertyChanges);
    }

    [Fact]
    public async Task LoadAsync_without_api_key_shows_paste_hint_and_defaults()
    {
        var (vm, _) = CreateViewModel();

        await vm.LoadAsync();

        Assert.Equal(AsrSettingsStore.DefaultPresetId, vm.SelectedPreset!.Id);
        Assert.Equal("https://api.siliconflow.cn/v1/audio/transcriptions", vm.Endpoint);
        Assert.Equal("FunAudioLLM/SenseVoiceSmall", vm.Model);
        Assert.Equal(0, vm.LanguageIndex);
        Assert.False(vm.HasApiKey);
        Assert.Equal("粘贴 API Key", vm.ApiKeyWatermark);
        Assert.Equal(AsrSettingsStatusKind.Neutral, vm.StatusKind);
        Assert.Contains("尚未配置 API Key", vm.StatusText);
    }

    [Fact]
    public async Task LoadAsync_unknown_language_falls_back_to_auto_and_saves_auto()
    {
        var settings = new TestAppSettingsRepository();
        settings.Values[AppSettingKeys.AsrLanguage] = "xx-unknown";
        var store = CreateStore(settings);
        var vm = new AsrSettingsViewModel(store);

        await vm.LoadAsync();

        Assert.Equal(0, vm.LanguageIndex);

        await vm.SaveAsync();

        Assert.Equal(AsrSettingsStore.DefaultLanguage, settings.Values[AppSettingKeys.AsrLanguage]);
    }

    [Fact]
    public async Task LoadAsync_reports_error_and_restores_loading_state()
    {
        var settings = new TestAppSettingsRepository
        {
            GetAllException = new InvalidOperationException("db down")
        };
        var vm = new AsrSettingsViewModel(CreateStore(settings));

        await vm.LoadAsync();

        Assert.Equal(AsrSettingsStatusKind.Error, vm.StatusKind);
        Assert.Contains("读取语音识别设置失败", vm.StatusText);
        Assert.Contains("db down", vm.StatusText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_sets_loading_state_while_store_is_pending()
    {
        var settings = new TestAppSettingsRepository
        {
            GetAllRelease = NewSignal()
        };
        var vm = new AsrSettingsViewModel(CreateStore(settings));

        var task = vm.LoadAsync();
        await settings.GetAllEntered;

        Assert.True(vm.IsLoading);
        Assert.Equal(AsrSettingsStatusKind.Neutral, vm.StatusKind);

        settings.GetAllRelease!.SetResult();
        await task;

        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void SelectedPreset_change_applies_preset_defaults()
    {
        var (vm, _) = CreateViewModel();

        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("groq");

        Assert.Equal(GroqEndpoint, vm.Endpoint);
        Assert.Equal(GroqModel, vm.Model);
        Assert.Equal(AsrSettingsStore.ResolvePreset("groq").Description, vm.PresetDescription);
        Assert.Equal(AsrSettingsStatusKind.Neutral, vm.StatusKind);
        Assert.Contains("已切换为", vm.StatusText);
        Assert.Contains("Groq", vm.StatusText);
    }

    [Fact]
    public void SelectedPreset_custom_keeps_fields_blank_and_shows_hint()
    {
        var (vm, _) = CreateViewModel();

        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("custom");

        Assert.Equal(string.Empty, vm.Endpoint);
        Assert.Equal(string.Empty, vm.Model);
        Assert.Contains("自定义服务需要填写接口地址和模型", vm.StatusText);
        Assert.Equal(AsrSettingsStatusKind.Neutral, vm.StatusKind);
    }

    [Fact]
    public async Task SelectedPreset_change_is_suppressed_while_loading()
    {
        var settings = new TestAppSettingsRepository();
        settings.Values[AppSettingKeys.AsrProviderPreset] = "custom";
        settings.Values[AppSettingKeys.AsrEndpoint] = "https://asr.example.com/v1/audio/transcriptions";
        settings.Values[AppSettingKeys.AsrModel] = "my-model";
        settings.Values[AppSettingKeys.AsrLanguage] = "ja";
        settings.GetAllRelease = NewSignal();
        var vm = new AsrSettingsViewModel(CreateStore(settings));

        var task = vm.LoadAsync();
        await settings.GetAllEntered;

        // 加载期间用户切换预设不应把已保存的接口/模型覆盖成预设默认值。
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("groq");
        Assert.Equal(string.Empty, vm.Endpoint);
        Assert.Equal(string.Empty, vm.Model);

        settings.GetAllRelease!.SetResult();
        await task;

        Assert.Equal("custom", vm.SelectedPreset!.Id);
        Assert.Equal("https://asr.example.com/v1/audio/transcriptions", vm.Endpoint);
        Assert.Equal("my-model", vm.Model);
        Assert.Equal(3, vm.LanguageIndex);
    }

    [Fact]
    public async Task SaveAsync_persists_form_and_clears_api_key()
    {
        var (vm, settings) = CreateViewModel();
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("groq");
        vm.LanguageIndex = 3; // ja
        vm.ApiKey = "sk-new";

        await vm.SaveAsync();

        Assert.Equal("groq", settings.Values[AppSettingKeys.AsrProviderPreset]);
        Assert.Equal(GroqEndpoint, settings.Values[AppSettingKeys.AsrEndpoint]);
        Assert.Equal(GroqModel, settings.Values[AppSettingKeys.AsrModel]);
        Assert.Equal("ja", settings.Values[AppSettingKeys.AsrLanguage]);
        string encrypted = Assert.IsType<string>(settings.Values[AppSettingKeys.AsrEncryptedApiKey]);
        Assert.NotEqual("sk-new", encrypted);

        // 保存后清空明文输入并重新读取，刷新 Watermark 与状态。
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.True(vm.HasApiKey);
        Assert.Equal("已保存，留空保持不变", vm.ApiKeyWatermark);
        Assert.Equal(AsrSettingsStatusKind.Success, vm.StatusKind);
        Assert.Equal("语音识别设置已保存。", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveAsync_blank_api_key_keeps_existing_key()
    {
        var settings = new TestAppSettingsRepository();
        var store = CreateStore(settings);
        await store.SaveAsync(new AsrSettingsDraft("siliconflow_cn", "", "", "auto", "sk-keep"));
        var vm = new AsrSettingsViewModel(store);
        await vm.LoadAsync();
        Assert.Equal(string.Empty, vm.ApiKey);

        await vm.SaveAsync();

        var config = await store.GetConfigAsync();
        Assert.NotNull(config);
        Assert.Equal("sk-keep", config!.ApiKey);
    }

    [Fact]
    public async Task SaveAsync_invalid_custom_preset_blocks_persistence()
    {
        var (vm, settings) = CreateViewModel();
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("custom");
        vm.ApiKey = "sk-x";

        await vm.SaveAsync();

        Assert.Equal(0, settings.SetAttempts);
        Assert.Equal(AsrSettingsStatusKind.Error, vm.StatusKind);
        Assert.Equal("自定义服务需要填写接口地址和模型。", vm.StatusText);
        Assert.Equal("sk-x", vm.ApiKey);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveAsync_custom_preset_with_values_persists()
    {
        var (vm, settings) = CreateViewModel();
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("custom");
        vm.Endpoint = "https://asr.example.com/v1/audio/transcriptions";
        vm.Model = "my-model";
        vm.ApiKey = "sk-custom";

        await vm.SaveAsync();

        Assert.Equal("custom", settings.Values[AppSettingKeys.AsrProviderPreset]);
        Assert.Equal("https://asr.example.com/v1/audio/transcriptions", settings.Values[AppSettingKeys.AsrEndpoint]);
        Assert.Equal("my-model", settings.Values[AppSettingKeys.AsrModel]);
        Assert.Equal(AsrSettingsStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task SaveAsync_reports_store_failure_without_clearing_key()
    {
        var (vm, settings) = CreateViewModel();
        settings.SetException = new InvalidOperationException("disk full");
        vm.ApiKey = "sk-x";

        await vm.SaveAsync();

        Assert.Equal(AsrSettingsStatusKind.Error, vm.StatusKind);
        Assert.Contains("保存语音识别设置失败", vm.StatusText);
        Assert.Contains("disk full", vm.StatusText);
        Assert.Equal("sk-x", vm.ApiKey);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveAsync_sets_busy_state_and_ignores_reentry_while_pending()
    {
        var (vm, settings) = CreateViewModel();
        settings.SetRelease = NewSignal();
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("groq");
        vm.ApiKey = "sk-x";

        var task = vm.SaveAsync();
        await settings.SetEntered;

        Assert.True(vm.IsBusy);
        Assert.False(vm.IsNotBusy);
        Assert.Equal(1, settings.SetAttempts);

        // 保存进行中再次点击不应重复写库。
        await vm.SaveAsync();
        Assert.Equal(1, settings.SetAttempts);

        settings.SetRelease!.SetResult();
        await task;

        Assert.False(vm.IsBusy);
        Assert.Equal(5, settings.SetAttempts); // 预设 + 接口 + 模型 + 语言 + Key
        Assert.Equal(AsrSettingsStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task LanguageIndex_out_of_range_saves_default_language()
    {
        var (vm, settings) = CreateViewModel();
        vm.LanguageIndex = 99;

        await vm.SaveAsync();

        Assert.Equal(AsrSettingsStore.DefaultLanguage, settings.Values[AppSettingKeys.AsrLanguage]);
    }

    [Fact]
    public async Task TestConnectionAsync_reports_success_without_persisting()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (vm, settings) = CreateViewModel(handler);
        vm.ApiKey = "sk-test";

        await vm.TestConnectionAsync();

        Assert.Equal(AsrSettingsStatusKind.Success, vm.StatusKind);
        Assert.Contains("连接成功", vm.StatusText);
        Assert.Contains("FunAudioLLM/SenseVoiceSmall", vm.StatusText);
        Assert.Equal("测试连接", vm.TestButtonText);
        Assert.False(vm.IsBusy);
        Assert.Equal(0, settings.SetAttempts);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.siliconflow.cn/v1/models", request.RequestUri!.AbsoluteUri);
        Assert.Equal("sk-test", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task TestConnectionAsync_reports_http_error()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid api key"}""", Encoding.UTF8, "application/json")
        });
        var (vm, _) = CreateViewModel(handler);
        vm.ApiKey = "sk-bad";

        await vm.TestConnectionAsync();

        Assert.Equal(AsrSettingsStatusKind.Error, vm.StatusKind);
        Assert.Contains("401", vm.StatusText);
        Assert.Contains("invalid api key", vm.StatusText);
        Assert.Equal("测试连接", vm.TestButtonText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task TestConnectionAsync_sets_busy_and_button_text_while_pending()
    {
        var handler = new GatedHttpHandler();
        var (vm, _) = CreateViewModel(handler);
        vm.ApiKey = "sk-test";

        var task = vm.TestConnectionAsync();
        await handler.Entered;

        Assert.True(vm.IsBusy);
        Assert.False(vm.IsNotBusy);
        Assert.Equal("正在测试…", vm.TestButtonText);

        handler.Respond(HttpStatusCode.OK);
        await task;

        Assert.False(vm.IsBusy);
        Assert.Equal("测试连接", vm.TestButtonText);
        Assert.Equal(AsrSettingsStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task TestConnectionAsync_invalid_custom_preset_skips_http()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var (vm, _) = CreateViewModel(handler);
        vm.SelectedPreset = AsrSettingsStore.ResolvePreset("custom");
        vm.ApiKey = "sk-x";

        await vm.TestConnectionAsync();

        Assert.Empty(handler.Requests);
        Assert.Equal(AsrSettingsStatusKind.Error, vm.StatusKind);
        Assert.Equal("自定义服务需要填写接口地址和模型。", vm.StatusText);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (AsrSettingsViewModel Vm, TestAppSettingsRepository Settings) CreateViewModel(
        HttpMessageHandler? handler = null)
    {
        var settings = new TestAppSettingsRepository();
        var store = CreateStore(settings, handler);
        return (new AsrSettingsViewModel(store), settings);
    }

    private static AsrSettingsStore CreateStore(
        TestAppSettingsRepository settings,
        HttpMessageHandler? handler = null)
    {
        var http = handler ?? new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        return new AsrSettingsStore(settings, new HttpClient(http));
    }

    private sealed class TestAppSettingsRepository : IAppSettingsRepository
    {
        private readonly TaskCompletionSource _getAllEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _setEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>当前键值内容；测试可直接读写以预置/断言配置。</summary>
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>设置后 <see cref="GetAllAsync"/> 抛出该异常（用于读取失败分支）。</summary>
        public Exception? GetAllException { get; set; }

        /// <summary>设置后 <see cref="SetAsync"/> 抛出该异常（用于保存失败分支）。</summary>
        public Exception? SetException { get; set; }

        /// <summary>设置后 GetAllAsync 在返回前挂起，直到测试调用 SetResult（用于加载状态断言）。</summary>
        public TaskCompletionSource? GetAllRelease { get; set; }

        /// <summary>设置后 SetAsync 在写入前挂起，直到测试调用 SetResult（用于忙状态断言）。</summary>
        public TaskCompletionSource? SetRelease { get; set; }

        public int SetAttempts { get; private set; }

        public Task GetAllEntered => _getAllEntered.Task;

        public Task SetEntered => _setEntered.Task;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>(Values.TryGetValue(key, out var value) ? value : null);

        public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
        {
            if (GetAllException is not null)
            {
                throw GetAllException;
            }

            var snapshot = new Dictionary<string, string>(Values);
            if (GetAllRelease is not null)
            {
                _getAllEntered.TrySetResult();
                await GetAllRelease.Task.ConfigureAwait(false);
            }

            return snapshot;
        }

        public async Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            SetAttempts++;
            if (SetException is not null)
            {
                throw SetException;
            }

            if (SetRelease is not null)
            {
                _setEntered.TrySetResult();
                await SetRelease.Task.ConfigureAwait(false);
            }

            Values[key] = value;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class GatedHttpHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Respond(HttpStatusCode status, string body = "")
            => _response.TrySetResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return await _response.Task.ConfigureAwait(false);
        }
    }
}
