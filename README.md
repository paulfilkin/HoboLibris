# HoboLibris

Keeps a copy of your Kobo and Kindle books on your computer, sorts your Kobo eReader into collections, and sends your own books to it.

## Requirements

To run it:

- Windows 10 or 11, 64-bit.
- The Microsoft Edge WebView2 runtime, for the Kobo sign-in page.  Windows 10 and 11 include it.
- The .NET 8 Desktop Runtime, only for the zip from a GitHub release.  The installer brings its own.

To build it:

- The .NET 8 SDK.
- Inno Setup 6, for the installer.

## Running it

**Installer.**  Build it with the commands below and run `HoboLibrisSetup-<version>.exe` from `Releases\`.  It installs for the current user without an administrator password, and replaces an earlier installation, including the Rust build.

**Release zip.**  Pushing a version tag (`v1.2.3`) makes the Release workflow attach `HoboLibris-v1.2.3.zip` to a GitHub release.  Unzip it and run `HoboLibris.exe`.

**From the source:**

```powershell
dotnet run --project HoboLibris\HoboLibris\HoboLibris.csproj
```

Settings, the Kobo sign-in, logs and the eReader book-list backups are kept in `%APPDATA%\HoboLibris`.  Books are backed up to `Documents\HoboLibris` unless you choose another folder.

### Building and testing

```powershell
dotnet build HoboLibris\HoboLibris.sln
dotnet test HoboLibris\HoboLibris.Tests\HoboLibris.Tests.csproj
```

The installer:

```powershell
dotnet publish HoboLibris\HoboLibris\HoboLibris.csproj -c Release -r win-x64 --self-contained
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" HoboLibris\HoboLibris.iss
```

### UI text

All text is in `HoboLibris\HoboLibris\Resources\Strings.resx`, with one `Strings.<language>.resx` per translation.  After adding or renaming a string, regenerate the strongly typed class (the build does not do it), then check the translations:

```powershell
powershell -NoProfile -File HoboLibris\Tools\Update-Strings.ps1
powershell -NoProfile -File HoboLibris\Tools\Check-Translations.ps1
```

### Screenshots

`HoboLibris.exe --screenshot <file.png>` saves a picture of the main window after four seconds and closes.  Add `--kindle`, `--send` or `--collections` to show that screen.

## Layout

- `.github\` holds the workflows (build and test, Guard, CodeQL, release, project board), the issue forms and the pull request template.
- `.githooks\` holds the local commit-message and pre-push checks.
- `Docs\` holds the Git guide, the notes on how the Kobo desktop program talks to the store (`HoboLibris-Mechanics.md`), and the icon sources.
- `HoboLibris\` holds the solution: the app (`HoboLibris\`), its tests (`HoboLibris.Tests\`), the string tools (`Tools\`) and the installer script (`HoboLibris.iss`).
- `Releases\` receives the installer.  It is not in Git.

## Working on it

How changes get in, and the commands for each step: `CONTRIBUTING.md` and `Docs\Working with Git.md`.
