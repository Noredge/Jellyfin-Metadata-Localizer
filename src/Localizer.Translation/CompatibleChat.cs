using System.Text.Json.Nodes;

namespace Localizer.Translation;

internal static class CompatibleChat
{
    internal static JsonObject Parameters(int maximum, bool jsonMode)
    {
        var value = new JsonObject { ["max_tokens"] = maximum };
        if (jsonMode) value["response_format"] = new JsonObject { ["type"] = "json_object" };
        return value;
    }
    internal static JsonObject Body(string model, string prompt, string input, int maximum, bool jsonMode) =>
        Complete(Parameters(maximum, jsonMode), model, prompt, input);
    internal static JsonObject Complete(JsonObject parameters, string model, string prompt, string input)
    {
        parameters["model"] = model;
        parameters["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = prompt },
            new JsonObject { ["role"] = "user", ["content"] = input });
        return parameters;
    }
}
