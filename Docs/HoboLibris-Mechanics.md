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
* The Kobo tab shows one list: store books (from the account), the books added to the eReader (when plugged in), and the books only in the Kobo folder ("This computer": not store copies and not on a connected eReader; title and author read from "Title - Author.ext", "_ " shown as ": "), with a "From" column.  Folder books can be deleted (Recycle Bin) like the others.  One folder holds both.  Store copies on the eReader are not listed, since they are the store books already in the list.
* An eReader book whose title matches any book in the store list (letters and digits compared, subtitle after `:` or `(` ignored) is shown as "Same as store book" and left out of "Back up all".
* Store downloads are named `Title - First author.ext` like the eReader copies; copies saved earlier as `Author - Title.ext` still count as saved.
* Copies go straight into the chosen folder, named after the book: `Title - First author.ext` (`.kepub.epub` kept); a second book with the same name gets its format, then a number, added.  Kindle copies also go straight into the chosen folder under their own file names, with their `.sdr` folders beside them.
* Observed: 146 added books (all with title and author from the database) and 1 store copy; the 1 saved store book matched none of the 146.

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

Estimated from Kobo's book-list layout as calibre's Kobo driver uses it; not yet tried on the person's eReader.

* Collections are rows of `Shelf` (`Name`, `Id`, `InternalName`, `CreationDate`, `LastModified`, `Type`, `_IsDeleted`, `_IsVisible`, `_IsSynced`...); membership is `ShelfContent` (`ShelfName`, `ContentId`, `DateModified`, `_IsDeleted`, `_IsSynced`).  Rows with `_IsDeleted = 'true'` don't count.
* Books shown: everything the person added, downloaded store books, and any book already in a collection.
* Changes stay in the app until Save.  Save copies the book list to `%APPDATA%\HoboLibris\Kobo book list backups\` first, refuses if the eReader's book list changed since it was read, and writes in one transaction.
* New collection: revive a deleted one of the same name, else insert a `Shelf` row with only the columns the table has, a new UUID `Id`, `Name` = `InternalName`, `Type` = the most common existing type, `_IsSynced = 'false'`.
* Delete collection / remove book: mark `_IsDeleted = 'true'`, `_IsSynced = 'false'` (rows are kept, so Kobo's sync can pass the change on).  Add book: revive the row, else insert one.
* Rename: the books go to a collection with the new name and the old one is deleted (the same steps as above), since `ShelfContent` refers to collections by name.
* Move: the ticked books go into the chosen collection and out of every other one.  "Also add" keeps their other collections.
* Times are UTC `YYYY-MM-DDTHH:MM:SSZ`.  The eReader shows the changes after it is ejected.

## Display

* Kobo.exe's manifest has no DPI-awareness entry, so Windows bitmap-stretches it on hi-res screens.  The rebuild is DPI-aware through egui.
