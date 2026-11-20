using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterRightLang.Desktop;

internal sealed record Preferences(CorrectionMode Mode, string Language)
{
    public string[] Whitelist { get; init; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<CorrectionMode>(allowIntegerValues: false) }
    };

    internal static Preferences Load(string file)
    {
        var defaults = new Preferences(CorrectionMode.Auto,
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "th" ? "th" : "en");
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file), Json);
            return saved is not null && Enum.IsDefined(saved.Mode) && saved.Language is "th" or "en" ? saved : defaults;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return defaults; }
    }

    internal void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, file, true);
        }
        finally { File.Delete(temp); }
    }
}
