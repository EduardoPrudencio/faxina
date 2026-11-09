'use strict';

// ───────────────────────── ponte com o C# ─────────────────────────
const bridge = (() => {
  const pending = new Map();
  const listeners = {};
  let seq = 0;

  function onMessage(msg) {
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.ok ? resolve(msg.result) : reject(new Error(msg.error || 'Erro desconhecido'));
    } else if (msg.event) {
      (listeners[msg.event] || []).forEach(fn => fn(msg.data));
    }
  }

  // Três jeitos de falar com o C#: a janela WebView2 (Windows), o servidor local com token (Linux)
  // ou, aberta num navegador comum sem token, os dados de exemplo do mock.js.
  const wv = window.chrome && window.chrome.webview;
  const token = httpToken();
  let transport;
  if (wv) {
    transport = { send: m => wv.postMessage(m) };
    wv.addEventListener('message', e => onMessage(e.data));
  } else if (token) {
    transport = { send: m => httpCall(m) };
    const events = new EventSource('/api/events?t=' + encodeURIComponent(token));
    events.onmessage = e => onMessage(JSON.parse(e.data));
    events.onerror = () => { if (events.readyState === EventSource.CLOSED) document.dispatchEvent(new Event('faxina:offline')); };
  } else {
    transport = window.MockHost.create(onMessage);
  }

  function httpToken() {
    if (wv) return null;
    const fromUrl = new URLSearchParams(location.search).get('t');
    let t = fromUrl;
    try {
      if (fromUrl) sessionStorage.setItem('faxina.token', fromUrl);
      else t = sessionStorage.getItem('faxina.token');
    } catch { }
    // Tira o token da barra de endereço (não aparece em prints nem no histórico).
    if (fromUrl) history.replaceState(null, '', location.pathname);
    return t;
  }

  async function httpCall(m) {
    try {
      const res = await fetch('/api/call', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Faxina-Token': token },
        body: JSON.stringify(m),
      });
      if (!res.ok) throw new Error(res.status === 401 ? 'Sessão expirada: abra de novo o endereço mostrado no terminal.' : 'Erro ' + res.status);
      onMessage(await res.json());
    } catch (e) {
      const msg = e instanceof TypeError ? 'O Faxina foi encerrado no terminal. Rode-o de novo para continuar.' : e.message;
      onMessage({ id: m.id, ok: false, error: msg });
    }
  }

  return {
    isMock: !wv && !token,
    call(method, params = {}) {
      const id = 'r' + (++seq);
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        transport.send({ id, method, params });
      });
    },
    on(evt, fn) { (listeners[evt] = listeners[evt] || []).push(fn); },
  };
})();

// ───────────────────────── ícones ─────────────────────────
const ICONS = {
  home: '<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M10 21v-6h4v6"/>',
  tree: '<rect x="3" y="3" width="8" height="8" rx="1.5"/><rect x="13" y="3" width="8" height="5" rx="1.5"/><rect x="13" y="10" width="8" height="11" rx="1.5"/><rect x="3" y="13" width="8" height="8" rx="1.5"/>',
  file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>',
  sparkle: '<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  terminal: '<path d="M4 17l6-5-6-5"/><path d="M12 19h8"/>',
  'chevron-up': '<path d="M6 15l6-6 6 6"/>',
  'chevron-right': '<path d="M9 6l6 6-6 6"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/>',
  shield: '<path d="M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6z"/>',
  'shield-check': '<path d="M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
  play: '<path d="M7 4v16l13-8z"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
  refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/>',
  trash: '<path d="M4 7h16"/><path d="M10 11v6M14 11v6"/><path d="M6 7l1 13h10l1-13"/><path d="M9 7V4h6v3"/>',
  recycle: '<path d="M7 19H4.8a1.8 1.8 0 0 1-1.6-2.7L5 13"/><path d="M11 19h8.2a1.8 1.8 0 0 0 1.6-2.7l-1.2-2"/><path d="M14 16l-3 3 3 3"/><path d="M8.3 13.3 5 13l-.3 3.3"/><path d="m9.3 5.6 1.1-1.9a1.8 1.8 0 0 1 3.1 0l4.1 7"/><path d="m14.4 10.4 3.3.3.3-3.3"/>',
  open: '<path d="M14 4h6v6"/><path d="M20 4l-9 9"/><path d="M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5"/>',
  hdd: '<rect x="3" y="13" width="18" height="7" rx="2"/><path d="M5 13l2.5-8h9L19 13"/><circle cx="17" cy="16.5" r=".8"/>',
  eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  cloud: '<path d="M7 18a5 5 0 1 1 .9-9.9A6 6 0 0 1 19 10a4 4 0 0 1-1 8z"/>',
  history: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/><path d="M12 7v5l3 2"/>',
  memory: '<rect x="3" y="7" width="18" height="10" rx="1.5"/><path d="M7 7V4M12 7V4M17 7V4M7 20v-3M12 20v-3M17 20v-3"/>',
  moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  alert: '<path d="M12 3l9.5 17h-19z"/><path d="M12 10v4"/><circle cx="12" cy="17" r=".6"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5"/><circle cx="12" cy="8" r=".6"/>',
  x: '<path d="M6 6l12 12M18 6L6 18"/>',
  box: '<path d="M21 8l-9-5-9 5 9 5z"/><path d="M3 8v8l9 5 9-5V8"/><path d="M12 13v8"/>',
  code: '<path d="M8 8l-5 4 5 4M16 8l5 4-5 4M14 4l-4 16"/>',
  windows: '<rect x="3" y="3" width="8" height="8"/><rect x="13" y="3" width="8" height="8"/><rect x="3" y="13" width="8" height="8"/><rect x="13" y="13" width="8" height="8"/>',
  docker: '<path d="M3 13h16c1.5 0 2.5-1 3-2-1-.5-2-.5-3 0-.3-1.3-1.2-2-2-2.5-.7 1-.7 2-.3 3"/><path d="M3 13c0 4 3 7 8 7 4.5 0 7.5-2.5 8.5-7"/><rect x="5" y="9" width="3" height="3"/><rect x="8.5" y="9" width="3" height="3"/><rect x="12" y="9" width="3" height="3"/><rect x="8.5" y="5.5" width="3" height="3"/>',
  apps: '<rect x="4" y="4" width="16" height="16" rx="3"/><path d="M9 4v16"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="M10.8 12.2 20 3"/><path d="M17 6l3 3"/><path d="M15 8l2 2"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 3.6-7 8-7s8 3 8 7"/>',
  lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
};
const icon = (name, cls = '') => `<span class="ico ${cls}"><svg viewBox="0 0 24 24">${ICONS[name] || ''}</svg></span>`;
function hydrateIcons(root = document) {
  root.querySelectorAll('[data-icon]').forEach(el => { el.innerHTML = `<svg viewBox="0 0 24 24">${ICONS[el.dataset.icon] || ''}</svg>`; el.classList.add('ico'); });
}

// ───────────────────────── estado ─────────────────────────
const PALETTE = ['var(--c1)', 'var(--c2)', 'var(--c3)', 'var(--c4)', 'var(--c5)', 'var(--c6)', 'var(--c7)', 'var(--c8)', 'var(--c9)'];
const OTHER = 'var(--c10)';
const CATEGORY_COLORS = {
  'Vídeos': 'var(--c4)', 'Imagens': 'var(--c3)', 'Áudio': 'var(--c6)', 'Documentos': 'var(--c5)',
  'Compactados': 'var(--c7)', 'Instaladores': 'var(--c8)', 'Discos virtuais': 'var(--c1)',
  'Bibliotecas e código': 'var(--c9)', 'Bancos de dados': 'var(--c2)', 'Sistema e cache': '#8d93a6', 'Outros': OTHER,
};
const CATEGORY_ICONS = { 'Windows': 'windows', 'Sistema': 'hdd', 'Programas': 'apps', 'Docker, WSL e VMs': 'docker', 'Desenvolvimento': 'code' };

const store = {
  get(k, d) { try { const v = localStorage.getItem('faxina.' + k); return v == null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('faxina.' + k, JSON.stringify(v)); } catch { } },
};

const state = {
  view: 'overview',
  info: null,
  scan: null,           // resumo da última análise
  node: null,           // pasta aberta no Explorar
  scanning: false,
  progress: null,
  scanTarget: null,
  filesFilter: 'Todos',
  cleanup: {
    targets: [], measures: {}, selections: {}, running: null,
    nm: { roots: store.get('nmRoots', null), idleDays: store.get('nmIdle', 90) },
  },
  apps: null, appsLoading: false, appsFilter: 'all', appsQuery: '', uninstalling: null,
  appsSort: store.get('appsSort', { by: 'size', desc: true }),
  account: null, changingPassword: false,
};

const $ = s => document.querySelector(s);
const view = name => document.getElementById('view-' + name);

