using System.Text.Json.Nodes;
using HoboLibris.Helpers;
using HoboLibris.Models;
using HoboLibris.Resources;
using HoboLibris.Services;
using HoboLibris.ViewModels;

namespace HoboLibris.Assistant
{
    /// <summary>
    /// The Assistant's tools for HoboLibris's own features: the Kobo list and backups, collections,
    /// Send to eReader and the Kindle tab.  Each calls the same view-model code as the screens, so
    /// what the Assistant does shows on screen as if done by hand.  Descriptions are for the model and
    /// stay in English; titles and approval sentences are for the person and are translated.
    /// </summary>
    public sealed class HoboTools
    {
        private const int DefaultLimit = 200;
        private readonly MainViewModel _main;
        private readonly SourceTools _sources;

        public HoboTools(MainViewModel main)
        {
            _main = main;
            _sources = new SourceTools(main);
            All = Build().Concat(_sources.All).ToList();
        }

        public IReadOnlyList<AssistantTool> All { get; }

        /// <summary>Background work the sources want at start-up (the Gutenberg catalogue).</summary>
        public Task WarmUpAsync() => _sources.WarmUpAsync();

        /// <summary>The person pressed Download in a search's list.</summary>
        public Task<ToolOutcome> DownloadFoundAsync(Sources.EbookResult book) => _sources.DownloadFoundAsync(book);

        private CollectionsViewModel Collections => _main.Collections;
        private ArticlesViewModel ArticlesScreen => _main.Articles;
        private PreviewsViewModel PreviewsScreen => _main.Previews;

        private static readonly JsonObject Ids = Schema.Array(Schema.String("A book id from the list tool."), "The books, by id.");
        private static readonly JsonObject Query = Schema.String("Only books whose title or author contains this text.");
        private static readonly JsonObject Limit = Schema.Integer($"The most books to return (default {DefaultLimit}).");
        private static readonly JsonObject Offset = Schema.Integer("How many to skip, for the next page (default 0).");

