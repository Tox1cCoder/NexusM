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
  if (wrap.scrollWidth > wrap.clientWidth + 1) errors.push('Library table requires horizontal scrolling.');
  if (document.documentElement.scrollWidth > document.documentElement.clientWidth + 1) errors.push('The page overflows horizontally.');
  for (const row of document.querySelectorAll('#mods-body tr[data-mod-key]')) {
    const badge = row.querySelector('.badge').getBoundingClientRect();
    const open = row.querySelector('.row-chevron').getBoundingClientRect();
    if (badge.left < bounds.left || badge.right > bounds.right) errors.push('Status badge is clipped.');
    if (open.left < bounds.left || open.right > bounds.right) errors.push('Open arrow is clipped.');
    const cells = [...row.cells].map(cell => cell.getBoundingClientRect());
    for (const selector of ['.mod-thumb','.mod-name','.mod-game','.version-chip']) {
      const element = row.querySelector(selector);
      const cell = element.closest('td').getBoundingClientRect();
      const rect = element.getBoundingClientRect();
      if (rect.left < cell.left - 1 || rect.right > cell.right + 1) errors.push(`${selector} overlaps another column.`);
    }
    if (cells.at(-1).right > bounds.right + 1) errors.push('The last column extends past the table.');
  }
  return {test:`Library columns stay visible at ${width}px`,passed:errors.length===0,errors};
})();
