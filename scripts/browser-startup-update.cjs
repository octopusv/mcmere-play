const { chromium } = require('../ui/node_modules/playwright');
const assert = require('node:assert/strict');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const dist = path.resolve(__dirname, '../ui/dist');
  const output = path.resolve(__dirname, '../.test-data/startup-update-browser');
  fs.mkdirSync(output, { recursive: true });
  const host = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const file = path.resolve(dist, '.' + (url.pathname === '/' ? '/index.html' : url.pathname));
    if (!file.startsWith(dist + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png' })[path.extname(file)] || 'application/octet-stream');
    fs.createReadStream(file).pipe(res);
  });
  await new Promise(resolve => host.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 1080, height: 850 } });
    const state = {
      version: '0.1.0', settings: { theme: 'light', servers: [], selectedServer: null }, servers: [],
      busy: false, canCancel: false, dataRoot: 'C:\\Example\\play',
      activity: { gameRunning: false, prismRunning: false, uncertain: false },
      update: { stage: 'checking', version: null, notes: null, received: 0, length: 120000000, queued: false, error: null },
    };
    const calls = [], errors = [];
    let completeDownload;
    page.on('pageerror', error => errors.push(error.message));
    const emit = () => page.evaluate(value => window.__emit({ type: 'state', value }), state);
    await page.exposeBinding('__bridge', async (_source, request) => {
      calls.push(request.op);
      if (request.op === 'update-now') {
        state.update.stage = 'downloading'; state.update.received = 60000000;
        await emit();
        await new Promise(resolve => { completeDownload = resolve; });
        state.update.stage = 'ready'; state.update.queued = true;
        await emit();
      }
      if (request.op === 'queue-update') state.update.queued = request.body.queued;
      return { id: request.id, ok: true, value: request.op === 'state' ? JSON.parse(JSON.stringify(state)) : null };
    });
    await page.addInitScript(() => {
      const listeners = [];
      window.__emit = data => listeners.forEach(listener => listener({ data }));
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        addEventListener: (_event, listener) => listeners.push(listener),
        postMessage: request => window.__bridge(request).then(window.__emit),
      };
    });
    await page.goto(`http://127.0.0.1:${host.address().port}`);
    await page.getByRole('heading', { name: '最初のサーバーを追加' }).waitFor();
    assert.equal(await page.locator('[data-role="app-update-notice"]').count(), 0);
    assert.equal(await page.getByRole('button', { name: 'サーバーを追加', exact: true }).first().isEnabled(), true);
    Object.assign(state.update, { stage: 'available', version: '0.2.0', notes: '新しい機能と修正' });
    await emit();
    await page.getByRole('button', { name: '更新する', exact: true }).waitFor();
    assert.equal(calls.includes('update-now'), false);
    assert.equal(calls.includes('download-update'), false);
    for (const width of [1080, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await page.screenshot({ path: path.join(output, `available-${width}.png`), fullPage: true });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    }
    await page.getByRole('button', { name: 'あとで', exact: true }).click();
    assert.equal(await page.locator('[data-role="app-update-notice"]').count(), 0);
    await page.getByRole('button', { name: 'Playの更新あり', exact: true }).click();
    await page.getByRole('button', { name: /^更新を準備/ }).waitFor();
    await page.reload();
    await page.getByRole('button', { name: '更新する', exact: true }).waitFor();
    state.activity.gameRunning = true;
    state.activity.prismRunning = true;
    await emit();
    await page.getByRole('button', { name: '更新する', exact: true }).click();
    await page.getByText(/ダウンロード中/).waitFor();
    assert.equal(await page.getByRole('button', { name: '更新する', exact: true }).count(), 0);
    assert.equal(await page.getByRole('button', { name: 'あとで', exact: true }).count(), 0);
    completeDownload();
    await page.getByText(/PrismとMinecraftが閉じ、処理が完了すると更新します/).waitFor();
    assert.equal(calls.filter(op => op === 'update-now').length, 1);
    await page.getByRole('button', { name: '更新の詳細', exact: true }).click();
    await page.getByRole('button', { name: '更新予約を取り消す', exact: true }).click();
    assert.equal(state.update.queued, false);
    Object.assign(state.update, { stage: 'idle', version: null, error: '更新を確認できません。' });
    await page.reload();
    await page.getByRole('heading', { name: '最初のサーバーを追加' }).waitFor();
    assert.equal(await page.locator('[data-role="app-update-notice"]').count(), 0);
    assert.equal(await page.getByRole('alert').count(), 0);
    Object.assign(state.update, { stage: 'current', error: null });
    await emit();
    assert.equal(await page.getByRole('button', { name: 'Playの更新あり' }).count(), 0);
    state.settings.theme = 'dark';
    Object.assign(state.update, { stage: 'available', version: '0.2.1', error: '更新を取得できません。再試行してください。' });
    await emit();
    await page.getByRole('alert').waitFor();
    await page.screenshot({ path: path.join(output, 'retry-dark.png'), fullPage: true });
    assert.equal(await page.getByRole('button', { name: '更新する', exact: true }).isEnabled(), true);
    assert.deepEqual(errors, []);
    console.log('Startup update notice verified: offer, defer, reopen, download, queued wait, retry, offline and narrow layouts.');
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => host.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
