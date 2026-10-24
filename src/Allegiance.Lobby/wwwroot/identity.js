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
function showAccount(account) {
  el('login').hidden = !!account; el('account').hidden = !account;
  el('pilot').textContent = account?.callsign || '';
  el('account-id').textContent = account ? 'Account ID: ' + account.id : '';
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
}
function action(id, fn) { el(id).addEventListener('click', async () => {
  el(id).disabled = true; try { await fn(); } catch (error) { say(error.message); } finally { el(id).disabled = false; }
}); }
action('register', () => passkey(true)); action('signin', () => passkey(false));
action('signout', async () => { await api('signout', {}); showAccount(null); say('Signed out of this browser.'); });
action('complete', async () => { const account = await api('external/complete', { callsign: el('callsign').value.trim() }); showAccount(account); say('Signed in.'); });
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
  try { showAccount(await api('me')); } catch { showAccount(null); }
  try {
    const providers = await api('providers');
    for (const provider of providers) {
      const link = document.createElement('a'); link.href = '/api/identity/external/' + provider;
      link.textContent = 'Continue with ' + provider; el('providers').appendChild(link);
    }
    el('complete').hidden = providers.length === 0;
  } catch (error) { say(error.message); }
})();
