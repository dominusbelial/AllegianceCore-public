// Run with node tools/p944-lobby-page-test.js. No browser or external packages.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
class Element {
  constructor(tag) { this.tag = tag; this.children = []; }
  appendChild(child) { this.children.push(child); }
  set textContent(text) { this.text = text; this.children = []; }
}
const tbody = new Element('tbody');
const status = new Element('p');
const hostile = '<img src=x onerror=alert(1)> & " --auto-join';
const servers = [false, true].map(playerHosted => ({
  id: hostile + (playerHosted ? '/hosted?&=' : '/dedicated'), name: hostile,
  address: hostile, port: 6073, playerCount: 1, maxPlayers: 8, missionName: hostile,
  coreFile: 'PCore001.igc', version: '1', playerHosted,
}));
const context = {
  document: { createElement: tag => new Element(tag), getElementById: id => id === 'servers' ? { tBodies: [tbody] } : status },
  location: { origin: 'https://lobby.example.org:4443' },
  fetch: async () => ({ ok: true, json: async () => servers }), setInterval: () => {},
};
vm.createContext(context);
vm.runInContext(fs.readFileSync(process.argv[2] || 'src/Allegiance.Lobby/wwwroot/app.js', 'utf8'), context);
setImmediate(() => {
  assert.equal(tbody.children.length, 2);
  for (let i = 0; i < 2; i++) {
    const row = tbody.children[i];
    assert.equal(row.children.length, 9);
    assert.equal(row.children[0].text, hostile + ' [unverified]');
    const link = row.children[8].children[0];
    assert.equal(link.tag, 'a'); assert.equal(link.text, 'Join');
    const url = new URL(link.href);
    assert.equal(url.protocol, 'allegiance:'); assert.equal(url.hostname, 'join');
    assert.equal(url.searchParams.get('lobby'), context.location.origin);
    assert.equal(url.searchParams.get('id'), servers[i].id);
    assert.equal(url.searchParams.get('v'), '1');
    assert.equal([...url.searchParams].length, 3);
  }
  console.log('P944 lobby DOM: dedicated and player-hosted hostile-field cases passed.');
});
