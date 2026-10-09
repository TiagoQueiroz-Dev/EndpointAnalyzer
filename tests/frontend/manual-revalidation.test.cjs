const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

function harness(fetch) {
  const elements = new Map();
  const storage = new Map();
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      value: '', textContent: '', innerHTML: '', disabled: false, open: false,
      addEventListener() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const context = vm.createContext({
    document: { getElementById: element, querySelectorAll: () => [], querySelector: () => null, addEventListener() {} },
    window: { addEventListener() {} },
    localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: k => storage.delete(k) },
    fetch, URL, console, setTimeout, clearTimeout,
  });
  vm.runInContext(script, context);
  vm.runInContext(`
    selected = { httpMethod: 'POST', route: '/api/test' }; solutionPath = 'Api.sln';
    const testScenario = { id: 'CEN-01', status: 'inconclusivo', reasons: ['sem prova'], request: { method: 'POST', url: '/api/test', body: { quantidade: 0 } }, expected: { outcome: 'erro', httpStatus: 400, messages: [], effects: [], persisted: false, statusSource: 'codigo' } };
    lastResponse = { report: { analysisId: 'session', runtime: { matrix: { scenarios: [testScenario] } }, ai: { summary: 'preservado' } }, markdown: 'anterior' };
    savedAnalyses.set(selected, solutionPath, lastResponse);
    let rendered = 0; render = () => rendered++; setAnalyzeStatus = () => {};
    openRevalidation('CEN-01');
  `, context);
  return { context, element, run: code => vm.runInContext(code, context) };
}

test('editor abre o ultimo payload e JSON invalido nao envia HTTP', async () => {
  let calls = 0;
  const h = harness(async () => { calls++; throw Error('unexpected HTTP'); });
  assert.deepEqual(JSON.parse(h.element('manualBody').value), { quantidade: 0 });
  h.element('manualBody').value = '{invalid';
  await h.run('revalidateScenario()');
  assert.equal(calls, 0);
  assert.match(h.element('manualMessage').textContent, /JSON inválido/);
});

test('clique concorrente envia uma chamada e atualiza runtime, Markdown e analise salva', async () => {
  let resolveFetch;
  let calls = 0;
  let request;
  const h = harness((url, options) => {
    calls++; request = { url, ...options };
    return new Promise(resolve => { resolveFetch = resolve; });
  });
  h.element('manualBody').value = '{"quantidade":0,"Quantidade":1}';
  const pending = h.run('revalidateScenario()');
  await h.run('revalidateScenario()');
  assert.equal(calls, 1);
  assert.equal(h.element('manualBody').disabled, true);
  assert.match(request.body, /"quantidade":0,"Quantidade":1/); // Servidor recebe duplicatas para recusá-las.
  const updated = { id: 'CEN-01', status: 'confirmado', observed: { body: 'resposta' } };
  resolveFetch({ ok: true, headers: { get: () => 'application/json' }, json: async () => ({ runtime: { matrix: { scenarios: [updated] } }, markdown: 'novo' }) });
  await pending;
  assert.equal(h.run('lastResponse.report.runtime.matrix.scenarios[0].status'), 'confirmado');
  assert.equal(h.run('lastResponse.report.ai.summary'), 'preservado');
  assert.equal(h.run('savedAnalyses.get(selected).response.markdown'), 'novo');
  assert.equal(h.run('rendered'), 1);
  assert.equal(h.element('manualRun').disabled, true);
  assert.equal(h.element('manualClose').disabled, false);
});

test('resposta atrasada nao sobrescreve uma analise nova', async () => {
  let resolveFetch;
  const h = harness(() => new Promise(resolve => { resolveFetch = resolve; }));
  const pending = h.run('revalidateScenario()');
  h.run(`lastResponse = { report: { analysisId: 'new-session', ai: { summary: 'nova' } }, markdown: 'nova' }; savedAnalyses.set(selected, solutionPath, lastResponse);`);
  resolveFetch({ ok: true, headers: { get: () => 'application/json' }, json: async () => ({ runtime: { matrix: { scenarios: [{ id: 'CEN-01', status: 'inconclusivo', reasons: ['outra regra'] }] } }, markdown: 'velha' }) });
  await pending;
  assert.equal(h.run('lastResponse.report.analysisId'), 'new-session');
  assert.equal(h.run('savedAnalyses.get(selected).response.markdown'), 'nova');
  assert.equal(h.run('rendered'), 0);
  assert.match(h.element('manualMessage').textContent, /continua inconclusivo/);
});

