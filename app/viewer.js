/**
 * pdfview - viewer front end.
 * Continuous scrolling viewer built on pdf.js: lazy canvas rendering,
 * selectable text, links, thumbnails, outline and full document search.
 */
import * as pdfjsLib from '/vendor/pdf.min.mjs';

pdfjsLib.GlobalWorkerOptions.workerSrc = '/vendor/pdf.worker.min.mjs';

const CMAP_URL = '/vendor/cmaps/';
const FONT_URL = '/vendor/standard_fonts/';
const RENDER_MARGIN = 1;          // pages rendered either side of the viewport
const MAX_CANVAS_PIXELS = 24e6;   // guard against absurd canvas allocations
const PAGE_GAP = 16;

const $ = (id) => document.getElementById(id);

/* The Windows app hosts this page in a WebView2. In a plain browser there is
   no shell and every one of these calls quietly does nothing. */
const shell = (window.chrome && window.chrome.webview) || null;
const tellShell = (message) => { if (shell) shell.postMessage(message); };

/* Startup timing. The shell stamps each mark against the process start time,
   which is the only clock that sees the part of the wait before this file ran. */
const traced = new Set();
const mark = (name) => {
  if (traced.has(name)) return;
  traced.add(name);
  tellShell({ type: 'trace', mark: name });
};
mark('module evaluated');

const el = {
  viewer: $('viewer'),
  container: $('viewer-container'),
  dropzone: $('dropzone'),
  dragOverlay: $('drag-overlay'),
  loading: $('loading'),
  loadingText: $('loading-text'),
  toast: $('toast'),
  title: $('doc-title'),
  pageNum: $('page-num'),
  pageCount: $('page-count'),
  zoom: $('zoom-select'),
  spread: $('spread-select'),
  sidebar: $('sidebar'),
  thumbs: $('thumbs'),
  outline: $('outline'),
  findbar: $('findbar'),
  findInput: $('find-input'),
  findStatus: $('find-status'),
  findCase: $('find-case'),
  recentMenu: $('recent-menu'),
  dzRecent: $('dz-recent'),
  fileInput: $('file-input'),
  helpDialog: $('help-dialog'),
};

const prefs = {
  get(key, fallback) {
    try {
      const v = localStorage.getItem('pdfview:' + key);
      return v === null ? fallback : JSON.parse(v);
    } catch {
      return fallback;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem('pdfview:' + key, JSON.stringify(value));
    } catch {
      /* private mode, nothing to do */
    }
  },
};

const state = {
  doc: null,
  pages: [],          // { num, div, base, width, height, rendered, task, textLayer }
  gen: 0,             // bumped on every layout change; stale renders bail out
  scale: 1,
  fitMode: 'width',
  spread: 'off',     // 'off' | 'two' | 'cover'
  rotation: 0,
  current: 1,
  source: null,       // { kind: 'path' | 'file', path, name, url, data }
  recent: [],
  rendering: new Set(),
  thumbObserver: null,
  find: { query: '', matches: [], index: -1, pageText: [], token: 0 },
};

/* ================= utilities ================= */

let toastTimer = null;
function toast(message, isError) {
  el.toast.textContent = message;
  el.toast.classList.toggle('error', !!isError);
  el.toast.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    el.toast.hidden = true;
  }, isError ? 6000 : 2600);
}

function busy(on, text) {
  el.loadingText.textContent = text || 'Loading';
  el.loading.hidden = !on;
}

function debounce(fn, ms) {
  let t = null;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}

/* ================= document loading ================= */

async function openByPath(filePath) {
  const url = '/api/file?path=' + encodeURIComponent(filePath);
  const name = filePath.split(/[\\/]/).pop();
  await loadDocument({ url, cMapUrl: CMAP_URL, cMapPacked: true, standardFontDataUrl: FONT_URL },
    { kind: 'path', path: filePath, name, url });
}

async function openLocalFile(file) {
  const data = await file.arrayBuffer();
  const url = URL.createObjectURL(file);
  await loadDocument({ data: new Uint8Array(data), cMapUrl: CMAP_URL, cMapPacked: true, standardFontDataUrl: FONT_URL },
    { kind: 'file', name: file.name, size: file.size, url });
}

async function loadDocument(params, source) {
  busy(true, 'Opening ' + source.name);
  try {
    if (state.doc) {
      await state.doc.destroy().catch(() => {});
      state.doc = null;
    }
    const task = pdfjsLib.getDocument(params);
    task.onPassword = (updateCallback, reason) => {
      const prompt = reason === pdfjsLib.PasswordResponses.INCORRECT_PASSWORD
        ? 'Incorrect password. Try again:'
        : 'This document is password protected. Password:';
      const password = window.prompt(prompt);
      if (password === null) task.destroy();
      else updateCallback(password);
    };
    const doc = await task.promise;

    mark('pdf parsed');
    state.doc = doc;
    state.source = source;
    state.rotation = 0;
    state.find = { query: '', matches: [], index: -1, pageText: [], token: 0 };
    closeFind();

    document.title = source.name + ' - pdfview';
    tellShell({ type: 'opened', path: source.kind === 'path' ? source.path : null });
    el.title.textContent = source.name;
    el.title.title = source.path || source.name;
    el.dropzone.hidden = true;

    await buildPages();
    setEnabled(true);

    await recentsReady;   // the only thing in this open that needs the list
    const startPage = lastPageFor(source);
    applyFit(true);
    if (startPage > 1) goToPage(startPage);
    else updateVisiblePages();

    buildThumbs();
    buildOutline();
    rememberOpen();
    el.container.focus();
  } catch (err) {
    console.error(err);
    const message = err && err.name === 'PasswordException'
      ? 'Could not open: wrong or missing password.'
      : 'Could not open ' + source.name + ': ' + (err && err.message ? err.message : err);
    toast(message, true);
    if (!state.doc) el.dropzone.hidden = false;
  } finally {
    busy(false);
  }
}

