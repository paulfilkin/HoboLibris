# How the original signs in, lists and downloads books

The rebuild is called HoboLibris (program `hobolibris.exe`, data in `%APPDATA%\HoboLibris`; data left under the earlier names `%APPDATA%\KoboLibris` or `%APPDATA%\BookBackup` is moved there once).

Read from Kobo Desktop Edition 4.38.23483 (Kobo.exe and nickel.dll) with the Deep engine.  Addresses are in nickel.dll unless stated.

## Identity sent with every device sign-in

| Value | What it is | Source |
|---|---|---|
| Store host | `https://storeapi.kobo.com` | string 0x107a2c78, used by 0x1013f2cb |
| Device sign-in path | `/v1/auth/device` | 0x100e257b |
| Token refresh path | `/v1/auth/refresh` | string 0x1079886c |
| Initialisation path | `/v1/initialization` | string 0x10797944 |
| Body fields | `DeviceId`, `AffiliateName`, `AppVersion`, `PlatformId`, `ClientKey`, and `UserKey` once signed in | request builder 0x100e205e |
| ClientKey | base64 of the UTF-8 PlatformId | 0x100e205e (toUtf8 then toBase64) |
| AppVersion | `4.38.23483` (format `%1.%2.%3`) | Kobo.exe version resource; formatter 0x1015f598 |
| AffiliateName | `Kobo` | estimated: the affiliate object supplies it (0x100e205e); `kobodesktop` is an affiliate key at 0x1079d76c |
| PlatformId | `00000000-0000-0000-0000-000000004000` | estimated: desktop ids `...0001` to `...0012` map to "desktop" at 0x1010e8d4, but the one used at run time was not found |
| Auth header | `Authorization: Bearer <AccessToken>` | strings 0x107987f0 and 0x1079afd8 |
| Token fields | `AccessToken`, `RefreshToken`, `TokenType`, `UserKey` | strings 0x107a0ac0, 0x10798850, 0x107a69e8, 0x10798898 |
| Refresh body | `RefreshToken`, `ClientKey` (plus AppVersion, PlatformId) | strings next to `/v1/auth/refresh`, 0x10798840 |

## Sign-in page

* `/v1/initialization` returns `Resources`, one of which is `sign_in_page` (string 0x107a28cc).
* The page takes the device parameters `wsa`, `pwsav`, `pwspid`, `pwsdid`, `pwsdm`, `pwspos`, `pwspov`, `pwspt`, `wscfv` (strings around 0x107a2ce0 and 0x107bffc4) and a `returnUrl`.
* When sign-in finishes the page navigates to `kobo://UserAuthenticated` (string 0x10790e50) with `userId` and `userKey` (0x107c0044, 0x107c004c).
* The original catches that navigation inside its own web view.  The rebuild does the same in its own sign-in window.
* Then a second `/v1/auth/device` call with `UserKey` gives the account's tokens.

## Library list

* The library is fetched page by page with header `x-kobo-synctoken` (0x1079b024, sent by 0x100f1112).
* The server answers with header `x-kobo-sync`; `continue` means another page follows (LibraryParser, 0x107a8a84 and 0x107a8c1c).
* Items are objects keyed `NewEntitlement`, `ChangedEntitlement`, `DeletedEntitlement`, with `BookEntitlement` and `BookMetadata` (audiobooks use `AudiobookEntitlement` / `AudiobookMetadata`).
* Library URL: the initialisation resource `library_sync`, which a live anonymous call on 2026-11-21 returned as `https://storeapi.kobo.com/v1/library/sync`; the same address is the fallback.
* The same call returned `sign_in_page` as `https://authorize.kobo.com/signin?returnUrl=https://kobo.com/`; the rebuild replaces that returnUrl with `kobo://UserAuthenticated`.
* The anonymous `/v1/auth/device` call with PlatformId `...4000` was accepted and returned `AccessToken`, `RefreshToken`, `TokenType`, `TrackingId`, `UserKey`.
* Removed items carry `IsRemoved` (0x1079f970).

## Download links

* `BookMetadata.DownloadUrls` is a list (0x107a8b90).  Each entry has `DrmType`, `Format`, `Url`, `Platform`, `Size` (strings 0x1079e5ec to 0x1079e66c).
* Formats seen: `EPUB3`, `EPUB3FL`, `EPUB3OMF`, `KEPUB`, `FLEPUB`, `EPUB`, sample variants and `MANIFEST` (0x1079e678 onwards; ContentUrl::epubType 0x1010d16e).
* The rebuild prefers an unprotected link, then EPUB3, EPUB, KEPUB, and skips samples and manifests.  It saves the file as delivered; it never removes copy protection.

## Wishlist (not from the desktop program)

