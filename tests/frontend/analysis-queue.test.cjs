const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

const endpointList = ['POST /api/a', 'PUT /api/b', 'DELETE /api/c'].map(id => {
  const [httpMethod, route] = id.split(' ');
  return { httpMethod, route, project: 'Api', group: 'G', summary: id, controller: 'C', action: 'A' };
});
const claude = { cliInstalled: true, loggedIn: true, usesSubscription: true, activeSource: 'subscription', mode: 'auto' };
const codex = { cliInstalled: true, loggedIn: true, authMethod: 'chatgpt' };
const reportOf = (provider) => ({ report: { version: { analyzerVersion: '1', aiProvider: provider }, sections: ['summary'], context: {} }, markdown: '' });

/**
 * Página com 3 endpoints e as duas contas conectadas. Cada POST /api/analysis fica pendente até o teste resolver
 * (answer) ou o navegador abortar; as demais rotas respondem {}.
 */
function harness(storage = new Map()) {
  const elements = new Map();
  const analyses = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      id, value: '', textContent: '', innerHTML: '', className: '', disabled: false, hidden: false, open: false,
      addEventListener() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const context = vm.createContext({
    document: { getElementById: element, querySelectorAll: () => [], querySelector: () => null, addEventListener() {} },
    window: { addEventListener() {}, open: () => null },
    localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
    sessionStorage: { getItem: () => null, setItem() {} },
    fetch: (url, options) => new Promise((resolve, reject) => {
      const reply = (status, body) => resolve({ ok: status < 400, status, statusText: '', headers: { get: () => 'application/json' }, json: async () => body });
      if (url !== '/api/analysis') { reply(200, {}); return; }
      const request = JSON.parse(options.body);
      const call = { request, answer: reply, aborted: false };
      options.signal?.addEventListener('abort', () => { call.aborted = true; reject(new Error('aborted')); });
      analyses.push(call);
    }),
    URL, AbortController, console, setTimeout, clearTimeout, alert() {}, confirm: () => true,
  });
  vm.runInContext(script, context);
  vm.runInContext(`endpoints = ${JSON.stringify(endpointList)}; solutionPath = 'Api.sln'; render = () => {};
    authState = ${JSON.stringify(claude)}; codexState = ${JSON.stringify(codex)};`, context);
  return { context, element, analyses, storage, run: code => vm.runInContext(code, context) };
}

const tick = () => new Promise(r => setTimeout(r, 0));
const statuses = (h) => h.run('queue.items.map(i => i.status).join(",")');
const pick = (h, ...ids) => h.run(`togglePick(${JSON.stringify(ids.map(id => `Api:${id}`))}, true)`);

test('selecionar varios e iniciar uma vez analisa um por vez, na ordem, cada um com o proprio resultado', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b', 'DELETE /api/c');
  h.run('startQueue()');
  await tick();

  assert.equal(h.analyses.length, 1, 'só a primeira análise começa');
  assert.equal(h.analyses[0].request.endpoint.route, '/api/a');
  assert.equal(statuses(h), 'analisando,pendente,pendente');
  assert.match(h.element('queuePanel').innerHTML, /Analisando 1 de 3 endpoints/);

  h.analyses[0].answer(200, reportOf('claude'));
  await tick(); await tick();
  assert.equal(h.analyses.length, 2, 'a segunda só começa quando a primeira termina');
  assert.equal(h.analyses[1].request.endpoint.route, '/api/b');
  assert.match(h.element('queuePanel').innerHTML, /Analisando 2 de 3 endpoints/);

  h.analyses[1].answer(200, reportOf('claude'));
  await tick(); await tick();
  h.analyses[2].answer(200, reportOf('claude'));
  await h.run('queue.runner');

  assert.equal(statuses(h), 'concluido,concluido,concluido');
  assert.match(h.element('queuePanel').innerHTML, /Fila concluída: 3 concluído\(s\)/);
  // Cada endpoint tem o resultado salvo, para abrir depois sem nova análise.
  for (const e of endpointList) assert.ok(h.storage.has(`ea.analysis:Api.sln|Api:${e.httpMethod} ${e.route}`));
});

test('cliques repetidos nao duplicam itens nem analises', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b');
  h.run('startQueue(); startQueue(); runQueue(); runQueue();');
  pick(h, 'POST /api/a', 'PUT /api/b');
  h.run('startQueue(); analyze();');
  await tick();

  assert.equal(h.run('queue.items.length'), 2);
  assert.equal(h.analyses.length, 1);
  // ANALISAR do endpoint pendente na fila não dispara outra análise.
  h.run('selected = endpoints[1]; analyze();');
  await tick();
  assert.equal(h.analyses.length, 1);
});

test('cancelar os restantes impede que eles comecem; o atual termina normalmente', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b', 'DELETE /api/c');
  h.run('startQueue()');
  await tick();

  h.run('cancelQueueRemaining()');
  h.analyses[0].answer(200, reportOf('claude'));
  await h.run('queue.runner');

  assert.equal(h.analyses.length, 1);
  assert.equal(statuses(h), 'concluido,cancelado,cancelado');
});

test('cancelar o atual interrompe a requisicao e segue para o proximo', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b');
  h.run('startQueue()');
  await tick();

  h.run('cancelQueueCurrent()');
  await tick(); await tick();

  assert.ok(h.analyses[0].aborted);
  assert.equal(h.analyses.length, 2);
  assert.equal(statuses(h), 'cancelado,analisando');
});

test('falha de um endpoint fica registrada e a fila continua com os proximos', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b');
  h.run('startQueue()');
  await tick();

  h.analyses[0].answer(500, { error: 'Falha ao montar o call graph.' });
  await tick(); await tick();
  h.analyses[1].answer(200, reportOf('claude'));
  await h.run('queue.runner');

  assert.equal(statuses(h), 'erro,concluido');
  assert.match(h.element('queuePanel').innerHTML, /Falha ao montar o call graph/);
  assert.match(h.element('queuePanel').innerHTML, /Tentar de novo/);
});

test('limite de uso pausa a fila sem perder o item e continua no outro provedor', async () => {
  const h = harness();
  pick(h, 'POST /api/a', 'PUT /api/b');
  h.run('startQueue()');
  await tick();
  assert.equal(h.analyses[0].request.provider, 'claude');

  h.analyses[0].answer(429, { error: 'Limite de uso do Claude atingido.', code: 'usage_limit', provider: 'claude' });
  await h.run('queue.runner');

  assert.equal(statuses(h), 'pendente,pendente', 'o item volta para a fila');
  assert.equal(h.analyses.length, 1, 'nenhum outro item começa com a fila pausada');
  assert.match(h.element('queuePanel').innerHTML, /Continuar com o Codex/);

  h.run('resumeQueue("codex")');
  await tick();
  assert.equal(h.analyses.length, 2);
  assert.equal(h.analyses[1].request.endpoint.route, '/api/a', 'continua pelo mesmo endpoint');
  assert.equal(h.analyses[1].request.provider, 'codex');
});

test('a fila sobrevive a recarregar a pagina e volta pausada', async () => {
  const first = harness();
  pick(first, 'POST /api/a', 'PUT /api/b');
  first.run('startQueue()');
  await tick();

  const second = harness(first.storage);
  second.run('restoreQueue()');

  assert.equal(statuses(second), 'pendente,pendente');
  assert.ok(second.run('queue.paused'));
  assert.equal(second.analyses.length, 0, 'nada roda sem o usuário retomar');
  second.run('resumeQueue()');
  await tick();
  assert.equal(second.analyses.length, 1);
  assert.equal(second.analyses[0].request.endpoint.route, '/api/a');
});
