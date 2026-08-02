using System.Text.Json.Nodes;

namespace NoSilence.Settings;

/// <summary>
/// Strips values that match the defaults, so <c>settings.json</c> records only what the user
/// actually changed.
/// </summary>
/// <remarks>
/// Writing the whole object graph looks harmless and is not. The app saves settings on exit,
/// so every install ends up with a complete file — and from then on an improved default can
/// never reach it. This bit us directly: after the release window was shortened from 20 s to
/// 5 s and console hosts were added to the rules, an existing config silently kept the old
/// values, and the console-bell fix was inert.
/// <para>
/// The trade-off, stated plainly: if a value is deliberately set to whatever the default
/// happens to be today, it is not recorded, and it will move if that default later changes.
/// For a personal tool being actively tuned, defaults reaching users matters more.
/// </para>
/// </remarks>
internal static class SparseJson
{
    /// <summary>Keys always written, even when they equal the default.</summary>
    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "schemaVersion",
    };

    /// <summary>
    /// Returns <paramref name="value"/> with every property that deep-equals the matching
    /// property of <paramref name="defaults"/> removed. Null if nothing is left.
    /// </summary>
    public static JsonNode? Strip(JsonNode? value, JsonNode? defaults)
    {
        if (value is not JsonObject obj)
        {
            return AreEqual(value, defaults) ? null : value;
        }

        var defaultObj = defaults as JsonObject;
        var result = new JsonObject();

        foreach ((string key, JsonNode? child) in obj)
        {
            JsonNode? defaultChild = defaultObj is not null && defaultObj.TryGetPropertyValue(key, out JsonNode? d) ? d : null;

            if (AlwaysKeep.Contains(key))
            {
                result[key] = child?.DeepClone();
                continue;
            }

            // Arrays are all-or-nothing: a rules list that differs is only meaningful whole,
            // and merging one element-wise against defaults would be unpredictable.
            if (child is JsonArray or JsonValue or null)
            {
                if (!AreEqual(child, defaultChild))
                {
                    result[key] = child?.DeepClone();
                }

                continue;
            }

            JsonNode? stripped = Strip(child, defaultChild);
            if (stripped is not null)
            {
                result[key] = stripped;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool AreEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return JsonNode.DeepEquals(a, b);
    }
}
