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

// P1062 — the browser build's offer, read once from the lobby's own config answer.
// Enabled means every listed game that advertises a WebSocket route can be opened
// in the browser at the served prefix; a game without one shows no browser link.
// A failed/disabled answer leaves the column empty (the desktop Join link stays).
const offerReady = fetch('/api/browser')
  .then(res => res.ok ? res.json() : null)
  .then(offer => offer && offer.enabled === true && typeof offer.prefix === 'string' && offer.prefix ? offer : null)
  .catch(() => null);

function cell(text) {
  const td = document.createElement('td');
  // textContent only — never a markup string — so a hostile Name can't become markup.
  td.textContent = text;
  return td;
}

// The browser link opens the same listing entry the desktop Join link names, with
// that server preselected; the browser client still requires sign-in and a ticket.
function browserCell(server, offer) {
  const td = document.createElement('td');
  if (!offer || typeof server.webSocketUrl !== 'string' || server.webSocketUrl.length === 0) return td;
  const play = document.createElement('a');
  play.textContent = 'Play in browser';
  play.href = offer.prefix + '/menu.html?online=1&server=' + encodeURIComponent(server.id);
  td.appendChild(play);
  return td;
}

async function refresh() {
  const offer = await offerReady;
  let servers;
  try {
    const res = await fetch('/api/servers');
    if (!res.ok) throw new Error('HTTP ' + res.status);
    servers = await res.json();
  } catch (err) {
    tbody.textContent = '';
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 10;
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
    td.colSpan = 10;
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
      // P970 — a password-protected game shows a lock next to its mission name.
      // The FACT only: the lobby never receives the password itself.
      tr.appendChild(cell(s.missionName + (s.passwordRequired ? ' \uD83D\uDD12' : '')));
      tr.appendChild(cell(s.coreFile));
      tr.appendChild(cell(s.version));
      tr.appendChild(cell(s.lastSeenUtc ? new Date(s.lastSeenUtc).toLocaleString() : ''));
      const joinCell = document.createElement('td');
      const join = document.createElement('a');
      join.textContent = 'Join';
      join.href = 'allegiance://join?lobby=' + encodeURIComponent(location.origin) +
        '&id=' + encodeURIComponent(s.id) + '&v=1';
      joinCell.appendChild(join);
      tr.appendChild(joinCell);
      tr.appendChild(browserCell(s, offer));
      tbody.appendChild(tr);
    }
  }

  statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

refresh();
setInterval(refresh, REFRESH_MS);
