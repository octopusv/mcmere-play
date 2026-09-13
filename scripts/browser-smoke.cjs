const { chromium } = require('../ui/node_modules/playwright');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const dist = path.resolve(__dirname, '../ui/dist');
  const out = path.resolve(__dirname, '../artifacts/qa/browser');
  fs.mkdirSync(out, { recursive: true });
  const server = http.createServer((req, res) => {
    const file = path.resolve(dist, '.' + (req.url.split('?')[0] === '/' ? '/index.html' : req.url.split('?')[0]));
    if (!file.startsWith(dist + path.sep) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png' })[path.extname(file)] || 'application/octet-stream');
    fs.createReadStream(file).pipe(res);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1080, height: 800 }, reducedMotion: 'reduce' });
    const errors = []; const calls = [];
    page.on('pageerror', error => errors.push(error.message));
    const file = (id, required) => ({ id, path: 'mods/' + id + '.jar', name: id === 'main' ? 'Cobblemon' : '推奨MOD', version: '1.0', length: 3000000, sha512: 'a'.repeat(128),
      source: { kind: 'modrinth', projectId: id }, requirement: required ? 'required' : 'recommended', defaultEnabled: true, modIds: [id], requires: [] });
    const state = { version: '0.1.0', settings: { theme: 'light', selectedServer: 'fixture', servers: [{ id: 'fixture', name: 'Example server', playerName: null, memoryMiB: 4096, optionalChoices: {}, gameObserved: true, target: { origin: 'https://packs.example', publicId: '2'.repeat(32) } }] },
      servers: [{ id: 'fixture', stage: 'idle', error: null, errorCode: null, manifest: null, plan: null, status: null, javaReady: false, prismReady: false, total: 0, received: 0 }],
      busy: false, canCancel: false, dataRoot: 'C:\\Example\\mcmere-play', migration: null, update: { stage: 'idle', version: null, notes: null, length: 0, received: 0, queued: false, error: null }, activity: { gameRunning: false, prismRunning: false, uncertain: false } };
    const clone = value => JSON.parse(JSON.stringify(value));
    let settingsCorrupt = false;
    await page.exposeBinding('__playBridge', async (_source, request) => {
      calls.push(request.op);
      const row = state.servers[0];
      if (request.op === 'state' && settingsCorrupt) return { id: request.id, ok: false, error: { code: 'settings_corrupt', message: 'アプリ設定を読み取れません。' } };
      if (request.op === 'recover-settings') settingsCorrupt = false;
      if (request.op === 'check-update') Object.assign(state.update, { stage: 'available', version: '0.1.1', notes: 'アプリ更新の検証用表示', length: 120000000 });
      if (request.op === 'download-update') Object.assign(state.update, { stage: 'ready', received: state.update.length });
      if (request.op === 'queue-update') state.update.queued = request.body.queued;
      if (request.op === 'choose-migration') return { id: request.id, ok: true, value: { id: 'migration-fixture', source: state.dataRoot, destination: 'D:\\Games\\mcmere-play-data', bytes: 10000000, requiredFreeBytes: 90000000, files: 100, prismLoginRequired: true } };
      if (request.op === 'migrate-data') { state.dataRoot = 'D:\\Games\\mcmere-play-data'; return { id: request.id, ok: true, value: { moved: true } }; }
      if (request.op === 'state') return { id: request.id, ok: true, value: clone(state) };
      if (request.op === 'identify') {
        if (request.body.name !== 'PlayerName') return { id: request.id, ok: false, error: { code: 'name_not_listed', message: 'ホワイトリストで名前を確認できません。' } };
        state.settings.servers[0].playerName = 'PlayerName';
        Object.assign(row, { stage: 'update', javaReady: true, prismReady: true, status: { gameState: 'online', compatibilityState: 'matched' },
          manifest: { releaseId: 'r', sequence: 1, serverName: 'Example server', displayVersion: '1', changelog: '検証用の構成', minecraftVersion: '1.21.1', loader: { version: '21.1.250' }, java: { major: 21 }, files: [file('main', true), file('optional', false)] },
          plan: { changes: [{ scope: 'game', path: 'mods/main.jar', action: 'add' }], unknownMods: [], requiredFreeBytes: 300000000, selectedIds: ['main', 'optional'] } });
      }
      if (request.op === 'prepare') { row.stage = 'ready'; row.plan.changes = []; }
      if (request.op === 'memory') state.settings.servers[0].memoryMiB = request.body.memoryMiB;
      if (request.op === 'theme') state.settings.theme = request.body.theme;
      if (request.op === 'optional') state.settings.servers[0].optionalChoices['project:' + request.body.fileId] = request.body.enabled;
      if (request.op === 'history') return { id: request.id, ok: true, value: [] };
      return { id: request.id, ok: true, value: null };
    });
    await page.addInitScript(() => {
      const listeners = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = { addEventListener: (_name, listener) => listeners.push(listener), postMessage: request => {
        window.__playBridge(request).then(data => listeners.forEach(listener => listener({ data })));
      } };
    });
    await page.goto('http://127.0.0.1:' + server.address().port);
    await page.getByRole('button', { name: '名前を入力', exact: true }).first().click();
    await page.getByRole('dialog').getByLabel('Minecraftの名前', { exact: true }).fill('NotListed');
    await page.getByRole('button', { name: '名前を確認する', exact: true }).click();
    await page.getByRole('dialog').getByRole('alert').waitFor();
    await page.getByRole('dialog').getByLabel('Minecraftの名前', { exact: true }).fill('PlayerName');
    await page.getByRole('button', { name: '名前を確認する', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: '準備して参加', exact: true }).click();
    await page.getByRole('heading', { name: '参加の準備ができました', exact: true }).waitFor();
    await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === '参加する' && !button.disabled));
    await page.screenshot({ path: path.join(out, 'ready-light.png'), fullPage: true });
    await page.getByRole('tab', { name: 'MOD', exact: true }).click();
    if (await page.getByLabel('Cobblemonを含める').isEnabled()) throw new Error('Required mod could be disabled.');
    await page.getByLabel('推奨MODを含める').click();
    await page.waitForFunction(() => !document.querySelector('[aria-label="推奨MODを含める"]').checked);
    await page.getByRole('tab', { name: '設定', exact: true }).click();
    await page.getByLabel('ゲームに割り当てる最大メモリ').selectOption('6144');
    await page.getByRole('button', { name: 'アプリ設定', exact: true }).click();
    await page.getByLabel('外観', { exact: true }).selectOption('dark');
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'dark');
    state.activity.prismRunning = true;
    await page.getByRole('button', { name: '更新を確認', exact: true }).click();
    await page.getByRole('button', { name: /^更新を準備/ }).click();
    await page.getByRole('button', { name: 'Prismとゲームの終了後に更新', exact: true }).click();
    await page.getByRole('button', { name: '更新予約を取り消す', exact: true }).waitFor();
    await page.screenshot({ path: path.join(out, 'app-update-waiting.png'), fullPage: true });
    await page.getByRole('button', { name: '更新予約を取り消す', exact: true }).click();
    await page.getByRole('button', { name: 'Prismとゲームの終了後に更新', exact: true }).waitFor();
    state.activity.prismRunning = false;
    await page.getByRole('button', { name: '保存先を変更', exact: true }).click();
    await page.getByRole('dialog', { name: 'データ保存先を変更', exact: true }).waitFor();
    if (!(await page.getByRole('dialog').innerText()).includes('Prismの全体設定と認証情報は移行しません')) throw new Error('Migration did not explain Prism sign-in.');
    if (await page.getByRole('dialog').evaluate(dialog => dialog.scrollWidth > dialog.clientWidth + 1)) throw new Error('Migration dialog has horizontal overflow.');
    await page.screenshot({ path: path.join(out, 'migration-confirm.png'), fullPage: true });
    await page.getByRole('button', { name: '移行して切り替える', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('[data-role="data-root"]')?.textContent === 'D:\\Games\\mcmere-play-data');
    await page.getByRole('button', { name: /Example server/ }).click();
    await page.getByRole('tab', { name: '概要', exact: true }).click();
    await page.setViewportSize({ width: 860, height: 760 });
    await page.screenshot({ path: path.join(out, 'ready-dark.png'), fullPage: true });
    if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Horizontal overflow.');
    settingsCorrupt = true;
    await page.reload();
    await page.getByRole('button', { name: '保存済みの設定を復元', exact: true }).waitFor();
    await page.screenshot({ path: path.join(out, 'settings-recovery.png'), fullPage: true });
    await page.getByRole('button', { name: '保存済みの設定を復元', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.play-app')?.dataset.ready === 'true');
    if (errors.length) throw new Error(errors.join('\n'));
    for (const op of ['identify', 'prepare', 'optional', 'memory', 'theme', 'recover-settings', 'check-update', 'download-update', 'queue-update', 'choose-migration', 'migrate-data']) if (!calls.includes(op)) throw new Error('Missing native operation: ' + op);
    fs.writeFileSync(path.join(out, 'result.json'), JSON.stringify({ passed: true, transport: 'synthetic native bridge', calls, errors, realGameConnectionTested: false }, null, 2));
    console.log('Play UI checks passed.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exit(1); });
