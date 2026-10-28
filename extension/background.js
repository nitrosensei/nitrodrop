// NitroDrop extension service worker.
// Collects normal-window tabs (never incognito), pushes them debounced to NitroDrop over Native Messaging,
// and relays popup requests (other devices' tabs, link targets, send link).
'use strict';

const HOST = 'com.nitrobyte.nitrodrop';
const DEBOUNCE_MS = 800;
const REQUEST_TIMEOUT_MS = 8000;

const BROWSER = (() => {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  if (brands.some((b) => /Edge/i.test(b.brand)) || / Edg\//.test(navigator.userAgent)) return 'edge';
  return 'chrome';
})();

let port = null;
let status = 'connecting'; // connecting | ok | not_running | not_installed
let lastDevices = [];
let nextReqId = 1;
const pending = new Map();
let pushTimer = null;
let reconnectDelay = 1000;
let reconnectTimer = null;

function connect() {
  if (port) return port;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    status = 'not_installed';
    port = null;
    return null;
  }
  status = 'connecting';
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError && chrome.runtime.lastError.message;
    if (err && /not found|not installed|forbidden/i.test(err)) status = 'not_installed';
    else if (status !== 'not_running') status = 'not_running';
    port = null;
    for (const [, p] of pending) p.reject(new Error(status));
    pending.clear();
    scheduleReconnect();
  });
  port.postMessage({ type: 'hello', browser: BROWSER, version: chrome.runtime.getManifest().version });
  schedulePush();
  return port;
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, 60000);
}

function onHostMessage(msg) {
  if (!msg || typeof msg !== 'object') return;
  if (msg.type === 'error' && msg.error === 'nitrodrop_not_running') {
    status = 'not_running';
    return;
  }
  if (msg.type === 'hello') {
    status = 'ok';
    reconnectDelay = 1000;
  }
  if (msg.type === 'devices' && Array.isArray(msg.devices)) lastDevices = msg.devices;
  if (msg.reqId !== undefined && pending.has(msg.reqId)) {
    const p = pending.get(msg.reqId);
    pending.delete(msg.reqId);
    p.resolve(msg);
  }
}

function request(message) {
  const p = connect();
  if (!p) return Promise.reject(new Error(status));
  const reqId = nextReqId++;
  return new Promise((resolve, reject) => {
    pending.set(reqId, { resolve, reject });
    setTimeout(() => {
      if (pending.delete(reqId)) reject(new Error('timeout'));
    }, REQUEST_TIMEOUT_MS);
    try {
      p.postMessage({ ...message, reqId });
    } catch (e) {
      pending.delete(reqId);
      reject(e);
    }
  });
}

// ---------------- tab collection ----------------

function isShareable(url) {
  return typeof url === 'string' && /^https?:\/\//i.test(url);
}

async function collectTabs() {
  const windows = await chrome.windows.getAll({ populate: true, windowTypes: ['normal'] });
  const tabs = [];
  for (const w of windows) {
    if (w.incognito) continue; // private windows are never collected
    for (const t of w.tabs || []) {
      if (t.incognito || !isShareable(t.url)) continue;
      tabs.push({
        browser: BROWSER,
        windowId: t.windowId,
        tabId: t.id,
        title: (t.title || '').slice(0, 300),
        url: t.url,
        faviconUrl: isShareable(t.favIconUrl) ? t.favIconUrl : undefined,
        active: !!(t.active && w.focused),
        lastAccessed: Math.round(t.lastAccessed || 0),
      });
      if (tabs.length >= 500) return tabs;
    }
  }
  return tabs;
}

function schedulePush() {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(pushTabs, DEBOUNCE_MS);
}

async function pushTabs() {
  const p = connect();
  if (!p) return;
  try {
    p.postMessage({ type: 'tabs', browser: BROWSER, tabs: await collectTabs() });
  } catch (e) {
    /* port closed; reconnect handles it */
  }
}

chrome.tabs.onCreated.addListener(schedulePush);
chrome.tabs.onRemoved.addListener(schedulePush);
chrome.tabs.onActivated.addListener(schedulePush);
chrome.tabs.onReplaced.addListener(schedulePush);
chrome.tabs.onMoved.addListener(schedulePush);
chrome.tabs.onAttached.addListener(schedulePush);
chrome.tabs.onUpdated.addListener((_id, info) => {
  if (info.url || info.title || info.favIconUrl || info.status === 'complete') schedulePush();
});
chrome.windows.onRemoved.addListener(schedulePush);
chrome.windows.onFocusChanged.addListener(schedulePush);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// ---------------- popup bridge ----------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id || !msg || typeof msg.cmd !== 'string') return false;
  (async () => {
    switch (msg.cmd) {
      case 'status':
        connect();
        return { status, devices: lastDevices, browser: BROWSER };
      case 'getDevices': {
        const r = await request({ type: 'getDevices' });
        lastDevices = r.devices || [];
        return { status, devices: lastDevices };
      }
      case 'getTargets': {
        const r = await request({ type: 'getTargets' });
        return { targets: r.targets || [] };
      }
      case 'sendLink': {
        if (!isShareable(msg.url)) return { ok: false, error: chrome.i18n.getMessage('cannotSend') };
        const r = await request({ type: 'sendLink', targetId: msg.targetId, url: msg.url, title: msg.title });
        return { ok: !!r.ok, error: r.error };
      }
      case 'continue': {
        const r = await request({ type: 'continue', deviceId: msg.deviceId });
        return { ok: !!r.ok };
      }
      default:
        return { error: 'unknown' };
    }
  })()
    .then(sendResponse)
    .catch((e) => sendResponse({ error: String((e && e.message) || e), status }));
  return true; // async response
});

connect();
