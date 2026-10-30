using System;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NexusM
{
    internal sealed class MainForm : Form
    {
        private WebView2 web;
        internal readonly Controller Controller;
        private readonly Storage storage;
        private CoreWebView2Environment environment;
        private readonly string initialNxm, smokeReport, identity;
        private NamedPipeServerStream activePipe;
        private bool closing, smokeStarted;
        private bool pipeStarted, initialLinkHandled;
        private int automaticRecoveries;
        private ulong navigationId;
        private readonly string uiFolder;
        private readonly bool startupFixture;
        private readonly BrowserStartup startup = new BrowserStartup();
        private readonly System.Windows.Forms.Timer startupTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly Panel startupPanel = new Panel();
        private readonly Label startupTitle = new Label(), startupMessage = new Label();
        private readonly Button retryInterface = new Button();
        internal bool InterfaceReady { get { return startup.Ready; } }
        internal bool StartupFallbackVisible { get { return startupPanel.Visible; } }
        internal string StartupFailure { get { return startup.Failure; } }
        internal WebView2 InterfaceBrowser { get { return web; } }
        protected override bool ShowWithoutActivation { get { return startupFixture || base.ShowWithoutActivation; } }
        private readonly System.Collections.Generic.List<BrowserForm> browsers = new System.Collections.Generic.List<BrowserForm>();
        private readonly System.Collections.Generic.Queue<string[]> authorizations = new System.Collections.Generic.Queue<string[]>();
        private BrowserForm authorizationBrowser;
        private string currentAuthorizationId;
        internal MainForm(Storage store, string nxm, string smoke, string pipeIdentity, string interfaceFolder = null, bool isolatedStartupFixture = false) {
            storage = store; initialNxm = nxm; smokeReport = smoke; identity = pipeIdentity;
            uiFolder = interfaceFolder ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui"); startupFixture = isolatedStartupFixture;
            Text = "NexusM"; Width = 1340; Height = 880; MinimumSize = new Size(980, 680); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(22, 23, 27); Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            CreateInterface(); CreateStartupPanel();
            Controller = new Controller(this, storage);
            Shown += async delegate { await Initialize(); };
            startupTimer.Tick += delegate { CheckStartupTimeout(DateTime.UtcNow); };
            FormClosing += delegate { closing = true; startupTimer.Stop(); startupTimer.Dispose(); foreach (var browser in browsers.ToArray()) browser.Close(); Controller.Dispose(); if (activePipe != null) activePipe.Dispose(); };
        }
        private void CreateInterface() {
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web); web.SendToBack();
        }
        private void CreateStartupPanel() {
            startupPanel.Dock = DockStyle.Fill; startupPanel.BackColor = BackColor;
            var contents = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 560 };
            startupTitle.AutoSize = true; startupTitle.ForeColor = Color.White; startupTitle.Font = new Font("Segoe UI", 19F); startupTitle.Text = "Opening NexusM...";
            startupMessage.AutoSize = true; startupMessage.MaximumSize = new Size(540, 0); startupMessage.ForeColor = Color.FromArgb(175, 180, 190); startupMessage.Font = new Font("Segoe UI", 10F); startupMessage.Margin = new Padding(0, 12, 0, 20); startupMessage.Text = "Loading your window. Your saved library will appear shortly.";
            retryInterface.Text = "Retry"; retryInterface.Width = 140; retryInterface.Height = 36; retryInterface.Font = new Font("Segoe UI", 10F); retryInterface.Visible = false;
            retryInterface.Click += async delegate { await RetryInterface(); };
            contents.Controls.Add(startupTitle); contents.Controls.Add(startupMessage); contents.Controls.Add(retryInterface); startupPanel.Controls.Add(contents);
            startupPanel.Resize += delegate { contents.Left = Math.Max(20, (startupPanel.ClientSize.Width - contents.Width) / 2); contents.Top = Math.Max(20, (startupPanel.ClientSize.Height - contents.Height) / 2); };
            Controls.Add(startupPanel); startupPanel.BringToFront();
        }
        private bool CurrentInterface(WebView2 view, int generation) { return !closing && !IsDisposed && ReferenceEquals(web, view) && generation == startup.Generation && String.IsNullOrEmpty(startup.Failure); }
        private void FailInterface(int generation, string code, string message) {
            if (closing || IsDisposed || !startup.Fail(generation, code)) return;
            startupTimer.Stop(); startupTitle.Text = "NexusM couldn't load its window"; startupMessage.Text = message;
            retryInterface.Visible = true; retryInterface.Enabled = true; startupPanel.Visible = true; web.Visible = false; startupPanel.BringToFront();
            StartupDiagnostics.Record(storage.Root, code);
            if (smokeReport != null) { File.WriteAllText(smokeReport, Data.Json.Serialize(Data.Obj("passed", false, "error", code))); Close(); }
        }
        private void ReadyInterface(int generation) {
            if (closing || IsDisposed || !startup.Complete(generation)) return;
            startupTimer.Stop(); startupPanel.Visible = false; web.Visible = true; web.BringToFront();
        }
        internal void CheckStartupTimeout(DateTime now) {
            if (startup.TimedOut(now, TimeSpan.FromSeconds(20))) FailInterface(startup.Generation, "startup-timeout", "NexusM is taking longer than expected to open its window. Select Retry to try again.");
        }
        internal async Task RetryInterface() {
            if (closing || IsDisposed) return;
            // Recreate only the main view. The controller, downloads, account,
            // profile, and other Nexus windows keep their existing lifetimes.
            startup.Begin(DateTime.UtcNow);
            try {
                var previous = web; Controls.Remove(previous); previous.Dispose(); CreateInterface();
                await Initialize();
            } catch (Exception) { if (!closing && !IsDisposed) FailInterface(startup.Generation, "ui-retry-failed", "The window could not be reloaded. Select Retry to try again."); }
        }
        private async Task Initialize() {
            int generation = startup.Begin(DateTime.UtcNow); var view = web;
            startupTitle.Text = "Opening NexusM..."; startupMessage.Text = "Loading your window. Your saved library will appear shortly.";
            retryInterface.Visible = false; startupPanel.Visible = true; startupPanel.BringToFront(); startupTimer.Start();
            try {
                if (!File.Exists(Path.Combine(uiFolder, "index.html"))) throw new FileNotFoundException("The NexusM interface files are missing.");
                var options = new CoreWebView2EnvironmentOptions("--disk-cache-size=16777216 --media-cache-size=1048576");
                var selectedEnvironment = environment ?? await CoreWebView2Environment.CreateAsync(null, Path.Combine(storage.Root, "WebView"), options);
                if (!CurrentInterface(view, generation)) return; environment = selectedEnvironment;
                await view.EnsureCoreWebView2Async(selectedEnvironment);
                if (!CurrentInterface(view, generation)) return;
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = smokeReport != null || startupFixture;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.SetVirtualHostNameToFolderMapping("nexusm.local", uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += async delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
                    if (!ReferenceEquals(view, web) || closing) return;
                    Uri source; if (!Uri.TryCreate(e.Source, UriKind.Absolute, out source) || source.Host != "nexusm.local" || source.Scheme != "https") return;
                    object id = null;
                    ulong requestNavigation = navigationId;
                    try {
                        var message = Data.Map(Data.Json.DeserializeObject(e.WebMessageAsJson)); id = Data.Get(message, "id");
                        string action = Data.Str(message, "action");
                        if (action == "uiReady") { ReadyInterface(startup.Generation); return; }
                        if (action == "uiStartupError") { FailInterface(startup.Generation, "ui-startup-error", "The interface could not finish loading. Select Retry to load it again."); return; }
                        // Smoke tests read the real cache but never launch a whole-library update.
                        var result = smokeReport != null && action == "initialize" ? storage.PublicState() : await Controller.Handle(action, Data.Get(message, "payload"));
                        if (!ReferenceEquals(view, web) || closing || requestNavigation != navigationId) return;
                        Reply(Data.Obj("id", id, "ok", true, "result", result));
                    } catch (Exception ex) { if (ReferenceEquals(view, web) && !closing && requestNavigation == navigationId) Reply(Data.Obj("id", id, "ok", false, "error", ex.Message)); }
                };
                core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    if (!ReferenceEquals(view, web) || closing) { e.Cancel = true; return; }
                    Uri target; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out target) || target.Scheme != "https" || target.Host != "nexusm.local") { e.Cancel = true; if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenSafeUrl(e.Uri); return; }
                    navigationId = e.NavigationId;
                    startup.Navigate(DateTime.UtcNow); startupTimer.Start();
                };
                core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; OpenSafeUrl(e.Uri); };
                core.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                    if (!CurrentInterface(view, generation) || e.NavigationId != navigationId) return;
                    if (!e.IsSuccess) { FailInterface(generation, "navigation-" + e.WebErrorStatus, "The app page could not load. Select Retry to try again."); return; }
                    if (!startup.Loading && !startup.Ready) return;
                    try {
                        string ready = await view.ExecuteScriptAsync("Boolean(window.NexusM && window.NexusM.ready === true)");
                        if (!CurrentInterface(view, generation) || e.NavigationId != navigationId) return;
                        if (ready == "true") ReadyInterface(generation);
                        else if (!startup.Ready) { FailInterface(generation, "ui-not-ready", "The interface could not finish loading. Select Retry to load it again."); return; }
                        if (smokeReport != null && !smokeStarted) { smokeStarted = true; await SmokeTest(); }
                    } catch (Exception) { if (CurrentInterface(view, generation)) FailInterface(generation, "ui-ready-check-failed", "The app window stopped responding. Select Retry to load it again."); }
                };
                core.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e) {
                    if (!ReferenceEquals(view, web) || closing) return;
                    bool browserExited = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
                    bool rendererExited = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited;
                    bool unresponsive = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;
                    if (!browserExited && !rendererExited && !unresponsive) return;
                    FailInterface(startup.Generation, "process-" + e.ProcessFailedKind, "The app window stopped responding. Select Retry to load it again. Your saved library and downloads are kept.");
                    if (closing || IsDisposed) return;
                    if (browserExited) foreach (var browser in browsers.ToArray()) browser.Close();
                    if (!unresponsive && automaticRecoveries++ == 0) BeginInvoke(new Action(async delegate { await RetryInterface(); }));
                };
                core.Navigate("https://nexusm.local/index.html");
                if (smokeReport == null && !startupFixture && !pipeStarted) { pipeStarted = true; StartPipe(); }
                if (!startupFixture && !initialLinkHandled && !String.IsNullOrEmpty(initialNxm)) { initialLinkHandled = true; ReceiveNxm(initialNxm); }
            } catch (Exception ex) {
                if (!CurrentInterface(view, generation)) return;
                FailInterface(generation, ex is FileNotFoundException ? "ui-files-missing" : "webview-initialization-failed", ex is FileNotFoundException ? "The app files could not be found. Extract the complete NexusM download, then select Retry." : "The browser component could not start. Select Retry; if this repeats, install or repair Microsoft Edge WebView2 Runtime.");
            }
        }
        internal void OnUi(Action action) { if (closing || IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(action); } catch { } } else action(); }
        private void Reply(object message) { OnUi(delegate { try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(Data.Json.Serialize(message)); } catch (InvalidOperationException) { FailInterface(startup.Generation, "ui-message-failed", "The app window stopped responding. Select Retry to load it again."); } catch (System.Runtime.InteropServices.COMException) { FailInterface(startup.Generation, "ui-message-failed", "The app window stopped responding. Select Retry to load it again."); } }); }
        internal void Emit(string name, object value) { Reply(Data.Obj("event", name, "data", value)); }
        internal void OpenSafeUrl(string input) {
            Uri uri; if (!Uri.TryCreate(input, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        internal void OpenAuthorization(string url, string id) {
            if (closing) return;
            if (authorizationBrowser != null && currentAuthorizationId == id) { authorizationBrowser.Reveal("Continue the selected download on Nexus."); authorizationBrowser.Activate(); return; }
            foreach (var queued in authorizations) if (queued[1] == id) return;
            authorizations.Enqueue(new[] { url, id }); NextAuthorization();
        }
        internal void CancelAuthorization(string id) {
            int count = authorizations.Count;
            for (int i = 0; i < count; i++) { var queued = authorizations.Dequeue(); if (queued[1] != id) authorizations.Enqueue(queued); }
            if (authorizationBrowser != null && currentAuthorizationId == id) authorizationBrowser.Close();
        }
        internal void NextAuthorization() {
            if (closing || authorizationBrowser != null || !Controller.CanStartAuthorization) return;
            while (authorizations.Count > 0) {
                var next = authorizations.Dequeue();
                var row = storage.Downloads.Find(x => Data.Str(x, "id") == next[1]);
                if (row == null || Data.Str(row, "status") != "waiting-authorization") continue;
                BrowserForm nextBrowser = null;
                try {
                    nextBrowser = OpenBrowser(next[0], false, "", next[1], false);
                    authorizationBrowser = nextBrowser; currentAuthorizationId = next[1];
                    nextBrowser.FormClosed += delegate {
                        if (ReferenceEquals(authorizationBrowser, nextBrowser)) { authorizationBrowser = null; currentAuthorizationId = null; }
                        if (!closing) {
                            if (!nextBrowser.DownloadCaptured) Controller.AuthorizationFailed(next[1], "The Nexus authorization window was closed. Retry this download when ready.", "authorization-closed");
                            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(NextAuthorization));
                        }
                    };
                    nextBrowser.Show(this);
                    break;
                } catch {
                    authorizationBrowser = null; currentAuthorizationId = null;
                    if (nextBrowser != null) { nextBrowser.Close(); nextBrowser.Dispose(); }
                    Controller.AuthorizationFailed(next[1], "The Nexus authorization window could not start. Retry this download.", "authorization-failed");
                }
            }
        }
        internal void OpenHistory(string domain) { OpenBrowser(BrowserImport.HistoryUrl(domain), true, domain); }
        private BrowserForm OpenBrowser(string url, bool history, string domain, string downloadId = null, bool show = true) {
            if (environment == null) throw new InvalidOperationException("The browser is still starting. Try again in a moment.");
            var browser = new BrowserForm(this, environment, url, history, domain, storage, downloadId);
            browsers.Add(browser); browser.FormClosed += delegate { browsers.Remove(browser); }; if (show) browser.Show(this); return browser;
        }
        internal async void ReceiveNxm(string uri) {
            try { WindowState = FormWindowState.Normal; Activate(); await Controller.ReceiveNxm(uri); }
            catch (Exception ex) { Emit("notice", Data.Obj("message", ex.Message)); MessageBox.Show(this, ex.Message, "NexusM download", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private async void StartPipe() {
            while (!closing) {
                try {
                    using (var pipe = new NamedPipeServerStream(Program.PipeName + identity, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)) {
                        activePipe = pipe; await pipe.WaitForConnectionAsync();
                        using (var reader = new StreamReader(pipe)) {
                            string line = await reader.ReadLineAsync();
                            if (line != null && line.Length <= 8192 && line.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) ReceiveNxm(line);
                            else { WindowState = FormWindowState.Normal; Activate(); }
                        }
                    }
                } catch { if (closing) break; }
                await Task.Delay(500);
            }
        }
        internal async Task ClearCache() {
            if (web.CoreWebView2 != null) await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
        }
        private async Task SmokeTest() {
            try {
                await Task.Delay(1800);
                var state = await Controller.Handle("getState", null);
                await web.ExecuteScriptAsync("window.NexusM && window.NexusM.setState(" + Data.Json.Serialize(state) + ");");
                await Task.Delay(800);
                string result = await web.ExecuteScriptAsync("JSON.stringify({title:document.title,bodyLength:document.body.innerText.length,buttons:document.querySelectorAll('button').length,gameNamesPresent:document.body.innerText.includes('Baldur'),errors:window.__nexusTestErrors||[]})");
                object decoded = Data.Json.DeserializeObject(result); var info = Data.Map(Data.Json.DeserializeObject(Convert.ToString(decoded)));
                bool pass = Data.Num(info, "bodyLength") > 200 && Data.Num(info, "buttons") > 5 && Data.Bool(info, "gameNamesPresent");
                object uiChecks = null;
                string fixture = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)), "tests", "ui-checks.js");
                if (File.Exists(fixture)) {
                    await web.ExecuteScriptAsync(File.ReadAllText(fixture));
                    for (int i = 0; i < 100; i++) {
                        await Task.Delay(150);
                        string rawChecks = await web.ExecuteScriptAsync("JSON.stringify(window.__uiChecks || {status:'waiting'})");
                        uiChecks = Data.Json.DeserializeObject(Convert.ToString(Data.Json.DeserializeObject(rawChecks)));
                        if (Data.Str(uiChecks, "status") == "complete") break;
                    }
                    pass = pass && Data.Str(uiChecks, "status") == "complete" && Data.Bool(uiChecks, "passed");
                }
                object liveDetail = await Controller.Handle("getModDetails", Data.Obj("gameDomain", "baldursgate3", "modId", 3423));
                var liveState = new System.Collections.Generic.Dictionary<string, object>(Data.Map(state)); liveState["mods"] = new[] { Data.Get(liveDetail, "mod") };
                await web.ExecuteScriptAsync("window.NexusM.setState(" + Data.Json.Serialize(liveState) + "); window.NexusM.showMod('baldursgate3:3423');");
                bool detailReady = false;
                for (int i = 0; i < 60; i++) {
                    await Task.Delay(100);
                    string count = await web.ExecuteScriptAsync("document.querySelectorAll('.file-card').length");
                    int number; if (Int32.TryParse(count, out number) && number > 0) { detailReady = true; break; }
                }
                pass = pass && detailReady;
                bool imageReady = false;
                for (int i = 0; i < 40 && detailReady; i++) {
                    string imageLoaded = await web.ExecuteScriptAsync("!!document.querySelector('.detail-hero img') && document.querySelector('.detail-hero img').naturalWidth > 0");
                    if (imageLoaded == "true") { imageReady = true; break; }
                    await Task.Delay(100);
                }
                pass = pass && imageReady;
                if (detailReady) using (var image = File.Create(Path.Combine(Path.GetDirectoryName(smokeReport), "ui-detail.png"))) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                await web.ExecuteScriptAsync("document.getElementById('detail-close').click(); window.NexusM.setState(" + Data.Json.Serialize(state) + ");");
                bool nxmCaptured = await ProbeNxmCapture(); pass = pass && nxmCaptured;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(smokeReport)));
                using (var image = File.Create(Path.ChangeExtension(smokeReport, ".png"))) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                File.WriteAllText(smokeReport, Data.Json.Serialize(Data.Obj("passed", pass, "ui", info, "games", Data.List(Data.Get(state, "games")).Count, "checks", uiChecks, "liveFileChooser", detailReady, "liveModImage", imageReady, "nxmCaptureWithoutRegistration", nxmCaptured)));
            } catch (Exception ex) { File.WriteAllText(smokeReport, Data.Json.Serialize(Data.Obj("passed", false, "error", ex.ToString()))); }
            Close();
        }
        private async Task<bool> ProbeNxmCapture() {
            using (var probe = new WebView2 { Width = 1, Height = 1, Left = -100, Top = -100 }) {
                Controls.Add(probe); probe.SendToBack(); await probe.EnsureCoreWebView2Async(environment);
                var capture = new TaskCompletionSource<bool>();
                probe.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri.StartsWith("nxm://")) { e.Cancel = true; capture.TrySetResult(true); } };
                probe.CoreWebView2.LaunchingExternalUriScheme += delegate(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) { e.Cancel = true; if (e.Uri.StartsWith("nxm://")) capture.TrySetResult(true); };
                probe.CoreWebView2.NavigationCompleted += async delegate { if (!capture.Task.IsCompleted) await probe.ExecuteScriptAsync("location.href='nxm://baldursgate3/mods/1/files/2?key=integration-test&expires=1&user_id=1'"); };
                probe.CoreWebView2.NavigateToString("<!doctype html><title>Local protocol handoff check</title>");
                await Task.WhenAny(capture.Task, Task.Delay(5000));
                Controls.Remove(probe); return capture.Task.IsCompleted && capture.Task.Result;
            }
        }
    }

    internal sealed class BrowserForm : Form
    {
        private readonly WebView2 browser = new WebView2();
        private readonly MainForm owner;
        private readonly CoreWebView2Environment environment;
        private readonly string url;
        private readonly bool history;
        private readonly ComboBox gameBox = new ComboBox();
        private readonly Button import = new Button();
        private readonly Label status = new Label();
        private readonly System.Windows.Forms.Timer downloadTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly string downloadId, flowScript;
        private bool importing, polling, navigating, captured, revealed, authorizationEnded;
        private DateTime flowStarted = DateTime.UtcNow;
        private readonly int authorizationTimeoutSeconds;
        internal bool DownloadCaptured { get { return captured; } }
        protected override bool ShowWithoutActivation { get { return !history && !revealed; } }
        internal BrowserForm(MainForm parent, CoreWebView2Environment env, string initialUrl, bool isHistory, string selectedDomain, Storage storage, string selectedDownload = null) {
            owner = parent; environment = env; url = initialUrl; history = isHistory;
            authorizationTimeoutSeconds = (int)Math.Max(300, Math.Min(1200, Data.Num(storage.Settings, "downloadTimeoutSeconds") * 2));
            downloadId = selectedDownload;
            if (!history && !String.IsNullOrEmpty(downloadId)) {
                string[] parts = downloadId.Split(':'); long modId, fileId;
                if (parts.Length == 3 && Int64.TryParse(parts[1], out modId) && Int64.TryParse(parts[2], out fileId)) flowScript = FreeDownloadFlow.Script(parts[0], modId, fileId);
            }
            if (flowScript != null) { Opacity = 0; ShowInTaskbar = false; }
            Text = history ? "NexusM — import download history" : "NexusM — authorize free download";
            Icon = parent.Icon;
            Size = new Size(1120, 840); MinimumSize = new Size(800, 600); StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(32, 33, 38); ForeColor = Color.White; Font = new Font("Segoe UI", 9F);
            browser.Dock = DockStyle.Fill; Controls.Add(browser);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = history ? 82 : 55, Padding = new Padding(10), BackColor = BackColor, AutoSize = false };
            status.AutoSize = false; status.Width = 760; status.Height = 25;
            status.Text = history ? "Sign in to Nexus here, open your download history, then click Import history." : "Preparing your download through Nexus. Its normal free-download countdown still applies.";
            toolbar.Controls.Add(status);
            if (history) {
                gameBox.DropDownStyle = ComboBoxStyle.DropDownList; gameBox.Width = 220; gameBox.Items.Add(new GameChoice("", "All games"));
                foreach (var game in storage.Games) gameBox.Items.Add(new GameChoice(Data.Str(game, "domain"), Data.Str(game, "name")));
                gameBox.SelectedIndex = 0;
                for (int i = 0; i < gameBox.Items.Count; i++) if (((GameChoice)gameBox.Items[i]).Domain == selectedDomain) gameBox.SelectedIndex = i;
                import.Text = "Import history"; import.AutoSize = true; import.BackColor = Color.FromArgb(242, 145, 49); import.ForeColor = Color.Black; import.FlatStyle = FlatStyle.Flat;
                toolbar.Controls.Add(gameBox); toolbar.Controls.Add(import);
                import.Click += async delegate { await Import(); };
                gameBox.SelectedIndexChanged += delegate { if (browser.CoreWebView2 != null) browser.CoreWebView2.Navigate(BrowserImport.HistoryUrl(((GameChoice)gameBox.SelectedItem).Domain)); };
            }
            var external = new Button { Text = "Open in browser", AutoSize = true, FlatStyle = FlatStyle.Flat };
            external.Click += delegate { owner.OpenSafeUrl(browser.Source == null ? url : browser.Source.AbsoluteUri); };
            toolbar.Controls.Add(external); Controls.Add(toolbar);
            downloadTimer.Tick += async delegate { CheckAuthorizationTimeout(DateTime.UtcNow); if (!authorizationEnded) await AdvanceDownload(); };
            FormClosed += delegate { downloadTimer.Stop(); downloadTimer.Dispose(); };
            Shown += async delegate { if (flowScript != null) downloadTimer.Start(); await Initialize(); };
        }
        private async Task Initialize() {
            try {
                await browser.EnsureCoreWebView2Async(environment);
                if (IsDisposed || authorizationEnded) return;
                browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
                browser.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    if (CaptureNxm(e.Uri)) { e.Cancel = true; return; }
                    navigating = true;
                    Uri target; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out target) || target.Scheme != "https") e.Cancel = true;
                };
                browser.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                    navigating = false;
                    if (!e.IsSuccess && !captured) Reveal("Nexus could not load the download page. Continue here or try again.");
                };
                browser.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) {
                    e.Handled = true; if (!CaptureNxm(e.Uri)) { Uri target; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out target) && target.Scheme == "https") browser.CoreWebView2.Navigate(target.AbsoluteUri); }
                };
                browser.CoreWebView2.LaunchingExternalUriScheme += delegate(object sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e) { if (CaptureNxm(e.Uri)) e.Cancel = true; };
                browser.CoreWebView2.Navigate(url);
            } catch {
                if (history) Reveal("The Nexus browser could not start. Close this window and try importing again.");
                else EndAuthorization("authorization-failed", "The Nexus authorization window could not start. Retry this download.");
            }
        }
        internal void CheckAuthorizationTimeout(DateTime utcNow) {
            if (history || captured || authorizationEnded || IsDisposed || (utcNow - flowStarted).TotalSeconds < authorizationTimeoutSeconds) return;
            EndAuthorization("authorization-timeout", "Nexus did not authorize this file before the timeout. The download is kept; retry it after signing in or completing the browser check.");
        }
        private void EndAuthorization(string code, string message) {
            if (history || captured || authorizationEnded || IsDisposed) return;
            authorizationEnded = true; downloadTimer.Stop();
            owner.Controller.AuthorizationFailed(downloadId, message, code);
            Close();
        }
        private async Task AdvanceDownload() {
            if (polling || captured || authorizationEnded || IsDisposed || flowScript == null || browser.CoreWebView2 == null) return;
            if (navigating) { if ((DateTime.UtcNow - flowStarted).TotalSeconds > 45) Reveal("Nexus is taking a while to load. Continue in this window."); return; }
            polling = true;
            try {
                string raw = await browser.ExecuteScriptAsync(flowScript);
                if (captured || authorizationEnded || IsDisposed) return;
                var result = Data.Map(Data.Json.DeserializeObject(raw));
                string message = Data.Str(result, "message"), phase = Data.Str(result, "status");
                if (phase == "needs-user" || phase == "error") Reveal(message);
                else if (phase == "waiting") {
                    status.Text = message;
                    owner.Controller.AuthorizationStatus(downloadId, message, revealed);
                    if ((DateTime.UtcNow - flowStarted).TotalSeconds > 120 && !revealed) Reveal("Nexus has not started this download yet. Continue in this window.");
                }
            } catch { if (!captured && !authorizationEnded && !IsDisposed) Reveal("Continue the download in this Nexus window."); }
            finally { polling = false; }
        }
        internal void Reveal(string message) {
            if (IsDisposed || captured || authorizationEnded) return;
            status.Text = message;
            if (!history) owner.Controller.AuthorizationStatus(downloadId, message, true);
            if (!revealed) { revealed = true; Opacity = 1; ShowInTaskbar = true; WindowState = FormWindowState.Normal; Show(); Activate(); }
        }
        private bool CaptureNxm(string uri) {
            if (!uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return false;
            if (authorizationEnded || captured || IsDisposed) return true;
            if (!String.IsNullOrEmpty(downloadId)) {
                try {
                    if (DownloadRequest.ParseNxm(uri, "").Id != downloadId) {
                        EndAuthorization("authorization-mismatch", "Nexus returned a link for a different file. Retry the selected download."); return true;
                    }
                } catch (ArgumentException) {
                    EndAuthorization("authorization-invalid", "Nexus returned an invalid download link. Retry the selected download."); return true;
                }
            }
            captured = true; downloadTimer.Stop();
            owner.ReceiveNxm(uri); BeginInvoke(new Action(Close)); return true;
        }
        private async Task Import() {
            if (importing || browser.CoreWebView2 == null) return;
            importing = true; import.Enabled = false;
            try {
                string domain = ((GameChoice)gameBox.SelectedItem).Domain;
                await browser.ExecuteScriptAsync(BrowserImport.StartHistoryScript(domain));
                object result = null;
                for (int i = 0; i < 200; i++) {
                    await Task.Delay(300); if (IsDisposed) return;
                    string raw = await browser.ExecuteScriptAsync(BrowserImport.PollHistoryScript);
                    result = Data.Json.DeserializeObject(raw);
                    if (result is string) result = Data.Json.DeserializeObject((string)result);
                    status.Text = Data.Str(result, "message");
                    string phase = Data.Str(result, "status");
                    if (phase == "complete") break;
                    if (phase == "error") throw new InvalidOperationException(Data.Str(result, "message"));
                }
                if (Data.Str(result, "status") != "complete") throw new InvalidOperationException("History import timed out. Wait for the page to load and try again.");
                var items = Data.List(Data.Get(result, "items"));
                if (items.Count == 0) throw new InvalidOperationException("No downloaded mods were found. Check that you are signed in and viewing Download history.");
                await owner.Controller.ImportHistory(items);
                status.Text = "Imported " + items.Count + " mods." + (Data.Bool(result, "partial") ? " Only visible rows were available. Import other pages too." : " You can close this window.");
            } catch (Exception ex) { status.Text = ex.Message; }
            finally { importing = false; if (!IsDisposed) import.Enabled = true; }
        }
        private sealed class GameChoice {
            internal readonly string Domain, Name;
            internal GameChoice(string domain, string name) { Domain = domain; Name = name; }
            public override string ToString() { return Name; }
        }
    }
}
