/* Browser-level fixture checks. Loaded only by the development smoke harness. */
(() => {
  const report = window.__uiChecks = {status:'running',passed:false,checks:[]};
  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const bridge = window.chrome.webview;
  const originalPost = bridge.postMessage;
  let snapshot = null;
  let refreshRequest = null;
  let folderClearRequest = null;
  let folderSaveRequest = null;
  let queueSettingsRequest = null;
  let browseFolderResult = {cancelled:true};
  let batchDownloadRequest = null;
  let downloadUpdateRequest = null;
  let downloadUpdateCheckRequest = null;
  const batchCandidateRequests = new Map();
  const requests = [];
  const previewRequests = new Map();
  const fileDownloadRequests = new Map();
  let deferFileDownloads = false;
  const settle = () => new Promise(resolve => setTimeout(resolve,0));
  const check = async (name, test) => {
    try {await test();report.checks.push({test:name,passed:true});}
    catch(error){report.checks.push({test:name,passed:false,error:error.message});}
  };
  const expect = (condition,message) => {if(!condition)throw new Error(message);};
  const replyPreview = (request,result,error=null) => {
    expect(request&&previewRequests.has(String(request.id)),'No matching preview request is waiting.');
    previewRequests.delete(String(request.id));
    window.NexusM.receive(error?{id:request.id,ok:false,error}:{id:request.id,ok:true,result});
  };
  const replyFileDownload = (request,error=null) => {
    expect(request&&fileDownloadRequests.has(String(request.id)),'No file download request is waiting.');
    fileDownloadRequests.delete(String(request.id));
    window.NexusM.receive(error?{id:request.id,ok:false,error}:{id:request.id,ok:true,result:{}});
  };
  const replyCandidate = (request,candidate,error=null) => {
    expect(request&&batchCandidateRequests.has(String(request.id)),'No batch file check is waiting.');
    batchCandidateRequests.delete(String(request.id));
    window.NexusM.receive(error?{id:request.id,ok:false,error}:{id:request.id,ok:true,result:{candidates:[candidate]}});
  };
  const fixture = {
    games:Array.from({length:9},(_,i)=>({id:i+1,domain:`game${i+1}`,name:`Test Game ${i+1}`})),
    mods:[
      {gameDomain:'game1',modId:1,name:'Alpha overhaul',author:'Alice',version:'2.0',updatedAt:1792450000,lastDownloadedAt:1792300000,needsReview:true},
      {gameDomain:'game1',modId:2,name:'Beta optional',author:'Bob',version:'1.1',updatedAt:1792400000,lastDownloadedAt:1792440000,needsReview:false},
      {gameDomain:'game2',modId:3,name:'Gamma patch',author:'Carol',version:'3.0',updatedAt:1792500000,lastDownloadedAt:1792200000,needsReview:true},
      {gameDomain:'removedgame',modId:4,name:'Preserved old-game history',author:'Dave',version:'1.0',updatedAt:1792600000,lastDownloadedAt:1792100000,needsReview:true}
    ],
    downloads:[{id:'interrupted-fixture',gameDomain:'game1',modId:1,fileId:10,name:'Interrupted archive',status:'interrupted',error:'The app closed before this download finished.'}],
    downloadUpdates:[],
    fileDownloads:{
      'game1:1:12':{gameDomain:'game1',modId:1,fileId:12,status:'completed',downloadedAt:1792480000},
      'game1:1:11':{gameDomain:'game1',modId:1,fileId:11,status:'failed',downloadedAt:1792480000},
      'game1:2:11':{gameDomain:'game1',modId:2,fileId:11,status:'completed',downloadedAt:1792480000},
      'game2:1:11':{gameDomain:'game2',modId:1,fileId:11,status:'completed',downloadedAt:1792480000}
    },
    settings:{hasApiKey:true,downloadFolder:'C:\\Downloads\\NexusM',yourGamesOnly:true,maxParallelDownloads:2,maxDownloadRetries:3,downloadTimeoutSeconds:90,retryDelaySeconds:2,resumeDownloadsOnStartup:true},
    user:{name:'UI fixture',isPremium:false},lastCheckedAt:1792500000,refreshProgress:{running:false}
  };
  const fakeDetails = {
    mod:{...fixture.mods[0],summary:'Safe summary',description:'<p>Keep <strong>author instructions</strong>.</p><script>window.__uiInjected=true</script><img src="javascript:window.__uiInjected=true" onerror="window.__uiInjected=true"><a href="javascript:window.__uiInjected=true">Unsafe link</a><iframe src="https://untrusted.example"></iframe><a href="https://www.nexusmods.com/game1/mods/1">Safe link</a>'},
    files:[
      {fileId:10,name:'Main regular',category:'MAIN',version:'2.0',size:4096,uploadedAt:1792450000,downloadedAt:1792470000,description:'[b]Clean install required[/b]'},
      {fileId:11,name:'Main alternative',category:'MAIN',version:'2.0',size:8192,uploadedAt:1792450000,downloadedAt:0,description:'[url=javascript:alert(1)]Bad link[/url]'},
      {fileId:12,name:'Optional translation',category:'OPTIONAL',version:'1.1',size:2048,uploadedAt:1792400000,description:'[i]Requires the main file[/i]'}
    ],
    changelogs:{
      '1.9':['Older release'],
      '2':['Major release'],
      'v1.10-rc.2':['Second release candidate'],
      '10':['Latest major release'],
      '2.0':['Fixes [b]a bug[/b]','New dependency'],
      '1.10':['Stable release'],
      'v1.10-rc.10':['Tenth release candidate'],
      '1.10-beta.2':['Beta release'],
      '1.10-alpha.1':['Alpha release'],
      '1.10.1':['Patch release']
    },
    requirements:[{name:'Required framework',url:'https://www.nexusmods.com/game1/mods/99',notes:'Install this first.'}]
  };
  async function run() {
    try {
      expect(typeof window.NexusM==='object','The application script did not initialize.');
      expect(window.NexusM.ready===true,'The interface must finish its initial render before reporting ready.');
      snapshot=await new Promise((resolve,reject)=>{
        const id=`ui-fixture-snapshot-${Date.now()}`;
        const listener=e=>{let m=e.data;if(typeof m==='string')try{m=JSON.parse(m);}catch{return;}if(String(m?.id)!==id)return;bridge.removeEventListener('message',listener);clearTimeout(timer);m.ok===false?reject(new Error(m.error)):resolve(m.result);};
        const timer=setTimeout(()=>{bridge.removeEventListener('message',listener);reject(new Error('Could not read the original library.'));},12000);
        bridge.addEventListener('message',listener);
        originalPost.call(bridge,{id,action:'getState',payload:{}});
      });
      bridge.postMessage=function(message){
        if(message.action==='getModDetails'){requests.push(message);queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:fakeDetails}));}
        else if(message.action==='getFileContents'){requests.push(message);previewRequests.set(String(message.id),message);}
        else if(message.action==='getBatchDownloadCandidates'){requests.push(message);batchCandidateRequests.set(String(message.id),message);}
        else if(message.action==='startBatchDownload'){requests.push(message);batchDownloadRequest=message;}
        else if(['dismissDownloadUpdate','replaceDownloadUpdate','deleteDownloadUpdate'].includes(message.action)){requests.push(message);downloadUpdateRequest=message;}
        else if(message.action==='checkDownloadUpdates'){requests.push(message);downloadUpdateCheckRequest=message;}
        else if(message.action==='refresh'){requests.push(message);refreshRequest=message;}
        else if(message.action==='importWebsite'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{opened:true}}));
        }
        else if(message.action==='saveSettings'&&typeof message.payload.yourGamesOnly==='boolean'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{settings:{yourGamesOnly:message.payload.yourGamesOnly}}}));
        }
        else if(message.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(message.payload,'downloadFolder')){
          requests.push(message);folderSaveRequest=message;
        }
        else if(message.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(message.payload,'maxParallelDownloads')){
          requests.push(message);queueSettingsRequest=message;
        }
        else if(message.action==='browseFolder'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:browseFolderResult}));
        }
        else if(message.action==='clearDownloads'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{downloads:[{id:'active-fixture',gameDomain:'game1',name:'Active archive',status:'downloading',bytes:1024,total:4096},{id:'queued-fixture',gameDomain:'game1',name:'Queued archive',status:'queued'}]}}));
        }
        else if(message.action==='clearDownloadFolder'){
          requests.push(message);folderClearRequest=message;
          queueMicrotask(()=>window.NexusM.receive({event:'state',data:{clearingDownloadFolder:true}}));
        }
        else if(message.action==='startDownload'){
          requests.push(message);
          if(deferFileDownloads)fileDownloadRequests.set(String(message.id),message);
          else queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{}}));
        }
        else if(message.action==='retryDownload'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:{}}));
        }
        else if(message.action==='openUrl'){
          requests.push(message);
          queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:true,result:null}));
        }
        else queueMicrotask(()=>window.NexusM.receive({id:message.id,ok:false,error:'Mutation blocked by UI fixture.'}));
      };
      window.NexusM.setState(fixture);
      window.NexusM.navigate('library');
      await check('large-library rendering benchmark preserves the full list and search',()=>{
        const mods=Array.from({length:2500},(_,index)=>({gameDomain:`game${index%9+1}`,modId:index+1,name:`Benchmark mod ${index+1}`,author:'Fixture author',version:'1.0',updatedAt:1792500000-index*60,lastDownloadedAt:1792100000-index*60,needsReview:index%3===0}));
        const large={...fixture,mods},metrics={mods:mods.length};
        const measure=(name,action)=>{const start=performance.now();action();document.body.offsetHeight;metrics[name]=Math.round((performance.now()-start)*10)/10;};
        try{
          measure('initialRenderMs',()=>window.NexusM.setState(large));
          expect($$('#mods-body tr[data-mod-key]').length===2500,'The complete large library must remain visible.');
          measure('unchangedRefreshMs',()=>window.NexusM.setState({...large,mods:mods.map(mod=>({...mod}))}));
          measure('downloadOnlyUpdateMs',()=>window.NexusM.setState({downloads:[{id:'benchmark-transfer',name:'Fixture download',status:'downloading',bytes:512,total:1024}]}));
          measure('searchMs',()=>{$('#mod-search').value='Benchmark mod 2499';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));});
          expect($$('#mods-body tr[data-mod-key]').length===1&&$('#mods-body tr').dataset.modKey==='game6:2499','Large-library search returned the wrong row.');
          report.checks.push({test:'Large-library timings',passed:true,metrics});
        }finally{$('#mod-search').value='';window.NexusM.setState(fixture);}
      });
      await check('nine games and all-game library render',()=>{
        expect($$('[data-select-game]').length===10,'Expected nine games plus All games.');
        expect($$('#mods-body tr[data-mod-key]').length===3,'Expected all three fixture mods.');
        expect($('#library-count').textContent==='3','Removed-game records should stay out of active-library counts.');
        expect(!$('#mods-body').innerText.includes('Preserved old-game history'),'Removed-game history should stay hidden while Your games only is enabled.');
        expect($('#your-games-only').getAttribute('aria-pressed')==='true','Your games only should default to enabled.');
      });
      await check('library checkboxes inspect only selected mods without opening details',async()=>{
        const before=requests.filter(request=>request.action==='getBatchDownloadCandidates').length;
        const detailsBefore=requests.filter(request=>request.action==='getModDetails').length;
        const input=$('[data-select-mod="game1:1"]'),row=input.closest('tr'),target=input.closest('label');
        expect(!before,'Rendering even a large library must not eagerly request file lists.');
        const bounds=target.getBoundingClientRect();
        expect(bounds.width>=44&&bounds.height>=44,'Library selection needs a padded target at least 44px wide and tall.');
        input.focus();
        const space=new KeyboardEvent('keydown',{key:' ',bubbles:true,cancelable:true});input.dispatchEvent(space);
        expect(!space.defaultPrevented&&document.activeElement===input,'Space on the checkbox must remain available for native keyboard activation.');
        expect(requests.filter(request=>request.action==='getModDetails').length===detailsBefore,'Checkbox keyboard events must not open the row.');
        target.click();
        const request=[...batchCandidateRequests.values()].pop();
        expect(request.payload.mods.length===1&&request.payload.mods[0].modId===1,'Checking a box should inspect only that mod.');
        expect(input.checked&&input.disabled&&input.getAttribute('aria-busy')==='true','An in-flight check should keep the checkbox checked and busy.');
        expect($('#download-selected-mods').disabled,'Downloading must wait for the file check.');
        expect(requests.filter(request=>request.action==='getModDetails').length===detailsBefore&&$('#detail-panel').classList.contains('hidden'),'Checking a box must not open mod details.');
        replyCandidate(request,{gameDomain:'game1',modId:1,eligible:true,status:'eligible',fileId:101,name:'Only main archive'});await settle();
        expect(input.checked&&!input.disabled&&!$('#download-selected-mods').disabled,'One main file with no optional files should become selected.');
        expect($('#download-selected-mods').textContent==='Download selected (1)','The batch button must show the selected count.');
        window.NexusM.setState({downloads:[...fixture.downloads,{id:'batch-progress',status:'downloading',bytes:10,total:100}]});
        expect($('[data-select-mod="game1:1"]')===input&&input.closest('tr')===row&&input.checked,'Download progress must preserve row identity and selection.');
        target.click();target.click();await settle();
        expect(requests.filter(request=>request.action==='getBatchDownloadCandidates').length===before+1&&input.checked,'Reselecting a verified unchanged mod should reuse its file check.');
        $('#clear-mod-selection').click();window.NexusM.setState(fixture);
      });
      await check('mods with file choices are disabled while failed file checks remain retryable',async()=>{
        const choices=$('[data-select-mod="game1:2"]');choices.click();
        replyCandidate([...batchCandidateRequests.values()].pop(),{gameDomain:'game1',modId:2,eligible:false,status:'ineligible',reason:'This mod has optional files. Open it to choose files.'});await settle();
        expect(!choices.checked&&choices.disabled&&choices.closest('tr').textContent.includes('optional files'),'Optional files must leave the mod unselected and explain why.');
        const before=requests.length;
        choices.closest('label').click();await settle();
        expect(!choices.checked&&requests.length===before&&$('#detail-panel').classList.contains('hidden'),'Clicking the enlarged disabled target must neither select nor open the mod.');
        expect(choices.closest('label').title.includes('optional files'),'The whole disabled target must explain why this mod cannot be selected.');
        const retry=$('[data-select-mod="game2:3"]');retry.click();
        replyCandidate([...batchCandidateRequests.values()].pop(),null,'Nexus is temporarily unavailable.');await settle();
        expect(!retry.checked&&!retry.disabled,'A failed API call must allow another selection attempt.');
        retry.click();replyCandidate([...batchCandidateRequests.values()].pop(),{gameDomain:'game2',modId:3,eligible:true,status:'eligible',fileId:103,name:'Single patch'});await settle();
        expect(retry.checked,'Retrying the file check should select an eligible mod.');
        $('#clear-mod-selection').click();
      });
      await check('batch selections survive filters and queue once while partial failures remain selected',async()=>{
        $('[data-select-mod="game1:1"]').click();$('[data-select-mod="game2:3"]').click();await settle();
        expect($('#download-selected-mods').textContent==='Download selected (2)','Both verified mods should be selected.');
        $('#mod-search').value='Alpha';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        expect($('#batch-selection-status').textContent.includes('1 selected mod is hidden'),'The selection count must include mods hidden by search.');
        window.NexusM.setState({clearingDownloadFolder:true});
        expect($('#download-selected-mods').disabled&&$('[data-select-mod="game1:1"]').disabled,'Folder cleanup must disable library download controls.');
        window.NexusM.setState({clearingDownloadFolder:false});
        const before=requests.filter(request=>request.action==='startBatchDownload').length;
        $('#download-selected-mods').click();$('#download-selected-mods').click();
        expect(requests.filter(request=>request.action==='startBatchDownload').length===before+1&&batchDownloadRequest.payload.mods.length===2,'Repeated clicks should start only one batch containing hidden selections.');
        window.NexusM.receive({id:batchDownloadRequest.id,ok:true,result:{state:{downloads:[...fixture.downloads,{id:'game1:1:101',gameDomain:'game1',modId:1,fileId:101,status:'waiting-authorization'}]},batch:{queued:1,skipped:0,failed:1,candidates:[{gameDomain:'game1',modId:1,fileId:101,eligible:true,status:'queued',reason:'Already in download queue.'},{gameDomain:'game2',modId:3,eligible:false,status:'failed',reason:'Temporary file lookup failure.'}]}}});batchDownloadRequest=null;await settle();
        expect($('#download-selected-mods').textContent==='Download selected (1)'&&!$('#download-selected-mods').disabled,'Only the failed selection should remain ready for retry.');
        $('#mod-search').value='';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        expect(!$('[data-select-mod="game1:1"]').checked&&$('[data-select-mod="game2:3"]').checked,'Queued mods must clear while failed mods stay selected.');
        $('#download-selected-mods').click();
        expect(batchDownloadRequest.payload.mods.length===1&&batchDownloadRequest.payload.mods[0].modId===3,'Retry should submit only the failed mod.');
        window.NexusM.receive({id:batchDownloadRequest.id,ok:true,result:{state:{downloads:fixture.downloads},batch:{queued:1,skipped:0,failed:0,candidates:[{gameDomain:'game2',modId:3,fileId:103,eligible:true,status:'queued'}]}}});batchDownloadRequest=null;await settle();
        expect($('#download-selected-mods').disabled&&$('#download-selected-mods').textContent==='Download selected (0)','A completed batch should clear all queued selections.');
        window.NexusM.setState(fixture);
      });
      await check('changed file choices on batch start clear the selection and explain why',async()=>{
        const input=$('[data-select-mod="game1:1"]');input.click();
        replyCandidate([...batchCandidateRequests.values()].pop(),{gameDomain:'game1',modId:1,eligible:true,status:'eligible',fileId:101});await settle();
        $('#download-selected-mods').click();
        window.NexusM.receive({id:batchDownloadRequest.id,ok:true,result:{state:{downloads:fixture.downloads},batch:{queued:0,skipped:1,failed:0,candidates:[{gameDomain:'game1',modId:1,eligible:false,status:'ineligible',reason:'This mod now has multiple main files. Open it to choose files.'}]}}});batchDownloadRequest=null;await settle();
        expect(!input.checked&&input.disabled&&input.closest('tr').textContent.includes('multiple main files'),'Fresh validation should stop a changed mod from downloading the wrong file.');
      });
      await check('metadata changes and cancelled checks cannot restore stale batch selections',async()=>{
        window.NexusM.setState({mods:fixture.mods.map(mod=>mod.modId===1?{...mod,version:'2.1'}:mod)});
        let input=$('[data-select-mod="game1:1"]');expect(!input.disabled,'A new version should allow file eligibility to be checked again.');input.click();
        const cancelled=[...batchCandidateRequests.values()].pop();$('#clear-mod-selection').click();
        replyCandidate(cancelled,{gameDomain:'game1',modId:1,eligible:true,status:'eligible',fileId:201});await settle();
        expect(!input.checked&&$('#download-selected-mods').disabled,'Clearing an in-flight selection must ignore its late response.');
        input.click();const stale=[...batchCandidateRequests.values()].pop();
        window.NexusM.setState({mods:fixture.mods.filter(mod=>mod.modId!==1)});
        replyCandidate(stale,{gameDomain:'game1',modId:1,eligible:true,status:'eligible',fileId:201});await settle();
        expect($('#download-selected-mods').disabled,'Removing a mod during its check must prevent a hidden stale selection.');
        window.NexusM.setState(fixture);
      });
      await check('unrelated state updates preserve library rows, focus, and game controls',()=>{
        const row=$('#mods-body tr'),game=$('[data-select-game="game1"]'),account=$('#account button');
        row.focus();
        window.NexusM.setState({...fixture,mods:fixture.mods.map(mod=>({...mod,summary:'Updated background metadata',lastCheckedAt:1792600000})),lastCheckedAt:1792600000});
        expect($('#mods-body tr')===row&&document.activeElement===row,'An identical visible library must retain its focused row.');
        expect($('[data-select-game="game1"]')===game&&$('#account button')===account,'Unchanged game and account controls must retain their DOM nodes.');
        window.NexusM.setState({downloads:[{id:'render-fixture',name:'Progress archive',status:'downloading',bytes:100,total:200}]});
        expect($('#mods-body tr')===row&&document.activeElement===row,'A download-only state update must not rebuild the library.');
        expect($('#last-checked').textContent.startsWith('Checked '),'The latest check date should still update without replacing rows.');
        window.NexusM.setState({mods:fixture.mods.map(mod=>mod.modId===1?{...mod,version:'9.9'}:mod)});
        expect($('[data-mod-key="game1:1"] .version-chip').textContent==='9.9','A changed visible mod field must still repaint the library.');
        window.NexusM.setState(fixture);
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
      await check('newly synced games explain missing browser history and open the selected import',async()=>{
        const game={id:100,domain:'phantombladezero',name:'Phantom Blade Zero'};
        try{
          window.NexusM.receive({event:'state',data:{games:[...fixture.games,game]}});
          $('[data-select-game="phantombladezero"]').click();
          expect(!$('#library-empty').classList.contains('hidden')&&$('#mods-table-wrap').classList.contains('hidden'),'A synced game without imported records needs its own actionable empty state.');
          expect($('#library-empty h3').textContent==='No mods imported for Phantom Blade Zero','The empty state must identify the selected game.');
          expect($('#library-empty p').textContent.includes('downloaded in your browser')&&$('#library-empty p').textContent.includes('already in your library'),'The empty state must explain game sync, history import, and the scope of update checks.');
          expect($('#empty-import').textContent==='Import this game’s history'&&$('#empty-add'),'The selected game needs direct history import and Add a mod actions.');
          $('#empty-import').click();
          $('[data-select-game="game9"]').click();
          await settle();
          const request=requests.filter(request=>request.action==='importWebsite').at(-1);
          expect(request?.payload.kind==='history'&&request.payload.gameDomain==='phantombladezero','Direct import must use the game selected when clicked, even if the selection changes while opening.');
          expect(!$('#input-dialog').open,'Direct game history import must not require an intermediate app dialog.');
          $('[data-select-game="phantombladezero"]').click();
          const mod={gameDomain:game.domain,modId:10,name:'Imported Phantom Blade mod',version:'1.0',lastDownloadedAt:1792500000,updatedAt:1792400000,needsReview:false};
          window.NexusM.receive({event:'state',data:{mods:[...fixture.mods,mod]}});
          expect($('#library-empty').classList.contains('hidden')&&!$('#mods-table-wrap').classList.contains('hidden'),'Imported records must replace the empty state while retaining the selected game.');
          expect($$('#mods-body tr[data-mod-key]').length===1&&$('#mods-body tr').dataset.modKey==='phantombladezero:10','The selected game must immediately display its imported mod.');
        }finally{window.NexusM.setState(fixture);}
      });
      await check('search and review filters keep their normal no-results state for imported games',()=>{
        try{
          $('[data-select-game="game1"]').click();
          $('#mod-search').value='a search that matches nothing';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
          expect($('#library-empty').classList.contains('hidden')&&!$('#mods-table-wrap').classList.contains('hidden')&&$('#mods-body .no-results'),'A search hiding known mods must remain a filter result, not ask for another history import.');
          $('#mod-search').value='';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
          window.NexusM.setState({mods:fixture.mods.map(mod=>({...mod,needsReview:false}))});
          $('[data-filter="updates"]').click();
          expect($('#library-empty').classList.contains('hidden')&&$('#mods-body .no-results'),'An imported game without updates must remain a review-filter result.');
        }finally{$('#mod-search').value='';$('[data-filter="all"]').click();$('[data-select-game=""]').click();window.NexusM.setState(fixture);}
      });
      await check('keyboard focus reveals each game removal control',()=>{
        const game=$('[data-select-game="game1"]'),remove=game.closest('.game-card').querySelector('[data-remove-game]');
        game.focus();
        expect(getComputedStyle(remove).display!=='none','Focusing a game must reveal its removal control without hovering.');
        remove.focus();
        expect(document.activeElement===remove&&getComputedStyle(remove).display!=='none','Keyboard users must be able to reach and retain focus on game removal.');
        game.focus();
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
      await check('library counts follow live mod, review, and game changes',()=>{
        const added={gameDomain:'game1',modId:5,name:'New live mod',needsReview:false};
        window.NexusM.receive({event:'state',data:{mods:[...fixture.mods,added]}});
        expect($('#library-count').textContent==='4'&&$('#all-count').textContent==='4'&&$('#updates-count').textContent==='2','Adding a mod should immediately update library and filter counts.');
        expect($('[data-select-game=""] small').textContent==='4 mods'&&$('[data-select-game="game1"]').title.includes('3 mods'),'Game card counts should immediately include new mods.');
        $('[data-select-game="game1"]').click();$('[data-filter="updates"]').click();
        window.NexusM.receive({event:'state',data:{mods:[...fixture.mods.map(m=>m.modId===1?{...m,needsReview:false}:m),added]}});
        expect($('#all-count').textContent==='3'&&$('#updates-count').textContent==='0'&&$('#visible-count').textContent==='0 of 3 mods','Review changes should update the selected game and filtered row count.');
        window.NexusM.receive({event:'state',data:{games:fixture.games.filter(g=>g.domain!=='game1')}});
        expect($('#game-count').textContent==='8'&&$('#library-count').textContent==='1'&&$('#all-count').textContent==='1'&&$('#mods-title').textContent==='All mods','Removing the selected game should reset its scope and update all counts.');
        $('[data-filter="all"]').click();window.NexusM.setState(fixture);
      });
      await check('mod details support keyboard tabs, contain focus, and restore the library opener',async()=>{
        const opener=$('[data-mod-key="game1:1"]');opener.focus();
        const key=(element,value,extra={})=>{const event=new KeyboardEvent('keydown',{key:value,bubbles:true,cancelable:true,...extra});element.dispatchEvent(event);return event;};
        try{
          await window.NexusM.showMod('game1:1');
          const close=$('#detail-close'),files=$('[data-detail-tab="files"]'),overview=$('[data-detail-tab="overview"]'),requirements=$('[data-detail-tab="requirements"]');
          expect(document.activeElement===close,'Opening details should focus their close button.');
          $('#mod-search').focus();expect(document.activeElement===close,'The library behind open details must reject focus.');
          $('.sidebar .nav-item').focus();expect(document.activeElement===close,'Sidebar navigation must stay out of the open dialog.');
          files.focus();key(files,'ArrowRight');
          expect(document.activeElement===overview&&overview.getAttribute('aria-selected')==='true'&&$('#detail-tab-body').textContent.includes('Safe summary'),'Right arrow should focus and activate Overview.');
          expect(files.tabIndex===-1&&overview.tabIndex===0&&$('#detail-tab-body').getAttribute('aria-labelledby')===overview.id,'Only the active tab should be in the tab order and name its panel.');
          key(overview,'Home');expect(document.activeElement===files,'Home should activate the first tab.');
          key(files,'ArrowLeft');expect(document.activeElement===requirements,'Left arrow on the first tab should wrap to Requirements.');
          key(requirements,'Home');key(files,'End');expect(document.activeElement===requirements,'End should activate the last tab.');
          key(requirements,'Home');
          const first=$('#detail-external'),last=$('[data-download-file="12"]');
          last.focus();const forward=key(last,'Tab');
          expect(forward.defaultPrevented&&document.activeElement===first,'Tab at the end of details should wrap to their first control.');
          last.disabled=true;const backward=key(first,'Tab',{shiftKey:true});
          expect(backward.defaultPrevented&&document.activeElement===$('[data-preview-file="12"]'),'Reverse Tab should wrap past disabled controls.');
          last.disabled=false;key(document.activeElement,'Escape');
          expect($('#detail-panel').classList.contains('hidden')&&document.activeElement===opener,'Escape should close details and return focus to the library opener.');
          expect(!$('main').inert&&!$('.sidebar').inert,'Closing details must restore background interaction.');
        }finally{if(!$('#detail-panel').classList.contains('hidden'))$('#detail-close').click();}
      });
      await check('multiple main files and optional files stay separate',async()=>{
        await window.NexusM.showMod('game1:1');
        expect($$('.file-group').length===2,'Expected main and optional file groups.');
        expect($$('.file-group')[0].querySelectorAll('.file-card').length===2,'Both main files must remain selectable.');
        expect($$('.file-group')[1].querySelectorAll('.file-card').length===1,'Optional file should have its own group.');
        expect($('#mark-reviewed-button'),'Mark reviewed control is missing.');
        const main=$('[data-select-file="10"]'),optional=$('[data-select-file="12"]');
        expect(main.getBoundingClientRect().width>=20&&main.getBoundingClientRect().height>=20,'File checkboxes should be visually large enough to find.');
        expect(main.closest('label').getBoundingClientRect().height>=44,'File selection headings need a comfortably sized click target.');
        main.closest('label').querySelector('strong').click();optional.closest('label').click();
        expect($('#selected-files-count').textContent==='2'&&!$('#download-selected').disabled,'Multiple file selection did not update.');
        optional.closest('label').click();
        expect(!optional.checked&&$('#selected-files-count').textContent==='1','Clicking a file heading again must deselect exactly once.');
        optional.click();
      });
      await check('downloaded flags identify exact main and optional files',()=>{
        expect($('[data-file-card="10"] .file-download-flag.downloaded')?.textContent==='Downloaded','The downloaded main file needs a visible check flag.');
        expect($('[data-file-card="12"] .file-download-flag.downloaded')?.textContent==='Downloaded','An optional file from persistent file history should be flagged.');
        expect($('[data-file-card="11"] .file-download-flag')?.textContent==='No download recorded','Mod-level dates, failed records, and matching IDs from other mods or games must not flag this alternative.');
        expect($('[data-file-card="10"] .file-downloaded-date').textContent.startsWith('Last downloaded '),'The exact file download date is missing.');
        expect($('.file-history-note').textContent.includes('Older downloads without file-level history'),'Unknown historical file downloads need an explanation.');
      });
      await check('completed downloads update open file flags without losing selection or fetching details',()=>{
        const input=$('[data-select-file="12"]'),detailsRequests=requests.filter(r=>r.action==='getModDetails').length;
        input.focus();
        const record={id:'game1:1:11',gameDomain:'game1',modId:1,fileId:11,name:'Main alternative',downloadedAt:1792600000};
        window.NexusM.receive({event:'download',data:{...record,status:'failed'}});
        expect(!$('[data-file-card="11"] .file-download-flag').classList.contains('downloaded'),'An unsuccessful transfer must not receive a downloaded flag.');
        window.NexusM.receive({event:'download',data:{...record,status:'completed',downloadedAt:0}});
        expect(!$('[data-file-card="11"] .file-download-flag').classList.contains('downloaded'),'A missing download date must not turn into a year-2000 downloaded flag.');
        window.NexusM.receive({event:'download',data:{...record,status:'completed'}});
        expect($('[data-file-card="11"] .file-download-flag.downloaded')?.textContent==='Downloaded','Completing a transfer should immediately flag the open file.');
        expect($('[data-select-file="12"]')===input&&document.activeElement===input,'Updating a flag must preserve the focused checkbox.');
        expect(input.checked&&$('[data-select-file="10"]').checked&&$('#selected-files-count').textContent==='2','Updating a flag must retain selected files.');
        expect(requests.filter(r=>r.action==='getModDetails').length===detailsRequests,'A completed flag should not fetch the mod details again.');
        window.NexusM.setState({fileDownloads:{...fixture.fileDownloads,'game1:1:11':{...record,status:'completed'}},downloads:[]});
        expect($('[data-file-card="11"] .file-download-flag.downloaded'),'Clearing the visible list must keep the persisted file flag.');
        window.NexusM.setState({fileDownloads:fixture.fileDownloads,downloads:fixture.downloads});
      });
      await check('file previews show exact archive paths safely without downloading or losing selections',async()=>{
        const buttons=$$('[data-preview-file]'),input=$('[data-select-file="12"]');
        expect(buttons.length===3&&buttons.every(button=>button.textContent==='Preview file contents'),'Every main and optional file needs its own preview button.');
        const before=requests.filter(r=>r.action==='getFileContents').length,downloads=requests.filter(r=>r.action==='startDownload').length;
        expect(before===0,'Archive previews must remain lazy until requested.');
        const button=$('[data-preview-file="10"]');button.click();
        const request=requests.filter(r=>r.action==='getFileContents').at(-1);
        expect(request?.payload.gameDomain==='game1'&&request.payload.modId===1&&request.payload.fileId===10,'Preview must request the exact selected main file.');
        expect(button.getAttribute('aria-controls')==='file-preview-10'&&button.getAttribute('aria-expanded')==='true','The expanded preview needs an accessible control relationship.');
        expect($('#file-preview-10 [role="status"]')?.textContent.includes('Loading'),'A pending preview needs an inline loading status.');
        button.click();button.click();
        expect(requests.filter(r=>r.action==='getFileContents').length===before+1,'Repeated toggles while loading must share the same request.');
        const unsafeName='<img src=x onerror="window.__previewInjected=true">.pak';
        replyPreview(request,{available:true,entries:[
          {path:'Mods/Data/main.pak',size:128,isDirectory:false},
          {path:'Mods/'+unsafeName,size:64,isDirectory:false},
          {path:'README.txt',size:32,isDirectory:false},
          {path:'Empty',size:0,isDirectory:true}
        ],notice:'',truncated:false});await settle();
        const panel=$('#file-preview-10');
        expect(!panel.classList.contains('hidden')&&panel.textContent.includes('main.pak')&&panel.textContent.includes('README.txt'),'The expanded preview must show archive file names.');
        expect([...panel.querySelectorAll('.preview-folder summary')].some(folder=>folder.textContent==='Mods')&&[...panel.querySelectorAll('.preview-folder summary')].some(folder=>folder.textContent==='Data'),'Archive paths should render as navigable folders.');
        expect([...panel.querySelectorAll('.preview-file-name')].some(file=>file.textContent===unsafeName),'HTML-looking file names should remain literal text.');
        expect(!panel.querySelector('img,script,[onerror]')&&!window.__previewInjected,'Archive names must never become executable markup.');
        expect(panel.textContent.includes('128 B')&&panel.textContent.includes('Empty folder'),'File sizes and empty directories should be represented.');
        expect($('[data-select-file="12"]')===input&&input.checked&&$('[data-select-file="10"]').checked&&$('#selected-files-count').textContent==='2','Opening a preview must preserve file selections.');
        expect(requests.filter(r=>r.action==='startDownload').length===downloads,'Previewing file contents must not start an archive download.');
        button.click();expect(panel.classList.contains('hidden')&&button.getAttribute('aria-expanded')==='false','The preview should collapse on another click.');
        button.click();expect(!panel.classList.contains('hidden'),'A cached preview should reopen immediately.');
        $('[data-detail-tab="overview"]').click();$('[data-detail-tab="files"]').click();
        expect(!$('#file-preview-10').classList.contains('hidden')&&$('#file-preview-10').textContent.includes('main.pak'),'Returning to Files should preserve the open preview.');
        expect(requests.filter(r=>r.action==='getFileContents').length===before+1,'Reopening and switching tabs must reuse the preview already fetched.');
      });
      await check('optional file previews handle unavailable archive metadata independently',async()=>{
        $('[data-preview-file="12"]').click();
        const request=requests.filter(r=>r.action==='getFileContents').at(-1);
        expect(request?.payload.gameDomain==='game1'&&request.payload.modId===1&&request.payload.fileId===12,'The optional file must request its own archive rather than a main file.');
        replyPreview(request,{available:false,entries:[],notice:'Nexus has not indexed this archive yet.',truncated:false});await settle();
        expect($('#file-preview-12').textContent.includes('Nexus has not indexed this archive yet.'),'An unavailable preview should show the Nexus explanation.');
        expect(!$('#file-preview-12').querySelector('.preview-file'),'Unavailable metadata must not reuse another file\'s contents.');
        expect($('#file-preview-10').textContent.includes('main.pak'),'Opening an optional preview must not replace a main preview.');
      });
      await check('failed previews show an inline retry and empty archives have a clear result',async()=>{
        $('[data-preview-file="11"]').click();
        const request=requests.filter(r=>r.action==='getFileContents').at(-1);
        replyPreview(request,null,'Preview temporarily unavailable.');await settle();
        expect($('#file-preview-11 [role="alert"]')?.textContent.includes('Preview temporarily unavailable.'),'A failed preview needs a visible inline error.');
        const retry=$('[data-retry-preview="11"]');expect(retry,'A failed preview needs a retry control.');
        const before=requests.filter(r=>r.action==='getFileContents').length;retry.click();
        const second=requests.filter(r=>r.action==='getFileContents').at(-1);
        expect(requests.filter(r=>r.action==='getFileContents').length===before+1&&second.payload.fileId===11,'Retry should fetch only the failed file again.');
        replyPreview(second,{available:true,entries:[],notice:'',truncated:false});await settle();
        expect($('#file-preview-11').textContent.includes('No files are listed in this archive.')&&!$('#file-preview-11 [role="alert"]'),'An empty successful preview should replace the error with a clear result.');
      });
      await check('late preview responses cannot replace a newly opened mod detail',async()=>{
        $('#detail-close').click();await window.NexusM.showMod('game1:1');
        $('[data-preview-file="10"]').click();
        const stale=requests.filter(r=>r.action==='getFileContents').at(-1);
        $('#detail-close').click();await window.NexusM.showMod('game1:1');
        replyPreview(stale,{available:true,entries:[{path:'STALE-PREVIEW.pak',size:1,isDirectory:false}],notice:'',truncated:false});await settle();
        expect($('#file-preview-10').classList.contains('hidden')&&!$('#detail-panel').textContent.includes('STALE-PREVIEW.pak'),'A response for closed details must not render in a new detail session.');
        const before=requests.filter(r=>r.action==='getFileContents').length;$('[data-preview-file="10"]').click();
        const current=requests.filter(r=>r.action==='getFileContents').at(-1);
        expect(requests.filter(r=>r.action==='getFileContents').length===before+1,'A new detail session must not inherit a stale preview request.');
        replyPreview(current,{available:true,entries:[{path:'Fresh.pak',size:1,isDirectory:false}],notice:'',truncated:true});await settle();
        expect($('#file-preview-10').textContent.includes('Fresh.pak')&&$('#file-preview-10').textContent.includes('only part of the archive listing'),'A fresh partial preview should show its files and truncation notice.');
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
      await check('selected-file batches stay busy across tabs and retain failed selections',async()=>{
        await window.NexusM.showMod('game1:1');
        $('[data-select-file="10"]').click();$('[data-select-file="12"]').click();
        const before=requests.filter(request=>request.action==='startDownload').length;
        deferFileDownloads=true;
        try{
          $('#download-selected').click();
          const first=[...fileDownloadRequests.values()][0];
          expect(first?.payload.fileId===10,'A selected-file batch should start with its first chosen file.');
          $('[data-detail-tab="overview"]').click();$('[data-detail-tab="files"]').click();
          expect($('#download-selected').disabled&&$('#download-selected').getAttribute('aria-busy')==='true','Returning to Files must retain the in-flight batch state.');
          expect($$('[data-download-file]').every(button=>button.disabled),'Individual file buttons must wait for the accepted batch to finish queueing.');
          $('#download-selected').click();
          expect(requests.filter(request=>request.action==='startDownload').length===before+1,'Switching tabs must not allow a duplicate selected-file batch.');
          replyFileDownload(first);await settle();
          const second=[...fileDownloadRequests.values()][0];
          expect(second?.payload.fileId===12&&second.payload.modId===1,'The batch should queue the second chosen file exactly once.');
          replyFileDownload(second,'Fixture download could not be queued.');await settle();
          expect(!$('[data-select-file="10"]').checked&&$('[data-select-file="12"]').checked,'Only successfully queued selections should clear.');
          expect(!$('#download-selected').disabled&&$('#download-selected').getAttribute('aria-busy')==='false','A failed selection must remain available for retry after the batch finishes.');
        }finally{
          deferFileDownloads=false;
          for(const request of [...fileDownloadRequests.values()])replyFileDownload(request,'UI fixture cleanup.');
          await settle();
        }
      });
      await check('accepted file batches keep their original mod identity when another mod opens',async()=>{
        await window.NexusM.showMod('game1:1');
        $('[data-select-file="10"]').click();$('[data-select-file="12"]').click();
        const originalMod=fakeDetails.mod;
        deferFileDownloads=true;
        try{
          $('#download-selected').click();
          const first=[...fileDownloadRequests.values()][0];
          $('#detail-close').click();
          fakeDetails.mod={...fixture.mods[1],description:originalMod.description};
          await window.NexusM.showMod('game1:2');
          const newSelection=$('[data-select-file="12"]');newSelection.click();
          expect(newSelection.checked&&$('#download-selected').disabled,'A new detail session may select files while the accepted batch finishes.');
          replyFileDownload(first);await settle();
          const second=[...fileDownloadRequests.values()][0];
          expect(second?.payload.gameDomain==='game1'&&second.payload.modId===1&&second.payload.fileId===12,'Remaining file IDs must use the original mod identity after opening another mod.');
          replyFileDownload(second);await settle();
          expect($('[data-select-file="12"]')===newSelection&&newSelection.checked&&$('#selected-files-count').textContent==='1','Old batch replies must preserve the new mod\'s checkbox and selection.');
          expect(!$('#download-selected').disabled,'The new mod selection should become available when the prior batch finishes.');
        }finally{
          deferFileDownloads=false;fakeDetails.mod=originalMod;
          for(const request of [...fileDownloadRequests.values()])replyFileDownload(request,'UI fixture cleanup.');
          await settle();$('#detail-close').click();await window.NexusM.showMod('game1:1');
        }
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
      await check('Quest Trigger Fixes BBCode renders with or without surrounding HTML',async()=>{
        const original=fakeDetails.mod.description;
        const sample="[color=#ffff00][size=5][b]QUEST TRIGGER FIXES[/b][/size][/color]\n\nQuest Trigger Fixes is a gameplay mod that fixes quests still bugged in the latest patch, enabling you to play the game closer to how it was intended to work.\n\nUpdated releases include 'Compatibility Version' to avoid conflict with most popular mods. The full release is now known as the 'All-In-One' (AIO) version.\n\nSupports Update 2.5.\n\n[b]Quests[/b]";
        try{
          for(const description of [sample,`<div>${sample.replace(/\n/g,'<br>')}</div>`,`<div>${sample}</div>`]){
            fakeDetails.mod.description=description;await window.NexusM.showMod('game1:1');$('[data-detail-tab="overview"]').click();
            const content=$('#detail-tab-body .rich-text'),title=content.querySelector('strong');
            expect(title?.textContent==='QUEST TRIGGER FIXES','The supplied title should be bold.');
            expect(getComputedStyle(title).color==='rgb(255, 255, 0)','The supplied yellow title should retain its color.');
            expect(parseFloat(getComputedStyle(title).fontSize)>parseFloat(getComputedStyle(content).fontSize),'The supplied size should enlarge the title.');
            expect(!/\[\/?(?:color|size|b)[^\]]*\]/i.test(content.textContent),'Supported BBCode must not remain visible.');
            expect(content.textContent.includes('Supports Update 2.5.')&&content.querySelectorAll('br').length>=6,'Body text and paragraph breaks must remain readable.');
          }
        }finally{fakeDetails.mod.description=original;await window.NexusM.showMod('game1:1');}
      });
      await check('nested BBCode keeps lists, quotes, spoilers and literal code examples readable',async()=>{
        const original=fakeDetails.mod.description;
        try{
          fakeDetails.mod.description='[quote][b]Outer [b]inner[/b] end[/b][/quote][list=1][*]First[list][*]Nested[/list][*]Second[/list][spoiler=Install details]Hidden instructions[/spoiler][code][b]literal[/b]\n<unsafe> & text[/code]';
          await window.NexusM.showMod('game1:1');$('[data-detail-tab="overview"]').click();
          const content=$('#detail-tab-body .rich-text');
          expect(content.querySelector('blockquote strong strong')?.textContent==='inner'&&!content.querySelector('blockquote').textContent.includes('[/b]'),'Repeated nested tags should balance.');
          expect(content.querySelectorAll('ol>li').length===2&&content.querySelector('ol>li>ul>li')?.textContent==='Nested','Numbered and nested lists should retain their hierarchy.');
          const spoiler=content.querySelector('details');
          expect(spoiler&&!spoiler.open&&spoiler.querySelector('summary').textContent==='Install details','Spoilers should use a labeled disclosure.');
          spoiler.querySelector('summary').click();expect(spoiler.open,'Spoiler instructions should be reachable by clicking the disclosure.');
          expect(content.querySelector('pre code')?.textContent==='[b]literal[/b]\n<unsafe> & text'&&!content.querySelector('pre br'),'Code samples should preserve literal tags and newlines.');
        }finally{fakeDetails.mod.description=original;await window.NexusM.showMod('game1:1');}
      });
      await check('mixed author markup rejects unsafe URLs, styles and attribute injection',async()=>{
        const original=fakeDetails.mod.description;
        try{
          fakeDetails.mod.description='<p>[b]Safe instructions[/b]</p>[color=red;position:fixed]Readable[/color][url=javascript:alert(1)]Bad link[/url][img]data:image/svg+xml,unsafe[/img][url="https://example.com/?a=1&b=2"]Safe link[/url][url]https://example.com/?x=1&y=2[/url][img]https://example.com/image.png" onerror="window.__uiInjected=true[/img]<span style="position:fixed;background:url(https://example.com);color:#ffff00" onclick="window.__uiInjected=true">Allowed color</span><code>[b]HTML code sample[/b]</code><script>window.__uiInjected=true</script>';
          await window.NexusM.showMod('game1:1');$('[data-detail-tab="overview"]').click();
          const content=$('#detail-tab-body .rich-text');
          expect(content.querySelector('p strong')?.textContent==='Safe instructions','BBCode in an HTML paragraph should render.');
          expect(!content.querySelector('[onclick],[onerror],script,a[href^="javascript:"],img[src^="data:"]')&&!window.__uiInjected,'Unsafe markup or attributes reached the reader.');
          expect([...content.querySelectorAll('[style]')].every(node=>!node.style.position&&!node.style.background),'Only supported text formatting may survive author styles.');
          expect([...content.querySelectorAll('a[href]')].map(a=>a.href).includes('https://example.com/?a=1&b=2'),'Quoted links should preserve query parameters.');
          expect(content.querySelector('code')?.textContent==='[b]HTML code sample[/b]','Existing HTML code samples must stay literal.');
        }finally{fakeDetails.mod.description=original;await window.NexusM.showMod('game1:1');}
      });
      await check('mixed formatting works in file notes, changelogs and requirements',async()=>{
        const file=fakeDetails.files[0].description,logs=fakeDetails.changelogs,requirements=fakeDetails.requirements;
        try{
          fakeDetails.files[0].description='<p>[b]File install notes[/b]</p>';
          fakeDetails.changelogs={'3.0':['<div>[i]Release notes[/i]</div>']};
          fakeDetails.requirements=[{name:'Framework',notes:'<p>[u]Install first[/u]</p>'}];
          await window.NexusM.showMod('game1:1');
          expect($('[data-file-card="10"] .rich-text strong')?.textContent==='File install notes','File notes should render mixed formatting.');
          $('[data-detail-tab="changelog"]').click();expect($('.changelog-entry em')?.textContent==='Release notes','Changelog notes should render mixed formatting.');
          $('[data-detail-tab="requirements"]').click();expect($('.requirement-card u')?.textContent==='Install first','Requirement notes should render mixed formatting.');
        }finally{fakeDetails.files[0].description=file;fakeDetails.changelogs=logs;fakeDetails.requirements=requirements;await window.NexusM.showMod('game1:1');}
      });
      await check('encoded BBCode links and mixed raw line breaks preserve author content',async()=>{
        const original=fakeDetails.mod.description;
        try{
          fakeDetails.mod.description='First line\nSecond line\n<img src="https://example.com/a.png">\nThird line\n[url=&quot;https://example.com/?a=1&amp;b=2&quot;]Guide[/url]\n[url=https://example.com/?a=1&amp;b=2]Other guide[/url]\n[url]https://example.com/?x=1&y=2[/url]<pre><code>literal</code>\n[b]still literal[/b]</pre><p>Paragraph</p>\n<p>Next paragraph</p>';
          await window.NexusM.showMod('game1:1');$('[data-detail-tab="overview"]').click();
          const content=$('#detail-tab-body .rich-text'),links=[...content.querySelectorAll('a[href]')];
          expect(links.length===3&&links[0].href==='https://example.com/?a=1&b=2'&&links[1].href===links[0].href&&links[2].href==='https://example.com/?x=1&y=2','URL parameters should be decoded once and preserve their query strings.');
          expect(content.querySelectorAll('br').length>=5,'Inline HTML must not collapse surrounding plain-text line breaks.');
          expect(content.querySelector('pre').textContent==='literal\n[b]still literal[/b]'&&!content.querySelector('pre strong'),'Everything in an HTML pre block must remain literal.');
          expect(!content.innerHTML.includes('</p><br><p>'),'HTML indentation between paragraphs must not add blank lines.');
        }finally{fakeDetails.mod.description=original;await window.NexusM.showMod('game1:1');}
      });
      await check('wide author tables and code scroll within the details panel',async()=>{
        const original=fakeDetails.mod.description;
        try{
          fakeDetails.mod.description=`<table><tr>${Array.from({length:48},(_,index)=>`<td>Column ${index}</td>`).join('')}</tr></table><pre><code>${'long_code_example_'.repeat(100)}</code></pre>`;
          await window.NexusM.showMod('game1:1');$('[data-detail-tab="overview"]').click();
          const panel=$('#detail-panel'),table=$('#detail-tab-body table'),code=$('#detail-tab-body pre');
          expect(panel.scrollWidth<=panel.clientWidth+1,'Wide author content must not overflow the whole details panel.');
          for(const element of [table,code]){
            expect(element.scrollWidth>element.clientWidth,'Wide tables and code examples need their own horizontal scroll area.');
            element.focus();expect(document.activeElement===element,'Scrollable author content must be reachable by keyboard.');
            element.scrollLeft=100;expect(element.scrollLeft>0,'Author content must scroll horizontally to expose the remaining text.');
          }
        }finally{fakeDetails.mod.description=original;await window.NexusM.showMod('game1:1');}
      });
      await check('dictionary changelogs sort latest first and keep author note order',()=>{
        $('[data-detail-tab="changelog"]').click();
        const versions=$$('.changelog-entry h3').map(el=>el.textContent.replace(/^Version /,''));
        expect(JSON.stringify(versions)===JSON.stringify(['10','2','2.0','1.10.1','1.10','v1.10-rc.10','v1.10-rc.2','1.10-beta.2','1.10-alpha.1','1.9']),'Changelogs should use descending versions, including numeric object keys, multi-digit components, v prefixes, and previews.');
        const release=$$('.changelog-entry').find(el=>el.querySelector('h3').textContent==='Version 2.0');
        expect(JSON.stringify([...release.querySelectorAll('li')].map(el=>el.textContent))===JSON.stringify(['Fixes a bug','New dependency']),'Sorting releases must preserve the author\'s note order.');
        expect(release.querySelector('strong')?.textContent==='a bug','Author formatting should remain in release notes.');
      });
      await check('array changelogs sort latest first across release and preview formats',async()=>{
        const original=fakeDetails.changelogs;
        try{
          fakeDetails.changelogs=[
            {version:'v1.9',changes:['Older release']},
            {version:'1.10-beta.2',changes:['Beta release']},
            {version:'v1.10+build.2',changes:['Stable release']},
            {version:'1.10-rc.2',changes:['Second release candidate']},
            {version:'1.10-rc.10',changes:['Tenth release candidate']},
            {version:'1.10-alpha.1',changes:['Alpha release']},
            {name:'2.0',changelog:[{text:'First author note'},{description:'Second author note'}]}
          ];
          await window.NexusM.showMod('game1:1');$('[data-detail-tab="changelog"]').click();
          const versions=$$('.changelog-entry h3').map(el=>el.textContent.replace(/^Version /,''));
          expect(JSON.stringify(versions)===JSON.stringify(['2.0','v1.10+build.2','1.10-rc.10','1.10-rc.2','1.10-beta.2','1.10-alpha.1','v1.9']),'Array changelogs should use the same version order, treating build metadata as a stable release.');
          expect(JSON.stringify([...$('.changelog-entry').querySelectorAll('li')].map(el=>el.textContent))===JSON.stringify(['First author note','Second author note']),'Array release notes must retain their author order.');
        }finally{fakeDetails.changelogs=original;}
      });
      await check('requirement content renders',()=>{
        $('[data-detail-tab="requirements"]').click();expect($('#detail-tab-body').innerText.includes('Required framework'),'Requirement missing.');
      });
      $('#detail-close').click();
      await check('interrupted downloads remain visible without increasing the downloaded count',()=>{
        window.NexusM.navigate('downloads');
        expect($('[data-retry-download="interrupted-fixture"]'),'Interrupted download needs a Retry control.');
        expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 0 active, 1 entry','An interrupted entry must not count as downloaded.');
        expect($('#downloads-summary').textContent==='0 downloaded, 0 active, 1 entry','The visible summary should distinguish downloaded files from list entries.');
      });
      await check('download count follows queue events, completion, retries, and clearing',()=>{
        const record={id:'live-count-fixture',name:'Live count archive',status:'queued'};
        window.NexusM.receive({event:'download',data:record});
        expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 1 active, 2 entries','Queuing a file should increase active entries without counting it as downloaded.');
        expect($('#downloads-summary').textContent==='0 downloaded, 1 active, 2 entries','The summary should immediately reflect a queued entry.');
        window.NexusM.receive({event:'downloadProgress',data:{id:record.id,status:'downloading',bytes:1024,total:4096}});
        expect($('#download-count').textContent==='0'&&$$('#downloads-list .download-card').length===2,'Progress should update the existing entry without counting it as downloaded or adding a duplicate.');
        for(const status of ['completed','failed','cancelled']){
          window.NexusM.receive({event:'download',data:{id:record.id,status}});
          const count=status==='completed'?1:0,summary=`${count} downloaded, 0 active, 2 entries`;
          expect($('#download-count').textContent===String(count)&&$('#download-count').title===summary,`Only completed entries should count as downloaded after a ${status} event.`);
          expect($('#downloads-summary').textContent===summary&&$$('#downloads-list .download-card').length===2,'Status changes should update the summary while keeping both entries visible.');
        }
        window.NexusM.receive({event:'download',data:{id:record.id,status:'waiting-authorization'}});
        expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 1 active, 2 entries','Retrying an entry should update its active status without counting it as downloaded.');
        window.NexusM.receive({event:'download',data:{downloads:[]}});
        expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 0 active, 0 entries'&&!$$('#downloads-list .download-card').length,'An empty replacement list should reset the count immediately.');
        expect($('#downloads-summary').textContent==='0 downloaded, 0 active, 0 entries','Clearing the list should reset the visible summary.');
        window.NexusM.setState(fixture);
      });
      await check('download counts separate completed files, active transfers, and all entries',()=>{
        const statuses=['completed','completed','queued','authorizing','retrying','downloading','waiting-authorization','awaiting-authorization','waiting','awaiting_authorization','failed','cancelled','canceled','interrupted','unknown'];
        try{
          window.NexusM.setState({downloads:statuses.map((status,index)=>({id:`mixed-count-${index}`,name:`Count fixture ${index}`,status}))});
          expect($('#download-count').textContent==='2'&&$('#download-count').title==='2 downloaded, 8 active, 15 entries','Only completed rows should count as downloaded, and only known queue statuses should count as active.');
          expect($('#downloads-summary').textContent==='2 downloaded, 8 active, 15 entries','The visible summary must show the same completed, active, and total counts.');
          expect($$('#downloads-list .download-card').length===15,'Pending, failed, cancelled, interrupted, and unknown entries must stay visible.');
        }finally{window.NexusM.setState(fixture);}
      });
      await check('empty download snapshots remove cards added by individual events',()=>{
        try{
          for(const event of ['download','downloadProgress']){
            window.NexusM.setState({downloads:[]});
            window.NexusM.receive({event,data:{id:'snapshot-added-fixture',name:'Snapshot archive',status:event==='download'?'queued':'downloading',bytes:1024,total:4096}});
            expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 1 active, 1 entry'&&$$('#downloads-list .download-card').length===1,`${event} should add one active entry without marking it downloaded.`);
            window.NexusM.setState({downloads:[]});
            expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 0 active, 0 entries'&&!$$('#downloads-list .download-card').length,`Restoring an empty snapshot after ${event} must remove the event's card and active count.`);
            expect($('#downloads-summary').textContent==='0 downloaded, 0 active, 0 entries','Restoring an empty snapshot must also reset its visible summary.');
          }
        }finally{window.NexusM.setState(fixture);}
      });
      await check('completed download snapshots restore cards changed by individual events',()=>{
        const completed={id:'snapshot-completed-fixture',name:'Completed snapshot archive',status:'completed',bytes:4096,total:4096};
        try{
          for(const status of ['failed','retrying']){
            window.NexusM.setState({downloads:[completed]});
            window.NexusM.receive({event:'download',data:{id:completed.id,status,error:'Temporary transfer error'}});
            expect($('#download-count').textContent==='0'&&$('.download-status').textContent===(status==='failed'?'Failed':'Resuming'),'The individual event should update the displayed status and remove the completed count.');
            window.NexusM.setState({downloads:[completed]});
            expect($('#download-count').textContent==='1'&&$('#download-count').title==='1 downloaded, 0 active, 1 entry'&&$$('#downloads-list .download-card').length===1,'Restoring a completed snapshot must restore exactly one downloaded entry.');
            expect($('#downloads-summary').textContent==='1 downloaded, 0 active, 1 entry','Restoring a completed snapshot must update the visible summary.');
            expect($('.download-status').textContent==='Downloaded'&&$(`[data-download-folder="${completed.id}"]`),'The completed snapshot must restore the downloaded status and folder control.');
            expect(!$('#downloads-list').textContent.includes('Temporary transfer error'),'Restoring a completed snapshot must remove the event error.');
          }
        }finally{window.NexusM.setState(fixture);}
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
      await check('download retries and recovery expose bounded safe logs without losing disclosure or focus',()=>{
        const retryAt=Math.floor(Date.now()/1000)+30;
        const record={id:'queue-log-fixture',name:'Recoverable archive',status:'retrying',attempt:2,maxAttempts:4,retryAt,errorCode:'NETWORK_TIMEOUT',error:'The server did not send data in time.',bytes:1024,total:4096,events:Array.from({length:60},(_,i)=>({time:1792600000+i,code:i===59?'<img src=x onerror=alert(1)>':'RETRY',message:i===59?'<script>window.__queueInjected=true</script>':'Attempt '+i}))};
        try{
          window.NexusM.setState({downloads:[record]});
          const card=$('.download-card'),log=$('.download-log'),summary=$('.download-log summary');
          expect($('.download-attempt').textContent==='Attempt 2 of 4','Retry attempt totals should be visible.');
          expect($('.download-retry-time').textContent.startsWith('Retrying in ')&&$('.download-retry-time').title,'A scheduled retry needs a countdown and its absolute time.');
          expect($('.download-error-code').textContent==='NETWORK_TIMEOUT'&&$('.download-error').textContent.includes('in time'),'The current error and diagnostic code should be visible.');
          expect($$('.download-log li').length===50&&summary.textContent.includes('50 latest'),'The UI should bound long imported logs to the latest events.');
          expect(!$('.download-log img')&&!$('.download-log script')&&!window.__queueInjected,'Log text and codes must never become executable markup.');
          window.NexusM.receive({event:'download',data:{id:record.id,events:[null,{time:1e40,code:'LEGACY',message:'Unknown event time'},{time:0,code:'RECOVERED',message:'Saved transfer'}]}});
          expect($$('.download-log li').length===2&&!$('.download-log time'),'Malformed imported event timestamps should not break rendering or invent dates.');
          log.open=true;summary.focus();
          window.NexusM.receive({event:'downloadProgress',data:{id:record.id,status:'downloading',bytes:2048,speed:1024}});
          expect($('.download-card')===card&&$('.download-log')===log&&log.open&&document.activeElement===summary,'Progress should preserve the card, open log, and keyboard focus.');
          expect(!$('.download-retry-time')&&$('[role="progressbar"]').getAttribute('aria-valuenow')==='50','A resumed transfer should replace its retry countdown with current progress.');
          window.NexusM.receive({event:'download',data:{id:record.id,status:'interrupted',resumeOnStartup:true,error:'Waiting for the Nexus connection.'}});
          expect($('.download-recovery').textContent.includes('recovery when connected')&&$('[data-retry-download]'),'An interrupted recoverable transfer should retain its explanation and manual Retry control.');
          window.NexusM.receive({event:'download',data:{id:record.id,status:'cancelled',resumeOnStartup:false}});
          expect(!$('.download-recovery')&&$('.download-status').textContent==='Cancelled','Explicit cancellation must not look like automatic recovery.');
        }finally{window.NexusM.setState(fixture);}
      });
      await check('server retry deadlines remain visible and release manual Retry without starting a download',async()=>{
        const originalNow=Date.now,deadline=Math.floor(Date.now()/1000)+7200;
        try{
          window.NexusM.navigate('downloads');
          const record={id:'retry-deadline-fixture',name:'Server pause archive',status:'failed',errorCode:'rate-limited',error:'The server requested a longer wait.',retryAt:deadline};
          window.NexusM.setState({downloads:[record]});
          expect($('.download-retry-available').textContent.startsWith('Retry available after ')&&$('.download-retry-available').title,'A failed long server pause must show its absolute retry deadline.');
          let button=$('[data-retry-download]');const before=requests.filter(request=>request.action==='retryDownload').length;
          expect(button.disabled,'Retry must remain disabled until the server deadline.');button.click();
          expect(requests.filter(request=>request.action==='retryDownload').length===before,'A disabled Retry must not request another download.');
          window.NexusM.setState({downloads:[{...record,status:'interrupted',resumeOnStartup:true}]});
          button=$('[data-retry-download]');const log=$('.download-log'),summary=$('.download-log summary');log.open=true;summary.focus();
          expect(button.disabled&&$('.download-retry-available').textContent.startsWith('Retry available after '),'Recovered interrupted jobs must retain their server deadline.');
          Date.now=()=>(deadline+1)*1000;
          await new Promise(resolve=>setTimeout(resolve,1100));
          expect($('[data-retry-download]')===button&&!button.disabled&&$('.download-retry-available').textContent==='Retry available now','The timer must enable the existing Retry control when its deadline expires.');
          expect(log.open&&document.activeElement===summary,'Expiry must preserve an open log and keyboard focus.');
          expect(requests.filter(request=>request.action==='retryDownload').length===before,'Expiry must not initiate a download by itself.');
          button.click();await settle();
          expect(requests.filter(request=>request.action==='retryDownload').length===before+1,'Manual Retry should become available after the deadline.');
        }finally{Date.now=originalNow;window.NexusM.setState(fixture);}
      });
      await check('automatic recovery preference updates saved-job guidance without losing its queue entry',()=>{
        const record={id:'recovery-preference-fixture',name:'Saved queue archive',status:'interrupted',resumeOnStartup:true};
        try{
          window.NexusM.setState({downloads:[record],settings:{resumeDownloadsOnStartup:true}});
          const card=$('.download-card');
          expect($('.download-recovery').textContent==='Saved for recovery when connected.','Enabled recovery should explain how the saved job continues.');
          window.NexusM.setState({settings:{resumeDownloadsOnStartup:false}});
          expect($('.download-card')===card&&$('.download-recovery').textContent.includes('Automatic recovery is off')&&$('[data-retry-download]'),'Disabling recovery should retain the saved job and explain manual Retry.');
          window.NexusM.setState({settings:{resumeDownloadsOnStartup:true}});
          expect($('.download-card')===card&&$('.download-recovery').textContent==='Saved for recovery when connected.','Re-enabling recovery should immediately update saved-job guidance.');
          expect($('#downloads-summary').textContent==='0 downloaded, 0 active, 1 entry','A recovery preference change must not alter queue counts.');
        }finally{window.NexusM.setState(fixture);}
      });
      await check('Clear list dispatches the action and keeps active downloads visible',async()=>{
        window.NexusM.setState({downloads:[...fixture.downloads,{id:'finished-fixture',name:'Finished archive',status:'completed'},{id:'waiting-fixture',name:'Waiting archive',status:'waiting-authorization'},{id:'active-fixture',name:'Active archive',status:'downloading',bytes:1024,total:4096},{id:'queued-fixture',name:'Queued archive',status:'queued'}]});
        expect(!$('#clear-downloads-button').disabled,'Clear list should be available for finished and waiting entries.');
        expect($('#clear-downloads-button').title.includes('keeps archives and download history'),'Clear list should explain what is kept.');
        $('#clear-downloads-button').click();await settle();
        expect(requests.some(r=>r.action==='clearDownloads'),'Clear list did not request the host action.');
        expect($$('#downloads-list .download-card').length===2&&$('#downloads-list').innerText.includes('Active archive')&&$('#downloads-list').innerText.includes('Queued archive'),'Active and queued transfers should remain visible.');
        expect($('#clear-downloads-button').disabled,'Clear list should be disabled with only active transfers.');
        expect($('#download-count').textContent==='0'&&$('#download-count').title==='0 downloaded, 2 active, 2 entries','Remaining active transfers must not count as downloaded.');
        expect($('#downloads-summary').textContent==='0 downloaded, 2 active, 2 entries','The summary should show only the active entries retained by Clear list.');
      });
      await check('Clear download folder waits for idle transfers and remains available with an empty list',()=>{
        window.NexusM.navigate('downloads');
        for(const status of ['queued','authorizing','retrying','downloading','waiting-authorization','awaiting-authorization','waiting','awaiting_authorization']){
          window.NexusM.setState({downloads:[{id:'busy-fixture',status}],clearingDownloadFolder:false});
          expect($('#clear-download-folder-button').disabled,`Folder cleanup must wait while a transfer is ${status}.`);
        }
        window.NexusM.setState({downloads:[],clearingDownloadFolder:false});
        expect(!$('#clear-download-folder-button').disabled,'Archives can remain on disk after the visible list is cleared.');
        expect($('#clear-downloads-button').disabled,'An empty list should still disable Clear list.');
        const cleanupTitle=$('#clear-download-folder-button').title,cleanupNote=$('.download-cleanup-note').textContent;
        expect(cleanupTitle.includes('Permanently deletes only NexusM downloads')&&cleanupTitle.includes('after confirmation'),'The folder control should limit deletion to the app\'s files and describe confirmation.');
        expect(cleanupTitle.includes('unrelated files')&&cleanupTitle.includes('sidecars'),'The folder control should explain that unrelated files stay and app sidecars can be removed.');
        expect(cleanupNote.includes('Clear list removes entries only'),'The two cleanup actions need a visible distinction.');
        expect(cleanupNote.includes('downloads recorded by NexusM, its partial files, and its sidecars')&&cleanupNote.includes('Unrelated files, subfolders, and download history stay.'),'The cleanup note must identify the app-owned files and what is retained.');
      });
      await check('folder cleanup dispatches only its action and preserves file flags and history',async()=>{
        window.NexusM.setState({downloads:[],fileDownloads:fixture.fileDownloads,mods:fixture.mods,clearingDownloadFolder:false});
        await window.NexusM.showMod('game1:1');$('[data-select-file="10"]').click();
        const beforeList=requests.filter(r=>r.action==='clearDownloads').length,beforeFolder=requests.filter(r=>r.action==='clearDownloadFolder').length;
        $('#clear-download-folder-button').click();await settle();
        expect(requests.filter(r=>r.action==='clearDownloadFolder').length===beforeFolder+1,'Folder cleanup should submit its own host action.');
        expect(requests.filter(r=>r.action==='clearDownloads').length===beforeList,'Folder cleanup must not clear the visible list.');
        expect(!$('#input-dialog').open,'The native host handles confirmation; no second app dialog should open.');
        expect($('#clear-download-folder-button').disabled&&$('#clear-download-folder-button').getAttribute('aria-busy')==='true','The folder button should stay busy until cleanup finishes.');
        expect($('#browse-folder-button').disabled&&$('#save-folder-button').disabled,'The configured folder must stay fixed during cleanup.');
        expect($('#download-folder').disabled,'The editable folder path must also be locked during cleanup.');
        expect($('#download-selected').disabled&&$$('[data-download-file]').every(button=>button.disabled),'New downloads must wait for folder cleanup.');
        window.NexusM.receive({event:'state',data:{clearingDownloadFolder:false}});
        window.NexusM.receive({id:folderClearRequest.id,ok:true,result:{state:{downloads:[],fileDownloads:fixture.fileDownloads,mods:fixture.mods,clearingDownloadFolder:false}}});folderClearRequest=null;
        await settle();
        expect(!$('#clear-download-folder-button').disabled&&!$('#browse-folder-button').disabled&&!$('#save-folder-button').disabled,'Idle cleanup controls must restore after the host response.');
        expect(!$('#download-folder').disabled,'The folder path should become editable after cleanup.');
        expect(!$('#download-selected').disabled&&$('[data-select-file="10"]').checked,'Selected downloads should restore after cleanup.');
        expect($('[data-file-card="10"] .file-download-flag.downloaded')&&$('[data-file-card="12"] .file-download-flag.downloaded'),'Clearing disk files must retain downloaded history flags.');
        expect($('[data-mod-key="game1:1"] .table-date').textContent.includes('2026'),'Mod download history should remain visible.');
        $('#detail-close').click();
        window.NexusM.setState({downloads:fixture.downloads});
      });
      await check('automatic refresh shows progress and keeps the cached library usable',()=>{
        window.NexusM.navigate('library');
        window.NexusM.receive({event:'refreshProgress',data:{running:true,phase:'connecting',message:'Connecting to Nexus.'}});
        expect($('#refresh-button').disabled&&$('#refresh-button span').textContent.includes('Connecting'),'Startup connection should show a disabled busy update button.');
        window.NexusM.receive({event:'refreshProgress',data:{running:true,phase:'mods',done:3,total:9,message:'Checking your saved mods.'}});
        expect($('#refresh-button span').textContent.includes('3 / 9'),'Refresh progress is missing.');
        expect(!$('#refresh-status').classList.contains('hidden')&&$('#refresh-status').textContent.includes('Checking your saved mods'),'Refresh status is missing.');
        $('#mod-search').value='beta';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        expect($$('#mods-body tr[data-mod-key]').length===1&&$('#mods-body tr').dataset.modKey==='game1:2','The cached library must remain searchable during an update check.');
        $('#mod-search').value='';$('#mod-search').dispatchEvent(new Event('input',{bubbles:true}));
        window.NexusM.setState({mods:fixture.mods.map(m=>m.modId===1?{...m,version:'2.1'}:m)});
        expect($('[data-mod-key="game1:1"] .version-chip').textContent==='2.1','New metadata should appear while the update check continues.');
        expect($('#refresh-button').disabled,'A library update should not clear the running status.');
        window.NexusM.receive({event:'refreshProgress',data:{running:false}});
        expect(!$('#refresh-button').disabled&&$('#refresh-button span').textContent==='Check for updates'&&$('#refresh-status').classList.contains('hidden'),'Finishing an automatic check should reset the update control.');
        window.NexusM.setState({mods:fixture.mods});
      });
      await check('manual refresh ignores repeated clicks and restores the button',async()=>{
        const before=requests.filter(r=>r.action==='refresh').length;
        $('#refresh-button').click();$('#refresh-button').click();
        expect(requests.filter(r=>r.action==='refresh').length===before+1,'Repeated clicks should submit one update check.');
        expect($('#refresh-button').disabled&&$('#refresh-button').classList.contains('busy'),'A manual check should become busy immediately.');
        expect($$('#mods-body tr[data-mod-key]').length===3,'Cached rows should remain visible while waiting for Nexus.');
        window.NexusM.receive({id:refreshRequest.id,ok:true,result:{refreshProgress:{running:false},mods:fixture.mods,lastCheckedAt:1792600000}});
        refreshRequest=null;await settle();
        expect(!$('#refresh-button').disabled&&!$('#refresh-button').classList.contains('busy'),'A completed manual check should restore the button.');
        expect($('#last-checked').textContent.startsWith('Checked '),'The check timestamp should update.');
      });
      await check('failed refresh preserves cached rows and releases the update control',async()=>{
        $('#refresh-button').click();
        window.NexusM.receive({id:refreshRequest.id,ok:false,error:'Nexus is temporarily unavailable.'});
        refreshRequest=null;await settle();
        expect(!$('#refresh-button').disabled&&$('#refresh-button span').textContent==='Check for updates','A failed check should allow retry.');
        expect($$('#mods-body tr[data-mod-key]').length===3,'A failed check must keep the saved library.');
        expect($('.toasts').textContent.includes('temporarily unavailable'),'The connection error should be shown without a blocking dialog.');
      });
      await check('key masking, folder, storage, and protocol controls exist',()=>{
        window.NexusM.navigate('settings');
        expect($('#api-key').type==='password'&&!$('#api-key').value,'The saved secret must not be sent to the UI.');
        expect($('#browse-folder-button')&&$('#clear-cache-button')&&$('#register-nxm-button'),'A required settings control is missing.');
      });
      await check('API key link opens the official Nexus page through the external-browser action',async()=>{
        const link=$('#get-key-button'),before=requests.filter(r=>r.action==='openUrl').length;
        expect(link.tagName==='A'&&link.href==='https://www.nexusmods.com/users/myaccount?tab=api','Settings should expose a usable link to the official Nexus API page.');
        link.querySelector('svg').dispatchEvent(new MouseEvent('click',{bubbles:true,cancelable:true}));await settle();
        const opened=requests.filter(r=>r.action==='openUrl');
        expect(opened.length===before+1&&opened[opened.length-1].payload.url==='https://www.nexusmods.com/users/myaccount?tab=api','Clicking the link icon should request the Nexus API page in the external browser.');
        expect($('#page-settings').classList.contains('active')&&!$('#api-key').value,'Opening the website should keep Settings visible and the saved key out of the input.');
      });
      await check('queue settings preserve drafts, submit typed values once, and reflect saved settings',async()=>{
        window.NexusM.setState({settings:fixture.settings});
        const edit=(id,value)=>{const input=$(`#${id}`);if(input.type==='checkbox')input.checked=value;else input.value=String(value);input.dispatchEvent(new Event(input.type==='checkbox'?'change':'input',{bubbles:true}));};
        expect($('#queue-parallel').value==='2'&&$('#queue-retries').value==='3'&&$('#queue-timeout').value==='90'&&$('#queue-retry-delay').value==='2'&&$('#queue-resume').checked,'The queue defaults should be visible.');
        edit('queue-parallel',4);edit('queue-retries',5);edit('queue-timeout',120);edit('queue-retry-delay',3);edit('queue-resume',false);
        window.NexusM.receive({event:'state',data:{settings:{...fixture.settings,cacheSizeMB:12}}});
        window.NexusM.receive({event:'downloadProgress',data:{id:'queue-draft-progress',status:'downloading',bytes:1024,total:4096}});
        window.NexusM.navigate('downloads');window.NexusM.navigate('settings');
        expect($('#queue-parallel').value==='4'&&$('#queue-retries').value==='5'&&!$('#queue-resume').checked,'Unrelated state, progress, and navigation must preserve queue drafts.');
        const before=requests.filter(r=>r.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(r.payload,'maxParallelDownloads')).length;
        $('#save-queue-settings').click();$('#save-queue-settings').click();
        expect(queueSettingsRequest&&requests.filter(r=>r.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(r.payload,'maxParallelDownloads')).length===before+1,'A pending queue save must not submit twice.');
        expect(JSON.stringify(queueSettingsRequest.payload)===JSON.stringify({maxParallelDownloads:4,maxDownloadRetries:5,downloadTimeoutSeconds:120,retryDelaySeconds:3,resumeDownloadsOnStartup:false}),'Queue numbers and boolean should have their expected host types.');
        expect($('#save-queue-settings').disabled&&$('#download-queue-form').getAttribute('aria-busy')==='true','Saving queue settings should stay busy until the response.');
        window.NexusM.receive({id:queueSettingsRequest.id,ok:true,result:{settings:{maxParallelDownloads:3,maxDownloadRetries:5,downloadTimeoutSeconds:120,retryDelaySeconds:3,resumeDownloadsOnStartup:false}}});queueSettingsRequest=null;await settle();
        expect($('#queue-parallel').value==='3'&&!$('#save-queue-settings').disabled&&$('#queue-settings-result').textContent==='Queue settings saved.','Successful saves should show the returned settings and restore Save.');
        window.NexusM.setState({settings:fixture.settings,downloads:fixture.downloads});
        expect($('#queue-parallel').value==='2'&&$('#queue-resume').checked,'A successfully saved form should follow future stored settings again.');
      });
      await check('queue settings validate bounds and preserve newer edits or rejected saves',async()=>{
        const edit=(id,value)=>{const input=$(`#${id}`);input.value=String(value);input.dispatchEvent(new Event('input',{bubbles:true}));};
        const before=requests.length;
        for(const [id,value,valid] of [['queue-parallel',5,2],['queue-parallel',1.5,2],['queue-retries',-1,3],['queue-retries',11,3],['queue-timeout',14,90],['queue-timeout',601,90],['queue-retry-delay',0,2],['queue-retry-delay',61,2]]){
          edit(id,value);$('#save-queue-settings').click();
          expect(!$(`#${id}`).checkValidity()&&!queueSettingsRequest,'Out-of-range or fractional queue settings must be rejected before host submission.');
          edit(id,valid);
        }
        expect(requests.length===before,'Invalid queue settings should never reach the host.');
        edit('queue-parallel',3);$('#save-queue-settings').click();const first=queueSettingsRequest;
        edit('queue-parallel',4);
        window.NexusM.receive({id:first.id,ok:true,result:{settings:{maxParallelDownloads:3}}});queueSettingsRequest=null;await settle();
        expect($('#queue-parallel').value==='4','A reply for a previous queue draft must not erase a newer edit.');
        $('#save-queue-settings').click();
        window.NexusM.receive({id:queueSettingsRequest.id,ok:false,error:'Finish active downloads before changing queue settings.'});queueSettingsRequest=null;await settle();
        window.NexusM.receive({event:'state',data:{settings:fixture.settings}});
        expect($('#queue-parallel').value==='4'&&!$('#save-queue-settings').disabled&&$('#queue-settings-result').getAttribute('role')==='alert'&&$('#queue-settings-result').textContent.includes('Finish active'),'Rejected queue saves should retain edits and expose their actionable error.');
        edit('queue-parallel',2);$('#save-queue-settings').click();
        window.NexusM.receive({id:queueSettingsRequest.id,ok:true,result:{settings:fixture.settings}});queueSettingsRequest=null;await settle();
      });
      await check('typed download folder survives live state updates and saves without an added subfolder',async()=>{
        window.NexusM.setState({settings:fixture.settings});
        const input=$('#download-folder'),path='D:\\Downloads';
        expect(!input.readOnly&&!input.disabled,'The download folder should accept a typed path.');
        input.value=path;input.dispatchEvent(new Event('input',{bubbles:true}));
        window.NexusM.receive({event:'state',data:{mods:fixture.mods,settings:fixture.settings}});
        expect(input.value===path,'A live library update must not erase an unsaved path.');
        window.NexusM.navigate('library');window.NexusM.navigate('settings');
        expect(input.value===path,'Browsing the library must preserve an unsaved path.');
        $('#save-folder-button').click();
        expect(folderSaveRequest?.payload.downloadFolder===path,'The exact custom folder should be submitted, without a NexusM suffix.');
        window.NexusM.receive({id:folderSaveRequest.id,ok:true,result:{settings:{downloadFolder:path}}});folderSaveRequest=null;await settle();
        expect(input.value===path&&$('.toasts').textContent.includes('Download location saved.'),'The saved path and confirmation should be visible.');
        window.NexusM.receive({event:'state',data:{settings:{downloadFolder:'E:\\Archives'}}});
        expect(input.value==='E:\\Archives','After saving, the field should follow the stored setting again.');
      });
      await check('Browse stages a chosen folder until Save and Cancel preserves a typed path',async()=>{
        const input=$('#download-folder'),typed='D:\\Downloads',chosen='E:\\Mod Archives';
        input.value=typed;input.dispatchEvent(new Event('input',{bubbles:true}));
        browseFolderResult={cancelled:true};$('#browse-folder-button').click();await settle();
        expect(input.value===typed,'Cancelling Browse must retain the typed path.');
        const saves=requests.filter(r=>r.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(r.payload,'downloadFolder')).length;
        browseFolderResult={path:chosen};$('#browse-folder-button').click();await settle();
        expect(input.value===chosen,'Browse should place the selected folder in the field.');
        expect(requests.filter(r=>r.action==='saveSettings'&&Object.prototype.hasOwnProperty.call(r.payload,'downloadFolder')).length===saves,'Browse should wait for Save location before persisting the selection.');
        window.NexusM.receive({event:'state',data:{settings:fixture.settings}});
        expect(input.value===chosen,'The selected folder must survive a background state update before Save.');
        $('#save-folder-button').click();
        expect(folderSaveRequest?.payload.downloadFolder===chosen,'Save should submit the Browse selection exactly.');
        window.NexusM.receive({id:folderSaveRequest.id,ok:true,result:{settings:{downloadFolder:chosen}}});folderSaveRequest=null;await settle();
      });
      await check('failed saves and in-flight edits preserve the folder draft',async()=>{
        const input=$('#download-folder'),submitted='D:\\Downloads',newer='E:\\Next Downloads';
        input.value=submitted;input.dispatchEvent(new Event('input',{bubbles:true}));$('#save-folder-button').click();
        const first=folderSaveRequest;
        input.value=newer;input.dispatchEvent(new Event('input',{bubbles:true}));
        window.NexusM.receive({id:first.id,ok:true,result:{settings:{downloadFolder:submitted}}});folderSaveRequest=null;await settle();
        expect(input.value===newer,'Saving the previous path must not clear a newer edit.');
        input.value='';input.dispatchEvent(new Event('input',{bubbles:true}));$('#save-folder-button').click();
        window.NexusM.receive({id:folderSaveRequest.id,ok:false,error:'Enter a valid absolute download folder path, such as D:\\Downloads.'});folderSaveRequest=null;await settle();
        window.NexusM.receive({event:'state',data:{settings:{downloadFolder:submitted}}});
        expect(input.value===''&&!$('#save-folder-button').disabled,'A failed save must retain the draft and allow correction.');
        expect($('.toasts').textContent.includes('valid absolute download folder'),'The path validation error should be visible.');
        input.value=fixture.settings.downloadFolder;input.dispatchEvent(new Event('input',{bubbles:true}));$('#save-folder-button').click();
        window.NexusM.receive({id:folderSaveRequest.id,ok:true,result:{settings:fixture.settings}});folderSaveRequest=null;await settle();
      });
      const updateArchive={id:'game1:1:40',gameDomain:'game1',modId:1,fileId:40,name:'Main archive',filename:'original-v1.zip',status:'completed',version:'1.0'};
      const archiveUpdate={id:updateArchive.id,gameDomain:'game1',modId:1,modName:'Alpha overhaul',oldFileId:40,oldName:'Main archive',oldFilename:'original-v1.zip',oldVersion:'1.0',oldPath:'C:\\Downloads\\NexusM\\original-v1.zip',fileId:41,name:'New archive',filename:'renamed-v1.1-<img src=x onerror=alert(1)>.zip',version:'1.1',updateKey:'file:41',archiveKey:'archive-fixture-original',dismissed:false};
      const answerUpdate=async(result,error=null)=>{
        expect(downloadUpdateRequest,'An update action must be waiting.');
        window.NexusM.receive(error?{id:downloadUpdateRequest.id,ok:false,error}:{id:downloadUpdateRequest.id,ok:true,result});
        downloadUpdateRequest=null;await settle();
      };
      await check('download update popup waits for existing dialogs and safely shows renamed archives',async()=>{
        $('#add-mod-button').click();expect($('#input-dialog').open,'The existing add dialog must be open.');
        window.NexusM.setState({downloads:[updateArchive],downloadUpdates:[archiveUpdate]});
        expect(!$('#download-update-dialog').open,'An update must wait for the existing dialog.');
        $('#dialog-cancel').click();
        for(let attempt=0;attempt<30&&!$('#download-update-dialog').open;attempt++)await new Promise(resolve=>setTimeout(resolve,10));
        expect($('#download-update-dialog').open,'The pending update must appear when the existing dialog closes.');
        expect($('#download-update-files').textContent.includes('original-v1.zip')&&$('#download-update-files').textContent.includes(archiveUpdate.filename),'The popup must show both different filenames verbatim.');
        expect(!$('#download-update-files img'),'An archive filename must never become executable markup.');
        expect($('#download-update-files').textContent.includes('1.0')&&$('#download-update-files').textContent.includes('1.1'),'Both versions must be visible.');
        expect($('#download-update-path').textContent===archiveUpdate.oldPath,'The exact old path must be visible before deletion.');
        expect($('#download-update-dialog').textContent.includes('bypasses the Recycle Bin'),'Permanent deletion must be explained before choosing an action.');
        expect(document.activeElement===$('#download-update-keep'),'The safe keep choice should receive initial focus.');
        const before=requests.length;$('#download-update-keep').click();$('#download-update-keep').click();
        expect(requests.length===before+1&&downloadUpdateRequest.action==='dismissDownloadUpdate','Repeated clicks must send only one dismissal.');
        expect(downloadUpdateRequest.payload.id===updateArchive.id&&downloadUpdateRequest.payload.fileId===41&&downloadUpdateRequest.payload.updateKey==='file:41'&&downloadUpdateRequest.payload.archiveKey===archiveUpdate.archiveKey,'Actions must identify the exact old archive and new file.');
        await answerUpdate({downloadUpdates:[{...archiveUpdate,dismissed:true}]});
        expect(!$('#download-update-dialog').open,'Keeping the old archive must close the popup.');
        window.NexusM.setState({downloadUpdates:[archiveUpdate]});
        expect(!$('#download-update-dialog').open,'Repeated state events must not reopen the same notification.');
        expect($('[data-review-download-update]')&&$('#downloads-list').textContent.includes('Update available'),'The update must remain reviewable in Downloads.');
      });
      await check('replacement waits for a single explicit action and preserves retryable errors',async()=>{
        $('[data-review-download-update]').click();expect($('#download-update-dialog').open,'Review update must reopen a kept update.');
        const before=requests.length;$('#download-update-replace').click();$('#download-update-replace').click();
        expect(requests.length===before+1&&downloadUpdateRequest.action==='replaceDownloadUpdate','Replace must send exactly one replacement action.');
        expect($$('#download-update-dialog button').every(button=>button.disabled),'All popup actions must wait while a mutation is pending.');
        expect($('#downloads-list').textContent.includes('original-v1.zip'),'The old download must remain while replacement authorization is pending.');
        await answerUpdate(null,'Nexus is temporarily unavailable.');
        expect($('#download-update-dialog').open&&!$('#download-update-replace').disabled&&!$('#download-update-error').classList.contains('hidden'),'A failed replacement must show an inline error and allow retry.');
        $('#download-update-replace').click();
        await answerUpdate({downloads:[updateArchive,{id:'game1:1:41',gameDomain:'game1',modId:1,fileId:41,name:'New archive',status:'waiting-authorization'}],downloadUpdates:[{...archiveUpdate,dismissed:true}]});
        expect(!$('#download-update-dialog').open&&$('#downloads-list').textContent.includes('original-v1.zip'),'Queued replacement must keep the old archive visible.');
        expect($('.toasts').textContent.includes('old archive stays until the new download finishes'),'Replacement feedback must explain that the old archive is retained until success.');
      });
      await check('deleting an old archive sends only its exact update action',async()=>{
        $('[data-review-download-update]').click();$('#download-update-delete').click();
        expect(downloadUpdateRequest.action==='deleteDownloadUpdate'&&downloadUpdateRequest.payload.id===updateArchive.id&&downloadUpdateRequest.payload.updateKey==='file:41','Delete must target only the recorded old archive.');
        await answerUpdate({downloads:[],downloadUpdates:[]});
        expect(!$('#download-update-dialog').open&&!$('[data-review-download-update]'),'Successful deletion must remove the stale update card.');
      });
      await check('new updates notify again while Escape and close persist the keep choice',async()=>{
        const next={...archiveUpdate,fileId:42,version:'1.2',updateKey:'file:42'};
        window.NexusM.setState({downloads:[updateArchive],downloadUpdates:[next]});
        expect($('#download-update-dialog').open,'A newer target file must produce a new popup.');
        $('#download-update-dialog').dispatchEvent(new Event('cancel',{cancelable:true}));
        expect(downloadUpdateRequest.action==='dismissDownloadUpdate'&&downloadUpdateRequest.payload.fileId===42,'Escape must persist dismissal of this particular version.');
        await answerUpdate({downloadUpdates:[{...next,dismissed:true}]});
        $('[data-review-download-update]').click();$('#download-update-close').click();
        expect(downloadUpdateRequest.action==='dismissDownloadUpdate','The close button must also preserve the keep choice.');
        await answerUpdate({downloadUpdates:[{...next,dismissed:true}]});
      });
      await check('a failed saved dismissal never traps the user in the update popup',async()=>{
        $('[data-review-download-update]').click();$('#download-update-close').click();
        await answerUpdate(null,'The old archive is no longer present.');
        expect(!$('#download-update-dialog').open,'The close choice must work locally when its archive changed or Nexus is unavailable.');
        window.NexusM.setState({downloadUpdates:[{...archiveUpdate,fileId:42,updateKey:'file:42'}]});
        expect(!$('#download-update-dialog').open,'A failed save must still suppress repeated popups in this session.');
      });
      await check('a changed archive identity refreshes displayed consent before deleting',async()=>{
        const original={...archiveUpdate,fileId:43,updateKey:'file:43'};
        window.NexusM.setState({downloads:[updateArchive],downloadUpdates:[original]});
        expect($('#download-update-dialog').open&&$('#download-update-path').textContent===original.oldPath,'The original archive must be visible before its identity changes.');
        const changed={...original,archiveKey:'archive-fixture-replaced',oldPath:'D:\\New Archives\\downloaded-again.zip',oldFilename:'downloaded-again.zip'};
        window.NexusM.setState({downloadUpdates:[changed]});
        expect($('#download-update-dialog').open&&$('#download-update-path').textContent===changed.oldPath,'Replacing a queue record must replace stale popup consent with the new archive path.');
        $('#download-update-delete').click();
        expect(downloadUpdateRequest.action==='deleteDownloadUpdate'&&downloadUpdateRequest.payload.archiveKey===changed.archiveKey,'Deletion must submit the exact archive identity that is currently displayed.');
        await answerUpdate({downloads:[],downloadUpdates:[]});
      });
      await check('unlinked mod updates offer file review without assuming a replacement',async()=>{
        const update={...archiveUpdate,fileId:0,version:'2.0',updateKey:'mod:1792600000',reviewOnly:true};
        window.NexusM.setState({downloads:[updateArchive],downloadUpdates:[update]});
        expect($('#download-update-dialog').open&&$('#download-update-replace').textContent==='Review files','An unlinked change must offer file review.');
        expect($$('#download-update-files>div').length===1&&!$('#download-update-files').textContent.includes(archiveUpdate.filename),'An unknown replacement must not be presented as an exact file match.');
        const replacements=requests.filter(request=>request.action==='replaceDownloadUpdate').length;
        $('#download-update-replace').click();
        expect(downloadUpdateRequest.action==='dismissDownloadUpdate'&&downloadUpdateRequest.payload.updateKey===update.updateKey,'Review files must dismiss only the current mod-change notice.');
        await answerUpdate({downloadUpdates:[{...update,dismissed:true}]});
        expect(!$('#download-update-dialog').open&&!$('#detail-panel').classList.contains('hidden'),'Review files must open the matching mod details.');
        expect(requests.filter(request=>request.action==='replaceDownloadUpdate').length===replacements,'File review must not queue an assumed replacement.');
        $('#detail-close').click();window.NexusM.setState({downloadUpdates:[],downloads:[]});
      });
      await check('a matching queued update prompts over mod details and joins the existing download',async()=>{
        await window.NexusM.showMod('game1:1');
        const update={...archiveUpdate,fileId:44,updateKey:'file:44'},target={id:'game1:1:44',gameDomain:'game1',modId:1,fileId:44,name:'Queued newer archive',status:'waiting-authorization'};
        window.NexusM.setState({downloads:[updateArchive,{...target,id:'game2:1:44',gameDomain:'game2'},{...target,id:'game1:2:44',modId:2}],downloadUpdates:[update]});
        expect(!$('#download-update-dialog').open,'A matching file ID from another game or mod must not interrupt file review.');
        window.NexusM.setState({downloads:[updateArchive,target]});
        expect($('#download-update-dialog').open&&!$('#detail-panel').classList.contains('hidden'),'Starting the newer file must offer old-archive replacement while its mod details remain open.');
        expect($('#download-update-replace').textContent==='Replace when ready'&&$('#download-update-replace-note').textContent.includes('already in your queue'),'Queued replacement consent must explain that it uses the existing download.');
        const starts=requests.filter(request=>request.action==='startDownload').length;
        $('#download-update-replace').click();
        expect(downloadUpdateRequest?.action==='replaceDownloadUpdate'&&downloadUpdateRequest.payload.fileId===44,'The pending download must require an explicit replacement action for the correct target.');
        await answerUpdate({downloads:[updateArchive,target],downloadUpdates:[{...update,dismissed:true}]});
        expect(!$('#download-update-dialog').open&&$('.toasts').textContent.includes('Replacement scheduled.'),'Joining an existing download must close the prompt and confirm scheduled cleanup.');
        expect(requests.filter(request=>request.action==='startDownload').length===starts,'Joining an existing download must not issue another file download request.');
        $('#detail-close').click();window.NexusM.setState({downloads:[],downloadUpdates:[]});
      });
      await check('a completed target refreshes open consent and reuses the downloaded archive',async()=>{
        const update={...archiveUpdate,fileId:45,updateKey:'file:45'},target={id:'game1:1:45',gameDomain:'game1',modId:1,fileId:45,name:'Downloaded newer archive',status:'downloading'};
        window.NexusM.setState({downloads:[updateArchive,target],downloadUpdates:[update]});
        expect($('#download-update-dialog').open&&$('#download-update-replace').textContent==='Replace when ready','An active transfer must initially offer cleanup after completion.');
        target.status='completed';window.NexusM.setState({downloads:[updateArchive,{...target}]});
        expect($('#download-update-replace').textContent==='Replace with downloaded file'&&$('#download-update-summary').textContent.includes('already downloaded'),'Completing the transfer must update the currently displayed replacement choice.');
        expect($('#download-update-replace-note').textContent.includes('verifies its saved file identity'),'Reusing a completed file must explain archive verification before permanent cleanup.');
        const starts=requests.filter(request=>request.action==='startDownload').length;
        $('#download-update-replace').click();
        expect(downloadUpdateRequest?.action==='replaceDownloadUpdate'&&downloadUpdateRequest.payload.archiveKey===update.archiveKey,'Completed replacement must still submit the displayed old archive identity.');
        await answerUpdate({downloads:[target],downloadUpdates:[],notice:'The updated archive was downloaded and the old version was deleted.'});
        expect(!$('#download-update-dialog').open&&!$('#downloads-list').textContent.includes(updateArchive.filename),'Successful reuse must remove only the old archive entry.');
        expect(requests.filter(request=>request.action==='startDownload').length===starts,'A completed replacement must not request another download.');
        expect($('.toasts').textContent.includes('old version was deleted'),'Completed replacement must show the backend cleanup result.');
        window.NexusM.setState({downloads:[],downloadUpdates:[],notice:''});
      });
      await check('periodic downloaded-update checks run once and wait for connection, refresh, and cleanup',async()=>{
        window.NexusM.setState({downloads:[updateArchive],downloadUpdates:[],settings:{hasApiKey:true},refreshProgress:{running:false}});
        const before=requests.filter(request=>request.action==='checkDownloadUpdates').length;
        const first=window.NexusM.checkDownloadedUpdates(),second=window.NexusM.checkDownloadedUpdates();
        expect(downloadUpdateCheckRequest&&requests.filter(request=>request.action==='checkDownloadUpdates').length===before+1,'An interval tick must check retained downloads only once while its request is pending.');
        window.NexusM.receive({id:downloadUpdateCheckRequest.id,ok:true,result:{downloadUpdates:[{...archiveUpdate,dismissed:true}]}});downloadUpdateCheckRequest=null;await Promise.all([first,second]);
        expect($('[data-review-download-update]'),'A periodic response must update the downloadable archive card.');
        window.NexusM.setState({refreshProgress:{running:true}});await window.NexusM.checkDownloadedUpdates();
        window.NexusM.setState({refreshProgress:{running:false},clearingDownloadFolder:true});await window.NexusM.checkDownloadedUpdates();
        window.NexusM.setState({clearingDownloadFolder:false,settings:{hasApiKey:false}});await window.NexusM.checkDownloadedUpdates();
        window.NexusM.setState({settings:{hasApiKey:true},downloads:[],downloadUpdates:[]});await window.NexusM.checkDownloadedUpdates();
        expect(requests.filter(request=>request.action==='checkDownloadUpdates').length===before+1,'Periodic checks must wait for an active refresh/cleanup and require a key plus retained completed archives.');
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
        expect($('#empty-import').textContent==='Import download history','Returning to the whole library must restore the global import action.');
        $('#empty-import').click();
        expect($('#input-dialog').open&&$('#dialog-website-import'),'Whole-library import must retain its existing website and pasted-history choices.');
        $('#dialog-cancel').click();
        expect($('#clear-downloads-button').disabled,'An empty download list should not offer a clear action.');
      });
    } catch(error) {report.checks.push({test:'UI fixture setup',passed:false,error:error.message});}
    finally {
      if(refreshRequest)window.NexusM.receive({id:refreshRequest.id,ok:false,error:'UI fixture ended.'});
      if(folderClearRequest)window.NexusM.receive({id:folderClearRequest.id,ok:false,error:'UI fixture ended.'});
      if(folderSaveRequest)window.NexusM.receive({id:folderSaveRequest.id,ok:false,error:'UI fixture ended.'});
      if(queueSettingsRequest)window.NexusM.receive({id:queueSettingsRequest.id,ok:false,error:'UI fixture ended.'});
      if(batchDownloadRequest)window.NexusM.receive({id:batchDownloadRequest.id,ok:false,error:'UI fixture ended.'});
      if(downloadUpdateRequest)window.NexusM.receive({id:downloadUpdateRequest.id,ok:false,error:'UI fixture ended.'});
      if(downloadUpdateCheckRequest)window.NexusM.receive({id:downloadUpdateCheckRequest.id,ok:false,error:'UI fixture ended.'});
      for(const request of batchCandidateRequests.values())window.NexusM.receive({id:request.id,ok:false,error:'UI fixture ended.'});
      batchCandidateRequests.clear();$('#clear-mod-selection').click();
      for(const request of previewRequests.values())window.NexusM.receive({id:request.id,ok:false,error:'UI fixture ended.'});
      previewRequests.clear();
      window.NexusM.setState({clearingDownloadFolder:false,downloadUpdates:[]});
      window.NexusM.receive({event:'refreshProgress',data:{running:false}});
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
