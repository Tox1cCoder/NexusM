/* NexusM: deliberately framework-free. All account and disk access stays in the host. */
(() => {
  'use strict';
  let interfaceReady=false;
  function reportStartupError(){
    if(interfaceReady)return;
    try{window.chrome?.webview?.postMessage({action:'uiStartupError',payload:{message:'The app interface could not start. Reload the window to try again.'}});}catch{}
  }
  window.addEventListener('error',reportStartupError);
  window.addEventListener('unhandledrejection',reportStartupError);
  const $ = (selector, scope = document) => scope.querySelector(selector);
  const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
  const icon = name => `<svg aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const pending = new Map();
  let sequence = 0;
  let state = {games:[],mods:[],downloads:[],settings:{yourGamesOnly:true},user:null};
  let activeGame = null, filter = 'all', currentPage = 'library', detail = null, detailTab = 'files', detailToken = 0, modalConfig = null;
  let selectedFiles = new Set();
  const selectedMods = new Set(), batchCandidates = new Map();
  let preparingBatch = false;
  let filePreviews = new Map();
  let lastFocus = null, lastNotice = null;
  let loadingLibrary = false, refreshPromise = null;
  let downloadFolderDraft = null;
  const renderedHtml=new WeakMap();
  let librarySignature='',gamesSignature='',downloadsSignature='',settingsSignature='';
  let gameNames=new Map(),gameModCounts=new Map(),scopedLibrary=[];
  const dateFormatter=new Intl.DateTimeFormat(undefined,{day:'numeric',month:'short',year:'numeric'});
  const timeFormatter=new Intl.DateTimeFormat(undefined,{hour:'2-digit',minute:'2-digit'});
  const dateTimeFormatter=new Intl.DateTimeFormat(undefined,{day:'numeric',month:'short',year:'numeric',hour:'2-digit',minute:'2-digit'});
  const dateCells=new Map();
  function setHtml(selector,html){const element=$(selector);if(renderedHtml.get(element)!==html){element.innerHTML=html;renderedHtml.set(element,html);}}

  function rpc(action, payload = {}) {
    const bridge = window.chrome?.webview;
    if (!bridge) return Promise.reject(new Error('Open NexusM.exe to connect to Nexus and use local files.'));
    const id = String(++sequence);
    return new Promise((resolve,reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('This request is taking longer than expected. Please try again.')); }, ['refresh','initialize','importData','startBatchDownload'].includes(action) ? 180000 : 90000);
      pending.set(id,{resolve,reject,timer});
      bridge.postMessage({id,action,payload});
    });
  }
  function receive(message) {
    let msg = message;
    if (typeof msg === 'string') {try {msg=JSON.parse(msg);}catch{return;}}
    if (!msg) return;
    if (msg.id != null && pending.has(String(msg.id))) {
      const task=pending.get(String(msg.id));pending.delete(String(msg.id));clearTimeout(task.timer);
      if(msg.ok === false || msg.error) task.reject(new Error(typeof msg.error==='object' ? msg.error.message : msg.error || 'The request failed.'));
      else task.resolve(msg.result);
    }
    if(msg.event==='state') setState(msg.data);
    if(msg.event==='download' || msg.event==='downloadProgress') {
      if(msg.data?.downloads) setState(msg.data);
      else if(msg.data?.id){
        const i=state.downloads.findIndex(d=>String(d.id)===String(msg.data.id));
        state.downloads=i>=0?state.downloads.map((d,index)=>index===i?{...d,...msg.data}:d):[msg.data,...state.downloads];
        renderDownloads();renderCounts();refreshFileDownloadFlags();
      }
    }
    if(msg.event==='error') toast(msg.data?.message || msg.data || 'Something went wrong.',true);
    if(msg.event==='notice') toast(msg.data?.message || msg.data);
    if(msg.event==='refreshProgress'){state.refreshProgress=msg.data||{};renderRefresh();}
  }
  window.chrome?.webview?.addEventListener('message',e=>receive(e.data));

  function toast(message,error=false) {
    if(!message)return;
    const el=document.createElement('div');el.className=`toast${error?' error':''}`;
    el.innerHTML=`${icon(error?'info':'check')}<span>${esc(message)}</span><button class="icon-button" aria-label="Dismiss notification">${icon('close')}</button>`;
    $('.toasts').append(el);el.querySelector('button').addEventListener('click',()=>el.remove());
    setTimeout(()=>el.remove(),error?16000:8000);
  }
  async function run(action,payload={},button=null,success=null) {
    if(button){button.disabled=true;button.classList.add('busy');}
    try {const result=await rpc(action,payload);consume(result);if(success)toast(success);return result;}
    catch(error){toast(error.message,true);return null;}
    finally{if(button){button.disabled=false;button.classList.remove('busy');}renderDownloadControls();}
  }
  function renderRefresh(){
    const progress=state.refreshProgress||{},running=progress.running===true||!!refreshPromise;
    const busy=loadingLibrary||running,button=$('#refresh-button'),status=$('#refresh-status');
    button.disabled=busy;button.classList.toggle('busy',busy);button.setAttribute('aria-busy',String(busy));
    const done=Number(progress.done||0),total=Number(progress.total||0);
    $('#refresh-button span').textContent=running?(progress.phase==='connecting'?'Connecting to Nexus…':total?`Checking ${done} / ${total}…`:'Checking for updates…'):loadingLibrary?'Loading library…':'Check for updates';
    status.classList.toggle('hidden',!running);
    status.textContent=running?(progress.message||'Checking your saved mods. You can keep browsing your library.'):'';
  }
  function checkUpdates(action='refresh',payload={}){
    if(refreshPromise)return refreshPromise;
    if(state.refreshProgress?.running===true)return Promise.resolve();
    refreshPromise=(async()=>{
      try{consume(await rpc(action,payload));}
      catch(error){toast(error.message,true);}
      finally{refreshPromise=null;state.refreshProgress={...state.refreshProgress,running:false};renderRefresh();}
    })();
    renderRefresh();return refreshPromise;
  }
  async function loadLibrary(){
    loadingLibrary=true;renderRefresh();
    try{
      consume(await rpc('getState'));
      if(state.user||state.settings.hasApiKey)await checkUpdates('initialize');
    }catch(error){toast(error.message,true);}
    finally{loadingLibrary=false;renderRefresh();}
  }
  function consume(result) {
    const next=result?.state??result;
    if(next&&['games','mods','downloads','settings','user','fileDownloads','lastCheckedAt','refreshProgress','clearingDownloadFolder','notice'].some(key=>Object.prototype.hasOwnProperty.call(next,key)))setState(next);
  }
  function setState(next) {
    if(!next || typeof next!=='object')return;
    state={...state,...next,settings:{...state.settings,...next.settings}};
    state.games=Array.isArray(state.games)?state.games:[];state.mods=Array.isArray(state.mods)?state.mods:[];state.downloads=Array.isArray(state.downloads)?state.downloads:[];
    if(activeGame&&!state.games.some(g=>gameDomain(g)===activeGame))activeGame=null;
    const nextGames=JSON.stringify(state.games);
    const gamesChanged=nextGames!==gamesSignature;
    const nextLibrary=JSON.stringify([nextGames,state.settings.yourGamesOnly,state.mods.map(mod=>[modDomain(mod),modId(mod),mod.name,mod.version,mod.author,mod.uploaded_by,picture(mod),timestamp(updated(mod)),timestamp(downloaded(mod)),needsReview(mod)])]);
    const libraryChanged=nextLibrary!==librarySignature;
    const nextDownloads=JSON.stringify(state.downloads),nextSettings=JSON.stringify([state.settings,state.user,state.clearingDownloadFolder]);
    reconcileBatchSelection();
    if(libraryChanged){
      librarySignature=nextLibrary;gamesSignature=nextGames;
      gameNames=new Map(state.games.map(game=>[gameDomain(game),game.name]));gameModCounts=new Map();
      for(const mod of state.mods){const domain=modDomain(mod);gameModCounts.set(domain,(gameModCounts.get(domain)||0)+1);}
      scopedLibrary=state.settings.yourGamesOnly===false?state.mods:state.mods.filter(mod=>gameNames.has(modDomain(mod)));
      renderGames();renderMods();
    }else renderCheckDate();
    if(nextDownloads!==downloadsSignature||gamesChanged){downloadsSignature=nextDownloads;renderDownloads();}
    if(nextSettings!==settingsSignature){settingsSignature=nextSettings;renderSettings();}
    renderCounts();renderRefresh();renderDownloadControls();refreshFileDownloadFlags();
    if(next.notice&&next.notice!==lastNotice)toast(next.notice);
    lastNotice=next.notice||null;
  }
  const gameDomain = game => String(game.domain ?? game.domain_name ?? game.gameDomain ?? game.id ?? '');
  const modDomain = mod => String(mod.gameDomain ?? mod.game_domain_name ?? mod.gameDomainName ?? mod.domain ?? '');
  const modId = mod => Number(mod.modId ?? mod.mod_id ?? mod.id);
  const fileId = file => Number(file.fileId ?? file.file_id ?? file.id);
  const modKey = mod => `${modDomain(mod)}:${modId(mod)}`;
  const batchFingerprint = mod => JSON.stringify([mod.version,timestamp(updated(mod))]);
  const queuedStatuses = new Set(['queued','authorizing','retrying','downloading','waiting-authorization','awaiting-authorization','waiting','awaiting_authorization']);
  function reconcileBatchSelection(){
    if(!batchCandidates.size&&!selectedMods.size)return;
    const mods=new Map(state.mods.map(mod=>[modKey(mod),mod]));
    for(const [key,candidate] of batchCandidates){
      const mod=mods.get(key);
      if(!mod||candidate.fingerprint!==batchFingerprint(mod)||(candidate.status==='alreadyQueued'&&!state.downloads.some(download=>modKey(download)===key&&fileId(download)===Number(candidate.fileId)&&queuedStatuses.has(download.status)))){
        batchCandidates.delete(key);selectedMods.delete(key);
      }
    }
    for(const key of selectedMods)if(!mods.has(key))selectedMods.delete(key);
  }
  function renderBatchControls(){
    const checking=[...batchCandidates.values()].filter(candidate=>candidate.status==='checking').length;
    const clearing=state.clearingDownloadFolder===true;
    const button=$('#download-selected-mods');
    button.disabled=preparingBatch||clearing||checking>0||selectedMods.size===0;
    button.classList.toggle('busy',preparingBatch);button.setAttribute('aria-busy',String(preparingBatch));
    const label=preparingBatch?'Preparing downloads…':`Download selected (${selectedMods.size})`;
    if(button.textContent!==label)button.textContent=label;
    $('#clear-mod-selection').disabled=preparingBatch||(!selectedMods.size&&!checking);
    $('#batch-selection').classList.toggle('hidden',state.mods.length===0);
    let visibleSelected=0;
    for(const input of $$('[data-select-mod]')){
      const key=input.dataset.selectMod,candidate=batchCandidates.get(key),pendingCheck=candidate?.status==='checking';
      const checked=selectedMods.has(key)||pendingCheck,disabled=preparingBatch||clearing||pendingCheck||['ineligible','alreadyQueued'].includes(candidate?.status);
      if(input.checked!==checked)input.checked=checked;if(input.disabled!==disabled)input.disabled=disabled;
      input.setAttribute('aria-busy',String(pendingCheck));
      const row=input.closest('tr');row.classList.toggle('selected-for-download',selectedMods.has(key));
      const message=pendingCheck?'Checking files…':['ineligible','failed','alreadyQueued'].includes(candidate?.status)?candidate.reason||(candidate.status==='alreadyQueued'?'This file is already in the download queue.':'Open this mod to choose its files.') :'';
      const status=$('.batch-mod-status',row);if(status.textContent!==message)status.textContent=message;status.classList.toggle('hidden',!message);
      const title=message||(candidate?.eligible?`Download ${candidate.name||'the main file'}`:'Check files and select this mod for download');
      if(input.title!==title)input.title=title;
      if(selectedMods.has(key))visibleSelected++;
    }
    const hidden=selectedMods.size-visibleSelected;
    const message=checking?`Checking ${checking} ${checking===1?'mod':'mods'}…`:hidden?`${hidden} selected ${hidden===1?'mod is':'mods are'} hidden by the current filters.`:'';
    if($('#batch-selection-status').textContent!==message)$('#batch-selection-status').textContent=message;
  }
  async function selectModForDownload(input){
    const key=input.dataset.selectMod;
    if(preparingBatch||state.clearingDownloadFolder){renderBatchControls();return;}
    if(!input.checked){selectedMods.delete(key);renderBatchControls();return;}
    const mod=state.mods.find(mod=>modKey(mod)===key);if(!mod)return;
    const checking=[...batchCandidates.values()].filter(candidate=>candidate.status==='checking').length;
    if(selectedMods.size+checking>=100){toast('Select up to 100 mods per download batch.',true);renderBatchControls();return;}
    const cached=batchCandidates.get(key);
    if(cached?.eligible&&cached.status==='eligible'){selectedMods.add(key);renderBatchControls();return;}
    const candidate={status:'checking',fingerprint:batchFingerprint(mod)};
    batchCandidates.set(key,candidate);renderBatchControls();
    try{
      const result=await rpc('getBatchDownloadCandidates',{mods:[{gameDomain:modDomain(mod),modId:modId(mod)}]});
      if(batchCandidates.get(key)!==candidate)return;
      const checked=result?.candidates?.find(row=>modKey(row)===key);
      if(!checked)throw new Error('The file list could not be checked. Try selecting this mod again.');
      const status=checked.status||(checked.eligible?'eligible':'ineligible');
      batchCandidates.set(key,{...checked,status,fingerprint:candidate.fingerprint});
      if(checked.eligible&&status==='eligible')selectedMods.add(key);
      else if(status==='failed')toast(checked.reason||'The file list could not be checked. Try again.',true);
    }catch(error){
      if(batchCandidates.get(key)!==candidate)return;
      batchCandidates.set(key,{...candidate,status:'failed',reason:error.message});toast(error.message,true);
    }finally{renderBatchControls();}
  }
  function clearModSelection(){
    if(preparingBatch)return;
    selectedMods.clear();for(const [key,candidate] of batchCandidates)if(candidate.status==='checking')batchCandidates.delete(key);
    renderBatchControls();
  }
  async function downloadSelectedMods(){
    if(preparingBatch||state.clearingDownloadFolder||!selectedMods.size||[...batchCandidates.values()].some(candidate=>candidate.status==='checking'))return;
    const requested=new Set(selectedMods),mods=state.mods.filter(mod=>requested.has(modKey(mod))).map(mod=>({gameDomain:modDomain(mod),modId:modId(mod)}));
    preparingBatch=true;renderBatchControls();
    try{
      const result=await rpc('startBatchDownload',{mods});consume(result);
      const batch=result?.batch;if(!batch)throw new Error('The download batch could not be prepared. Try again.');
      for(const candidate of batch.candidates||[]){
        const key=modKey(candidate);if(!requested.has(key))continue;
        const mod=state.mods.find(mod=>modKey(mod)===key);if(!mod)continue;
        const status=candidate.status==='queued'?'alreadyQueued':candidate.status;
        batchCandidates.set(key,{...candidate,status,fingerprint:batchFingerprint(mod)});
        if(['queued','alreadyQueued','ineligible'].includes(candidate.status))selectedMods.delete(key);
      }
      const queued=Number(batch.queued)||0,skipped=Number(batch.skipped)||0,failed=Number(batch.failed)||0;
      toast(`${queued} ${queued===1?'mod':'mods'} added to the download queue.${skipped?` ${skipped} skipped.`:''}${failed?` ${failed} could not be queued. Select Download selected to retry.`:''}`,failed>0);
    }catch(error){toast(error.message,true);}
    finally{preparingBatch=false;renderBatchControls();}
  }
  function libraryMods(){return scopedLibrary;}
  const gameName = domain => gameNames.get(domain) || domain || 'Nexus Mods';
  const picture = mod => mod.pictureUrl ?? mod.picture_url ?? mod.imageUrl ?? '';
  const imageUrl = value => {try {const url=new URL(String(value));return url.protocol==='https:'||url.protocol==='http:'?url.href:'';}catch{return '';}};
  const updated = mod => mod.updatedAt ?? mod.updated_timestamp ?? mod.updated_time;
  const downloaded = mod => mod.lastDownloadedAt ?? mod.downloadedAt ?? mod.last_downloaded;
  function timestamp(value){if(value==null||value==='')return 0;const n=Number(value);if(Number.isFinite(n)&&n>0)return n<1e12?n*1000:n;const t=Date.parse(value);return Number.isFinite(t)?t:0;}
  function needsReview(mod){return mod.needsReview!=null?Boolean(mod.needsReview):timestamp(updated(mod))>timestamp(downloaded(mod));}
  function date(value,withTime=false){const t=timestamp(value);if(!t)return 'Not recorded';return (withTime?dateTimeFormatter:dateFormatter).format(t);}
  function dateCell(value){const t=timestamp(value);if(!t)return '<span class="muted">Not recorded</span>';if(dateCells.has(t))return dateCells.get(t);const html=`${esc(dateFormatter.format(t))}<span>${esc(timeFormatter.format(t))}</span>`;if(dateCells.size>=8192)dateCells.clear();dateCells.set(t,html);return html;}
  function bytes(n){n=Number(n);if(!Number.isFinite(n)||n<=0)return '—';if(n<1024)return `${n} B`;const units=['KB','MB','GB','TB'];let i=-1;do{n/=1024;i++;}while(n>=1024&&i<units.length-1);return `${n.toFixed(n<10?1:0)} ${units[i]}`;}
  function shortName(name){return String(name||'M').split(/[\s_-]+/).filter(Boolean).slice(0,2).map(x=>x[0]).join('').toUpperCase();}
  function modUrl(mod){return imageUrl(mod.url)||`https://www.nexusmods.com/${encodeURIComponent(modDomain(mod))}/mods/${modId(mod)}`;}
  async function openUrl(url){if(!imageUrl(url))return;await run('openUrl',{url});}
  function navigate(page){if(!['library','downloads','settings'].includes(page))return;currentPage=page;$$('.page').forEach(el=>el.classList.toggle('active',el.id===`page-${page}`));$$('.nav-item').forEach(el=>el.classList.toggle('active',el.dataset.page===page));document.documentElement.scrollTop=0;}

  function renderCounts(){
    $('#library-count').textContent=libraryMods().length;
    $('#game-count').textContent=state.games.length;
    const active=state.downloads.filter(d=>!['completed','failed','cancelled','canceled','interrupted'].includes(d.status)).length;
    const downloads=$('#download-count'),total=state.downloads.length;
    downloads.textContent=total;downloads.title=`${total} ${total===1?'download':'downloads'}, ${active} active`;
    const name=state.user?.name || state.user?.username;
    setHtml('#account',`<span class="avatar">${esc(name?shortName(name):'N')}</span><div><strong>${esc(name || 'Not connected')}</strong><small>${name?(state.user.isPremium?'Premium account':'Free account'):'Connect your Nexus account'}</small></div><button class="icon-button" data-page="settings" aria-label="Account settings">${icon('settings')}</button>`);
  }
  function renderGames(){
    const total=libraryMods().length;
    setHtml('#game-strip',`<button class="game-card all-games ${activeGame?'':'selected'}" data-select-game="" aria-pressed="${!activeGame}">${icon('library')}<strong>All games</strong><small>${total} ${total===1?'mod':'mods'}</small></button>`+
      state.games.map(g=>{const domain=gameDomain(g),img=imageUrl(g.imageUrl??g.image_url??g.tile_image??g.image),count=gameModCounts.get(domain)||0;return `<div class="game-card ${activeGame===domain?'selected':''}"><button class="game-select" data-select-game="${esc(domain)}" aria-label="Show ${esc(g.name)} mods" aria-pressed="${activeGame===domain}" title="${esc(g.name)} · ${count} mods">${img?`<img src="${esc(img)}" alt="" loading="lazy"><span class="game-label">${esc(g.name)}</span>`:`<span class="game-fallback">${icon('game')}<span>${esc(g.name)}</span></span>`}</button><button class="game-remove" data-remove-game="${esc(domain)}" aria-label="Remove ${esc(g.name)} from your games" title="Remove from your games">×</button></div>`;}).join('')+
      `<button class="game-card add-game-card" id="game-strip-add">${icon('plus')}<span>Add a game</span></button>`);
  }
  function renderCheckDate(){$('#last-checked').textContent=state.lastCheckedAt?`Checked ${date(state.lastCheckedAt,true)}`:'Not checked yet';}
  function renderMods(){
    const library=libraryMods();
    const scoped=library.filter(m=>!activeGame||modDomain(m)===activeGame);
    const search=$('#mod-search').value.trim().toLowerCase();
    let mods=scoped.filter(m=>(filter!=='updates'||needsReview(m))&&(!search||`${m.name} ${m.author??m.uploaded_by??''} ${gameName(modDomain(m))}`.toLowerCase().includes(search)));
    const sort=$('#mod-sort').value;
    mods.sort((a,b)=>sort==='name'?String(a.name).localeCompare(String(b.name)):sort==='downloaded'?timestamp(downloaded(b))-timestamp(downloaded(a)):timestamp(updated(b))-timestamp(updated(a)));
    $('#mods-title').textContent=activeGame?gameName(activeGame):'All mods';
    $('#all-count').textContent=scoped.length;$('#updates-count').textContent=scoped.filter(needsReview).length;
    renderCheckDate();
    const empty=library.length===0;
    $('.table-toolbar').classList.toggle('hidden',state.mods.length===0);
    $('#your-games-only').setAttribute('aria-pressed',String(state.settings.yourGamesOnly!==false));
    $('#library-empty h3').textContent=state.mods.length?'No mods from your games yet.':'No mods in your library';
    $('#library-empty p').innerHTML=state.mods.length?'Add a game above or turn off <strong>Your games only</strong> to view all imported mods.':'Import your Nexus download history or add a mod link.';
    $('#library-empty').classList.toggle('hidden',!empty);$('#mods-table-wrap').classList.toggle('hidden',empty);$('#table-footer').classList.toggle('hidden',empty);
    $('#visible-count').textContent=`${mods.length} of ${scoped.length} ${scoped.length===1?'mod':'mods'}`;
    setHtml('#mods-body',mods.map(m=>{const img=imageUrl(picture(m));const review=needsReview(m),hasDownload=!!timestamp(downloaded(m));return `<tr tabindex="0" data-mod-key="${esc(modKey(m))}" aria-label="View ${esc(m.name)}"><td class="mod-select-cell"><input type="checkbox" data-select-mod="${esc(modKey(m))}" aria-label="Select ${esc(m.name)} for download" aria-describedby="batch-mod-${esc(modKey(m))}"></td><td><div class="mod-cell">${img?`<img class="mod-thumb" src="${esc(img)}" loading="lazy" alt="">`:`<span class="mod-thumb fallback">${esc(shortName(m.name))}</span>`}<span><strong class="mod-name" title="${esc(m.name)}">${esc(m.name)}</strong><small class="mod-game">${esc(gameName(modDomain(m)))}${m.author?` · ${esc(m.author)}`:''}</small><small class="batch-mod-status hidden" id="batch-mod-${esc(modKey(m))}"></small></span></div></td><td><span class="version-chip">${esc(m.version||'—')}</span></td><td class="table-date">${dateCell(downloaded(m))}</td><td class="table-date">${dateCell(updated(m))}</td><td><span class="badge ${review?'update':hasDownload?'current':''}">${review?'Needs review':hasDownload?'Downloaded':'Unreviewed'}</span></td><td>${icon('chevron').replace('<svg','<svg class="row-chevron"')}</td></tr>`;}).join('') || '<tr><td colspan="7" class="no-results">No mods match this view. Try another game or search.</td></tr>');
    renderBatchControls();
  }
  function renderDownloads(){
    const downloads=[...state.downloads];
    $('#clear-downloads-button').disabled=!downloads.some(d=>['completed','failed','cancelled','canceled','interrupted','waiting-authorization','awaiting-authorization','waiting','awaiting_authorization'].includes(d.status));
    $('#downloads-list').innerHTML=downloads.length?downloads.map(d=>{
      const status=d.status||'queued',done=status==='completed',failed=['failed','cancelled','canceled','interrupted'].includes(status),waiting=['waiting-authorization','awaiting-authorization','waiting','awaiting_authorization'].includes(status);
      const current=Number(d.bytes??d.downloadedBytes??d.receivedBytes??0),total=Number(d.total??d.totalBytes??0),percent=done?100:Math.max(0,Math.min(100,total?current/total*100:Number(d.progress||0)));
      const labels={queued:'Queued',authorizing:'Authorizing',retrying:'Resuming',downloading:'Downloading',completed:'Downloaded',failed:'Failed',cancelled:'Cancelled',canceled:'Cancelled',interrupted:'Interrupted'};
      return `<article class="download-card"><div class="download-icon">${icon(done?'check':'download')}</div><div class="download-info"><h3>${esc(d.name??d.fileName??d.filename??'Mod archive')}</h3><div class="download-meta">${esc(d.fileName??d.filename??gameName(d.gameDomain))}${total?` · ${bytes(current)} / ${bytes(total)}`:''}${d.speed?` · ${bytes(d.speed)}/s`:''}${waiting&&d.authorizationMessage?`<br><span>${esc(d.authorizationMessage)}</span>`:''}${d.error?`<br><span class="download-error">${esc(d.error)}</span>`:''}</div>${status==='downloading'?`<div class="progress-track" role="progressbar" aria-label="Download progress" aria-valuenow="${Math.round(percent)}" aria-valuemin="0" aria-valuemax="100"><span style="width:${percent}%"></span></div>`:''}</div><span class="download-status ${done?'completed':''}">${esc(waiting?(d.needsUser?'Nexus needs attention':'Preparing download'):labels[status]||status)}</span><div class="download-actions">${done?`<button class="icon-button" data-download-folder="${esc(d.id)}" aria-label="Show file in folder" title="Show file in folder">${icon('folder')}</button>`:failed?`<button class="button small" data-retry-download="${esc(d.id)}">Retry</button>`:`${waiting?`<button class="button small" data-retry-download="${esc(d.id)}">Open Nexus</button>`:''}<button class="icon-button" data-cancel-download="${esc(d.id)}" aria-label="Cancel download" title="Cancel download">${icon('close')}</button>`}</div></article>`;
    }).join(''):`<div class="empty-state"><div class="empty-symbol">${icon('download')}</div><h3>No downloads</h3><p>Open a mod in your library and choose the files you want.<br>Your downloads will appear here.</p><div class="empty-actions"><button class="button" data-page="library">Back to my library</button></div></div>`;
    renderDownloadControls();
  }
  function renderDownloadControls(){
    const clearing=state.clearingDownloadFolder===true;
    const active=state.downloads.some(d=>['queued','authorizing','retrying','downloading','waiting-authorization','awaiting-authorization','waiting','awaiting_authorization'].includes(d.status||'queued'));
    const clear=$('#clear-download-folder-button');
    clear.disabled=clearing||active||clear.classList.contains('busy');clear.setAttribute('aria-busy',String(clearing||clear.classList.contains('busy')));
    $('#download-folder').disabled=clearing;
    for(const button of $$('[data-download-file],[data-retry-download],#download-selected,#browse-folder-button,#save-folder-button'))button.disabled=clearing||button.classList.contains('busy')||(button.id==='download-selected'&&selectedFiles.size===0);
    renderBatchControls();
  }
  function renderSettings(){
    const connected=!!(state.user || state.settings.hasApiKey);
    $('#connection-badge').textContent=state.user?'Connected':state.settings.hasApiKey?'Key saved':'Not connected';$('#connection-badge').classList.toggle('current',!!state.user);
    $('#api-key').placeholder=connected?'A key is saved. Paste here to replace it.':'Paste a personal Nexus API key';
    $('#download-folder').value=downloadFolderDraft??state.settings.downloadFolder??'';
    if($('#cache-size'))$('#cache-size').textContent=state.settings.cacheSizeMB!=null?`${Number(state.settings.cacheSizeMB).toFixed(1)} MB cached. Images can be fetched again.`:'Images can be fetched again when you open the app.';
    renderDownloadControls();
  }

  // Convert common Nexus BBCode, then allow only harmless markup and web links.
  function richText(source){
    if(!source)return '';
    let text=String(source);
    const htmlLike=/<\/?(?:p|div|br|ul|li|strong|b|img|a|span|h[1-6])\b/i.test(text);
    if(!htmlLike){
      text=esc(text).replace(/\[b\]([\s\S]*?)\[\/b\]/gi,'<strong>$1</strong>').replace(/\[i\]([\s\S]*?)\[\/i\]/gi,'<em>$1</em>').replace(/\[u\]([\s\S]*?)\[\/u\]/gi,'<u>$1</u>').replace(/\[s\]([\s\S]*?)\[\/s\]/gi,'<s>$1</s>').replace(/\[url=(?:&quot;)?([^\]]*?)(?:&quot;)?\]([\s\S]*?)\[\/url\]/gi,'<a href="$1">$2</a>').replace(/\[url\]([^\[]*)\[\/url\]/gi,'<a href="$1">$1</a>').replace(/\[img\]([^\[]*)\[\/img\]/gi,'<img src="$1" alt="Mod image">').replace(/\[quote(?:=[^\]]*)?\]([\s\S]*?)\[\/quote\]/gi,'<blockquote>$1</blockquote>').replace(/\[list(?:=1)?\]([\s\S]*?)\[\/list\]/gi,(_,body)=>`<ul>${body.split(/\[\*\]/).filter(x=>x.trim()).map(x=>`<li>${x}</li>`).join('')}</ul>`).replace(/\[size=[^\]]*\]([\s\S]*?)\[\/size\]/gi,'$1').replace(/\[color=[^\]]*\]([\s\S]*?)\[\/color\]/gi,'$1').replace(/\[center\]([\s\S]*?)\[\/center\]/gi,'<p>$1</p>').replace(/\[spoiler(?:=[^\]]*)?\]([\s\S]*?)\[\/spoiler\]/gi,'<blockquote>$1</blockquote>').replace(/\r?\n/g,'<br>');
    }
    const parsed=new DOMParser().parseFromString(text,'text/html');
    const allowed=new Set(['P','BR','STRONG','B','EM','I','U','S','UL','OL','LI','H1','H2','H3','H4','DIV','SPAN','IMG','A','HR','TABLE','THEAD','TBODY','TR','TD','TH','BLOCKQUOTE','CODE','PRE']);
    const blocked=new Set(['SCRIPT','STYLE','IFRAME','OBJECT','EMBED','FORM','INPUT','BUTTON','TEXTAREA','SELECT','SVG','MATH','LINK','META','BASE']);
    function clean(node){
      if(node.nodeType===Node.TEXT_NODE)return document.createTextNode(node.textContent);
      if(node.nodeType!==Node.ELEMENT_NODE)return document.createTextNode('');
      if(blocked.has(node.tagName))return document.createTextNode('');
      const out=allowed.has(node.tagName)?document.createElement(node.tagName.toLowerCase()):document.createDocumentFragment();
      if(node.tagName==='A'){const url=imageUrl(node.getAttribute('href'));if(url){out.setAttribute('href',url);out.setAttribute('data-external','true');}}
      if(node.tagName==='IMG'){const url=imageUrl(node.getAttribute('src'));if(!url)return document.createTextNode('');out.setAttribute('src',url);out.setAttribute('alt',node.getAttribute('alt')||'Mod image');out.setAttribute('loading','lazy');out.setAttribute('referrerpolicy','no-referrer');}
      for(const child of node.childNodes)out.append(clean(child));
      return out;
    }
    const target=document.createElement('div');for(const child of parsed.body.childNodes)target.append(clean(child));return target.innerHTML;
  }

  async function showMod(key){
    const mod=state.mods.find(m=>modKey(m)===key);if(!mod)return;
    lastFocus=document.activeElement;detail={mod,files:[],changelogs:{},requirements:[]};detailTab='files';selectedFiles.clear();filePreviews.clear();
    $('#detail-panel').classList.remove('hidden');$('#detail-scrim').classList.remove('hidden');document.body.style.overflow='hidden';$('#detail-close').focus();
    $('#detail-breadcrumb').textContent=gameName(modDomain(mod)).toUpperCase();$('#detail-content').innerHTML='<div class="loading"><span class="spinner"></span>Getting the latest mod details…</div>';
    const token=++detailToken;
    try{const result=await rpc('getModDetails',{gameDomain:modDomain(mod),modId:modId(mod)});if(token!==detailToken)return;detail={...detail,...result,mod:{...mod,...result?.mod},files:result?.files??[]};renderDetail();}
    catch(error){if(token===detailToken)$('#detail-content').innerHTML=`<div class="detail-tab-content"><h3>Couldn’t load this mod</h3><p class="subtle-empty">${esc(error.message)}</p><button class="button" data-detail-retry="${esc(key)}">Try again</button></div>`;}
  }
  function closeDetail(){detailToken++;filePreviews.clear();$('#detail-panel').classList.add('hidden');$('#detail-scrim').classList.add('hidden');document.body.style.overflow='';detail=null;if(lastFocus?.isConnected)lastFocus.focus();}
  function renderDetail(){
    if(!detail)return;const mod=detail.mod,img=imageUrl(picture(mod));
    $('#detail-content').innerHTML=`<div class="detail-hero">${img?`<img src="${esc(img)}" alt="">`:`<div class="hero-fallback">${icon('game')}</div>`}</div><div class="detail-intro"><div class="detail-game">${esc(gameName(modDomain(mod)))}</div><h2>${esc(mod.name)}</h2><div class="detail-byline">${mod.author?`<span>By ${esc(mod.author)}</span>`:''}<span>Version ${esc(mod.version||'—')}</span><span>Updated ${esc(date(updated(mod)))}</span><button class="text-button detail-review-button" id="mark-reviewed-button" title="Dismiss the current update without changing your download date">${icon('check')}Mark reviewed</button></div></div><div class="detail-tabs" role="tablist" aria-label="Mod content">${['files','overview','changelog','requirements'].map(tab=>`<button data-detail-tab="${tab}" class="${detailTab===tab?'active':''}" role="tab" aria-selected="${detailTab===tab}">${{files:'Files',overview:'Overview',changelog:'Changelog',requirements:'Requirements'}[tab]}${tab==='files'?` (${detail.files.length})`:''}</button>`).join('')}</div><div class="detail-tab-content" id="detail-tab-body"></div>`;
    renderDetailTab();
  }
  function categoryName(file){const category=file.category??file.categoryName??file.category_name??file.category_id??'Other files';const map={1:'Main files',2:'Updates',3:'Optional files',4:'Old versions',5:'Miscellaneous',6:'Old versions',MAIN:'Main files',OPTIONAL:'Optional files',UPDATE:'Updates',OLD_VERSION:'Old versions',MISCELLANEOUS:'Miscellaneous',ARCHIVED:'Old versions'};return map[category] || (/^main/i.test(category)?'Main files':/^optional/i.test(category)?'Optional files':String(category));}
  function fileDownloadedAt(file){
    if(!detail)return 0;
    const domain=modDomain(detail.mod),mod=modId(detail.mod),id=fileId(file),key=`${domain}:${mod}:${id}`;
    const recordedTime=value=>{const number=Number(value);return Number.isFinite(number)?number>0?timestamp(number):0:Math.max(0,timestamp(value));};
    let at=recordedTime(file.downloadedAt);
    const saved=state.fileDownloads?.[key];
    if(saved?.status==='completed')at=Math.max(at,recordedTime(saved.downloadedAt));
    for(const record of state.downloads)if(record.status==='completed'&&modDomain(record)===domain&&modId(record)===mod&&fileId(record)===id)at=Math.max(at,recordedTime(record.downloadedAt));
    return at;
  }
  function fileDownloadFlag(at){return `${at?icon('check'):''}${at?'Downloaded':'No download recorded'}`;}
  function refreshFileDownloadFlags(){
    if(!detail||detailTab!=='files')return;
    for(const file of detail.files){
      const card=$(`[data-file-card="${fileId(file)}"]`,$('#detail-panel'));if(!card)continue;
      const at=fileDownloadedAt(file),flag=$('.file-download-flag',card),when=$('.file-downloaded-date',card);
      if(!flag||!when||card.dataset.downloadedAt===String(at))continue;
      card.dataset.downloadedAt=String(at);flag.classList.toggle('downloaded',!!at);flag.innerHTML=fileDownloadFlag(at);
      flag.title=at?'This exact file has a recorded download. Installation is manual.':'No download date is available for this exact file.';
      when.textContent=at?`Last downloaded ${date(at,true)}`:'';when.classList.toggle('hidden',!at);
    }
  }
  function previewTree(entries){
    const root=new Map();
    for(const entry of entries){
      const parts=String(entry.path||'').replace(/\\/g,'/').split('/').filter(Boolean);if(!parts.length)continue;
      if(parts.length>64)parts.splice(63,parts.length-63,parts.slice(63).join('/'));
      let siblings=root;
      parts.forEach((name,index)=>{if(!siblings.has(name))siblings.set(name,{name,children:new Map()});const node=siblings.get(name);if(index===parts.length-1)node.entry=entry;siblings=node.children;});
    }
    const render=(nodes,depth=0)=>`<ul>${[...nodes.values()].sort((a,b)=>Number(!!b.children.size||!!b.entry?.isDirectory)-Number(!!a.children.size||!!a.entry?.isDirectory)||a.name.localeCompare(b.name,undefined,{numeric:true,sensitivity:'base'})).map(node=>{
      const folder=node.children.size>0||node.entry?.isDirectory;
      return folder?`<li><details class="preview-folder" ${depth===0?'open':''}><summary>${icon('folder')}<span>${esc(node.name)}</span></summary>${node.children.size?render(node.children,depth+1):'<p class="preview-empty-folder">Empty folder</p>'}</details></li>`:`<li class="preview-file"><span class="preview-file-name">${esc(node.name)}</span><small>${esc(node.entry?.sizeLabel||bytes(node.entry?.size))}</small></li>`;
    }).join('')}</ul>`;
    return render(root);
  }
  function renderFilePreview(id){
    const card=$(`[data-file-card="${id}"]`,$('#detail-panel'));if(!card)return;
    const preview=filePreviews.get(id),button=$('[data-preview-file]',card),panel=$('.file-preview',card);
    button.setAttribute('aria-expanded',String(preview?.open===true));panel.classList.toggle('hidden',preview?.open!==true);
    if(!preview)return;
    panel.setAttribute('aria-busy',String(preview.loading===true));
    if(preview.loading){panel.innerHTML='<p class="preview-message" role="status">Loading file contents...</p>';return;}
    if(preview.error){panel.innerHTML=`<p class="preview-message" role="alert">${esc(preview.error)}</p><button class="button small" data-retry-preview="${id}">Retry preview</button>`;return;}
    const result=preview.result||{},entries=Array.isArray(result.entries)?result.entries:[];
    panel.innerHTML=`<h4>File contents</h4>${result.notice?`<p class="preview-message">${esc(result.notice)}</p>`:''}${entries.length?`<div class="file-preview-tree">${previewTree(entries)}</div>`:`<p class="preview-message">${result.available===false?'Nexus has no file contents preview for this archive.':'No files are listed in this archive.'}</p>`}${result.truncated?'<p class="preview-message">This preview shows only part of the archive listing.</p>':''}`;
  }
  async function previewFile(id,retry=false){
    id=Number(id);if(!detail||!detail.files.some(file=>fileId(file)===id))return;
    const previous=filePreviews.get(id);
    if(previous&&!retry){previous.open=!previous.open;renderFilePreview(id);return;}
    const preview={open:true,loading:true};filePreviews.set(id,preview);renderFilePreview(id);
    const token=detailToken,mod=detail.mod;
    try{const result=await rpc('getFileContents',{gameDomain:modDomain(mod),modId:modId(mod),fileId:id});if(token!==detailToken||filePreviews.get(id)!==preview)return;preview.result=result;}
    catch(error){if(token!==detailToken||filePreviews.get(id)!==preview)return;preview.error=error.message;}
    finally{if(token===detailToken&&filePreviews.get(id)===preview){preview.loading=false;renderFilePreview(id);}}
  }
  const versionCollator=new Intl.Collator(undefined,{numeric:true,sensitivity:'base'});
  function compareChangelogVersions(a,b){
    const parse=value=>String(value).trim().replace(/^v(?=\d)/i,'').split('+')[0].match(/^(\d+(?:\.\d+)*)(.*)$/);
    const first=parse(a),second=parse(b);
    if(first&&second){
      const left=first[1].split('.').map(Number),right=second[1].split('.').map(Number);
      for(let i=0;i<Math.max(left.length,right.length);i++){const difference=(right[i]||0)-(left[i]||0);if(difference)return difference;}
      const leftSuffix=first[2].replace(/^[\s._-]+/,''),rightSuffix=second[2].replace(/^[\s._-]+/,'');
      // The release precedes its previews; numeric suffixes put rc.10 before rc.2.
      if(!leftSuffix||!rightSuffix)return leftSuffix?1:rightSuffix?-1:0;
      return versionCollator.compare(rightSuffix,leftSuffix);
    }
    return versionCollator.compare(String(b),String(a));
  }
  function renderDetailTab(){
    if(!detail)return;const el=$('#detail-tab-body');if(!el)return;const mod=detail.mod;
    if(detailTab==='overview')el.innerHTML=`${mod.summary?`<p class="detail-summary">${esc(mod.summary)}</p>`:''}<div class="rich-text">${richText(mod.description)||'<p>No description provided. Open the mod on Nexus for more information.</p>'}</div><div style="margin-top:22px"><button class="button small" data-open-mod="true">Open on Nexus ${icon('arrow')}</button><button class="text-button remove-mod-link" data-remove-mod="${esc(modKey(mod))}">Remove from local library</button></div>`;
    if(detailTab==='files'){
      const groups=new Map();detail.files.forEach(f=>{const group=categoryName(f);if(!groups.has(group))groups.set(group,[]);groups.get(group).push(f);});
      const order=['Main files','Updates','Optional files','Miscellaneous','Other files','Old versions'];
      const sorted=[...groups].sort((a,b)=>(order.indexOf(a[0])<0?99:order.indexOf(a[0]))-(order.indexOf(b[0])<0?99:order.indexOf(b[0])));
      el.innerHTML=`<div class="details-note">${icon('info')}<span>Choose the exact files you need. Check the changelog, requirements, and each file’s instructions before installing. Get this file handles Nexus’s free-download steps automatically. Sign in or complete a browser check if prompted.</span></div><p class="file-history-note">Flags track exact files, not installed mods. Older downloads without file-level history show “No download recorded.”</p><div class="files-toolbar"><p><span id="selected-files-count">${selectedFiles.size}</span> files selected</p><button class="button primary small" id="download-selected" ${selectedFiles.size?'':'disabled'}>${icon('download')}Download selected</button></div>`+
        sorted.map(([group,files])=>`<section class="file-group"><h3>${esc(group)}<span>${files.length}</span></h3>${files.map(f=>{const id=fileId(f),selected=selectedFiles.has(id),at=fileDownloadedAt(f);return `<article class="file-card ${selected?'selected':''}" data-file-card="${id}" data-downloaded-at="${at}"><label class="file-card-head"><input type="checkbox" data-select-file="${id}" ${selected?'checked':''} aria-label="Select ${esc(f.name)}"><span class="file-card-title"><span class="file-title-row"><strong>${esc(f.name)}</strong><span class="file-download-flag ${at?'downloaded':''}" title="${at?'This exact file has a recorded download. Installation is manual.':'No download date is available for this exact file.'}">${fileDownloadFlag(at)}</span></span><span class="file-card-meta"><span class="version-chip">${esc(f.version||'—')}</span><span>${esc(date(f.uploadedAt??f.uploaded_timestamp??f.uploaded_time,true))}</span><span>${bytes(f.size??f.size_in_bytes??Number(f.size_kb||0)*1024)}</span><span class="file-downloaded-date ${at?'':'hidden'}">${at?`Last downloaded ${esc(date(at,true))}`:''}</span></span></span></label><div class="file-card-body"><div class="rich-text">${richText(f.description)||'<span>No file description provided.</span>'}</div><div class="file-card-actions"><button class="text-button" data-preview-file="${id}" aria-expanded="false" aria-controls="file-preview-${id}">Preview file contents</button><button class="button small file-download" data-download-file="${id}">${icon('download')}Get this file</button></div><section class="file-preview hidden" id="file-preview-${id}" aria-label="File contents of ${esc(f.name)}"></section></div></article>`;}).join('')}</section>`).join('')+(!detail.files.length?'<p class="subtle-empty">No downloadable files are currently available for this mod.</p>':'');
      for(const id of filePreviews.keys())renderFilePreview(id);
    }
    if(detailTab==='changelog'){
      const entries=Array.isArray(detail.changelogs)?detail.changelogs.map(c=>[c.version??c.name??'Update',c.changes??c.changelog??c.description??c.text]):Object.entries(detail.changelogs||{});
      entries.sort((a,b)=>compareChangelogVersions(a[0],b[0]));
      el.innerHTML=entries.length?entries.map(([version,changes])=>`<article class="changelog-entry"><h3>Version ${esc(version)}</h3><div class="rich-text">${Array.isArray(changes)?`<ul>${changes.map(c=>`<li>${richText(typeof c==='string'?c:c.text??c.description??'')}</li>`).join('')}</ul>`:richText(changes)}</div></article>`).join(''):'<p class="subtle-empty">The author hasn’t published a changelog through the API. Check the description and the mod page for release notes or pinned instructions.</p><button class="button small" data-open-mod="true">Open on Nexus '+icon('arrow')+'</button>';
    }
    if(detailTab==='requirements'){
      const requirements=Array.isArray(detail.requirements)?detail.requirements:[];
      el.innerHTML=`<div class="details-note">${icon('info')}<span>${esc(detail.requirementsNotice||'Requirements may also be described in the overview, file notes, or pinned comments. Read the author’s instructions for the complete installation steps.')}</span></div>`+requirements.map(r=>`<article class="requirement-card"><div><strong>${esc(r.name??r.modName??'Requirement')}</strong>${r.notes?`<div class="rich-text">${richText(r.notes)}</div>`:''}</div>${imageUrl(r.url)?`<button class="icon-button" data-open-url="${esc(r.url)}" aria-label="Open requirement on Nexus">${icon('arrow')}</button>`:''}</article>`).join('')+(!requirements.length?'<p class="subtle-empty">No structured requirements are available. This doesn’t confirm the mod has no dependencies.</p>':'')+'<button class="button small" data-open-mod="true">Check requirements on Nexus '+icon('arrow')+'</button>';
    }
    renderDownloadControls();
  }
  async function downloadFile(id,button){if(!detail)return;const result=await run('startDownload',{gameDomain:modDomain(detail.mod),modId:modId(detail.mod),fileId:Number(id)},button);if(result!==null)toast('Preparing your download…');}
  async function downloadSelected(button){const ids=[...selectedFiles];if(!ids.length)return;button.disabled=true;button.classList.add('busy');let added=0;try{for(const id of ids){if(!detail)break;const result=await run('startDownload',{gameDomain:modDomain(detail.mod),modId:modId(detail.mod),fileId:id});if(result!==null){added++;selectedFiles.delete(id);}}if(detail)renderDetailTab();if(added)toast(`Added ${added} ${added===1?'file':'files'} to the download queue.`);}finally{button.classList.remove('busy');renderDownloadControls();}}

  function showDialog(config){
    modalConfig=config;$('#dialog-title').textContent=config.title;$('#dialog-description').textContent=config.description||'';$('#dialog-body').innerHTML=config.body;$('#dialog-submit').textContent=config.submit||'Save';$('#dialog-submit').disabled=false;$('#dialog-error').classList.add('hidden');$('#input-dialog').showModal();
    setTimeout(()=>$('#dialog-body input:not([type=file]), #dialog-body textarea')?.focus(),50);
  }
  function addGame(){showDialog({title:'Add a game',description:'Use the game’s Nexus domain, name, or page link. You can remove games from this strip any time.',body:'<label class="dialog-label" for="dialog-value">Nexus game</label><input class="dialog-input" id="dialog-value" required placeholder="baldursgate3 or nexusmods.com/baldursgate3"><div class="game-results" id="game-results"></div>',submit:'Add game',onSubmit:async()=>{
    const query=$('#dialog-value').value.trim();if(!query)throw new Error('Enter a Nexus game name, domain, or URL.');const result=await rpc('addGame',{query});consume(result);const results=result?.results??result?.gamesFound;if(results?.length){$('#game-results').innerHTML=results.map(g=>`<button type="button" class="game-result" data-add-result="${esc(gameDomain(g))}">${esc(g.name)} ${icon('plus')}</button>`).join('');return false;}toast('Game added to your library.');return true;
  }});}
  function addMod(){showDialog({title:'Add a mod',description:'Paste a Nexus Mods link. This adds it to your local library so you can check its updates.',body:'<label class="dialog-label" for="dialog-value">Mod page URL</label><input class="dialog-input" id="dialog-value" type="url" required placeholder="https://www.nexusmods.com/baldursgate3/mods/123">',submit:'Add to library',onSubmit:async()=>{const url=$('#dialog-value').value.trim();if(!/^https?:\/\/(?:www\.)?nexusmods\.com\//i.test(url))throw new Error('Paste a mod page link from nexusmods.com.');consume(await rpc('addMod',{url}));toast('Mod added. Open it to review files and updates.');return true;}});}
  function importHistory(){showDialog({title:'Import download history',description:'Import the mods you’ve already downloaded. NexusM remembers future downloads locally; it doesn’t scan installed mods.',body:`<div class="dialog-options"><button type="button" class="button" id="dialog-website-import">${icon('arrow')}Open Nexus history</button><button type="button" class="button" id="dialog-tracked-import">Import tracked mods</button></div><p class="dialog-tip">Sign in on the Nexus page, then use the import button in that window. You can also paste copied history, saved HTML, or a NexusM JSON backup below.</p><label class="dialog-label" for="dialog-value">History text, HTML, or JSON</label><textarea class="dialog-textarea" id="dialog-value" placeholder="Paste your history here…"></textarea><input type="file" id="dialog-file" class="dialog-file" accept=".json,.html,.htm,.txt,.csv,.tsv"><p class="dialog-tip">For copied text, include mod links where possible. Website history import can preserve your mod download dates. Per-file dates start with downloads through NexusM.</p>`,submit:'Import pasted data',onSubmit:async()=>{const text=$('#dialog-value').value.trim();if(!text)throw new Error('Paste history, choose a file, or use Open Nexus history.');const result=await rpc('importData',{text});consume(result);toast(result?.message||'History imported into your library.');return true;}});}
  async function submitDialog(e){e.preventDefault();if(!modalConfig)return;const button=$('#dialog-submit');button.disabled=true;$('#dialog-error').classList.add('hidden');try{const close=await modalConfig.onSubmit();if(close!==false)$('#input-dialog').close();}catch(error){$('#dialog-error').textContent=error.message;$('#dialog-error').classList.remove('hidden');}finally{button.disabled=false;}}

  document.addEventListener('click',async e=>{
    if(e.target.closest('[data-select-mod]'))return;
    const target=e.target.closest('button,a,tr[data-mod-key]');if(!target)return;
    if(target.matches('a')){e.preventDefault();if(target.classList.contains('brand'))navigate('library');else if(target.href)openUrl(target.href);return;}
    if(target.dataset.page){navigate(target.dataset.page);return;}
    if(target.hasAttribute('data-select-game')){activeGame=target.dataset.selectGame||null;renderGames();renderMods();return;}
    if(target.dataset.removeGame){const domain=target.dataset.removeGame;showDialog({title:'Remove this game?',description:`Remove ${gameName(domain)} from your games? Your downloaded archives stay in your folder.`,body:'<p class="dialog-tip">You can add the game again later.</p>',submit:'Remove game',onSubmit:async()=>{consume(await rpc('removeGame',{domain}));return true;}});return;}
    if(target.dataset.modKey){showMod(target.dataset.modKey);return;}
    if(target.dataset.filter){filter=target.dataset.filter;$$('[data-filter]').forEach(b=>b.classList.toggle('active',b===target));renderMods();return;}
    if(target.dataset.detailTab){detailTab=target.dataset.detailTab;$$('[data-detail-tab]').forEach(b=>{b.classList.toggle('active',b===target);b.setAttribute('aria-selected',String(b===target));});renderDetailTab();return;}
    if(target.dataset.detailRetry){showMod(target.dataset.detailRetry);return;}
    if(target.hasAttribute('data-open-mod')){if(detail)openUrl(modUrl(detail.mod));return;}
    if(target.dataset.openUrl){openUrl(target.dataset.openUrl);return;}
    if(target.dataset.previewFile){previewFile(target.dataset.previewFile);return;}
    if(target.dataset.retryPreview){previewFile(target.dataset.retryPreview,true);return;}
    if(target.dataset.downloadFile){downloadFile(target.dataset.downloadFile,target);return;}
    if(target.dataset.cancelDownload){run('cancelDownload',{id:target.dataset.cancelDownload},target);return;}
    if(target.dataset.retryDownload){run('retryDownload',{id:target.dataset.retryDownload},target);return;}
    if(target.dataset.downloadFolder){run('openFolder',{id:target.dataset.downloadFolder});return;}
    if(target.dataset.addResult){const result=await run('addGame',{query:target.dataset.addResult},target);if(result!==null)$('#input-dialog').close();return;}
    if(target.dataset.removeMod){const mod=detail?.mod;closeDetail();if(mod){consume(await run('removeMod',{gameDomain:modDomain(mod),modId:modId(mod)}));toast('Mod removed from your local library.');}return;}
    const handlers={
      'refresh-button':()=>checkUpdates('refresh',activeGame?{gameDomain:activeGame}:{}),
      'add-game-button':addGame,'game-strip-add':addGame,'add-mod-button':addMod,'empty-add':addMod,
      'sync-games-button':()=>run('syncGames',{},target,'Your Nexus favourite games are synced.'),
      'import-history-button':importHistory,'empty-import':importHistory,'settings-import':importHistory,
      'detail-close':closeDetail,'detail-external':()=>detail&&openUrl(modUrl(detail.mod)),
      'downloads-folder':()=>run('openFolder'),'settings-open-folder':()=>run('openFolder'),
      'clear-downloads-button':async()=>{await run('clearDownloads',{},target);renderDownloads();},
      'clear-download-folder-button':()=>run('clearDownloadFolder',{},target),
      'your-games-only':()=>run('saveSettings',{yourGamesOnly:state.settings.yourGamesOnly===false},target),
      'toggle-key':()=>{const input=$('#api-key');input.type=input.type==='password'?'text':'password';target.textContent=input.type==='password'?'Show':'Hide';},
      'save-key-button':async()=>{const apiKey=$('#api-key').value.trim();if(!apiKey){toast('Paste your personal Nexus API key first.',true);return;}const result=await run('saveSettings',{apiKey},target);if(result!==null){$('#api-key').value='';$('#api-key').type='password';$('#toggle-key').textContent='Show';toast('Connected to Nexus Mods.');await checkUpdates('initialize');}},
      'browse-folder-button':async()=>{const result=await run('browseFolder',{},target);const path=typeof result==='string'?result:result?.path??result?.downloadFolder;if(typeof path==='string'){downloadFolderDraft=path;renderSettings();}},
      'save-folder-button':async()=>{const path=$('#download-folder').value;const result=await run('saveSettings',{downloadFolder:path},target,'Download location saved.');if(result!==null&&$('#download-folder').value===path){downloadFolderDraft=null;renderSettings();}},
      'clear-cache-button':()=>run('clearCache',{},target,'Image cache cleared.'),
      'register-nxm-button':()=>run('registerNxm',{},target,'NexusM now handles Nexus download links on this Windows account.'),
      'mark-reviewed-button':async()=>{if(!detail)return;const result=await run('markReviewed',{gameDomain:modDomain(detail.mod),modId:modId(detail.mod)},target);if(result!==null){detail.mod={...detail.mod,needsReview:false};toast('Marked reviewed. Your download date stays the same.');target.innerHTML=`${icon('check')}Reviewed`;target.disabled=true;}},
      'export-button':()=>run('exportData',{},target),
      'dialog-close':()=>$('#input-dialog').close(),'dialog-cancel':()=>$('#input-dialog').close(),
      'dialog-website-import':async()=>{const result=await run('importWebsite',{kind:'history',...(activeGame?{gameDomain:activeGame}:{})},target);if(result!==null){$('#input-dialog').close();toast('The Nexus history window is open. Sign in and use its import button.');}},
      'dialog-tracked-import':async()=>{const result=await run('importTracked',{},target);if(result!==null){$('#input-dialog').close();toast('Tracked mods imported into your local library.');}},
      'download-selected':()=>downloadSelected(target),
      'download-selected-mods':downloadSelectedMods,'clear-mod-selection':clearModSelection
    };if(handlers[target.id])handlers[target.id]();
  });
  document.addEventListener('change',async e=>{
    const input=e.target;
    if(input.dataset.selectMod){await selectModForDownload(input);return;}
    if(input.dataset.selectFile){const id=Number(input.dataset.selectFile);input.checked?selectedFiles.add(id):selectedFiles.delete(id);input.closest('.file-card').classList.toggle('selected',input.checked);$('#selected-files-count').textContent=selectedFiles.size;renderDownloadControls();}
    if(input.id==='dialog-file'&&input.files?.[0]){try{const file=input.files[0];if(file.size>30*1024*1024)throw new Error('Choose a history or backup file smaller than 30 MB.');$('#dialog-value').value=await file.text();}catch(error){toast(error.message,true);}}
  });
  document.addEventListener('keydown',e=>{if(e.key==='Escape'&&detail&&!$('#input-dialog').open)closeDetail();if((e.key==='Enter'||e.key===' ')&&e.target.matches('tr[data-mod-key]')){e.preventDefault();showMod(e.target.dataset.modKey);}if(e.key==='Tab'&&detail&&!$('#input-dialog').open){const focusable=$$('button:not([disabled]),input,a[href],select,textarea,summary', $('#detail-panel')).filter(el=>el.getClientRects().length);const first=focusable[0],last=focusable[focusable.length-1];if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus();}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus();}}});
  $('#mod-search').addEventListener('input',renderMods);$('#mod-sort').addEventListener('change',renderMods);$('#input-form').addEventListener('submit',submitDialog);$('#detail-scrim').addEventListener('click',closeDetail);
  $('#download-folder').addEventListener('input',e=>{downloadFolderDraft=e.target.value;});
  document.addEventListener('error',e=>{if(e.target.tagName==='IMG'){e.target.style.display='none';if(e.target.closest('.game-card'))e.target.parentElement.insertAdjacentHTML('afterbegin',`<span class="game-fallback">${icon('game')}<span>${esc(e.target.parentElement.title?.split(' · ')[0]||'Game')}</span></span>`);}},true);

  // Exposed for host-driven refreshes and local smoke tests, never contains the key.
  window.NexusM={setState,receive,navigate,showMod,ready:false};
  try{
    setState(state);
    window.NexusM.ready=true;interfaceReady=true;
    window.removeEventListener('error',reportStartupError);window.removeEventListener('unhandledrejection',reportStartupError);
    try{window.chrome?.webview?.postMessage({action:'uiReady'});}catch{}
    if(window.chrome?.webview)loadLibrary();
  }catch{reportStartupError();}
})();
