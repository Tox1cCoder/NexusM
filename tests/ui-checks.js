/* Browser-level fixture checks. Loaded only by the development smoke harness. */
(() => {
  const report = window.__uiChecks = {status:'running',passed:false,checks:[]};
  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const bridge = window.chrome.webview;
  const originalPost = bridge.postMessage;
  let snapshot = null;
  const requests = [];
  const settle = () => new Promise(resolve => setTimeout(resolve,0));
  const check = async (name, test) => {
    try {await test();report.checks.push({test:name,passed:true});}
    catch(error){report.checks.push({test:name,passed:false,error:error.message});}
  };
  const expect = (condition,message) => {if(!condition)throw new Error(message);};
  const fixture = {
    games:Array.from({length:9},(_,i)=>({id:i+1,domain:`game${i+1}`,name:`Test Game ${i+1}`})),
    mods:[
      {gameDomain:'game1',modId:1,name:'Alpha overhaul',author:'Alice',version:'2.0',updatedAt:1792450000,lastDownloadedAt:1792300000,needsReview:true},
      {gameDomain:'game1',modId:2,name:'Beta optional',author:'Bob',version:'1.1',updatedAt:1792400000,lastDownloadedAt:1792440000,needsReview:false},
      {gameDomain:'game2',modId:3,name:'Gamma patch',author:'Carol',version:'3.0',updatedAt:1792500000,lastDownloadedAt:1792200000,needsReview:true},
      {gameDomain:'removedgame',modId:4,name:'Preserved old-game history',author:'Dave',version:'1.0',updatedAt:1792600000,lastDownloadedAt:1792100000,needsReview:true}
    ],
    downloads:[{id:'interrupted-fixture',gameDomain:'game1',modId:1,fileId:10,name:'Interrupted archive',status:'interrupted',error:'The app closed before this download finished.'}],
    settings:{hasApiKey:true,downloadFolder:'C:\\Downloads\\NexusM',yourGamesOnly:true},
    user:{name:'UI fixture',isPremium:false},lastCheckedAt:1792500000
  };
  const fakeDetails = {
    mod:{...fixture.mods[0],summary:'Safe summary',description:'<p>Keep <strong>author instructions</strong>.</p><script>window.__uiInjected=true</script><img src="javascript:window.__uiInjected=true" onerror="window.__uiInjected=true"><a href="javascript:window.__uiInjected=true">Unsafe link</a><iframe src="https://untrusted.example"></iframe><a href="https://www.nexusmods.com/game1/mods/1">Safe link</a>'},
    files:[
      {fileId:10,name:'Main regular',category:'MAIN',version:'2.0',size:4096,uploadedAt:1792450000,description:'[b]Clean install required[/b]'},
      {fileId:11,name:'Main alternative',category:'MAIN',version:'2.0',size:8192,uploadedAt:1792450000,description:'[url=javascript:alert(1)]Bad link[/url]'},
      {fileId:12,name:'Optional translation',category:'OPTIONAL',version:'1.1',size:2048,uploadedAt:1792400000,description:'[i]Requires the main file[/i]'}
    ],
    changelogs:{'2.0':['Fixes [b]a bug[/b]','New dependency']},
    requirements:[{name:'Required framework',url:'https://www.nexusmods.com/game1/mods/99',notes:'Install this first.'}]
  };
  async function run() {
    try {
      expect(typeof window.NexusM==='object','The application script did not initialize.');
      snapshot=await new Promise((resolve,reject)=>{
        const id=`ui-fixture-snapshot-${Date.now()}`;
        const listener=e=>{let m=e.data;if(typeof m==='string')try{m=JSON.parse(m);}catch{return;}if(String(m?.id)!==id)return;bridge.removeEventListener('message',listener);clearTimeout(timer);m.ok===false?reject(new Error(m.error)):resolve(m.result);};
        const timer=setTimeout(()=>{bridge.removeEventListener('message',listener);reject(new Error('Could not read the original library.'));},12000);
        bridge.addEventListener('message',listener);
        originalPost.call(bridge,{id,action:'getState',payload:{}});
      });
      bridge.postMessage=function(message){
        if(message.action==='getModDetails')queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:fakeDetails}));
        else if(message.action==='saveSettings'&&typeof message.payload.yourGamesOnly==='boolean'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{state:{settings:{yourGamesOnly:message.payload.yourGamesOnly}}}}));
        }
        else if(message.action==='clearDownloads'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{downloads:[{id:'active-fixture',gameDomain:'game1',name:'Active archive',status:'downloading',bytes:1024,total:4096},{id:'queued-fixture',gameDomain:'game1',name:'Queued archive',status:'queued'}]}}));
        }
        else if(message.action==='startDownload'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{}}));
        }
        else queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:false,error:'Mutation blocked by UI fixture.'}));
      };
      window.NexusM.setState(fixture);
      window.NexusM.navigate('library');
      await check('nine games and all-game library render',()=>{
        expect($$('[data-select-game]').length===10,'Expected nine games plus All games.');
        expect($$('#mods-body tr[data-mod-key]').length===3,'Expected all three fixture mods.');
        expect($('#library-count').textContent==='3','Removed-game records should stay out of active-library counts.');
        expect(!$('#mods-body').innerText.includes('Preserved old-game history'),'Removed-game history should stay hidden while Your games only is enabled.');
        expect($('#your-games-only').getAttribute('aria-pressed')==='true','Your games only should default to enabled.');
      });
      await check('updated sort and name sort',()=>{
        expect($('#mods-body tr').dataset.modKey==='game2:3','Newest update should sort first.');
        $('#mod-sort').value='name';$('#mod-sort').dispatchEvent(new Event('change',{bubbles:true}));
        expect($('#mods-body tr').dataset.modKey==='game1:1','Name sort should put Alpha first.');
        $('#mod-sort').value='updated';$('#mod-sort').dispatchEvent(new Event('change',{bubbles:true}));
      });
      await check('search and review filter',()=>{
        $('#mod-search').value='beta';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        expect($$('#mods-body tr[data-mod-key]').length===1&&$('#mods-body tr').dataset.modKey==='game1:2','Search did not isolate Beta.');
        $('#mod-search').value='';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        $('[data-filter="updates"]').click();expect($$('#mods-body tr[data-mod-key]').length===2,'Review filter should show two mods.');
        $('[data-filter="all"]').click();
      });
      await check('game selection scopes the table',()=>{
        $('[data-select-game="game1"]').click();expect($$('#mods-body tr[data-mod-key]').length===2,'Game filter should show two mods.');
        $('[data-select-game=""]').click();
      });
      await check('Your games only toggles imported history and updates counts',async()=>{
        $('#your-games-only').click();await settle();
        expect(requests.some(r=>r.action==='saveSettings'&&r.payload.yourGamesOnly===false),'The filter preference was not saved.');
        expect($('#your-games-only').getAttribute('aria-pressed')==='false','The filter did not become unpressed.');
        expect($$('#mods-body tr[data-mod-key]').length===4&&$('#mods-body').innerText.includes('Preserved old-game history'),'All imported mods should be available with the filter off.');
        expect($('#library-count').textContent==='4'&&$('#all-count').textContent==='4','Library and tab counts should include all imported mods.');
        expect($('[data-select-game=""] small').textContent==='4 mods','All games card should count the selected scope.');
        $('[data-select-game="game1"]').click();
        expect($$('#mods-body tr[data-mod-key]').length===2,'Selected game should still scope the table when Your games only is off.');
        $('[data-select-game=""]').click();
        $('#your-games-only').click();await settle();
        expect($('#your-games-only').getAttribute('aria-pressed')==='true'&&$('#library-count').textContent==='3','Enabling the filter should restore the Your games scope.');
      });
      await check('multiple main files and optional files stay separate',async()=>{
        await window.NexusM.showMod('game1:1');
        expect($$('.file-group').length===2,'Expected main and optional file groups.');
        expect($$('.file-group')[0].querySelectorAll('.file-card').length===2,'Both main files must remain selectable.');
        expect($$('.file-group')[1].querySelectorAll('.file-card').length===1,'Optional file should have its own group.');
        expect($('#mark-reviewed-button'),'Mark reviewed control is missing.');
        $('[data-select-file="10"]').click();$('[data-select-file="12"]').click();
        expect($('#selected-files-count').textContent==='2'&&!$('#download-selected').disabled,'Multiple file selection did not update.');
      });
      await check('Get this file prepares a download with one click',async()=>{
        expect($('#detail-tab-body .details-note').textContent.includes('free-download steps automatically'),'File instructions should describe automatic preparation.');
        const before=requests.filter(r=>r.action==='startDownload').length;
        $('[data-download-file="10"]').click();await settle();
        const downloads=requests.filter(r=>r.action==='startDownload');
        expect(downloads.length===before+1&&downloads.at(-1).payload.fileId===10,'One click should submit exactly the selected file.');
        expect(!$('#input-dialog').open,'Starting a download should not open another app confirmation.');
        expect($('.toasts').textContent.includes('Preparing your download'),'Preparation status should be visible.');
      });
      await check('author markup is preserved and executable markup removed',()=>{
        $('[data-detail-tab="overview"]').click();
        const body=$('#detail-tab-body');
        expect(body.querySelector('strong')?.textContent==='author instructions','Safe author formatting should be kept.');
        expect(!body.querySelector('script,iframe,img,[onerror],[onclick]'),'Unsafe markup reached the details DOM.');
        expect(!body.querySelector('a[href^="javascript:"]'),'Script URL reached the details DOM.');
        expect(body.querySelector('a[href^="https://www.nexusmods.com/"]'),'Safe author link should remain available.');
        expect(!window.__uiInjected,'Author script executed.');
      });
      await check('changelog and requirement content render',()=>{
        $('[data-detail-tab="changelog"]').click();expect($('#detail-tab-body').innerText.includes('2.0'),'Changelog version missing.');
        $('[data-detail-tab="requirements"]').click();expect($('#detail-tab-body').innerText.includes('Required framework'),'Requirement missing.');
      });
      $('#detail-close').click();
      await check('interrupted downloads offer retry and do not count as active',()=>{
        window.NexusM.navigate('downloads');
        expect($('[data-retry-download="interrupted-fixture"]'),'Interrupted download needs a Retry control.');
        expect($('#download-count').textContent==='0','Interrupted download should not count as active.');
      });
      await check('authorization preparation and user attention have distinct statuses',()=>{
        window.NexusM.setState({downloads:[{id:'preparing-fixture',gameDomain:'game1',name:'Preparing archive',status:'waiting-authorization',needsUser:false,authorizationMessage:'Waiting for the Nexus countdown.'},{id:'attention-fixture',gameDomain:'game1',name:'Attention archive',status:'waiting-authorization',needsUser:true,authorizationMessage:'Sign in to Nexus to continue.'}]});
        const cards=$$('#downloads-list .download-card');
        expect(cards[0].querySelector('.download-status').textContent==='Preparing download','Automatic authorization should be labelled as preparation.');
        expect(cards[1].querySelector('.download-status').textContent==='Nexus needs attention','Login and browser checks should be labelled as needing attention.');
        expect(cards[0].textContent.includes('Nexus countdown')&&cards[1].textContent.includes('Sign in to Nexus'),'Authorization guidance is missing.');
        expect($('[data-retry-download="preparing-fixture"]')&&$('[data-retry-download="attention-fixture"]'),'Both states should allow opening Nexus when needed.');
        expect(!$('#clear-downloads-button').disabled,'A list containing only waiting downloads should still be clearable.');
        expect($('#page-downloads .notice').textContent.includes('countdown still applies'),'The download notice should retain the free-account countdown restriction.');
      });
      await check('Clear list dispatches the action and keeps active downloads visible',async()=>{
        window.NexusM.setState({downloads:[...fixture.downloads,{id:'finished-fixture',name:'Finished archive',status:'completed'},{id:'waiting-fixture',name:'Waiting archive',status:'waiting-authorization'},{id:'active-fixture',name:'Active archive',status:'downloading',bytes:1024,total:4096},{id:'queued-fixture',name:'Queued archive',status:'queued'}]});
        expect(!$('#clear-downloads-button').disabled,'Clear list should be available for finished and waiting entries.');
        expect($('#clear-downloads-button').title.includes('keeps archives and download history'),'Clear list should explain what is kept.');
        $('#clear-downloads-button').click();await settle();
        expect(requests.some(r=>r.action==='clearDownloads'),'Clear list did not request the host action.');
        expect($$('#downloads-list .download-card').length===2&&$('#downloads-list').innerText.includes('Active archive')&&$('#downloads-list').innerText.includes('Queued archive'),'Active and queued transfers should remain visible.');
        expect($('#clear-downloads-button').disabled,'Clear list should be disabled with only active transfers.');
        expect($('#download-count').textContent==='2','Remaining transfers should still count as active.');
      });
      await check('refresh progress is visible',()=>{
        window.NexusM.receive({event:'refreshProgress',data:{done:3,total:9}});
        expect($('#refresh-button span').textContent.includes('3 / 9'),'Refresh progress is missing.');
        $('#refresh-button span').textContent='Check for updates';
      });
      await check('key masking, folder, storage, and protocol controls exist',()=>{
        window.NexusM.navigate('settings');
        expect($('#api-key').type==='password'&&!$('#api-key').value,'The saved secret must not be sent to the UI.');
        expect($('#browse-folder-button')&&$('#clear-cache-button')&&$('#register-nxm-button'),'A required settings control is missing.');
      });
      await check('empty Your games scope still exposes the filter and imported history',async()=>{
        window.NexusM.setState({games:[],mods:fixture.mods,downloads:[]});window.NexusM.navigate('library');
        expect(!$('#library-empty').classList.contains('hidden'),'Empty state is hidden.');
        expect($('#mods-table-wrap').classList.contains('hidden'),'Empty table should be hidden.');
        expect(!$('.table-toolbar').classList.contains('hidden'),'The scope toggle must remain accessible when all games have been removed.');
        $('#your-games-only').click();await settle();
        expect($$('#mods-body tr[data-mod-key]').length===4&&!$('#mods-table-wrap').classList.contains('hidden'),'Turning off the filter should restore imported history even with no games.');
        $('#your-games-only').click();await settle();
      });
      await check('empty library has actionable import controls and disabled clear list',()=>{
        window.NexusM.setState({games:[],mods:[],downloads:[]});window.NexusM.navigate('library');
        expect(!$('#library-empty').classList.contains('hidden'),'Empty library should display the import state.');
        expect($('#empty-import')&&$('#empty-add'),'Empty library needs import and add controls.');
        expect($('#clear-downloads-button').disabled,'An empty download list should not offer a clear action.');
      });
    } catch(error) {report.checks.push({test:'UI fixture setup',passed:false,error:error.message});}
    finally {
      bridge.postMessage=originalPost;
      if(!$('#detail-panel').classList.contains('hidden'))$('#detail-close').click();
      $('#mod-search').value='';$('#mod-sort').value='updated';
      $('[data-filter="all"]').click();
      if(snapshot)window.NexusM.setState(snapshot);
      window.NexusM.navigate('library');
      report.passed=report.checks.length>=16&&report.checks.every(x=>x.passed);
      report.status='complete';
    }
  }
  run();
})();
