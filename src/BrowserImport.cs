using System;
using System.Text.RegularExpressions;

namespace NexusM
{
    // Runs only after the user opens the Nexus sign-in/history window and presses Import.
    // Nexus does not publish a download-history enumeration API. Read the signed-in
    // page's own DataTable first, with a same-origin, read-only website request fallback.
    internal static class BrowserImport
    {
        public static string HistoryUrl(string gameDomain)
        {
            string domain = NormalizeDomain(gameDomain);
            return "https://www.nexusmods.com/" + (domain.Length == 0 ? "" : domain + "/") +
                "users/myaccount?tab=download+history";
        }

        public static string StartHistoryScript(string gameDomain)
        {
            return HistoryScript.Replace("__GAME_DOMAIN__", "'" + NormalizeDomain(gameDomain) + "'");
        }

        public const string PollHistoryScript =
            "(function(){return window.__nexusMHistoryImport || {status:'error',message:'The page changed. Open download history and import again.',items:[],processed:0,total:0};})()";

        private static string NormalizeDomain(string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (value.Length > 0 && !Regex.IsMatch(value, "^[a-z0-9_-]+$"))
                throw new ArgumentException("Use the game's Nexus domain, for example baldursgate3.");
            return value;
        }

        private const string HistoryScript = @"
(function () {
    'use strict';
    if (window.__nexusMHistoryImport && window.__nexusMHistoryImport.status === 'running')
        return window.__nexusMHistoryImport;
    const state = window.__nexusMHistoryImport = {
        status: 'running', message: 'Reading your signed-in Nexus download history...',
        items: [], processed: 0, total: 0, partial: false
    };
    const gameFilter = __GAME_DOMAIN__;
    const seen = new Map();
    const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
    const validDomain = value => /^[a-z0-9_-]+$/i.test(String(value || ''));
    const parseHtml = value => {
        // Template contents stay inert: imported images and event handlers must not run.
        const template = document.createElement('template');
        template.innerHTML = String(value == null ? '' : value);
        return template.content;
    };
    const normalizeText = value => String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
    const plain = value => normalizeText(parseHtml(value).textContent);
    const dateIso = value => {
        if (value == null || value === '') return null;
        if (typeof value === 'object') {
            return dateIso(value.timestamp || value.date || value.value || value.display);
        }
        const raw = String(value).trim();
        const attr = raw.match(/(?:data-timestamp|data-time|datetime|data-order|data-sort)\s*=\s*['\x22]([^'\x22]+)['\x22]/i);
        if (attr) return dateIso(attr[1]);
        let milliseconds;
        if (/^\d{10,13}(?:\.\d+)?$/.test(raw)) {
            const number = Number(raw);
            milliseconds = number > 100000000000 ? number : number * 1000;
        } else {
            const text = plain(raw).replace(/(\d)(st|nd|rd|th)\b/gi, '$1').replace(/\s+/g, ' ');
            milliseconds = Date.parse(text);
        }
        if (!Number.isFinite(milliseconds)) return null;
        const date = new Date(milliseconds);
        if (date.getUTCFullYear() < 2000 || date.getUTCFullYear() > 2100) return null;
        return date.toISOString();
    };
    const safeImage = html => {
        const el = parseHtml(html);
        const image = el.querySelector('img');
        if (!image) return '';
        try {
            const url = new URL(image.getAttribute('data-src') || image.getAttribute('src') || '', location.href);
            return url.protocol === 'https:' && /(^|\.)nexusmods\.com$/i.test(url.hostname) ? url.href : '';
        } catch (_) { return ''; }
    };
    const add = item => {
        if (!item || !validDomain(item.gameDomain) || !Number.isSafeInteger(item.modId) || item.modId <= 0) return;
        item.gameDomain = item.gameDomain.toLowerCase();
        if (gameFilter && item.gameDomain !== gameFilter) return;
        const key = item.gameDomain + ':' + item.modId;
        const previous = seen.get(key);
        if (!previous || String(item.lastDownloadedUtc || '') > String(previous.lastDownloadedUtc || '')) seen.set(key, item);
    };
    const parseRow = (row, columns) => {
        if (Array.isArray(row) && row.length >= 10 && validDomain(row[9]) && /^\d+$/.test(String(row[8]))) {
            // Website history schema; derive date positions from its actual DataTable headers
            // when available. Unknown dates stay unknown rather than becoming today's date.
            const downloaded = columns && columns.downloaded != null ? columns.downloaded : 2;
            const updated = columns && columns.updated != null ? columns.updated : 0;
            return {
                gameDomain: String(row[9]), modId: Number(row[8]), name: plain(row[1]),
                lastDownloadedUtc: dateIso(row[downloaded]), updatedUtc: dateIso(row[updated]),
                thumbnailUrl: ''
            };
        }
        if (row && !Array.isArray(row) && typeof row === 'object') {
            const domain = row.game_domain_name || row.game_domain || row.gameDomain || row.domain_name;
            const id = row.mod_id || row.modId;
            if (validDomain(domain) && /^\d+$/.test(String(id || ''))) return {
                gameDomain: String(domain), modId: Number(id), name: plain(row.name || row.mod_name),
                lastDownloadedUtc: dateIso(row.last_downloaded_at || row.downloaded_at || row.last_downloaded || row.downloaded),
                updatedUtc: dateIso(row.updated_at || row.updated), thumbnailUrl: ''
            };
        }
        const html = Array.isArray(row) ? row.map(value => String(value == null ? '' : value)).join(' ') : String(row || '');
        const el = parseHtml(html);
        const links = Array.from(el.querySelectorAll('a[href]'));
        for (const link of links) {
            let url;
            try { url = new URL(link.getAttribute('href'), location.href); } catch (_) { continue; }
            const match = url.pathname.match(/^\/(?:games\/)?([a-z0-9_-]+)\/mods\/(\d+)(?:\/|$)/i);
            if (!match || !/(^|\.)nexusmods\.com$/i.test(url.hostname)) continue;
            const cells = Array.isArray(row) ? row : Array.from(el.querySelectorAll('td')).map(cell => cell.innerHTML);
            const downloaded = columns && columns.downloaded != null ? columns.downloaded : 1;
            const updated = columns && columns.updated != null ? columns.updated : 4;
            return {
                gameDomain: match[1], modId: Number(match[2]), name: normalizeText(link.textContent),
                lastDownloadedUtc: dateIso(cells[downloaded]), updatedUtc: dateIso(cells[updated]), thumbnailUrl: safeImage(html)
            };
        }
        return null;
    };
    const tableColumns = table => {
        const columns = {};
        Array.from(table.querySelectorAll('thead th')).forEach((header, index) => {
            const title = normalizeText(header.textContent).toLowerCase();
            if (/last\s*(dl|download)|downloaded/.test(title)) columns.downloaded = index;
            if (/updated/.test(title)) columns.updated = index;
        });
        return columns;
    };
    const dataTableRows = () => {
        const jq = window.jQuery;
        if (!jq || !jq.fn || !jq.fn.dataTable) return null;
        const tables = Array.from(jq.fn.dataTable.tables());
        for (const table of tables) {
            const headers = normalizeText(table.querySelector('thead') ? table.querySelector('thead').textContent : '');
            if (!/last\s*dl|downloaded|download history/i.test(headers)) continue;
            const api = jq(table).DataTable();
            const settings = api.settings()[0];
            if (settings && settings.oFeatures && settings.oFeatures.bServerSide) continue;
            const columns = tableColumns(table);
            const rowIndexes = api.rows({search: 'none'}).indexes().toArray();
            state.total = rowIndexes.length;
            for (const index of rowIndexes) {
                const raw = api.row(index).data();
                const result = parseRow(raw, null);
                if (result) {
                    for (const key of ['downloaded', 'updated']) {
                        if (columns[key] == null) continue;
                        try {
                            const date = dateIso(api.cell(index, columns[key]).render('sort')) || dateIso(api.cell(index, columns[key]).render('display'));
                            if (date) result[key === 'downloaded' ? 'lastDownloadedUtc' : 'updatedUtc'] = date;
                        } catch (_) { }
                    }
                    add(result);
                } else {
                    const rendered = Array.from(table.querySelectorAll('thead th')).map((_, col) => api.cell(index, col).render('display'));
                    add(parseRow(rendered, columns));
                }
                state.processed++;
            }
            if (rowIndexes.length) return true;
        }
        return null;
    };
    const visibleRows = () => {
        const rows = [];
        for (const table of document.querySelectorAll('table')) {
            const headers = normalizeText(table.querySelector('thead') ? table.querySelector('thead').textContent : '');
            if (!/last\s*dl|downloaded|download history/i.test(headers)) continue;
            const columns = tableColumns(table);
            for (const row of table.querySelectorAll('tbody tr')) {
                const parsed = parseRow(Array.from(row.querySelectorAll('td')).map(cell => cell.innerHTML), columns);
                if (parsed) rows.push(parsed);
            }
        }
        return rows;
    };
    (async function () {
        try {
            if (location.protocol !== 'https:' || !/^(www\.)?nexusmods\.com$/i.test(location.hostname))
                throw new Error('Open Nexus Mods and sign in before importing history.');
            if (!/download.?history/i.test(decodeURIComponent(location.search.replace(/\+/g, ' '))) && !/download.?history/i.test(location.pathname))
                throw new Error('Open the Download history tab, wait for it to load, then press Import.');
            // An initialized client-side DataTable already contains every page, not only
            // the twenty rows displayed on screen. Give it a short chance to finish loading.
            for (let attempt = 0; attempt < 6; attempt++) {
                if (dataTableRows()) break;
                if (attempt < 5) await pause(350);
            }
            if (!seen.size) {
                const controller = new AbortController();
                const timeout = setTimeout(() => controller.abort(), 45000);
                try {
                    const response = await fetch('/Core/Libs/Common/Managers/Mods?GetDownloadHistory', {
                        method: 'GET', credentials: 'same-origin', cache: 'no-store',
                        headers: {'Accept': 'application/json'}, signal: controller.signal
                    });
                    if (!response.ok) throw new Error('Nexus history returned HTTP ' + response.status + '. Check that you are signed in.');
                    const contentType = response.headers.get('content-type') || '';
                    if (!/json/i.test(contentType)) throw new Error('Nexus returned a sign-in or verification page. Complete it, then import again.');
                    const json = await response.json();
                    const rows = Array.isArray(json) ? json : (Array.isArray(json.data) ? json.data : json.aaData);
                    if (!Array.isArray(rows)) throw new Error('Nexus history format changed. Import the visible history table instead.');
                    state.total = rows.length;
                    state.processed = 0;
                    for (const row of rows) { add(parseRow(row, null)); state.processed++; }
                    if (rows.length && !seen.size && !gameFilter)
                        throw new Error('The history format was not recognised. No existing records were changed.');
                } catch (error) {
                    const rows = visibleRows();
                    if (!rows.length) throw error;
                    rows.forEach(add);
                    state.total = rows.length;
                    state.processed = rows.length;
                    state.partial = true;
                } finally { clearTimeout(timeout); }
            }
            state.items = Array.from(seen.values());
            state.status = 'complete';
            state.message = state.items.length + ' mods read' + (gameFilter ? ' for ' + gameFilter : ' across your games') +
                (state.partial ? ' from the visible page only. Switch pages and import again to add more.' : '.');
        } catch (error) {
            state.status = 'error';
            state.items = [];
            state.message = error.name === 'AbortError' ? 'History request timed out. Wait for the Nexus page to load and import again.' : String(error.message || error);
        }
    })();
    return {status: 'running', message: state.message, items: [], processed: 0, total: 0};
})()
";
    }
}