function setEnabled(on) {
  for (const id of ['btn-prev', 'btn-next', 'btn-zoom-in', 'btn-zoom-out', 'btn-rotate',
    'btn-find', 'btn-print', 'btn-download', 'btn-sidebar']) {
    $(id).disabled = !on;
  }
  el.pageNum.disabled = !on;
  el.zoom.disabled = !on;
  el.spread.disabled = !on;
  queueFit();   // the page count and title change width when a document opens
}

/* ================= page layout ================= */

async function buildPages() {
  el.viewer.textContent = '';
  state.pages = [];
  state.rendering.clear();

  const total = state.doc.numPages;
  el.pageCount.textContent = total;

  for (let i = 1; i <= total; i++) {
    if (i % 50 === 0) busy(true, 'Reading pages ' + i + ' / ' + total);
    const page = await state.doc.getPage(i);
    const base = page.getViewport({ scale: 1, rotation: (page.rotate + state.rotation) % 360 });

    const div = document.createElement('div');
    div.className = 'page';
    div.dataset.page = String(i);
    const badge = document.createElement('div');
    badge.className = 'page-badge';
    badge.textContent = i;
    div.append(badge);

    state.pages.push({ num: i, div, base, rendered: false, task: null, textLayer: null });
  }
  arrange();
  busy(true, 'Rendering');
}

/* ================= page layout ================= */

/// How many pages sit across the viewer. Two-page mode keeps two columns
/// throughout, so a lone cover or a lone final page renders at the same scale
/// as every spread around it.
function spreadColumns() {
  return state.spread === 'off' ? 1 : 2;
}

/// The left-hand page of the spread that `n` belongs to.
function spreadStart(n) {
  if (state.spread === 'off') return n;
  if (state.spread === 'cover') return n <= 1 ? 1 : n - ((n - 2) % 2);
  return n - ((n - 1) % 2);
}

/// One spread forward or back from wherever `from` is.
function stepSpread(from, direction) {
  if (state.spread === 'off') return from + direction;
  const start = spreadStart(from);
  if (direction > 0) return start + (state.spread === 'cover' && start === 1 ? 1 : 2);
  if (start <= 1) return 1;
  return Math.max(1, start - 2);
}

/// Puts the page elements into the viewer, singly or in spread rows.
function arrange() {
  el.viewer.textContent = '';

  if (state.spread === 'off') {
    for (const p of state.pages) el.viewer.append(p.div);
    return;
  }

  const row = (pages) => {
    const div = document.createElement('div');
    div.className = 'spread';
    for (const p of pages) div.append(p.div);
    el.viewer.append(div);
  };

  let i = 0;
  if (state.spread === 'cover' && state.pages.length) {
    row([state.pages[0]]);
    i = 1;
  }
  for (; i < state.pages.length; i += 2) {
    row(state.pages.slice(i, i + 2));
  }
}

function setSpread(mode) {
  state.spread = mode;
  prefs.set('spread', mode);
  el.spread.value = mode;
  if (!state.pages.length) return;

  const stay = spreadStart(state.current);
  arrange();
  applyFit(true);
  goToPage(stay);
}

function containerBox() {
  const styles = getComputedStyle(el.container);
  return {
    width: el.container.clientWidth - parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight) - 36,
    height: el.container.clientHeight - 36,
  };
}

function computeFitScale(mode) {
  const first = state.pages[state.current - 1] || state.pages[0];
  if (!first) return 1;
  const box = containerBox();
  const across = spreadColumns();
  const spreadWidth = first.base.width * across + PAGE_GAP * (across - 1);
  if (mode === 'width') return Math.max(0.1, box.width / spreadWidth);
  return Math.max(0.1, Math.min(box.width / spreadWidth, box.height / first.base.height));
}

function applyFit(skipAnchor) {
  if (state.fitMode === 'width' || state.fitMode === 'page') {
    state.scale = computeFitScale(state.fitMode);
  }
  layout(skipAnchor);
}

function layout(skipAnchor) {
  if (!state.pages.length) return;
  state.gen++;

  // Remember where we are so zooming does not throw the reader off the page.
  const anchor = state.pages[state.current - 1];
  const anchorOffset = anchor ? el.container.scrollTop - anchor.div.offsetTop : 0;
  const anchorRatio = anchor && anchor.div.offsetHeight ? anchorOffset / anchor.div.offsetHeight : 0;

  for (const p of state.pages) {
    p.width = Math.floor(p.base.width * state.scale);
    p.height = Math.floor(p.base.height * state.scale);
    p.div.style.width = p.width + 'px';
    p.div.style.height = p.height + 'px';
    p.div.style.setProperty('--scale-factor', String(state.scale));
    p.div.style.setProperty('--total-scale-factor', String(state.scale));
    discardPage(p);
  }

  if (skipAnchor !== true && anchor) {
    el.container.scrollTop = anchor.div.offsetTop + anchorRatio * anchor.div.offsetHeight;
  }
  updateZoomSelect();
  updateVisiblePages();
}