        private List<AssistantTool> Build() => new()
        {
            // ---- Kobo: the Back up books screen ----
            new AssistantTool
            {
                Name = "list_books",
                Title = Strings.Asst_Tool_ListBooks,
                Description = "List the books on the Kobo tab's Back up books screen: the person's Kobo store books, the books on the connected Kobo eReader, and book files only in the Kobo folder on this computer. Each has an id, title, authors, where it is from, its format, and whether a backup copy is saved in the Kobo folder.",
                Schema = Schema.Object(
                    ("source", Schema.String("Which books (default all).", "all", "store", "ereader", "computer"), false),
                    ("query", Query, false),
                    ("not_backed_up_only", Schema.Boolean("Only books with no backup copy yet that can be backed up."), false),
                    ("limit", Limit, false),
                    ("offset", Offset, false)),
                Run = (args, _) => Task.FromResult(ListBooks(args)),
            },
            new AssistantTool
            {
                Name = "refresh_library",
                Title = Strings.Asst_Tool_Refresh,
                Description = "Read the Kobo store library again (when signed in) and look at the connected eReader again, as the Refresh button does. Use when the person says books are missing or they have just plugged in their eReader.",
                Schema = Schema.Object(),
                Run = async (_, _) =>
                {
                    await _main.RefreshForAssistantAsync();
                    var error = _main.Error;
                    return error != null
                        ? new ToolOutcome(error, true)
                        : new ToolOutcome(Counts().ToJsonString(), Summary: _main.Summary);
                },
            },
            new AssistantTool
            {
                Name = "back_up_books",
                Title = Strings.Asst_Tool_BackUp,
                Description = "Save backup copies of Kobo books into the Kobo folder: store books are downloaded, eReader books are copied. Give ids from list_books, or all_not_backed_up to back up every book not yet saved, as the Back up all button does. Waits until the copies are done and reports each one.",
                Schema = Schema.Object(
                    ("ids", Ids, false),
                    ("all_not_backed_up", Schema.Boolean("Back up every book that has no copy yet."), false)),
                Changes = true,
                Check = BackUpCheck,
                Describe = args =>
                {
                    var rows = BackUpRows(args).Found;
                    return string.Format(Strings.Asst_Desc_BackUp, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = (args, _) => BackUpAsync(args),
            },
            new AssistantTool
            {
                Name = "delete_backups",
                Title = Strings.Asst_Tool_DeleteBackups,
                Description = "Move backup copies in the Kobo folder to the Recycle Bin: the saved copies of store or eReader books, or files only on this computer, by ids from list_books. Nothing is removed from the eReader or the Kobo account. The person can restore the files from the Recycle Bin.",
                Schema = Schema.Object(("ids", Ids, true)),
                AlwaysAsks = true,
                Check = DeleteBackupsCheck,
                Describe = args =>
                {
                    var rows = BookRows(args.Strings("ids")).Found;
                    return string.Format(Strings.Asst_Desc_DeleteBackups, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = (args, _) => DeleteBackupsAsync(args),
            },

            // ---- Kobo: Collections ----
            new AssistantTool
            {
                Name = "list_collections",
                Title = Strings.Asst_Tool_ListCollections,
                Description = "List the Kobo eReader's collections with how many books each holds, how many books are in none or in several, and the changes waiting to be saved. Reads the collections from the eReader the first time; when it isn't connected, they are as it was last time (offline).",
                Schema = Schema.Object(),
                Run = async (_, _) =>
                {
                    if (await LoadCollectionsAsync() is { } problem)
                    {
                        return problem;
                    }
                    return new ToolOutcome(CollectionsState().ToJsonString(), Summary: string.Format(Strings.Asst_CollectionsFound, Collections.Coll!.Current.Count));
                },
            },
            new AssistantTool
            {
                Name = "list_collection_books",
                Title = Strings.Asst_Tool_ListCollectionBooks,
                Description = "List the books on the Kobo eReader as the Collections screen sees them, with the collections each is in (pending changes included). Use these ids with the other collection tools.",
                Schema = Schema.Object(
                    ("filter", Schema.String("Which books (default all).", "all", "not_in_any", "several", "collection"), false),
                    ("collection", Schema.String("The collection, when filter is collection."), false),
                    ("query", Query, false),
                    ("limit", Limit, false),
                    ("offset", Offset, false)),
                Run = async (args, _) =>
                {
                    if (await LoadCollectionsAsync() is { } problem)
                    {
                        return problem;
                    }
                    return ListCollectionBooks(args);
                },
            },
            CollectionTool("create_collection", Strings.Asst_Tool_CreateCollection,
                "Create an empty collection (a pending change).",
                Schema.Object(("name", Schema.String("The new collection's name."), true)),
                (coll, args) =>
                {
                    coll.Create(args.Str("name") ?? "");
                    return $"Created \"{args.Str("name")}\".";
                }),
            CollectionTool("rename_collection", Strings.Asst_Tool_RenameCollection,
                "Rename a collection; its books go with it (a pending change).",
                Schema.Object(("name", Schema.String("The collection to rename."), true), ("new_name", Schema.String("Its new name."), true)),
                (coll, args) =>
                {
                    var name = CollectionName(coll, args.Str("name"));
                    coll.Rename(name, args.Str("new_name") ?? "");
                    return $"Renamed \"{name}\" to \"{args.Str("new_name")}\".";
                }),
            CollectionTool("delete_collection", Strings.Asst_Tool_DeleteCollection,
                "Delete a collection (a pending change). Its books stay on the eReader.",
                Schema.Object(("name", Schema.String("The collection to delete."), true)),
                (coll, args) =>
                {
                    var name = CollectionName(coll, args.Str("name"));
                    coll.Delete(name);
                    return $"Deleted \"{name}\"; its books stay on the eReader.";
                }),
            CollectionTool("add_to_collection", Strings.Asst_Tool_AddToCollection,
                "Put books into a collection (a pending change), by ids from list_collection_books. With move, they are also taken out of every other collection.",
                Schema.Object(
                    ("collection", Schema.String("The collection."), true),
                    ("ids", Ids, true),
                    ("move", Schema.Boolean("Take the books out of every other collection too (default false)."), false)),
                (coll, args) =>
                {
                    var name = CollectionName(coll, args.Str("collection"));
                    var (books, unknown) = CollectionBooks(args.Strings("ids"));
                    var ids = books.Select(b => b.Id).ToList();
                    if (args.Bool("move", false))
                    {
                        coll.MoveTo(name, ids);
                    }
                    else
                    {
                        coll.Add(name, ids);
                    }
                    return $"{(args.Bool("move", false) ? "Moved" : "Added")} {ids.Count} books to \"{name}\"." + Unknown(unknown);
                }),
            CollectionTool("remove_from_collection", Strings.Asst_Tool_RemoveFromCollection,
                "Take books out of a collection (a pending change), by ids from list_collection_books. The books stay on the eReader.",
                Schema.Object(("collection", Schema.String("The collection."), true), ("ids", Ids, true)),
                (coll, args) =>
                {
                    var name = CollectionName(coll, args.Str("collection"));
                    var (books, unknown) = CollectionBooks(args.Strings("ids"));
                    coll.Remove(name, books.Select(b => b.Id));
                    return $"Took {books.Count} books out of \"{name}\"." + Unknown(unknown);
                }),
            CollectionTool("undo_collection_changes", Strings.Asst_Tool_UndoCollections,
                "Throw away every collection change not yet saved, including ones the person made by hand, as Undo all does.",
                Schema.Object(),
                (coll, _) =>
                {
                    coll.Discard();
                    return "Every unsaved collection change was undone.";
                }),
            new AssistantTool
            {
                Name = "save_collections",
                Title = Strings.Asst_Tool_SaveCollections,
                Description = "Write the pending collection changes to the connected Kobo eReader, as Save to Kobo eReader does. The eReader's book list is backed up first. The person approves this every time. When the eReader isn't connected, saving waits until it is.",
                Schema = Schema.Object(),
                AlwaysAsks = true,
                Check = _ => SaveCollectionsCheck(),
                Describe = _ => string.Format(Strings.Asst_Desc_SaveCollections, Collections.Pending),
                Run = async (_, _) =>
                {
                    if (SaveCollectionsCheck() is { } refused)
                    {
                        return refused;
                    }
                    var count = Collections.Pending;
                    await Collections.SaveAsync();
                    return Collections.Error is { } error
                        ? new ToolOutcome(error, true)
                        : new ToolOutcome($"Saved {count} changes to the eReader.", Summary: Strings.Coll_Saved)
                        {
                            Changed = Strings.Asst_Chg_CollectionsSaved,
                            ChangeKey = CollectionsChangeKey,
                        };
                },
            },

            // ---- Kobo: Send to eReader ----
            new AssistantTool
            {
                Name = "list_send",
                Title = Strings.Asst_Tool_ListSend,
                Description = "List the book files in the Kobo folder as the Send to eReader screen sees them: whether each is ready to send, already on the eReader, a store book (the eReader gets those from the account), copy-protected, not a Kobo format, or unchecked because no eReader is connected, and why.",
                Schema = Schema.Object(("query", Query, false)),
                Run = async (args, _) =>
                {
                    await _main.ScanSendFolderAsync();
                    return ListSend(args);
                },
            },
            new AssistantTool
            {
                Name = "send_to_ereader",
                Title = Strings.Asst_Tool_Send,
                Description = "Copy book files from the Kobo folder to the connected Kobo eReader, by ids from list_send, or every file that is ready with all_ready. Only files that are ready (or copy-protected, which may not open) can be sent. Ids from search_ebooks don't work here: download the book with download_ebook first, then find it with list_send. The person approves this every time.",
                Schema = Schema.Object(
                    ("ids", Ids, false),
                    ("all_ready", Schema.Boolean("Send every file that is ready."), false)),
                AlwaysAsks = true,
                Check = SendCheck,
                Describe = args =>
                {
                    var rows = SendRows(args).Found;
                    return string.Format(Strings.Asst_Desc_Send, rows.Count, TitleList(rows.Select(r => r.FileName)));
                },
                Run = (args, _) => SendAsync(args),
            },
            new AssistantTool
            {
                Name = "eject_ereader",
                Title = Strings.Asst_Tool_Eject,
                Description = "Eject the Kobo eReader so it can be unplugged safely. Refused while books are being copied. Collection changes not yet saved are kept on this computer for the next time it is plugged in.",
                Schema = Schema.Object(),
                Changes = true,
                Describe = _ => Strings.Asst_Desc_Eject,
                Run = async (_, _) =>
                {
                    if (!_main.ReaderConnected)
                    {
                        return new ToolOutcome("No Kobo eReader is connected.", true);
                    }
                    if (!_main.CanEjectReader || _main.Send.Sending)
                    {
                        return new ToolOutcome("Books are being copied. Wait until they are done.", true);
                    }
                    var waiting = Collections.Pending;
                    await _main.EjectForAssistantAsync();
                    return _main.Reader.Ejected
                        ? new ToolOutcome("Ejected. The eReader can be unplugged."
                            + (waiting > 0 ? $" The {waiting} collection changes weren't saved to it: they are kept on this computer until it is plugged in again." : ""),
                            Summary: Strings.Asst_Ejected) { Changed = Strings.Asst_Chg_Ejected }
                        : new ToolOutcome(_main.Error ?? "Windows didn't eject the eReader; something may still be using it.", true);
                },
            },

            // ---- Kindle ----
            new AssistantTool
            {
                Name = "list_kindle_books",
                Title = Strings.Asst_Tool_ListKindle,
                Description = "List the books on the Kindle tab: found on a connected Kindle, in Kindle for PC, and in folders the person added, with whether each has a backup copy in the Kindle folder.",
                Schema = Schema.Object(
                    ("query", Query, false),
                    ("not_backed_up_only", Schema.Boolean("Only books with no backup copy yet."), false),
                    ("limit", Limit, false),
                    ("offset", Offset, false)),
                Run = async (args, _) =>
                {
                    if (!_main.Kindle.Scanned)
                    {
                        await _main.ScanKindleForAssistantAsync();
                    }
                    return ListKindle(args);
                },
            },
            new AssistantTool
            {
                Name = "back_up_kindle_books",
                Title = Strings.Asst_Tool_BackUpKindle,
                Description = "Copy Kindle books into the Kindle folder, by ids from list_kindle_books, or every book not yet saved with all_not_backed_up. Waits until the copies are done.",
                Schema = Schema.Object(
                    ("ids", Ids, false),
                    ("all_not_backed_up", Schema.Boolean("Back up every Kindle book that has no copy yet."), false)),
                Changes = true,
                Check = BackUpKindleCheck,
                Describe = args =>
                {
                    var rows = KindleRows(args).Found;
                    return string.Format(Strings.Asst_Desc_BackUpKindle, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = (args, _) => BackUpKindleAsync(args),
            },

            // ---- Kobo: Articles (#53) ----
            new AssistantTool
            {
                Name = "list_articles",
                Title = Strings.Asst_Tool_ListArticles,
                Description = "List the articles saved to the Kobo eReader with Instapaper, as the Articles screen shows them, with the copies kept on this computer. Each has an id, title, author, site, date added, how far it was read, whether it is still on the eReader and whether a copy is kept. With no eReader connected, only the kept copies are listed.",
                Schema = Schema.Object(
                    ("query", Schema.String("Only articles whose title, author or site contains this text."), false),
                    ("not_kept_only", Schema.Boolean("Only articles on the eReader with no copy kept yet."), false)),
                Run = async (args, _) =>
                {
                    await ArticlesScreen.LoadAsync();
                    return ListArticles(args);
                },
            },
            new AssistantTool
            {
                Name = "keep_articles",
                Title = Strings.Asst_Tool_KeepArticles,
                Description = "Keep copies of articles on this computer, by ids from list_articles: each is saved with its pictures as one web page in the Articles folder, and stays after the article has gone from the eReader. Needs the eReader plugged in.",
                Schema = Schema.Object(("ids", Ids, true)),
                Changes = true,
                Check = KeepArticlesCheck,
                Describe = args =>
                {
                    var rows = ArticleRows(args.Strings("ids")).Found;
                    return string.Format(Strings.Asst_Desc_KeepArticles, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = (args, _) => KeepArticlesAsync(args),
            },
            new AssistantTool
            {
                Name = "remove_articles",
                Title = Strings.Asst_Tool_RemoveArticles,
                Description = "Remove articles from the Kobo eReader, by ids from list_articles. The eReader's book list is backed up first. Instapaper isn't told, so they stay in the person's Instapaper account; to archive or delete them there too, the person uses Archive or Delete on the eReader. Keep a copy first (keep_articles) when the person wants one. The person approves this every time.",
                Schema = Schema.Object(("ids", Ids, true)),
                AlwaysAsks = true,
                Check = RemoveArticlesCheck,
                Describe = args =>
                {
                    var rows = ArticleRows(args.Strings("ids")).Found.Where(r => r.IsOnReader).ToList();
                    return string.Format(Strings.Asst_Desc_RemoveArticles, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = async (args, _) =>
                {
                    if (RemoveArticlesCheck(args) is { } refused)
                    {
                        return refused;
                    }
                    var (found, unknown) = ArticleRows(args.Strings("ids"));
                    var removed = await ArticlesScreen.RemoveAsync(found.Where(r => r.IsOnReader).ToList());
                    if (removed.Count == 0)
                    {
                        return new ToolOutcome(ArticlesScreen.Error ?? "Nothing was removed.", true);
                    }
                    var json = new JsonObject
                    {
                        ["removed"] = new JsonArray(removed.Select(r => (JsonNode)r.Title).ToArray()),
                        ["note"] = "They stay in the person's Instapaper account.",
                    };
                    if (unknown.Count > 0)
                    {
                        json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
                    }
                    return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_ArticlesRemoved, removed.Count))
                    {
                        Changed = string.Format(Strings.Asst_ArticlesRemoved, removed.Count),
                    };
                },
            },

            // ---- Kobo: Previews and the wishlist (#59) ----
            new AssistantTool
            {
                Name = "list_previews",
                Title = Strings.Asst_Tool_ListPreviews,
                Description = "List the Kobo store previews (free samples) on the Kobo eReader, as the Previews screen shows them, and the wishlist in the person's Kobo account. Each preview has an id, title, author, the date it arrived, its size, whether it is on the wishlist, and its kind: in_my_books (saved with Save Preview), from_search (opened from the store's search results) or not_saved (comes back after a sync, so it can't be removed); each wishlist book has an id, title, author, date added, price, and whether its removal is waiting for the eReader's next sync. Previews are listed only while the eReader is plugged in; the wishlist only while the person is signed in.",
                Schema = Schema.Object(("query", Schema.String("Only previews and wishlist books whose title or author contains this text."), false)),
                Run = async (args, _) =>
                {
                    await PreviewsScreen.LoadAsync();
                    return ListPreviews(args);
                },
            },
            new AssistantTool
            {
                Name = "add_to_wishlist",
                Title = Strings.Asst_Tool_AddToWishlist,
                Description = "Add previews to the wishlist in the person's Kobo account, by preview ids from list_previews. The change is left on the Kobo eReader, which sends it to the account when it next syncs, so the eReader must be plugged in. Its book list is backed up first.",
                Schema = Schema.Object(("ids", Ids, true)),
                Changes = true,
                Check = AddToWishlistCheck,
                Describe = args =>
                {
                    var rows = PreviewRows(args.Strings("ids")).Found.Where(r => r.CanWish).ToList();
                    return string.Format(Strings.Asst_Desc_AddToWishlist, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = async (args, _) =>
                {
                    if (AddToWishlistCheck(args) is { } refused)
                    {
                        return refused;
                    }
                    var (found, unknown) = PreviewRows(args.Strings("ids"));
                    var added = await PreviewsScreen.AddToWishlistAsync(found);
                    if (added.Count == 0)
                    {
                        return new ToolOutcome(PreviewsScreen.Error ?? "Nothing was added.", true);
                    }
                    return WishlistOutcome(added.Select(r => r.Title), unknown);
                },
            },
            new AssistantTool
            {
                Name = "remove_from_wishlist",
                Title = Strings.Asst_Tool_RemoveFromWishlist,
                Description = "Remove books from the wishlist in the person's Kobo account, by wishlist ids from list_previews. The change is left on the Kobo eReader, which sends it to the account when it next syncs, so the eReader must be plugged in. Its book list is backed up first. The person approves this every time.",
                Schema = Schema.Object(("ids", Ids, true)),
                AlwaysAsks = true,
                Check = RemoveFromWishlistCheck,
                Describe = args =>
                {
                    var rows = WishRows(args.Strings("ids")).Found.Where(r => r.CanTick).ToList();
                    return string.Format(Strings.Asst_Desc_RemoveFromWishlist, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = async (args, _) =>
                {
                    if (RemoveFromWishlistCheck(args) is { } refused)
                    {
                        return refused;
                    }
                    var (found, unknown) = WishRows(args.Strings("ids"));
                    var removed = await PreviewsScreen.RemoveFromWishlistAsync(found);
                    if (removed.Count == 0)
                    {
                        return new ToolOutcome(PreviewsScreen.WishError ?? "Nothing was removed.", true);
                    }
                    return WishlistOutcome(removed.Select(r => r.Title), unknown);
                },
            },
            new AssistantTool
            {
                Name = "remove_previews",
                Title = Strings.Asst_Tool_RemovePreviews,
                Description = "Remove previews from the Kobo eReader, by preview ids from list_previews. A preview in_my_books is taken out of the person's Kobo account at the eReader's next sync, as Remove from My Books does; one from_search is deleted from the eReader now. Previews that are not_saved come back after a sync, so they are left alone. The eReader must be plugged in, and its book list is backed up first. The person approves this every time.",
                Schema = Schema.Object(("ids", Ids, true)),
                AlwaysAsks = true,
                Check = RemovePreviewsCheck,
                Describe = args =>
                {
                    var rows = PreviewRows(args.Strings("ids")).Found.Where(r => r.Preview.CanRemove).ToList();
                    return string.Format(Strings.Asst_Desc_RemovePreviews, rows.Count, TitleList(rows.Select(r => r.Title)));
                },
                Run = async (args, _) =>
                {
                    if (RemovePreviewsCheck(args) is { } refused)
                    {
                        return refused;
                    }
                    var (found, unknown) = PreviewRows(args.Strings("ids"));
                    var left = found.Where(r => !r.Preview.CanRemove).Select(r => r.Title).ToList();
                    var removed = await PreviewsScreen.RemoveAsync(found);
                    if (removed.Count == 0)
                    {
                        return new ToolOutcome(PreviewsScreen.Error ?? "Nothing was removed.", true);
                    }
                    var json = new JsonObject
                    {
                        ["removed"] = new JsonArray(removed.Select(r => (JsonNode)new JsonObject
                        {
                            ["title"] = r.Title,
                            ["when"] = r.Preview.Kind == PreviewKind.Saved ? "at the eReader's next sync" : "now",
                        }).ToArray()),
                    };
                    if (left.Count > 0)
                    {
                        json["left_alone"] = new JsonArray(left.Select(t => (JsonNode)t).ToArray());
                        json["note"] = "Previews that are not_saved, or already being removed, were left alone.";
                    }
                    if (unknown.Count > 0)
                    {
                        json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
                    }
                    return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_PreviewsRemoved, removed.Count))
                    {
                        Changed = string.Format(Strings.Asst_PreviewsRemoved, removed.Count),
                    };
                },
            },

            // ---- Moving about ----
            new AssistantTool
            {
                Name = "show_screen",
                Title = Strings.Asst_Tool_ShowScreen,
                Description = "Show one of HoboLibris's screens, so the person sees what changed.",
                Schema = Schema.Object(("screen", Schema.String("The screen.", "back_up", "collections", "send", "articles", "previews", "kindle"), true)),
                Run = (args, _) =>
                {
                    switch (args.Str("screen"))
                    {
                        case "kindle":
                            _main.Tab = MainTab.Kindle;
                            break;
                        case "collections":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Collections;
                            break;
                        case "send":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Send;
                            break;
                        case "articles":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Articles;
                            break;
                        case "previews":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Previews;
                            break;
                        case "back_up":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Backup;
                            break;
                        default:
                            return Task.FromResult(new ToolOutcome("Unknown screen; use back_up, collections, send, articles, previews or kindle.", true));
                    }
                    return Task.FromResult(new ToolOutcome("Shown.", Summary: ScreenName()));
                },
            },
        };

        // ---- Where the person is --------------------------------------------------------------

        public string ScreenName() => _main.Tab == MainTab.Kindle
            ? Strings.Main_Kindle
            : Strings.Main_Kobo + " · " + _main.KoboMode switch
            {
                KoboMode.Collections => Strings.Kobo_CollectionsTab,
                KoboMode.Send => Strings.Kobo_SendTab,
                KoboMode.Articles => Strings.Kobo_ArticlesTab,
                KoboMode.Previews => Strings.Kobo_PreviewsTab,
                _ => Strings.Kobo_BackUpTab,
            };

        /// <summary>The state for the system prompt.  No paths, email address or sign-in details.</summary>
        public JsonObject Context()
        {
            var screen = _main.Tab == MainTab.Kindle ? "kindle" : _main.KoboMode switch
            {
                KoboMode.Collections => "collections",
                KoboMode.Send => "send",
                KoboMode.Articles => "articles",
                KoboMode.Previews => "previews",
                _ => "back_up",
            };
            return new JsonObject
            {
                ["program_language"] = LanguageManager.GetCurrentLanguage(),
                ["screen"] = screen,
                ["signed_in_to_kobo"] = _main.SignedIn,
                ["kobo_ereader_connected"] = _main.ReaderConnected,
                ["kindle_connected"] = _main.Kindle.Scanned ? _main.Kindle.IsConnected : null,
                ["books"] = Counts(),
                ["collections_loaded"] = Collections.Coll != null,
                ["collections_offline"] = Collections.IsOffline,
                ["collection_changes_waiting"] = Collections.Pending,
                ["send_new_books_automatically"] = _main.AutoSend,
                ["ebook_sources_on"] = _sources.EnabledSources(),
                ["busy"] = new JsonObject
                {
                    ["backing_up"] = _main.KoboBusy || _main.Kindle.Copying,
                    ["sending"] = _main.Send.Sending,
                },
            };
        }

        private JsonObject Counts() => new()
        {
            ["store"] = _main.StoreBooks.Count,
            ["ereader"] = _main.Reader.Rows.Count,
            ["only_on_computer"] = _main.Send.FolderOnly().Count(),
            ["not_backed_up"] = _main.NotYetSavedCount,
            ["ready_to_send"] = _main.ReadyToSend,
            ["kindle"] = _main.Kindle.Scanned ? _main.Kindle.Rows.Count : null,
        };

        // ---- Kobo books -------------------------------------------------------------------------

        private static string KindOf(BookRow row) => row.Source switch
        {
            RowSource.Store => BookIds.Store,
            RowSource.Reader => BookIds.Reader,
            RowSource.Folder => BookIds.Folder,
            _ => BookIds.Kindle,
        };

        private static string ShortId(BookRow row) => BookIds.For(KindOf(row), row.Id);

        private (List<BookRow> Found, List<string> Unknown) BookRows(IEnumerable<string> ids) =>
            BookIds.Resolve(ids, _main.AllKoboRows(), ShortId);

        private ToolOutcome ListBooks(JsonObject args)
        {
            var source = args.Str("source") ?? "all";
            var query = args.Str("query") ?? "";
            var rows = _main.AllKoboRows()
                .Where(r => source switch
                {
                    "store" => r.Source == RowSource.Store,
                    "ereader" => r.Source == RowSource.Reader,
                    "computer" => r.Source == RowSource.Folder,
                    _ => true,
                })
                .Where(r => r.Matches(query))
                .Where(r => !args.Bool("not_backed_up_only", false) || r.NeedsBackup)
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var page = Page(rows, args);
            var books = new JsonArray(page.Select(r => (JsonNode)BookJson(r)).ToArray());
            return new ToolOutcome(new JsonObject { ["total"] = rows.Count, ["books"] = books }.ToJsonString(),
                Summary: string.Format(Strings.Asst_BooksFound, rows.Count));
        }

        private static JsonObject BookJson(BookRow r)
        {
            var json = new JsonObject
            {
                ["id"] = ShortId(r),
                ["title"] = r.Title,
                ["authors"] = r.Authors,
                ["from"] = r.Source switch
                {
                    RowSource.Store => "store",
                    RowSource.Reader => "ereader",
                    RowSource.Folder => "computer",
                    _ => r.DeviceBook?.Origin ?? "kindle",
                },
                ["format"] = r.Format,
                ["backed_up"] = r.IsSaved || r.Source == RowSource.Folder,
                ["can_back_up"] = r.NeedsBackup,
            };
            if (r.StoreBook?.IsProtected == true)
            {
                json["copy_protected"] = true;
            }
            if (r.NotDownloadable)
            {
                json["note"] = "The store offers no file for this book.";
            }
            else if (r.Duplicate != null)
            {
                json["note"] = r.Source == RowSource.Reader ? "Same as a store book, which is backed up instead." : "Saved from the store.";
            }
            if (r.IsFailed)
            {
                json["last_error"] = r.State.Error;
            }
            return json;
        }

        private (List<BookRow> Found, List<string> Unknown) BackUpRows(JsonObject args)
        {
            if (args.Bool("all_not_backed_up", false))
            {
                return (_main.AllKoboRows().Where(r => r.NeedsBackup).ToList(), new List<string>());
            }
            var (found, unknown) = BookRows(args.Strings("ids"));
            return (found.Where(r => r.Source is RowSource.Store or RowSource.Reader).ToList(), unknown);
        }

        /// <summary>Why a backup can't run, before the person is asked; null when it can.</summary>
        private ToolOutcome? BackUpCheck(JsonObject args)
        {
            if (_main.KoboBusy)
            {
                return new ToolOutcome("Books are being backed up already. Wait until they are done.", true);
            }
            var (rows, unknown) = BackUpRows(args);
            return rows.Count == 0 ? new ToolOutcome("No books to back up." + Unknown(unknown), unknown.Count > 0) : null;
        }

        private async Task<ToolOutcome> BackUpAsync(JsonObject args)
        {
            if (BackUpCheck(args) is { } refused)
            {
                return refused;
            }
            var (rows, unknown) = BackUpRows(args);
            await _main.BackUpForAssistantAsync(
                rows.Where(r => r.Source == RowSource.Store).Select(r => r.Id).ToList(),
                rows.Where(r => r.Source == RowSource.Reader).Select(r => r.Id).ToList());
            return Report(rows, r => r.IsSaved, unknown, Strings.Asst_Chg_BackedUp);
        }

        /// <summary>The books asked for that have a copy on this computer to delete.</summary>
        private (List<BookRow> Found, List<string> Unknown) BackupCopies(JsonObject args)
        {
            var (rows, unknown) = BookRows(args.Strings("ids"));
            return (rows.Where(r => r.IsSaved || r.Source == RowSource.Folder).ToList(), unknown);
        }

        private ToolOutcome? DeleteBackupsCheck(JsonObject args)
        {
            var (rows, unknown) = BackupCopies(args);
            return rows.Count == 0 ? new ToolOutcome("None of these books has a backup copy." + Unknown(unknown), true) : null;
        }

        private async Task<ToolOutcome> DeleteBackupsAsync(JsonObject args)
        {
            if (DeleteBackupsCheck(args) is { } refused)
            {
                return refused;
            }
            var (rows, unknown) = BackupCopies(args);
            await _main.DeleteBackupsForAssistantAsync(
                rows.Where(r => r.Source == RowSource.Store).Select(r => r.Id).ToList(),
                rows.Where(r => r.Source == RowSource.Reader).Select(r => r.Id).ToList(),
                rows.Where(r => r.Source == RowSource.Folder).Select(r => r.Id).ToList());
            var error = _main.Error;
            return new ToolOutcome((error ?? _main.Status) + Unknown(unknown), error != null, error == null ? _main.Status : null)
            {
                Changed = error == null ? string.Format(Strings.Asst_Chg_Deleted, rows.Count) : null,
            };
        }

        /// <summary>Which books worked and which failed, with why.</summary>
        private static ToolOutcome Report(List<BookRow> rows, Func<BookRow, bool> worked, List<string> unknown, string changed)
        {
            var done = rows.Where(worked).ToList();
            var failed = rows.Where(r => !worked(r)).ToList();
            var json = new JsonObject
            {
                ["done"] = new JsonArray(done.Select(r => (JsonNode)r.Title).ToArray()),
                ["failed"] = new JsonArray(failed.Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["error"] = r.State.Error ?? "Not copied." }).ToArray()),
            };
            if (unknown.Count > 0)
            {
                json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
            }
            return new ToolOutcome(json.ToJsonString(), done.Count == 0,
                failed.Count == 0 ? string.Format(Strings.Asst_BackedUp, done.Count) : string.Format(Strings.Asst_BackedUpWithFailures, done.Count, failed.Count))
            {
                Changed = done.Count > 0 ? string.Format(changed, done.Count) : null,
            };
        }

        // ---- Collections ----------------------------------------------------------------------

        /// <summary>Reads the collections from the eReader if they aren't loaded; an outcome when they can't be.</summary>
        private async Task<ToolOutcome?> LoadCollectionsAsync()
        {
            if (Collections.Coll == null && !Collections.HasConflicts)
            {
                await Collections.LoadAsync();
            }
            if (Collections.HasConflicts)
            {
                return new ToolOutcome("The changes made here and the changes made on the eReader clash. The person chooses what to keep on the Collections screen first.", true);
            }
            return Collections.Coll == null
                ? new ToolOutcome(Collections.Error ?? "The collections couldn't be read.", true)
                : null;
        }

        private JsonObject CollectionsState()
        {
            var coll = Collections.Coll!;
            var changes = coll.Changes();
            return new JsonObject
            {
                ["offline"] = coll.Offline,
                ["as_of"] = coll.Offline ? coll.Taken.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null,
                ["collections"] = new JsonArray(coll.Current.Select(c => (JsonNode)new JsonObject { ["name"] = c.Key, ["books"] = c.Value.Count }).ToArray()),
                ["books_on_ereader"] = coll.Books.Count,
                ["books_in_no_collection"] = coll.Books.Count(b => !coll.IsShelved(b.Id)),
                ["books_in_several"] = coll.Books.Count(b => coll.ShelfCount(b.Id) > 1),
                ["changes_waiting"] = new JsonObject
                {
                    ["created"] = new JsonArray(changes.Created.Select(n => (JsonNode)n).ToArray()),
                    ["deleted"] = new JsonArray(changes.Deleted.Select(n => (JsonNode)n).ToArray()),
                    ["books_added"] = changes.Added.Count,
                    ["books_removed"] = changes.Removed.Count,
                },
            };
        }

        private ToolOutcome ListCollectionBooks(JsonObject args)
        {
            var coll = Collections.Coll!;
            var filter = args.Str("filter") ?? "all";
            string? shelf = null;
            if (filter == "collection")
            {
                shelf = CollectionName(coll, args.Str("collection"));
            }
            var query = args.Str("query") ?? "";
            var books = coll.Books
                .Where(b => filter switch
                {
                    "not_in_any" => !coll.IsShelved(b.Id),
                    "several" => coll.ShelfCount(b.Id) > 1,
                    "collection" => coll.Current[shelf!].Contains(b.Id),
                    _ => true,
                })
                .Where(b => query.Length == 0
                            || b.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || b.Authors.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var page = Page(books, args).Select(b => (JsonNode)new JsonObject
            {
                ["id"] = BookIds.For(BookIds.Collection, b.Id),
                ["title"] = b.Title,
                ["authors"] = b.Authors,
                ["collections"] = new JsonArray(coll.ShelvesOf(b.Id).Select(s => (JsonNode)s).ToArray()),
            }).ToArray();
            return new ToolOutcome(new JsonObject { ["total"] = books.Count, ["books"] = new JsonArray(page) }.ToJsonString(),
                Summary: string.Format(Strings.Asst_BooksFound, books.Count));
        }

        private (List<CollectionBook> Found, List<string> Unknown) CollectionBooks(IEnumerable<string> ids) =>
            BookIds.Resolve(ids, Collections.Coll!.Books, b => BookIds.For(BookIds.Collection, b.Id));

        /// <summary>The collection's own name, matched without regard to case; an error naming the ones there are.</summary>
        private ToolOutcome? SaveCollectionsCheck()
        {
            if (Collections.Coll == null)
            {
                return new ToolOutcome("The collections aren't loaded: call list_collections first.", true);
            }
            if (Collections.Pending == 0)
            {
                return new ToolOutcome("There are no changes to save.");
            }
            return Collections.IsOffline
                ? new ToolOutcome($"The Kobo eReader isn't connected, so saving waits: the {Collections.Pending} changes are kept on this computer and merged with the eReader when it is plugged in.")
                : null;
        }

        private static string CollectionName(Services.Collections coll, string? name)
        {
            var match = coll.Current.Keys.FirstOrDefault(k => string.Equals(k, name?.Trim(), StringComparison.CurrentCultureIgnoreCase));
            return match ?? throw new ArgumentException($"There is no collection \"{name}\". The collections are: {string.Join(", ", coll.Current.Keys.Select(k => $"\"{k}\""))}.");
        }

        /// <summary>A tool that changes the pending collections, then redraws the Collections screen.</summary>
        private AssistantTool CollectionTool(string name, string title, string description, JsonObject schema, Func<Services.Collections, JsonObject, string> change) => new()
        {
            Name = name,
            Title = title,
            Description = description + " Nothing reaches the eReader until save_collections.",
            Schema = schema,
            Run = async (args, _) =>
            {
                if (await LoadCollectionsAsync() is { } problem)
                {
                    return problem;
                }
                var text = change(Collections.Coll!, args);
                Collections.Done = null;
                Collections.Error = null;
                Collections.Rebuild();
                return new ToolOutcome(text + $" Changes waiting to be saved: {Collections.Pending}.",
                    Summary: string.Format(Strings.Asst_ChangesWaiting, Collections.Pending))
                {
                    // Listed once per request, with the count after the last change.
                    Changed = name == "undo_collection_changes"
                        ? Strings.Asst_Chg_CollectionsUndone
                        : string.Format(Strings.Asst_Chg_CollectionsWaiting, Collections.Pending),
                    ChangeKey = CollectionsChangeKey,
                };
            },
        };

        /// <summary>The collection tools' and save_collections' changes replace each other in a request's summary.</summary>
        private const string CollectionsChangeKey = "collections";

        // ---- Articles (#53) ----------------------------------------------------------------------

        private (List<ArticleRow> Found, List<string> Unknown) ArticleRows(IEnumerable<string> ids) =>
            BookIds.Resolve(ids, ArticlesScreen.Rows, r => BookIds.For(BookIds.Article, r.Id));

        private ToolOutcome ListArticles(JsonObject args)
        {
            var query = args.Str("query") ?? "";
            var notKept = args.Bool("not_kept_only", false);
            var rows = ArticlesScreen.Rows
                .Where(r => !notKept || (r.IsOnReader && !r.IsKept))
                .Where(r => query.Length == 0
                            || r.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.Site.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            var json = new JsonObject
            {
                ["ereader_connected"] = ArticlesScreen.ReaderFound,
                ["total"] = rows.Count,
                ["articles"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = BookIds.For(BookIds.Article, r.Id),
                    ["title"] = r.Title,
                    ["author"] = r.Author,
                    ["site"] = r.Site,
                    ["added"] = r.Added?.ToString("yyyy-MM-dd"),
                    ["read"] = r.OnReader == null ? null : r.OnReader.Finished ? "finished" : $"{r.OnReader.Percent}%",
                    ["on_ereader"] = r.IsOnReader,
                    ["kept"] = r.IsKept,
                }).ToArray()),
            };
            if (!ArticlesScreen.ReaderFound)
            {
                json["note"] = ArticlesScreen.Error ?? "No Kobo eReader is connected: only the copies kept on this computer are listed.";
            }
            return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_ArticlesFound, rows.Count));
        }

        private ToolOutcome? KeepArticlesCheck(JsonObject args)
        {
            if (!_main.ReaderConnected)
            {
                return new ToolOutcome("No Kobo eReader is connected: articles can be kept only while it is plugged in.", true);
            }
            var (found, unknown) = ArticleRows(args.Strings("ids"));
            if (!found.Any(r => r.IsOnReader))
            {
                return new ToolOutcome("None of those is an article on the eReader. Take ids from list_articles." + Unknown(unknown), true);
            }
            return null;
        }

        private ToolOutcome? RemoveArticlesCheck(JsonObject args) => KeepArticlesCheck(args);

        private async Task<ToolOutcome> KeepArticlesAsync(JsonObject args)
        {
            if (KeepArticlesCheck(args) is { } refused)
            {
                return refused;
            }
            var (found, unknown) = ArticleRows(args.Strings("ids"));
            var (kept, failed) = await ArticlesScreen.KeepAsync(found.Where(r => r.IsOnReader).ToList());
            var json = new JsonObject
            {
                ["kept"] = new JsonArray(kept.Select(r => (JsonNode)r.Title).ToArray()),
                ["failed"] = new JsonArray(failed.Select(r => (JsonNode)new JsonObject { ["title"] = r.Title, ["why"] = r.Error }).ToArray()),
            };
            if (unknown.Count > 0)
            {
                json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
            }
            return new ToolOutcome(json.ToJsonString(), kept.Count == 0, string.Format(Strings.Asst_ArticlesKept, kept.Count))
            {
                Changed = kept.Count > 0 ? string.Format(Strings.Asst_ArticlesKept, kept.Count) : null,
            };
        }

        // ---- Previews and the wishlist (#59) -------------------------------------------------------

        private (List<PreviewRow> Found, List<string> Unknown) PreviewRows(IEnumerable<string> ids) =>
            BookIds.Resolve(ids, PreviewsScreen.Rows, r => BookIds.For(BookIds.Preview, r.Id));

        private ToolOutcome ListPreviews(JsonObject args)
        {
            var query = args.Str("query") ?? "";
            bool Matches(string title, string author) => query.Length == 0
                || title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || author.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var rows = PreviewsScreen.Rows.Where(r => Matches(r.Title, r.Author)).ToList();
            var wished = PreviewsScreen.WishRows.Where(r => Matches(r.Title, r.Author)).ToList();
            var json = new JsonObject
            {
                ["ereader_connected"] = PreviewsScreen.ReaderFound,
                ["previews"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = BookIds.For(BookIds.Preview, r.Id),
                    ["title"] = r.Title,
                    ["author"] = r.Author,
                    ["arrived"] = r.Preview.Arrived?.ToString("yyyy-MM-dd"),
                    ["size"] = r.SizeText,
                    ["on_wishlist"] = r.OnWishlist,
                    ["wishlist_add_waiting_for_sync"] = r.Adding,
                    ["kind"] = r.Preview.Kind switch
                    {
                        PreviewKind.Saved => "in_my_books",
                        PreviewKind.SearchResult => "from_search",
                        _ => "not_saved",
                    },
                    ["removal_waiting_for_sync"] = r.Preview.Removing,
                }).ToArray()),
                ["signed_in"] = !PreviewsScreen.WishSignedOut,
                ["wishlist"] = new JsonArray(wished.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = BookIds.For(BookIds.Wish, r.Item.CrossRevisionId),
                    ["title"] = r.Title,
                    ["author"] = r.Author,
                    ["added"] = r.Item.Added?.ToString("yyyy-MM-dd"),
                    ["price"] = r.Item.PriceText,
                    ["pre_order"] = r.Item.PreOrder,
                    ["removal_waiting_for_sync"] = r.Removing,
                }).ToArray()),
            };
            var notes = new List<string>();
            if (!PreviewsScreen.ReaderFound)
            {
                notes.Add(PreviewsScreen.Error ?? "No Kobo eReader is connected, so no previews are listed.");
            }
            if (PreviewsScreen.WishSignedOut)
            {
                notes.Add("The person isn't signed in to their Kobo account, so the wishlist can't be read.");
            }
            else if (PreviewsScreen.WishError is { } wishError)
            {
                notes.Add(wishError);
            }
            if (notes.Count > 0)
            {
                json["note"] = string.Join(" ", notes);
            }
            return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_PreviewsFound, rows.Count, wished.Count));
        }

        private (List<WishRow> Found, List<string> Unknown) WishRows(IEnumerable<string> ids) =>
            BookIds.Resolve(ids, PreviewsScreen.WishRows, r => BookIds.For(BookIds.Wish, r.Item.CrossRevisionId));

        private ToolOutcome? AddToWishlistCheck(JsonObject args)
        {
            if (!_main.ReaderConnected)
            {
                return new ToolOutcome("No Kobo eReader is connected: the wishlist is changed through the eReader, so it must be plugged in.", true);
            }
            var (found, unknown) = PreviewRows(args.Strings("ids"));
            if (!found.Any(r => r.CanWish))
            {
                return new ToolOutcome("None of those is a preview that can be added: take preview ids from list_previews, leaving out those already on the wishlist." + Unknown(unknown), true);
            }
            return null;
        }

        private ToolOutcome? RemoveFromWishlistCheck(JsonObject args)
        {
            if (!_main.ReaderConnected)
            {
                return new ToolOutcome("No Kobo eReader is connected: the wishlist is changed through the eReader, so it must be plugged in.", true);
            }
            var (found, unknown) = WishRows(args.Strings("ids"));
            if (!found.Any(r => r.CanTick))
            {
                return new ToolOutcome("None of those is a wishlist book that can be removed: take wishlist ids from list_previews." + Unknown(unknown), true);
            }
            return null;
        }

        private ToolOutcome? RemovePreviewsCheck(JsonObject args)
        {
            if (!_main.ReaderConnected)
            {
                return new ToolOutcome("No Kobo eReader is connected: previews are removed from the eReader, so it must be plugged in.", true);
            }
            var (found, unknown) = PreviewRows(args.Strings("ids"));
            if (!found.Any(r => r.Preview.CanRemove))
            {
                return new ToolOutcome("None of those is a preview that can be removed: take preview ids from list_previews whose kind is in_my_books or from_search, leaving out those already being removed." + Unknown(unknown), true);
            }
            return null;
        }

        private static ToolOutcome WishlistOutcome(IEnumerable<string> titles, List<string> unknown)
        {
            var list = titles.ToList();
            var json = new JsonObject
            {
                ["changed"] = new JsonArray(list.Select(t => (JsonNode)t).ToArray()),
                ["note"] = "The change is on the eReader and reaches the Kobo account when the eReader next syncs.",
            };
            if (unknown.Count > 0)
            {
                json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
            }
            return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_WishlistChanged, list.Count))
            {
                Changed = string.Format(Strings.Asst_WishlistChanged, list.Count),
            };
        }

        // ---- Send to eReader --------------------------------------------------------------------

        private (List<SendRow> Found, List<string> Unknown) SendRows(JsonObject args)
        {
            if (args.Bool("all_ready", false))
            {
                return (_main.Send.Rows.Where(r => r.Kind == VerdictKind.Ready && !r.Sent).ToList(), new List<string>());
            }
            return BookIds.Resolve(args.Strings("ids"), _main.Send.Rows, r => BookIds.For(BookIds.Send, r.Id));
        }

        private ToolOutcome ListSend(JsonObject args)
        {
            var query = args.Str("query") ?? "";
            var rows = _main.Send.Rows
                .Where(r => query.Length == 0
                            || r.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.Item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            || r.Item.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            var files = rows.Select(r =>
            {
                var json = new JsonObject
                {
                    ["id"] = BookIds.For(BookIds.Send, r.Id),
                    ["file"] = r.FileName,
                    ["title"] = r.Item.Title,
                    ["author"] = r.Item.Author,
                    ["format"] = r.Format,
                    ["status"] = r.Kind switch
                    {
                        VerdictKind.Ready => "ready",
                        VerdictKind.OnReader => "on_ereader",
                        VerdictKind.StoreBook => "store_book",
                        VerdictKind.Protected => "copy_protected",
                        VerdictKind.NotKobo => "not_kobo_format",
                        _ => "unchecked",
                    },
                    ["why"] = r.Verdict.Why,
                };
                if (r.Sent)
                {
                    json["sent_now"] = true;
                }
                if (r.IsFailed)
                {
                    json["last_error"] = r.Error;
                }
                return (JsonNode)json;
            }).ToArray();
            return new ToolOutcome(new JsonObject
            {
                ["ereader_connected"] = _main.ReaderConnected,
                ["ready"] = _main.ReadyToSend,
                ["files"] = new JsonArray(files),
            }.ToJsonString(), Summary: string.Format(Strings.Asst_FilesReady, _main.ReadyToSend));
        }

        private ToolOutcome? SendCheck(JsonObject args)
        {
            if (!_main.ReaderConnected)
            {
                return new ToolOutcome("No Kobo eReader is connected.", true);
            }
            if (_main.Send.Sending)
            {
                return new ToolOutcome("Books are being sent already. Wait until they are done.", true);
            }
            var (rows, unknown) = SendRows(args);
            var refused = rows.Where(r => !r.Verdict.CanSend).ToList();
            if (rows.Count > refused.Count)
            {
                return null;
            }
            return new ToolOutcome("Nothing to send." + Unknown(unknown)
                + (refused.Count > 0 ? " Not sendable: " + string.Join("; ", refused.Select(r => $"{r.FileName} ({r.Verdict.Why})")) : ""), true);
        }

        private async Task<ToolOutcome> SendAsync(JsonObject args)
        {
            if (SendCheck(args) is { } refusal)
            {
                return refusal;
            }
            var (rows, unknown) = SendRows(args);
            var refused = rows.Where(r => !r.Verdict.CanSend).ToList();
            rows = rows.Where(r => r.Verdict.CanSend).ToList();
            await _main.StartSendAsync(rows.Select(r => r.Id).ToList());
            var sent = rows.Where(r => r.Sent && !r.IsFailed).ToList();
            var failed = rows.Where(r => r.IsFailed).ToList();
            var json = new JsonObject
            {
                ["sent"] = new JsonArray(sent.Select(r => (JsonNode)r.FileName).ToArray()),
                ["failed"] = new JsonArray(failed.Select(r => (JsonNode)new JsonObject { ["file"] = r.FileName, ["error"] = r.Error }).ToArray()),
            };
            if (refused.Count > 0)
            {
                json["not_sendable"] = new JsonArray(refused.Select(r => (JsonNode)new JsonObject { ["file"] = r.FileName, ["why"] = r.Verdict.Why }).ToArray());
            }
            if (unknown.Count > 0)
            {
                json["unknown_ids"] = new JsonArray(unknown.Select(u => (JsonNode)u).ToArray());
            }
            return new ToolOutcome(json.ToJsonString(), sent.Count == 0,
                failed.Count == 0 ? string.Format(Strings.Asst_Sent, sent.Count) : string.Format(Strings.Asst_SentWithFailures, sent.Count, failed.Count))
            {
                Changed = sent.Count > 0 ? string.Format(Strings.Asst_Chg_Sent, sent.Count) : null,
            };
        }

        // ---- Kindle -----------------------------------------------------------------------------

        private (List<BookRow> Found, List<string> Unknown) KindleRows(JsonObject args)
        {
            if (args.Bool("all_not_backed_up", false))
            {
                return (_main.Kindle.Rows.Where(r => r.NeedsBackup).ToList(), new List<string>());
            }
            return BookIds.Resolve(args.Strings("ids"), _main.Kindle.Rows, ShortId);
        }

        private ToolOutcome ListKindle(JsonObject args)
        {
            var query = args.Str("query") ?? "";
            var rows = _main.Kindle.Rows
                .Where(r => r.Matches(query))
                .Where(r => !args.Bool("not_backed_up_only", false) || r.NeedsBackup)
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var page = Page(rows, args).Select(r => (JsonNode)BookJson(r)).ToArray();
            var json = new JsonObject { ["kindle_connected"] = _main.Kindle.IsConnected, ["total"] = rows.Count, ["books"] = new JsonArray(page) };
            if (_main.Kindle.Note is { } note && rows.Count == 0)
            {
                json["note"] = note;
            }
            return new ToolOutcome(json.ToJsonString(), Summary: string.Format(Strings.Asst_BooksFound, rows.Count));
        }

        private ToolOutcome? BackUpKindleCheck(JsonObject args)
        {
            if (_main.Kindle.Copying)
            {
                return new ToolOutcome("Kindle books are being backed up already. Wait until they are done.", true);
            }
            var (rows, unknown) = KindleRows(args);
            return rows.Count == 0 ? new ToolOutcome("No Kindle books to back up." + Unknown(unknown), unknown.Count > 0) : null;
        }

        private async Task<ToolOutcome> BackUpKindleAsync(JsonObject args)
        {
            if (BackUpKindleCheck(args) is { } refused)
            {
                return refused;
            }
            var (rows, unknown) = KindleRows(args);
            await _main.Kindle.BackUpAsync(rows.Select(r => r.Id).ToList(), _main.KindleFolder);
            return Report(rows, r => r.IsSaved, unknown, Strings.Asst_Chg_KindleBackedUp);
        }

        // ---- Helpers --------------------------------------------------------------------------

        private static List<T> Page<T>(List<T> items, JsonObject args) =>
            items.Skip(Math.Max(0, args.Int("offset", 0))).Take(Math.Clamp(args.Int("limit", DefaultLimit), 1, 1000)).ToList();

        private static string Unknown(List<string> ids) =>
            ids.Count == 0 ? "" : $" These ids matched no book (list the books again): {string.Join(", ", ids)}."
                + (ids.Any(SourceTools.IsSearchId)
                    ? " Ids from search_ebooks are only for download_ebook and open_book_page; a downloaded book gets its own id in list_send."
                    : "");

        /// <summary>Up to ten titles for an approval card, then "and 4 more".</summary>
        private static string TitleList(IEnumerable<string> titles)
        {
            var all = titles.ToList();
            var shown = string.Join(", ", all.Take(10));
            return all.Count > 10 ? string.Format(Strings.Asst_AndMore, shown, all.Count - 10) : shown;
        }
    }
}