// ───────────────────────── navegação ─────────────────────────
const TITLES = {
  overview: ['Visão geral', 'Como o espaço do seu disco está distribuído'],
  explore: ['Explorar disco', 'Veja o tamanho de cada pasta e entre nelas para achar o que pesa'],
  files: ['Maiores arquivos', 'Os 300 maiores arquivos encontrados na última análise'],
  cleanup: ['Limpeza', 'Coisas que podem ser apagadas com segurança, com quanto cada uma libera'],
  apps: ['Programas', 'Programas instalados, com tamanho e recomendações'],
  account: ['Conta', 'Senha do usuário deste computador'],
};

function go(name) {
  state.view = name;
  document.querySelectorAll('#nav button').forEach(b => b.classList.toggle('active', b.dataset.view === name));
  document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + name));
  $('#viewTitle').textContent = TITLES[name][0];
  $('#viewSub').textContent = TITLES[name][1];
  $('.main').scrollTop = 0;
  if (name === 'apps' && !state.apps && !state.appsLoading) loadApps();
  if (name === 'account') loadAccount();
  render();
}

function render() {
  renderSidebar();
  renderTopActions();
  ({ overview: renderOverview, explore: renderExplore, files: renderFiles, cleanup: renderCleanup, apps: renderApps, account: renderAccount })[state.view]();
}

function renderTopActions() {
  const el = $('#topActions');
  const a = [];
  if (state.view === 'overview' || state.view === 'explore') {
    a.push(state.scanning
      ? `<button class="btn" data-act="scan-cancel">${icon('stop')}Cancelar análise</button>`
      : `<button class="btn btn-primary" data-act="scan-system">${icon('play')}${state.scan ? 'Analisar de novo' : 'Analisar disco ' + sysDriveLetter()}</button>`);
  }
  if (state.view === 'cleanup') a.push(`<button class="btn" data-act="measure-all">${icon('refresh')}Medir de novo</button>`);
  if (state.view === 'apps') a.push(`<button class="btn" data-act="apps-reload" ${state.appsLoading ? 'disabled' : ''}>${icon('refresh')}Atualizar lista</button>`);
  el.innerHTML = a.join('');
}

// ───────────────────────── barra lateral ─────────────────────────
function sysDrive() {
  const i = state.info;
  if (!i) return null;
  return i.drives.find(d => d.name.toUpperCase() === i.systemDrive.toUpperCase()) || i.drives[0];
}
const sysDriveLetter = () => (state.info?.systemDrive || 'C:\\').replace('\\', '');
const isLinux = () => state.info?.platform === 'linux';
// Usado de verdade: no Linux, a reserva do ext4 para o root não conta como usada nem como livre.
const usedOf = d => d.total - d.free - (d.reserved || 0);
const ADMIN = () => (isLinux() ? 'root' : 'admin');
const adminButton = (label) =>
  `<button class="btn btn-sm" data-act="relaunch">${icon('shield')}${label || (isLinux() ? 'Liberar modo root' : 'Reabrir como admin')}</button>`;

function renderSidebar() {
  const d = sysDrive();
  if (d) {
    const used = usedOf(d), f = used / d.total;
    const color = f > .92 ? 'var(--danger)' : f > .8 ? 'var(--warn)' : 'var(--accent)';
    $('#miniDrive').innerHTML = `
      <div class="row"><span>${icon('hdd')} <b>${esc(d.name.replace('\\', ''))}</b></span><span class="muted">${pct(f)} usado</span></div>
      <div class="progress"><span style="width:${f * 100}%;background:${color}"></span></div>
      <div class="row" style="margin:8px 0 0"><span class="muted">Livre</span><b class="num">${fmtBytes(d.free)}</b></div>`;
  }
  const admin = state.info?.isAdmin;
  $('#adminBox').innerHTML = admin
    ? `<div class="admin-ok">${icon('shield-check')}${isLinux() ? 'Rodando como root' : 'Modo administrador'}</div>`
    : adminButton();
  const total = reclaimableTotal();
  $('#navCleanup').textContent = total > 0 ? fmtBytes(total) : '';
}

// ───────────────────────── visão geral ─────────────────────────
function renderOverview() {
  const el = view('overview');
  const info = state.info, d = sysDrive();
  if (!info || !d) { el.innerHTML = `<div class="card"><div class="skeleton" style="height:260px"></div></div>`; return; }

  const used = usedOf(d);
  const reserved = d.reserved || 0;
  const usedFrac = used / d.total;
  const reclaim = reclaimableTotal();
  const scanIsDrive = state.scan && sameDir(state.scan.root.path, info.systemDrive);

  el.innerHTML = `
    ${!info.isAdmin ? `<div class="callout info">${icon('info')}<div>${isLinux()
      ? 'Sem root, as limpezas do sistema (pacotes, logs, snaps) ficam bloqueadas e algumas pastas não podem ser lidas.'
      : 'Algumas informações (pontos de restauração, pastas do sistema) e algumas limpezas só aparecem com permissão de administrador.'}</div>${adminButton()}</div>` : ''}
    ${usedFrac > .9 ? `<div class="callout">${icon('alert')}<div><b>Seu disco está ${pct(usedFrac)} cheio.</b> Com pouco espaço livre o ${isLinux() ? 'sistema' : 'Windows'} fica lento e as atualizações falham. O ideal é manter pelo menos 15% livre.</div></div>` : ''}
    <div class="grid g-hero">
      <div class="card drive-card">
        <div class="card-head" style="width:100%;margin-bottom:0"><h3>Disco ${esc(d.name.replace('\\', ''))}</h3><span class="muted">${esc(d.label)} · ${esc(d.format)}</span></div>
        <div id="ovDonut"></div>
        <div class="legend">
          <div class="legend-row"><span class="sw" style="background:${usedFrac > .9 ? 'var(--danger)' : 'var(--accent)'}"></span><span class="lbl">Usado</span><span class="val">${fmtBytes(used)}</span><span class="pct">${pct(usedFrac)}</span></div>
          <div class="legend-row"><span class="sw" style="background:var(--free)"></span><span class="lbl">Livre</span><span class="val">${fmtBytes(d.free)}</span><span class="pct">${pct(d.free / d.total)}</span></div>
          ${reserved > 0 ? `<div class="legend-row" title="O ext4 guarda este espaço para o root não ficar sem disco"><span class="sw" style="background:var(--c10)"></span><span class="lbl">Reservado ao root</span><span class="val">${fmtBytes(reserved)}</span><span class="pct">${pct(reserved / d.total)}</span></div>` : ''}
        </div>
      </div>
      <div class="card">
        <div class="card-head"><h3>Para onde foi o espaço</h3>${scanIsDrive ? `<span class="muted">análise de ${fmtTime(state.scan.seconds)} · ${fmtNum(state.scan.files)} arquivos</span>` : ''}</div>
        <div id="ovComposition"></div>
      </div>
    </div>

    <div class="grid g-3" style="margin-top:16px">
      ${statCard('hdd', 'var(--free)', 'var(--free-soft)', 'Livre agora', fmtBytes(d.free), `${pct(d.free / d.total)} de ${fmtBytes(d.total)}`)}
      ${statCard('sparkle', 'var(--accent)', 'var(--accent-soft)', 'Pode ser liberado', reclaim > 0 ? fmtBytes(reclaim) : measuringAny() ? '<span class="spinner"></span>' : '—',
        `<a href="#" data-act="go-cleanup" style="color:var(--accent);text-decoration:none;font-weight:600">Ver limpezas →</a>`)}
      ${scanIsDrive
        ? statCard('folder', 'var(--c3)', 'var(--warn-soft)', 'Maior pasta', esc(biggestLeaf(state.scan.root)?.name || '—'), fmtBytes(biggestLeaf(state.scan.root)?.size || 0))
        : statCard('cloud', 'var(--c5)', 'var(--accent-soft)', 'Arquivos analisados', state.scan ? fmtNum(state.scan.files) : '—', state.scan ? 'em ' + esc(state.scan.root.path) : 'rode uma análise')}
    </div>

    <div class="grid g-2" style="margin-top:16px">
      <div class="card">
        <div class="card-head"><h3>${esc(info.hiddenTitle || 'Espaço que o Explorer não mostra')}</h3>${icon('eye')}</div>
        <div class="hidden-list">${hiddenSpaceRows()}</div>
      </div>
      <div class="card">
        <div class="card-head"><h3>Maiores oportunidades</h3><a href="#" class="muted" data-act="go-cleanup" style="text-decoration:none">ver todas</a></div>
        <div id="ovOpps"></div>
      </div>
    </div>
    ${info.drives.length > 1 ? `<div class="card" style="margin-top:16px"><div class="card-head"><h3>Outros discos</h3></div><div class="grid g-3">${info.drives.filter(x => x !== d).map(x => `
      <div><div class="row" style="justify-content:space-between;margin-bottom:8px"><b>${esc(x.name)} ${esc(x.label)}</b><span class="muted">${fmtBytes(x.free)} livres</span></div>
      <div class="progress"><span style="width:${usedOf(x) / x.total * 100}%"></span></div></div>`).join('')}</div></div>` : ''}`;

  Charts.donut($('#ovDonut'), [
    { label: 'Usado', value: used, color: usedFrac > .9 ? 'var(--danger)' : 'var(--accent)' },
    { label: 'Livre', value: d.free, color: 'var(--free)' },
    { label: 'Reservado ao root', value: reserved, color: 'var(--c10)' },
  ], { center: `<div class="big">${pct(usedFrac)}</div><div class="small">ocupado</div>` });

  renderComposition($('#ovComposition'), scanIsDrive, used);
  renderOpportunities($('#ovOpps'));
}