function updateZoomSelect() {
  const value = state.fitMode === 'custom' ? 'custom' : state.fitMode;
  const custom = el.zoom.querySelector('option[value="custom"]');
  custom.textContent = Math.round(state.scale * 100) + '%';
  custom.hidden = state.fitMode !== 'custom';
  el.zoom.value = value;
}

/* ================= rendering ================= */

function pagesInView() {
  const top = el.container.scrollTop;
  const bottom = top + el.container.clientHeight;
  const visible = [];
  for (const p of state.pages) {
    const pTop = p.div.offsetTop;
    const pBottom = pTop + p.div.offsetHeight;
    if (pBottom > top - PAGE_GAP && pTop < bottom + PAGE_GAP) visible.push(p);
  }
  return visible;
}

function updateVisiblePages() {
  if (!state.pages.length) return;
  const visible = pagesInView();
  if (!visible.length) return;

  const first = visible[0].num;
  const last = visible[visible.length - 1].num;
  const from = Math.max(1, first - RENDER_MARGIN);
  const to = Math.min(state.pages.length, last + RENDER_MARGIN);

  for (const p of state.pages) {
    if (p.num >= from && p.num <= to) renderPage(p);
    else if (p.rendered && (p.num < from - 4 || p.num > to + 4)) discardPage(p);
  }
}

function discardPage(p) {
  if (p.task) {
    try {
      p.task.cancel();
    } catch {
      /* already settled */
    }
    p.task = null;
  }
  if (p.textLayer) {
    p.textLayer.cancel();
    p.textLayer = null;
  }
  p.div.textContent = '';
  const badge = document.createElement('div');
  badge.className = 'page-badge';
  badge.textContent = p.num;
  p.div.append(badge);
  p.rendered = false;
}

async function renderPage(p) {
  if (p.rendered || state.rendering.has(p.num) || !state.doc) return;
  state.rendering.add(p.num);
  const gen = state.gen;
  // A zoom or rotation mid-render invalidates everything computed so far.
  const stale = () => gen !== state.gen;

  try {
    const page = await state.doc.getPage(p.num);
    if (stale()) return retryLater();
    const rotation = (page.rotate + state.rotation) % 360;
    const viewport = page.getViewport({ scale: state.scale, rotation });

    let dpr = Math.min(window.devicePixelRatio || 1, 2);
    while (viewport.width * viewport.height * dpr * dpr > MAX_CANVAS_PIXELS && dpr > 0.5) dpr -= 0.25;

    const canvas = document.createElement('canvas');
    canvas.width = Math.floor(viewport.width * dpr);
    canvas.height = Math.floor(viewport.height * dpr);
    canvas.style.width = Math.floor(viewport.width) + 'px';
    canvas.style.height = Math.floor(viewport.height) + 'px';
    const ctx = canvas.getContext('2d', { alpha: false });

    const task = page.render({
      canvasContext: ctx,
      viewport,
      transform: dpr === 1 ? null : [dpr, 0, 0, dpr, 0, 0],
      background: '#ffffff',
    });
    p.task = task;
    await task.promise;
    p.task = null;
    if (stale()) return retryLater();

    p.div.textContent = '';
    p.div.append(canvas);

    // Selectable text
    const textDiv = document.createElement('div');
    textDiv.className = 'textLayer';
    p.div.append(textDiv);
    const textLayer = new pdfjsLib.TextLayer({
      textContentSource: page.streamTextContent({ includeMarkedContent: false }),
      container: textDiv,
      viewport,
    });
    p.textLayer = textLayer;
    await textLayer.render();
    pdfjsLib.setLayerDimensions(textDiv, viewport);

    await buildLinkLayer(page, viewport, p.div);
    if (stale()) return retryLater();

    p.rendered = true;
    mark('first-paint');
    if (state.find.query) highlightPage(p);
  } catch (err) {
    if (err && err.name === 'RenderingCancelledException') return;
    console.error('render page ' + p.num, err);
  } finally {
    state.rendering.delete(p.num);
  }

  // Drop what was drawn at the old scale and let the viewport ask again.
  function retryLater() {
    discardPage(p);
    queueMicrotask(updateVisiblePages);
  }
}

async function buildLinkLayer(page, viewport, pageDiv) {
  let annotations = [];
  try {
    annotations = await page.getAnnotations({ intent: 'display' });
  } catch {
    return;
  }
  const links = annotations.filter((a) => a.subtype === 'Link' && (a.url || a.dest));
  if (!links.length) return;

  const layer = document.createElement('div');
  layer.className = 'linkLayer';
  for (const a of links) {
    const rect = viewport.convertToViewportRectangle(a.rect);
    const [x1, y1, x2, y2] = pdfjsLib.Util.normalizeRect(rect);
    const link = document.createElement('a');
    link.style.left = x1 + 'px';
    link.style.top = y1 + 'px';
    link.style.width = x2 - x1 + 'px';
    link.style.height = y2 - y1 + 'px';
    if (a.url) {
      link.href = a.url;
      link.target = '_blank';
      link.rel = 'noreferrer noopener';
      link.title = a.url;
    } else {
      link.href = '#';
      link.title = 'Go to destination';
      link.addEventListener('click', (ev) => {
        ev.preventDefault();
        goToDestination(a.dest);
      });
    }
    layer.append(link);
  }
  pageDiv.append(layer);
}

async function goToDestination(dest) {
  try {
    const explicit = typeof dest === 'string' ? await state.doc.getDestination(dest) : dest;
    if (!explicit) return;
    const index = await state.doc.getPageIndex(explicit[0]);
    goToPage(index + 1);
  } catch (err) {
    console.warn('destination failed', err);
  }
}

/* ================= navigation ================= */

