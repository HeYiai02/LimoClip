using System.Net;
using System.Text;
using LiveMonitor.Core;
using LiveMonitor.Core.Interfaces;
using LiveMonitor.Desktop.Services;
using Xunit;

namespace LiveMonitor.Desktop.Tests;

public sealed class AsrSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_returns_preset_defaults_for_empty_store()
    {
        var store = new AsrSettingsStore(new FakeAppSettingsRepository());

        var snapshot = await store.LoadAsync();

        Assert.Equal("siliconflow_cn", snapshot.PresetId);
        Assert.Equal("https://api.siliconflow.cn/v1/audio/transcriptions", snapshot.Endpoint);
        Assert.Equal("FunAudioLLM/SenseVoiceSmall", snapshot.Model);
        Assert.Equal("auto", snapshot.Language);
        Assert.False(snapshot.HasApiKey);
    }

    [Fact]
    public async Task SaveAsync_encrypts_api_key_and_round_trips_config()
    {
        var settings = new FakeAppSettingsRepository();
        var store = new AsrSettingsStore(settings);

        await store.SaveAsync(new AsrSettingsDraft(
            "groq",
            "https://api.groq.com/openai/v1/audio/transcriptions",
            "whisper-large-v3-turbo",
            "zh",
            "sk-test-123"));

        string stored = Assert.IsType<string>(settings.Values[AppSettingKeys.AsrEncryptedApiKey]);
        Assert.NotEqual("sk-test-123", stored);
        Assert.True(settings.Values.ContainsKey(AppSettingKeys.AsrProviderPreset));

        var snapshot = await store.LoadAsync();
        Assert.True(snapshot.HasApiKey);
        Assert.Equal("groq", snapshot.PresetId);
        Assert.Equal("zh", snapshot.Language);

        var config = await store.GetConfigAsync();
        Assert.NotNull(config);
        Assert.Equal("sk-test-123", config!.ApiKey);
        Assert.Equal("whisper-large-v3-turbo", config.Model);
        Assert.Equal("zh", config.Language);
    }

    [Fact]
    public async Task SaveAsync_keeps_existing_key_when_draft_key_is_null()
    {
        var store = new AsrSettingsStore(new FakeAppSettingsRepository());
        await store.SaveAsync(new AsrSettingsDraft("siliconflow_cn", "", "", "auto", "sk-keep"));

        await store.SaveAsync(new AsrSettingsDraft("siliconflow_cn", "", "", "auto", PlainApiKey: null));

        var snapshot = await store.LoadAsync();
        Assert.True(snapshot.HasApiKey);
        var config = await store.GetConfigAsync();
        Assert.Equal("sk-keep", config!.ApiKey);
    }

    [Fact]
    public async Task SaveAsync_empty_key_clears_stored_key()
    {
        var settings = new FakeAppSettingsRepository();
        var store = new AsrSettingsStore(settings);
        await store.SaveAsync(new AsrSettingsDraft("siliconflow_cn", "", "", "auto", "sk-keep"));

        await store.SaveAsync(new AsrSettingsDraft("siliconflow_cn", "", "", "auto", "   "));

        Assert.False((await store.LoadAsync()).HasApiKey);
        Assert.False(settings.Values.ContainsKey(AppSettingKeys.AsrEncryptedApiKey));
    }

    [Fact]
    public async Task Custom_preset_keeps_user_endpoint_and_model()
    {
        var store = new AsrSettingsStore(new FakeAppSettingsRepository());
        await store.SaveAsync(new AsrSettingsDraft(
            "custom",
            "https://asr.example.com/v1/audio/transcriptions",
            "my-model",
            "ja",
            "sk-custom"));

        var snapshot = await store.LoadAsync();

        Assert.Equal("custom", snapshot.PresetId);
        Assert.Equal("https://asr.example.com/v1/audio/transcriptions", snapshot.Endpoint);
        Assert.Equal("my-model", snapshot.Model);
        Assert.Equal("ja", snapshot.Language);
    }

    [Fact]
    public void ResolvePreset_falls_back_to_default_for_unknown_id()
    {
        Assert.Equal("groq", AsrSettingsStore.ResolvePreset("GROQ").Id);
        Assert.Equal("siliconflow_cn", AsrSettingsStore.ResolvePreset("unknown").Id);
        Assert.Equal("siliconflow_cn", AsrSettingsStore.ResolvePreset(null).Id);
    }

    [Theory]
    [InlineData("https://api.groq.com/openai/v1/audio/transcriptions", "https://api.groq.com/openai/v1/models")]
    [InlineData("https://api.siliconflow.cn/v1/audio/transcriptions/", "https://api.siliconflow.cn/v1/models")]
    [InlineData("https://asr.example.com/v1", "https://asr.example.com/v1/models")]
    public void BuildModelsUrl_derives_models_endpoint(string endpoint, string expected)
    {
        Assert.Equal(expected, AsrSettingsStore.BuildModelsUrl(endpoint));
    }

    [Fact]
    public void BuildModelsUrl_rejects_relative_endpoint()
    {
        Assert.Throws<UriFormatException>(() => AsrSettingsStore.BuildModelsUrl("not a url"));
    }

    [Fact]
    public async Task TestConnectionAsync_sends_bearer_and_reports_success()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var store = new AsrSettingsStore(new FakeAppSettingsRepository(), new HttpClient(handler));

        var result = await store.TestConnectionAsync(new AsrSettingsDraft(
            "groq",
            "https://api.groq.com/openai/v1/audio/transcriptions",
            "whisper-large-v3-turbo",
            "auto",
            "sk-abc"));

        Assert.True(result.Success);
        Assert.Contains("whisper-large-v3-turbo", result.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.groq.com/openai/v1/models", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-abc", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task TestConnectionAsync_falls_back_to_saved_key()
    {
        var settings = new FakeAppSettingsRepository();
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var store = new AsrSettingsStore(settings, new HttpClient(handler));
        await store.SaveAsync(new AsrSettingsDraft("groq", "", "", "auto", "sk-saved"));

        var result = await store.TestConnectionAsync(new AsrSettingsDraft("groq", "", "", "auto", PlainApiKey: null));

        Assert.True(result.Success);
        Assert.Equal("sk-saved", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task TestConnectionAsync_requires_api_key()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var store = new AsrSettingsStore(new FakeAppSettingsRepository(), new HttpClient(handler));

        var result = await store.TestConnectionAsync(new AsrSettingsDraft("groq", "", "", "auto", null));

        Assert.False(result.Success);
        Assert.Contains("API Key", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestConnectionAsync_reports_http_error_body()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid api key"}""", Encoding.UTF8, "application/json")
        });
        var store = new AsrSettingsStore(new FakeAppSettingsRepository(), new HttpClient(handler));

        var result = await store.TestConnectionAsync(new AsrSettingsDraft("groq", "", "", "auto", "sk-bad"));

        Assert.False(result.Success);
        Assert.Contains("401", result.Message);
        Assert.Contains("invalid api key", result.Message);
    }

    private sealed class FakeAppSettingsRepository : IAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Values));

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
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
}
