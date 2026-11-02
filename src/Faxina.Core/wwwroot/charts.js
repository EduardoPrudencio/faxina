// Gráficos em SVG/HTML puro (sem bibliotecas): rosca, treemap, barras horizontais e barra empilhada.
'use strict';

const Charts = (() => {
  const tip = () => document.getElementById('tooltip');

  function showTip(evt, html) {
    const t = tip();
    t.innerHTML = html;
    t.hidden = false;
    const pad = 14;
    let x = evt.clientX + pad, y = evt.clientY + pad;
    const r = t.getBoundingClientRect();
    if (x + r.width > innerWidth - 8) x = evt.clientX - r.width - pad;
    if (y + r.height > innerHeight - 8) y = evt.clientY - r.height - pad;
    t.style.left = x + 'px';
    t.style.top = y + 'px';
  }
  const hideTip = () => { tip().hidden = true; };

  function bindTip(el, html) {
    el.addEventListener('mousemove', e => showTip(e, typeof html === 'function' ? html() : html));
    el.addEventListener('mouseleave', hideTip);
  }

  /** Rosca: segments = [{label, value, color}] */
  function donut(container, segments, { size = 210, thickness = 26, center = null, gap = 0.012 } = {}) {
    const total = segments.reduce((s, x) => s + Math.max(0, x.value), 0) || 1;
    const r = (size - thickness) / 2;
    const c = 2 * Math.PI * r;
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
    svg.setAttribute('width', size);
    svg.setAttribute('height', size);
    svg.style.transform = 'rotate(-90deg)';

    const track = document.createElementNS(ns, 'circle');
    Object.entries({ cx: size / 2, cy: size / 2, r, fill: 'none', stroke: 'var(--track)', 'stroke-width': thickness })
      .forEach(([k, v]) => track.setAttribute(k, v));
    svg.appendChild(track);

    let offset = 0;
    const visible = segments.filter(s => s.value > 0);
    visible.forEach((s, i) => {
      const frac = s.value / total;
      const g = visible.length > 1 ? Math.min(gap, frac / 3) : 0;
      const len = Math.max(0, (frac - g) * c);
      const arc = document.createElementNS(ns, 'circle');
      Object.entries({
        cx: size / 2, cy: size / 2, r, fill: 'none', stroke: s.color, 'stroke-width': thickness,
        'stroke-dasharray': `${len} ${c - len}`, 'stroke-dashoffset': -offset * c,
        'stroke-linecap': visible.length > 1 && len > thickness ? 'butt' : 'butt',
      }).forEach(([k, v]) => arc.setAttribute(k, v));
      arc.style.transition = 'stroke-width .15s';
      arc.style.cursor = 'default';
      arc.addEventListener('mouseenter', () => arc.setAttribute('stroke-width', thickness + 6));
      arc.addEventListener('mouseleave', () => arc.setAttribute('stroke-width', thickness));
      bindTip(arc, `<b>${esc(s.label)}</b><br>${fmtBytes(s.value)} · ${pct(s.value / total)}`);
      svg.appendChild(arc);
      offset += frac;
    });

    const wrap = document.createElement('div');
    wrap.className = 'donut-wrap';
    wrap.style.width = wrap.style.height = size + 'px';
    wrap.style.position = 'relative';
    wrap.appendChild(svg);
    if (center) {
      const cEl = document.createElement('div');
      cEl.className = 'donut-center';
      cEl.innerHTML = center;
      wrap.appendChild(cEl);
    }
    container.innerHTML = '';
    container.appendChild(wrap);
  }

  /** Treemap "squarified". items = [{label, value, color, sub, onClick}] */
  function treemap(container, items, { onClick } = {}) {
    container.innerHTML = '';
    container.classList.add('treemap');
    const data = items.filter(i => i.value > 0).sort((a, b) => b.value - a.value);
    if (!data.length) return;

    const draw = () => {
      container.querySelectorAll('.tm-cell').forEach(n => n.remove());
      const W = container.clientWidth, H = container.clientHeight;
      if (!W || !H) return;
      const total = data.reduce((s, d) => s + d.value, 0);
      const scaled = data.map(d => ({ ...d, area: d.value / total * W * H }));
      layout(scaled, { x: 0, y: 0, w: W, h: H }).forEach(cell => {
        const el = document.createElement('div');
        el.className = 'tm-cell';
        const { x, y, w, h } = cell.rect;
        Object.assign(el.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px', background: cell.color });
        if (w < 46 || h < 30) el.classList.add('tiny');
        else if (h < 48 || w < 80) el.classList.add('small');
        if (!cell.clickable) el.classList.add('leaf');
        el.innerHTML = `<div class="tm-name">${esc(cell.label)}</div><div class="tm-size">${fmtBytes(cell.value)}</div>`;
        bindTip(el, `<b>${esc(cell.label)}</b><br>${fmtBytes(cell.value)} · ${pct(cell.value / total)}${cell.sub ? '<br>' + cell.sub : ''}`);
        if (cell.clickable && onClick) el.addEventListener('click', () => { hideTip(); onClick(cell); });
        container.appendChild(el);
      });
    };

    draw();
    if (container._ro) container._ro.disconnect();
    container._ro = new ResizeObserver(() => draw());
    container._ro.observe(container);
  }

  // Algoritmo squarified (Bruls, Huizing, van Wijk).
  function layout(items, rect) {
    const out = [];
    let row = [];
    let r = { ...rect };
    const worst = (row, side) => {
      const s = row.reduce((a, b) => a + b.area, 0);
      const max = Math.max(...row.map(x => x.area)), min = Math.min(...row.map(x => x.area));
      return Math.max(side * side * max / (s * s), (s * s) / (side * side * min));
    };
    const place = (row, r) => {
      const s = row.reduce((a, b) => a + b.area, 0);
      if (r.w >= r.h) {
        const w = s / r.h; let y = r.y;
        row.forEach(it => { const h = it.area / w; out.push({ ...it, rect: { x: r.x, y, w, h } }); y += h; });
        return { x: r.x + w, y: r.y, w: r.w - w, h: r.h };
      }
      const h = s / r.w; let x = r.x;
      row.forEach(it => { const w = it.area / h; out.push({ ...it, rect: { x, y: r.y, w, h } }); x += w; });
      return { x: r.x, y: r.y + h, w: r.w, h: r.h - h };
    };
    const queue = [...items];
    while (queue.length) {
      const item = queue[0];
      const side = Math.min(r.w, r.h);
      if (!row.length || worst([...row, item], side) <= worst(row, side)) { row.push(item); queue.shift(); }
      else { r = place(row, r); row = []; }
    }
    if (row.length) place(row, r);
    return out;
  }

  /** Barras horizontais: rows = [{label, value, color, extra}] */
  function hbars(container, rows, { max } = {}) {
    const top = max ?? Math.max(1, ...rows.map(r => r.value));
    container.innerHTML = `<div class="hbars">${rows.map(r => `
      <div class="hbar" title="${esc(r.title || r.label)}">
        <div class="top"><span>${esc(r.label)}</span><b>${fmtBytes(r.value)}</b></div>
        <div class="bar"><span style="width:${Math.max(1, r.value / top * 100)}%;background:${r.color || 'var(--accent)'}"></span></div>
      </div>`).join('')}</div>`;
  }

  /** Barra empilhada com legenda. segments = [{label, value, color, hint}] */
  function stacked(container, segments, total) {
    const sum = total ?? segments.reduce((s, x) => s + x.value, 0);
    const bar = document.createElement('div');
    bar.className = 'stacked';
    segments.filter(s => s.value > 0).forEach(s => {
      const d = document.createElement('div');
      d.style.width = (s.value / sum * 100) + '%';
      d.style.background = s.color;
      bindTip(d, `<b>${esc(s.label)}</b><br>${fmtBytes(s.value)} · ${pct(s.value / sum)}${s.hint ? '<br>' + esc(s.hint) : ''}`);
      bar.appendChild(d);
    });
    const legend = document.createElement('div');
    legend.className = 'legend';
    legend.style.marginTop = '18px';
    legend.style.display = 'grid';
    legend.style.gridTemplateColumns = 'repeat(auto-fill, minmax(240px, 1fr))';
    legend.style.columnGap = '28px';
    legend.innerHTML = segments.filter(s => s.value > 0).map(s => `
      <div class="legend-row" title="${esc(s.hint || '')}">
        <span class="sw" style="background:${s.color}"></span>
        <span class="lbl">${esc(s.label)}</span>
        <span class="val">${fmtBytes(s.value)}</span>
        <span class="pct">${pct(s.value / sum)}</span>
      </div>`).join('');
    container.innerHTML = '';
    container.append(bar, legend);
  }

  return { donut, treemap, hbars, stacked, bindTip, hideTip };
})();

// ───────── formatação (compartilhada com app.js) ─────────
const nf1 = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1, minimumFractionDigits: 1 });
const nf0 = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

function fmtBytes(b) {
  if (b == null || isNaN(b)) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = Math.abs(b), i = 0;
  while (v >= 1000 && i < u.length - 1) { v /= 1024; i++; }
  const s = i === 0 ? nf0.format(v) : v < 100 ? nf1.format(v) : nf0.format(v);
  return (b < 0 ? '-' : '') + s + ' ' + u[i];
}
function pct(f) { return (f * 100 < 1 && f > 0) ? '<1%' : nf0.format(f * 100) + '%'; }
function fmtNum(n) { return nf0.format(n); }
function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