function goToPage(num) {
  const n = Math.min(Math.max(1, Math.round(num)), state.pages.length);
  const p = state.pages[n - 1];
  if (!p) return;
  el.container.scrollTop = p.div.offsetTop - 12;
  setCurrentPage(n);
  updateVisiblePages();
}

function setCurrentPage(n) {
  if (n === state.current) return;
  state.current = n;
  if (document.activeElement !== el.pageNum) el.pageNum.value = String(n);
  const active = el.thumbs.querySelector('.thumb.current');
  if (active) active.classList.remove('current');
  const thumb = el.thumbs.querySelector('.thumb[data-page="' + n + '"]');
  if (thumb) {
    thumb.classList.add('current');
    const box = el.thumbs.getBoundingClientRect();
    const rect = thumb.getBoundingClientRect();
    if (rect.top < box.top || rect.bottom > box.bottom) scrollThumbIntoView('nearest');
  }
  savePosition();
}

function currentPageFromScroll() {
  const mid = el.container.scrollTop + 80;
  let best = 1;
  for (const p of state.pages) {
    if (p.div.offsetTop <= mid) best = p.num;
    else break;
  }
  return spreadStart(best);
}

let scrollRaf = 0;
el.container.addEventListener('scroll', () => {
  if (scrollRaf) return;
  scrollRaf = requestAnimationFrame(() => {
    scrollRaf = 0;
    if (!state.pages.length) return;
    setCurrentPage(currentPageFromScroll());
    updateVisiblePages();
  });
}, { passive: true });

/* ================= zoom ================= */

const ZOOM_STEPS = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];

function setScale(scale, mode) {
  state.scale = Math.min(6, Math.max(0.1, scale));
  state.fitMode = mode || 'custom';
  prefs.set('fitMode', state.fitMode);
  prefs.set('scale', state.scale);
  layout();
}

function zoomBy(direction) {
  const current = state.scale;
  if (direction > 0) {
    const next = ZOOM_STEPS.find((z) => z > current + 0.001);
    setScale(next || current * 1.25);
  } else {
    const below = ZOOM_STEPS.filter((z) => z < current - 0.001);
    setScale(below.length ? below[below.length - 1] : current / 1.25);
  }
}

/* ================= sidebar: thumbnails and outline ================= */

function buildThumbs() {
  el.thumbs.textContent = '';
  if (state.thumbObserver) state.thumbObserver.disconnect();

  state.thumbObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      renderThumb(entry.target);
      state.thumbObserver.unobserve(entry.target);
    }
  }, { root: el.thumbs, rootMargin: '300px' });

  for (const p of state.pages) {
    const btn = document.createElement('button');
    btn.className = 'thumb' + (p.num === state.current ? ' current' : '');
    btn.dataset.page = String(p.num);
    const ph = document.createElement('div');
    ph.className = 'thumb-ph';
    ph.style.aspectRatio = p.base.width + ' / ' + p.base.height;
    const label = document.createElement('div');
    label.className = 'thumb-label';
    label.textContent = p.num;
    btn.append(ph, label);
    btn.addEventListener('click', () => goToPage(p.num));
    el.thumbs.append(btn);
    state.thumbObserver.observe(btn);
  }
  scrollThumbIntoView('center');
}

function scrollThumbIntoView(block) {
  const thumb = el.thumbs.querySelector('.thumb.current');
  if (thumb) thumb.scrollIntoView({ block: block || 'nearest' });
}

async function renderThumb(btn) {
  const num = Number(btn.dataset.page);
  try {
    const page = await state.doc.getPage(num);
    const rotation = (page.rotate + state.rotation) % 360;
    const base = page.getViewport({ scale: 1, rotation });
    const width = 170;
    const viewport = page.getViewport({ scale: width / base.width, rotation });
    const canvas = document.createElement('canvas');
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.floor(viewport.width * dpr);
    canvas.height = Math.floor(viewport.height * dpr);
    const ctx = canvas.getContext('2d', { alpha: false });
    await page.render({
      canvasContext: ctx,
      viewport,
      transform: [dpr, 0, 0, dpr, 0, 0],
      background: '#ffffff',
    }).promise;
    const ph = btn.querySelector('.thumb-ph');
    if (ph) ph.replaceWith(canvas);
    else btn.prepend(canvas);
  } catch (err) {
    console.warn('thumbnail ' + num, err);
  }
}

async function buildOutline() {
  el.outline.textContent = '';
  let outline = null;
  try {
    outline = await state.doc.getOutline();
  } catch {
    outline = null;
  }
  if (!outline || !outline.length) {
    const empty = document.createElement('div');
    empty.className = 'side-empty';
    empty.textContent = 'This document has no outline.';
    el.outline.append(empty);
    return;
  }

  const build = (items, parent) => {
    for (const item of items) {
      const btn = document.createElement('button');
      btn.className = 'outline-item';
      btn.textContent = item.title || '(untitled)';
      btn.title = item.title || '';
      btn.addEventListener('click', () => {
        if (item.dest) goToDestination(item.dest);
        else if (item.url) window.open(item.url, '_blank', 'noreferrer');
      });
      parent.append(btn);
      if (item.items && item.items.length) {
        const children = document.createElement('div');
        children.className = 'outline-children';
        parent.append(children);
        build(item.items, children);
      }
    }
  };
  build(outline, el.outline);
}