function statCard(ic, color, bg, label, value, hint) {
  return `<div class="card stat-card">
    <div class="stat-icon" style="background:${bg};color:${color}">${icon(ic)}</div>
    <div class="stat"><span class="label">${label}</span><span class="value">${value}</span><span class="hint">${hint}</span></div>
  </div>`;
}

function renderComposition(el, scanIsDrive, used) {
  if (state.scanning) {
    el.innerHTML = progressBlock();
    return;
  }
  if (!scanIsDrive) {
    el.innerHTML = `<div class="empty" style="padding:24px 10px">${icon('tree')}<h3>Descubra o que está ocupando o disco</h3>
      <p>A análise percorre todas as pastas do ${sysDriveLetter()} e mostra quanto cada uma ocupa. Leva de 1 a 3 minutos.</p>
      <button class="btn btn-primary" data-act="scan-system">${icon('play')}Analisar disco ${sysDriveLetter()}</button></div>`;
    return;
  }
  const root = state.scan.root;
  const kids = root.children.filter(c => c.size > 0);
  const top = kids.slice(0, 7);
  const restKids = kids.slice(7).reduce((s, c) => s + c.size, 0) + (root.others?.size || 0);
  const shadowUsed = state.info.shadow?.used || 0;
  const segs = top.map((c, i) => ({ label: c.name, value: c.size, color: PALETTE[i % PALETTE.length], hint: c.path }));
  if (restKids > 0) segs.push({ label: 'Outras pastas', value: restKids, color: OTHER });
  if (root.filesSize > 0) segs.push({ label: 'Arquivos na raiz', value: root.filesSize, color: '#8d93a6', hint: state.info.rootFilesHint });
  if (shadowUsed > 0) segs.push({ label: 'Pontos de restauração', value: shadowUsed, color: 'var(--c8)', hint: 'Cópias de sombra do Windows' });
  const unknown = used - root.size - shadowUsed;
  if (unknown > used * 0.02) segs.push({
    ...unknownSegment(),
    value: unknown, color: 'repeating-linear-gradient(45deg, var(--track), var(--track) 5px, var(--border-strong) 5px, var(--border-strong) 10px)',
  });
  Charts.stacked(el, segs, Math.max(used, segs.reduce((s, x) => s + x.value, 0)));
  // a legenda não sabe desenhar o gradiente listrado como cor sólida
  el.querySelectorAll('.sw').forEach(sw => { if (sw.style.background.includes('gradient')) sw.style.border = '1px solid var(--border-strong)'; });
}

// O que sobra entre "usado" e o que a análise encontrou.
function unknownSegment() {
  const admin = state.info.isAdmin;
  if (isLinux()) return admin
    ? { label: 'Não identificado', hint: 'Metadados do sistema de arquivos e arquivos apagados que algum programa ainda mantém abertos' }
    : { label: 'Sem acesso (requer root)', hint: 'Pastas que só o root consegue ler' };
  return admin
    ? { label: 'Protegido pelo sistema', hint: 'Pastas que nem o administrador consegue ler, como System Volume Information' }
    : { label: 'Sem acesso (requer admin)', hint: 'Pastas que só o administrador consegue ler' };
}

function hiddenSpaceRows() {
  const rows = (state.info.hidden || []).map(h => row(h.icon, h.title, h.sub, h.bytes, h.faded));
  if (state.scan?.cloudBytes) rows.push(row('cloud', 'Arquivos só na nuvem (OneDrive)', 'Aparecem no Explorer, mas não ocupam o disco', state.scan.cloudBytes, true));
  return rows.join('');

  function row(ic, title, sub, bytes, faded) {
    return `<div class="hidden-item">${icon(ic)}<div class="txt"><b>${esc(title)}</b><small>${esc(sub)}</small></div>
      <span class="size" style="${faded ? 'color:var(--muted)' : ''}">${bytes == null ? '—' : fmtBytes(bytes)}</span></div>`;
  }
}

function renderOpportunities(el) {
  const list = state.cleanup.targets
    .map(t => ({ t, bytes: targetBytes(t) }))
    .filter(x => x.bytes > 0)
    .sort((a, b) => b.bytes - a.bytes)
    .slice(0, 6);
  if (!list.length) {
    el.innerHTML = measuringAny()
      ? `<div class="hbars">${[1, 2, 3, 4].map(() => `<div class="skeleton" style="height:30px"></div>`).join('')}</div>`
      : `<p class="muted">Nada relevante para limpar agora.</p>`;
    return;
  }
  Charts.hbars(el, list.map((x, i) => ({ label: x.t.title, value: x.bytes, color: PALETTE[i % PALETTE.length] })));
}

function biggestLeaf(root) {
  const n = root.children[0];
  return n ? { name: n.name, size: n.size } : null;
}

// ───────────────────────── análise ─────────────────────────
async function startScan(path) {
  state.scanTarget = path;
  state.scanning = true;
  state.progress = { files: 0, bytes: 0, current: path, elapsed: 0 };
  render();
  try { await bridge.call('scan.start', { path }); }
  catch (e) { state.scanning = false; toast(e.message, 'err'); render(); }
}

bridge.on('scan.progress', p => {
  state.progress = p;
  const el = document.querySelectorAll('.scan-progress');
  if (!el.length) return render();
  el.forEach(x => x.outerHTML = progressBlock());
});
bridge.on('scan.done', summary => {
  state.scanning = false;
  state.scan = summary;
  state.node = summary.root;
  toast(`Análise concluída: ${fmtNum(summary.files)} arquivos, ${fmtBytes(summary.bytes)}`, 'ok');
  refreshInfo();
  render();
});
bridge.on('scan.cancelled', () => { state.scanning = false; toast('Análise cancelada'); render(); });
bridge.on('scan.error', e => { state.scanning = false; toast(e.message, 'err'); render(); });

function progressBlock() {
  const p = state.progress || {};
  return `<div class="scan-progress">
    <div class="progress indeterminate"><span></span></div>
    <div class="nums">
      <div class="stat"><span class="label">Arquivos</span><span class="value num" style="font-size:20px">${fmtNum(p.files || 0)}</span></div>
      <div class="stat"><span class="label">Tamanho somado</span><span class="value num" style="font-size:20px">${fmtBytes(p.bytes || 0)}</span></div>
      <div class="stat"><span class="label">Tempo</span><span class="value num" style="font-size:20px">${fmtTime(p.elapsed || 0)}</span></div>
    </div>
    <div class="current" title="${esc(p.current)}">${esc(p.current || '')}</div>
  </div>`;
}

async function openNode(path) {
  try {
    state.node = await bridge.call('scan.node', { path });
    render();
    $('.main').scrollTop = 0;
  } catch (e) { toast(e.message, 'err'); }
}

