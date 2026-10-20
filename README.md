# NexusM

A small Windows desktop app for checking Nexus Mods updates, reading release information, selecting individual files, and saving archives for manual installation. Nothing runs after you close the app.

## Run

Open `dist\NexusM.exe`. Keep the other files in `dist` beside it. The portable app is about 1.2 MB and uses .NET Framework 4.8 and Microsoft Edge WebView2 Runtime. These components are commonly already installed on Windows. Browser profile data and downloaded archives are separate from that size.

Enter your personal Nexus Mods API key under **Settings**. NexusM reuses a key already configured for the current Windows account and encrypts it with Windows DPAPI in `%LOCALAPPDATA%\NexusM\api-key.bin`. The key is never embedded in the executable, source, library backup, or download sidecar. Each Windows account configures its own key.

1. Your Nexus favourite games sync when the app opens. Add/remove games locally as needed; local additions and exclusions survive syncing. **Your games only** filters the library and update checks to this list by default. Turn it off to see/check all imported mods, including older games; selecting one game still narrows the table to that game. Removing a game preserves its history for later. These controls do not change your favourites on the website.
2. Click **Import history → Open Nexus history**, sign in in the window, wait for Download history to load, and click **Import history** in that window. Select All games or a specific game. This step discovers which mods belong in your library.
3. Click **Check for updates**. For known mods, NexusM refreshes metadata and your account's last download dates through the API, including downloads made outside the app. A newer mod update is a prompt to review, because it may affect an optional file you do not use.
4. Open a mod to read its description, changelogs, requirements, and individual file cards. Multiple main files and optional files remain separate. **Mark reviewed** dismisses the current update without changing your download date.
5. Click **Get this file**, or select several files and click **Download selected**. NexusM handles the website's normal free-download controls and countdown automatically in its embedded browser, then receives the authorized `nxm://` link and saves the archive in your chosen folder. The browser stays out of the way unless Nexus needs sign-in, cookie preferences, a browser check, or manual help. Installation is manual.

Downloads queue sequentially. Cancel/closing the app keeps a `.part` file; Retry obtains fresh website authorization and resumes when the server supports it. Completed archives are not overwritten. Each file downloaded through NexusM gets its own local download record. Imported website history contains mod-level dates, not historical per-file markers. **Downloads → Clear list** removes finished, failed, cancelled, interrupted, and waiting entries. Active/queued transfers remain, and clearing never deletes archives, partial files, or saved download dates. Waiting website windows close with their removed entries.

The embedded Nexus window captures download links without changing your Windows protocol settings. **Settings → Handle Nexus download links** optionally makes NexusM the default `nxm://` handler for links clicked in your normal browser; this replaces the current handler.

## History and author information

Nexus does not expose complete download-history enumeration in its public API. Import uses the signed-in website's own history table/read-only history request, then normal API calls handle update checks. It imports all available table rows and clearly reports when only the visible page could be read. New mods downloaded outside NexusM require another history import or **Add mod**.

The website importer has fixture coverage, but its live operation requires signing in and can be affected by website changes or browser challenges. If necessary, use the provided **Open in browser** option, then import saved HTML/JSON or paste mod links. Plain links/HTML fallback may lack reliable downloaded dates; Check for updates retrieves those dates for the known mods when Nexus exposes them.

Requirements are author-provided metadata. Read descriptions/changelogs and use **Open on Nexus** for pinned posts and instructions such as removing old versions. The app does not infer installation steps or track installed mods.

Library/download records live in `%LOCALAPPDATA%\NexusM\state.json`. The WebView profile preserves website sign-in; HTTP image caching is limited to approximately 16 MB. WebView profile databases add their own storage. **Settings → Clear cache** clears browser cache while retaining sign-in. **Export** creates a library/history backup without credentials; **Import** merges it back, including per-file download history and review dates.

## Build and verify

No Node, Electron, SDK installation, or bundled browser is needed. The build uses Windows' C# compiler and downloads the pinned Microsoft WebView2 SDK package into `%LOCALAPPDATA%\NexusM-BuildCache`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Backend.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-HistoryImport.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-Controller.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-FreeDownloadFlow.ps1
```

`-Test` performs read-only live API checks with the locally encrypted key. Other suites use isolated fixtures and do not need a Nexus account. The UI smoke test runs the real WebView window, its bridge, and `tests\ui-checks.js`:

```powershell
Start-Process .\dist\NexusM.exe -ArgumentList '--smoke-test', "$PWD\artifacts\ui-smoke.json" -Wait
```

Reports and screenshots are under `artifacts` (excluded from source control). No tests initiate a real Nexus archive download.

Official references: [v1 client](https://github.com/Nexus-Mods/node-nexus-api), [GraphQL documentation](https://graphql.nexusmods.com/), [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/). Independent personal app; not affiliated with Nexus Mods.