function toggleSidebar(force) {
  const show = force === undefined ? el.sidebar.hidden : force;
  el.sidebar.hidden = !show;
  $('btn-sidebar').classList.toggle('on', show);
  prefs.set('sidebar', show);
  if (show) requestAnimationFrame(() => scrollThumbIntoView('center'));
  if (state.pages.length && (state.fitMode === 'width' || state.fitMode === 'page')) {
    requestAnimationFrame(() => applyFit());
  }
}

/* ================= find ================= */

function normalize(text, caseSensitive) {
  const spaced = text.replace(/\s/g, ' ');
  return caseSensitive ? spaced : spaced.toLowerCase();
}

async function buildFindIndex(token) {
  if (state.find.pageText.length === state.pages.length) return true;
  const texts = [];
  for (let i = 1; i <= state.pages.length; i++) {
    if (token !== state.find.token) return false;
    const page = await state.doc.getPage(i);
    const content = await page.getTextContent({ includeMarkedContent: false });
    let text = '';
    for (const item of content.items) {
      text += item.str;
      if (item.hasEOL) text += ' ';
    }
    texts.push(text);
    if (i % 25 === 0) el.findStatus.textContent = 'Indexing ' + i + '/' + state.pages.length;
  }
  state.find.pageText = texts;
  return true;
}

const runFind = debounce(async (jump) => {
  const query = el.findInput.value;
  const caseSensitive = el.findCase.checked;
  const token = ++state.find.token;

  clearAllHighlights();
  state.find.query = query;
  state.find.matches = [];
  state.find.index = -1;

  if (!query) {
    el.findStatus.textContent = '';
    el.findStatus.classList.remove('none');
    return;
  }
  if (!state.doc) return;

  const ok = await buildFindIndex(token);
  if (!ok || token !== state.find.token) return;

  const needle = normalize(query, caseSensitive);
  const matches = [];
  state.find.pageText.forEach((raw, i) => {
    const hay = normalize(raw, caseSensitive);
    let at = hay.indexOf(needle);
    let nth = 0;
    while (at !== -1) {
      matches.push({ page: i + 1, nth });
      nth++;
      at = hay.indexOf(needle, at + Math.max(1, needle.length));
    }
  });

  state.find.matches = matches;
  el.findStatus.classList.toggle('none', matches.length === 0);
  if (!matches.length) {
    el.findStatus.textContent = 'No results';
    return;
  }
  for (const p of state.pages) if (p.rendered) highlightPage(p);
  if (jump !== false) {
    const from = matches.findIndex((m) => m.page >= state.current);
    gotoMatch(from === -1 ? 0 : from);
  } else {
    updateFindStatus();
  }
}, 220);

function updateFindStatus() {
  const { matches, index } = state.find;
  el.findStatus.textContent = matches.length
    ? (index + 1) + ' of ' + matches.length
    : 'No results';
}

async function gotoMatch(index) {
  const { matches } = state.find;
  if (!matches.length) return;
  const i = (index + matches.length) % matches.length;
  state.find.index = i;
  const match = matches[i];

  goToPage(match.page);
  updateFindStatus();

  const p = state.pages[match.page - 1];
  await renderPage(p);
  highlightPage(p);

  const active = p.div.querySelector('mark.active');
  if (active) {
    const rect = active.getBoundingClientRect();
    const box = el.container.getBoundingClientRect();
    if (rect.top < box.top + 40 || rect.bottom > box.bottom - 20) {
      el.container.scrollTop += rect.top - box.top - el.container.clientHeight / 3;
    }
  }
}

function clearAllHighlights() {
  for (const p of state.pages) {
    if (!p.rendered) continue;
    clearHighlights(p.div);
  }
}

function clearHighlights(pageDiv) {
  const layer = pageDiv.querySelector('.textLayer');
  if (!layer) return;
  const marks = layer.querySelectorAll('mark');
  if (!marks.length) return;
  for (const mark of marks) {
    const parent = mark.parentNode;
    while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
    parent.removeChild(mark);
    parent.normalize();
  }
}

/**
 * Wrap every occurrence of the query inside a rendered page's text layer.
 * The layer's text nodes are walked once to build a flat string plus a
 * per-character map back into the DOM, so matches that span several spans
 * still highlight correctly.
 */