function renderExplore() {
  const el = view('explore');
  const drives = state.info?.drives || [];
  const target = state.scanTarget || state.info?.systemDrive || 'C:\\';
  const isDrive = drives.some(d => sameDir(d.name, target));
  const toolbar = `<div class="toolbar">
      <select class="input" id="scanTarget" ${state.scanning ? 'disabled' : ''}>
        ${drives.map(d => `<option value="${esc(d.name)}" ${sameDir(d.name, target) ? 'selected' : ''}>${esc(d.name)} ${esc(d.label)} — ${fmtBytes(usedOf(d))} usados</option>`).join('')}
        ${!isDrive ? `<option value="${esc(target)}" selected>${esc(target)}</option>` : ''}
      </select>
      <button class="btn" data-act="pick-folder" ${state.scanning ? 'disabled' : ''}>${icon('folder')}Outra pasta…</button>
      ${state.scanning
        ? `<button class="btn" data-act="scan-cancel">${icon('stop')}Cancelar</button>`
        : `<button class="btn btn-primary" data-act="scan-selected">${icon('play')}Analisar</button>`}
    </div>`;

  if (state.scanning) {
    el.innerHTML = toolbar + `<div class="card"><div class="card-head"><h3>Analisando ${esc(state.scanTarget)}</h3><span class="spinner"></span></div>${progressBlock()}</div>`;
    return;
  }
  if (!state.scan || !state.node) {
    el.innerHTML = toolbar + `<div class="card"><div class="empty">${icon('tree')}<h3>Nenhuma análise ainda</h3>
      <p>Escolha um disco ou uma pasta e clique em <b>Analisar</b>. Você vai ver um mapa com o tamanho de cada pasta e poderá entrar nelas.</p></div></div>`;
    return;
  }

  const n = state.node;
  const kids = n.children.filter(c => !c.isMount);
  const mounts = n.children.filter(c => c.isMount);
  const listTotal = n.size || 1;
  const cats = state.scan.categories.filter(c => c.bytes > 0);

  el.innerHTML = toolbar + `
    <div class="crumbs">${n.breadcrumb.map((b, i) => `${i ? `<span class="sep">${icon('chevron-right')}</span>` : ''}<button data-node="${esc(b.path)}">${esc(b.name)}</button>`).join('')}</div>
    <div class="grid g-explore">
      <div class="card">
        <div class="card-head"><h3>${esc(n.name)}</h3><span class="muted num">${fmtBytes(n.size)} · ${fmtNum(n.files)} arquivos · ${fmtNum(n.dirs)} pastas</span></div>
        <div id="tm"></div>
        <p class="muted" style="font-size:12.5px;margin:10px 0 0">Clique numa área para entrar na pasta. ${n.filesSize > 0 ? `Arquivos soltos nesta pasta: <b>${fmtBytes(n.filesSize)}</b>.` : ''}</p>
      </div>
      <div class="card">
        <div class="card-head"><h3>Tipos de arquivo</h3><span class="muted">análise inteira</span></div>
        <div id="catDonut" style="display:flex;justify-content:center;margin-bottom:18px"></div>
        <div class="legend">${cats.slice(0, 9).map(c => `
          <div class="legend-row"><span class="sw" style="background:${CATEGORY_COLORS[c.name] || OTHER}"></span><span class="lbl">${esc(c.name)}</span><span class="val">${fmtBytes(c.bytes)}</span></div>`).join('')}</div>
      </div>
    </div>

    <div class="card" style="margin-top:16px;padding:16px 10px 6px">
      <table class="list">
        <thead><tr><th>Nome</th><th class="bar-cell">Proporção</th><th class="r">Tamanho</th><th class="r">Arquivos</th><th class="r">Alterada</th><th></th></tr></thead>
        <tbody>
          ${kids.map((c, i) => `
          <tr class="${c.hasChildren ? 'clickable' : ''}" ${c.hasChildren ? `data-node="${esc(c.path)}"` : ''}>
            <td class="grow"><div class="name-cell">${icon(c.isLink ? 'link' : 'folder', 'folder')}<div class="txt"><b>${esc(c.name)}</b>${c.isLink ? '<small>atalho de pasta (não contado)</small>' : ''}</div></div></td>
            <td class="bar-cell"><div class="bar"><span style="width:${c.size / listTotal * 100}%;background:${i < 9 ? PALETTE[i] : OTHER}"></span></div></td>
            <td class="r size-cell">${fmtBytes(c.size)}</td>
            <td class="r muted num">${fmtNum(c.files)}</td>
            <td class="r muted num">${fmtDate(c.modified)}</td>
            <td>${rowActions(c.path)}</td>
          </tr>`).join('')}
          ${n.filesSize > 0 ? `<tr><td class="grow"><div class="name-cell">${icon('file')}<div class="txt"><b class="muted">Arquivos nesta pasta</b></div></div></td><td class="bar-cell"><div class="bar"><span style="width:${n.filesSize / listTotal * 100}%;background:#8d93a6"></span></div></td><td class="r size-cell">${fmtBytes(n.filesSize)}</td><td></td><td></td><td></td></tr>` : ''}
          ${n.others ? `<tr><td colspan="2" class="muted">+ ${fmtNum(n.others.count)} pastas menores</td><td class="r size-cell">${fmtBytes(n.others.size)}</td><td colspan="3"></td></tr>` : ''}
        </tbody>
      </table>
      ${!kids.length ? `<div class="empty" style="padding:20px">Esta pasta não tem subpastas.</div>` : ''}
      ${mounts.length ? `<p class="muted" style="font-size:12.5px;margin:10px 10px 6px">Não contados (outros sistemas de arquivos montados aqui): ${mounts.map(m => esc(m.name)).join(', ')}.</p>` : ''}
    </div>

    ${state.scan.heavy?.length ? `<div class="card" style="margin-top:16px">
      <div class="card-head"><h3>Pastas típicas de desenvolvimento</h3><span class="muted">somadas na análise inteira</span></div>
      <div id="heavy"></div></div>` : ''}`;

  const tmItems = kids.filter(c => c.size > 0 && !c.isLink && !c.isMount).map((c, i) => ({
    label: c.name, value: c.size, color: i < 9 ? PALETTE[i] : OTHER, path: c.path, clickable: c.hasChildren,
    sub: c.preview?.length ? c.preview.slice(0, 4).map(p => `${esc(p.name)} · ${fmtBytes(p.size)}`).join('<br>') : '',
  }));
  if (n.filesSize > 0) tmItems.push({ label: 'Arquivos nesta pasta', value: n.filesSize, color: '#8d93a6', clickable: false });
  Charts.treemap($('#tm'), tmItems, { onClick: cell => openNode(cell.path) });

  Charts.donut($('#catDonut'), cats.map(c => ({ label: c.name, value: c.bytes, color: CATEGORY_COLORS[c.name] || OTHER })),
    { size: 180, thickness: 22, center: `<div class="big" style="font-size:22px">${fmtBytes(state.scan.bytes)}</div><div class="small">analisados</div>` });

  if (state.scan.heavy?.length) {
    const labels = { node_modules: 'node_modules (dependências JS)', '.git': '.git (histórico do Git)', bin: 'bin (saída de build)', obj: 'obj (intermediários .NET)', build: 'build', '.gradle': '.gradle', __pycache__: '__pycache__', '.venv': '.venv (Python)', venv: 'venv (Python)' };
    Charts.hbars($('#heavy'), state.scan.heavy.map((h, i) => ({ label: `${labels[h.name] || h.name} · ${fmtNum(h.count)} pasta(s)`, value: h.bytes, color: PALETTE[i % PALETTE.length] })));
  }
}

function rowActions(path) {
  const p = esc(path);
  return `<div class="row-actions">
    <button class="btn btn-ghost btn-icon btn-sm" title="${isLinux() ? 'Abrir pasta' : 'Abrir no Explorer'}" data-reveal="${p}">${icon('open')}</button>
    <button class="btn btn-ghost btn-icon btn-sm" title="Mover para a Lixeira" data-delete="${p}">${icon('recycle')}</button>
    <button class="btn btn-ghost btn-icon btn-sm" title="Excluir permanentemente" data-delete="${p}" data-permanent="1">${icon('trash')}</button>
  </div>`;
}

async function deletePath(path, permanent) {
  const ok = await confirmModal({
    title: permanent ? 'Excluir permanentemente?' : 'Mover para a Lixeira?',
    icon: permanent ? 'trash' : 'recycle',
    body: `<p><code>${esc(path)}</code></p>${permanent
      ? '<p>O item será apagado de vez, sem passar pela Lixeira. <b>Não dá para desfazer.</b></p>'
      : '<p>Vai para a Lixeira e pode ser restaurado. Lembre que, enquanto estiver lá, <b>continua ocupando espaço</b>.</p>'}`,
    confirm: permanent ? 'Excluir' : 'Mover para a Lixeira', danger: permanent,
  });
  if (!ok) return;
  try {
    const r = await bridge.call('fs.delete', { path, permanent });
    if (r.ok) toast(`${permanent ? 'Excluído' : 'Movido para a Lixeira'}: ${fmtBytes(r.freed)}`, 'ok');
    else toast(r.message, 'err');
    if (state.node) state.node = await bridge.call('scan.node', { path: state.node.path });
    state.scan = await bridge.call('scan.summary');
    refreshInfo();
    render();
  } catch (e) { toast(e.message, 'err'); }
}

// ───────────────────────── maiores arquivos ─────────────────────────
function renderFiles() {
  const el = view('files');
  if (!state.scan) {
    el.innerHTML = `<div class="card"><div class="empty">${icon('file')}<h3>Rode uma análise primeiro</h3><p>A lista dos maiores arquivos é montada durante a análise do disco.</p>
      <button class="btn btn-primary" data-act="scan-system">${icon('play')}Analisar disco ${sysDriveLetter()}</button></div></div>`;
    return;
  }
  const files = state.scan.topFiles;
  const counts = {};
  files.forEach(f => { counts[f.category] = (counts[f.category] || 0) + f.size; });
  const cats = ['Todos', ...Object.keys(counts).sort((a, b) => counts[b] - counts[a])];
  const shown = state.filesFilter === 'Todos' ? files : files.filter(f => f.category === state.filesFilter);
  const max = shown[0]?.size || 1;
  el.innerHTML = `
    <div class="toolbar"><div class="chips">${cats.map(c => `<button class="chip ${state.filesFilter === c ? 'active' : ''}" data-files-filter="${esc(c)}">
      ${c !== 'Todos' ? `<span class="dot" style="background:${CATEGORY_COLORS[c] || OTHER}"></span>` : ''}${esc(c)}${c !== 'Todos' ? ` · ${fmtBytes(counts[c])}` : ''}</button>`).join('')}</div></div>
    <div class="card" style="padding:16px 10px 6px">
      <table class="list">
        <thead><tr><th>Arquivo</th><th class="bar-cell">Tamanho relativo</th><th class="r">Tamanho</th><th class="r">Alterado</th><th></th></tr></thead>
        <tbody>${shown.map(f => `
          <tr>
            <td class="grow"><div class="name-cell">${icon('file')}<div class="txt"><b>${esc(f.name)}</b><small title="${esc(f.path)}">${esc(dirName(f.path))}</small></div></div></td>
            <td class="bar-cell"><div class="bar"><span style="width:${f.size / max * 100}%;background:${CATEGORY_COLORS[f.category] || OTHER}"></span></div></td>
            <td class="r size-cell">${fmtBytes(f.size)}</td>
            <td class="r muted num">${fmtDate(f.modified)}</td>
            <td>${rowActions(f.path)}</td>
          </tr>`).join('')}</tbody>
      </table>
    </div>`;
}

