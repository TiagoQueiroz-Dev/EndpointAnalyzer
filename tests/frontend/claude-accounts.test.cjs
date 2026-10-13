const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

/** Página com um endpoint aberto; fetch registra as chamadas e responde com handler(url, options). */
function harness(handler, storage = new Map()) {
  const elements = new Map();
  const calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      id, value: '', textContent: '', innerHTML: '', className: '', disabled: false, hidden: false, open: false, checked: false,
      addEventListener() {}, focus() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const confirms = [];
  // Abas e IA marcadas: Resumo com IA.
  const box = (dataset) => ({ dataset, checked: true, disabled: false, addEventListener() {}, closest: () => ({ classList: { toggle() {} } }) });
  const boxes = { '[data-section]': [box({ section: 'summary' })], '[data-ai]': [box({ ai: 'summary' })] };
  const context = vm.createContext({
    document: { getElementById: element, querySelectorAll: (selector) => boxes[selector] ?? [], querySelector: () => null, addEventListener() {} },
    window: { addEventListener() {}, open: () => null },
    localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
    sessionStorage: { getItem: () => null, setItem() {} },
    fetch: async (url, options) => {
      calls.push({ url, method: options?.method ?? 'GET', body: options?.body ? JSON.parse(options.body) : undefined });
      const { status = 200, body } = await handler(url, options);
      return { ok: status < 400, status, statusText: '', headers: { get: () => 'application/json' }, json: async () => body };
    },
    URL, AbortController, console, setTimeout, clearTimeout, alert() {}, confirm: (message) => { confirms.push(message); return true; },
  });
  vm.runInContext(script, context);
  vm.runInContext(`selected = { httpMethod: 'POST', route: '/api/test', project: 'Api' }; solutionPath = 'Api.sln'; render = () => {}; progressPollMs = 0;`, context);
  return { element, calls, confirms, run: code => vm.runInContext(code, context) };
}

const tick = () => new Promise(r => setTimeout(r, 0));
const account = (id, display, state, extra = {}) => ({ id, display, email: display, state, loggedIn: state !== 'desconectado',
  usesSubscription: state !== 'desconectado', plan: 'max', ...extra });
const status = (accounts, extra = {}) => ({ cliInstalled: true, loggedIn: true, usesSubscription: true, activeSource: 'subscription', mode: 'auto',
  continueWithOtherProvider: true, accounts, ...extra });
const three = status([
  account('principal', 'a@empresa.com', 'em-uso'),
  account('b1', 'b@empresa.com', 'sem-uso', { until: '2026-10-13T18:00:00Z', message: 'Limite de uso do plano atingido.' }),
  account('c2', 'Conta 3', 'desconectado'),
]);

