// Allegiance Lobby — squads list page (P820a). Renders the lobby's squads from
// GET /api/squads, read-only.
//
// SECURITY-CRITICAL: like app.js/ladder.js, every row is built exclusively with
// document.createElement + .textContent (safe DOM construction). Squad names are
// player-supplied (account-held) and must never be interpolated into markup.

const statusEl = document.getElementById('status');
const tbody = document.querySelector('#squads tbody');

function cell(text, num) {
  const td = document.createElement('td');
  if (num) td.className = 'num';
  td.textContent = text;
  return td;
}

async function refresh() {
  let rows;
  try {
    const resp = await fetch('/api/squads/?sort=1&count=200');
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    rows = await resp.json();
  } catch (err) {
    statusEl.textContent = 'Could not reach the squad service (' + err.message + ')';
    return;
  }
  tbody.textContent = '';
  if (!Array.isArray(rows) || rows.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.className = 'empty';
    td.textContent = 'No squads have been formed yet. Create one from the in-game Squads screen.';
    tr.appendChild(td);
    tbody.appendChild(tr);
    statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
    return;
  }
  for (const s of rows) {
    const tr = document.createElement('tr');
    tr.appendChild(cell(s.ranking, true));
    const name = document.createElement('td');
    const link = document.createElement('a');
    link.href = '/squad-profile.html?id=' + encodeURIComponent(s.id);
    link.textContent = s.name;
    name.appendChild(link);
    tr.appendChild(name);
    tr.appendChild(cell(s.tag || ''));
    tr.appendChild(cell(s.memberCount, true));
    tr.appendChild(cell(s.leaderCallsign || ''));
    tbody.appendChild(tr);
  }
  statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

refresh();
