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
                Description = "List the connected Kobo eReader's collections with how many books each holds, how many books are in none or in several, and the changes waiting to be saved. Reads the collections from the eReader the first time.",
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
                Description = "List the books on the connected Kobo eReader as the Collections screen sees them, with the collections each is in (pending changes included). Use these ids with the other collection tools.",
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
                Description = "Write the pending collection changes to the connected Kobo eReader, as Save to Kobo eReader does. The eReader's book list is backed up first. The person approves this every time.",
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
                        : new ToolOutcome($"Saved {count} changes to the eReader.", Summary: Strings.Coll_Saved);
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
                Description = "Eject the Kobo eReader so it can be unplugged safely. Refused while collection changes are waiting to be saved or books are being copied.",
                Schema = Schema.Object(),
                Changes = true,
                Describe = _ => Strings.Asst_Desc_Eject,
                Run = async (_, _) =>
                {
                    if (!_main.ReaderConnected)
                    {
                        return new ToolOutcome("No Kobo eReader is connected.", true);
                    }
                    if (Collections.Pending > 0)
                    {
                        return new ToolOutcome($"{Collections.Pending} collection changes aren't saved. Save or undo them first.", true);
                    }
                    if (!_main.CanEjectReader || _main.Send.Sending)
                    {
                        return new ToolOutcome("Books are being copied. Wait until they are done.", true);
                    }
                    await _main.EjectForAssistantAsync();
                    return _main.Reader.Ejected
                        ? new ToolOutcome("Ejected. The eReader can be unplugged.", Summary: Strings.Asst_Ejected)
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

            // ---- Moving about ----
            new AssistantTool
            {
                Name = "show_screen",
                Title = Strings.Asst_Tool_ShowScreen,
                Description = "Show one of HoboLibris's screens, so the person sees what changed.",
                Schema = Schema.Object(("screen", Schema.String("The screen.", "back_up", "collections", "send", "kindle"), true)),
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
                        case "back_up":
                            _main.Tab = MainTab.Kobo;
                            _main.KoboMode = KoboMode.Backup;
                            break;
                        default:
                            return Task.FromResult(new ToolOutcome("Unknown screen; use back_up, collections, send or kindle.", true));
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
                _ => Strings.Kobo_BackUpTab,
            };

        /// <summary>The state for the system prompt.  No paths, email address or sign-in details.</summary>
        public JsonObject Context()
        {
            var screen = _main.Tab == MainTab.Kindle ? "kindle" : _main.KoboMode switch
            {
                KoboMode.Collections => "collections",
                KoboMode.Send => "send",
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
            return Report(rows, r => r.IsSaved, unknown);
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
            return new ToolOutcome((error ?? _main.Status) + Unknown(unknown), error != null, error == null ? _main.Status : null);
        }

        /// <summary>Which books worked and which failed, with why.</summary>
        private static ToolOutcome Report(List<BookRow> rows, Func<BookRow, bool> worked, List<string> unknown)
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
                failed.Count == 0 ? string.Format(Strings.Asst_BackedUp, done.Count) : string.Format(Strings.Asst_BackedUpWithFailures, done.Count, failed.Count));
        }

        // ---- Collections ----------------------------------------------------------------------

        /// <summary>Reads the collections from the eReader if they aren't loaded; an outcome when they can't be.</summary>
        private async Task<ToolOutcome?> LoadCollectionsAsync()
        {
            if (Collections.Coll == null)
            {
                await Collections.LoadAsync();
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
            return Collections.Pending == 0 ? new ToolOutcome("There are no changes to save.") : null;
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
                    Summary: string.Format(Strings.Asst_ChangesWaiting, Collections.Pending));
            },
        };

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
                failed.Count == 0 ? string.Format(Strings.Asst_Sent, sent.Count) : string.Format(Strings.Asst_SentWithFailures, sent.Count, failed.Count));
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
            return Report(rows, r => r.IsSaved, unknown);
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