test('rodape lista as contas na ordem, com o estado de cada uma e as acoes possiveis', () => {
  const h = harness(() => ({ body: { models: [], model: '', effort: '', usage: null } }));
  h.run(`renderAuth(${JSON.stringify(three)})`);
  const footer = h.element('claudeFooter').innerHTML;

  assert.match(footer, /a@empresa\.com[\s\S]*b@empresa\.com[\s\S]*Conta 3/, 'na ordem de uso');
  assert.match(footer, /claudeState-principal">em uso</);
  assert.match(footer, /claudeState-b1">sem uso até/);
  assert.match(footer, /claudeState-c2">desconectada</);
  assert.match(footer, /data-account-action="check" data-provider="claude"\s+data-account="b1"/, 'conta sem uso pode ser verificada de novo');
  assert.match(footer, /data-account-action="login" data-provider="claude"\s+data-account="c2"/, 'conta desconectada pode entrar');
  assert.doesNotMatch(footer, /data-account-action="login" data-provider="claude"\s+data-account="principal"/);
  assert.match(footer, /data-account-action="up" data-provider="claude"\s+data-account="principal"[^>]*disabled/);
  assert.match(footer, /data-account-action="down" data-provider="claude"\s+data-account="c2"[^>]*disabled/);
  assert.match(footer, /\+ Adicionar conta Claude/);
  // A opção de continuar no outro provedor fica no menu da engrenagem do cabeçalho.
  assert.match(footer, /class="footer-head">[\s\S]*<details class="footer-settings"[\s\S]*id="claudeContinueOther" data-continue-other checked[\s\S]*<\/details><\/div>/);
  // O uso do plano é lido por conta (só as logadas no plano mensal).
  assert.deepEqual(h.calls.filter(c => c.url.startsWith('/api/auth/usage')).map(c => c.url),
    ['/api/auth/usage?account=principal&refresh=false', '/api/auth/usage?account=b1&refresh=false']);
  assert.match(h.element('aiSource').textContent, /plano mensal · 2 contas/);
});

test('adicionar, entrar, reordenar, sair e remover usam a conta certa sem afetar as outras', async () => {
  const added = status([...three.accounts, account('d3', 'Conta 4', 'desconectado')]);
  const h = harness((url) => url === '/api/auth/accounts' ? { body: { id: 'd3', status: added } }
    : url === '/api/auth/login' ? { body: { loginUrl: 'https://claude.ai/oauth/x' } }
    : url.startsWith('/api/auth/usage') ? { body: { usage: null } } : { body: added });
  h.run(`renderAuth(${JSON.stringify(three)})`);

  await h.run('accountAction("claude", "add")');
  assert.equal(h.element('loginDialog').open, true);
  assert.match(h.element('loginAccountNote').textContent, /Conta 4 na ordem de uso/);
  await h.run('startLogin()');
  h.element('loginCode').value = 'codigo-123';
  await h.run('confirmLogin()');
  const sent = h.calls.filter(c => c.url === '/api/auth/login' || c.url === '/api/auth/code').map(c => c.body);
  assert.deepEqual(sent, [{ email: null, account: 'd3' }, { code: 'codigo-123', account: 'd3' }]);

  h.run(`renderAuth(${JSON.stringify(three)})`);
  await h.run('accountAction("claude", "down", "principal")');
  assert.deepEqual(h.calls.find(c => c.url === '/api/auth/accounts/order').body, { ids: ['b1', 'principal', 'c2'] });

  h.run(`renderAuth(${JSON.stringify(three)})`);
  await h.run('accountAction("claude", "logout", "b1")');
  await h.run('accountAction("claude", "remove", "c2")');
  await h.run('accountAction("claude", "check", "b1")');
  assert.ok(h.calls.some(c => c.url === '/api/auth/logout?account=b1' && c.method === 'POST'));
  assert.ok(h.calls.some(c => c.url === '/api/auth/accounts/c2' && c.method === 'DELETE'));
  assert.ok(h.calls.some(c => c.url === '/api/auth/accounts/b1/check' && c.method === 'POST'));
  assert.match(h.confirms[0], /b@empresa\.com[\s\S]*outras contas continuam conectadas/);
});

test('durante a analise mostra a conta em uso e a troca automatica de conta', async () => {
  let finish;
  const progress = { running: true, provider: 'claude', account: 'b@empresa.com', model: 'opus',
    handoffs: [{ fromAccount: 'a@empresa.com', toAccount: 'b@empresa.com', reason: 'limite-de-uso', step: 'documentação' }] };
  const report = { version: { analyzerVersion: '1', aiProvider: 'claude' }, sections: ['summary'], context: {},
    aiRun: { accounts: [{ label: 'a@empresa.com', provider: 'claude' }, { label: 'b@empresa.com', provider: 'claude' }], handoffs: progress.handoffs, notes: [] } };
  const h = harness((url) => url === '/api/analysis' ? new Promise(r => { finish = r; })
    : url.startsWith('/api/analysis/progress') ? { body: progress }
    : url.startsWith('/api/auth/status') ? { body: three } : { body: { usage: null } });
  h.run(`authState = ${JSON.stringify(three)}; codexState = { cliInstalled: true, loggedIn: false }; progressPollMs = 5;`);

  const running = h.run('analyze()');
  await new Promise(r => setTimeout(r, 30));
  assert.match(h.run('running.get(key(selected)).status'), /Analisando com o Claude \(b@empresa\.com\) · a@empresa\.com atingiu o limite de uso: a análise continuou em b@empresa\.com/);
  assert.match(h.element('analyzeStatus').textContent, /continuou em b@empresa\.com/);
  assert.ok(h.calls.some(c => c.url.startsWith('/api/auth/status')), 'o rodapé relê o estado das contas depois da troca');

  finish({ body: { report, markdown: '' } });
  await running;
  const polls = h.calls.filter(c => c.url.startsWith('/api/analysis/progress')).length;
  await new Promise(r => setTimeout(r, 30));
  assert.equal(h.calls.filter(c => c.url.startsWith('/api/analysis/progress')).length, polls, 'o acompanhamento para quando a análise termina');
  assert.match(h.element('analyzeStatus').textContent, /Concluído com o Claude · contas: a@empresa\.com → b@empresa\.com/);
});

test('sem conta com uso a fila pausa, guarda o parcial e preserva o item e os restantes', async () => {
  const interrupted = { version: { analyzerVersion: '1', aiProvider: 'claude' }, sections: ['summary'], context: {},
    aiRun: { accounts: [{ label: 'a@empresa.com' }], handoffs: [], notes: [],
      interruption: { reason: 'limite-de-uso', message: 'Nenhuma conta da IA com uso disponível (a@empresa.com: limite de uso).', pending: ['Negócio: fluxo de negócio pela IA'] } } };
  const answers = [];
  const h = harness((url) => url === '/api/analysis' ? new Promise(r => answers.push(r))
    : url.startsWith('/api/auth/status') ? { body: three } : { body: { usage: null } });
  h.run(`authState = ${JSON.stringify(three)}; codexState = { cliInstalled: true, loggedIn: false };
    endpoints = ['/api/a', '/api/b'].map(route => ({ httpMethod: 'POST', route, project: 'Api', group: 'G', summary: route }));
    togglePick(endpoints.map(key), true); startQueue();`);
  await tick();

  answers[0]({ body: { report: interrupted, markdown: '' } });
  await h.run('queue.runner');

  assert.equal(h.run('queue.items.map(i => i.status).join(",")'), 'pendente,pendente');
  assert.match(h.run('queue.items[0].note'), /Parcial salvo; pendente: Negócio/);
  assert.match(h.run('queue.paused.reason'), /Nenhuma conta da IA com uso disponível/);
  assert.equal(answers.length, 1, 'nenhum outro endpoint começa sem conta disponível');
  // O parcial fica salvo para abrir o endpoint sem nova análise.
  assert.ok(h.run('savedAnalyses.get(endpoints[0])?.response.report.aiRun.interruption'));
});

test('o resultado mostra as trocas de conta e o aviso de analise parcial', () => {
  const h = harness(() => ({ body: {} }));
  const run = {
    accounts: [{ label: 'a@empresa.com', provider: 'claude' }, { label: 'eu@chatgpt.com', provider: 'codex' }],
    handoffs: [{ fromAccount: 'a@empresa.com', toAccount: 'eu@chatgpt.com', reason: 'limite-de-uso', step: 'adquirir dados (rodada 2)', at: '2026-10-13T15:00:00Z' }],
    notes: ['Claude (b@empresa.com) pulada: limite de uso até 13/10 18:00.'],
    interruption: { reason: 'limite-de-uso', message: 'Nenhuma conta da IA com uso disponível.', pending: ['Cenários: validação em runtime (3 cenário(s) sem conclusão)'] },
  };
  const page = h.run(`aiRunHtml(${JSON.stringify(run)})`);
  assert.match(page, /Análise parcial: nenhuma conta da IA com uso disponível/);
  assert.match(page, /Pendente: Cenários: validação em runtime \(3 cenário\(s\) sem conclusão\)/);
  assert.match(page, /a@empresa\.com atingiu o limite de uso em "adquirir dados \(rodada 2\)": a mesma etapa continuou em eu@chatgpt\.com/);
  assert.match(page, /b@empresa\.com\) pulada/);
  assert.match(h.run(`analysisStatus({ version: { aiProvider: 'codex' }, aiRun: ${JSON.stringify(run)} })`),
    /^Concluído parcialmente com o Codex · contas: a@empresa\.com → eu@chatgpt\.com · nenhuma conta com uso disponível/);
  assert.equal(h.run(`aiRunHtml({ accounts: [{ label: 'a@empresa.com' }], handoffs: [], notes: [] })`), '');
});
