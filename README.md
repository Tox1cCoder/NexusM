# NexusM

NexusM is a small Windows app for checking Nexus Mods updates, reading release notes, and downloading the exact mod files you want. You install the downloaded archives yourself. Closing the app stops it; there is no background service.

## What you need

- A 64-bit Windows PC.
- A Nexus Mods account and your personal API key. Free accounts can download through the normal Nexus website steps and countdown.
- [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) or a compatible newer version, and [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). Install these if the app asks for them.

## Quick start

1. **Open the app.** Run `NexusM.exe` from the app folder (`dist\NexusM.exe` when built from source). Keep its DLLs, configuration file, and `ui` folder beside it. If you only have source code, see [Build from source](#build-from-source).
2. **Connect your account.** Open **Settings → Nexus connection → Get your Nexus API key**. This opens the [official Nexus API page](https://www.nexusmods.com/users/myaccount?tab=api) in your web browser. Sign in, copy your **Personal API Key**, paste it into NexusM, and select **Save & connect**.
3. **Choose a download folder.** Under **Settings → Download location**, type a full path or select **Browse**, then **Save location**. The default is `%USERPROFILE%\Downloads\NexusM`. A custom path such as `D:\Mod Archives` is used directly.
4. **Import your mods.** Select **Import history → Open Nexus history**. Sign in inside the Nexus window, wait for Download history to load, choose **All games** or one game, and press **Import history** in that window.
5. **Browse your library.** NexusM loads current mod information after importing. Select a mod to see its files, overview, changelog, and requirements.

You can also use **Add mod** to paste a Nexus mod-page link, or **Import tracked mods** in the import dialog.

## Check for updates

Your saved library appears when the app opens. Once connected, NexusM syncs your favourite games and checks known mods automatically. Use **Check for updates** to check again while the app is open.

- **Your games only** shows and checks mods for the games in your game list. Turn it off to include imported history for other games.
- Select a game to narrow the list and update check to that game.
- **Needs review** means the mod changed after your last download or review. Open it and read the author's notes; the change may concern an optional file you do not use.
- **Mark reviewed** dismisses the current update without changing your download date.

Adding or removing a game changes your local list. It keeps the game's mod history and does not change your favourites on the Nexus website.

New mods downloaded outside NexusM need another history import or **Add mod**. Update checks can retrieve newer account download dates for mods already in your library.

## Download a mod file

1. Open a mod and read its **Overview**, **Changelog**, and **Requirements**.
2. In **Files**, choose **Get this file**, or select several files and choose **Download selected**. Main files and optional files are listed separately.
3. NexusM opens the website's normal download flow and handles its free-download controls and countdown. Sign in, choose cookie preferences, or complete a browser check if a Nexus window needs your attention.
4. Follow progress under **Downloads**. Use **Open download folder** to find the completed archives, then install them using the author's instructions or your mod manager.

Downloads run one at a time. Cancelling a download or closing the app keeps its partial file. **Retry** gets fresh authorization and resumes when the server supports it. Existing completed archives are not overwritten.

**Downloaded** flags record exact files downloaded through NexusM; they do not mean a mod is installed. Older website history may have a mod-level download date without a per-file flag.

### Links from your normal browser

NexusM captures `nxm://` links inside its own Nexus window automatically. To handle links clicked in your usual browser too, choose **Settings → Handle Nexus download links → Set NexusM as handler**. This replaces your Windows account's current `nxm://` handler, including another mod manager if one is configured.

## Free up space

| Action | What it does | What it keeps |
| --- | --- | --- |
| **Downloads → Clear list** | Removes finished, failed, cancelled, interrupted, and waiting entries. Waiting authorization windows close. | Archives, partial files, download history, and active or queued transfers. |
| **Downloads → Clear download folder** | **Permanently deletes the confirmed files directly in your download folder**, including archives, partial downloads, sidecars, and any other files there. It bypasses the Recycle Bin. | Subfolders and their contents, download records, and Downloaded flags. |
| **Settings → Clear cache** | Clears cached browser content and images, which can be fetched again. | Website sign-in, your library, and downloaded archives. |

**Permanent deletion cannot be undone in the app or through the Recycle Bin.** Check the folder path and file count in the confirmation before continuing. This is normal file deletion, not secure data erasure.

Finish or cancel pending downloads before clearing the folder. Use a dedicated folder on a local fixed drive. Drive roots, personal library roots, linked folders, Windows folders, and NexusM's application/data folders are protected. Files added or changed after the confirmation snapshot are skipped; files that cannot be deleted are reported so you can address them and try again.

## Back up or restore your library

- **Back up:** choose **Settings → Library & history → Export** and save the JSON file.
- **Restore:** choose **Settings → Library & history → Import**, select a NexusM JSON backup, and import it. Records merge with your existing library.

Backups include games, mod history, review dates, and per-file download records. They do not include your API key, website sign-in, or archive files. They can contain local file paths and your mod history, so they are not anonymous.

## Your data and API key

NexusM stores app data under `%LOCALAPPDATA%\NexusM`. Paste that path into File Explorer to open it.

| Location | Contents |
| --- | --- |
| `api-key.bin` | Your API key, encrypted with Windows DPAPI for the current Windows account. |
| `state.json` | Your local library, settings, and download records. This file is not encrypted. |
| `WebView` | Embedded browser data, including the Nexus website sign-in session. |
| Your chosen download folder | Completed archives and unfinished downloads with their small resume sidecars. |

The saved API key is not sent back to the app's UI or included in library backups or download sidecars. API requests send it to `api.nexusmods.com` over HTTPS; archive servers do not receive it. Each Windows account configures its own key. Keep the app-data folder private because it also contains your website session.

## Troubleshooting

| Problem | Try this |
| --- | --- |
| The app will not open, or reports a missing runtime | Keep all app files together. Install or repair WebView2 Runtime and .NET Framework using the links above. |
| Nexus rejects the API key | Copy your current Personal API Key again and select **Save & connect** in Settings. |
| History import finds nothing | Sign in inside the Nexus window, open Download history, wait for the table, and press that window's **Import history** button. |
| Only part of the history imports | NexusM reports when only visible rows were available. Import the other pages too, or import saved HTML/JSON through the import dialog. |
| A mod is missing from the library | Turn off **Your games only**, import history again, or use **Add mod**. |
| A download stalls or its link expires | Complete any sign-in/browser challenge, then use **Retry** or **Get this file** for fresh authorization. The free-account countdown still applies. |
| Clear download folder is unavailable | Finish or cancel pending downloads. Check that the saved location is a dedicated local folder outside protected paths. |
| Nexus is unavailable or rate-limits requests | Browse the saved library and try checking again later. Previous mod information is retained when requests fail. |

Website changes can affect history import and free-download automation. **Open in browser** lets you continue on Nexus. Requirements and installation instructions come from mod authors; read the mod page and pinned posts when something is unclear.

## Build from source

From the project folder, run this in Windows PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Then open `dist\NexusM.exe`. The build uses Windows' C# compiler and downloads a pinned Microsoft WebView2 SDK into `%LOCALAPPDATA%\NexusM-BuildCache`. Node, Electron, and a bundled browser are not required. The app files are about 1.3 MB, excluding runtimes, browser data, and downloads.

### Developer checks

After building, these suites use isolated fixtures and need no Nexus account. The UI suite runs the real WebView UI without using your saved profile or API key.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Backend.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-HistoryImport.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Controller.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FolderCleanup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-ModBatch.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Refresh.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FreeDownloadFlow.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-UI.ps1
```

Optional read-only live API checks use your locally encrypted key:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`Test-ModBatch.ps1 -Live` checks live pagination; `Test-Refresh.ps1 -Live` checks an isolated copy of your library. The account-dependent `--smoke-test` mode additionally checks live mod details, images, and protocol capture. No test initiates a real Nexus archive download. Reports and screenshots go in `artifacts`, which is excluded from source control.

The icon source is `ui\assets\app-icon.svg`. After editing it, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-Icon.ps1` before rebuilding.

---

NexusM is an independent app and is not affiliated with Nexus Mods.