// ───────────────────────── limpeza ─────────────────────────
async function loadCleanup() {
  try {
    const r = await bridge.call('cleanup.list');
    state.cleanup.targets = r.targets;
    if (!state.cleanup.nm.roots || !state.cleanup.nm.roots.length) state.cleanup.nm.roots = r.projectRoots;
    measureAll();
  } catch (e) { toast(e.message, 'err'); }
}

function measureAll() { state.cleanup.targets.forEach(t => measure(t.id)); }

async function measure(id) {
  const c = state.cleanup;
  c.measures[id] = { loading: true };
  if (state.view === 'cleanup') renderCleanup();
  try {
    const opts = id === 'node-modules' ? { roots: c.nm.roots, idleDays: c.nm.idleDays } : undefined;
    const m = await bridge.call('cleanup.measure', { id, options: opts });
    c.measures[id] = m;
    c.selections[id] = new Set((m.items || []).filter(i => i.selected).map(i => i.path));
  } catch (e) {
    c.measures[id] = { error: e.message, available: true };
  }
  if (state.view === 'cleanup') renderCleanup();
  else if (state.view === 'overview') renderOverview();
  renderSidebar();
}

function targetBytes(t) {
  const m = state.cleanup.measures[t.id];
  if (!m || m.loading || m.error || m.available === false) return 0;
  if (t.selectable && m.items) {
    const sel = state.cleanup.selections[t.id] || new Set();
    if (m.unknown) return 0;
    return m.items.filter(i => sel.has(i.path)).reduce((s, i) => s + i.size, 0);
  }
  return m.unknown ? 0 : (m.bytes || 0);
}
const reclaimableTotal = () => state.cleanup.targets.reduce((s, t) => s + targetBytes(t), 0);
const measuringAny = () => Object.values(state.cleanup.measures).some(m => m.loading) || !state.cleanup.targets.length;

function renderCleanup() {
  const el = view('cleanup');
  const c = state.cleanup;
  if (!c.targets.length) { el.innerHTML = `<div class="card"><div class="skeleton" style="height:120px"></div></div>`; return; }
  const total = reclaimableTotal();
  const pending = Object.values(c.measures).filter(m => m.loading).length;
  const groups = {};
  c.targets.forEach(t => (groups[t.category] = groups[t.category] || []).push(t));
  const admin = state.info?.isAdmin;

  el.innerHTML = `
    <div class="card cleanup-hero">
      <div class="stat"><span class="label">${icon('sparkle')}Pode ser liberado</span><span class="value num">${fmtBytes(total)}</span>
      <span class="hint">${pending ? `<span class="spinner"></span> medindo ${pending} item(ns)…` : 'soma dos itens marcados abaixo'}</span></div>
      <div class="spacer"></div>
      ${!admin ? `<div style="max-width:360px;font-size:13px;color:var(--text-2);line-height:1.45">${isLinux()
        ? 'As limpezas do sistema (pacotes, logs, snaps, kernels antigos) precisam de root.'
        : 'Várias limpezas do Windows (pontos de restauração, hibernação, discos do Docker) precisam de administrador.'}<br>
        <div style="margin-top:8px">${adminButton()}</div></div>` : ''}
    </div>
    ${Object.entries(groups).map(([cat, list]) => {
      const sum = list.reduce((s, t) => s + targetBytes(t), 0);
      const sorted = [...list].sort((a, b) => avail(b) - avail(a) || targetBytes(b) - targetBytes(a));
      return `<div class="cat-title">${icon(CATEGORY_ICONS[cat] || 'box')}<h2>${esc(cat)}</h2><span class="muted">${sum > 0 ? fmtBytes(sum) : ''}</span></div>
        <div class="cleanup-grid">${sorted.map(cleanupCard).join('')}</div>`;
    }).join('')}`;

  function avail(t) { const m = c.measures[t.id]; return m && m.available === false ? 0 : 1; }
}

function cleanupCard(t) {
  const c = state.cleanup;
  const m = c.measures[t.id] || { loading: true };
  const admin = state.info?.isAdmin;
  const bytes = targetBytes(t);
  const running = c.running === t.id;
  const unavailable = m.available === false;
  const sel = c.selections[t.id] || new Set();

  let sizeHtml;
  if (m.loading) sizeHtml = `<span class="spinner"></span>`;
  else if (m.error) sizeHtml = `<span class="size zero">erro</span>`;
  else if (unavailable) sizeHtml = `<span class="size zero">—</span>`;
  else if (m.unknown) sizeHtml = `<span class="size zero">${t.selectable && m.items ? fmtBytes(m.items.filter(i => sel.has(i.path)).reduce((s, i) => s + i.size, 0)) : '?'}</span>`;
  else sizeHtml = `<span class="size ${bytes ? '' : 'zero'}">${fmtBytes(bytes)}</span>`;

  const needsAdmin = t.requiresAdmin && !admin;
  const canRun = !m.loading && !unavailable && !running && !c.running && (m.unknown ? (!t.selectable || sel.size > 0) : bytes > 0);
  let button;
  if (running) button = `<button class="btn btn-primary btn-sm" disabled><span class="spinner" style="border-top-color:#fff"></span>Limpando…</button>`;
  else if (needsAdmin && !unavailable) button = adminButton(`Requer ${ADMIN()}`);
  else button = `<button class="btn ${t.risk === 'atencao' ? 'btn-primary' : 'btn-success'} btn-sm" data-run="${t.id}" ${canRun ? '' : 'disabled'}>${icon('sparkle')}Limpar</button>`;

  const items = t.selectable && m.items && m.items.length ? `
    <div class="items">${m.items.map(i => `
      <label class="item" title="${esc(i.path)}">
        <input type="checkbox" data-sel="${t.id}" value="${esc(i.path)}" ${sel.has(i.path) ? 'checked' : ''} ${running ? 'disabled' : ''}>
        <span class="lbl">${esc(i.label || i.path)}</span>
        ${i.lastActivity ? `<span class="when">${relDays(i.lastActivity)}</span>` : ''}
        <span class="sz">${fmtBytes(i.size)}</span>
      </label>`).join('')}</div>` : '';

  const nmOptions = t.id === 'node-modules' ? `
    <div class="nm-options">
      <span class="muted">Pastas:</span>
      <span class="badge" title="${esc((c.nm.roots || []).join('\n'))}">${esc((c.nm.roots || []).join(', ') || 'nenhuma')}</span>
      <button class="btn btn-ghost btn-sm" data-act="nm-pick">${icon('folder')}Trocar</button>
      <span class="spacer"></span>
      <span class="muted">Parado há</span>
      <select class="input" data-act-change="nm-idle">${[30, 60, 90, 180, 365].map(d => `<option value="${d}" ${c.nm.idleDays === d ? 'selected' : ''}>${d} dias</option>`).join('')}</select>
    </div>` : '';

  return `<div class="card ccard ${running ? 'selected-run' : ''} ${unavailable ? 'unavailable' : ''}">
    <div class="head">
      <div class="txt"><h4>${esc(t.title)}</h4><p class="desc">${esc(t.description)}</p></div>
      ${sizeHtml}
    </div>
    ${m.note || m.error ? `<div class="note">${esc(m.error || m.note)}</div>` : ''}
    ${nmOptions}
    ${items}
    <div class="foot">
      ${t.risk === 'atencao' ? `<span class="badge warn">${icon('alert')}Atenção</span>` : `<span class="badge ok">${icon('check')}Seguro</span>`}
      ${t.requiresAdmin ? `<span class="badge accent">${icon('shield')}${isLinux() ? 'Root' : 'Admin'}</span>` : ''}
      ${button}
    </div>
  </div>`;
}

