// Real browsers, synthetic authentication/data only. No production API mutations.
const { chromium, firefox, webkit } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const os = require('node:os');
const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const phase = process.env.PHASE || 'after';
const out = path.join(__dirname, 'artifacts', phase);
fs.mkdirSync(out, { recursive: true });
const long = 'شركة المنتجات الإسمنتية والمقاولات والتطوير العقاري العربية الدولية ';
const unbroken = 'EG-BLOCK-Quotation-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'.repeat(3);
const recipients = [true, false].map((isActive, i) => ({ id: i + 1, displayName: long.repeat(2), chatId: `-100123456789${i}`, isActive, totalSent: 12345, totalFailed: 23, createdAt: '2026-10-21T08:00:00Z' }));
const quotes = [0, 1].map(i => ({ id: i + 1, quoteNumber: unbroken, clientName: long.repeat(2), createdByFullName: long, contactPerson: long, projectName: unbroken, location: long, issuerName: long, issuerJobTitle: long, itemCount: 100, totalAmount: 987654321.25, createdAt: '2026-10-21T08:00:00Z', pdfUrl: '/synthetic.pdf', docxUrl: '/synthetic.docx', telegramDeliveries: i ? [] : [{ displayName: long, chatId: recipients[0].chatId, status: 'Sent' }, { displayName: long, chatId: recipients[1].chatId, status: 'Failed', errorMessage: long }] }));
const logs = [{ id: 1, timestamp: '2026-10-21T08:00:00Z', user: { fullName: long, role: 'Admin' }, action: 'QuoteDownloaded', entityId: unbroken + long.repeat(3) }];
const results = [];
const server = http.createServer((req, res) => {
  const file = path.resolve(root, '.' + decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(root + path.sep)) { res.writeHead(403).end(); return; }
  if (phase === 'before' && process.env.BASELINE_REF && file.endsWith('admin.html')) {
    const data = execFileSync('git', ['show', `${process.env.BASELINE_REF}:${path.relative(root, file).replaceAll('\\', '/')}`], { cwd: root });
    res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(data); return;
  }
  fs.readFile(file, (err, data) => { if (err) res.writeHead(404).end(); else { res.setHeader('Content-Type', file.endsWith('.html') ? 'text/html; charset=utf-8' : 'text/javascript; charset=utf-8'); res.end(data); } });
});
async function fixture(browser, url, viewport) {
  const context = await browser.newContext({ viewport });
  const calls = [];
  let mode = 'data';
  await context.addInitScript(() => { localStorage.setItem('fq_token_admin', 'synthetic-admin'); localStorage.setItem('fq_token_owner', 'synthetic-owner'); });
  await context.route('**/api/**', async route => {
    const req = route.request(), u = new URL(req.url());
    calls.push({ path: u.pathname + u.search, method: req.method(), body: req.postData(), auth: req.headers().authorization });
    if (req.method() === 'OPTIONS') return route.fulfill({ status: 204, headers: {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Headers': 'authorization, content-type',
      'Access-Control-Allow-Methods': 'GET, POST, PUT, PATCH, DELETE, OPTIONS'
    } });
    let data = { success: true, message: 'تم بنجاح', results: [{ chatId: recipients[1].chatId, success: true }] };
    if (u.pathname.endsWith('/discover-updates')) data = { success: true, count: 1, recentChats: [{ chatId: '-100987654321', firstName: long }] };
    else if (u.pathname.endsWith('/verify')) data = { valid: true };
    else if (req.method() === 'GET') {
      if (mode === 'error') return route.fulfill({ status: 503, json: { message: 'تعذر الاتصال' }, headers: { 'Access-Control-Allow-Origin': '*' } });
      if (u.pathname.endsWith('/stats')) data = { totalQuotes: 123456, totalRevenue: 987654321012, todayQuotes: 1234, totalClients: 56789 };
      else if (u.pathname.endsWith('/quotes')) data = mode === 'empty' ? [] : mode === 'normal' ? quotes.map((q, i) => ({ ...q, quoteNumber: `EG-2026-00${i + 1}`, clientName: 'شركة النور للمقاولات', contactPerson: 'أحمد علي', createdByFullName: 'عبدالله سيد', projectName: 'مصنع العاشر من رمضان', location: 'المنطقة الصناعية', issuerName: 'محمد حسن', issuerJobTitle: 'مدير المبيعات', telegramDeliveries: [] })) : quotes;
      else if (u.pathname.endsWith('/logs')) data = mode === 'empty' ? [] : logs;
      else if (u.pathname.endsWith('/recipients')) data = mode === 'empty' ? [] : recipients;
      else data = { isConfigured: true, botUsername: 'EG_BLOCK_TestBot', recentChats: [{ chatId: '-100987654321', firstName: long }] };
    }
    await route.fulfill({ json: data, headers: { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': 'authorization, content-type' } });
  });
  await context.route('**/synthetic.*', route => route.fulfill({ body: 'Synthetic document only', contentType: 'application/octet-stream' }));
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(url);
  await page.waitForSelector('#quotesTableBody tr td:nth-child(9)');
  await page.waitForFunction(() => typeof Swal !== 'undefined' && getComputedStyle(document.body).fontFamily.includes('Cairo') && getComputedStyle(document.querySelector('header')).backgroundColor !== 'rgba(0, 0, 0, 0)');
  await page.evaluate(() => document.fonts.ready);
  return { context, page, calls, errors, setMode: value => { mode = value; } };
}
async function check(page, name) {
  if (await page.locator('.swal2-popup').count()) await page.locator('.swal2-popup').evaluate(el => Promise.all(el.getAnimations().map(animation => animation.finished)));
  const metrics = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth, body: document.body.scrollWidth }));
  results.push({ name, ...metrics, pass: metrics.scroll <= metrics.width && metrics.body <= metrics.width });
  if (phase !== 'before') assert(results.at(-1).pass, `${name}: overflow ${JSON.stringify(metrics)}`);
  if (phase !== 'before') {
    const failures = await page.evaluate(() => {
      const visible = el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden';
      const issues = [];
      for (const el of document.querySelectorAll('button, .admin-header a, .admin-table a')) {
        if (visible(el) && el.getBoundingClientRect().height < 43) issues.push('small touch target: ' + el.textContent.trim());
      }
      for (const el of document.querySelectorAll('.admin-table td, .admin-stats [id], .admin-brand h1')) {
        if (visible(el) && el.scrollWidth > el.clientWidth + 1) issues.push('clipped text: ' + el.textContent.slice(0, 40));
      }
      for (const group of document.querySelectorAll('.admin-header-actions, .admin-nav > div, .admin-table td:last-child > div')) {
        const rects = [...group.querySelectorAll('button, a')].filter(visible).map(el => el.getBoundingClientRect());
        for (let i = 0; i < rects.length; i++) for (let j = i + 1; j < rects.length; j++) {
          const a = rects[i], b = rects[j];
          if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 && Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1) issues.push('overlapping controls');
        }
      }
      return issues;
    });
    assert.deepEqual(failures, [], `${name}: usability`);
  }
}
async function snapshot(page, name) {
  const dialog = await page.locator('.swal2-popup').count() || await page.locator('#adminAuthModal').isVisible();
  await page.screenshot({ path: path.join(out, name + '.png'), fullPage: !dialog });
}
async function closeResult(page, title) {
  // Fetch completion and popup replacement can exceed a fixed sleep in WebKit.
  // Wait for the actual result, then for SweetAlert to restore dashboard accessibility.
  try {
    await page.waitForFunction(expected => Swal.getTitle()?.textContent === expected && !Swal.isLoading(), title);
  } catch (error) {
    console.error('Expected result:', title, 'Actual popup:', await page.locator('.swal2-popup').allTextContents());
    await snapshot(page, 'failed-result-dialog');
    throw error;
  }
  await page.evaluate(() => Swal.close());
  await page.waitForSelector('.swal2-popup', { state: 'detached' });
}
async function run() {
  assert.equal(fs.readFileSync(path.join(root, 'frontend/admin.html'), 'utf8'), fs.readFileSync(path.join(root, 'backend/wwwroot/admin.html'), 'utf8'), 'admin frontends must remain identical');
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = process.env.BASE_URL || `http://127.0.0.1:${server.address().port}/frontend/admin.html`;
  const engines = phase === 'before' ? { chromium } : Object.fromEntries(Object.entries({ chromium, firefox, webkit }).filter(([name]) => !process.env.ENGINES || process.env.ENGINES.split(',').includes(name)));
  for (const [engine, type] of Object.entries(engines)) {
    if (process.env.ZOOM_ONLY) break;
    const browser = await type.launch(engine === 'chromium' && process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
    try {
      for (const width of process.env.FUNCTIONAL_ONLY ? [] : [320, 360, 375, 390, 430, 768, 1024, 1280, 1440, 1920]) {
        const f = await fixture(browser, base, { width, height: 900 });
        const { page, context } = f;
        await check(page, `${engine}-${width}-quotes`);
        if (phase !== 'before') {
          const columns = await page.locator('.admin-stats').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
          assert.equal(columns, width >= 1024 ? 4 : width >= 768 ? 2 : 1);
        }
        if (engine === 'chromium') await snapshot(page, `${width}-quotes`);
        await page.locator('#tabTelegramBtn').click();
        await page.waitForSelector('#telegramRecipientsTableBody tr td:nth-child(7)');
        await check(page, `${engine}-${width}-telegram`);
        if (engine === 'chromium') await snapshot(page, `${width}-telegram`);
        if (phase !== 'before') {
          await page.getByRole('button', { name: 'إضافة مستلم جديد', exact: true }).click();
          await page.waitForSelector('.swal2-popup');
          await page.locator('.swal2-popup').evaluate(el => Promise.all(el.getAnimations().map(animation => animation.finished)));
          await check(page, `${engine}-${width}-modal`);
          const popup = await page.locator('.swal2-popup').boundingBox();
          assert(popup.x >= 0 && popup.x + popup.width <= width + 1);
          await page.locator('.swal2-cancel').click();
          assert.equal(f.errors.length, 0, f.errors.join('\n'));
        }
        await context.close();
      }
      if (phase !== 'before') await functional(browser, base, engine);
    } finally { await browser.close(); }
  }
  if (phase !== 'before' && engines.chromium) await nativeZoom(base);
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify(results, null, 2));
  console.log(`${phase}: ${results.length} browser checks; ${results.filter(r => !r.pass).length} overflow failures`);
}
async function functional(browser, base, engine) {
  const f = await fixture(browser, base, { width: 390, height: 844 });
  const { page, calls } = f;
  await page.locator('#quotesTableBody a').first().click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.endsWith('/audit') && c.method === 'POST' && c.body.includes('PDF')));
  await page.locator('#quotesTableBody a').nth(1).click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.body?.includes('DOCX')));
  await page.locator('#quotesTableBody button').first().click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.includes('retry-telegram?chatId=' + encodeURIComponent(recipients[1].chatId))));
  await closeResult(page, 'تم الإرسال بنجاح!');
  await page.locator('#quotesTableBody tr').nth(1).getByRole('button').click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.endsWith('/quotes/2/retry-telegram') && c.method === 'POST'));
  await closeResult(page, 'تم الإرسال بنجاح!');
  await page.locator('#tabTelegramBtn').click();
  await page.waitForSelector('#telegramRecipientsTableBody button');
  await page.getByRole('button', { name: 'إضافة مستلم جديد', exact: true }).click();
  await page.locator('#swalChatId').fill('-100777777');
  await page.locator('#swalDisplayName').fill(long);
  await page.locator('.swal2-confirm').click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.method === 'POST' && c.path.endsWith('/recipients') && c.body.includes('-100777777')));
  await closeResult(page, 'تم بنجاح!');
  await page.locator('#telegramRecipientsTableBody tr').nth(1).getByRole('button', { name: 'تعديل الاسم' }).click();
  await page.locator('#swalEditDisplayName').fill('اسم جديد آمن');
  await page.locator('.swal2-confirm').click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.endsWith('/recipients/2') && c.method === 'PUT'));
  await closeResult(page, 'تم بنجاح!');
  for (const [row, name, id] of [[0, 'تعطيل', 1], [1, 'تفعيل', 2]]) {
    await page.locator('#telegramRecipientsTableBody tr').nth(row).getByRole('button', { name, exact: true }).click();
    await page.locator('.swal2-confirm').click();
    await page.waitForTimeout(100);
    assert(calls.some(c => c.path.endsWith(`/recipients/${id}/toggle-active`) && c.method === 'POST'));
    await closeResult(page, 'تم بنجاح!');
  }
  await page.locator('#btnDiscoverChats').click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.endsWith('/discover-updates') && c.auth === 'Bearer synthetic-owner'));
  await closeResult(page, 'تم رصد المحادثات');
  await page.locator('#telegramRecipientsTableBody tr').nth(1).getByRole('button', { name: 'حذف', exact: true }).click();
  await page.locator('.swal2-confirm').click();
  await page.waitForTimeout(100);
  assert(calls.some(c => c.path.endsWith('/recipients/2') && c.method === 'DELETE'));
  await closeResult(page, 'تم بنجاح!');
  // Landscape and reduced visual viewport simulate the space available with a keyboard.
  for (const viewport of [{ width: 844, height: 390 }, { width: 320, height: 256 }, { width: 640, height: 450 }]) {
    await page.setViewportSize(viewport);
    await page.getByRole('button', { name: 'إضافة مستلم جديد', exact: true }).click();
    await page.locator('#swalDisplayName').focus();
    await check(page, `${engine}-${viewport.width}x${viewport.height}-keyboard-reflow`);
    const confirm = await page.locator('.swal2-confirm').boundingBox();
    assert(confirm.y >= 0 && confirm.y + confirm.height <= viewport.height + 1, 'confirmation outside viewport');
    await snapshot(page, `${engine}-${viewport.width}x${viewport.height}-dialog`);
    await page.locator('.swal2-cancel').click();
  }
  await page.setViewportSize({ width: 320, height: 256 });
  await page.locator('#telegramRecipientsTableBody tr').nth(1).getByRole('button', { name: 'حذف', exact: true }).click();
  await check(page, `${engine}-long-confirmation-short-viewport`);
  const longConfirm = await page.locator('.swal2-confirm').boundingBox();
  await snapshot(page, `${engine}-long-confirmation-short-viewport`);
  assert(longConfirm.y >= 0 && longConfirm.y + longConfirm.height <= 256, `long confirmation outside viewport: ${JSON.stringify(longConfirm)}`);
  await page.locator('.swal2-cancel').click();
  for (const mode of ['empty', 'error']) {
    f.setMode(mode);
    await page.evaluate(async () => { await loadQuotes(); await loadLogs(); await loadTelegramRecipients(); });
    await check(page, `${engine}-${mode}`);
    assert(await page.locator('#quotesTableBody').innerText());
    if (mode === 'error') assert((await page.locator('#telegramRecipientsTableBody').innerText()).includes('تعذر تحميل'));
    if (engine === 'chromium') await snapshot(page, mode + '-telegram');
  }
  f.setMode('normal');
  for (const width of [390, 768, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await page.locator('#tabQuotesBtn').click();
    await page.waitForSelector('#quotesTableBody tr td:nth-child(9)');
    await check(page, `${engine}-${width}-normal`);
    if (engine === 'chromium') await snapshot(page, `${width}-normal-quotes`);
  }
  await page.setViewportSize({ width: 320, height: 450 });
  await page.evaluate(() => { Auth.clearToken('admin'); showAdminModal(); });
  await page.locator('#adminPasswordInput').focus();
  await check(page, `${engine}-login-short-viewport`);
  await page.locator('#adminLoginBtn').scrollIntoViewIfNeeded();
  await snapshot(page, `${engine}-login`);
  await page.locator('#adminPasswordInput').fill('synthetic-password');
  await page.locator('#adminLoginBtn').click();
  await page.waitForSelector('#adminAuthModal.hidden', { state: 'attached' });
  assert(calls.some(c => c.path.endsWith('/auth/login') && c.method === 'POST'));
  assert.equal(await page.locator('#adminDashboardContent [onclick*="Employee"]').count(), 0);
  assert.equal(f.errors.length, 0, f.errors.join('\n'));
  results.push({ name: `${engine}-functional`, pass: true });
  await f.context.close();
}
async function nativeZoom(base) {
  const extension = path.join(__dirname, 'zoom-extension');
  const context = await chromium.launchPersistentContext(fs.mkdtempSync(path.join(os.tmpdir(), 'admin-zoom-')), {
    channel: 'chromium', headless: true,
    ...(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {}),
    args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
    viewport: { width: 1280, height: 900 }
  });
  try {
    const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
    const f = await fixture({ newContext: async () => context }, base, { width: 1280, height: 900 });
    await worker.evaluate(async () => {
      const tabs = await chrome.tabs.query({});
      const tab = tabs.find(tab => /\/admin(?:\.html)?$/.test(new URL(tab.url).pathname));
      await chrome.tabs.setZoom(tab.id, 2);
    });
    await f.page.waitForFunction(() => innerWidth === 640 && devicePixelRatio === 2);
    const cdp = await context.newCDPSession(f.page);
    const capture = async name => {
      const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      fs.writeFileSync(path.join(out, name + '.png'), Buffer.from(data, 'base64'));
    };
    await check(f.page, 'chromium-native-200-percent-quotes');
    await capture('chromium-native-200-percent-quotes');
    await f.page.locator('#tabTelegramBtn').click();
    await f.page.waitForSelector('#telegramRecipientsTableBody button');
    await check(f.page, 'chromium-native-200-percent-telegram');
    await f.page.getByRole('button', { name: 'إضافة مستلم جديد', exact: true }).click();
    await check(f.page, 'chromium-native-200-percent-dialog');
    const popup = await f.page.locator('.swal2-popup').boundingBox();
    const button = await f.page.locator('.swal2-confirm').boundingBox();
    assert(popup.x >= 0 && popup.x + popup.width <= 640 && button.y + button.height <= 450, 'native zoom dialog bounds');
    await capture('chromium-native-200-percent-dialog');
  } finally { await context.close(); }
}
run().catch(error => { console.error(error); fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ error: error.message, results }, null, 2)); process.exitCode = 1; }).finally(() => server.close());
