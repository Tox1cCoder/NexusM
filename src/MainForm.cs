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
        private readonly WebView2 web = new WebView2();
        internal readonly Controller Controller;
        private readonly Storage storage;
        private CoreWebView2Environment environment;
        private readonly string initialNxm, smokeReport, identity;
        private NamedPipeServerStream activePipe;
        private bool closing, smokeStarted;
        private readonly System.Collections.Generic.List<BrowserForm> browsers = new System.Collections.Generic.List<BrowserForm>();
        private readonly System.Collections.Generic.Queue<string[]> authorizations = new System.Collections.Generic.Queue<string[]>();
        private BrowserForm authorizationBrowser;
        private string currentAuthorizationId;
        internal MainForm(Storage store, string nxm, string smoke, string pipeIdentity) {
            storage = store; initialNxm = nxm; smokeReport = smoke; identity = pipeIdentity;
            Text = "NexusM"; Width = 1340; Height = 880; MinimumSize = new Size(980, 680); StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(22, 23, 27); Icon = SystemIcons.Application;
            web.Dock = DockStyle.Fill; web.DefaultBackgroundColor = BackColor; Controls.Add(web);
            Controller = new Controller(this, storage);
            Shown += async delegate { await Initialize(); };
            FormClosing += delegate { closing = true; foreach (var browser in browsers.ToArray()) browser.Close(); Controller.Dispose(); if (activePipe != null) activePipe.Dispose(); };
        }
        private async Task Initialize() {
            try {
                var options = new CoreWebView2EnvironmentOptions("--disk-cache-size=16777216 --media-cache-size=1048576");
                environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(storage.Root, "WebView"), options);
                await web.EnsureCoreWebView2Async(environment);
                web.CoreWebView2.Settings.AreDevToolsEnabled = smokeReport != null;
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                web.CoreWebView2.SetVirtualHostNameToFolderMapping("nexusm.local", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
                web.CoreWebView2.WebMessageReceived += async delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
                    Uri source; if (!Uri.TryCreate(e.Source, UriKind.Absolute, out source) || source.Host != "nexusm.local" || source.Scheme != "https") return;
                    object id = null;
                    try {
                        var message = Data.Map(Data.Json.DeserializeObject(e.WebMessageAsJson)); id = Data.Get(message, "id");
                        // Smoke tests read the real cache but never launch a whole-library update.
                        var result = smokeReport != null && Data.Str(message, "action") == "initialize" ? storage.PublicState() : await Controller.Handle(Data.Str(message, "action"), Data.Get(message, "payload"));
                        Reply(Data.Obj("id", id, "ok", true, "result", result));
                    } catch (Exception ex) { Reply(Data.Obj("id", id, "ok", false, "error", ex.Message)); }
                };
                web.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) {
                    Uri target; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out target) || target.Scheme != "https" || target.Host != "nexusm.local") { e.Cancel = true; if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenSafeUrl(e.Uri); }
                };
                web.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; OpenSafeUrl(e.Uri); };
                web.CoreWebView2.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                    if (smokeReport != null && !smokeStarted && e.IsSuccess && web.Source != null && web.Source.Host == "nexusm.local") { smokeStarted = true; await SmokeTest(); }
                };
                web.CoreWebView2.Navigate("https://nexusm.local/index.html");
                if (smokeReport == null) StartPipe();
                if (!String.IsNullOrEmpty(initialNxm)) ReceiveNxm(initialNxm);
            } catch (Exception ex) {
                if (smokeReport != null) { File.WriteAllText(smokeReport, Data.Json.Serialize(Data.Obj("passed", false, "error", ex.Message))); Close(); return; }
                MessageBox.Show(this, "NexusM could not open its window. Install or repair Microsoft Edge WebView2 Runtime, then try again.\n\n" + ex.Message, "NexusM", MessageBoxButtons.OK, MessageBoxIcon.Error); Close();
            }
        }
        internal void OnUi(Action action) { if (closing || IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(action); } catch { } } else action(); }
        private void Reply(object message) { OnUi(delegate { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(Data.Json.Serialize(message)); }); }
        internal void Emit(string name, object value) { Reply(Data.Obj("event", name, "data", value)); }
        internal void OpenSafeUrl(string input) {
            Uri uri; if (!Uri.TryCreate(input, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !String.IsNullOrEmpty(uri.UserInfo)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        internal void OpenAuthorization(string url, string id) { if (authorizationBrowser != null && currentAuthorizationId == id) { authorizationBrowser.Reveal("Continue the selected download on Nexus."); authorizationBrowser.Activate(); return; } authorizations.Enqueue(new[] { url, id }); NextAuthorization(); }
        internal void CancelAuthorization(string id) { if (authorizationBrowser != null && currentAuthorizationId == id) authorizationBrowser.Close(); }
        internal void NextAuthorization() {
            if (closing || authorizationBrowser != null || Controller.HasActiveTransfers) return;
            while (authorizations.Count > 0) {
                var next = authorizations.Dequeue();
                var row = storage.Downloads.Find(x => Data.Str(x, "id") == next[1]);
                if (row == null || Data.Str(row, "status") != "waiting-authorization") continue;
                authorizationBrowser = OpenBrowser(next[0], false, "", next[1]);
                currentAuthorizationId = next[1];
                authorizationBrowser.FormClosed += delegate { authorizationBrowser = null; currentAuthorizationId = null; if (!closing) BeginInvoke(new Action(NextAuthorization)); };
                break;
            }
        }
        internal void OpenHistory(string domain) { OpenBrowser(BrowserImport.HistoryUrl(domain), true, domain); }
        private BrowserForm OpenBrowser(string url, bool history, string domain, string downloadId = null) {
            if (environment == null) throw new InvalidOperationException("The browser is still starting. Try again in a moment.");
            var browser = new BrowserForm(this, environment, url, history, domain, storage, downloadId);
            browsers.Add(browser); browser.FormClosed += delegate { browsers.Remove(browser); }; browser.Show(this); return browser;
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
        private bool importing, polling, navigating, captured, revealed;
        private DateTime flowStarted;
        protected override bool ShowWithoutActivation { get { return !history && !revealed; } }
        internal BrowserForm(MainForm parent, CoreWebView2Environment env, string initialUrl, bool isHistory, string selectedDomain, Storage storage, string selectedDownload = null) {
            owner = parent; environment = env; url = initialUrl; history = isHistory;
            downloadId = selectedDownload;
            if (!history && !String.IsNullOrEmpty(downloadId)) {
                string[] parts = downloadId.Split(':'); long modId, fileId;
                if (parts.Length == 3 && Int64.TryParse(parts[1], out modId) && Int64.TryParse(parts[2], out fileId)) flowScript = FreeDownloadFlow.Script(parts[0], modId, fileId);
            }
            if (flowScript != null) { Opacity = 0; ShowInTaskbar = false; }
            Text = history ? "NexusM — import download history" : "NexusM — authorize free download";
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
            downloadTimer.Tick += async delegate { await AdvanceDownload(); };
            FormClosed += delegate { downloadTimer.Stop(); downloadTimer.Dispose(); };
            Shown += async delegate { await Initialize(); };
        }
        private async Task Initialize() {
            try {
                await browser.EnsureCoreWebView2Async(environment);
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
                flowStarted = DateTime.UtcNow;
                if (flowScript != null) downloadTimer.Start();
                browser.CoreWebView2.Navigate(url);
            } catch (Exception ex) { Reveal(ex.Message); }
        }
        private async Task AdvanceDownload() {
            if (polling || captured || IsDisposed || flowScript == null || browser.CoreWebView2 == null) return;
            if (navigating) { if ((DateTime.UtcNow - flowStarted).TotalSeconds > 45) Reveal("Nexus is taking a while to load. Continue in this window."); return; }
            polling = true;
            try {
                string raw = await browser.ExecuteScriptAsync(flowScript);
                if (captured || IsDisposed) return;
                var result = Data.Map(Data.Json.DeserializeObject(raw));
                string message = Data.Str(result, "message"), phase = Data.Str(result, "status");
                if (phase == "needs-user" || phase == "error") Reveal(message);
                else if (phase == "waiting") {
                    status.Text = message;
                    owner.Controller.AuthorizationStatus(downloadId, message, revealed);
                    if ((DateTime.UtcNow - flowStarted).TotalSeconds > 120 && !revealed) Reveal("Nexus has not started this download yet. Continue in this window.");
                }
            } catch { if (!captured && !IsDisposed) Reveal("Continue the download in this Nexus window."); }
            finally { polling = false; }
        }
        internal void Reveal(string message) {
            if (IsDisposed || captured) return;
            status.Text = message;
            if (!history) owner.Controller.AuthorizationStatus(downloadId, message, true);
            if (!revealed) { revealed = true; Opacity = 1; ShowInTaskbar = true; WindowState = FormWindowState.Normal; Show(); Activate(); }
        }
        private bool CaptureNxm(string uri) {
            if (!uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return false;
            if (captured) return true;
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
