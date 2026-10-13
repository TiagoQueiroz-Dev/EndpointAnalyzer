const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

/** Página com as abas e a IA marcadas; fetch registra as chamadas e responde com handler(url, options). */
function harness(handler) {
  const elements = new Map();
  const storage = new Map();
  const calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      id, value: '', textContent: '', innerHTML: '', className: '', disabled: false, open: false,
      addEventListener() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const box = (dataset) => ({ dataset, checked: true, disabled: false, addEventListener() {}, closest: () => ({ classList: { toggle() {} } }) });
  const sections = ['summary', 'business', 'complete', 'scenarios'].map(section => box({ section }));
  const ai = ['summary', 'business', 'scenarios'].map(id => box({ ai: id }));
  const context = vm.createContext({
    document: {
      getElementById: element,
      querySelectorAll: (selector) => selector === '[data-section]' ? sections : selector === '[data-ai]' ? ai : [],
      querySelector: () => null, addEventListener() {},
    },
    window: { addEventListener() {}, open: () => null },
    localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
    fetch: async (url, options) => {
      calls.push({ url, options });
      const { status = 200, body } = await handler(url, options);
      return { ok: status < 400, status, statusText: '', headers: { get: () => 'application/json' }, json: async () => body };
    },
    URL, AbortController, console, setTimeout, clearTimeout, alert() {}, confirm: () => true,
  });
  vm.runInContext(script, context);
  vm.runInContext(`selected = { httpMethod: 'POST', route: '/api/test', project: 'Api' }; solutionPath = 'Api.sln'; render = () => {};`, context);
  return { context, element, calls, run: code => vm.runInContext(code, context), storage };
}

const claude = { cliInstalled: true, loggedIn: true, usesSubscription: true, activeSource: 'subscription', mode: 'auto' };
const codex = { cliInstalled: true, loggedIn: true, usesSubscription: true, authMethod: 'chatgpt', planType: 'plus',
  usage: { primary: { usedPercent: 23, windowMinutes: 300, resetsAt: '2026-10-13T18:00:00Z' }, secondary: { usedPercent: 40, windowMinutes: 10080 }, limitReached: false } };
const report = { version: { analyzerVersion: '1', aiProvider: 'codex', aiModel: 'gpt-x (Codex, effort high)' }, sections: ['summary'], context: {} };

test('analise com o Codex escolhido envia o provedor e informa qual IA esta rodando', async () => {
  let resolve;
  const h = harness((url) => url === '/api/analysis' ? new Promise(r => { resolve = r; }) : { body: {} });
  h.run(`authState = ${JSON.stringify(claude)}; codexState = ${JSON.stringify(codex)}; store.set("ea.provider", "codex");`);

  const running = h.run('analyze()');
  const request = h.calls.find(c => c.url === '/api/analysis');
  assert.equal(JSON.parse(request.options.body).provider, 'codex');
  assert.match(h.run('running.get(key(selected)).status'), /Codex/);

  resolve({ body: { report, markdown: '' } });
  await running;
  assert.match(h.element('analyzeStatus').textContent, /Concluído com o Codex/);
});

test('com so o Claude conectado a escolha salva do Codex cai para o Claude', () => {
  const h = harness(() => ({ body: {} }));
  h.run(`authState = ${JSON.stringify(claude)}; codexState = { cliInstalled: true, loggedIn: false }; store.set("ea.provider", "codex");`);

  assert.equal(h.run('currentProvider()'), 'claude');
  h.run('codexState = ' + JSON.stringify(codex));
  assert.equal(h.run('currentProvider()'), 'codex');
  h.run('authState = { cliInstalled: true, loggedIn: false }; codexState = { cliInstalled: false, loggedIn: false }');
  assert.equal(h.run('currentProvider()'), null);
});

test('limite de uso do Codex atualiza o estado da conta do Codex', async () => {
  const h = harness((url) => url === '/api/analysis'
    ? { status: 429, body: { error: 'Limite de uso do Codex atingido: try again at 3 PM', code: 'usage_limit', provider: 'codex' } }
    : { body: { ...codex, usage: { ...codex.usage, limitReached: true } } });
  h.run(`authState = ${JSON.stringify(claude)}; codexState = ${JSON.stringify(codex)}; store.set("ea.provider", "codex");`);

  await h.run('analyze()');
  await new Promise(r => setTimeout(r, 0));

  assert.match(h.element('analyzeStatus').textContent, /Limite de uso do Codex/);
  assert.ok(h.calls.some(c => c.url.startsWith('/api/auth/codex/status')));
  assert.ok(!h.calls.some(c => c.url.startsWith('/api/auth/status')));
});

test('rodape do Codex mostra a conta e o uso do limite sem afetar o do Claude', () => {
  const h = harness(() => ({ body: { models: [], model: '', effort: '' } }));
  h.run(`renderAuth(${JSON.stringify(claude)}); renderCodex(${JSON.stringify(codex)});`);

  const footer = h.element('codexFooter').innerHTML;
  assert.match(footer, /Plus/);
  assert.match(footer, /sessão/);
  assert.match(footer, /23%/);
  assert.match(footer, /semana/);
  assert.match(h.element('claudeFooter').innerHTML, /Sair/);
});

test('rodape do Claude mostra o uso da sessao e da semana e avisa o limite atingido', async () => {
  const usage = { primary: { usedPercent: 30, windowMinutes: 300, resetsAt: '2026-10-13T13:50:00Z' },
    secondary: { usedPercent: 100, windowMinutes: 10080, resetsAt: '2026-10-13T21:00:00Z' }, limitReached: true };
  const h = harness((url) => url.startsWith('/api/auth/usage') ? { body: { usage } } : { body: { models: [], model: '', effort: '' } });
  h.run(`codexState = { cliInstalled: true, loggedIn: false }; renderAuth(${JSON.stringify(claude)});`);
  await new Promise(r => setTimeout(r, 0));

  const bars = h.element('claudeUsage').innerHTML;
  assert.match(bars, /usage claude/);
  assert.match(bars, /sessão/);
  assert.match(bars, /30%/);
  assert.match(bars, /semana/);
  assert.match(bars, /use o Codex/);
  assert.match(h.element('aiSource').textContent, /IA: Claude · plano mensal · limite de uso atingido/);
});

test('depois da analise atualiza so o uso da conta usada', async () => {
  const h = harness((url) => url === '/api/analysis' ? { body: { report: { ...report, version: { ...report.version, aiProvider: 'claude' } }, markdown: '' } }
    : url.startsWith('/api/auth/usage') ? { body: { usage: null } } : { body: {} });
  h.run(`authState = ${JSON.stringify(claude)}; codexState = ${JSON.stringify(codex)}; store.set("ea.provider", "claude");`);

  await h.run('analyze()');

  assert.ok(h.calls.some(c => c.url === '/api/auth/usage?refresh=true'));
  assert.ok(!h.calls.some(c => c.url.startsWith('/api/auth/codex/status')));
});
