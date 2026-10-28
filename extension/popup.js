'use strict';
// Popup: current tab → "send to phone or PC", and "other devices" (other PCs' tabs + iPhone activity).
// All remote strings are inserted with textContent only. UI text: _locales/<lang>/messages.json (chrome.i18n, browser UI
// language; default_locale en). Plurals via Intl.PluralRules (<key>_one/_few/_many/_other), relative times via
// Intl.RelativeTimeFormat.

const $ = (id) => document.getElementById(id);
const send = (msg) => chrome.runtime.sendMessage(msg);
const isWeb = (u) => typeof u === 'string' && /^https?:\/\//i.test(u);
const TAB_LIMIT = 8;
const LANG = chrome.i18n.getUILanguage();
const i18n = (key, ...subs) => chrome.i18n.getMessage(key, subs.map(String)) || key;
function plural(key, n) {
  const cat = new Intl.PluralRules(LANG).select(n);
  return chrome.i18n.getMessage(`${key}_${cat}`, [String(n)]) || chrome.i18n.getMessage(`${key}_other`, [String(n)]);
}
document.documentElement.lang = LANG;
document.querySelectorAll('[data-i18n]').forEach((e) => { e.textContent = i18n(e.dataset.i18n); });

let currentTab = null;

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function hostOf(url) {
  try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return ''; }
}

function favicon(url, pageUrl) {
  if (isWeb(url)) {
    const img = el('img', 'fav');
    img.alt = '';
    img.referrerPolicy = 'no-referrer';
    img.src = url;
    img.onerror = () => img.replaceWith(letter(pageUrl));
    return img;
  }
  return letter(pageUrl);
}

function letter(pageUrl) {
  const h = hostOf(pageUrl || '');
  return el('span', 'fav letter', (h[0] || '•').toUpperCase());
}

function deviceIcon(kind) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', 'device-icon');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.6');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', kind === 'phone'
    ? 'M8 2.5h8a1.5 1.5 0 0 1 1.5 1.5v16a1.5 1.5 0 0 1-1.5 1.5H8A1.5 1.5 0 0 1 6.5 20V4A1.5 1.5 0 0 1 8 2.5Zm3 16h2'
    : 'M3.5 5.5h17v10h-17zM9 19.5h6M12 15.5v4');
  svg.appendChild(path);
  return svg;
}

function ago(ms) {
  if (!ms) return '';
  const s = Math.max(0, (Date.now() - ms) / 1000);
  const rtf = new Intl.RelativeTimeFormat(LANG, { numeric: 'auto', style: 'short' });
  if (s < 60) return rtf.format(0, 'second');
  if (s < 3600) return rtf.format(-Math.floor(s / 60), 'minute');
  if (s < 86400) return rtf.format(-Math.floor(s / 3600), 'hour');
  return rtf.format(-Math.floor(s / 86400), 'day');
}

function setStatus(status) {
  const s = $('status');
  const map = {
    ok: [i18n('statusOk'), 'ok'],
    connecting: [i18n('statusConnecting'), ''],
    not_running: [i18n('statusNotRunning'), 'bad'],
    not_installed: [i18n('statusNotInstalled'), 'bad'],
  };
  const [text, cls] = map[status] || [String(status || ''), 'bad'];
  s.textContent = text;
  s.className = `status ${cls}`;
}

// ---------------- current tab ----------------

async function renderCurrent() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  currentTab = tab || null;
  const icon = $('cur-icon');
  if (!tab || !isWeb(tab.url)) {
    $('cur-title').textContent = i18n('cannotSend');
    $('cur-host').textContent = i18n('onlyWebPages');
    icon.replaceWith(letter(''));
    return;
  }
  $('cur-title').textContent = tab.title || tab.url;
  $('cur-host').textContent = hostOf(tab.url);
  icon.replaceWith(favicon(tab.favIconUrl, tab.url));
}

