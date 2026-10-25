// Allegiance Lobby — pilot profile page (P808c). Renders one player's character
// info from GET /api/career/{accountId}, read-only. Account-held callsigns are
// rendered with DOM-only construction (never markup interpolation).

const RANK_NAMES = [
  '', 'Recruit', 'Cadet', 'Ensign', 'Lieutenant Junior Grade', 'Lieutenant',
  'Lieutenant Commander', 'Commander', 'Captain', 'Commodore', 'Rear Admiral',
  'Vice Admiral', 'Admiral', 'Fleet Admiral', 'Grand Admiral',
  'Master 1','Master 2','Master 3','Master 4','Master 5','Master 6',
  'Master 7','Master 8','Master 9'
];
function rankName(r) { return (r > 0 && r < RANK_NAMES.length) ? RANK_NAMES[r] : ''; }
function fmtHours(min) { return Math.floor(min / 60) + 'h ' + (min % 60) + 'm'; }
function cell(text, num) {
  const td = document.createElement('td');
  if (num) td.className = 'num';
  td.textContent = text;
  return td;
}

const account = new URLSearchParams(location.search).get('account');
const titleEl = document.getElementById('title');
const statusEl = document.getElementById('status');
const profileEl = document.getElementById('profile');
const tbody = document.querySelector('#civs tbody');

async function load() {
  if (!account) {
    titleEl.textContent = 'No pilot selected';
    statusEl.textContent = 'Missing ?account=... query parameter.';
    return;
  }
  let info;
  try {
    const resp = await fetch('/api/career/' + encodeURIComponent(account));
    if (resp.status === 404) {
      titleEl.textContent = 'Pilot not found';
      statusEl.textContent = 'No career data exists for that account id.';
      return;
    }
    if (!resp.ok) throw new Error('HTTP ' + resp.status);
    info = await resp.json();
  } catch (err) {
    titleEl.textContent = 'Could not load pilot';
    statusEl.textContent = 'Career service unreachable (' + err.message + ').';
    return;
  }

  titleEl.textContent = info.callsign;
  document.title = info.callsign + ' — Pilot Profile';
  document.getElementById('description').textContent =
    info.description || 'No profile description set.';
  document.getElementById('rating').textContent = info.rating || '—';
  document.getElementById('civcount').textContent = info.civs.length;
  const total = info.civs.reduce((a, c) => ({
    score: a.score + c.score, minutes: a.minutes + c.minutesPlayed,
    kills: a.kills + c.kills, deaths: a.deaths + c.deaths,
    wins: a.wins + c.wins, losses: a.losses + c.losses,
  }), { score: 0, minutes: 0, kills: 0, deaths: 0, wins: 0, losses: 0 });
  document.getElementById('totalscore').textContent = Math.round(total.score);
  document.getElementById('totaltime').textContent = fmtHours(total.minutes);
  document.getElementById('rating').textContent = info.rating || '—';

  tbody.textContent = '';
  if (!info.civs || info.civs.length === 0) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 11;
    td.className = 'empty';
    td.textContent = 'No career data recorded for this pilot yet.';
    tr.appendChild(td);
    tbody.appendChild(tr);
  } else {
    for (const c of info.civs) {
      const tr = document.createElement('tr');
      tr.appendChild(cell('Civ ' + c.civ));
      tr.appendChild(cell(rankName(c.rank)));
      tr.appendChild(cell(Math.round(c.score), true));
      tr.appendChild(cell(c.rating, true));
      tr.appendChild(cell(fmtHours(c.minutesPlayed), true));
      tr.appendChild(cell(c.kills, true));
      tr.appendChild(cell(c.deaths, true));
      tr.appendChild(cell(c.wins, true));
      tr.appendChild(cell(c.losses, true));
      tr.appendChild(cell(c.baseKills, true));
      tr.appendChild(cell(c.baseCaptures, true));
      tbody.appendChild(tr);
    }
    const tot = document.createElement('tr');
    tot.appendChild(cell('All civilizations'));
    tot.appendChild(cell(''));
    tot.appendChild(cell(Math.round(total.score), true));
    tot.appendChild(cell(''));
    tot.appendChild(cell(fmtHours(total.minutes), true));
    tot.appendChild(cell(total.kills, true));
    tot.appendChild(cell(total.deaths, true));
    tot.appendChild(cell(total.wins, true));
    tot.appendChild(cell(total.losses, true));
    tot.appendChild(cell(''));
    tot.appendChild(cell(''));
    tbody.appendChild(tot);
  }
  profileEl.hidden = false;
  statusEl.textContent = '';
}

load();
