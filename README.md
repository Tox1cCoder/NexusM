# NexusM

NexusM is a small Windows app for checking Nexus Mods updates, reading release notes, and downloading the exact mod files you want. You install the downloaded archives yourself. Closing the app stops it; there is no background service.

**[Download the latest Windows release](https://github.com/Tox1cCoder/NexusM/releases/latest)** · [Build and test status](https://github.com/Tox1cCoder/NexusM/actions/workflows/ci.yml)

Download `NexusM-<version>-windows-x64.zip` from **Assets** and extract it before opening the app. The automatically generated **Source code** downloads contain source only. A matching `.sha256` file is available to check the ZIP's integrity.

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
4. Follow progress under **Downloads**. Use the folder button beside a completed download to highlight its archive in File Explorer, then install it using the author's instructions or your mod manager. **Open download folder** opens your configured download location.

Downloads run in parallel, with two transfers by default. Under **Settings → Download queue**, choose one to four parallel transfers, automatic retries, retry delay, network timeout, and whether unfinished downloads resume when the app opens.

Every accepted file is saved in the queue before authorization or transfer starts. Closing the app or replacing it with a newer version keeps unfinished jobs and their original folders. With automatic recovery enabled, connecting to Nexus requests fresh authorization and resumes those jobs in queue order. Sign in or complete a browser check if prompted. A verified partial file resumes when the server supports it; otherwise the transfer restarts safely. Existing completed archives are not overwritten.

Temporary connection errors, timeouts, server failures, and rate limits receive bounded retries. Failed or timed-out jobs stay in **Downloads** with a **Retry** button while other jobs continue. Cancelling a job keeps its partial file and prevents automatic recovery of that job. **Download log** on each entry shows recent state changes, attempts, recovery, and error codes. API keys and signed download links are excluded from these diagnostics.

The Downloads badge counts completed archives in the current list; the page summary shows downloaded, active, and total entries.

See [Download queue behavior and recovery](docs/download-queue.md) for configuration defaults, restart behavior, failure handling, and verification details.

While a completed archive remains in **Downloads**, NexusM checks for newer versions at startup, with **Check for updates**, and every 30 minutes while the app is open. A popup shows the old and new filenames and versions. Matching uses Nexus mod/file IDs and the author's file-update links, so changed archive names are supported.

Downloading a linked newer file also checks for older retained archives, including batch downloads and Nexus download links. The popup can appear while the mod's details are open. If the newer file is already queued, **Replace when ready** uses that transfer; if it has finished, **Replace with downloaded file** verifies and uses the saved archive. Neither choice downloads a second copy.

- **Replace** downloads the newer file through the normal Nexus flow, then permanently deletes the recorded old archive after the new download succeeds. Failed or cancelled downloads keep the old file.
- **Delete old file** permanently deletes only that recorded archive and removes its Downloads entry. Its download history stays.
- **Keep old file** dismisses that update's popup. Use **Review update** on its Downloads entry to reopen it; a later update can notify you again.

If the author has not identified a single replacement, NexusM can notify you when the mod reports a changed version and offers **Review files** so you can choose. It does not guess which optional file or variant replaces your archive. Only archives that still match their saved file identity are eligible; renamed, moved, or externally changed files are left alone. Clearing the Downloads list stops these archive notifications.

For mods with exactly one main file and no optional files, use the checkboxes in **My library**, then **Download selected** to queue up to 100 mods together. Checking a box verifies that mod's current files without opening its details. Mods with file choices explain why they cannot be selected; open their details to choose files. Selections stay when you search or switch games, and **Clear selection** removes them.

The batch checks file choices again before queuing, skips files already queued, and keeps failed selections available for retry. Downloads follow the same Nexus authorization, free-account countdown, and sign-in steps as individual downloads.

**Downloaded** flags record exact files downloaded through NexusM; they do not mean a mod is installed. Older website history may have a mod-level download date without a per-file flag.

Select **Preview file contents** on any file card to browse its archive folders and filenames before downloading. The preview loads only when requested and keeps your selected files unchanged. Some archives have no preview available on Nexus.

### Links from your normal browser

NexusM captures `nxm://` links inside its own Nexus window automatically. To handle links clicked in your usual browser too, choose **Settings → Handle Nexus download links → Set NexusM as handler**. This replaces your Windows account's current `nxm://` handler, including another mod manager if one is configured.

## Free up space

| Action | What it does | What it keeps |
| --- | --- | --- |
| **Downloads → Clear list** | Removes finished, failed, cancelled, interrupted, and waiting entries. Waiting authorization windows close. | Archives, partial files, download history, and active or queued transfers. |
| **Downloads → Clear download folder** | **Permanently deletes only recorded NexusM downloads and verified NexusM partial files/resume sidecars directly in your download folder.** It bypasses the Recycle Bin. | Unrelated files, unrecorded or renamed archives, subfolders and their contents, download records, and Downloaded flags. |
| **Settings → Clear cache** | Clears cached browser content and images, which can be fetched again. | Website sign-in, your library, and downloaded archives. |

**Permanent deletion cannot be undone in the app or through the Recycle Bin.** Check the folder path and file count in the confirmation before continuing. This is normal file deletion, not secure data erasure.

Finish or cancel pending downloads before clearing the folder. Cleanup uses exact saved archive paths and matching NexusM resume metadata; it does not select files just because they are ZIPs or resemble a NexusM filename. Clearing the download list keeps the per-file history used to identify completed downloads. Files with insufficient ownership information stay. Use a dedicated folder on a local fixed drive. Drive roots, personal library roots, linked folders, Windows folders, and NexusM's application/data folders are protected. Files added or changed after the confirmation snapshot are skipped; files that cannot be deleted are reported so you can address them and try again.

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
| `state.json.bak` | The previous saved state, used to recover an unreadable state file. Unreadable originals are preserved separately for recovery. |
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
| The app window stays blank or stops responding | NexusM shows a loading or recovery screen and attempts one automatic recovery after a browser crash. Select **Retry** if it cannot recover. Your saved library, sign-in, and downloads are retained. If it repeats, check `%LOCALAPPDATA%\NexusM\startup.log` for the recorded failure code. |
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
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-QueueBrowser.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-QueueRestart.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FolderCleanup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-ModBatch.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-BatchDownload.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Refresh.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-DownloadUpdates.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FilePreview.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FreeDownloadFlow.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-UI.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Startup.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Version.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Package.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Release.ps1
```

Optional read-only live API checks use your locally encrypted key:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`Test-ModBatch.ps1 -Live` checks live pagination; `Test-Refresh.ps1 -Live` checks an isolated copy of your library. The account-dependent `--smoke-test` mode additionally checks live mod details, images, and protocol capture. No test initiates a real Nexus archive download. Reports and screenshots go in `artifacts`, which is excluded from source control.

The icon source is `ui\assets\app-icon.svg`. After editing it, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-Icon.ps1` before rebuilding.

### CI and publishing releases

Push your changes to `main`. **Windows CI** assigns a new build version, builds and tests that commit, verifies the package checksum, creates the matching tag, and publishes the ZIP, SHA-256 checksum, and release notes under [Releases](https://github.com/Tox1cCoder/NexusM/releases). No manual version edit, tag, release creation, personal Nexus API key, or repository secret is required.

Automatic releases use a plain `major.minor.patch` version everywhere. The source version is a floor: with source `1.5.2.0` and existing `v1.5.2.*` releases, the next main build publishes `v1.5.3` with `NexusM-1.5.3-windows-x64.zip`, followed by `1.5.4`. Settings and the release title show the same plain version; Windows stores the equivalent `1.5.3.0`. CI reads existing tags after the preceding main workflow finishes, and reruns reuse the plain version already reserved for their commit. Existing releases and four-part legacy tags stay unchanged. Version stamping changes only generated build files. Increase the source major or minor to start a new release line, or set a higher patch as the next release floor.

Pull requests, version tags, and manual **Windows CI** runs also build, test, and upload a `nexusm-windows-x64` artifact containing the ZIP and checksum. Pull requests and manual runs do not publish. Build jobs have read-only repository access; only the publishing job can create a tag or release using GitHub's built-in workflow token. Publisher fixtures run on both Windows PowerShell and Linux PowerShell before publishing.

Manually pushing a matching `v<version>` tag is still supported for a named source version, such as `v1.5.1` for `1.5.1.0`. A tag that does not match the built version, or an unpublished tag already pointing at a different commit, stops publishing. Existing tags and published assets are never moved or overwritten. A failed asset upload leaves a draft; rerun the failed publishing job after resolving the error so it reuses the original verified build artifact. Recovering an older draft does not replace a newer build as the Latest download.

For a local package after building, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Package-Release.ps1`. Output goes to `artifacts\release`. Packaging uses an explicit app-file list and refuses to overwrite an existing package; choose a fresh `-OutputDirectory` for another local run.

To reproduce a particular automatic version locally, run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -AppVersion 1.5.3`. An optional `-OutputDirectory` keeps that build in a separate folder. Numeric components must be between 0 and 65534, as required by the Windows assembly version format. Legacy four-part versions remain accepted for reproducing older builds.

---

NexusM is an independent app and is not affiliated with Nexus Mods.