function highlightPage(p) {
  const layer = p.div.querySelector('.textLayer');
  if (!layer || !state.find.query) return;
  clearHighlights(p.div);

  const caseSensitive = el.findCase.checked;
  const needle = normalize(state.find.query, caseSensitive);
  if (!needle) return;

  const walker = document.createTreeWalker(layer, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
  let flat = '';
  const map = [];   // one entry per character: { node, offset } or null for synthetic
  let node = walker.nextNode();
  while (node) {
    if (node.nodeType === Node.TEXT_NODE) {
      const text = node.nodeValue;
      for (let i = 0; i < text.length; i++) {
        flat += text[i];
        map.push({ node, offset: i });
      }
    } else if (node.nodeName === 'BR') {
      flat += ' ';
      map.push(null);
    }
    node = walker.nextNode();
  }

  const hay = normalize(flat, caseSensitive);
  const ranges = [];
  let at = hay.indexOf(needle);
  while (at !== -1) {
    ranges.push([at, at + needle.length]);
    at = hay.indexOf(needle, at + Math.max(1, needle.length));
  }
  if (!ranges.length) return;

  const activeNth = state.find.index >= 0 && state.find.matches[state.find.index] &&
    state.find.matches[state.find.index].page === p.num
    ? state.find.matches[state.find.index].nth
    : -1;

  // Wrap back to front so earlier offsets stay valid as the DOM changes.
  for (let r = ranges.length - 1; r >= 0; r--) {
    const [start, end] = ranges[r];
    const runs = [];
    let run = null;
    for (let i = start; i < end; i++) {
      const entry = map[i];
      if (!entry) {
        run = null;
        continue;
      }
      if (run && run.node === entry.node && run.end === entry.offset) run.end = entry.offset + 1;
      else {
        run = { node: entry.node, start: entry.offset, end: entry.offset + 1 };
        runs.push(run);
      }
    }
    for (let k = runs.length - 1; k >= 0; k--) {
      const item = runs[k];
      try {
        const range = document.createRange();
        range.setStart(item.node, item.start);
        range.setEnd(item.node, item.end);
        const mark = document.createElement('mark');
        if (r === activeNth) mark.className = 'active';
        range.surroundContents(mark);
      } catch {
        /* layout changed under us; skip this run */
      }
    }
  }
}

function openFind() {
  el.findbar.hidden = false;
  document.body.classList.add('find-open');
  el.findInput.focus();
  el.findInput.select();
}

function closeFind() {
  el.findbar.hidden = true;
  document.body.classList.remove('find-open');
  state.find.query = '';
  state.find.matches = [];
  state.find.index = -1;
  el.findStatus.textContent = '';
  clearAllHighlights();
}

/* ================= recents and position memory ================= */

function positionKey(source) {
  return source.kind === 'path'
    ? 'pos:' + source.path
    : 'pos:file:' + source.name + ':' + (source.size || 0);
}

function lastPageFor(source) {
  if (source.kind === 'path') {
    const hit = state.recent.find((r) => r.path === source.path);
    if (hit && hit.page) return hit.page;
  }
  return prefs.get(positionKey(source), 1) || 1;
}

const savePosition = debounce(() => {
  if (!state.source) return;
  prefs.set(positionKey(state.source), state.current);
  if (state.source.kind === 'path') rememberOpen();
}, 700);

function rememberOpen() {
  if (!state.source || state.source.kind !== 'path') return;
  fetch('/api/recent', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      path: state.source.path,
      name: state.source.name,
      page: state.current,
      pages: state.pages.length,
    }),
  }).then(loadRecents).catch(() => {});
}

/* Resolves once the recent list has arrived. Held here because the list is
   fetched in parallel with opening a document but is needed at one precise
   point in that open: the page the document was last left on. */
let recentsReady = Promise.resolve();

async function loadRecents() {
  try {
    const res = await fetch('/api/recent');
    const body = await res.json();
    state.recent = body.recent || [];
  } catch {
    state.recent = [];
  }
  renderRecents();
}

function renderRecents() {
  el.recentMenu.textContent = '';
  el.dzRecent.textContent = '';

  if (!state.recent.length) {
    const empty = document.createElement('div');
    empty.className = 'menu-empty';
    empty.textContent = 'No recent files yet.';
    el.recentMenu.append(empty);
    return;
  }

  const makeItem = (item) => {
    const btn = document.createElement('button');
    btn.className = 'menu-item';
    btn.title = item.path;
    const name = document.createElement('span');
    name.className = 'mi-name';
    name.textContent = item.name;
    const meta = document.createElement('span');
    meta.className = 'mi-meta';
    meta.textContent = item.pages ? 'p. ' + item.page + ' / ' + item.pages : '';
    btn.append(name, meta);
    btn.addEventListener('click', () => {
      el.recentMenu.hidden = true;
      openByPath(item.path);
    });
    return btn;
  };

  for (const item of state.recent) el.recentMenu.append(makeItem(item));

  const title = document.createElement('div');
  title.className = 'dz-recent-title';
  title.textContent = 'Recent';
  el.dzRecent.append(title);
  for (const item of state.recent.slice(0, 6)) el.dzRecent.append(makeItem(item));
}

/* ================= actions ================= */

async function openDialog() {
  try {
    const res = await fetch('/api/open');
    const body = await res.json();
    if (body.files && body.files.length) {
      await openByPath(body.files[0]);
      if (body.files.length > 1) {
        toast('Opened the first of ' + body.files.length + ' files.');
      }
      return;
    }
    if (body.canceled) return;
  } catch {
    /* fall through to the browser picker */
  }
  el.fileInput.click();
}

function printDocument() {
  if (!state.source) return;
  if (shell && state.source.kind === 'path') {
    fetch('/api/print?path=' + encodeURIComponent(state.source.path)).catch(() => {});
    return;
  }
  const frame = document.createElement('iframe');
  frame.style.position = 'fixed';
  frame.style.width = '0';
  frame.style.height = '0';
  frame.style.border = '0';
  frame.src = state.source.url;
  frame.addEventListener('load', () => {
    try {
      frame.contentWindow.focus();
      frame.contentWindow.print();
    } catch {
      window.open(state.source.url, '_blank');
    }
    setTimeout(() => frame.remove(), 60000);
  });
  document.body.append(frame);
}

function downloadDocument() {
  if (!state.source) return;
  if (shell && state.source.kind === 'path') {
    fetch('/api/save?path=' + encodeURIComponent(state.source.path))
      .then((res) => res.json())
      .then((body) => { if (body.ok) toast('Saved to ' + body.path); })
      .catch(() => {});
    return;
  }
  const a = document.createElement('a');
  a.href = state.source.url;
  a.download = state.source.name;
  document.body.append(a);
  a.click();
  a.remove();
}

function setTheme(theme) {
  document.documentElement.dataset.theme = theme;
  prefs.set('theme', theme);
  tellShell({
    type: 'theme',
    background: getComputedStyle(document.documentElement).getPropertyValue('--bg').trim(),
  });
}