const jsonResponse = (body, status = 200) => ({
  ok: status >= 200 && status < 300, status,
  headers: { get: () => 'application/json' }, json: async () => body,
});

test('data de expiracao antiga nao bloqueia o editor', () => {
  const h = harness(async () => { throw Error('unexpected HTTP'); });
  h.run(`lastResponse.report.sessionExpiresAt = '2000-01-01'; openRevalidation('CEN-01');`);
  assert.equal(h.element('manualRun').disabled, false);
  assert.equal(h.element('manualBody').disabled, false);
});

test('recupera analise do SQLite mesmo sem cache no navegador', async () => {
  let requestUrl;
  const restored = { report: { analysisId: 'persisted-id', runtime: { matrix: { scenarios: [] } } }, markdown: 'persistido' };
  const h = harness(async url => { requestUrl = url; return jsonResponse(restored); });
  h.run(`store.set(savedAnalyses.id(selected), null); lastResponse = null;`);
  await h.run('restoreAnalysis(selected)');
  assert.match(requestUrl, /\/api\/analysis\/saved\?/);
  assert.equal(h.run('lastResponse.report.analysisId'), 'persisted-id');
  assert.equal(h.run('savedAnalyses.get(selected).response.markdown'), 'persistido');
});

test('importa cache antigo quando SQLite nao possui a analise, sem chamar analise com IA', async () => {
  const requests = [];
  let oldReport;
  const h = harness(async (url, options) => {
    requests.push({ url, options });
    if (requests.length === 1) return jsonResponse({ error: 'nenhuma análise salva' }, 404);
    const report = JSON.parse(options.body).report;
    oldReport = report;
    return jsonResponse({ report: { ...report, analysisId: 'imported-id', sessionExpiresAt: null }, markdown: 'importado' });
  });
  h.element('apiToken').value = 'token-to-preserve';
  await h.run('restoreAnalysis(selected)');
  assert.equal(requests.length, 2);
  assert.equal(requests[1].url, '/api/analysis/restore');
  assert.equal(JSON.parse(requests[1].options.body).apiToken, 'token-to-preserve');
  assert.equal(oldReport.analysisId, 'session');
  assert.equal(h.run('lastResponse.report.analysisId'), 'imported-id');
});

test('reanalisar restaura sessao ausente e envia o payload editado com o token informado', async () => {
  const requests = [];
  const h = harness(async (url, options) => {
    const request = JSON.parse(options.body);
    requests.push({ url, request });
    if (requests.length === 1) return jsonResponse({ error: 'análise não encontrada' }, 404);
    if (url === '/api/analysis/restore') return jsonResponse({ report: { ...request.report, analysisId: 'restored-id' } });
    return jsonResponse({ runtime: { matrix: { scenarios: [{ id: 'CEN-01', status: 'confirmado', observed: { body: 'ok' } }] } }, markdown: 'novo' });
  });
  h.element('apiToken').value = 'same-token';
  h.run(`openRevalidation('CEN-01')`);
  h.element('manualBody').value = '{"quantidade":136}';
  await h.run('revalidateScenario()');
  assert.deepEqual(requests.map(r => r.url), ['/api/analysis/scenarios/revalidate', '/api/analysis/restore', '/api/analysis/scenarios/revalidate']);
  assert.equal(requests[2].request.analysisId, 'restored-id');
  assert.equal(requests[2].request.body.quantidade, 136);
  assert.equal(requests[2].request.apiToken, 'same-token');
  assert.equal(h.run('savedAnalyses.get(selected).response.report.analysisId'), 'restored-id');
});

test('recuperacao atrasada do SQLite nao sobrescreve analise nova', async () => {
  let resolveFetch;
  const h = harness(() => new Promise(resolve => { resolveFetch = resolve; }));
  const pending = h.run('restoreAnalysis(selected)');
  h.run(`lastResponse = { report: { analysisId: 'new-analysis' }, markdown: 'nova' }; savedAnalyses.set(selected, solutionPath, lastResponse);`);
  resolveFetch(jsonResponse({ report: { analysisId: 'old-analysis' }, markdown: 'antiga' }));
  await pending;
  assert.equal(h.run('lastResponse.report.analysisId'), 'new-analysis');
  assert.equal(h.run('savedAnalyses.get(selected).response.markdown'), 'nova');
});
