using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SsmsChatExtension.Services
{
    /// <summary>One saved message of a conversation.</summary>
    public sealed class SavedMessage
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public ChatRole Role { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>A chat conversation as stored on disk, including the Claude session it resumes.</summary>
    public sealed class ChatConversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Title { get; set; } = "New chat";

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Claude Code session id, so reopening the chat continues the same Claude conversation.</summary>
        public string SessionId { get; set; }

        public List<SavedMessage> Messages { get; set; } = new List<SavedMessage>();

        [JsonIgnore]
        public string DisplayDate
        {
            get { return UpdatedUtc.ToLocalTime().ToString("g"); }
        }
    }

    /// <summary>
    /// Saves conversations as JSON files in %LOCALAPPDATA%\SsmsChatExtension\history (one file per chat).
    /// </summary>
    public static class ChatHistoryStore
    {
        private const int MaxTitleLength = 60;

        public static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsmsChatExtension", "history");
            }
        }

        /// <summary>All saved conversations, newest first. Unreadable files are skipped.</summary>
        public static IReadOnlyList<ChatConversation> List()
        {
            if (!Directory.Exists(Folder)) return Array.Empty<ChatConversation>();

            var conversations = new List<ChatConversation>();
            foreach (var file in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var conversation = JsonConvert.DeserializeObject<ChatConversation>(File.ReadAllText(file));
                    if (conversation != null && conversation.Messages.Count > 0) conversations.Add(conversation);
                }
                catch
                {
                    // Corrupt file - ignore it.
                }
            }
            return conversations.OrderByDescending(c => c.UpdatedUtc).ToList();
        }

        public static void Save(ChatConversation conversation)
        {
            if (conversation == null || conversation.Messages.Count == 0) return;

            conversation.UpdatedUtc = DateTime.UtcNow;
            if (conversation.Title == "New chat")
            {
                var first = conversation.Messages.FirstOrDefault(m => m.Role == ChatRole.User);
                if (first != null) conversation.Title = MakeTitle(first.Text);
            }

            Directory.CreateDirectory(Folder);
            var path = PathFor(conversation.Id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(conversation, Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static void Delete(string id)
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string PathFor(string id)
        {
            // Ids are generated GUIDs; never trust them as paths.
            var safe = new string((id ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
            if (safe.Length == 0) throw new ArgumentException("Invalid conversation id.");
            return Path.Combine(Folder, safe + ".json");
        }

        private static string MakeTitle(string text)
        {
            var title = string.Join(" ", (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0) return "New chat";
            return title.Length > MaxTitleLength ? title.Substring(0, MaxTitleLength).TrimEnd() + "..." : title;
        }
    }
}
