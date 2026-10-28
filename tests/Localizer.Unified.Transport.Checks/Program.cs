using System.Net;
using System.Text;
using System.Text.Json;
using Localizer.Translation;
using Localizer.Unified;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("New output directory required");
Directory.CreateDirectory(args[0]);
var checks = new List<object>();
void Check(string name, bool passed) { checks.Add(new { name, passed }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) throw new InvalidOperationException(name); }
var calls = 0; var catalogCalls = 0; var marked = 0; var loaded = false; var mode = "title"; string? body = null;
var credentialLoads = 0; var catalogMode = "normal"; string? catalogEndpoint = null, catalogKey = null;
string validationContent = "{}", finishReason = "stop";
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var unchanged = string.Concat(Enumerable.Repeat("山を歩いて旅を続ける。", 12));
var handler = new Handler(async (message, token) =>
{
    if (message.Method == HttpMethod.Get)
    {
        catalogCalls++;
        catalogEndpoint = message.RequestUri!.AbsoluteUri; catalogKey = message.Headers.Authorization?.Parameter;
        if (catalogMode == "denied") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("SECRET_CATALOG_BODY") };
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"fixture\"},{\"id\":\"embedding-fixture\",\"type\":\"embedding\"},{\"id\":\"alternate\"},{\"id\":\"image-fixture\",\"type\":\"image\"},{\"id\":\"fixture\"}]}") };
    }
    calls++; Check("sending marker precedes actual handler", marked == calls && loaded);
    body = await message.Content!.ReadAsStringAsync(CancellationToken.None);
    if (mode == "bad-model") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"unsupported_model\",\"message\":\"SECRET_RAW_ERROR\"}}") };
    if (mode == "hold") await release.Task;
    if (mode == "429") return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"SECRET_RAW_ERROR\"}}"), Headers = { { "Retry-After", "3" } } };
    if (mode == "broken") return new(HttpStatusCode.OK) { Content = new StringContent("not json SECRET_RAW_ERROR") };
    var content = mode == "validation" ? validationContent : mode == "unchanged" ? JsonSerializer.Serialize(new { title = unchanged }) : mode == "overview" ? "{\"overview\":\"人物__JML_PERSON_A__回来了。\"}" : mode == "term" ? "{\"entries\":[{\"id\":\"G0001\",\"translations\":{\"zh-Hans\":\"奇幻\"}}]}" : "{\"title\":\"__JML_PERSON_A__归来第2次\"}";
    var envelope = new { choices = new[] { new { finish_reason = finishReason, message = new { content } } }, usage = new { prompt_tokens = 12, completion_tokens = 8, total_tokens = 20 } };
    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json") };
});
using var transport = new UnifiedCompatibleTransport((reference, endpoint, token) => { loaded = true; credentialLoads++; return ValueTask.FromResult("FIXTURE_KEY"); },
    language => new("rules-v1", "Natural translation.", []), handler);
var connection = new ConnectionVersion("c", 1, "https://example.org/v1", "fixture", 1000, 60, "ref", "{}");
var draft = new ModelCatalogConnection("https://draft.example.org/v1", "DRAFT_KEY");
var models = await transport.ListModelsAsync(draft, CancellationToken.None);
Check("unsaved endpoint and key fetch models without a model or credential lookup", catalogEndpoint == "https://draft.example.org/v1/models" && catalogKey == "DRAFT_KEY" && credentialLoads == 0 && calls == 0 && marked == 0);
Check("catalog preserves provider order duplicates and non-translation model IDs", models.SequenceEqual(new[] { "fixture", "embedding-fixture", "alternate", "image-fixture", "fixture" }));
Check("catalog connection diagnostics never print the draft key", !draft.ToString().Contains("DRAFT_KEY", StringComparison.Ordinal));
await transport.ListModelsAsync(new(connection.Endpoint, CredentialReference: connection.CredentialReference), CancellationToken.None);
Check("saved credential fallback remains available without a model parameter", catalogKey == "FIXTURE_KEY" && credentialLoads == 1 && catalogCalls == 2);
try { await transport.ListModelsAsync(new(draft.Endpoint, "BAD KEY"), CancellationToken.None); Check("invalid draft key rejected locally", false); }
catch (InvalidOperationException e) when (e.Message == "credential_unavailable") { Check("invalid draft key rejected locally", catalogCalls == 2 && credentialLoads == 1); }
catalogMode = "denied";
try { await transport.ListModelsAsync(draft, CancellationToken.None); Check("catalog authentication error uses existing safe failure", false); }
catch (InvalidOperationException e) when (e.Message == "model_catalog_unavailable") { Check("catalog authentication error uses existing safe failure", !e.Message.Contains("SECRET_CATALOG_BODY", StringComparison.Ordinal) && calls == 0 && marked == 0); }
catalogMode = "normal";
GenerationInput Input(string field) => new("i", "s", field, "zh-Hans", field == "Term" ? "ファンタジー" : "Aliceが帰ってきた1回目", transport.PromptVersion(field, "zh-Hans"),
    "[{\"original\":\"Alice\",\"replacement\":\"爱丽丝\",\"aliases\":[],\"revision\":1}]", "{\"protected_names\":[\"Alice\"]}");
