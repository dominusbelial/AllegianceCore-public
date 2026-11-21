'use strict';
const el = id => document.getElementById(id);
const say = text => { el('status').textContent = text; };
async function api(path, body) {
  const response = await fetch('/api/identity/' + path, body === undefined ? {} : {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
  });
  if (!response.ok) throw new Error(response.status === 409 ? 'That callsign or passkey is already registered.' :
    response.status === 401 ? 'Sign in first.' : response.status === 429 ? 'Too many attempts. Please wait a minute.' :
      'Request refused (' + response.status + '). Check your callsign, code and permission.');
  return response.status === 204 ? null : response.json();
}
const decode = text => Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
const encode = data => btoa(String.fromCharCode(...new Uint8Array(data))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
function raw(credential) {
  const response = { clientDataJSON: encode(credential.response.clientDataJSON) };
  for (const k of ['attestationObject', 'authenticatorData', 'signature', 'userHandle'])
    if (credential.response[k]) response[k] = encode(credential.response[k]);
  if (credential.response.getTransports) response.transports = credential.response.getTransports();
  return { id: credential.id, rawId: encode(credential.rawId), type: credential.type, response,
    extensions: credential.getClientExtensionResults() };
}
// P1062 — the browser build's offer: the lobby's own config answer (enabled and
// the served prefix) plus the caller's `return` target, which the server answers
// only after its closed same-origin, path-only rule. A refused target is null, so
// this page never redirects anywhere but the browser client's own prefix.
let offer = { enabled: false };
const offerReady = (async () => {
  try {
    const target = new URLSearchParams(location.search).get('return');
    const reply = await fetch('/api/browser' + (target === null ? '' : '?return=' + encodeURIComponent(target)));
    if (reply.ok) offer = await reply.json();
  } catch { offer = { enabled: false }; }
  // The playable page of the generation: /play/menu.html, which opens the online
  // panel by itself when `online=1` is present.
  el('play-browser').href = offer.enabled ? (offer.returnTarget || offer.prefix + '/menu.html?online=1') : '';
  el('play-now').href = el('play-browser').href;
  return offer;
})();
// The round trip: a signed-in player who came from the browser client returns to
// that same tab already signed in — no manual refresh and no second tab.
async function enterBrowser() {
  await offerReady;
  if (offer.enabled && offer.returnTarget) location.replace(offer.returnTarget);
}
async function showAccount(account) {
  await offerReady;
  el('login').hidden = !!account; el('account').hidden = !account;
  el('pilot').textContent = account?.callsign || '';
  el('account-id').textContent = account ? 'Account ID: ' + account.id : '';
  el('play-browser').hidden = !(account && offer.enabled);
  el('play-now').hidden = !!account || !offer.enabled;
  el('approve').hidden = true;
}
async function passkey(register) {
  if (!window.PublicKeyCredential) throw new Error('This browser needs passkey support and an HTTPS connection (or localhost).');
  const result = await api('passkeys/' + (register ? 'register' : 'login') + '/options', register ? { callsign: el('callsign').value.trim() } : {});
  const options = JSON.parse(result.options); options.challenge = decode(options.challenge);
  if (register) { options.user.id = decode(options.user.id); options.excludeCredentials?.forEach(c => c.id = decode(c.id)); }
  else options.allowCredentials?.forEach(c => c.id = decode(c.id));
  const credential = register ? await navigator.credentials.create({ publicKey: options }) : await navigator.credentials.get({ publicKey: options });
  if (!credential) throw new Error('Passkey request cancelled.');
  const account = await api('passkeys/' + (register ? 'register' : 'login'), { challenge: result.challenge, credential: raw(credential) });
  showAccount(account); say('Signed in. Enter the code shown in your game to continue.');
  enterBrowser();
}
function action(id, fn) { el(id).addEventListener('click', async () => {
  el(id).disabled = true; try { await fn(); } catch (error) { say(error.message); } finally { el(id).disabled = false; }
}); }
action('register', () => passkey(true)); action('signin', () => passkey(false));
action('signout', async () => { await api('signout', {}); showAccount(null); say('Signed out of this browser.'); });
action('complete', async () => { const account = await api('external/complete', { callsign: el('callsign').value.trim() }); showAccount(account); say('Signed in.'); enterBrowser(); });
action('review', async () => {
  const code = el('code').value.trim().toUpperCase();
  const device = await api('device/' + encodeURIComponent(code));
  el('device').textContent = (device.purpose === 'server' ? 'Game server enrollment: ' : 'Game sign-in: ') + device.name;
  el('approve').hidden = device.approved;
  el('approve').dataset.code = code;
  say(device.approved ? 'This code has already been approved.' : 'Check that this is your device, then approve.');
});
el('code').addEventListener('input', () => { el('approve').hidden = true; el('device').textContent = ''; });
action('approve', async () => { await api('device/approve', { userCode: el('approve').dataset.code }); el('approve').hidden = true; say('Approved. Return to your game or server.'); });
(async () => {
  await offerReady;
  let account = null;
  try { account = await api('me'); } catch { account = null; }
  showAccount(account);
  // Already signed in when the page loads: return to the browser client at once.
  if (account) enterBrowser();
  try {
    const providers = await api('providers');
    for (const provider of providers) {
      const link = document.createElement('a');
      // P1062 — the provider callback keeps a validated return target, so a
      // signed-in player still comes back to the browser client.
      link.href = '/api/identity/external/' + provider + (offer.returnTarget ? '?return=' + encodeURIComponent(offer.returnTarget) : '');
      link.textContent = 'Continue with ' + provider; el('providers').appendChild(link);
    }
    el('complete').hidden = providers.length === 0;
  } catch (error) { say(error.message); }
})();
