// The page under test for the browser automation scenarios. Its content security policy allows no inline script and
// no eval, which is what the automation agent has to work under.
(function () {
  'use strict';
  let clicks = 0;
  const $ = id => document.getElementById(id);
  $('counter').addEventListener('click', e => { clicks++; e.currentTarget.textContent = 'Clicked ' + clicks + ' times'; });
  $('counter').addEventListener('dblclick', e => { e.currentTarget.dataset.double = 'yes'; });
  $('form').addEventListener('submit', e => {
    e.preventDefault();
    const out = {};
    for (const [key, value] of new FormData(e.target)) out[key] = typeof value === 'string' ? value : value.name;
    out.editor = $('editor').textContent;
    $('result').textContent = JSON.stringify(out);
  });
  $('hover-target').addEventListener('mouseenter', () => { $('hover-state').textContent = 'hovered'; });
  $('drag-source').addEventListener('dragstart', e => e.dataTransfer.setData('text/plain', 'payload'));
  $('drop-target').addEventListener('dragover', e => e.preventDefault());
  $('drop-target').addEventListener('drop', e => { e.preventDefault(); e.currentTarget.textContent = 'Dropped ' + e.dataTransfer.getData('text/plain'); });
  $('confirm').addEventListener('click', e => { e.currentTarget.textContent = confirm('Proceed?') ? 'Confirmed' : 'Declined'; });
  $('later').addEventListener('click', () => setTimeout(() => {
    const paragraph = document.createElement('p');
    paragraph.textContent = 'Late arrival';
    document.body.appendChild(paragraph);
  }, 300));
  $('log').addEventListener('click', () => { console.log('clicked %s', 'log', { answer: 42 }); console.warn('careful'); console.error(new Error('boom')); });
  $('fetch').addEventListener('click', async () => {
    const response = await fetch('automation-data.json', { headers: { 'X-Test': '1' } });
    $('result').textContent = 'fetched ' + (await response.json()).value;
  });
  $('file').addEventListener('change', e => { $('result').textContent = 'files ' + Array.from(e.target.files).map(f => f.name + ':' + f.size).join(','); });
  document.addEventListener('keydown', e => { if (e.key === 'k' && e.ctrlKey) $('result').textContent = 'shortcut'; });
  console.info('fixture ready');
})();