async function runCleanup(id) {
  const t = state.cleanup.targets.find(x => x.id === id);
  const m = state.cleanup.measures[id];
  const sel = state.cleanup.selections[id];
  const chosen = t.selectable && m.items ? m.items.filter(i => sel.has(i.path)) : null;
  const ok = await confirmModal({
    title: t.title, icon: t.risk === 'atencao' ? 'alert' : 'sparkle',
    body: `<p>${esc(t.description)}</p>
      ${chosen ? `<ul>${chosen.slice(0, 8).map(i => `<li>${esc(i.label || i.path)} — <b>${fmtBytes(i.size)}</b></li>`).join('')}${chosen.length > 8 ? `<li>e mais ${chosen.length - 8}…</li>` : ''}</ul>` : ''}
      ${targetBytes(t) > 0 ? `<p>Estimativa: <b>${fmtBytes(targetBytes(t))}</b>.</p>` : ''}`,
    confirm: 'Limpar agora', danger: t.risk === 'atencao',
  });
  if (!ok) return;

  state.cleanup.running = id;
  openDrawer(true);
  setDrawerStatus(`<span class="spinner"></span>${esc(t.title)}`);
  renderCleanup();
  try {
    const r = await bridge.call('cleanup.run', { id, selected: chosen ? chosen.map(i => i.path) : undefined });
    toast(`${t.title}: ${fmtBytes(r.freed)} liberados`, 'ok');
    setDrawerStatus(`${icon('check')}Liberou ${fmtBytes(r.freed)}`);
  } catch (e) {
    toast(e.message, 'err');
    appendLog('✖ ' + e.message, 'l-err');
    setDrawerStatus('');
  }
  state.cleanup.running = null;
  await refreshInfo();
  measure(id);
}

// ───────────────────────── programas ─────────────────────────
async function loadApps() {
  state.appsLoading = true;
  render();
  try { state.apps = await bridge.call('apps.list'); }
  catch (e) { toast(e.message, 'err'); }
  state.appsLoading = false;
  render();
}

const SOURCE_LABELS = { win32: 'Programas', store: 'Microsoft Store', apt: 'apt', rpm: 'rpm', snap: 'Snap', flatpak: 'Flatpak' };

const VERDICTS = {
  remover: ['danger', 'Pode remover'],
  opcional: ['warn', 'Opcional'],
  manter: ['ok', 'Manter'],
};

const byName = new Intl.Collator('pt-BR', { sensitivity: 'base', numeric: true });
const APP_SORTS = {
  name: (a, b) => byName.compare((a.name || '').trim(), (b.name || '').trim()),
  size: (a, b) => a.size - b.size || byName.compare(b.name || '', a.name || ''),
};

// Cabeçalho que ordena a lista: clicar de novo inverte a ordem
function sortHeader(by, label, cls = '') {
  const { by: cur, desc } = state.appsSort;
  const arrow = cur === by ? `<span class="sort-arrow">${desc ? '↓' : '↑'}</span>` : '';
  return `<th class="${cls}"><button class="sort-btn ${cur === by ? 'active' : ''}" data-apps-sort="${by}"
    title="Ordenar por ${label.toLowerCase()}">${label}${arrow}</button></th>`;
}

function renderApps() {
  const el = view('apps');
  if (state.appsLoading && !state.apps) {
    el.innerHTML = `<div class="card"><div class="row muted" style="margin-bottom:16px"><span class="spinner"></span>Lendo programas instalados e calculando tamanhos…</div>
      ${[1, 2, 3, 4, 5, 6].map(() => `<div class="skeleton" style="height:44px;margin-bottom:10px"></div>`).join('')}</div>`;
    return;
  }
  const apps = state.apps || [];
  const q = state.appsQuery.trim().toLowerCase();
  const isDell = a => /dell/i.test(a.name + ' ' + (a.publisher || ''));
  const filters = { all: ['Todos', () => true], remover: ['Recomendo remover', a => a.verdict === 'remover'] };
  if (apps.some(isDell)) filters.dell = ['Dell', isDell];
  // Um filtro por origem que existir nesta máquina (Store no Windows; apt, Snap, Flatpak… no Linux)
  const sources = [...new Set(apps.map(a => a.source))];
  if (sources.length > 1 || isLinux()) sources.filter(s => s !== 'win32').forEach(s => { filters['src-' + s] = [SOURCE_LABELS[s] || s, a => a.source === s]; });
  filters.big = ['Acima de 500 MB', a => a.size > 500 * 2 ** 20];
  if (!filters[state.appsFilter]) state.appsFilter = 'all';

  const { by, desc } = state.appsSort;
  const shown = apps.filter(filters[state.appsFilter][1]).filter(a => !q || (a.name + ' ' + (a.publisher || '')).toLowerCase().includes(q))
    .sort((a, b) => (desc ? -1 : 1) * APP_SORTS[by](a, b));
  const removable = apps.filter(a => a.verdict === 'remover');
  const max = Math.max(1, ...shown.map(a => a.size));
  const total = apps.reduce((s, a) => s + a.size, 0);
  const third = filters.dell
    ? statCard('box', 'var(--c5)', 'var(--accent-soft)', 'Programas da Dell', fmtNum(apps.filter(isDell).length), `somando ${fmtBytes(apps.filter(isDell).reduce((s, a) => s + a.size, 0))}`)
    : statCard('box', 'var(--c5)', 'var(--accent-soft)', 'Origens', fmtNum(sources.length), sources.map(s => `${SOURCE_LABELS[s] || s} ${apps.filter(a => a.source === s).length}`).join(' · '));

  el.innerHTML = `
    <div class="grid g-3" style="margin-bottom:16px">
      ${statCard('grid', 'var(--accent)', 'var(--accent-soft)', 'Programas instalados', fmtNum(apps.length), `somando ${fmtBytes(total)}`)}
      ${statCard('trash', 'var(--danger)', 'var(--danger-soft)', 'Recomendo remover', fmtNum(removable.length), `liberaria cerca de ${fmtBytes(removable.reduce((s, a) => s + a.size, 0))}`)}
      ${third}
    </div>
    ${isLinux() ? `<p class="muted" style="margin:-4px 0 14px;font-size:13px">Pacotes do sistema aparecem só se foram instalados por você (apt-mark showmanual). Dependências órfãs saem pela Limpeza, em "Pacotes órfãos".</p>` : ''}
    <div class="toolbar">
      <div class="search">${icon('search')}<input class="input" id="appSearch" placeholder="Buscar programa ou fabricante" value="${esc(state.appsQuery)}"></div>
      <div class="chips">${Object.entries(filters).map(([k, [label, fn]]) => `<button class="chip ${state.appsFilter === k ? 'active' : ''}" data-apps-filter="${k}">${label} · ${apps.filter(fn).length}</button>`).join('')}</div>
    </div>
    <div class="card" style="padding:16px 10px 6px">
      <table class="list">
        <thead><tr>${sortHeader('name', 'Programa')}<th>Recomendação</th>${sortHeader('size', 'Tamanho', 'bar-cell')}<th class="r"></th><th class="r">Instalado</th><th></th></tr></thead>
        <tbody>${shown.map(a => {
          const v = VERDICTS[a.verdict];
          const busy = state.uninstalling === a.id;
          return `<tr class="app-row">
            <td class="grow"><div class="name-cell"><div class="app-logo" style="background:${logoColor(a.publisher || a.name)}">${esc((a.name || '?').trim()[0].toUpperCase())}</div>
              <div class="txt"><b title="${esc(a.name)}">${esc(a.name)}</b><small>${esc(a.publisher || '—')}${a.version ? ' · ' + esc(a.version) : ''}${a.source !== 'win32' ? ' · ' + esc(SOURCE_LABELS[a.source] || a.source) : ''}</small>
              ${a.advice ? `<div class="app-advice">${esc(a.advice)}</div>` : ''}</div></div></td>
            <td>${v ? `<span class="badge ${v[0]}">${v[1]}</span>` : '<span class="muted">—</span>'}</td>
            <td class="bar-cell"><div class="bar"><span style="width:${a.size / max * 100}%;background:${a.verdict === 'remover' ? 'var(--danger)' : 'var(--accent)'}"></span></div></td>
            <td class="r size-cell">${a.size ? fmtBytes(a.size) : '<span class="muted">—</span>'}</td>
            <td class="r muted num">${a.installDate ? fmtDate(a.installDate) : ''}</td>
            <td class="r">${a.requiresAdmin && !state.info?.isAdmin ? adminButton(`Requer ${ADMIN()}`) : `<button class="btn btn-sm ${a.verdict === 'remover' ? 'btn-danger' : ''}" data-uninstall="${esc(a.id)}" ${state.uninstalling ? 'disabled' : ''}>
              ${busy ? '<span class="spinner"></span>Aguardando…' : icon('trash') + 'Desinstalar'}</button>`}</td>
          </tr>`;
        }).join('')}</tbody>
      </table>
      ${!shown.length ? `<div class="empty" style="padding:24px">Nenhum programa encontrado.</div>` : ''}
    </div>`;
}

