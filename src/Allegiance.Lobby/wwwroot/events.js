// Allegiance Lobby — zone events list page (P820b). Renders the lobby's zone
// events from GET /api/events, read-only.
//
// SECURITY-CRITICAL: like app.js/squads.js, every row is built exclusively with
// document.createElement + .textContent (safe DOM construction). Event names,
// descriptions, sign-up URLs and file names are operator-supplied and must
// never be interpolated into markup. Only trusted URL values become anchors
// (href is set via setAttribute on a freshly-created anchor; protocol is
// enforced to http/https).

const statusEl = document.getElementById('status');
const tbody = document.querySelector('#events tbody');

function cell(text) {
  const td = document.createElement('td');
  td.textContent = text;
  return td;
}

// Build a link cell. Only http/https URLs become real anchors.
function urlCell(href) {
  const td = document.createElement('td');
  try {
    const u = new URL(href, window.location.href);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') { td.textContent = href; return td; }
    const a = document.createElement('a');
    a.setAttribute('href', u.href);
    a.textContent = 'Sign up';
    td.appendChild(a);
  } catch {
    td.textContent = href;
  }
  return td;
}

function filesCell(files) {
  const td = document.createElement('td');
  if (!Array.isArray(files) || files.length === 0) { td.textContent = ''; return td; }
  const list = document.createElement('ul');
  for (const f of files) {
    const li = document.createElement('li');
    li.textContent = f;
    list.appendChild(li);
  }
  td.appendChild(list);
  return td;
}

async function refresh() {
  let rows;
  try {
    const resp = await fetch('/api/events');
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    rows = await resp.json();
  } catch (err) {
    statusEl.textContent = 'Could not reach the events service (' + err.message + ')';
    return;
  }
  tbody.textContent = '';
  if (!Array.isArray(rows) || rows.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'empty';
    td.textContent = 'No zone events are scheduled yet.';
    tr.appendChild(td);
    tbody.appendChild(tr);
    statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
    return;
  }
  for (const e of rows) {
    const tr = document.createElement('tr');
    tr.appendChild(cell(e.date || ''));
    tr.appendChild(cell(e.time || ''));
    const nameTd = document.createElement('td');
    nameTd.textContent = e.name || '';
    tr.appendChild(nameTd);
    tr.appendChild(urlCell(e.signUpUrl || ''));
    const descTd = document.createElement('td');
    descTd.textContent = e.description || '';
    tr.appendChild(descTd);
    tr.appendChild(filesCell(e.files || []));
    tbody.appendChild(tr);
  }
  statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

refresh();