var reply = await transport.GenerateAsync(Input("Name"), connection, () => marked++, CancellationToken.None);
Check("title parsing restores frozen person mapping and accepts numeric variation diagnostically", reply.Text == "爱丽丝归来第2次" && reply.Error is null && reply.Usage?.TotalTokens == 20);
Check("request excludes raw credential and uses masked source", body is not null && !body.Contains("FIXTURE_KEY", StringComparison.Ordinal) && body.Contains("__JML_PERSON_A__", StringComparison.Ordinal));
mode = "bad-model"; reply = await transport.GenerateAsync(Input("Name"), connection with { Model = "embedding-fixture" }, () => marked++, CancellationToken.None);
Check("selected non-translation model reaches existing provider error handling", reply.HttpStatus == 400 && reply.Error == "configuration" && !reply.NotSent && body!.Contains("embedding-fixture", StringComparison.Ordinal) && !JsonSerializer.Serialize(reply).Contains("SECRET_RAW_ERROR", StringComparison.Ordinal));
mode = "unchanged"; reply = await transport.GenerateAsync(Input("Name") with { Language = "en", Text = unchanged, NamesJson = "[]", ContextJson = "{}" }, connection, () => marked++, CancellationToken.None);
Check("English output containing unchanged Japanese stays usable with diagnostics only", reply.Error is null && reply.Text == unchanged && reply.Diagnostics?.Warnings?.Contains("SourceTextUnchanged") == true);
mode = "overview"; reply = await transport.GenerateAsync(Input("Overview"), connection, () => marked++, CancellationToken.None);
Check("overview shares transport and frozen person rendering", reply.Text == "人物爱丽丝回来了。" && reply.Error is null);
var small = connection with { ParametersJson = "{\"MaxOutputTokens\":512,\"TokenParameter\":\"max_completion_tokens\"}" };
reply = await transport.GenerateAsync(Input("Overview"), small, () => marked++, CancellationToken.None);
Check("legacy 512 common limit gives overview an independent 4096 ceiling", JsonDocument.Parse(body!).RootElement.GetProperty("max_completion_tokens").GetInt32() == 4096 && reply.Diagnostics?.OutputTokenLimit == 4096);
reply = await transport.GenerateAsync(Input("Overview"), small with { ParametersJson = "{\"MaxOutputTokens\":512,\"OverviewMaxOutputTokens\":6144}" }, () => marked++, CancellationToken.None);
Check("explicit overview output limit overrides default", JsonDocument.Parse(body!).RootElement.GetProperty("max_tokens").GetInt32() == 6144);
reply = await transport.GenerateAsync(Input("Overview"), connection with { ParametersJson = "{\"MaxOutputTokens\":8192}" }, () => marked++, CancellationToken.None);
Check("higher legacy overview budget is preserved", JsonDocument.Parse(body!).RootElement.GetProperty("max_tokens").GetInt32() == 8192);
mode = "title"; reply = await transport.GenerateAsync(Input("Name"), small, () => marked++, CancellationToken.None);
Check("title and terms keep the common 512 limit", JsonDocument.Parse(body!).RootElement.GetProperty("max_completion_tokens").GetInt32() == 512);
mode = "validation";
foreach (var (content, code) in new[] { ("[]", "overview_expected_object"), ("{\"other\":1}", "overview_field_missing"), ("{\"overview\":3}", "overview_expected_string"), ("{\"overview\":\"\"}", "overview_empty_text"), ("{\"overview\":\"回来了。\"}", "overview_names_missing"), ("{\"overview\":\"__JML_PERSON_A____JML_PERSON_A__\"}", "overview_names_duplicated"), ("{SECRET_VALIDATION_BODY", "overview_invalid_json") })
{
    validationContent = content; reply = await transport.GenerateAsync(Input("Overview"), small, () => marked++, CancellationToken.None);
    Check("specific overview validation survives diagnostics: " + code, reply.Error == "invalid_output" && reply.Diagnostics?.Code == code && reply.Diagnostics.Validation?.GetProperty("Code").GetString() == code && !JsonSerializer.Serialize(reply).Contains("SECRET_VALIDATION_BODY", StringComparison.Ordinal));
}
Check("JSON parse position retained without response plaintext", reply.Diagnostics?.Validation?.GetProperty("JsonLine").ValueKind == JsonValueKind.Number);
validationContent = "{\"overview\":\"cut"; finishReason = "length";
reply = await transport.GenerateAsync(Input("Overview"), small, () => marked++, CancellationToken.None);
Check("truncation records finish reason and exact output limit", reply.Error == "output_budget" && reply.Diagnostics?.FinishReason == "length" && reply.Diagnostics.OutputTokenLimit == 4096);
finishReason = "stop";
mode = "term"; reply = await transport.GenerateAsync(Input("Term"), connection, () => marked++, CancellationToken.None);
Check("one shared dictionary term validates correlation id and requested language", reply.Text == "奇幻" && reply.Error is null);
mode = "429"; reply = await transport.GenerateAsync(Input("Name"), connection, () => marked++, CancellationToken.None);
Check("rate limit is known with shared retry duration and safe diagnostics", reply.Error == "rate_limit" && reply.HttpStatus == 429 && reply.RetryAfterMs == 3000 && !JsonSerializer.Serialize(reply).Contains("SECRET_RAW_ERROR", StringComparison.Ordinal));
mode = "broken"; reply = await transport.GenerateAsync(Input("Name"), connection, () => marked++, CancellationToken.None);
Check("malformed response stores hashes instead of raw text", reply.Error == "invalid_output" && reply.Diagnostics?.ContentSha256 is not null && !JsonSerializer.Serialize(reply).Contains("SECRET_RAW_ERROR", StringComparison.Ordinal));
var count = calls;
reply = await transport.GenerateAsync(Input("Name"), connection with { Endpoint = "http://127.0.0.1/v1" }, () => marked++, CancellationToken.None);
Check("invalid endpoint fails locally before sending", reply.NotSent && calls == count && marked == count);
using (var badCredential = new UnifiedCompatibleTransport((reference, endpoint, token) => throw new IOException("SECRET_CREDENTIAL_ERROR"), language => new("rules-v1", "", []), new Handler((m, t) => throw new InvalidOperationException("Must not send"))))
{
    reply = await badCredential.GenerateAsync(Input("Name"), connection, () => throw new InvalidOperationException("Must not mark"), CancellationToken.None);
    Check("credential failure creates no HTTP send", reply.Error == "credential_unavailable" && reply.NotSent && !JsonSerializer.Serialize(reply).Contains("SECRET_CREDENTIAL_ERROR", StringComparison.Ordinal));
}
mode = "hold";
using (var stop = new CancellationTokenSource())
{
    var task = transport.GenerateAsync(Input("Name"), connection, () => marked++, stop.Token);
    stop.Cancel(); await Task.Delay(80);
    Check("transport retains actual ignoring-cancellation HTTP task", !task.IsCompleted);
    release.SetResult();
    try { await task; } catch (OperationCanceledException) { }
    Check("held actual send is observed before transport completes", task.IsCompleted);
}
File.WriteAllText(Path.Combine(args[0], "transport-checks.json"), JsonSerializer.Serialize(new { status = "passed", synthetic_http = true, real_model_requests = 0, checks }, new JsonSerializerOptions { WriteIndented = true }));

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
