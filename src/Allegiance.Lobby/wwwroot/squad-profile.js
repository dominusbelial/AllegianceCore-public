// Allegiance Lobby — squad profile page (P820a). Renders one squad's details and
// membership from GET /api/squads/{id} + /{id}/log, read-only. Squad and
// member names are account-held; render exclusively with DOM-only construction.

const ROLES = { 0: 'Pending', 1: 'Member', 2: 'ASL', 3: 'Leader' };
function roleName(r) { return ROLES[r] || 'Unknown'; }
function cell(text) {
  const td = document.createElement('td');
  td.textContent = text;
  return td;
}

const id = new URLSearchParams(location.search).get('id');
const titleEl = document.getElementById('title');
const statusEl = document.getElementById('status');
const profileEl = document.getElementById('profile');
const tbody = document.querySelector('#members tbody');
const logEl = document.getElementById('log');

async function load() {
  if (!id) {
    titleEl.textContent = 'No squad selected';
    statusEl.textContent = 'Missing ?id=... query parameter.';
    return;
  }
  let d;
  try {
    const resp = await fetch('/api/squads/' + encodeURIComponent(id));
    if (resp.status === 404) {
      titleEl.textContent = 'Squad not found';
      statusEl.textContent = 'No squad exists for that id.';
      return;
    }
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    d = await resp.json();
  } catch (err) {
    titleEl.textContent = 'Could not load squad';
    statusEl.textContent = 'Squad service unreachable (' + err.message + ').';
    return;
  }

  titleEl.textContent = d.name + (d.tag ? '  [' + d.tag + ']' : '');
  document.title = d.name + ' — Squad Profile';
  document.getElementById('description').textContent = d.description || 'No description set.';
  const leader = (d.members || []).find(m => m.role === 3);
  document.getElementById('leader').textContent = leader ? leader.callsign : '—';
  const founded = new Date(d.createdAt * 1000).toISOString().slice(0, 10);
  document.getElementById('founded').textContent = founded;

  tbody.textContent = '';
  const members = d.members || [];
  if (members.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 2;
    td.className = 'empty';
    td.textContent = 'No members yet.';
    tr.appendChild(td);
    tbody.appendChild(tr);
  } else {
    for (const m of members) {
      const tr = document.createElement('tr');
      tr.appendChild(cell(m.callsign));
      tr.appendChild(cell(roleName(m.role)));
      tbody.appendChild(tr);
    }
  }

  logEl.textContent = '';
  try {
    const lresp = await fetch('/api/squads/' + encodeURIComponent(id) + '/log');
    if (lresp.ok) {
      const log = await lresp.json();
      (log || []).forEach(e => {
        const li = document.createElement('li');
        li.textContent = e.line;
        logEl.appendChild(li);
      });
    }
  } catch (_) { /* history is best-effort */ }

  profileEl.hidden = false;
  statusEl.textContent = '';
}

load();