function setInverted(on) {
  document.body.classList.toggle('inverted', on);
  $('btn-invert').classList.toggle('on', on);
  prefs.set('inverted', on);
}

/* The window handles Explorer drops itself, because only it gets real file
   paths out of them, and it drives the overlay and the open through here. */
window.pdfviewHost = {
  open: (path) => { openByPath(path); },
  dragOverlay: (show) => { el.dragOverlay.hidden = !show; },
};

/* ================= events ================= */

$('btn-open').addEventListener('click', openDialog);
$('dz-open').addEventListener('click', openDialog);
$('btn-sidebar').addEventListener('click', () => toggleSidebar());
$('btn-prev').addEventListener('click', () => goToPage(stepSpread(state.current, -1)));
$('btn-next').addEventListener('click', () => goToPage(stepSpread(state.current, 1)));
$('btn-zoom-in').addEventListener('click', () => zoomBy(1));
$('btn-zoom-out').addEventListener('click', () => zoomBy(-1));
$('btn-find').addEventListener('click', () => (el.findbar.hidden ? openFind() : closeFind()));
$('btn-print').addEventListener('click', printDocument);
$('btn-download').addEventListener('click', downloadDocument);
$('btn-theme').addEventListener('click', () => {
  setTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
});
$('btn-invert').addEventListener('click', () => setInverted(!document.body.classList.contains('inverted')));
$('btn-help').addEventListener('click', () => el.helpDialog.showModal());
$('help-close').addEventListener('click', () => el.helpDialog.close());

$('btn-rotate').addEventListener('click', async () => {
  if (!state.doc) return;
  state.rotation = (state.rotation + 90) % 360;
  busy(true, 'Rotating');
  for (const p of state.pages) {
    const page = await state.doc.getPage(p.num);
    p.base = page.getViewport({ scale: 1, rotation: (page.rotate + state.rotation) % 360 });
  }
  applyFit();
  buildThumbs();
  busy(false);
});

$('btn-recent').addEventListener('click', (ev) => {
  ev.stopPropagation();
  el.recentMenu.hidden = !el.recentMenu.hidden;
});

document.addEventListener('click', (ev) => {
  if (!el.recentMenu.hidden && !el.recentMenu.contains(ev.target)) el.recentMenu.hidden = true;
});

el.spread.addEventListener('change', () => setSpread(el.spread.value));

el.zoom.addEventListener('change', () => {
  const value = el.zoom.value;
  if (value === 'width' || value === 'page') {
    state.fitMode = value;
    prefs.set('fitMode', value);
    applyFit();
  } else if (value !== 'custom') {
    setScale(parseFloat(value), 'custom');
  }
});

el.pageNum.addEventListener('change', () => {
  const n = parseInt(el.pageNum.value, 10);
  if (Number.isFinite(n)) goToPage(n);
  el.pageNum.value = String(state.current);
});
el.pageNum.addEventListener('focus', () => el.pageNum.select());

for (const tab of document.querySelectorAll('.side-tab')) {
  tab.addEventListener('click', () => {
    for (const t of document.querySelectorAll('.side-tab')) t.classList.toggle('active', t === tab);
    el.thumbs.hidden = tab.dataset.tab !== 'thumbs';
    el.outline.hidden = tab.dataset.tab !== 'outline';
  });
}

el.findInput.addEventListener('input', () => runFind(true));
el.findCase.addEventListener('change', () => runFind(true));
$('find-next').addEventListener('click', () => gotoMatch(state.find.index + 1));
$('find-prev').addEventListener('click', () => gotoMatch(state.find.index - 1));
$('find-close').addEventListener('click', closeFind);
el.findInput.addEventListener('keydown', (ev) => {
  if (ev.key === 'Enter') {
    ev.preventDefault();
    gotoMatch(state.find.index + (ev.shiftKey ? -1 : 1));
  } else if (ev.key === 'Escape') {
    closeFind();
    el.container.focus();
  }
});

el.fileInput.addEventListener('change', () => {
  const file = el.fileInput.files && el.fileInput.files[0];
  if (file) openLocalFile(file);
  el.fileInput.value = '';
});

/* drag and drop */
let dragDepth = 0;
window.addEventListener('dragenter', (ev) => {
  ev.preventDefault();
  dragDepth++;
  el.dragOverlay.hidden = false;
});
window.addEventListener('dragover', (ev) => ev.preventDefault());
window.addEventListener('dragleave', (ev) => {
  ev.preventDefault();
  dragDepth = Math.max(0, dragDepth - 1);
  if (!dragDepth) el.dragOverlay.hidden = true;
});
window.addEventListener('drop', (ev) => {
  ev.preventDefault();
  dragDepth = 0;
  el.dragOverlay.hidden = true;
  const file = ev.dataTransfer && ev.dataTransfer.files && ev.dataTransfer.files[0];
  if (!file) return;
  if (file.type && file.type !== 'application/pdf' && !/\.pdf$/i.test(file.name)) {
    toast('That is not a PDF file.', true);
    return;
  }
  openLocalFile(file);
});

/* ctrl + wheel zoom */
el.container.addEventListener('wheel', (ev) => {
  if (!ev.ctrlKey || !state.doc) return;
  ev.preventDefault();
  zoomBy(ev.deltaY < 0 ? 1 : -1);
}, { passive: false });