async function uninstall(id) {
  const a = state.apps.find(x => x.id === id);
  const ok = await confirmModal({
    title: `Desinstalar ${a.name}?`, icon: 'trash',
    body: `${a.advice ? `<p>${esc(a.advice)}</p>` : ''}${isLinux()
      ? `<p>O pacote será removido pelo ${esc(SOURCE_LABELS[a.source] || a.source)}. Acompanhe o andamento na Atividade.</p>`
      : '<p>O desinstalador do próprio programa vai abrir. Se o Windows pedir permissão, confirme. Siga as telas até o fim e volte aqui.</p>'}`,
    confirm: 'Desinstalar', danger: true,
  });
  if (!ok) return;
  state.uninstalling = id;
  openDrawer(true);
  renderApps();
  try {
    const r = await bridge.call('apps.uninstall', { id });
    toast(r.code === 0 ? `${a.name} desinstalado` : `Desinstalador terminou com código ${r.code}`, r.code === 0 ? 'ok' : 'err');
  } catch (e) { toast(e.message, 'err'); }
  state.uninstalling = null;
  await refreshInfo();
  loadApps();
}

function logoColor(s) {
  let h = 0;
  for (const ch of s || '') h = (h * 31 + ch.charCodeAt(0)) % 360;
  return `hsl(${h} 55% 52%)`;
}

// ───────────────────────── conta ─────────────────────────
const ACCOUNT_KINDS = { local: 'Conta local do Windows', microsoft: 'Conta Microsoft', domain: 'Conta de domínio (empresa)', linux: 'Usuário Linux' };

async function loadAccount() {
  try { state.account = await bridge.call('account.info'); }
  catch (e) { toast(e.message, 'err'); }
  if (state.view === 'account') renderAccount();
}

function renderAccount() {
  const el = view('account');
  const a = state.account;
  if (!a) { el.innerHTML = `<div class="card"><div class="skeleton" style="height:200px"></div></div>`; return; }

  const blocked = !a.canChange;
  el.innerHTML = `
    <div class="grid g-hero">
      <div class="card">
        <div class="row" style="gap:14px;margin-bottom:18px">
          <div class="app-logo" style="width:52px;height:52px;border-radius:14px;font-size:20px;background:${logoColor(a.user)}">${esc(a.user.replace(/^.*\\/, '')[0].toUpperCase())}</div>
          <div><h3 style="font-size:18px">${esc(a.user)}</h3><span class="muted">${esc(ACCOUNT_KINDS[a.kind] || a.kind)}</span></div>
        </div>
        <div class="hidden-list">
          <div class="hidden-item">${icon('lock')}<div class="txt"><b>Só a sua senha</b><small>O Faxina troca apenas a senha do usuário que está usando o computador, e só depois de conferir a atual.</small></div></div>
          <div class="hidden-item">${icon('shield')}<div class="txt"><b>Nada é guardado</b><small>As senhas vão direto para o ${isLinux() ? 'sistema (chpasswd)' : 'Windows'} e não aparecem em logs nem ficam salvas.</small></div></div>
          ${!isLinux() ? `<div class="hidden-item">${icon('key')}<div class="txt"><b>PIN do Windows Hello</b><small>O PIN não muda junto. Ele é trocado em Configurações › Contas › Opções de entrada.</small></div></div>` : ''}
        </div>
      </div>

      <div class="card">
        <div class="card-head"><h3>Alterar senha</h3></div>
        ${blocked ? `
          <div class="callout ${a.manageUrl ? 'info' : ''}">${icon(a.manageUrl ? 'info' : 'alert')}<div>${esc(a.reason || '')}</div></div>
          ${a.manageUrl ? `<button class="btn btn-primary" data-act="account-manage">${icon('open')}Trocar a senha no site da Microsoft</button>`
            : isLinux() && !state.info?.isAdmin ? adminButton() : ''}`
        : `
          <form id="pwForm" class="pw-form" autocomplete="off">
            <label class="field"><span>Senha atual</span>
              <input class="input" type="password" id="pwCurrent" autocomplete="current-password" required></label>
            <label class="field"><span>Nova senha</span>
              <input class="input" type="password" id="pwNext" autocomplete="new-password" required></label>
            <div class="strength"><div class="progress"><span id="pwBar" style="width:0%"></span></div><span id="pwLabel" class="muted">—</span></div>
            <label class="field"><span>Confirme a nova senha</span>
              <input class="input" type="password" id="pwConfirm" autocomplete="new-password" required></label>
            <label class="row show-pw"><input type="checkbox" id="pwShow"> <span class="muted">Mostrar senhas</span></label>
            <ul class="checks" id="pwChecks"></ul>
            <div class="row" style="justify-content:flex-end;margin-top:6px">
              <button class="btn btn-primary" type="submit" id="pwSubmit" disabled>${state.changingPassword ? '<span class="spinner" style="border-top-color:#fff"></span>Alterando…' : icon('key') + 'Alterar senha'}</button>
            </div>
          </form>`}
      </div>
    </div>`;
  if (!blocked) updatePasswordChecks();
}

/** Força estimada: tamanho + variedade de caracteres, com penalidade para sequências e repetições óbvias. */
function passwordStrength(pw) {
  if (!pw) return 0;
  let score = Math.min(pw.length, 20) * 3;
  const kinds = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter(r => r.test(pw)).length;
  score += (kinds - 1) * 12;
  if (/^(.)\1+$/.test(pw) || /(0123|1234|2345|3456|4567|5678|6789|abcd|qwer|senha|password)/i.test(pw)) score -= 30;
  return Math.max(0, Math.min(100, score));
}

function updatePasswordChecks() {
  const cur = $('#pwCurrent')?.value || '', next = $('#pwNext')?.value || '', conf = $('#pwConfirm')?.value || '';
  const min = state.account?.minLength || 8;
  const s = passwordStrength(next);
  const [label, color] = s < 40 ? ['Fraca', 'var(--danger)'] : s < 70 ? ['Média', 'var(--warn)'] : ['Forte', 'var(--ok)'];
  $('#pwBar').style.width = (next ? Math.max(8, s) : 0) + '%';
  $('#pwBar').style.background = color;
  $('#pwLabel').textContent = next ? label : '—';
  const checks = [
    [next.length >= min, `Pelo menos ${min} caracteres`],
    [/[A-Za-z]/.test(next) && /\d/.test(next), 'Letras e números'],
    [next.length > 0 && next !== cur, 'Diferente da senha atual'],
    [conf.length > 0 && conf === next, 'A confirmação é igual'],
  ];
  $('#pwChecks').innerHTML = checks.map(([ok, t]) => `<li class="${ok ? 'ok' : ''}">${icon(ok ? 'check' : 'x')}${t}</li>`).join('');
  const required = checks[0][0] && checks[2][0] && checks[3][0] && cur.length > 0;
  $('#pwSubmit').disabled = !required || state.changingPassword;
}

async function submitPassword() {
  const cur = $('#pwCurrent'), next = $('#pwNext'), conf = $('#pwConfirm');
  if (next.value !== conf.value) return toast('A confirmação não confere.', 'err');
  state.changingPassword = true;
  $('#pwSubmit').disabled = true;
  $('#pwSubmit').innerHTML = '<span class="spinner" style="border-top-color:#fff"></span>Alterando…';
  try {
    await bridge.call('account.changePassword', { current: cur.value, next: next.value });
    toast('Senha alterada. Use a nova senha no próximo login.', 'ok');
    cur.value = next.value = conf.value = '';
  } catch (e) {
    toast(e.message, 'err');
    cur.value = '';
    cur.focus();
  } finally {
    state.changingPassword = false;
    $('#pwSubmit').innerHTML = icon('key') + 'Alterar senha';
    updatePasswordChecks();
  }
}

// ───────────────────────── gaveta de atividade ─────────────────────────
const MAX_LOG = 2000;
bridge.on('log', d => appendLog(d.line));

function appendLog(line, cls) {
  const pre = $('#logLines');
  const span = document.createElement('span');
  if (!cls) cls = line.startsWith('▶') ? 'l-head' : line.startsWith('✔') ? 'l-ok' : /erro|error|falha|failed|negado/i.test(line) ? 'l-err' : '';
  if (cls) span.className = cls;
  span.textContent = line + '\n';
  pre.appendChild(span);
  while (pre.childNodes.length > MAX_LOG) pre.removeChild(pre.firstChild);
  pre.scrollTop = pre.scrollHeight;
  openDrawer(false);
}
function openDrawer(expand) {
  const d = $('#drawer');
  d.classList.add('visible');
  if (expand) d.classList.add('open');
}
function setDrawerStatus(html) { $('#drawerStatus').innerHTML = html; }

// ───────────────────────── modal e toast ─────────────────────────
function confirmModal({ title, body, confirm = 'Confirmar', danger = false, icon: ic = 'info' }) {
  return new Promise(resolve => {
    const m = $('#modal');
    m.innerHTML = `<div class="modal" role="dialog" aria-modal="true">
      <h3>${icon(ic)}${esc(title)}</h3>
      <div class="body">${body}</div>
      <div class="actions"><button class="btn" data-m="0">Cancelar</button><button class="btn ${danger ? 'btn-danger' : 'btn-primary'}" data-m="1">${esc(confirm)}</button></div>
    </div>`;
    m.hidden = false;
    const close = v => { m.hidden = true; m.innerHTML = ''; document.removeEventListener('keydown', onKey); resolve(v); };
    const onKey = e => { if (e.key === 'Escape') close(false); if (e.key === 'Enter') close(true); };
    document.addEventListener('keydown', onKey);
    m.onclick = e => {
      if (e.target === m) return close(false);
      const b = e.target.closest('[data-m]');
      if (b) close(b.dataset.m === '1');
    };
    m.querySelector('[data-m="1"]').focus();
  });
}

