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
const group = (id, title, items, extra = {}) => ({ id, title, comparable: true, counts: counts(), notes: [], items, ...extra });
const diffOf = (summary, scenarios, extra = {}) => ({
  analysisId: 'a2', hasBase: true, hasDifferences: true, message: 'diferenças', base: version('a1', 'abc1234'), current: version('a2', 'def5678'),
  counts: counts(), notes: [], ...extra,
  sections: [{ id: 'summary', title: 'Resumo', comparable: true, counts: counts(), groups: summary },
    { id: 'scenarios', title: 'Cenários', comparable: true, counts: counts(), groups: scenarios }],
});

const sampleDiff = {
  analysisId: 'a2', hasBase: true, hasDifferences: true, message: '3 diferença(s): 1 adicionado(s), 1 removido(s), 1 alterado(s).',
  base: version('a1', 'abc1234'), current: version('a2', 'def5678', { hasAi: true, aiProvider: 'claude' }),
  counts: counts({ added: 1, removed: 1, changed: 1, notComparable: 1, minor: 1, total: 3 }),
  notes: ['Versões do analisador diferentes'],
  sections: [
    { id: 'summary', title: 'Resumo', comparable: true, counts: counts({ changed: 1, notComparable: 1, total: 1 }), groups: [
      { id: 'validacoes', title: 'Validações', comparable: true, counts: counts({ changed: 1, total: 1, unchanged: 2 }), notes: ['Fonte: validações estruturadas.'], items: [
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

/** Página do diff já carregado, com aba, filtro e secundárias escolhidos. */
async function page(diff, { section = 'summary', filter = 'all', minor = false } = {}) {
  const h = harness(() => ({ body: diff }));
  openReport(h);
  await h.run('loadDiff(lastResponse.report, true)');
  h.run(`diffSection = ${JSON.stringify(section)}; diffFilter = ${JSON.stringify(filter)}; diffMinor = ${minor}`);
  return { h, html: h.run('renderDiff(lastResponse.report)') };
}

/** Card fechado: o que vem antes da primeira expansão. */
const closed = (card) => card.split('<details class="diff-more">')[0];
const cards = (pageHtml) => pageHtml.split('<article').slice(1);
const occurrences = (text, re) => (text.match(re) ?? []).length;

test('a aba Diff aparece e consulta a comparacao salva sem reanalisar nem chamar a IA', async () => {
  const h = harness(url => url.startsWith('/api/analysis/diff') ? { body: sampleDiff } : { status: 500, body: { error: 'inesperado' } });
  openReport(h);
  h.run('render("diff")');
  assert.match(h.element('result').innerHTML, /data-tab="diff"/);
  assert.match(h.element('result').innerHTML, /Carregando a comparação/);
  await tick(); await tick();

  assert.deepEqual(h.calls, [{ url: '/api/analysis/diff?analysisId=a2', method: 'GET' }]);
  const html = h.run('renderDiff(lastResponse.report)');
  // Cabeçalho compacto: commits na linha principal; analisador, IA e notas só em "Detalhes das versões".
  assert.match(html, /Anterior: <code>abc1234<\/code>[\s\S]*Atual: <code>def5678<\/code>/);
  const [head, rest] = html.split('</details>');
  assert.match(head, /<details class="diff-meta"><summary>Detalhes das versões<\/summary>[\s\S]*analisador 1\.0[\s\S]*Versões do analisador diferentes/);
  assert.doesNotMatch(rest, /Versões do analisador diferentes|analisador 1\.0/);
  // Totais (itens) só no cabeçalho; filtros sem contagens; nada de "N iguais".
  assert.match(html, /diff-count adicionado"><b>\+0<\/b> Adicionados[\s\S]*diff-count removido"><b>−0<\/b> Removidos[\s\S]*diff-count alterado"><b>~1<\/b> Alterado</);
  assert.doesNotMatch(html, /class="n"|igual\(is\)|iguais/);
  assert.match(h.run('diffTabLabel(lastResponse.report)'), /badge[^>]*>3</);
});

test('alterado: card fechado mostra so a mudanca prioritaria; a expansao, so os campos diferentes', async () => {
  const changed = item('alterado', 'comportamento', 'Observação com 550 caracteres', [
    { path: 'requisicao.corpo', label: 'Body', impact: 'dados-teste', before: { obs: 'a' }, after: { obs: 'b' } },
    { path: 'maximo', label: 'Limite máximo', impact: 'comportamento', before: 500, after: 600 },
    { path: 'esperado.resultado', label: 'Resultado esperado', impact: 'comportamento', before: 'erro', after: 'sucesso' },
    { path: 'esperado.http', label: 'HTTP esperado', impact: 'comportamento', before: 400, after: 200 },
  ], { baseRef: 'CEN-03', currentRef: 'CEN-05', before: { titulo: 'Observação', payload: '550 caracteres' }, after: { titulo: 'Observação', payload: '550 caracteres' } });
  const { html } = await page(diffOf([], [group('matriz-estatica', 'Matriz estática', [changed])]), { section: 'scenarios' });

  const [card] = cards(html);
  const top = closed(card);
  assert.match(top, /diff-kind alterado">Alterado<\/span>[\s\S]*Observação com 550 caracteres/);
  assert.match(top, /HTTP esperado:<\/span>\s*<span class="diff-hl del"><b>Antes<\/b>400<\/span>[\s\S]*<span class="diff-hl ins"><b>Depois<\/b>200</);
  assert.doesNotMatch(top, /Limite máximo|Resultado esperado|Body|CEN-0|<pre>/, 'visão inicial sem campos técnicos, IDs nem JSON');

  // Primeira expansão: Campo | Antes | Depois só com os campos alterados (dados de teste por último, marcados).
  const [expanded, full] = card.split('<details class="diff-full">');
  assert.match(expanded, /<summary>Ver alterações<\/summary>[\s\S]*<th scope="col">Campo<\/th><th scope="col">Antes<\/th><th scope="col">Depois<\/th>/);
  assert.equal(occurrences(expanded, /<th scope="row">/g), 4);
  assert.match(expanded, /Limite máximo[\s\S]*Resultado esperado[\s\S]*data-side="Antes">Erro[\s\S]*data-side="Depois">Sucesso[\s\S]*HTTP esperado[\s\S]*Body <span class="diff-impact dados-teste">/);
  assert.doesNotMatch(expanded, /CEN-03|&quot;payload&quot;/, 'IDs e payload inalterado ficam na estrutura completa');
  // Segundo nível: IDs, natureza e snapshots.
  assert.match(full, /<summary>Estrutura completa<\/summary>[\s\S]*<code>CEN-03<\/code> → <code>CEN-05<\/code>[\s\S]*&quot;payload&quot;: &quot;550 caracteres&quot;/);
});

test('getDiffHighlight prioriza alerta, esperado, regra e efeitos e nunca inventa valores', () => {
  const h = harness(() => ({ body: {} }));
  const hl = (it) => JSON.parse(JSON.stringify(h.run(`getDiffHighlight(${JSON.stringify(it)})`)));
  const f = (path, label, before, after, impact = 'comportamento') => ({ path, label, impact, before, after });

  assert.deepEqual(hl(item('alterado', 'runtime', 'X', [f('runtime.http', 'HTTP observado', 200, 500, 'runtime'), f('runtime.status', 'Status runtime', 'confirmado', 'inconclusivo', 'runtime')], { alert: true })),
    { path: 'runtime.status', label: 'Status runtime', before: 'Confirmado', after: 'Inconclusivo' });
  assert.equal(hl(item('alterado', 'comportamento', 'X', [f('esperado.efeitos', 'Efeitos', ['a'], ['b']), f('expressao', 'Condição', 'x > 1', 'x > 2')])).path, 'expressao');
  assert.equal(hl(item('alterado', 'comportamento', 'X', [f('requisicao.corpo', 'Body', 'a', 'b', 'dados-teste'), f('esperado.persiste', 'Persiste', true, false)])).after, 'não');
  // Só objetos (sem valor seguro em uma linha) ou só campos secundários num item de comportamento: sem destaque.
  assert.equal(hl(item('alterado', 'comportamento', 'X', [f('decisoes', 'Tabela', { a: true }, { a: false })])), null);
  assert.equal(hl(item('alterado', 'comportamento', 'X', [f('origem', 'Origem', 'A.cs', 'B.cs', 'origem')])), null);
  assert.equal(hl(item('alterado', 'comportamento', 'X', [])), null);
});

test('adicionado e removido: resumo do lado existente, sem coluna vazia nem dump', async () => {
  const { html } = await page(sampleDiff, { section: 'scenarios' });
  const [added, removed] = cards(html);

  assert.match(closed(added), /diff-kind adicionado">Adicionado[\s\S]*&lt;b&gt;Pedido fechado&lt;\/b&gt;/, 'título escapado');
  assert.match(closed(added), /<span class="diff-hl ins"><b>Depois<\/b><span class="diff-hl-label">HTTP esperado:<\/span> 409/);
  assert.match(closed(removed), /<span class="diff-hl del"><b>Antes<\/b><span class="diff-hl-label">Mensagens:<\/span> Cancelado\./);
  for (const card of [added, removed]) {
    assert.doesNotMatch(closed(card), /<pre>|CEN-|—/);
    assert.match(card, /<summary>Ver detalhes<\/summary>/);
    assert.doesNotMatch(card.split('<details class="diff-full">')[0], /data-side="(Antes|Depois)"|<th scope="col">Antes/, 'uma coluna só, do lado existente');
  }
  assert.match(added, /<th scope="col">Versão atual<\/th>/);
  assert.match(removed, /<th scope="col">Versão anterior<\/th>/);
  assert.match(html, /diff-count adicionado"><b>\+1<\/b> Adicionado<[\s\S]*diff-count removido"><b>−1<\/b> Removido</);
});

test('filtros por aba e por tipo; dados de teste ocultos por padrao e acessiveis nas secundarias', async () => {
  const h = harness(() => ({ body: sampleDiff }));
  openReport(h);
  await h.run('loadDiff(lastResponse.report, true)');

  h.run('diffSection = "scenarios"; diffFilter = "removido"');
  let html = h.run('renderDiff(lastResponse.report)');
  assert.match(html, /Pedido cancelado/);
  assert.doesNotMatch(html, /Pedido fechado/);
  assert.match(html, /data-diff-filter="removido" aria-pressed="true">Removidas</);

  h.run('diffFilter = "all"');
  html = h.run('renderDiff(lastResponse.report)');
  assert.doesNotMatch(html, /Payload com outra data/, 'variação só de dados de teste fica oculta');
  assert.match(html, /<button type="button" class="secondary diff-minor-toggle" data-diff-minor aria-pressed="false">Exibir alterações de documentação e dados de teste \(1\)/);

  h.run('diffMinor = true');
  html = h.run('renderDiff(lastResponse.report)');
  assert.match(html, /Alterações secundárias[\s\S]*Payload com outra data[\s\S]*dados de teste/);
  assert.match(html, /Ocultar alterações de documentação e dados de teste \(1\)/);

  h.run('diffSection = "summary"');
  html = h.run('renderDiff(lastResponse.report)');
  assert.match(html, /Observação até 500 caracteres/);
  assert.doesNotMatch(html, /Pedido/);
  assert.match(html, /<details class="diff-about"><summary>Sobre esta comparação<\/summary>[\s\S]*Validações:<\/b> Fonte: validações estruturadas\./);
});

test('alteracao so de texto da IA nao conta como comportamento e fica nas secundarias', async () => {
  const doc = item('alterado', 'documentacao', 'Regra: Pedido não pode ser fechado',
    [{ path: 'titulo', label: 'Título', impact: 'documentacao', before: 'Pedido não pode ser fechado', after: 'Pedido fechado é bloqueado' }]);
  const diff = diffOf([group('regras', 'Regras de negócio', []), group('documentacao', 'Documentação da IA', [doc])], []);
  let { h, html } = await page(diff);

  assert.doesNotMatch(html, /Pedido fechado é bloqueado|Documentação da IA/);
  assert.match(html, /diff-count alterado"><b>~0<\/b> Alterados/);
  assert.equal(occurrences(html, /class="diff-empty"/g), 1);
  assert.match(html, /diff-empty">Nenhuma alteração neste filtro\./);
  assert.match(h.run('diffTabLabel(lastResponse.report)'), /badge[^>]*>0</);

  ({ html } = await page(diff, { minor: true }));
  assert.match(html, /Alterações secundárias[\s\S]*<h4>Documentação da IA<\/h4>[\s\S]*diff-impact documentacao">documentação[\s\S]*Título:<\/span>/);
});

test('correspondencia incerta aparece no filtro proprio e nunca vira inclusao ou remocao', async () => {
  const uncertain = item('incerto', 'comportamento', 'Pedido sem itens', [{ path: 'esperado.http', label: 'HTTP esperado', impact: 'comportamento', before: 400, after: 422 }],
    { baseRef: 'CEN-04', currentRef: 'CEN-06', notes: ['Correspondência só pela origem no código: confira se é o mesmo item.'] });
  const diff = diffOf([], [group('matriz-estatica', 'Matriz estática', [uncertain])]);

  let { html } = await page(diff, { section: 'scenarios', filter: 'incerto' });
  assert.match(html, /diff-kind incerto">Incerto[\s\S]*<p class="diff-context">Não foi possível associar os itens com segurança\.<\/p>/);
  assert.match(html, /diff-count adicionado"><b>\+0<\/b>[\s\S]*diff-count removido"><b>−0<\/b>[\s\S]*diff-count alterado"><b>~0<\/b>[\s\S]*diff-count incerto"[^>]*><b>1<\/b> incerto/);
  assert.match(html, /Estrutura completa[\s\S]*Correspondência só pela origem no código/);

  for (const filter of ['adicionado', 'removido', 'alterado']) {
    ({ html } = await page(diff, { section: 'scenarios', filter }));
    assert.doesNotMatch(html, /Pedido sem itens/, filter);
    assert.equal(occurrences(html, /diff-empty">Nenhuma alteração neste filtro\./g), 1);
  }
});

test('runtime ausente e nao comparavel nunca aparecem como remocao; queda de confirmado e alerta, nao regressao', async () => {
  const alert = item('alterado', 'runtime', 'Observação com 600 caracteres', [
    { path: 'runtime.status', label: 'Status runtime', impact: 'runtime', before: 'confirmado', after: 'inconclusivo' },
    { path: 'runtime.http', label: 'HTTP observado', impact: 'runtime', before: 200, after: null },
  ], { alert: true, notes: ['Alerta: deixou de ser confirmado em runtime. Não é prova isolada de regressão (limites, dados ou ambiente podem explicar).'] });
  const diff = diffOf(
    [group('documentacao', 'Documentação da IA', [], { comparable: false, reason: 'Resumo sem IA na versão anterior: a documentação da IA não é comparável (nada foi removido).' })],
    [group('matriz-runtime', 'Matriz validada em runtime', [alert])]);

  let { html } = await page(diff);
  assert.match(html, /<div class="diff-off"><b>Documentação da IA · Não comparável<\/b> Resumo sem IA na versão anterior/);
  assert.doesNotMatch(html, /Removido<\/span>|diff-item removido/);

  ({ html } = await page(diff, { section: 'scenarios' }));
  const top = closed(cards(html)[0]);
  assert.match(top, /diff-item alterado alert[\s\S]*diff-alert[\s\S]*diff-impact runtime">runtime/);
  assert.match(top, /deixou de ser confirmado em runtime/);
  assert.match(top, /Status runtime:<\/span>\s*<span class="diff-hl del"><b>Antes<\/b>Confirmado[\s\S]*<b>Depois<\/b>Inconclusivo/);
  assert.doesNotMatch(top, /HTTP observado|Regressão|regrediu/);

  const offRuntime = diffOf([], [group('matriz-runtime', 'Matriz validada em runtime', [], { comparable: false, reason: 'Na versão anterior não houve validação em runtime: a matriz validada não é comparável (nenhum cenário foi removido).' })]);
  ({ html } = await page(offRuntime, { section: 'scenarios' }));
  assert.match(html, /Matriz validada em runtime · Não comparável/);
  assert.match(html, /diff-count removido"><b>−0<\/b>/);

  const offTab = { ...diffOf([], []), sections: [{ id: 'summary', title: 'Resumo', comparable: false, reason: 'Aba Resumo não gerada na versão anterior: não comparável (nada foi removido).', counts: counts(), groups: [] }] };
  ({ html } = await page(offTab));
  assert.match(html, /<div class="diff-off"><b>Não comparável<\/b> Aba Resumo não gerada/);
});

test('primeira analise, analise sem mudancas e filtro vazio tem uma unica mensagem', async () => {
  const responses = {
    a1: { analysisId: 'a1', hasBase: false, hasDifferences: false, message: 'Nenhuma análise anterior para comparar.', current: version('a1'), counts: counts(), notes: [], sections: [] },
    a2: { ...sampleDiff, hasDifferences: false, message: 'Nenhuma diferença encontrada.', counts: counts({ unchanged: 9 }),
      sections: [{ id: 'summary', title: 'Resumo', comparable: true, counts: counts(), groups: [{ id: 'regras', title: 'Regras de negócio', comparable: true, counts: counts({ unchanged: 3 }), notes: [], items: [] }] }] },
  };
  const h = harness(url => ({ body: responses[new URL(url, 'http://x').searchParams.get('analysisId')] }));

  openReport(h, 'a1');
  await h.run('loadDiff(lastResponse.report, true)');
  let html = h.run('renderDiff(lastResponse.report)');
  assert.match(html, /diff-empty">Nenhuma análise anterior para comparar\./);
  assert.match(html, /Atual: [^<]/, 'sem commit, a data identifica a versão');

  openReport(h, 'a2');
  await h.run('loadDiff(lastResponse.report, true)');
  html = h.run('diffSection = "summary"; diffFilter = "all"; renderDiff(lastResponse.report)');
  assert.equal(occurrences(html, /class="diff-empty"/g), 1);
  assert.match(html, /diff-empty">Nenhuma diferença encontrada\./);
  assert.doesNotMatch(html, /diff-group|Regras de negócio|igual/);

  html = h.run('diffFilter = "removido"; renderDiff(lastResponse.report)');
  assert.equal(occurrences(html, /class="diff-empty"/g), 1);
  assert.match(html, /diff-empty">Nenhuma alteração neste filtro\./);
});

test('conteudo do relatorio e escapado e as expansoes usam details/summary nativos', async () => {
  const xss = item('alterado', 'comportamento', '<img src=x onerror=alert(1)>', [
    { path: 'esperado.mensagens', label: '<i>Mensagens</i>', impact: 'comportamento', before: ['<script>a</script>'], after: ['<script>b</script>'] },
    { path: 'requisicao.corpo', label: 'Body', impact: 'dados-teste', before: { x: '<b>1</b>' }, after: { x: '<b>2</b>' } },
  ], { before: { t: '<svg/onload=1>' }, after: { t: '<svg/onload=2>' } });
  const { html } = await page(diffOf([], [group('matriz-estatica', 'Matriz estática', [xss])]), { section: 'scenarios' });

  assert.doesNotMatch(html, /<img|<script>|<svg|<i>Mensagens|<b>1<\/b>/);
  assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;[\s\S]*&lt;script&gt;a&lt;\/script&gt;[\s\S]*&lt;b&gt;2&lt;\/b&gt;[\s\S]*&lt;svg\/onload=2&gt;/);
  assert.match(html, /<details class="diff-more"><summary>Ver alterações<\/summary>[\s\S]*<details class="diff-full"><summary>Estrutura completa<\/summary>/);
  assert.match(html, /<button type="button" class="chip on" data-diff-section="scenarios" aria-pressed="true">/);
  // Telas estreitas: a tabela de campos vira uma lista, sem colunas obrigatórias.
  assert.match(fs.readFileSync(path.resolve(__dirname, '../../src/EndpointAnalyzer.Api/wwwroot/index.html'), 'utf8'),
    /@media \(max-width: 700px\) \{[\s\S]*?table\.diff-fields thead \{ display: none; \}/);
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
