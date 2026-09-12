'use strict';

// Run with: node tests/Workbench.Tests/dashboard-regressions.cjs
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

const html = readFileSync(join(__dirname, '../../src/Workbench/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const decode = value => value.replace(/&(amp|lt|gt|quot|#39);/g, (_, entity) => ({
  amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'",
})[entity]);

function dashboard() {
  const elements = new Map(), intervals = [], requests = [];
  let stream, monotonic = 1000;
  class Element {
    constructor() {
      this.value = ''; this.dataset = {}; this.listeners = new Map(); this.attributes = new Map();
      this.classes = new Set(); this.textContent = ''; this._html = ''; this.params = [];
      this.classList = {
        add: name => this.classes.add(name), remove: name => this.classes.delete(name),
        contains: name => this.classes.has(name),
        toggle: (name, force = !this.classes.has(name)) => force ? this.classes.add(name) : this.classes.delete(name),
      };
      this.parentElement = { getBoundingClientRect: () => ({ width: 0, height: 0 }) };
    }
    addEventListener(name, fn) {
      if (!this.listeners.has(name)) this.listeners.set(name, []);
      this.listeners.get(name).push(fn);
    }
    dispatch(name, event = {}) {
      const handlers = [...(this.listeners.get(name) || [])];
      if (typeof this['on' + name] === 'function') handlers.push(this['on' + name]);
      return Promise.all(handlers.map(fn => fn(event)));
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    getAttribute(name) { return this.attributes.get(name); }
    getContext() { return {}; }
    get children() {
      const starts = [...this._html.matchAll(/<div class="(log-(?:row|detail|empty))[^\"]*"/g)];
      return starts.map((match, i) => ({
        start: match.index, end: starts[i + 1]?.index ?? this._html.length,
        classList: { contains: name => match[1] === name },
      }));
    }
    get firstElementChild() { return this.children[0]; }
    get lastChild() { return this.children.at(-1); }
    removeChild(child) { this._html = this._html.slice(0, child.start) + this._html.slice(child.end); }
    insertAdjacentHTML(position, value) {
      assert.equal(position, 'afterbegin');
      this._html = value + this._html;
    }
    focus() { this.focused = true; }
    querySelector(selector) { return selector === '[data-param]' ? this.params[0] || null : null; }
    get innerHTML() { return this._html; }
    set innerHTML(value) {
      this._html = value;
      if (this === elements.get('builder')) {
        for (const key of elements.keys()) if (key.startsWith('b-')) elements.delete(key);
        this.params = [];
        for (const match of value.matchAll(/<([a-z]+)\b([^>]*)>/g)) {
          const attrs = Object.fromEntries([...match[2].matchAll(/([\w-]+)="([^"]*)"/g)].map(m => [m[1], decode(m[2])]));
          const el = new Element();
          if (attrs.id) elements.set(attrs.id, el);
          if (attrs.id === 'b-verb') el.value = 'GET';
          if (attrs['data-param']) {
            for (const [key, val] of Object.entries(attrs)) {
              if (key.startsWith('data-')) el.dataset[key.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = val;
            }
            this.params.push(el);
          }
        }
      }
    }
  }
  for (const match of html.split('<script>')[0].matchAll(/\bid="([^"]+)"/g)) elements.set(match[1], new Element());
  const context = vm.createContext({
    document: {
      documentElement: new Element(), getElementById: id => elements.get(id) || null,
      querySelectorAll: selector => selector === '#b-url [data-param]' ? elements.get('builder').params : [],
    },
    window: { addEventListener() {}, devicePixelRatio: 1 },
    location: { origin: 'https://example.test', hash: '' },
    localStorage: { getItem() {}, setItem() {} },
    navigator: { clipboard: { async writeText() {} } },
    performance: { now: () => monotonic },
    // Deliberately skew the browser clock; clearing must follow the server clock instead.
    Date: class extends Date { static now() { return Date.parse('2099-01-01T00:00:00Z'); } },
    getComputedStyle: () => ({ getPropertyValue: () => '' }),
    requestAnimationFrame: fn => fn(), ResizeObserver: class { observe() {} },
    setInterval: fn => intervals.push(fn), setTimeout() {}, clearTimeout() {},
    EventSource: class extends Element {
      constructor(url) { super(); assert.equal(url, 'api/stream'); stream = this; }
    },
    fetch: async (url, options) => {
      requests.push({ url, options });
      return { ok: true, json: async () => [] };
    },
  });
  // Execute the complete production script. Only expose closure state for assertions;
  // the builders, renderers and event handlers themselves are never replaced.
  vm.runInContext(script.replace(/\}\)\(\);\s*$/, `
    globalThis.dashboardTest = {
      renderBuilder, currentRequest, buildCurl,
      state: () => ({ reqLog, pausedBuffer, paused, clearCutoff, MAX_LOG, RENDER_LIMIT })
    };
  })();`), context, { filename: 'dashboard-inline.js' });
  return {
    ...context.dashboardTest, element: id => elements.get(id),
    click: id => elements.get(id).dispatch('click'),
    emit: (name, data) => stream.dispatch(name, { data: JSON.stringify(data) }),
    disconnect: () => stream.onerror(),
    advance: ms => { monotonic += ms; }, tick: () => intervals.forEach(fn => fn()), requests,
    inputs: () => elements.get('builder').params,
  };
}

const base = Date.parse('2026-09-12T07:00:00Z');
const stamp = ms => new Date(base + ms).toISOString();
const entry = (id, ms) => ({ traceId: id, timestampUtc: stamp(ms), path: '/' + id, method: 'GET', statusCode: 200, durationMs: 1 });
const ids = entries => Array.from(entries, e => e.traceId);
const renderedIds = app => [...app.element('log-rows').innerHTML.matchAll(/data-trace="([^"]*)"/g)].map(m => m[1]);

function request(app, path, values) {
  const ep = { path, method: 'GET' };
  app.renderBuilder(ep);
  app.inputs().forEach((input, i) => { input.value = values[i] || ''; });
  return app.currentRequest(ep);
}

test('catch-all inputs generate single-star encoded and double-star segment-preserving URLs', () => {
  const app = dashboard();
  const value = 'folder one/ümlaut?#/%2F/$&/tail';
  assert.equal(request(app, '/files/{*path}', [value]).url, '/files/' + encodeURIComponent(value));
  assert.equal(app.inputs().length, 1);
  assert.equal(app.inputs()[0].dataset.param, 'path');
  assert.equal(app.inputs()[0].focused, true);
  assert.equal(request(app, '/files/{**path}', [value]).url, '/files/' + value.split('/').map(encodeURIComponent).join('/'));
  assert.equal(app.inputs()[0].dataset.catchAll, '2');
  assert.equal(request(app, '/files/{**path}', ['/a//b/']).url, '/files//a//b/');
  assert.equal(request(app, '/files/{*path}', ['']).url, '/files/');
  assert.equal(request(app, '/files/{**path}', ['  ']).url, '/files/');
  assert.equal(request(app, '/{**path}', ['']).url, '/');
});

test('constrained and optional parameters retain their existing behavior with catch-alls', () => {
  const app = dashboard();
  assert.equal(request(app, '/items/{id:int}/{name?}', ['42', 'a/b']).url, '/items/42/a%2Fb');
  assert.equal(request(app, '/items/{id:int}/{name?}', ['', '']).url, '/items/{id}/{name}');
  assert.equal(request(app, '/files/{id:int}/{**path:nonfile}', ['42', 'a b/c']).url, '/files/42/a%20b/c');
  const ep = { path: '/files/{**path}', method: 'ANY' };
  app.renderBuilder(ep);
  app.inputs()[0].value = 'a/b';
  app.element('b-verb').value = 'POST';
  app.element('b-query').value = '?page=2';
  app.element('b-headers').value = 'Accept: application/json';
  app.element('b-body').value = '{"ok":true}';
  const r = app.currentRequest(ep);
  assert.equal(r.url, '/files/a/b?page=2');
  assert.equal(r.verb, 'POST');
  assert.equal(r.body, '{"ok":true}');
  assert.equal(r.headers['Content-Type'], 'application/json');
  assert.match(app.buildCurl(ep), /https:\/\/example\.test\/files\/a\/b\?page=2/);
});

test('keyboard shortcut submits only the current endpoint after repeated builder changes', async () => {
  for (const modifier of ['ctrlKey', 'metaKey']) {
    const app = dashboard();
    app.renderBuilder({ path: '/previous', method: 'DELETE' });
    app.renderBuilder({ path: '/previous', method: 'DELETE' });
    app.renderBuilder({ path: '/current', method: 'POST' });
    app.element('b-body').value = '{"current":true}';
    let prevented = 0;
    await app.element('builder').dispatch('keydown', {
      [modifier]: true, key: 'Enter', preventDefault: () => prevented++,
    });
    const calls = app.requests.filter(r => r.options);
    assert.equal(prevented, 1);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/current');
    assert.equal(calls[0].options.method, 'POST');
    assert.equal(calls[0].options.body, '{"current":true}');
  }
});

test('keyboard shortcut ignores plain Enter and duplicate presses while a request is pending', async () => {
  const app = dashboard();
  app.renderBuilder({ path: '/current', method: 'POST' });
  await app.element('builder').dispatch('keydown', { key: 'Enter' });
  assert.equal(app.requests.filter(r => r.options).length, 0);
  const event = { ctrlKey: true, key: 'Enter', preventDefault() {} };
  await Promise.all([
    app.element('builder').dispatch('keydown', event),
    app.element('builder').dispatch('keydown', event),
  ]);
  assert.equal(app.requests.filter(r => r.options).length, 1);
});

test('reconnect snapshots merge with live rows, deduplicate the pair, and sort newest first', () => {
  const app = dashboard(), first = entry('first', 1000), missed = entry('missed', 2000), newest = entry('newest', 3000);
  app.emit('request', newest);
  app.emit('overview', { recentRequests: [first, newest], serverTimeUtc: stamp(3100) });
  app.disconnect();
  app.emit('overview', { recentRequests: [first, missed, newest, missed], serverTimeUtc: stamp(3200) });
  app.emit('request', missed);
  app.emit('request', entry('first', 2500));
  app.emit('request', entry('same-time', 2500));
  assert.deepEqual(ids(app.state().reqLog), ['newest', 'first', 'same-time', 'missed', 'first']);
  assert.deepEqual(renderedIds(app), ids(app.state().reqLog));
  assert.equal(app.element('rs-total').textContent, 5);
  const before = app.element('log-rows').innerHTML;
  app.emit('overview', { serverTimeUtc: stamp(3300) });
  assert.equal(app.element('log-rows').innerHTML, before);
  app.emit('overview', { recentRequests: [], serverTimeUtc: stamp(3400) });
  assert.equal(app.state().reqLog.length, 5);
});

test('snapshot sorting preserves server timestamp precision below a millisecond', () => {
  const app = dashboard();
  const entries = ['001', '0010001', '0011', '0010002'].map((fraction, i) => ({
    ...entry('row-' + i, 1), timestampUtc: '2026-09-12T07:00:00.' + fraction + 'Z',
  }));
  app.emit('overview', { recentRequests: entries, serverTimeUtc: stamp(1000) });
  assert.deepEqual(ids(app.state().reqLog), ['row-2', 'row-3', 'row-1', 'row-0']);
  assert.deepEqual(renderedIds(app), ids(app.state().reqLog));
});

test('periodic overview before the first history snapshot does not prevent subsequent seeding', () => {
  const app = dashboard();
  app.emit('overview', { serverTimeUtc: stamp(1000) });
  app.emit('overview', { recentRequests: [entry('history', 500)], serverTimeUtc: stamp(1100) });
  assert.deepEqual(ids(app.state().reqLog), ['history']);
});

test('initial and reconnect histories and live rows remain bounded and render only the newest rows', () => {
  const app = dashboard();
  const history = Array.from({ length: 700 }, (_, i) => entry('row-' + i, i));
  app.emit('overview', { recentRequests: history, serverTimeUtc: stamp(1000) });
  assert.equal(app.state().reqLog.length, 500);
  assert.equal(app.state().reqLog[499].traceId, 'row-200');
  app.emit('request', entry('latest', 1500));
  assert.deepEqual(renderedIds(app), ids(app.state().reqLog).slice(0, 300));
  assert.match(app.element('log-rows').innerHTML, /^<div class="log-row new"/);
  app.emit('overview', { recentRequests: history, serverTimeUtc: stamp(1600) });
  assert.equal(app.state().reqLog.length, 500);
  assert.equal(app.state().reqLog[0].traceId, 'latest');
  assert.equal(app.state().reqLog[499].traceId, 'row-201');
  assert.deepEqual(renderedIds(app), ids(app.state().reqLog).slice(0, 300));
});

test('paused reconnect snapshots and live events queue once, stay bounded and freeze rows and statistics', () => {
  const app = dashboard(), first = entry('visible', 1);
  app.emit('overview', { recentRequests: [first], serverTimeUtc: stamp(1000) });
  app.click('log-pause');
  const frozen = app.element('log-rows').innerHTML;
  const history = Array.from({ length: 650 }, (_, i) => entry('queued-' + i, i + 1000));
  app.disconnect();
  app.emit('overview', { recentRequests: [first, ...history], serverTimeUtc: stamp(2000) });
  app.emit('request', history[649]);
  app.emit('request', first);
  app.emit('request', entry('live', 2100));
  app.emit('overview', { recentRequests: history, serverTimeUtc: stamp(2200) });
  app.emit('overview', { serverTimeUtc: stamp(2300) });
  app.tick();
  assert.equal(app.state().pausedBuffer.length, 500);
  assert.deepEqual(ids(app.state().reqLog), ['visible']);
  assert.equal(app.element('log-rows').innerHTML, frozen);
  assert.equal(app.element('rs-total').textContent, 1);
  assert.match(app.element('log-hint').textContent, /500 queued/);
  app.click('log-pause');
  assert.equal(app.state().pausedBuffer.length, 0);
  assert.equal(app.state().reqLog.length, 500);
  assert.equal(app.state().reqLog[0].traceId, 'live');
  assert.equal(app.state().reqLog[499].traceId, 'queued-151');
  assert.deepEqual(renderedIds(app), ids(app.state().reqLog).slice(0, 300));
  assert.equal(app.element('rs-total').textContent, 500);
});

test('pausing before the initial snapshot queues history without seeding the visible log', () => {
  const app = dashboard();
  app.click('log-pause');
  const frozen = app.element('log-rows').innerHTML;
  app.emit('overview', { recentRequests: [entry('first', 1)], serverTimeUtc: stamp(1000) });
  assert.equal(app.state().reqLog.length, 0);
  assert.equal(app.state().pausedBuffer.length, 1);
  assert.equal(app.element('log-rows').innerHTML, frozen);
  app.click('log-pause');
  assert.deepEqual(ids(app.state().reqLog), ['first']);
});

test('clear uses elapsed server time, rejects reconnect history, and admits new requests despite browser clock skew', () => {
  const app = dashboard(), old = entry('old', 500);
  app.emit('overview', { recentRequests: [old], serverTimeUtc: stamp(1000) });
  app.disconnect();
  app.advance(1000);
  app.click('log-clear');
  app.advance(1000);
  const unseenBeforeClear = entry('unseen-old', 1500), afterClear = entry('new', 2500);
  app.emit('overview', { recentRequests: [old, unseenBeforeClear, afterClear], serverTimeUtc: stamp(3000) });
  app.emit('request', old);
  assert.deepEqual(ids(app.state().reqLog), ['new']);
  app.emit('overview', { serverTimeUtc: stamp(3000) });
  app.click('log-clear');
  app.emit('overview', { recentRequests: [old, afterClear, entry('newer', 3100)], serverTimeUtc: stamp(3200) });
  assert.deepEqual(ids(app.state().reqLog), ['newer']);
});

test('clear before the first connection resolves against the first server clock and keeps post-clear history', () => {
  const app = dashboard();
  app.disconnect();
  app.advance(1000);
  app.click('log-clear');
  app.advance(1000);
  app.emit('overview', { recentRequests: [entry('old', 1500), entry('new', 2500)], serverTimeUtc: stamp(3000) });
  assert.deepEqual(ids(app.state().reqLog), ['new']);
});

test('clear while paused empties both buffers and reconnect history cannot resurrect cleared rows on resume', () => {
  const app = dashboard(), old = entry('old', 500), queued = entry('queued', 1500);
  app.emit('overview', { recentRequests: [old], serverTimeUtc: stamp(1000) });
  app.click('log-pause');
  app.emit('request', queued);
  app.advance(1000);
  app.click('log-clear');
  assert.equal(app.state().reqLog.length, 0);
  assert.equal(app.state().pausedBuffer.length, 0);
  const frozen = app.element('log-rows').innerHTML;
  app.emit('overview', { recentRequests: [old, queued, entry('new', 2500)], serverTimeUtc: stamp(3000) });
  assert.equal(app.element('log-rows').innerHTML, frozen);
  assert.deepEqual(ids(app.state().pausedBuffer), ['new']);
  app.click('log-pause');
  assert.deepEqual(ids(app.state().reqLog), ['new']);
  assert.deepEqual(renderedIds(app), ['new']);
});
