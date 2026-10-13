const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1].replace(/init\(\)\.catch\([^\n]+\);/, '');

/** Página com uma análise aberta; fetch registra as chamadas e responde com handler(url). */
function harness(handler) {
  const elements = new Map();
  const calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      id, value: '', textContent: '', innerHTML: '', className: '', disabled: false, hidden: false, open: false,
      addEventListener() {}, remove() {}, showModal() { this.open = true; }, close() { this.open = false; },
    });
    return elements.get(id);
  }
  const context = vm.createContext({
    document: { getElementById: element, querySelectorAll: () => [], querySelector: () => null, addEventListener() {} },
    window: { addEventListener() {}, open: () => null },
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    sessionStorage: { getItem: () => null, setItem() {} },
    fetch: async (url, options) => {
      calls.push({ url, method: options?.method ?? 'GET' });
      const { status = 200, body } = await handler(url);
      return { ok: status < 400, status, statusText: '', headers: { get: () => 'application/json' }, json: async () => body };
    },
    URL, AbortController, console, setTimeout, clearTimeout, alert() {}, confirm: () => true,
  });
  vm.runInContext(script, context);
  return { element, calls, run: code => vm.runInContext(code, context) };
}

const tick = () => new Promise(r => setTimeout(r, 0));
const counts = (c = {}) => ({ added: 0, removed: 0, changed: 0, uncertain: 0, notComparable: 0, minor: 0, unchanged: 0, total: 0, ...c });
const version = (id, commit, extra = {}) => ({ analysisId: id, createdAt: '2026-10-13T10:00:00Z', updatedAt: '2026-10-13T10:00:00Z', commit,
  analyzerVersion: '1.0', sections: ['summary', 'scenarios'], hasAi: false, ...extra });
const item = (kind, impact, title, fields, refs = {}) => ({ kind, impact, key: title, title, fields, notes: [], ...refs });

const sampleDiff = {
  analysisId: 'a2', hasBase: true, hasDifferences: true, message: '3 diferença(s): 1 adicionado(s), 1 removido(s), 1 alterado(s).',
  base: version('a1', 'abc1234'), current: version('a2', 'def5678', { hasAi: true, aiProvider: 'claude' }),
  counts: counts({ added: 1, removed: 1, changed: 1, notComparable: 1, minor: 1, total: 3 }),
  notes: ['Versões do analisador diferentes'],
  sections: [
    { id: 'summary', title: 'Resumo', comparable: true, counts: counts({ changed: 1, notComparable: 1, total: 1 }), groups: [
      { id: 'validacoes', title: 'Validações', comparable: true, counts: counts({ changed: 1, total: 1, unchanged: 2 }), notes: [], items: [
        item('alterado', 'comportamento', 'Observação até 500 caracteres', [{ path: 'maximo', label: 'Máximo', impact: 'comportamento', before: '500', after: '600' }],
          { baseRef: 'D1', currentRef: 'D3' }),
      ] },
      { id: 'documentacao', title: 'Documentação da IA', comparable: false, reason: 'Resumo sem IA na versão anterior: a documentação da IA não é comparável (nada foi removido).',
        counts: counts({ notComparable: 1 }), notes: [], items: [] },
    ] },
    { id: 'scenarios', title: 'Cenários', comparable: true, counts: counts({ added: 1, removed: 1, minor: 1, total: 2 }), groups: [
      { id: 'matriz-estatica', title: 'Matriz estática', comparable: true, counts: counts({ added: 1, removed: 1, minor: 1, total: 2 }), notes: [], items: [
        item('adicionado', 'comportamento', '<b>Pedido fechado</b>', [{ path: 'esperado.http', label: 'HTTP esperado', impact: 'comportamento', after: 409 }], { currentRef: 'CEN-01' }),
        item('removido', 'comportamento', 'Pedido cancelado', [{ path: 'esperado.mensagens', label: 'Mensagens', impact: 'comportamento', before: ['Cancelado.'] }], { baseRef: 'CEN-09' }),
        item('alterado', 'dados-teste', 'Payload com outra data', [{ path: 'requisicao.corpo', label: 'Body', impact: 'dados-teste', before: { data: '2026-10-12' }, after: { data: '2026-10-13' } }],
          { baseRef: 'CEN-02', currentRef: 'CEN-03' }),
      ] },
    ] },
  ],
};

const openReport = (h, id = 'a2') => h.run(`lastResponse = { report: { analysisId: '${id}', version: { analyzerVersion: '1.0' }, sections: [], context: {} }, markdown: '' };`);

