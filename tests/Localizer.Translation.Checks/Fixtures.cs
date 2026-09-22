using System.Net;
using System.Text;
using System.Text.Json;
using Localizer.Core;

public sealed class FakeProvider : ITitleProvider
{
    public int Calls { get; private set; }
    public int MaximumActive { get; private set; }
    private int active;
    public Func<TitleProviderRequest, CancellationToken, Task<TitleProviderResult>>? Handler { get; set; }
    public async Task<TitleProviderResult> TranslateAsync(TitleProviderRequest request, CancellationToken token)
    {
        Calls++; active++; MaximumActive = Math.Max(active, MaximumActive);
        try { return Handler is null ? new("合成中文候选", null, []) : await Handler(request, token); }
        finally { active--; }
    }
}
public sealed class StubHttp : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        return Handler is null ? Task.FromResult(Response("__JML_PERSON_A__的旅行 0")) : Handler(request, token);
    }
    public static HttpResponseMessage Response(string title, string finish = "stop", string model = "synthetic-model") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { model, choices = new[] { new { finish_reason = finish, message = new { content = JsonSerializer.Serialize(new { title }) } } }, usage = new { total_tokens = 24 } }), Encoding.UTF8, "application/json") };
}
