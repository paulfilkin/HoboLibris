using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HoboLibris.Assistant
{
    /// <summary>Whether a tool runs by itself, asks first, or isn't offered to the model.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ToolPolicy
    {
        Allow,
        Ask,
        Off,
    }

    /// <summary>What a tool gives back to the model, and a one-line summary for the person.</summary>
    public sealed record ToolOutcome(string Content, bool IsError = false, string? Summary = null);

    /// <summary>
    /// A tool the Assistant can use.  Name is what the model sees (letters, digits, '_' and '-', at most
    /// 64, as both Anthropic and OpenAI require); Title is what the person sees.  Ported from Mycelia
    /// without its MCP parts.
    /// </summary>
    public sealed class AssistantTool
    {
        public required string Name { get; init; }
        public required string Title { get; init; }

        /// <summary>For the model, in English.</summary>
        public required string Description { get; init; }

        public required JsonObject Schema { get; init; }

        /// <summary>The tool changes something (asked about by default).</summary>
        public bool Changes { get; init; }

        /// <summary>
        /// The tool writes to the eReader or deletes files: it asks every time, whatever the settings
        /// say, and the approval card has no "Always allow".
        /// </summary>
        public bool AlwaysAsks { get; init; }

        public ToolPolicy Policy { get; set; }

        /// <summary>The policy before the person changes it: Ask for tools that change something, else Allow.</summary>
        public ToolPolicy Default => Changes || AlwaysAsks ? ToolPolicy.Ask : ToolPolicy.Allow;

        /// <summary>The call needs the person's answer first.</summary>
        public bool NeedsApproval(bool skipApprovals) => AlwaysAsks || (Policy == ToolPolicy.Ask && !skipApprovals);

        public required Func<JsonObject, CancellationToken, Task<ToolOutcome>> Run { get; init; }

        /// <summary>
        /// Looks at a call before the person is asked about it.  An answer means the call can do nothing
        /// (no eReader, ids from the wrong list, nothing to back up): the model gets the answer and no
        /// card is shown.  Null means go ahead.  Run checks again, as things can change while a card waits.
        /// </summary>
        public Func<JsonObject, ToolOutcome?>? Check { get; init; }

        /// <summary>A plain sentence saying what a call will do, for the approval card.</summary>
        public Func<JsonObject, string>? Describe { get; init; }

        public ToolSpec ToSpec() => new(Name, Description, Schema);
    }

    /// <summary>Small helpers for writing JSON Schemas and reading arguments.</summary>
    public static class Schema
    {
        public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, schema, isRequired) in properties)
            {
                // A JSON node can belong to one parent only; shared argument schemas are copied.
                props[name] = schema.DeepClone();
                if (isRequired)
                {
                    required.Add(name);
                }
            }
            var result = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0)
            {
                result["required"] = required;
            }
            return result;
        }

        public static JsonObject String(string description, params string[] choices)
        {
            var schema = new JsonObject { ["type"] = "string", ["description"] = description };
            if (choices.Length > 0)
            {
                schema["enum"] = new JsonArray(choices.Select(c => (JsonNode)c).ToArray());
            }
            return schema;
        }

        public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

        public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

        public static JsonObject Array(JsonObject items, string description) =>
            new() { ["type"] = "array", ["items"] = items.DeepClone(), ["description"] = description };

        public static string? Str(this JsonObject args, string name)
        {
            var node = args[name];
            if (node == null)
            {
                return null;
            }
            string text = node is JsonValue value && value.TryGetValue(out string? s) ? s : node.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        public static int Int(this JsonObject args, string name, int fallback)
        {
            var node = args[name];
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out int number))
                {
                    return number;
                }
                if (value.TryGetValue(out double real))
                {
                    return (int)real;
                }
                if (value.TryGetValue(out string? text) && int.TryParse(text, out number))
                {
                    return number;
                }
            }
            return fallback;
        }

        public static bool Bool(this JsonObject args, string name, bool fallback)
        {
            var node = args[name];
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out bool flag))
                {
                    return flag;
                }
                if (value.TryGetValue(out string? text) && bool.TryParse(text, out flag))
                {
                    return flag;
                }
            }
            return fallback;
        }

        public static List<string> Strings(this JsonObject args, string name)
        {
            var node = args[name];
            if (node is JsonArray array)
            {
                return array.Select(n => n?.ToString() ?? string.Empty).Where(s => s.Length > 0).ToList();
            }
            if (node != null && node.ToString() is { Length: > 0 } single)
            {
                return single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            return new List<string>();
        }
    }
}