/* keyboard */
document.addEventListener('keydown', (ev) => {
  const typing = /^(INPUT|SELECT|TEXTAREA)$/.test(ev.target.tagName);

  if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'o') {
    ev.preventDefault();
    openDialog();
    return;
  }
  if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'f') {
    ev.preventDefault();
    openFind();
    return;
  }
  if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'g') {
    ev.preventDefault();
    el.pageNum.focus();
    return;
  }
  if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'p') {
    ev.preventDefault();
    printDocument();
    return;
  }
  if (typing) return;
  if (!state.doc && !['?', 't'].includes(ev.key)) return;

  switch (ev.key) {
    case 'PageDown':
      ev.preventDefault();
      goToPage(stepSpread(state.current, 1));
      break;
    case 'PageUp':
      ev.preventDefault();
      goToPage(stepSpread(state.current, -1));
      break;
    case 'Home':
      ev.preventDefault();
      goToPage(1);
      break;
    case 'End':
      ev.preventDefault();
      goToPage(state.pages.length);
      break;
    case 'ArrowRight':
      ev.preventDefault();
      goToPage(stepSpread(state.current, 1));
      break;
    case 'ArrowLeft':
      ev.preventDefault();
      goToPage(stepSpread(state.current, -1));
      break;
    case 'ArrowDown':
      el.container.scrollTop += 80;
      break;
    case 'ArrowUp':
      el.container.scrollTop -= 80;
      break;
    case ' ':
      ev.preventDefault();
      el.container.scrollTop += el.container.clientHeight * (ev.shiftKey ? -0.9 : 0.9);
      break;
    case '+':
    case '=':
      zoomBy(1);
      break;
    case '-':
      zoomBy(-1);
      break;
    case '0':
      state.fitMode = 'width';
      applyFit();
      break;
    case 'd':
    case 'D': {
      const order = ['off', 'two', 'cover'];
      setSpread(order[(order.indexOf(state.spread) + 1) % order.length]);
      break;
    }
    case 'r':
    case 'R':
      $('btn-rotate').click();
      break;
    case 's':
    case 'S':
      toggleSidebar();
      break;
    case 't':
    case 'T':
      $('btn-theme').click();
      break;
    case 'i':
    case 'I':
      $('btn-invert').click();
      break;
    case 'f':
    case 'F':
      if (document.fullscreenElement) document.exitFullscreen();
      else document.documentElement.requestFullscreen().catch(() => {});
      break;
    case '?':
      el.helpDialog.showModal();
      break;
    case 'Escape':
      if (!el.findbar.hidden) closeFind();
      break;
    default:
      break;
  }
});

const onResize = debounce(() => {
  if (state.pages.length && (state.fitMode === 'width' || state.fitMode === 'page')) applyFit();
}, 150);
window.addEventListener('resize', onResize);

/* ================= toolbar overflow ================= */

/* A narrow window used to push the right-hand buttons off the edge, where they
   were clipped with nothing to say they were there. They now move into a menu,
   least useful first. The buttons themselves move, so their listeners and
   their disabled state come with them. */

const RIGHT_ORDER = ['btn-find', 'btn-print', 'btn-download', 'btn-invert', 'btn-theme', 'btn-help'];
const OVERFLOW_ORDER = ['btn-help', 'btn-theme', 'btn-invert', 'btn-download', 'btn-print', 'btn-find'];

const toolbar = document.querySelector('.toolbar');
const rightGroup = document.querySelector('.tb-group.right');
const overflowBtn = $('btn-overflow');
const overflowWrap = overflowBtn.parentElement;
const overflowMenu = $('overflow-menu');

function fitToolbar() {
  for (const id of RIGHT_ORDER) rightGroup.insertBefore($(id), overflowWrap);
  el.title.hidden = false;
  overflowMenu.hidden = true;
  overflowBtn.hidden = true;

  const tooWide = () => toolbar.scrollWidth > toolbar.clientWidth + 1;
  if (!tooWide()) return;

  /* The title goes first. Squeezed down it shows half a letter, and the window
     title bar carries the same name anyway. */
  el.title.hidden = true;
  if (!tooWide()) return;

  overflowBtn.hidden = false;
  for (const id of OVERFLOW_ORDER) {
    if (!tooWide()) break;
    overflowMenu.prepend($(id));
  }
}

let fitQueued = false;
function queueFit() {
  if (fitQueued) return;
  fitQueued = true;
  requestAnimationFrame(() => {
    fitQueued = false;
    fitToolbar();
  });
}

window.addEventListener('resize', queueFit);

overflowBtn.addEventListener('click', (ev) => {
  ev.stopPropagation();
  overflowMenu.hidden = !overflowMenu.hidden;
});

overflowMenu.addEventListener('click', () => { overflowMenu.hidden = true; });

document.addEventListener('click', (ev) => {
  if (!overflowMenu.hidden && !overflowWrap.contains(ev.target)) overflowMenu.hidden = true;
});

queueFit();

/* ================= startup ================= */

(async function start() {
  mark('start');
  setTheme(prefs.get('theme', matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'));
  setInverted(prefs.get('inverted', false));
  setSpread(prefs.get('spread', 'off'));
  state.fitMode = prefs.get('fitMode', 'width');
  state.scale = prefs.get('scale', 1);
  toggleSidebar(prefs.get('sidebar', false));
  setEnabled(false);
  updateZoomSelect();

  /* The recent list feeds a menu nobody has opened yet. Opening the document
     is what the window is for, so the two go at once rather than in order. */
  recentsReady = loadRecents().then(() => mark('recents loaded'));

  const params = new URLSearchParams(location.search);
  const path = params.get('path');
  if (path) {
    history.replaceState(null, '', '/');
    openByPath(path);
  } else {
    await recentsReady;
    mark('idle');
  }
})();
