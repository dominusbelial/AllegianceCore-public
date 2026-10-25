// Allegiance Lobby — career ladder page (P808c). Renders the leaderboard from
// the lobby's career API (GET /api/career/leaderboard), read-only.
//
// SECURITY-CRITICAL: like app.js, every row is built exclusively with
// document.createElement + .textContent (safe DOM construction). Callsigns are
// player-supplied (account-held) and must never be interpolated into markup.

const civSel = document.getElementById('civ');
const sortSel = document.getElementById('sort');
const statusEl = document.getElementById('status');
const tbody = document.querySelector('#board tbody');

// The rank names (native LookupRankName clamp-to-top semantics). Kept in sync
// with the game client's CareerRanks ladder.
const RANK_NAMES = [
  '', 'Recruit', 'Cadet', 'Ensign', 'Lieutenant Junior Grade', 'Lieutenant',
  'Lieutenant Commander', 'Commander', 'Captain', 'Commodore', 'Rear Admiral',
  'Vice Admiral', 'Admiral', 'Fleet Admiral', 'Grand Admiral',
  'Master 1','Master 2','Master 3','Master 4','Master 5','Master 6',
  'Master 7','Master 8','Master 9'
];
function rankName(r) { return (r > 0 && r < RANK_NAMES.length) ? RANK_NAMES[r] : ''; }
function fmtHours(min) { return Math.floor(min / 60) + 'h'; }
function cell(text, num) {
  const td = document.createElement('td');
  if (num) td.className = 'num';
  td.textContent = text;
  return td;
}

function populateCivs(entries) {
  // Discover the civilizations actually present so "All" plus each civ is offered.
  const present = new Set(entries.filter(e => e.civ !== undefined).map(e => e.civ));
  present.forEach(civ => {
    const opt = document.createElement('option');
    opt.value = civ;
    opt.textContent = 'Civilization ' + civ;
    civSel.appendChild(opt);
  });
}

async function refresh() {
  const civ = civSel.value || '-1';
  const sort = sortSel.value || '3';
  let resp;
  try {
    resp = await fetch(`/api/career/leaderboard?civ=${encodeURIComponent(civ)}&sort=${encodeURIComponent(sort)}`);
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
  } catch (err) {
    statusEl.textContent = 'Could not reach the career service (' + err.message + ')';
    return;
  }
  const board = await resp.json();
  tbody.textContent = '';
  if (!Array.isArray(board.entries) || board.entries.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 11;
    td.className = 'empty';
    td.textContent = board.failReason && board.failReason !== 'no players'
      ? 'Could not find ranking info for ' + board.failReason + '.'
      : 'No career data yet — play a ranked match on a verified server.';
    tr.appendChild(td);
    tbody.appendChild(tr);
    statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
    return;
  }
  for (const e of board.entries) {
    const tr = document.createElement('tr');
    tr.appendChild(cell(e.position, true));
    const name = document.createElement('td');
    const link = document.createElement('a');
    link.href = '/profile.html?account=' + encodeURIComponent(e.accountId);
    link.textContent = e.callsign;
    name.appendChild(link);
    tr.appendChild(name);
    tr.appendChild(cell(rankName(e.rank)));
    tr.appendChild(cell(e.rating, true));
    tr.appendChild(cell(e.gamesPlayed, true));
    tr.appendChild(cell(fmtHours(e.minutesPlayed), true));
    tr.appendChild(cell(e.kills, true));
    tr.appendChild(cell(e.deaths, true));
    tr.appendChild(cell(e.wins, true));
    tr.appendChild(cell(e.losses, true));
    tr.appendChild(cell(Math.round(e.totalScore), true));
    tbody.appendChild(tr);
  }
  statusEl.textContent = 'Updated ' + new Date().toLocaleTimeString();
}

civSel.addEventListener('change', refresh);
sortSel.addEventListener('change', refresh);
refresh();
