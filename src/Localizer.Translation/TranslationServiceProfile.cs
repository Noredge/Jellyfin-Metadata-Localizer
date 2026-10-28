namespace Localizer.Translation;

// Public HTTPS compatible protocol is the only runtime transport.
public static class TranslationServiceProfile
{
    internal static bool ValidModel(string? model) => !string.IsNullOrWhiteSpace(model) && model.Length <= 256
        && !model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
