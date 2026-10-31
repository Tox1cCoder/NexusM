/* Real-viewport layout checks, run by the isolated WebView fixture. */
(() => {
  const width = window.innerWidth;
  const name = 'A very long mod title with multiple editions and compatibility patches '.repeat(5);
  window.NexusM.setState({
    games:[{id:1,domain:'layoutgame',name:'A game with an unusually long edition name '.repeat(5)}],
    mods:[
      {gameDomain:'layoutgame',modId:1,name,author:'An author with a very long display name '.repeat(10),version:'2026.10.31-hotfix.1234567890',updatedAt:1792450000,lastDownloadedAt:1792300000,needsReview:true},
      {gameDomain:'layoutgame',modId:2,name:'Downloaded mod',version:'1.0',updatedAt:1792200000,lastDownloadedAt:1792300000,needsReview:false}
    ],
    downloads:[],downloadUpdates:[],settings:{yourGamesOnly:true,hasApiKey:false},refreshProgress:{running:false}
  });
  window.NexusM.navigate('library');
  const wrap = document.querySelector('#mods-table-wrap');
  const bounds = wrap.getBoundingClientRect();
  const errors = [];
  const visible = element => {
    const style = getComputedStyle(element),rect = element.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
  };
  const checkOverflow = context => {
    if (wrap.scrollWidth > wrap.clientWidth + 1) errors.push(`${context}: library table requires horizontal scrolling.`);
    if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) errors.push(`${context}: the page overflows horizontally.`);
  };
  const rows = [...document.querySelectorAll('#mods-body tr[data-mod-key]')];
  if (rows.length !== 2) errors.push('The fixture must retain both library rows.');
  rows[0].classList.add('selected-for-download');
  rows[0].querySelector('input').checked = true;
  checkOverflow('Populated library');
  for (const row of rows) {
    if (row.cells.length !== 7 || !visible(row)) errors.push('A responsive row must retain every column.');
    if (row.tabIndex !== 0 || !row.getAttribute('aria-label')) errors.push('A responsive row must retain its keyboard opener.');
    row.focus({preventScroll:true});
    if (document.activeElement !== row) errors.push('A responsive row cannot receive keyboard focus.');
    const input = row.querySelector('input'),target = row.querySelector('.mod-select-target');
    const targetBounds = target.getBoundingClientRect();
    if (!visible(input) || targetBounds.width < 44 || targetBounds.height < 44) errors.push('Selection must retain a visible 44px target.');
    input.focus({preventScroll:true});
    const space = new KeyboardEvent('keydown',{key:' ',bubbles:true,cancelable:true});
    input.dispatchEvent(space);
    if (space.defaultPrevented || document.activeElement !== input) errors.push('Selection must retain native keyboard activation.');
    const cells = [...row.cells].map(cell => cell.getBoundingClientRect());
    for (const cell of row.cells) {
      const rect = cell.getBoundingClientRect();
      if (!visible(cell) || rect.left < bounds.left - 1 || rect.right > bounds.right + 1) errors.push('A responsive column is clipped.');
      if (cell.scrollWidth > cell.clientWidth + 1) errors.push('Column content overflows its available width.');
    }
    for (let a = 0; a < cells.length; a++) for (let b = a + 1; b < cells.length; b++) {
      const horizontal = Math.min(cells[a].right,cells[b].right) - Math.max(cells[a].left,cells[b].left);
      const vertical = Math.min(cells[a].bottom,cells[b].bottom) - Math.max(cells[a].top,cells[b].top);
      if (horizontal > 1 && vertical > 1) errors.push('Responsive columns overlap.');
    }
    for (const selector of ['.mod-thumb','.mod-name','.mod-game','.version-chip','.badge','.row-chevron']) {
      const element = row.querySelector(selector);
      const cell = element.closest('td').getBoundingClientRect();
      const rect = element.getBoundingClientRect();
      if (!visible(element)) errors.push(`${selector} is hidden in the responsive row.`);
      if (rect.left < cell.left - 1 || rect.right > cell.right + 1) errors.push(`${selector} overlaps another column.`);
    }
  }
  const search = document.querySelector('#mod-search');
  search.value = 'No matching layout fixture';
  search.dispatchEvent(new Event('input',{bubbles:true}));
  const empty = document.querySelector('#mods-body .no-results');
  if (!empty || !visible(empty) || empty.colSpan !== 7 || document.querySelector('#mods-body tr[data-mod-key]')) errors.push('Responsive search must retain the ordinary empty result.');
  checkOverflow('Empty search');
  if (empty) {
    const rect = empty.getBoundingClientRect();
    if (rect.left < bounds.left - 1 || rect.right > bounds.right + 1) errors.push('The empty search result is clipped.');
  }
  search.value = '';
  search.dispatchEvent(new Event('input',{bubbles:true}));
  return {test:`Library columns stay visible at ${width}px`,passed:errors.length===0,errors:[...new Set(errors)]};
})();
