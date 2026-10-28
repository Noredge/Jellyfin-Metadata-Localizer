using System.Text.Json.Nodes;

namespace Localizer.Translation;

internal static class CompatibleChat
{
    internal static JsonObject Parameters(int maximum, bool jsonMode, string tokenParameter = "max_tokens", string? reasoning = null, bool? store = null)
    {
        var value = new JsonObject { [tokenParameter] = maximum };
        if (reasoning is not null) value["reasoning_effort"] = reasoning;
        if (store is not null) value["store"] = store.Value;
        if (jsonMode) value["response_format"] = new JsonObject { ["type"] = "json_object" };
        return value;
    }
    internal static JsonObject Body(string model, string prompt, string input, int maximum, bool jsonMode,
        string tokenParameter = "max_tokens", string? reasoning = null, bool? store = null) =>
        Complete(Parameters(maximum, jsonMode, tokenParameter, reasoning, store), model, prompt, input);
    internal static JsonObject Complete(JsonObject parameters, string model, string prompt, string input)
    {
        parameters["model"] = model;
        parameters["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = prompt },
            new JsonObject { ["role"] = "user", ["content"] = input });
        return parameters;
    }
}