async function renderTargets() {
  const box = $('targets');
  box.replaceChildren();
  let targets = [];
  try { targets = (await send({ cmd: 'getTargets' })).targets || []; } catch { /* ignore */ }
  if (!targets.length) {
    box.appendChild(el('span', 'empty', i18n('noTargets')));
    return;
  }
  for (const t of targets) {
    const b = el('button', 'chip');
    b.appendChild(deviceIcon(t.kind));
    b.appendChild(document.createTextNode(t.name));
    b.disabled = !currentTab || !isWeb(currentTab.url);
    b.addEventListener('click', () => sendTo(t, b));
    box.appendChild(b);
  }
}

async function sendTo(target, button) {
  const out = $('send-result');
  button.disabled = true;
  out.className = 'result';
  out.textContent = i18n('sending');
  try {
    const r = await send({ cmd: 'sendLink', targetId: target.id, url: currentTab.url, title: currentTab.title });
    out.className = r.ok ? 'result ok' : 'result err';
    out.textContent = r.ok ? i18n('sentTo', target.name) : (r.error ? i18n('sendFailedReason', r.error) : i18n('sendFailed'));
  } catch (e) {
    out.className = 'result err';
    out.textContent = i18n('sendFailed');
  } finally {
    button.disabled = false;
  }
}

// ---------------- other devices ----------------

function renderDevices(devices) {
  const box = $('devices');
  box.replaceChildren();
  if (!devices || !devices.length) {
    box.appendChild(el('div', 'notice', i18n('noDevices')));
    return;
  }
  for (const d of devices) {
    const card = el('div', 'device');
    const head = el('div', 'device-head');
    head.appendChild(deviceIcon(d.kind));
    head.appendChild(el('span', '', d.name));
    head.appendChild(el('span', 'sub', d.online ? '' : i18n('offline')));
    card.appendChild(head);

    if (d.kind === 'phone' && d.activity) {
      const a = d.activity;
      const row = el('div', 'row');
      const text = el('div', 'current-text');
      text.appendChild(el('div', 'title', a.title || (a.app ? i18n('appOpen', a.app) : i18n('sharedLink'))));
      text.appendChild(el('div', 'sub', [a.url ? hostOf(a.url) : '', ago(a.at)].filter(Boolean).join(' · ')));
      row.appendChild(text);
      if (a.canContinue) {
        const b = el('button', 'continue', i18n('continueOnPc'));
        b.addEventListener('click', async () => {
          if (isWeb(a.url)) { await chrome.tabs.create({ url: a.url }); window.close(); return; }
          await send({ cmd: 'continue', deviceId: d.id });
          window.close();
        });
        row.appendChild(b);
      }
      card.appendChild(row);
    }

    const tabs = (d.tabs || []).filter((t) => isWeb(t.url));
    tabs.slice(0, TAB_LIMIT).forEach((t) => card.appendChild(tabRow(t)));
    if (tabs.length > TAB_LIMIT) {
      const more = el('button', 'more', plural('moreTabs', tabs.length - TAB_LIMIT));
      more.addEventListener('click', () => {
        tabs.slice(TAB_LIMIT).forEach((t) => card.insertBefore(tabRow(t), more));
        more.remove();
      });
      card.appendChild(more);
    }
    if (d.kind === 'pc' && !tabs.length) card.appendChild(el('div', 'row sub', i18n('noTabs')));
    box.appendChild(card);
  }
}

function tabRow(t) {
  const row = el('button', 'row');
  row.title = t.url;
  row.appendChild(favicon(t.favIconUrl, t.url));
  const text = el('div', 'current-text');
  text.appendChild(el('div', 'title', t.title || t.url));
  text.appendChild(el('div', 'sub', [hostOf(t.url), t.browser === 'edge' ? 'Edge' : 'Chrome'].join(' · ')));
  row.appendChild(text);
  row.addEventListener('click', async () => {
    await chrome.tabs.create({ url: t.url });
    window.close();
  });
  return row;
}

// ---------------- boot ----------------

(async () => {
  await renderCurrent();
  let st = { status: 'connecting', devices: [] };
  try { st = await send({ cmd: 'status' }); } catch { /* ignore */ }
  setStatus(st.status);
  renderDevices(st.devices);
  try {
    const r = await send({ cmd: 'getDevices' });
    if (r && !r.error) { setStatus('ok'); renderDevices(r.devices); }
    else if (r && r.status) setStatus(r.status);
  } catch { /* keep cached */ }
  renderTargets();
})();
