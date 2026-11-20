using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nitrox.Model.Logger;

namespace Nitrox.Model.ModSync;

/// <summary>
///     Plugins with known multiplayer problems. Nitrod ships no entries it can't back up with evidence; players and hosts can add
///     their own findings in <c>known-incompatibilities.json</c> in the Nitrod config folder:
///     <code>[{ "Guid": "com.example.mod", "Reason": "Spawns creatures locally, which desyncs.", "Severity": "WARNING" }]</code>
/// </summary>
public static class KnownIncompatibilityList
{
    public const string FILE_NAME = "known-incompatibilities.json";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Entries backed by Nitrod's own tests/reports. Intentionally conservative.</summary>
    public static IReadOnlyList<KnownIncompatibility> BuiltIn { get; } = [];

    public static List<KnownIncompatibility> Load(string configDirectory)
    {
        List<KnownIncompatibility> result = [..BuiltIn];
        string path = Path.Combine(configDirectory, FILE_NAME);
        if (!File.Exists(path))
        {
            return result;
        }
        try
        {
            List<KnownIncompatibility>? custom = JsonSerializer.Deserialize<List<KnownIncompatibility>>(File.ReadAllText(path), jsonOptions);
            if (custom != null)
            {
                result.AddRange(custom.FindAll(entry => !string.IsNullOrWhiteSpace(entry.Guid)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {FILE_NAME}: {ex.Message}");
        }
        return result;
    }
}
