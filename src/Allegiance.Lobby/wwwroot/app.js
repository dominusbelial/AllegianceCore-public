// Allegiance Lobby browser page — renders the live server list, refreshing every 15s.
//
// SECURITY-CRITICAL: every row is built exclusively with document.createElement(...)
// + .textContent = ... (safe DOM construction). We NEVER build markup strings from
// server-supplied fields. A server's Name / MissionName / Address is attacker-controlled —
// anyone can register with any string — and interpolating those fields into markup would
// let a malicious self-hoster inject script into every visitor's browser. This is the one
// place untrusted text from the network reaches other users' browsers; keep it DOM-only.

const REFRESH_MS = 15000;

const table = document.getElementById('servers');
const tbody = table.tBodies[0];
const statusEl = document.getElementById('status');

function cell(text) {
  const td = document.createElement('td');
  // textContent only — never a markup string — so a hostile Name can't become markup.
  td.textContent = text;
  return td;
}

async function refresh() {
  let servers;
  try {
    const res = await fetch('/api/servers');
    if (!res.ok) throw new Error('HTTP ' + res.status);
    servers = await res.json();
  } catch (err) {
    tbody.textContent = '';
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 8;
    td.className = 'empty';
    td.textContent = 'Could not reach the lobby directory (' + err.message + ')';
    tr.appendChild(td);
    tbody.appendChild(tr);
    statusEl.textContent = 'Last refresh failed: ' + new Date().toLocaleTimeString();
    return;
  }

  tbody.textContent = '';

  if (!Array.isArray(servers) || servers.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 8;
    td.className = 'empty';
    td.textContent = 'No servers online right now.';
    tr.appendChild(td);
    tbody.appendChild(tr);
  } else {
    for (const s of servers) {
      const tr = document.createElement('tr');
      tr.appendChild(cell(s.name + (s.verified ? " [verified]" : " [unverified]")));
      tr.appendChild(cell(s.address));
      tr.appendChild(cell(s.port));
      tr.appendChild(cell(s.playerCount + ' / ' + s.maxPlayers));
      tr.appendChild(cell(s.missionName));
      tr.appendChild(cell(s.coreFile));
      tr.appendChild(cell(s.version));
      tr.appendChild(cell(s.lastSeenUtc ? new Date(s.lastSeenUtc).toLocaleString() : ''));
      tbody.appendChild(tr);
    }
  }

  statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

refresh();
setInterval(refresh, REFRESH_MS);
