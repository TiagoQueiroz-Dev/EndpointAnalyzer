const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

/** Página com um endpoint aberto; fetch registra as chamadas e responde com handler(url, options). */
function harness(handler) {
  const elements = new Map();
  const calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      id, value: '', textContent: '', innerHTML: '', className: '', disabled: false, hidden: false, open: false, checked: false,
      addEventListener() {}, focus() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const box = (dataset) => ({ dataset, checked: true, disabled: false, addEventListener() {}, closest: () => ({ classList: { toggle() {} } }) });
  const boxes = { '[data-section]': [box({ section: 'summary' })], '[data-ai]': [box({ ai: 'summary' })] };
  const context = vm.createContext({
    document: { getElementById: element, querySelectorAll: (selector) => boxes[selector] ?? [], querySelector: () => null, addEventListener() {} },
    window: { addEventListener() {}, open: () => ({ location: {}, close() {} }) },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {} },
    fetch: async (url, options) => {
      calls.push({ url, method: options?.method ?? 'GET', body: options?.body ? JSON.parse(options.body) : undefined });
      const { status = 200, body } = await handler(url, options);
      return { ok: status < 400, status, statusText: '', headers: { get: () => 'application/json' }, json: async () => body };
    },
    URL, AbortController, console, setTimeout, clearTimeout, alert() {}, confirm: () => true,
  });
  vm.runInContext(script, context);
  vm.runInContext(`selected = { httpMethod: 'POST', route: '/api/test', project: 'Api' }; solutionPath = 'Api.sln'; render = () => {}; progressPollMs = 0;`, context);
  return { element, calls, run: code => vm.runInContext(code, context) };
}

const usage = (used) => ({ primary: { usedPercent: used, windowMinutes: 300, resetsAt: '2026-10-13T18:00:00Z' }, limitReached: used >= 100 });
const account = (id, display, state, extra = {}) => ({ id, display, email: display, state, loggedIn: state !== 'desconectado',
  usesSubscription: state !== 'desconectado', authMethod: 'chatgpt', plan: 'plus', ...extra });
const status = (accounts, extra = {}) => ({ cliInstalled: true, loggedIn: accounts.some(a => a.loggedIn), authMethod: 'chatgpt', planType: 'plus',
  continueWithOtherProvider: true, accounts, ...extra });
const two = status([
  account('principal', 'eu@chatgpt.com', 'sem-uso', { usage: usage(100), until: '2026-10-13T18:00:00Z' }),
  account('k2', 'time@chatgpt.com', 'em-uso', { usage: usage(15) }),
]);

test('rodape do Codex lista as contas na ordem com o uso de cada uma e a engrenagem', () => {
  const h = harness(() => ({ body: { models: [], model: '', effort: '' } }));
  h.run(`renderCodex(${JSON.stringify(two)})`);
  const footer = h.element('codexFooter').innerHTML;

  assert.match(footer, /eu@chatgpt\.com · Plus[\s\S]*time@chatgpt\.com · Plus/, 'na ordem de uso');
  assert.match(footer, /codexState-principal">sem uso até/);
  assert.match(footer, /codexState-k2">em uso/);
  assert.match(footer, /id="codexUsage-principal"><div class="usage codex"[\s\S]*100%[\s\S]*Limite de uso atingido nesta conta/);
  assert.match(footer, /id="codexUsage-k2"><div class="usage codex"[\s\S]*15%/);
  assert.match(footer, /data-account-action="check" data-provider="codex"\s+data-account="principal"/);
  assert.match(footer, /\+ Adicionar conta Codex/);
  assert.match(footer, /class="footer-head">[\s\S]*id="codexSettings"[\s\S]*id="codexContinueOther" data-continue-other checked/);
});

test('origem da IA mostra as contas do Codex e so avisa o limite quando todas esgotaram', () => {
  const h = harness(() => ({ body: {} }));
  h.run(`authState = { cliInstalled: true, loggedIn: false, accounts: [] }; store.set("ea.provider", "codex");
    codexState = ${JSON.stringify(two)}; updateAiControls(false);`);
  assert.equal(h.element('aiSource').textContent, 'IA: Codex · plano Plus · 2 contas');

  const exhausted = status(two.accounts.map(a => ({ ...a, state: 'sem-uso', usage: usage(100) })));
  h.run(`codexState = ${JSON.stringify(exhausted)}; updateAiControls(false);`);
  assert.match(h.element('aiSource').textContent, /2 contas · limite de uso atingido/);
});

test('adicionar conta do Codex abre o login dela e acompanha ate conectar, sem afetar as outras', async () => {
  const added = status([...two.accounts, account('n3', 'Conta 3', 'desconectado')]);
  const connected = status([...two.accounts, account('n3', 'novo@chatgpt.com', 'disponivel', { usage: usage(0) })]);
  const h = harness((url) => url === '/api/auth/codex/accounts' ? { body: { id: 'n3', status: added } }
    : url === '/api/auth/codex/login' ? { body: { loginUrl: 'https://auth.openai.com/oauth/x' } }
    : url.startsWith('/api/auth/codex/status') ? { body: connected } : { body: { models: [], model: '', effort: '' } });
  h.run(`renderCodex(${JSON.stringify(two)})`);

  await h.run('accountAction("codex", "add")');
  assert.deepEqual(h.calls.find(c => c.url === '/api/auth/codex/login').body, { account: 'n3' });
  // O acompanhamento olha a conta do login: as outras já estavam conectadas e não fecham o diálogo.
  await new Promise(r => setTimeout(r, 0));
  await new Promise(r => setTimeout(r, 0));
  assert.equal(h.element('codexDialog').open, false);
  assert.match(h.element('codexFooter').innerHTML, /novo@chatgpt\.com/);
});

test('reordenar, sair e remover usam as rotas do Codex; a opcao vale para os dois provedores', async () => {
  const h = harness((url) => url === '/api/auth/settings' ? { body: { continueWithOtherProvider: false } } : { body: two });
  h.run(`authState = { cliInstalled: true, continueWithOtherProvider: true, accounts: [] }; renderCodex(${JSON.stringify(two)})`);

  await h.run('accountAction("codex", "up", "k2")');
  await h.run('accountAction("codex", "logout", "principal")');
  await h.run('accountAction("codex", "remove", "k2")');
  assert.deepEqual(h.calls.find(c => c.url === '/api/auth/codex/accounts/order').body, { ids: ['k2', 'principal'] });
  assert.ok(h.calls.some(c => c.url === '/api/auth/codex/logout?account=principal' && c.method === 'POST'));
  assert.ok(h.calls.some(c => c.url === '/api/auth/codex/accounts/k2' && c.method === 'DELETE'));
  assert.ok(!h.calls.some(c => c.url.startsWith('/api/auth/accounts')), 'as contas do Claude não são tocadas');

  h.element('claudeContinueOther').checked = true;
  h.element('codexContinueOther').checked = true;
  await h.run('saveContinueOther(false)');
  assert.deepEqual(h.calls.find(c => c.url === '/api/auth/settings').body, { continueWithOtherProvider: false });
  assert.equal(h.element('claudeContinueOther').checked, false);
  assert.equal(h.element('codexContinueOther').checked, false);
  assert.equal(h.run('authState.continueWithOtherProvider || codexState.continueWithOtherProvider'), false);
});