test('a aba Diff aparece e consulta a comparacao salva sem reanalisar nem chamar a IA', async () => {
  const h = harness(url => url.startsWith('/api/analysis/diff') ? { body: sampleDiff } : { status: 500, body: { error: 'inesperado' } });
  openReport(h);
  h.run('render("diff")');
  assert.match(h.element('result').innerHTML, /data-tab="diff"/);
  assert.match(h.element('result').innerHTML, /Carregando a comparação/);
  await tick(); await tick();

  assert.deepEqual(h.calls, [{ url: '/api/analysis/diff?analysisId=a2', method: 'GET' }]);
  const page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /Anterior[\s\S]*abc1234[\s\S]*Atual[\s\S]*def5678/);
  assert.match(page, /1 adicionado\(s\)[\s\S]*1 removido\(s\)[\s\S]*1 alterado\(s\)[\s\S]*1 não comparável\(is\)/);
  // Lado a lado: antes (del) à esquerda, depois (ins) à direita, com os ids das duas versões.
  assert.match(page, /diff-cell del" data-side="Anterior">500<\/div>\s*<div class="diff-cell ins" data-side="Atual">600/);
  assert.match(page, /<code>D1<\/code> → <code>D3<\/code>/);
  assert.match(h.run('diffTabLabel(lastResponse.report)'), /badge[^>]*>3</);
});

test('filtros por aba, por tipo e variacoes de dados de teste', async () => {
  const h = harness(() => ({ body: sampleDiff }));
  openReport(h);
  await h.run('loadDiff(lastResponse.report, true)');

  h.run('diffSection = "scenarios"; diffFilter = "removido"');
  let page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /Pedido cancelado/);
  assert.doesNotMatch(page, /Pedido fechado/);

  h.run('diffFilter = "all"');
  page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /&lt;b&gt;Pedido fechado&lt;\/b&gt;/, 'título escapado');
  assert.doesNotMatch(page, /Payload com outra data/, 'variação só de dados de teste fica oculta');
  assert.match(page, /Variações de dados de teste, origem e ambiente \(1\)/);

  h.run('diffMinor = true');
  page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /Payload com outra data[\s\S]*dados de teste/);

  h.run('diffSection = "summary"');
  page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /Observação até 500 caracteres/);
  assert.doesNotMatch(page, /Pedido/);
});

test('aba ausente aparece como nao comparavel, nunca como remocao', async () => {
  const h = harness(() => ({ body: sampleDiff }));
  openReport(h);
  await h.run('loadDiff(lastResponse.report, true)');
  const page = h.run('diffSection = "summary"; diffFilter = "all"; renderDiff(lastResponse.report)');
  assert.match(page, /Documentação da IA[\s\S]*diff-off[^>]*>Resumo sem IA na versão anterior/);
  assert.doesNotMatch(page.split('Documentação da IA')[1], /Removido/);
});

test('primeira analise e analise sem mudancas tem mensagens claras', async () => {
  const responses = {
    a1: { analysisId: 'a1', hasBase: false, hasDifferences: false, message: 'Nenhuma análise anterior para comparar.', current: version('a1'), counts: counts(), notes: [], sections: [] },
    a2: { ...sampleDiff, hasDifferences: false, message: 'Nenhuma diferença encontrada.', counts: counts({ unchanged: 9 }),
      sections: [{ id: 'summary', title: 'Resumo', comparable: true, counts: counts(), groups: [{ id: 'regras', title: 'Regras de negócio', comparable: true, counts: counts({ unchanged: 3 }), notes: [], items: [] }] }] },
  };
  const h = harness(url => ({ body: responses[new URL(url, 'http://x').searchParams.get('analysisId')] }));

  openReport(h, 'a1');
  await h.run('loadDiff(lastResponse.report, true)');
  let page = h.run('renderDiff(lastResponse.report)');
  assert.match(page, /diff-empty">Nenhuma análise anterior para comparar\./);

  openReport(h, 'a2');
  await h.run('loadDiff(lastResponse.report, true)');
  page = h.run('diffSection = "summary"; renderDiff(lastResponse.report)');
  assert.match(page, /diff-empty">Nenhuma diferença encontrada\./);
  assert.match(page, /3 igual\(is\)/);
});

test('resposta atrasada de outra analise nao substitui o diff exibido', async () => {
  const pending = {};
  const h = harness(url => new Promise(resolve => { pending[new URL(url, 'http://x').searchParams.get('analysisId')] = resolve; }));
  openReport(h, 'a1');
  const first = h.run('loadDiff(lastResponse.report, true)');
  openReport(h, 'a2');
  const second = h.run('loadDiff(lastResponse.report, true)');
  pending.a2({ body: sampleDiff });
  await second;
  pending.a1({ body: { ...sampleDiff, analysisId: 'a1', message: 'velho' } });
  await first;
  assert.equal(h.run('diffState.analysisId'), 'a2');
  assert.equal(h.run('diffState.result.message'), sampleDiff.message);
});
