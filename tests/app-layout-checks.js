/* App-wide layout fixtures. Network and host mutations stay intercepted by UiTests. */
(() => {
  const report = window.__appLayoutChecks = {status:'running',checks:[]};
  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const bridge = window.chrome.webview, originalPost = bridge.postMessage;
  const viewport = `${innerWidth} x ${innerHeight}px`;
  const longWord = 'ArchiveEditionCompatibilityPatch'.repeat(8);
  const longText = 'An unusually long title with several edition and compatibility notes '.repeat(4);
  const mod = {gameDomain:'layoutgame',modId:1,name:longText,author:longWord,version:longWord,updatedAt:1792450000,lastDownloadedAt:1792300000,needsReview:true};
  const details = {
    mod:{...mod,summary:longText,description:`<p>${longText}</p><table><tr><th>File</th><th>Installation instructions</th></tr><tr><td>${longWord}</td><td>${longText}</td></tr></table><pre><code>${longWord.repeat(4)}</code></pre>`},
    files:[{fileId:10,name:longWord,category:'MAIN',version:longWord,size:4096,uploadedAt:1792450000,description:longText}],
    changelogs:{[longWord]:[longText]},
    requirements:[{name:longWord,notes:longText,url:'https://www.nexusmods.com/layoutgame/mods/2'}]
  };
  const errors = [];
  let context = '';
  const fail = message => errors.push(`${context}: ${message}`);
  const visible = element => !!element && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== 'hidden';
  const fit = (element,label) => {
    if(!visible(element)){fail(`${label} is hidden.`);return;}
    const rect = element.getBoundingClientRect();
    if(rect.left < -1 || rect.right > document.documentElement.clientWidth + 1) fail(`${label} extends beyond the window (${rect.left.toFixed(1)}..${rect.right.toFixed(1)}px, available ${document.documentElement.clientWidth}px).`);
    if(element.clientWidth > 0 && element.scrollWidth > element.clientWidth + 1) fail(`${label} needs horizontal scrolling or clips content (${element.scrollWidth}/${element.clientWidth}px).`);
  };
  const pageFits = () => {
    if(document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) fail('The page overflows horizontally.');
    fit($('.page.active'),'Active page');
  };
  const reach = (element,label,container=null) => {
    if(!visible(element)){fail(`${label} is hidden.`);return;}
    element.scrollIntoView({block:'center',inline:'nearest'});
    const rect = element.getBoundingClientRect();
    const bounds = container ? container.getBoundingClientRect() : {top:0,bottom:innerHeight,left:0,right:document.documentElement.clientWidth};
    let top=Math.max(0,bounds.top),bottom=Math.min(innerHeight,bounds.bottom);
    if(container && element.closest('#detail-content')){
      const content=$('#detail-content').getBoundingClientRect();
      top=Math.max(top,content.top);bottom=Math.min(bottom,content.bottom);
      if(!element.closest('.detail-tabs')){
        const tabs=$('.detail-tabs').getBoundingClientRect();
        if(tabs.top<=top+1)top=Math.max(top,tabs.bottom);
      }
    }
    if(rect.top < top - 1 || rect.bottom > bottom + 1 || rect.left < bounds.left - 1 || rect.right > bounds.right + 1)
      fail(`${label} cannot be reached by vertical scrolling (control ${rect.top.toFixed(1)}..${rect.bottom.toFixed(1)}px, visible ${top.toFixed(1)}..${bottom.toFixed(1)}px).`);
  };
  const prepareCapture = scene => {
    // Keep screenshots readable while every assertion above still uses adversarial content.
    const saved=new Map(),remember=(node,value)=>{if(!saved.has(node))saved.set(node,node.nodeValue);node.nodeValue=value;};
    const replace=(selector,text)=>{for(const element of $$(selector))if(element.firstChild?.nodeType===Node.TEXT_NODE)remember(element.firstChild,text);};
    const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);
    for(let node=walker.nextNode();node;node=walker.nextNode()){
      const value=node.nodeValue.replaceAll(longText.toUpperCase(),"BALDUR'S GATE 3").replaceAll(longText,'Improved UI — Compatibility and Accessibility Update').replaceAll(longWord,'ImprovedUI-3.2.1');
      if(value!==node.nodeValue)remember(node,value);
    }
    const names=["Baldur's Gate 3",'Skyrim Special Edition','Cyberpunk 2077','Fallout 4','Stardew Valley','Elden Ring','Starfield','The Witcher 3','Hogwarts Legacy','Oblivion Remastered','Fallout New Vegas','Red Dead Redemption 2','Monster Hunter Wilds','Kingdom Come Deliverance II','Dragon Age Origins','Mass Effect Legendary Edition','No Mans Sky','Valheim'];
    $$('.game-fallback>span,.game-label').forEach((element,i)=>{if(element.firstChild)remember(element.firstChild,names[i%names.length]);});
    replace('.mod-game,.detail-game',"Baldur's Gate 3");
    replace('.version-chip','3.2.1');replace('#account strong','NexusM demo');
    replace('.file-title-row>strong','Improved UI — Main file with compatibility fixes');
    $$('.download-info h3').forEach((element,i)=>remember(element.firstChild,['Improved UI — Main archive','Community Compatibility Patch','Inventory Improvements','Improved Dialogue Interface'][i%4]));
    for(const element of $$('.download-error,.download-error-code'))if(element.firstChild)remember(element.firstChild,element.classList.contains('download-error-code')?'NETWORK_TIMEOUT':'The connection timed out. Retry this download when ready.');
    const scroll=$('#detail-content'),scrollTop=scroll.scrollTop;
    if(scene==='details')$('.files-toolbar')?.scrollIntoView({block:'start'});else if(scene!=='floating-download')window.scrollTo(0,0);
    return ()=>{for(const [node,value]of saved)if(node.isConnected)node.nodeValue=value;scroll.scrollTop=scrollTop;};
  };
  const check = async (name, action, capture=null) => {
    context = name;const start = errors.length;
    try{await action();}catch(error){fail(error.message);}
    report.checks.push({test:`${name} at ${viewport}`,passed:errors.length===start,errors:errors.slice(start)});
    if(capture && window.__captureAppLayout){
      const restore=prepareCapture(capture);
      await new Promise(resolve=>setTimeout(resolve,200));
      report.capture=capture;
      await new Promise(resolve=>window.__resumeAppLayout=resolve);
      restore();
      report.capture=null;window.__resumeAppLayout=null;
    }
  };
  const settle = () => new Promise(resolve => setTimeout(resolve,0));
  bridge.postMessage = message => {
    const result = message.action === 'getModDetails' ? details
      : message.action === 'getFileContents' ? {available:true,entries:[{path:`${Array.from({length:12},(_,i)=>`Folder-${i}`).join('/')}/${longWord}.pak`,size:4096}]}
      : message.action === 'getBatchDownloadCandidates' ? {candidates:message.payload.mods.map(candidate=>({...candidate,eligible:true,status:'eligible',fileId:10,name:'Main archive'}))}
      : null;
    queueMicrotask(()=>window.NexusM.receive(result ? {id:message.id,ok:true,result} : {id:message.id,ok:false,error:'Host mutations are blocked by the layout fixture.'}));
  };
  async function run(){
    try{
      $('.toasts').replaceChildren();
      window.NexusM.setState({
        games:Array.from({length:18},(_,i)=>({id:i+1,domain:i?`layoutgame${i}`:'layoutgame',name:i===0?longText:`Game ${i+1}`})),
        mods:[mod],downloads:[],downloadUpdates:[],fileDownloads:{},user:{name:longWord,isPremium:false},
        settings:{hasApiKey:false,yourGamesOnly:true,downloadFolder:`C:\\Downloads\\${longWord}`},refreshProgress:{running:false},clearingDownloadFolder:false
      });
      await check('All games and library controls fit',()=>{
        window.NexusM.navigate('library');
        pageFits();fit($('#game-strip'),'Game collection');
        if($$('#game-strip .game-card').length!==20) fail('The game collection must retain all games plus its two actions.');
        const bounds=$('#game-strip').getBoundingClientRect();
        for(const card of $$('#game-strip .game-card')){
          const rect=card.getBoundingClientRect();
          if(rect.left<bounds.left-1||rect.right>bounds.right+1) fail('A game card requires horizontal scrolling.');
          for(const label of card.querySelectorAll('.game-fallback>span,.game-label')){
            const content=label.getBoundingClientRect();
            fit(label,'Game title');
            if(label.scrollHeight>label.clientHeight+1||content.top<rect.top-1||content.bottom>rect.bottom+1)fail('A game card clips its title vertically.');
          }
        }
        for(const selector of ['.mods-heading','.table-toolbar','.table-filters','.table-controls','.batch-selection-actions']){
          const element=$(selector);if(visible(element))fit(element,selector);
        }
        for(const selector of ['#refresh-button','#add-game-button','#import-history-button','#add-mod-button','#mod-search','#mod-sort'])reach($(selector),selector);
        window.scrollTo(0,0);
      },'library');
      await check('Floating download action fits the scrolled library',async()=>{
        window.NexusM.setState({mods:[mod,...Array.from({length:30},(_,i)=>({...mod,modId:100+i,name:`Community compatibility mod ${i+1}`}))]});
        window.NexusM.navigate('library');
        $('[data-select-mod="layoutgame:1"]').click();await settle();
        window.scrollTo(0,$('#download-selected-mods').getBoundingClientRect().bottom+40);
        const floating=$('#floating-download-selected-mods');
        for(let attempt=0;attempt<50&&!visible(floating);attempt++)await new Promise(resolve=>setTimeout(resolve,20));
        fit(floating,'Floating download action');
        if(!visible(floating))return;
        const rect=floating.getBoundingClientRect(),sidebar=$('.sidebar').getBoundingClientRect();
        if(rect.left<sidebar.right-1||rect.top<0||rect.bottom>innerHeight+1||rect.height<44)fail('The floating action must fit beside the sidebar and remain reachable in the viewport.');
        if(!floating.contains(document.elementFromPoint((rect.left+rect.right)/2,(rect.top+rect.bottom)/2)))fail('Another element covers the floating action.');
        floating.focus({preventScroll:true});
        if(document.activeElement!==floating)fail('The floating action must accept keyboard focus without moving the library.');
        window.scrollTo(0,document.documentElement.scrollHeight);await settle();
        if($('#mods-body tr:last-child').getBoundingClientRect().bottom>floating.getBoundingClientRect().top)fail('The floating action obscures the final library row at the bottom of the page.');
        pageFits();
      },'floating-download');
      $('#clear-mod-selection').click();window.NexusM.setState({mods:[mod]});window.scrollTo(0,0);
      await check('Settings content and actions fit',()=>{
        window.NexusM.navigate('settings');pageFits();
        for(const element of $$('#page-settings .settings-card,#page-settings .card-heading,#page-settings .card-footer,#page-settings .setting-row,#page-settings .about-facts'))fit(element,element.className);
        for(const element of $$('#page-settings button,#page-settings input,#page-settings a'))reach(element,element.id||element.textContent.trim());
        window.scrollTo(0,0);
      },'settings');
      await check('Downloads retain filenames, statuses, actions, and logs',()=>{
        window.NexusM.setState({downloads:['waiting-authorization','failed','completed','downloading'].map((status,i)=>({id:`layout-download-${i}`,gameDomain:'layoutgame',modId:1,fileId:i+30,name:longText,fileName:`${longWord}.zip`,status,needsUser:true,authorizationMessage:longText,error:status==='failed'?longWord:'',errorCode:status==='failed'?longWord:'',bytes:1024,total:4096,events:[{time:1792450000,code:'LAYOUT_FIXTURE',message:longWord}]}))});
        window.NexusM.navigate('downloads');
        for(const log of $$('.download-log'))log.open=true;
        pageFits();
        for(const element of $$('.download-card,.download-info,.download-info h3,.download-meta,.download-status,.download-actions,.download-log ol'))fit(element,element.className||'Download title');
        for(const element of $$('#page-downloads button,.download-log summary'))reach(element,element.getAttribute('aria-label')||element.textContent.trim());
        if($$('.download-card').length!==4||!$('[data-retry-download]')||!$('[data-download-folder]'))fail('Every fixture entry and its ordinary actions must remain available.');
        window.scrollTo(0,0);
      },'downloads');
      await check('Import dialog fits and its footer remains reachable',async()=>{
        window.NexusM.navigate('library');$('#import-history-button').click();await settle();
        const dialog=$('#input-dialog');
        if(!dialog.open)throw new Error('The import dialog did not open.');
        fit(dialog,'Import dialog');fit($('.dialog-options'),'Import methods');
        for(const element of $$('#input-dialog button,#input-dialog textarea,#input-dialog input'))reach(element,element.id,dialog);
        $('#dialog-cancel').click();
      });
      await check('Mod files and archive paths fit',async()=>{
        await window.NexusM.showMod('layoutgame:1');
        const panel=$('#detail-panel');
        await Promise.all(panel.getAnimations().map(animation=>animation.finished.catch(()=>{})));
        fit(panel,'Mod details');
        for(const element of $$('.detail-topbar,.detail-intro,.detail-tabs,.detail-tab-content,.file-card,.file-card-head,.file-card-meta,.file-card-actions'))fit(element,element.className);
        const select=$('[data-select-file="10"]');select.click();
        $('[data-preview-file="10"]').click();await settle();
        for(const folder of $$('.preview-folder'))folder.open=true;
        fit($('.file-preview-tree'),'Archive tree');
        const file=$('.preview-file');if(!file)fail('The archive path must retain its final filename.');else fit(file,'Archive filename');
        if(!select.checked)fail('Opening a file preview lost the selected file.');
        for(const element of $$('#detail-panel button,#detail-panel input'))reach(element,element.id||element.textContent.trim(),panel);
        $('#detail-content').scrollTop=0;panel.scrollTop=0;
      },'details');
      await check('Overview, changelog, and requirements fit',()=>{
        const panel=$('#detail-panel');
        for(const tab of ['overview','changelog','requirements']){
          $(`[data-detail-tab="${tab}"]`).click();fit(panel,`${tab} panel`);fit($('#detail-tab-body'),`${tab} content`);
          if(tab==='overview'){
            if(!$('.rich-text table')||!$('.rich-text pre'))fail('Wide author content must be preserved.');
            for(const element of $$('.rich-text table,.rich-text pre')){
              fit(element,element.tagName==='TABLE'?'Author table':'Author code sample');
              if(element.tabIndex!==0)fail('Author content must remain keyboard accessible.');
            }
          }
          for(const element of $$('#detail-tab-body button'))reach(element,element.getAttribute('aria-label')||element.textContent.trim(),panel);
        }
        $('#detail-close').click();
      });
      await check('Update dialog fits long filenames and keeps every choice reachable',async()=>{
        const id='layout-update-archive';
        window.NexusM.setState({downloads:[{id,gameDomain:'layoutgame',modId:1,fileId:40,status:'completed',name:longText}],downloadUpdates:[{id,gameDomain:'layoutgame',modId:1,fileId:41,modName:longText,oldName:longText,oldFilename:`${longWord}.zip`,oldPath:`C:\\Downloads\\${longWord}.zip`,oldVersion:longWord,name:longText,filename:`updated-${longWord}.zip`,version:longWord,updateKey:`layout:${viewport}:${Date.now()}`,archiveKey:'layout-fixture'}]});
        await settle();
        const dialog=$('#download-update-dialog');
        if(!dialog.open)throw new Error('The retained archive update did not open.');
        fit(dialog,'Update dialog');fit($('#download-update-files'),'Archive comparison');
        for(const element of $$('#download-update-dialog button'))reach(element,element.id,dialog);
        if(!$('#download-update-path').textContent.includes(longWord))fail('The complete old archive path must be preserved.');
        window.NexusM.setState({downloadUpdates:[]});
      });
    }finally{
      bridge.postMessage=originalPost;
      if($('#input-dialog').open)$('#input-dialog').close();
      window.NexusM.setState({downloadUpdates:[],downloads:[],games:[],mods:[],user:null,settings:{hasApiKey:false},notice:''});
      if(!$('#detail-panel').classList.contains('hidden'))$('#detail-close').click();
      $('.toasts').replaceChildren();window.NexusM.navigate('library');window.scrollTo(0,0);
      report.status='complete';
    }
  }
  run().catch(error=>{report.checks.push({test:`Layout fixture at ${viewport}`,passed:false,errors:[error.message]});report.status='complete';});
})();
