using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Localizer.Core;

namespace Localizer.Translation;

/// <summary>Stateless LM Studio native title requests. No credentials, tools, redirects, proxies or fallback.</summary>
public sealed class LmStudioTitleProvider : ITitleProvider, IDisposable
{
    private readonly HttpClient client;
    public LmStudioTitleProvider(HttpMessageHandler? testHandler = null)
    {
        client = new(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                // Pin localhost as loopback without trusting DNS/hosts mappings. TLS validation remains enabled.
                var host = context.DnsEndPoint.Host;
                var address = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(host);
                if (!IPAddress.IsLoopback(address)) throw new HttpRequestException("LocalEndpointRequired");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken token)
    {
        string endpoint; JsonObject body; int maximum;
        try
        {
            endpoint = TranslationServiceProfile.LocalEndpoint(request.Spec.Endpoint);
            if (!TranslationServiceProfile.ValidModel(request.Spec.Model)) throw new ArgumentException();
            TranslationServiceProfile.RequireConfiguredModel(request.Spec.Model);
            var protection = TitlePipeline.ValidateContext(request);
            body = JsonNode.Parse(request.Spec.ParametersJson) as JsonObject ?? throw new ArgumentException();
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "temperature", "max_output_tokens", "reasoning", "store", "integrations", "stream" };
            if (body.Count != allowed.Count || body.Any(x => !allowed.Contains(x.Key))
                || body["temperature"]?.GetValue<double>() != 0.2 || body["reasoning"]?.GetValue<string>() != "off"
                || body["store"]?.GetValue<bool>() != false || body["stream"]?.GetValue<bool>() != false
                || body["integrations"] is not JsonArray integrations || integrations.Count != 0)
                throw new ArgumentException();
            maximum = body["max_output_tokens"]?.GetValue<int>() ?? 0;
            if (maximum is < 64 or > 4096) throw new ArgumentException();
            body["model"] = request.Spec.Model; body["system_prompt"] = request.Spec.PromptText; body["input"] = protection.MaskedSource;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { throw new TitleProviderException(ProviderError.Configuration); }
        var (text, diagnostics) = await SendAsync(HttpMethod.Post, endpoint + "/chat", body.ToJsonString(), token);
        if (text is null) return new(null, "ResponseTooLarge", [], Diagnostics: diagnostics);
        try
        {
            using var document = JsonDocument.Parse(text); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return new(null, "InvalidNativeResponse", [], Diagnostics: diagnostics);
            var output = root.GetProperty("output"); var stats = root.GetProperty("stats");
            if (output.ValueKind != JsonValueKind.Array || stats.ValueKind != JsonValueKind.Object)
                return new(null, "InvalidNativeResponse", [], Diagnostics: diagnostics);
            var final = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "reasoning") continue; // Never retain reasoning text.
                if (type != "message" || item.GetProperty("content").ValueKind != JsonValueKind.String)
                    return new(null, "InvalidNativeResponse", [], Diagnostics: diagnostics);
                final.Add(item.GetProperty("content").GetString()!);
            }
            if (final.Count != 1) return new(null, "InvalidNativeResponse", [], Diagnostics: diagnostics);
            static long Count(JsonElement stats, string name)
            {
                var value = stats.GetProperty(name);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var n) || n is < 0 or > 100000000)
                    throw new ArgumentException();
                return n;
            }
            var input = Count(stats, "input_tokens"); var generated = Count(stats, "total_output_tokens");
            var thinking = Count(stats, "reasoning_output_tokens");
            if (thinking > generated) throw new ArgumentException();
            diagnostics = diagnostics with { PromptTokens = input, CompletionTokens = generated, TotalTokens = input + generated };
            // Native non-streaming has no finish_reason; reaching the frozen budget is conservatively incomplete.
            var normalized = JsonSerializer.Serialize(new { model = root.GetProperty("model_instance_id").GetString(),
                choices = new[] { new { finish_reason = generated >= maximum ? "length" : "stop", message = new { content = final[0] } } },
                usage = new { prompt_tokens = input, completion_tokens = generated, total_tokens = input + generated } });
            var result = TitlePipeline.Parse(normalized, request);
            return result with { Diagnostics = diagnostics, TotalTokens = input + generated,
                Warnings = thinking == 0 ? result.Warnings : result.Warnings.Append("UnexpectedReasoningTokens").Distinct().ToArray() };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new(null, "InvalidNativeResponse", [], Diagnostics: diagnostics); }
    }
    public async Task<TranslationModelInfo[]> ListModelsAsync(string endpoint, CancellationToken token)
    {
        endpoint = TranslationServiceProfile.LocalEndpoint(endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var (text, _) = await SendAsync(HttpMethod.Get, endpoint + "/models", null, linked.Token);
        try
        {
            if (text is null) throw new ArgumentException();
            using var document = JsonDocument.Parse(text); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new ArgumentException();
            var result = new List<TranslationModelInfo>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object) throw new ArgumentException();
                if (model.TryGetProperty("type", out var type) && type.GetString() != "llm") continue;
                var key = model.GetProperty("key").GetString();
                if (!TranslationServiceProfile.ValidModel(key)) throw new ArgumentException();
                var name = model.TryGetProperty("display_name", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : key;
                var loaded = model.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0;
                if (loaded)
                {
                    foreach (var instance in instances.EnumerateArray())
                    {
                        var id = instance.GetProperty("id").GetString();
                        if (!TranslationServiceProfile.ValidModel(id)) throw new ArgumentException();
                        result.Add(new(id!, name, true));
                    }
                }
                else result.Add(new(key!, name, false));
                if (result.Count > 2000) throw new ArgumentException();
            }
            return result.DistinctBy(x => x.Id).ToArray();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new TitleProviderException(ProviderError.Configuration); }
    }
    private async Task<(string? Text, ProviderDiagnostics Diagnostics)> SendAsync(HttpMethod method, string endpoint, string? body, CancellationToken token)
    {
        using var message = new HttpRequestMessage(method, endpoint);
        if (body is not null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            var text = await GroqDiagnostics.Body(response, token);
            var diagnostics = GroqDiagnostics.Read(response, watch.ElapsedMilliseconds, text);
            if (!response.IsSuccessStatusCode)
                throw new TitleProviderException(GroqDiagnostics.Category(response.StatusCode, diagnostics.ServiceCode),
                    diagnostics.RetryAfterMs is { } delay ? TimeSpan.FromMilliseconds(delay) : null, diagnostics);
            return (text, diagnostics);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw new TitleProviderException(ProviderError.TransportUncertain, diagnostics: new(ElapsedMs: watch.ElapsedMilliseconds)); }
    }
    public void Dispose() => client.Dispose();
}
