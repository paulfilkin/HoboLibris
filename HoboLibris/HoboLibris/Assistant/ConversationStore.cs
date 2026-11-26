using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HoboLibris.Services;

namespace HoboLibris.Assistant
{
    /// <summary>One line of the pane as saved: a message, notice, tool activity or approval.</summary>
    public sealed class SavedChatItem
    {
        /// <summary>user, assistant, notice, tool, results (a search's list of books, as JSON in Result) or approval.</summary>
        public string Kind { get; set; } = string.Empty;
        public DateTime Time { get; set; }
        public string? Text { get; set; }
        public string? Context { get; set; }
        public bool IsError { get; set; }
        public string? Title { get; set; }
        public string? Arguments { get; set; }
        public string? Summary { get; set; }
        public string? Result { get; set; }
        public string? State { get; set; }
        public string? Description { get; set; }
        public string? Outcome { get; set; }
    }

    /// <summary>A tool call as saved, its arguments as JSON text.</summary>
    public sealed class SavedToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Arguments { get; set; } = "{}";
    }

    /// <summary>One turn of what is sent to the model: the person's message, the model's reply, or tool results.</summary>
    public sealed class SavedTurn
    {
        /// <summary>user, assistant or tools.</summary>
        public string Kind { get; set; } = string.Empty;
        public string? Text { get; set; }

        /// <summary>The state sent with a person's message (#77), kept so a reopened conversation sends the same bytes.</summary>
        public string? State { get; set; }
        public List<SavedToolCall>? Calls { get; set; }
        public List<ToolResult>? Results { get; set; }
    }

    /// <summary>The Assistant's conversation, as kept between sessions.</summary>
    public sealed class SavedConversation
    {
        public int Version { get; set; } = 1;
        public DateTime SavedUtc { get; set; }
        public List<SavedChatItem> Items { get; set; } = new();
        public List<SavedTurn> Turns { get; set; } = new();
    }

    /// <summary>
    /// Keeps the Assistant's conversation between sessions until the person clears it: what the pane
    /// shows and what is sent to the model, so the model keeps its context.  The file is encrypted with
    /// DPAPI for the current Windows user (SecretStore), because it holds book titles and collection
    /// names; one that can't be read is treated as no conversation.  Ported from Mycelia, which keeps
    /// one per family tree; HoboLibris has one.
    /// </summary>
    public static class ConversationStore
    {
        public static string DefaultPath => Path.Combine(AppPaths.DataDir, "Assistant", "conversation.json");

        public static void Save(SavedConversation conversation, string path)
        {
            conversation.SavedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, SecretStore.Protect(JsonSerializer.Serialize(conversation)));
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>The saved conversation, or null when there is none or it can't be read.</summary>
        public static SavedConversation? Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                string json = SecretStore.Unprotect(File.ReadAllText(path));
                return json.Length == 0 ? null : JsonSerializer.Deserialize<SavedConversation>(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        public static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public static SavedTurn ToSaved(ChatTurn turn) => turn switch
        {
            UserTurn user => new SavedTurn { Kind = "user", Text = user.Text, State = user.State },
            AssistantTurn assistant => new SavedTurn
            {
                Kind = "assistant",
                Text = assistant.Text,
                Calls = assistant.ToolCalls.Select(c => new SavedToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments.ToJsonString() }).ToList(),
            },
            ToolResultsTurn results => new SavedTurn { Kind = "tools", Results = results.Results.ToList() },
            _ => throw new ArgumentException("Unknown turn " + turn.GetType().Name),
        };

        /// <summary>The turn for the model, or null for one that can't be rebuilt.</summary>
        public static ChatTurn? FromSaved(SavedTurn turn) => turn.Kind switch
        {
            "user" => new UserTurn(turn.Text ?? string.Empty, turn.State),
            "assistant" => new AssistantTurn(turn.Text ?? string.Empty,
                (turn.Calls ?? new List<SavedToolCall>()).Select(c => new ToolCall(c.Id, c.Name, ParseArguments(c.Arguments))).ToList()),
            "tools" => new ToolResultsTurn(turn.Results ?? new List<ToolResult>()),
            _ => null,
        };

        private static JsonObject ParseArguments(string text)
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }
    }
}
