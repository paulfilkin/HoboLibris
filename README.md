<p align="center">
  <img src="Docs/images/hobolibris.png" width="256" alt="The HoboLibris icon: an open book on a teal tile, with a dashed copper trail running across its pages to a glowing compass star">
</p>

<h1 align="center">HoboLibris</h1>

<p align="center">
  <strong>Let your books wander.  A copy always stays home.</strong>
</p>

<p align="center">
  A Windows desktop app that keeps a copy of your Kobo and Kindle books on your computer,<br>
  sorts your Kobo eReader into collections, and sends your own books to it.
</p>

<p align="center">
  <a href="https://github.com/paulfilkin/HoboLibris/releases/latest">Download</a> ·
  <a href="#getting-started">Getting started</a> ·
  <a href="Docs/HoboLibris-Mechanics.md">How it works</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

## Why

E-books live in other people's places: a store account, an eReader, a reading app.  Getting a copy of your own is harder than it should be.

Kobo's desktop app downloads your books, but Windows stretches it on high-resolution screens until it is hard to use.  Amazon stopped offering Kindle book downloads in February 2025, so the copies already on your Kindle, or in Kindle for PC, are the ones you can still reach.  Collections on a Kobo eReader are sorted one book at a time, on its small screen.

HoboLibris puts all of that in one window that stays sharp on high-resolution screens.  Sign in to Kobo, plug in your eReader or Kindle, and press one button to back up everything not yet saved.  Books are copied exactly as they are delivered: nothing is decrypted, converted or uploaded anywhere.

## What it does

- **Backs up your Kobo store books.**  It signs in to your Kobo account the way the Kobo desktop app does, lists every book you bought, and downloads each one to a folder you choose, as `Title - Author.epub`.  Unprotected copies come first; protected ones are saved exactly as Kobo delivers them.
- **Backs up the books you added to your Kobo eReader.**  Plug it in and tap Connect, and the books you put on it yourself join the list, with titles and authors from the eReader's own book list.  A book that is also in your store list is marked, so it is not saved twice.
- **Sorts your eReader into collections.**  See which collection each book is in, show only the books in none, then create, rename, move and remove in a proper window.  Nothing changes on the eReader until you save, and its book list is copied to your computer first.
- **Sends your own books to the eReader.**  Books you put in your Kobo folder are checked: whether a Kobo can open them, whether they are damaged or copy-protected, and whether the eReader already has them.  Then they are copied across.  It can do this by itself each time the eReader is plugged in.
- **Backs up your Kindle books.**  From a Kindle plugged in by USB, with or without a drive letter, from Kindle for PC, or from any folder you add.  Each book is copied unchanged, with its sidecar folder where it has one.
- **Asks before removing anything.**  Deleted backups go to the Recycle Bin.  Nothing on the eReader is removed unless you tick the box that says so.
- **Comes in 12 languages.**  English, German, French, Spanish, Italian, Portuguese, Japanese, Chinese, Korean, Arabic, Hebrew and Ukrainian, with right-to-left layout for Arabic and Hebrew.

## The screens

<p align="center">
  <img src="Docs/images/kobo-screen.png" alt="HoboLibris on the Kobo tab's Back up books screen: the toolbar with Refresh list, Back up all not yet saved, Save to and Open folder; the backup folder; search and sort; and the list of books with their author, format, where each came from, and a green In the folder or Saved beside each one">
</p>

| Screen | What it does |
|---|---|
| Kobo: Back up books | One list of your store books, your eReader books and the books only on this computer, with what is saved and a button for the rest |
| Kobo: Collections | Your eReader's collections and the books in each, changed here and saved in one go |
| Kobo: Send to eReader | The books in your Kobo folder, whether each is ready to send, and why not when it isn't |
| Kindle | The books on your Kindle, in Kindle for PC or in folders you add, and what is backed up |

## Getting started

You need Windows 10 or 11, 64-bit.

1. Download `HoboLibrisSetup-<version>.exe` from the [latest release](https://github.com/paulfilkin/HoboLibris/releases/latest) and run it.  It installs for you alone, without an administrator password, and brings everything it needs.
2. On the **Kobo** tab, press **Sign in** and sign in to your Kobo account in the window that opens.  Your library loads by itself.
3. Press **Back up all not yet saved**.  Choose another folder first with **Save to…** if you like; the default is `Documents\HoboLibris\Kobo`.
4. Plug in your Kobo eReader and tap **Connect** on its screen.  The books you added to it join the list, and the **Collections** and **Send to eReader** screens can work with it.
5. On the **Kindle** tab, plug in your Kindle.  If it doesn't appear, the tab says what Windows can see and what to do next.

The release also has a zip with just the program.  It needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.

HoboLibris keeps its settings, your Kobo sign-in, its logs and the eReader book-list backups in `%APPDATA%\HoboLibris`.  Uninstalling leaves them, and your books, where they are.

## From a terminal

| Switch | What it does |
|---|---|
| `--screenshot <file.png>` | Saves a picture of the main window after four seconds, then closes |
| `--kindle`, `--send`, `--collections` | With `--screenshot`, shows that screen first |

## Building from source

You need the .NET 8 SDK, and Inno Setup 6 for the installer.

```powershell
gh repo clone paulfilkin/HoboLibris
```

```powershell
cd HoboLibris
```

```powershell
dotnet run --project HoboLibris\HoboLibris\HoboLibris.csproj
```

```powershell
dotnet test HoboLibris\HoboLibris.Tests\HoboLibris.Tests.csproj
```

The installer:

```powershell
dotnet publish HoboLibris\HoboLibris\HoboLibris.csproj -c Release -r win-x64 --self-contained
```

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" HoboLibris\HoboLibris.iss
```

The solution is in `HoboLibris\`.  In the app, `Services\` holds the work with the store, the devices and the files, `ViewModels\` and `Views\` the screens, `Windows\` the dialogs, `Themes\` the colours and styles, and `Resources\` the text in each language.  `HoboLibris.Tests\` holds the unit tests, and `Tools\` the two string scripts.

All text in the app is in `Resources\Strings.resx`, with one `Strings.<language>.resx` for each translation.  After adding or renaming a string, regenerate the strongly typed class, which the build does not do, and check the translations:

```powershell
powershell -NoProfile -File HoboLibris\Tools\Update-Strings.ps1
```

```powershell
powershell -NoProfile -File HoboLibris\Tools\Check-Translations.ps1
```

The app uses NLog, CommunityToolkit.Mvvm, Microsoft.Web.WebView2 for the Kobo sign-in page, Microsoft.Data.Sqlite for the eReader's book list, and System.Management for the Kindle connection check.

## Status

HoboLibris is at version 0.1.  It is a WPF rebuild of an earlier prototype that was used with a real Kobo account, Kobo eReader and Kindle.  The rebuild keeps the prototype's behaviour and has its tests, but signing in, downloading and plugged-in devices have not yet been tried on it.  It runs on Windows only.  Bugs and ideas are welcome as [issues](https://github.com/paulfilkin/HoboLibris/issues/new/choose).

## Documentation

- [How it works](Docs/HoboLibris-Mechanics.md): how the Kobo desktop program signs in, lists and downloads books, and the rules for eReaders and Kindles.
- [Working with Git](Docs/Working%20with%20Git.md): the commands for each step of a change, for someone new to Git.
- [Contributing](CONTRIBUTING.md): how changes get in.

## Copyright

Copyright © 2026 multifarious.