* `GET https://storeapi.kobo.com/v1/user/wishlist`, with the account's Bearer token, returns `Items`, each with `DateAdded`, `CrossRevisionId` and `ProductMetadata.Book` (`Title`, `Contributors`, `ContributorRoles`, `ISBN`, `Slug`, `Price` with `Currency` and `Price`, `IsPreOrder`, `HasPreview`, `Id`), and `CurrentPageIndex`, `TotalPageCount` and `ItemsPerPage` (100).  Later pages are asked for with `?PageIndex=n`; HoboLibris stops if a reply isn't the page it asked for.
* The store offers no address for changing the wishlist, so changes go through the eReader (see Previews and the wishlist below).
* A book's store page is `https://www.kobo.com/ebook/<Slug>`, which redirects to the person's country store.

## Kindle tab (not from the Kobo program)

These rules are not read from the original; they are estimated from how Kindle stores books.

* A plugged-in Kindle is a drive with `documents` and `system` folders; books are searched under `documents`.
* Kindle for PC keeps books in `Documents\My Kindle Content`, one `<ASIN>_EBOK` or `_PDOC` folder per book; each folder is copied whole.
* Book file types: azw, azw3, azw4, kfx, mobi, prc, pdf.  A `<name>.sdr` folder beside a book is copied with it (KFX books need it).
* Title and author come from the MOBI header (EXTH 503 and 100) when present, else from the file name without its `_ASIN` ending.
* Files are copied unchanged into `<backup folder>\<where found>\`; nothing is decrypted or converted.
* Amazon removed account downloads ("Download & Transfer via USB") in February 2025, so there is no account list to fetch.
* Kindles that connect as media devices (no drive letter) are read through the Windows shell (`This PC`, then each storage, then `documents`), using Windows PowerShell scripts with `Shell.Application`.  Copies use the shell's `CopyHere` and wait until the file's size reaches the size the device reported, or stops changing for 2 seconds (15-minute limit).

* When no Kindle can be read, the tab checks the connection: a disk whose model contains "Kindle" but has no size means the Kindle is connected with a data cable but has not opened its storage ("No Media").  No Kindle disk and no Kindle under This PC means Windows can't see it (asleep, wrong port, or a charge-only cable).  It re-checks every 4 seconds until books appear.
* In "USB Drive Mode" the Kindle tells the person to eject it to keep reading; that is only for going back to reading.  The tab says so, and its Eject Kindle button uses the same shell Eject as File Explorer.
* Observed on this computer on 2026-11-21, after a restart into USB Drive Mode: drive D: with 171 book files outside `.sdr` folders (137 KFX, 15 AZW, 13 AZW3, 6 PDF); the scan listed all 171 in 0.5 s.
* Observed on this computer on 2026-11-21: the person's Kindle (USB vendor 1949, product 0004) was connected as drive D: "Kindle Internal Storage" with No Media.

## Kobo eReader tab (not from the desktop program)

Estimated from how Kobo eReaders store books; checked against the person's eReader on 2026-11-21.

* A connected Kobo eReader is a drive with a hidden `.kobo` folder (the person taps Connect on its screen).
* Books the person added are ordinary files anywhere outside hidden and system folders: epub (including `.kepub.epub`), pdf, mobi, cbz, cbr, txt, rtf.
* Titles and authors come from `.kobo\KoboReader.sqlite`, table `content`, rows with `ContentType = 6`: `Title` and `Attribution`.  Added books have `ContentID` = `file:///mnt/onboard/<path>`.  The database is opened read-only with `immutable=1`, so nothing on the eReader changes.
* Store books have other ContentIDs and a protected copy in `.kobo\kepub\<ContentID>`.  They are counted but not listed, since the Kobo store tab downloads them.
* Store previews (free samples) are content rows with `ContentType = 6` and `Accessibility = 6`.  They are not counted as store books, and the Collections screen leaves them out unless one is already in a collection (0.1.15, #58).
* Instapaper articles are content rows with `ContentType = 6`, MimeType `application/x-kobo-html+instapaper` and numeric ContentIDs.  The Collections screen leaves them out (0.1.12, #28): rows HoboLibris had put in `ShelfContent` for them were ignored by the eReader.
* The Kobo tab shows one list: store books (from the account), the books added to the eReader (when plugged in), and the books only in the Kobo folder ("This computer": not store copies and not on a connected eReader; title and author read from "Title - Author.ext", "_ " shown as ": "), with a "From" column.  Folder books can be deleted (Recycle Bin) like the others.  One folder holds both.  Store copies on the eReader are not listed, since they are the store books already in the list.
* An eReader book whose title matches any book in the store list (letters and digits compared, subtitle after `:` or `(` ignored) is shown as "Same as store book" and left out of "Back up all".
* Store downloads are named `Title - First author.ext` like the eReader copies; copies saved earlier as `Author - Title.ext` still count as saved.
* Copies go straight into the chosen folder, named after the book: `Title - First author.ext` (`.kepub.epub` kept); a second book with the same name gets its format, then a number, added.  Kindle copies also go straight into the chosen folder under their own file names, with their `.sdr` folders beside them.
* Observed: 146 added books (all with title and author from the database) and 1 store copy; the 1 saved store book matched none of the 146.
* The book list must not stay open, or Windows refuses to eject the eReader ("currently in use") because HoboLibris holds the file.  Microsoft.Data.Sqlite pools connections and keeps the file open after a pooled connection is closed, so every connection to the eReader's book list is opened with `Pooling=False`, and the pool is emptied before an eject (fixed in 0.1.7, #41).
* An ejected eReader can't be reconnected from the computer (#63).  On eject it leaves the USB bus: its hub port reads "no device connected" and Windows marks it not present.  A port cycle (`IOCTL_USB_HUB_CYCLE_PORT`) then fails with error 433, and the hubs tested can't turn off one port's power.  The eReader offers Connect again only after the cable is unplugged and plugged in.  Observed on 2026-11-25 with a Kobo (USB 2237:4237) on a Genesys USB 2.0 hub.

## Send to eReader (not from the desktop program)

* Book files in the Kobo folder (up to 3 folders deep) are compared with the eReader and the store list.  A file is "On the eReader" when a book with the same file name or title is on it, or the file is a copy made from it; "Kobo store book" when its title matches a store book or it is a store download.
* Kobo opens: epub (and kepub.epub), pdf, mobi, txt, rtf, cbz, cbr.  acsm, azw, azw3, azw4, kfx, prc, djvu, fb2 and lit are listed as "Won't open on a Kobo".
* Checks: an EPUB must be a readable zip with `META-INF/container.xml`; `META-INF/rights.xml` means Adobe protection.  A PDF must start with `%PDF-`.  A MOBI must say `BOOKMOBI`, and a non-zero encryption field means Kindle protection.
* With no eReader plugged in, books a Kobo can open are shown as "Plug in the eReader to check" and none counts as ready.
* "Ready to send" books are copied to `<eReader>\Books\<same file name>`, never overwriting; the eReader adds them to its library when ejected.  Protected books can be sent one at a time ("Send anyway").  Nothing is ever deleted.
* With "Send new books automatically" on, ready books are sent once each time the eReader is plugged in (never during screenshot runs).
* A book on the eReader counts as backed up when a file with its own file name is in the Kobo folder, so a book sent from the folder isn't copied back under a second name.
* Observed: with the person's folder and eReader, all files were recognised as on the eReader (0 ready).

## Delete selected (Kobo tab)

* Backup copies of the ticked books in the Kobo folder go to the Windows Recycle Bin.
* For ticked books the person added to the eReader, the question offers "Also remove them from the Kobo eReader" (off by default).  Yes deletes the files on the eReader (it has no Recycle Bin); the eReader takes them out of its library when ejected.  If the eReader is gone, the paths are kept in settings (`kobo_pending_removals`) and removed the next time it is plugged in.
* Store books are never removed from the eReader or the account; the question says to remove them on the eReader itself.

## Kobo eReader collections (not from the desktop program)

Estimated from Kobo's book-list layout as calibre's Kobo driver uses it; saving has been tried on the person's eReader.

* Collections are rows of `Shelf` (`Name`, `Id`, `InternalName`, `CreationDate`, `LastModified`, `Type`, `_IsDeleted`, `_IsVisible`, `_IsSynced`...); membership is `ShelfContent` (`ShelfName`, `ContentId`, `DateModified`, `_IsDeleted`, `_IsSynced`).  Rows with `_IsDeleted = 'true'` don't count.
* Books shown: everything the person added, downloaded store books, and any book already in a collection.
* Changes stay in the app until Save.  Save copies the book list to `%APPDATA%\HoboLibris\Kobo book list backups\` first, refuses if the eReader's book list changed since it was read, and writes in one transaction.
* New collection: revive a deleted one of the same name, else insert a `Shelf` row with only the columns the table has, a new UUID `Id`, `Name` = `InternalName`, `Type` = the most common existing type, `_IsSynced = 'false'`.
* Delete collection / remove book: mark `_IsDeleted = 'true'`, `_IsSynced = 'false'` (rows are kept, so Kobo's sync can pass the change on).  Add book: revive the row, else insert one.
* Rename: the books go to a collection with the new name and the old one is deleted (the same steps as above), since `ShelfContent` refers to collections by name.
* Move: the ticked books go into the chosen collection and out of every other one.  "Also add" keeps their other collections.
* Times are UTC `YYYY-MM-DDTHH:MM:SSZ`.  The eReader shows the changes after it is ejected.

### Backups of the book list

* Every change HoboLibris makes to the eReader's book list (collections, articles, the wishlist) copies it and its journal files (`-wal`, `-shm`, `-journal`) first, as `KoboReader-<UTC time>.sqlite`, into the backup folder (`BookListBackups.cs`).
* A second backup in the same second gets a number, `KoboReader-<time>-2.sqlite`, then `-3`, and no copy overwrites another (0.1.17, #64).

### Offline and the merge (0.1.11, #32)

* The collections are kept in `%APPDATA%\HoboLibris\Collections\<serial number>.json` (`CollectionsStore.cs`): as they were when last read from or saved to the eReader, and with the changes made since.  Changes survive ejecting, unplugging and closing HoboLibris.
* With no eReader connected, the newest file opens offline, with a banner saying when it was read.  Ejecting no longer waits for unsaved changes.
* When the eReader is plugged in again, a three-way merge (`CollectionsMerge.cs`) combines the collections as read, the changes made here, and the eReader as it is now.  A change made on one side is kept; the same change on both sides is kept once; a collection's books are the base, plus what each side added, minus what each side took out.
* Collections are matched by the eReader's own `Shelf.Id`, then by name.  Not yet checked: whether the firmware keeps `Id` when a collection is renamed on the eReader.  If it doesn't, the rename is seen as a delete and a new collection.
* Clashes (renamed differently on each side, deleted on one side and changed on the other, two collections with the same name, books added here that are no longer on the eReader) are shown as a card where the person chooses.  When the changes were made offline for another eReader (a different serial number), the person chooses to apply them to this one, discard them, or keep them for the other.

## Articles (not from the desktop program)

Found on the person's eReader with firmware 4.46.23852 on 25 November 2026 (0.1.13, #53).  The code is in `ArticlesService.cs`.

* Each Instapaper article is a content row (`ContentType = 6`, MimeType `application/x-kobo-html+instapaper`, a numeric ContentID, the page it came from in `ContentURL`).  Its text is `.kobo\articles\<id>\index.html`, an HTML fragment.  Its pictures are files beside it, named by hash with no extension, referred to as `file:///mnt/onboard/.kobo/articles/<id>/<name>`.
* Archive on the eReader removes the folder, the `ContentType` 9 row and the `volume_shortcovers` row, and keeps the content row with `PocketStatus` 2 and `IsDownloaded` 'false'.  Such an article is no longer listed.
* Keep saves an article as one web page, `Title - Author.html`, in the Articles folder (`Documents\HoboLibris\Articles` unless chosen in Settings): its title, author, source and date at the top, the pictures embedded, no scripts.  A mark in the page with the article's id lets the screen find kept copies again.
* Remove takes a backup of the book list, then deletes the article's content rows, its `volume_shortcovers` rows and its folder, as the eReader's own Delete left things.  An article removed so stayed gone after the eReader synced, but Instapaper is not told, so the article stays in the person's Instapaper account.

## Previews and the wishlist (not from the desktop program)

Found on the person's eReader with firmware 4.46 on 25 November 2026 (0.1.16, #59).  The code is in `PreviewsService.cs` and `WishlistService.cs`.

* A preview is a content row with `ContentType = 6` and `Accessibility = 6`, with its ISBN and `CrossRevisionId`, and its file in `.kobo\kepub` of size `PreviewFileSize`.  The screen lists those still downloaded, newest first by `___SyncTime`.  Open in store searches kobo.com by ISBN, or by title and author.
* There are three kinds, all opened by the person on the eReader:
  * Saved with Save Preview ("In My Books"): `___UserID` is the person's user id and `EntitlementId` is set.  Saving adds a new content row with its own ContentID; the unsaved row for the same book stays.  These are in My Books, and in the account's library sync as entitlements with `Accessibility` Preview and `OriginCategory` Free.
  * Opened from the store's search results ("From a search"): `___UserID` is `<userid>_searchresult`, with no `EntitlementId`.
  * Opened elsewhere and not saved ("Comes back after a sync"): no user id and no entitlement.  These came back at the next sync after both a Remove Download style removal and a full deletion of the row.  Where from isn't known: the account's library sync doesn't list them, and its `x-kobo-recent-reads` reply header is only a count.  One read for a while has stayed removed since 24 November; how it was removed isn't known.
* The eReader's own removals: Remove Download sets `IsDownloaded` 'false', clears `AnnotationsSyncToken`, and deletes every row with the preview's ContentID as `BookID` (chapters, `ContentType` 9, and table of contents entries, 899), the `volume_shortcovers` rows and the file.  Remove from My Books sets `___ExpirationStatus` 3; at the next sync the account archives the preview (its entitlement comes back with `IsRemoved` true), and the eReader sets `___UserID` to 'removed', `___ExpirationStatus` back to 0 and `IsDownloaded` to 'false', and deletes its rows and file.
* HoboLibris removes previews the same way (#61), after a book-list backup: a saved one gets `___ExpirationStatus` 3 and is shown as removed at the next sync, and one from a search is removed as Remove Download removes it.  Those not saved can't be ticked for removal, and the screen says they come back.  Tested on 25 November 2026: a saved preview removed from HoboLibris was archived in the account at the next sync, and a preview from a search stayed removed after it.
* The wishlist is read from the account (see Wishlist above).  The eReader's `Wishlist` table (`CrossRevisionId`, `DateModified`, `IsAdded`, `IsSynced`, as 'true' and 'false' text) only queues changes:
  * The eReader's own add while online reaches the account at once, and its row is written already synced.
  * A row written with `IsAdded` 'true' and `IsSynced` 'false' reached the account at the next sync, with the row's `DateModified` as `DateAdded`, and the eReader deleted the row.  An add also sets `content.WishlistedDate`.
  * A row written with `IsAdded` 'false' and `IsSynced` 'false' took the book off the account's wishlist at the next sync, and the eReader deleted the row.
* So adding and removing needs the eReader plugged in, takes a book-list backup first, and the screen shows the change as waiting until the next sync.
* The eReader's last sync is `LastSync` (epoch seconds) in `.kobo\Kobo\Kobo eReader.conf`.

## The Assistant (not from the desktop program)

Added in 0.1.6.  The code is in `HoboLibris\Assistant\`; it was ported from Mycelia's Assistant without its MCP parts.

### AI services

* Anthropic (Messages API, `x-api-key`), OpenAI (chat completions, Bearer key) and any OpenAI-compatible service (Ollama, LM Studio, Gemini, Mistral), called through `HttpClient` with no SDK (`ChatProviders.cs`).
* Ollama on this computer is called on its own chat API, `/api/chat` on the host and port given in Settings (0.1.9, #47, `OllamaChatProvider.cs`), since only that one sets the context size and turns thinking off.  The context size (`num_ctx`) is 8,192 tokens unless changed in Settings (2,048 to 131,072); Ollama's own 4,096 dropped instructions without saying so.  Thinking is off unless turned on.  The reply is streamed, and each request's token counts and times go in the log.
* Before a request to Ollama, the conversation is fitted to the context (0.1.10, #48, `ContextFit.cs`): old tool results are left out first, then whole old exchanges, oldest first.  The system prompt, the tools and the current request are always sent, though a result in the current request that is too large is cut.  The conversation itself is kept whole.
* A model on this computer gets a shorter system prompt and only some tools (`ToolGroups.cs`): the group for the screen the person is on, the e-book sources and `show_screen`, and it asks for another group with `use_tools`.  With `qwen3:8b` this took the first request from 4,029 to 2,157 tokens.  Hosted models get every tool.
* The state (screen, connections, book counts, collections, sources) goes after the person's message for a hosted model, as it was when they sent it, and is saved with the message (#77).  Their system prompt then stays the same, so Anthropic and OpenAI read the start of each request from their caches: Anthropic's system prompt is marked for an hour and the last message for five minutes.  A model on this computer has the state in its shorter prompt instead.  Each hosted request's cache counts go in the log.
* The Model list is read from the service once it can be reached, never from a fixed list (0.1.7): `GET {base}/models` for OpenAI-compatible services, `https://api.anthropic.com/v1/models` with the key for Anthropic, and OpenAI's list filtered to chat models (`gpt-`, `o1`, `o3`, `o4`, `chatgpt`, without audio, image, search or embedding models).  Before a request to a service on this computer, a model it doesn't have is reported with the ones it has.
* Ollama (`localhost:11434`): HoboLibris can run `ollama serve` itself with no window (`OllamaServer.cs`), from Settings or on the first request.  A Windows job object with kill-on-close ties the server to HoboLibris, so it stops when HoboLibris closes or crashes; a server started any other way is never stopped.  On the test machine, Ollama 0.40.17's own app never started its server.
* Keys are encrypted with DPAPI (`SecretStore.cs`, prefix `dpapi:`) and added to requests by the app; the model never sees them.

### Tools and approvals

* 29 tools: 25 for the app's own screens (`HoboTools.cs`), which call the same view-model code as the screens, and 4 for e-book sources (`SourceTools.cs`), plus `use_tools` for a model on this computer and `get_state` for a hosted one.  Descriptions for the model are in English; titles and approval sentences are translated.
* Each tool is Allow, Ask first or Off (Settings, Built-in tools).  Tools that change something ask by default.  Saving collections, sending to the eReader, deleting backups, removing articles, taking books off the wishlist and removing previews always ask, whatever the settings, and their cards have no Always allow.
* A tool's request is checked before its approval card is shown (0.1.9, #46), so the person isn't asked to approve something that would fail.  A declined card tells the model so.
* Collection tools only change the Collections screen's pending changes; nothing reaches the eReader until `save_collections`.
* Books reach the model as short ids (`s-`, `r-`, `f-`, `k-`, `c-`, `e-`, `a-` for articles, `p-` for previews and `w-` for the wishlist, plus 8 hex digits of a SHA-256 of the real id), so file paths, which hold the Windows user name, are never sent.  The Kobo sign-in, tokens and email address are never sent.
* The conversation is kept between sessions in `%APPDATA%\HoboLibris\Assistant\conversation.json`, encrypted with DPAPI.

### E-book sources and their terms

Checked on 23 November 2026.

| Source | How it is used |
|---|---|
| Project Gutenberg | Its website is for people only and blocks the address of anything automated (`/policy/robot_access.html`), so nothing goes to `www.gutenberg.org`.  The catalogue `pg_catalog.csv.gz` (about 6 MB) comes from a PGLAF mirror (`gutenberg.pglaf.org`, then `aleph.pglaf.org`) at most once a week, fetched in the background 30 seconds after start-up, and is searched on the computer.  EPUBs come from the mirror: `/cache/epub/{n}/pg{n}-images-3.epub`. |
| Open Library and Internet Archive | Open Library is searched with `ebook_access:public` in the query itself (as a separate parameter it is ignored).  Before a download each Internet Archive item is checked: anything `access-restricted-item`, private, in a lending collection (`inlibrary`, `printdisabled`, `lendinglibrary`), or published within the US copyright term (95 years) without a `licenseurl` is skipped.  At most one request a second per site. |
| Google Books | The person's own key; `filter=free-ebooks` (or `filter=ebooks` with books for sale, below); a download is kept only if Google sends a real EPUB. |
| OPDS catalogues | The person's own (Calibre, Calibre-Web, Kavita).  Search is found through the feed's OpenSearch description; KEPUB is preferred; buy and borrow links are ignored.  The sign-in goes only to the catalogue's own server (scheme, host and port); the address is encrypted too, since Kavita puts its key in it. |
| Standard Ebooks | Link only: its `robots.txt` disallows `/ebooks/*/downloads/*` for AI assistants by name (`claude-user`, `chatgpt-user` and others). |
| Kobo store | Link only: the store's search page opens in the browser.  No purchasing. |

**Books for sale (#91), checked on 27 November 2026.**  A search of Google Books or Open Library can list books that aren't free, for the person to buy; they are never downloaded.  When the model doesn't say which it wants (`paid` on `search_ebooks`), the program shows a card, Free books only or Paid books too, unless Settings › E-book sources says otherwise.

* Google Books: `filter=ebooks` instead of `filter=free-ebooks`.  `saleInfo.saleability` is `FREE`, `FOR_SALE`, `FOR_PRESALE` or `NOT_FOR_SALE`; a book for sale has `listPrice` and `retailPrice` (`amount`, `currencyCode`), and the price shown is the retail one.  Only a `FREE` book's `accessInfo.epub.downloadLink` is used.
* Open Library: `ebook_access:public` is left out of the query, so its whole catalogue is searched.  A result is free only when `ebook_access` is `public` and it has Internet Archive items; `borrowable` ones can be borrowed on Open Library, not downloaded.
* A book that isn't free has a "Find in Kobo store" button: kobo.com's search for its title and author, opened in the browser.
* Kobo's own store search is not used.  Its Terms of Use (section 5, read at `authorize.kobo.com/gb/en/terms/termsofuse`) forbid robots, spiders or other automatic means to access or copy any part of the service, and data mining.  The store API's initialisation now needs a sign-in; Calibre-Web's copy of its resources has no store search, only `/v1/products/autocomplete`, and kobo.com's `robots.txt` disallows its web autocomplete.  Goodreads has had no public API since 2020.

Left out: Wikisource (`robots.txt` blocks every automated client), Feedbooks (no longer answers), DOAB and OAPEN (search APIs refused, mostly PDF), unglue.it (links lead to publishers' sites).

* Requests send the User-Agent `HoboLibris/<version> (+https://github.com/paulfilkin/HoboLibris)`, never the person's email address.
* Downloads go into the Kobo folder as `Title - First author.epub` (`.kepub.epub` for KEPUB), at most 300 MB, kept only if the file is a ZIP whose first entry says `application/epub+zip`.  An existing file is never overwritten.

### Measuring requests (#76)

Changes to what the Assistant sends are measured on the real program, as Mycelia's were (`LLM_Assistant_Approach.md`, section 2).  The kit is kept in `Docs\Measure\`, which `.git\info\exclude` keeps out of the repository:

* `RequestLog.cs.txt`: a temporary class that, when `HOBOLIBRIS_TRACE` names a file, appends one JSON line per request: the time, the provider, the body, the usage, the stop reason and the reply.  Only the body is written, never the headers, so no key reaches the file.
* `build.ps1 -Ref <ref> -Out <folder>`: builds that ref in Release from a scratch worktree with the class added, leaving the working tree alone.
* `drive.ps1`: starts the build, minimises it and gives the keyboard back, then types each question into the pane through UI Automation.  It waits until the request has started and Send is back on two checks in a row, can switch tab between questions (`@kindle`, `@kobo`), and copies the conversation out.  It never allows an approval card: if one appears it presses Stop.
* `run.ps1`: one run, with the provider and model set in `settings.json`.  The run's conversation is moved out of `%APPDATA%\HoboLibris\Assistant` afterwards, and a change to the collections kept on this computer is reported.
* `all.ps1`: `qwen3:8b` twice, then `claude-sonnet-5-5` and `gpt-5-mini`.  `settings.json` is backed up first, with the keys in, and restored at the end, checked by hash.
* `table.py`: per request, the message and tool counts, the system prompt and tool sizes in characters, the tokens at full price, read from the cache and written to it, and the first part of the request that differed from the one before.  `summary.py --replies`: per question, the requests, the tools sent and called, and the last reply.
* Question sets: six (below), eleven (the six and five more, for long conversations) and paused (four, 370 seconds apart, for a cache's lifetime).  All only read.

Close every copy of HoboLibris first: they share the settings and the conversation.  `qwen3:8b` varies between runs, so run it again before calling a difference a regression.

**Baseline, 26 November 2026**, `main` at `839e5c9` (0.1.18 with #74 and #75), eReader unplugged (collections offline), no Kindle.  The six questions:

1. Which of my Kobo books aren't backed up?
2. What collections are on my eReader?
3. Find Middlemarch on Project Gutenberg.
4. What is ready to send to my eReader?
5. (switch to the Kindle tab) Which Kindle books do I have?
6. Of the Kobo books you said weren't backed up, which authors appear more than once?

| | Requests | Input at full price | Read from cache | Output | First request |
|---|---|---|---|---|---|
| `qwen3:8b`, run 1 | 12 | 41,514 | - | 413 | 2,374 tokens, 10 tools |
| `qwen3:8b`, run 2 | 15 | 54,552 | - | 478 | 2,374 tokens, 10 tools |
| `claude-sonnet-5-5` | 11 | 168,347 | 0 | 992 | 8,013 tokens, 30 tools |
| `gpt-5-mini` | 11 | 38,194 | 59,648 | 2,536 | 4,422 tokens, 30 tools |

* Every answer was right, but `qwen3:8b` answered question 4 with `list_books` instead of `list_send` in both runs.  In run 2 its first reply to question 6 was empty and the note from #74 brought it back (4 requests instead of 1).  Ollama took 25 and 31 seconds in all.
* Hosted models get the full prompt (about 6,400 characters) and all 30 tools (about 16,300 characters as Anthropic sends them, 17,300 as OpenAI does).  A model on this computer gets a prompt of about 3,500 characters and 10 to 21 tools.
* Nothing is marked for Anthropic's cache, so all 168,347 tokens were at full price.  OpenAI cached 59,648 of 97,842 by itself.
* The system prompt changed twice within the run, which starts the cache again: after `list_collections` (the state in it changed) and after the switch to the Kindle tab.  #77 moves the state out of the system prompt.
* `list_send`'s result was the largest: with about 146 files in the Kobo folder it took Anthropic's request from 9,058 to 26,976 tokens, and every later request in the conversation carries it.

**State on the message and cache marks (#77), 26 November 2026**, measured on the uncommitted change against `main` at `839e5c9`, in the same conditions.  The state is now sent after the person's message, and the system prompt for hosted models no longer holds it.  Anthropic's system prompt carries a one-hour cache mark, and the last message a five-minute one.  Costs are for `claude-sonnet-5-5` at $2 a million input tokens, $0.10 read from the cache, $2.50 written for five minutes, $4 written for an hour and $10 output.

| Six questions | Requests | Input at full price | Read from cache | Written to cache | Output | Cost |
|---|---|---|---|---|---|---|
| `claude-sonnet-5-5`, before | 11 | 168,347 | 0 | 0 | 992 | $0.347 |
| `claude-sonnet-5-5`, after | 11 | 34 | 147,562 | 28,743 | 997 | $0.109 |
| `gpt-5-mini`, before | 11 | 38,194 | 59,648 | - | 2,536 | |
| `gpt-5-mini`, after | 11 | 18,747 | 84,352 | - | 2,474 | |
| `qwen3:8b`, before (two runs) | 12, 15 | 41,514, 54,552 | - | - | 413, 478 | |
| `qwen3:8b`, after (two runs) | 15, 13 | 54,525, 45,518 | - | - | 478, 441 | |

| Paused set (370 seconds between questions) | Requests | Input at full price | Read from cache | Written to cache | Output | Cost |
|---|---|---|---|---|---|---|
| `claude-sonnet-5-5`, before | 7 | 95,071 | 0 | 0 | 734 | $0.198 |
| `claude-sonnet-5-5`, after | 7 | 22 | 57,849 | 40,647 | 913 | $0.117 |
| `gpt-5-mini`, before | 7 | 14,019 | 40,832 | - | 1,864 | |
| `gpt-5-mini`, after | 7 | 14,181 | 43,136 | - | 2,598 | |

* No request now differs from the one before until its new turns: the change of tab no longer starts the cache again.  On Anthropic every step after the first reads the tools, the prompt and the conversation so far; the first step of the six read nothing because the new prompt had never been sent.
* After each pause, Anthropic read the tools and prompt (7,907 tokens) from the hour's cache and wrote the conversation again, since the five-minute mark had lapsed.  The paused run's first request read the prompt the six-question run had cached about eight minutes before; in use, the same happens between conversations within the hour.
* OpenAI caches by itself, and now reads more of each request from its cache in the six questions.  Over pauses its cache had mostly lapsed, so the paused set is about as before.
* `qwen3:8b` is sent the same requests as before: compared byte for byte, run 2 differed only in tool call ids and the model's own wording.  In that run it gave two empty replies to question 6 and the pane said so (#74); on the old code its run 2 had one, and recovered.  Every hosted answer was as before.
* In the paused baseline on Anthropic, two stray characters were typed into the box before question 3 (the driver now checks the text before Send).

**Tool groups for Anthropic (#78), 26 November 2026: measured and not kept.**  Built as Mycelia does it: every tool declared, those outside the groups shown at the start (the screen's and the e-book sources') marked `defer_loading`, and a group added by `use_tools` sent as `tool_addition` messages under the beta `mid-conversation-tool-changes-2026-07-01`, so the start of the request stayed the same.  Measured on Anthropic against #77, on `main` at `e22ba7b`, in the same conditions:

| `claude-sonnet-5-5` | Requests | First request (tokens) | Input at full price | Read from cache | Written to cache | Output | Cost |
|---|---|---|---|---|---|---|---|
| Six questions, every tool (#77) | 11 | 8,220 | 34 | 147,562 | 28,743 | 997 | $0.109 |
| Six questions, groups | 14 | 5,053 | 2,457 | 169,763 | 28,216 | 1,252 | $0.112 |
| Paused set, every tool (#77) | 7 | 8,220 | 22 | 57,849 | 40,647 | 913 | $0.117 |
| Paused set, groups | 9 | 5,053 | 1,969 | 50,391 | 46,235 | 860 | $0.133 |

* The first request was about 40% smaller and the cache held when a group was added, but each request cost more: Claude called `use_tools` before every question about collections, sending or Kindle, which added three requests to the six questions and two to the paused set.  An added group's definitions come after the cache mark, so they were charged at full price once (about 1,400 and 550 tokens).
* With about 30 tools, deferring 20 saves about 3,000 tokens a request, most of which the cache already charged at a tenth.  After each pause the conversation is written to the cache again whatever is deferred, so the smaller start didn't help there either.  Mycelia gained because it has 68 tools and its questions rarely needed another group.
* Every answer was as before.  Hosted models keep getting every tool.
* Seen in passing: `list_send`'s result for about 146 files is longer than the 40,000 characters a tool result is cut to, and Claude said the list was cut off.

**Shorter tool definitions (#79), 27 November 2026**, measured on the uncommitted change against #77 (`main` at `f442854` sends the same requests), in the same conditions.  A parameter whose name says enough is sent with no description (the shared `ids`, the collection names, `screen`, `source`, `store`, `group` and the search ids); `limit` and `offset` say only their defaults.  Each rule is stated once: "The person approves this every time" is gone from five tools, as the prompt says the program shows its own cards; "Nothing reaches the eReader until save_collections" is gone from the seven collection tools, as both prompts and `save_collections` say it; `remove_from_wishlist` points to `add_to_wishlist` for how the change reaches the account.  The book-list backup stays in each tool that takes one.  `list_previews`, `list_books`, `remove_previews` and `search_ebooks` no longer list the fields their results show.

| First request | Tools | Characters before | after | Tokens before | after |
|---|---|---|---|---|---|
| `claude-sonnet-5-5` | 31 | 16,553 | 14,037 | 8,220 | 7,482 |
| `gpt-5-mini` | 31 | 17,545 | 15,029 | 4,552 | 4,042 |
| `qwen3:8b` | 10 | 6,549 | 5,932 | 2,374 | 2,237 |

| Six questions | Requests | Input at full price | Read from cache | Written to cache | Output | Cost |
|---|---|---|---|---|---|---|
| `claude-sonnet-5-5`, before | 11 | 34 | 147,562 | 28,743 | 997 | $0.109 |
| `claude-sonnet-5-5`, after | 11 | 34 | 140,044 | 27,954 | 1,071 | $0.105 |
| `gpt-5-mini`, before | 11 | 18,747 | 84,352 | - | 2,474 | |
| `gpt-5-mini`, after | 11 | 18,136 | 79,232 | - | 2,711 | |
| `qwen3:8b`, before (two runs) | 15, 13 | 54,525, 45,518 | - | - | 478, 441 | |
| `qwen3:8b`, after (two runs) | 16, 16 | 60,011, 60,004 | - | - | 1,203, 1,207 | |

* Anthropic's tools and prompt, cached for the hour, went from 7,907 tokens to 7,169.  Each request reads 738 fewer tokens from the cache, so the saving is small: about 3% of the six questions' cost.
* Every hosted answer was as before, with the same tools called.
* `qwen3:8b` answered question 4 with `use_tools` and `list_send` in both runs, where before it called `list_books`.  That added two requests and `list_send`'s long result, which is why its input grew.  It then listed 31 files as ready to send, though every file was unchecked with the eReader unplugged and `ready` was 0; with `list_books` it had said nothing was ready.  Its reply to question 6 was empty in both runs, after the second try from #74; before it was empty in one run of two.

## Display

* Kobo.exe's manifest has no DPI-awareness entry, so Windows bitmap-stretches it on hi-res screens.  HoboLibris is a WPF program and is DPI-aware.