function toast(msg, kind = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.innerHTML = `${icon(kind === 'ok' ? 'check' : kind === 'err' ? 'alert' : 'info')}<span>${esc(msg)}</span>`;
  $('#toasts').appendChild(t);
  setTimeout(() => { t.style.transition = 'opacity .3s'; t.style.opacity = '0'; setTimeout(() => t.remove(), 300); }, kind === 'err' ? 6000 : 3500);
}

// ───────────────────────── util ─────────────────────────
const dateFmt = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: 'short', year: 'numeric' });
function fmtDate(s) { if (!s) return ''; const d = new Date(s); return isNaN(d) || d.getFullYear() < 1990 ? '' : dateFmt.format(d).replaceAll(' de ', ' ').replace('.', ''); }
function fmtTime(sec) { sec = Math.round(sec); return sec < 60 ? `${sec}s` : `${Math.floor(sec / 60)}min ${sec % 60}s`; }
function relDays(s) {
  const days = Math.floor((Date.now() - new Date(s)) / 864e5);
  if (days < 1) return 'hoje';
  if (days < 30) return `há ${days} d`;
  if (days < 365) return `há ${Math.round(days / 30)} meses`;
  return `há ${nf1.format(days / 365)} anos`;
}
const dirName = p => p.slice(0, Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'), 0)) || p;
const normDir = p => { const t = (p || '').replace(/[\\/]+$/, ''); return isLinux() ? t : t.toLowerCase(); };
const sameDir = (a, b) => normDir(a) === normDir(b);

/** Escolher pasta: janela nativa no Windows; no Linux (navegador) o caminho é digitado. */
async function pickFolder(title) {
  if (state.info?.canPickFolder) return bridge.call('dialog.pickFolder');
  return promptModal({ title, placeholder: '/home/' + (state.info?.user || 'usuario') + '/projetos', confirm: 'Usar esta pasta' });
}

function promptModal({ title, placeholder = '', confirm = 'OK' }) {
  return new Promise(resolve => {
    const m = $('#modal');
    m.innerHTML = `<div class="modal" role="dialog" aria-modal="true">
      <h3>${icon('folder')}${esc(title)}</h3>
      <div class="body"><p>Digite o caminho completo da pasta:</p><input class="input" id="promptInput" style="width:100%" placeholder="${esc(placeholder)}" spellcheck="false"></div>
      <div class="actions"><button class="btn" data-m="0">Cancelar</button><button class="btn btn-primary" data-m="1">${esc(confirm)}</button></div>
    </div>`;
    m.hidden = false;
    const input = $('#promptInput');
    const close = v => { m.hidden = true; m.innerHTML = ''; resolve(v); };
    input.addEventListener('keydown', e => { if (e.key === 'Enter') close(input.value.trim() || null); if (e.key === 'Escape') close(null); });
    m.onclick = e => {
      if (e.target === m) return close(null);
      const b = e.target.closest('[data-m]');
      if (b) close(b.dataset.m === '1' ? input.value.trim() || null : null);
    };
    input.focus();
  });
}

async function refreshInfo() {
  try { state.info = await bridge.call('system.info'); renderSidebar(); if (state.view === 'overview') renderOverview(); }
  catch (e) { toast(e.message, 'err'); }
}

// ───────────────────────── eventos ─────────────────────────
document.addEventListener('click', async e => {
  const nav = e.target.closest('#nav button');
  if (nav) return go(nav.dataset.view);

  const a = e.target.closest('[data-act]');
  if (a && a.tagName !== 'SELECT') {
    e.preventDefault();
    switch (a.dataset.act) {
      case 'scan-system': if (state.view !== 'overview') go('explore'); return startScan(state.info?.systemDrive || 'C:\\');
      case 'scan-selected': return startScan($('#scanTarget').value);
      case 'scan-cancel': return bridge.call('scan.cancel');
      case 'pick-folder': {
        const p = await pickFolder('Analisar outra pasta');
        if (p) startScan(p);
        return;
      }
      case 'go-cleanup': return go('cleanup');
      case 'account-manage': return bridge.call('account.openManage').catch(err => toast(err.message, 'err'));
      case 'measure-all': return measureAll();
      case 'apps-reload': return loadApps();
      case 'nm-pick': {
        const p = await pickFolder('Pasta dos seus projetos');
        if (!p) return;
        state.cleanup.nm.roots = [p];
        store.set('nmRoots', [p]);
        return measure('node-modules');
      }
      case 'relaunch': {
        if (!state.info?.canRelaunch) {
          // Linux: o navegador não consegue virar root; mostra o comando para rodar no terminal.
          const cmd = (state.info?.adminHint || '').replace(/^.*: /, '');
          const copy = await confirmModal({
            title: 'Liberar o modo root', icon: 'shield',
            body: `<p>As limpezas do sistema precisam de root. No terminal onde o Faxina está rodando, aperte <b>Ctrl+C</b> e rode:</p>
              <p><code>${esc(cmd)}</code></p><p>Depois abra o novo endereço que aparecer.</p>`,
            confirm: 'Copiar comando',
          });
          if (copy) { try { await navigator.clipboard.writeText(cmd); toast('Comando copiado', 'ok'); } catch { toast('Não consegui copiar; selecione o texto.', 'err'); } }
          return;
        }
        const ok = await confirmModal({
          title: 'Reabrir como administrador', icon: 'shield',
          body: '<p>O Windows vai pedir permissão. A análise atual será perdida e precisará ser refeita.</p>',
          confirm: 'Reabrir',
        });
        if (ok && !(await bridge.call('app.relaunchAdmin'))) toast('Permissão negada.', 'err');
        return;
      }
    }
  }

  const node = e.target.closest('[data-node]');
  if (node && !e.target.closest('.row-actions')) return openNode(node.dataset.node);

  const rev = e.target.closest('[data-reveal]');
  if (rev) return bridge.call('shell.reveal', { path: rev.dataset.reveal });

  const del = e.target.closest('[data-delete]');
  if (del) return deletePath(del.dataset.delete, del.dataset.permanent === '1');

  const ff = e.target.closest('[data-files-filter]');
  if (ff) { state.filesFilter = ff.dataset.filesFilter; return renderFiles(); }

  const af = e.target.closest('[data-apps-filter]');
  if (af) { state.appsFilter = af.dataset.appsFilter; return renderApps(); }

  const as = e.target.closest('[data-apps-sort]');
  if (as) {
    const by = as.dataset.appsSort;
    // Nome começa de A a Z; tamanho, do maior para o menor
    state.appsSort = state.appsSort.by === by ? { by, desc: !state.appsSort.desc } : { by, desc: by === 'size' };
    store.set('appsSort', state.appsSort);
    return renderApps();
  }

  const run = e.target.closest('[data-run]');
  if (run) return runCleanup(run.dataset.run);

  const un = e.target.closest('[data-uninstall]');
  if (un) return uninstall(un.dataset.uninstall);
});

document.addEventListener('change', e => {
  const cb = e.target.closest('[data-sel]');
  if (cb) {
    const set = state.cleanup.selections[cb.dataset.sel] ||= new Set();
    cb.checked ? set.add(cb.value) : set.delete(cb.value);
    renderCleanup();
    renderSidebar();
    return;
  }
  if (e.target.matches('[data-act-change="nm-idle"]')) {
    state.cleanup.nm.idleDays = +e.target.value;
    store.set('nmIdle', state.cleanup.nm.idleDays);
    measure('node-modules');
  }
});

document.addEventListener('submit', e => {
  if (e.target.id === 'pwForm') { e.preventDefault(); submitPassword(); }
});

document.addEventListener('input', e => {
  if (['pwCurrent', 'pwNext', 'pwConfirm'].includes(e.target.id)) return updatePasswordChecks();
  if (e.target.id === 'pwShow') {
    ['pwCurrent', 'pwNext', 'pwConfirm'].forEach(id => { $('#' + id).type = e.target.checked ? 'text' : 'password'; });
    return;
  }
  if (e.target.id === 'appSearch') {
    state.appsQuery = e.target.value;
    const pos = e.target.selectionStart;
    renderApps();
    const s = $('#appSearch');
    s.focus();
    s.setSelectionRange(pos, pos);
  }
});

$('#drawerToggle').addEventListener('click', () => $('#drawer').classList.toggle('open'));
document.addEventListener('faxina:offline', () => toast('Perdi a conexão com o Faxina. Ele ainda está rodando no terminal?', 'err'));

// ───────────────────────── início ─────────────────────────
(async function init() {
  hydrateIcons();
  go('overview');
  await refreshInfo();
  render();
  loadCleanup();
})();
