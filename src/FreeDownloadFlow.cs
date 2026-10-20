using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusM
{
    // Advances the website's ordinary free-download UI. Nexus itself creates the
    // signed nxm link after its countdown; this code never creates tokens, reads
    // hidden download URLs, changes timers, or calls private download endpoints.
    internal static class FreeDownloadFlow
    {
        public static string Script(string gameDomain, long modId, long fileId)
        {
            string domain = (gameDomain ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(domain, "^[a-z0-9_-]+$") || modId <= 0 || fileId <= 0 ||
                modId > 9007199254740991L || fileId > 9007199254740991L)
                throw new ArgumentException("A valid Nexus game, mod, and file are required.");
            return AdvanceScript.Replace("__GAME__", "'" + domain + "'")
                .Replace("__MOD__", modId.ToString(CultureInfo.InvariantCulture))
                .Replace("__FILE__", fileId.ToString(CultureInfo.InvariantCulture));
        }

        private const string AdvanceScript = @"
(function () {
    'use strict';
    const game = __GAME__, mod = __MOD__, file = __FILE__;
    const answer = (status, message) => ({status: status, message: message});
    const trusted = url => url.protocol === 'https:' && /^(www\.)?nexusmods\.com$/i.test(url.hostname) && !url.username && !url.password;
    const here = new URL(location.href);
    if (here.protocol === 'https:' && here.hostname === 'users.nexusmods.com')
        return answer('needs-user', 'Sign in to Nexus to continue this download.');
    if (!trusted(here)) return answer('needs-user', 'Open the selected file on Nexus to continue.');
    const visible = el => !!el && !el.hidden && el.getClientRects().length > 0 &&
        getComputedStyle(el).display !== 'none' && getComputedStyle(el).visibility !== 'hidden';
    const enabled = el => visible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true' &&
        !el.classList.contains('disabled');
    const text = el => String(el.innerText || el.value || el.textContent || '').replace(/\s+/g, ' ').trim();
    if (/just a moment|checking your browser|security verification/i.test(document.title) ||
        Array.from(document.querySelectorAll('#challenge-form, #challenge-stage, .cf-turnstile, iframe[src*=""challenges.cloudflare.com""]')).some(visible))
        return answer('needs-user', 'Complete Nexus browser verification to continue.');
    if (Array.from(document.querySelectorAll('#onetrust-banner-sdk, #qc-cmp2-container, .qc-cmp2-container')).some(visible))
        return answer('needs-user', 'Choose your Nexus cookie preferences to continue.');
    const headings = Array.from(document.querySelectorAll('h1,h2,.alert-danger,.error-message')).filter(visible).map(text).join(' ');
    if (/please (?:log|sign) in|(?:log|sign) in to download|must be logged in/i.test(headings) ||
        Array.from(document.querySelectorAll('input[type=""password""]')).some(visible))
        return answer('needs-user', 'Sign in to Nexus to continue this download.');
    if (/file (?:not found|unavailable|has been (?:removed|deleted))|download (?:unavailable|blocked)|access denied/i.test(headings))
        return answer('needs-user', 'Nexus cannot serve this file. Check the message in the Nexus window.');
    const modPath = url => {
        const match = url.pathname.match(/^\/(?:games\/)?([a-z0-9_-]+)\/mods\/(\d+)\/?$/i);
        return !!match && match[1].toLowerCase() === game && Number(match[2]) === mod;
    };
    const selectedFile = url => trusted(url) && modPath(url) && Number(url.searchParams.get('file_id')) === file;
    const requirementsPage = /\/Core\/Libs\/Common\/Widgets\/ModRequirementsPopUp\/?$/i.test(here.pathname) && Number(here.searchParams.get('id')) === file;
    let fromSelectedMod = false;
    try { fromSelectedMod = trusted(new URL(document.referrer)) && modPath(new URL(document.referrer)); } catch (_) { }
    if (!selectedFile(here) && !(requirementsPage && fromSelectedMod))
        return answer('needs-user', 'The Nexus page changed. Continue with the selected file in this window.');
    const target = game + ':' + mod + ':' + file;
    let state = window.__nexusMFreeDownload;
    if (!state || state.target !== target)
        state = window.__nexusMFreeDownload = {target: target, startedAt: Date.now(), clicked: new WeakSet(), slowClicked: false, requirementsClicked: false};
    if (document.readyState !== 'complete' || Date.now() - state.startedAt < 500)
        return answer('waiting', 'Loading the Nexus download page...');
    const age = Date.now() - state.startedAt;
    if (age > 45000) return answer('needs-user', 'Nexus has not handed over the file yet. Continue in this window.');
    // Current Nexus pages render the file picker inside an open web-component
    // shadow root. Scope traversal to the component for this exact file; never
    // read its authorization attributes or call its internal React handlers.
    const components = Array.from(document.querySelectorAll('mod-file-download'));
    const component = components.find(el => Number(el.getAttribute('file-id')) === file &&
        String(el.getAttribute('game-domain') || '').replace(/^\/+|\/+$/g, '').toLowerCase() === game &&
        el.getAttribute('is-nmm-download') === 'true');
    if (components.length && !component)
        return answer('needs-user', 'The Nexus download controls belong to another file. Continue in this window.');
    if (component && component.getAttribute('user-is-logged-in') === 'false')
        return answer('needs-user', 'Sign in to Nexus to continue this download.');
    const roots = component ? (component.shadowRoot ? [component.shadowRoot] : []) : [document];
    if (component) for (let i = 0; i < roots.length; i++)
        for (const el of roots[i].querySelectorAll('*')) if (el.shadowRoot) roots.push(el.shadowRoot);
    const query = selector => roots.flatMap(root => Array.from(root.querySelectorAll(selector)));
    const clickOnce = (el, kind) => {
        if (!enabled(el) || state.clicked.has(el)) return false;
        state.clicked.add(el);
        state[kind] = true;
        el.click();
        return true;
    };
    // Only continue requirements links for this exact chosen file and manager
    // download mode. Links to required mods and purchases never qualify.
    const panelSelector = '.popup-mod-requirements, #mod-requirements, [data-testid=""mod-requirements""]';
    const panels = Array.from(document.querySelectorAll(panelSelector)).concat(component ? query(panelSelector) : []).filter(visible);
    if (requirementsPage && !panels.length && document.body) panels.push(document.body);
    for (const panel of panels) {
        if (state.requirementsClicked) return answer('waiting', 'Opening the selected Nexus file...');
        for (const link of panel.querySelectorAll('a[href]')) {
            if (!enabled(link) || !/^(?:mod manager download|download with manager|download|continue|continue download)$/i.test(text(link))) continue;
            let next;
            try { next = new URL(link.getAttribute('href'), location.href); } catch (_) { continue; }
            if (selectedFile(next) && next.searchParams.get('nmm') === '1' && clickOnce(link, 'requirementsClicked'))
                return answer('waiting', 'Opening the selected Nexus file...');
        }
        return answer('needs-user', 'Review the requirements in the Nexus window to continue.');
    }
    if (state.slowClicked)
        return answer('waiting', 'Waiting for Nexus\u2019s free-download countdown and file authorization...');
    // The legacy data-download-url attribute is deliberately ignored. Nexus
    // staff confirm that its ERROR placeholder is not a download error.
    let slow = query('#slowDownloadButton').find(visible);
    if (!slow) slow = query('button,input[type=""button""]').find(el => visible(el) && /^slow download$/i.test(text(el)));
    if (slow && /^(?:BUTTON|INPUT)$/.test(slow.tagName) && /^slow download$/i.test(text(slow))) {
        if (clickOnce(slow, 'slowClicked'))
            return answer('waiting', 'Waiting for Nexus\u2019s free-download countdown and file authorization...');
        if (!enabled(slow)) return answer('waiting', 'Waiting for Nexus to enable the free download...');
    }
    return answer(age < 15000 ? 'waiting' : 'needs-user', age < 15000 ?
        'Waiting for the Nexus download controls...' : 'Continue the download in the Nexus window.');
})()
";
    }
}
