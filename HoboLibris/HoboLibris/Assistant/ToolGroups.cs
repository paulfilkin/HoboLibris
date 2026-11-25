namespace HoboLibris.Assistant
{
    /// <summary>
    /// The tools in groups, so a model on this computer is sent only the ones the request is likely to
    /// need: the tool definitions were two thirds of its prompt (2,692 of 4,029 tokens with qwen3:8b on
    /// 25 November 2026).  It gets the group for the screen the person is on, the e-book sources and
    /// show_screen, and asks for another group with use_tools.  Hosted models get every tool.
    /// </summary>
    public static class ToolGroups
    {
        public const string KoboBooks = "kobo_books";
        public const string Collections = "collections";
        public const string Send = "send";
        public const string Kindle = "kindle";
        public const string Articles = "articles";
        public const string Previews = "previews";
        public const string Sources = "sources";

        /// <summary>The groups use_tools can add, with what each is for (for the model).</summary>
        public static readonly IReadOnlyList<(string Name, string Purpose)> Optional = new[]
        {
            (KoboBooks, "list, back up and delete Kobo books"),
            (Collections, "read and change the Kobo eReader's collections"),
            (Send, "list the book files ready to send to the Kobo eReader, send them, and eject it"),
            (Kindle, "list and back up Kindle books"),
            (Articles, "list the articles saved to the Kobo eReader with Instapaper, keep copies of them on this computer, and remove them from the eReader"),
            (Previews, "list the Kobo store previews on the eReader and the wishlist in the Kobo account, and add to or remove from the wishlist"),
        };

        private static readonly Dictionary<string, string> ByTool = new()
        {
            ["list_books"] = KoboBooks,
            ["refresh_library"] = KoboBooks,
            ["back_up_books"] = KoboBooks,
            ["delete_backups"] = KoboBooks,
            ["list_collections"] = Collections,
            ["list_collection_books"] = Collections,
            ["create_collection"] = Collections,
            ["rename_collection"] = Collections,
            ["delete_collection"] = Collections,
            ["add_to_collection"] = Collections,
            ["remove_from_collection"] = Collections,
            ["undo_collection_changes"] = Collections,
            ["save_collections"] = Collections,
            ["list_send"] = Send,
            ["send_to_ereader"] = Send,
            ["eject_ereader"] = Send,
            ["list_kindle_books"] = Kindle,
            ["back_up_kindle_books"] = Kindle,
            ["list_articles"] = Articles,
            ["keep_articles"] = Articles,
            ["remove_articles"] = Articles,
            ["list_previews"] = Previews,
            ["add_to_wishlist"] = Previews,
            ["remove_from_wishlist"] = Previews,
            ["search_ebooks"] = Sources,
            ["download_ebook"] = Sources,
            ["open_book_page"] = Sources,
            ["open_store_search"] = Sources,
        };

        /// <summary>A tool's group; null for a tool that is always sent (show_screen, and any not listed here).</summary>
        public static string? Of(string tool) => ByTool.TryGetValue(tool, out var group) ? group : null;

        /// <summary>The group for a screen, as the state names it.</summary>
        public static string ForScreen(string? screen) => screen switch
        {
            "collections" => Collections,
            "send" => Send,
            "kindle" => Kindle,
            "articles" => Articles,
            "previews" => Previews,
            _ => KoboBooks,
        };
    }
}
