'use strict';
// Die mobile Seite von FerrumPix, Gegenstelle zu Services/BrowserBridgeMobile*.vb. Ohne fremde
// Bibliotheken. Vier Reiter (Fotos, Ordner, Suche, Mehr), ein Raster mit Fenstertechnik (nur die
// sichtbaren Kacheln stehen im Dokument), ein Vollbild mit Wischen und Zoomen.
//
// Bilder laedt eine eigene Schlange mit AbortController: was aus dem Blick scrollt, wird
// abgebrochen und dem Server gemeldet (/api/m/cancel), damit er es nicht mehr dekodiert.
(() => {
  const T = JSON.parse(document.getElementById('fp-texts').textContent || '{}');
  const $ = id => document.getElementById(id);
  const lang = document.documentElement.lang || 'en';
  const fmt = (text, ...args) => String(text || '').replace(/\{(\d+)\}/g, (m, i) => (args[+i] ?? ''));
  const variantKey = 'ferrumpix.variant';

  // --- Diagnose ----------------------------------------------------------------------------
  // Mit ?debug=1 steht oben eine Zeile mit Messwerten des Rasters der Zeitleiste: Breite,
  // Spalten, Hoehe, welches Element scrollt, geladene Tage und Bilder, Kacheln im Dokument und
  // woher die Kacheln kamen (Kopf X-FerrumPix-Thumb). Nur Zahlen, keine Namen oder Pfade.
  const diag = { on: /[?&]debug=1\b/.test(location.search), node: null, grid: null, source: null, renders: 0, thumbs: {} };
  function showDiag() {
    if (!diag.on) return;
    if (!diag.node) {
      diag.node = el('div', 'debug-line');
      diag.node.id = 'fp-debug';
      document.body.appendChild(diag.node);
    }
    const g = diag.grid, s = diag.source;
    const sc = g ? g.scroller : null;
    const loaded = s && s.items ? s.items.reduce(n => n + 1, 0) : 0;
    const parts = [
      'w=' + (g ? Math.round(g.root.clientWidth) : '-') + '/' + (sc ? sc.clientWidth : '-'),
      'h=' + (sc ? sc.clientHeight : '-'),
      'cols=' + (g && g.cols ? g.cols : '-'),
      'size=' + (g && g.size ? g.size.toFixed(1) : '-'),
      'zeilenH=' + (g && g.size ? (g.size + GAP).toFixed(1) : '-'),
      'zoom=' + (g ? g.zoom.toFixed(2) : '-'),
      'gezeichnet=' + (g ? g.renderedRows : '-'),
      'grid=' + (g && g.height ? Math.round(g.height) : '-'),
      'scroll=' + (sc ? Math.round(sc.scrollTop) + '/' + sc.scrollHeight + (sc.scrollHeight > sc.clientHeight ? ' el' : ' -') : '-'),
      'doc=' + document.scrollingElement.scrollHeight + '/' + window.innerHeight,
      'tage=' + (s ? s.sections.length : '-'),
      'bilder=' + loaded + '/' + (s ? s.total : '-'),
      'zeilen=' + (g && g.rows ? g.rows.length : '-'),
      'kacheln=' + (g ? g.tiles.size : '-'),
      'renders=' + diag.renders,
      'thumb ' + (Object.keys(diag.thumbs).map(k => k + ':' + diag.thumbs[k]).join(' ') || '-')
    ];
    diag.node.textContent = parts.join('  ');
  }
  if (diag.on) setInterval(showDiag, 1000);

  // --- Symbole -----------------------------------------------------------------------------
  const ICONS = {
    photos: '<rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="M21 15l-5-5L5 21"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
    more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    back: '<path d="M15 18l-6-6 6-6"/>',
    forward: '<path d="M9 18l6-6-6-6"/>',
    person: '<circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 4-6 8-6s8 2 8 6"/>',
    close: '<path d="M18 6L6 18M6 6l12 12"/>',
    star: '<path d="M12 3l2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/>',
    heart: '<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21.2l8.8-8.8a5.5 5.5 0 0 0 0-7.8z"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 16v-5M12 8h.01"/>',
    download: '<path d="M12 3v12M7 10l5 5 5-5M5 21h14"/>',
    share: '<path d="M4 12v7a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7M16 6l-4-4-4 4M12 2v13"/>',
    upload: '<path d="M12 21V9M7 14l5-5 5 5M5 3h14"/>',
    check: '<path d="M20 6L9 17l-5-5"/>',
    play: '<path d="M8 5v14l11-7z"/>',
    desktop: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>',
    signout: '<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9"/>',
    lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
    shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
    place: '<path d="M12 21s-7-6.2-7-11a7 7 0 0 1 14 0c0 4.8-7 11-7 11z"/><circle cx="12" cy="10" r="2.5"/>',
    select: '<circle cx="12" cy="12" r="9"/><path d="M8 12l3 3 5-6"/>',
    image: '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M21 15l-5-5L5 21"/>',
    rename: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13 7l4 4"/>',
    trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
    language: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>',
    missing: '<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z"/><path d="M14 3v6h6M9.5 12.5l5 5M14.5 12.5l-5 5"/>',
    unreadable: '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M21 15l-5-5L5 21M3 3l18 18"/>',
    video: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M10 9v6l5-3z"/>',
    live: '<circle cx="12" cy="12" r="2.5"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="9.5" stroke-dasharray="1.5 2.6"/>',
    sound: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
    muted: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9.5l5 5M22 9.5l-5 5"/>'
  };
  // Behalten und Verwerfen mit denselben Symbolen wie die Galerie der Anwendung: die Daumen aus
  // Assets/Icons/phosphor/bold (thumbs-up-bold.svg, thumbs-down-bold.svg), gefuellt im Raster 256.
  // Die Farbe (gruen bzw. rot wie in der Galerie) setzt app.css.
  const FILLED_ICONS = {
    keep: '<path d="M237,77.47A28,28,0,0,0,216,68H164V56a44.05,44.05,0,0,0-44-44,12,12,0,0,0-10.73,6.63L72.58,92H32a20,20,0,0,0-20,20v88a20,20,0,0,0,20,20H204a28,28,0,0,0,27.78-24.53l12-96A28,28,0,0,0,237,77.47ZM36,116H68v80H36ZM220,96.5l-12,96a4,4,0,0,1-4,3.5H92V106.83L126.82,37.2A20,20,0,0,1,140,56V80a12,12,0,0,0,12,12h64a4,4,0,0,1,4,4.5Z"/>',
    reject: '<path d="M243.78,156.53l-12-96A28,28,0,0,0,204,36H32A20,20,0,0,0,12,56v88a20,20,0,0,0,20,20H72.58l36.69,73.37A12,12,0,0,0,120,244a44.05,44.05,0,0,0,44-44V188h52a28,28,0,0,0,27.78-31.47ZM68,140H36V60H68Zm151,22.65a4,4,0,0,1-3,1.35H152a12,12,0,0,0-12,12v24a20,20,0,0,1-13.18,18.8L92,149.17V60H204a4,4,0,0,1,4,3.5l12,96A4,4,0,0,1,219,162.65Z"/>'
  };

  // Ein langer Dateiname, in der Mitte gekuerzt: Anfang und Endung bleiben lesbar.
  function middleEllipsis(text, max) {
    text = String(text || '');
    if (max < 8 || text.length <= max) return text;
    const tail = Math.max(4, Math.floor((max - 1) * 0.45));
    return text.slice(0, max - 1 - tail) + '…' + text.slice(text.length - tail);
  }
  // Wie viele Zeichen in die Breite eines Elements passen (grob, nach der Schriftgroesse).
  function charsFor(node, fontPx) {
    const width = node.clientWidth || 0;
    return width > 0 ? Math.floor(width / (fontPx * 0.56)) : 40;
  }
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('aria-hidden', 'true');
    if (FILLED_ICONS[name]) {
      svg.setAttribute('viewBox', '0 0 256 256');
      svg.setAttribute('class', 'filled');
      svg.innerHTML = FILLED_ICONS[name];
      return svg;
    }
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.innerHTML = ICONS[name] || '';
    return svg;
  }
  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }
  function iconButton(name, label, onClick) {
    const button = el('button', 'icon');
    button.type = 'button';
    button.appendChild(icon(name));
    if (label) { button.setAttribute('aria-label', label); button.title = label; }
    if (onClick) button.addEventListener('click', onClick);
    return button;
  }

  // --- Schnittstelle -----------------------------------------------------------------------
  // Ohne Sitzung antwortet sie 401; neu laden zeigt dann die Anmeldung.
  async function api(path, options) {
    const response = await fetch(path, Object.assign({ credentials: 'same-origin', cache: 'no-store' }, options || {}));
    if (response.status === 401) {
      location.reload();
      throw new Error('signed out');
    }
    return response;
  }
  // Mit Frist (Millisekunden) bricht die Anfrage danach ab und meldet "timeout", statt die Seite
  // unbestimmt warten zu lassen.
  async function getJson(path, timeoutMs) {
    const ctrl = timeoutMs ? new AbortController() : null;
    const timer = ctrl ? setTimeout(() => ctrl.abort(), timeoutMs) : 0;
    try {
      const response = await api(path, ctrl ? { signal: ctrl.signal } : undefined);
      if (!response.ok) throw new Error('HTTP ' + response.status);
      return await response.json();
    } catch (error) {
      if (ctrl && ctrl.signal.aborted) throw new Error('timeout');
      throw error;
    } finally {
      clearTimeout(timer);
    }
  }
  async function postJson(path, body) {
    const response = await api(path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {})
    });
    let answer = {};
    try { answer = await response.json(); } catch (ignored) { }
    if (!response.ok) throw new Error(answer.error || String(response.status));
    return answer;
  }

  let toastTimer = 0;
  function toast(text) {
    const box = $('toast');
    box.textContent = text;
    box.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { box.hidden = true; }, 2800);
  }

  function download(url) {
    const link = document.createElement('a');
    link.href = url;
    link.download = '';
    link.rel = 'noopener';
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  // --- Datum -------------------------------------------------------------------------------
  // Kopfzeile eines Tags im Raster, kurz in der Sprache des Benutzers ("Sa., 27. Aug. 2022").
  const dayFormat = new Intl.DateTimeFormat(lang, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
  const monthFormat = new Intl.DateTimeFormat(lang, { month: 'long', year: 'numeric' });
  const fullFormat = new Intl.DateTimeFormat(lang, { dateStyle: 'full', timeStyle: 'short' });
  const shortFormat = new Intl.DateTimeFormat(lang, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  function parseDay(day) {
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(day || '');
    return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
  }
  function parseIso(text) {
    const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})/.exec(text || '');
    return m ? new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]) : null;
  }
  function dayTitle(day) {
    const date = parseDay(day);
    return date ? dayFormat.format(date) : T.noDate;
  }
  function monthTitle(day) {
    const date = parseDay(day);
    return date ? monthFormat.format(date) : T.noDate;
  }
  // Abschnitte je Tag aus einer Liste von Bildern (Ordner, Suche): aufeinanderfolgende Bilder
  // desselben Tags bilden einen Abschnitt.
  function sectionsOf(items) {
    const sections = [];
    for (const item of items) {
      const day = (item.t || '').slice(0, 10);
      const last = sections[sections.length - 1];
      if (last && last.day === day) last.count++;
      else sections.push({ day, count: 1 });
    }
    return sections;
  }
  function formatBytes(bytes) {
    if (!(bytes > 0)) return '';
    const units = ['B', 'KB', 'MB', 'GB'];
    let value = bytes, unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return new Intl.NumberFormat(lang, { maximumFractionDigits: unit === 0 ? 0 : 1 }).format(value) + ' ' + units[unit];
  }

  // --- Bilder laden mit Abbruch ------------------------------------------------------------
  // Eine Schlange je Art: Kacheln bis zu vier gleichzeitig, Vorschauen zwei. Abgebrochen wird im
  // Browser (AbortController) und am Server: wer schon unterwegs war, wird gesammelt gemeldet.
  const pendingCancels = new Set();
  let cancelTimer = 0;
  function reportCancels() {
    cancelTimer = 0;
    if (pendingCancels.size === 0) return;
    const ids = Array.from(pendingCancels).slice(0, 500);
    ids.forEach(id => pendingCancels.delete(id));
    fetch('/api/m/cancel', {
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ r: ids })
    }).catch(() => { });
    if (pendingCancels.size > 0) cancelTimer = setTimeout(reportCancels, 100);
  }
  function randomId() {
    const bytes = new Uint8Array(9);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  }
  class Loader {
    constructor(limit) { this.limit = limit; this.active = 0; this.queue = []; }
    // Liefert einen Griff mit cancel(); onDone(blob) oder onFail(). low: nur Vorbereitung (das
    // Bild neben dem im Vollbild), der Server rechnet sie erst nach allem anderen.
    load(url, onDone, onFail, front, low) {
      const job = { url, onDone, onFail, rid: randomId(), ctrl: null, finished: false, cancelled: false, low: !!low };
      job.cancel = () => this.cancel(job);
      if (front) this.queue.unshift(job); else this.queue.push(job);
      this.pump();
      return job;
    }
    pump() {
      while (this.active < this.limit && this.queue.length > 0) this.start(this.queue.shift());
    }
    async start(job) {
      this.active++;
      job.ctrl = new AbortController();
      try {
        // Die Kennung der Anfrage (fuer den Abbruch am Server) geht als Kopf mit, nicht in der
        // Adresse: sonst waere jede Adresse neu, und der Zwischenspeicher des Browsers (ETag,
        // max-age) griffe nie; jede Kachel kaeme bei jedem Scrollen neu vom Server.
        const headers = { 'X-FerrumPix-Request': job.rid };
        if (job.low) headers['X-FerrumPix-Priority'] = 'low';
        const response = await fetch(job.url, { credentials: 'same-origin', signal: job.ctrl.signal, headers });
        if (response.status === 401) { location.reload(); return; }
        // Woher die Kachel kam (Speicher der Galerie, angelegt, Vorschau), fuer die Diagnosezeile;
        // bei einer Kachel ohne Bild, warum ("missing", "unreadable", "cancelled", "busy").
        const kind = response.headers.get('X-FerrumPix-Thumb');
        if (kind) diag.thumbs[kind] = (diag.thumbs[kind] || 0) + 1;
        if (!response.ok) {
          const failure = new Error(String(response.status));
          failure.status = response.status;
          failure.kind = kind || '';
          throw failure;
        }
        const blob = await response.blob();
        job.finished = true;
        if (!job.cancelled) job.onDone(blob);
      } catch (error) {
        job.finished = true;
        if (!job.cancelled && job.onFail) job.onFail(error);
      } finally {
        this.active--;
        this.pump();
      }
    }
    cancel(job) {
      if (job.cancelled || job.finished) return;
      job.cancelled = true;
      const waiting = this.queue.indexOf(job);
      if (waiting >= 0) { this.queue.splice(waiting, 1); return; }
      if (job.ctrl) {
        job.ctrl.abort();
        pendingCancels.add(job.rid);
        if (!cancelTimer) cancelTimer = setTimeout(reportCancels, 120);
      }
    }
  }
  const tileLoader = new Loader(4);
  const previewLoader = new Loader(2);

  // Eine Kachel ohne Bild zeigt ein Symbol statt einer grauen Flaeche: "missing" (die Datei gibt
  // es nicht mehr), "unreadable" (das Format liess sich nicht lesen), "video" (ein Film ohne
  // Standbild). Leer nimmt das Symbol wieder weg.
  function setFallback(tile, kind) {
    const old = tile.node.querySelector('.fallback');
    if (old) old.remove();
    tile.node.classList.toggle('failed', !!kind);
    if (!kind) return;
    const box = el('div', 'fallback');
    box.appendChild(icon(kind));
    const text = kind === 'missing' ? T.missing : kind === 'video' ? T.video : T.unreadable;
    box.appendChild(el('span', null, text));
    tile.node.appendChild(box);
  }

  const thumbUrl = item => '/api/m/thumb?id=' + encodeURIComponent(item.i) + '&s=' + encodeURIComponent(item.s || '');
  const previewEdge = Math.min(2048, Math.round(Math.max(screen.width || 1024, screen.height || 1024) * (window.devicePixelRatio || 1)));
  const previewUrl = item => '/api/m/preview?id=' + encodeURIComponent(item.i) + '&s=' + encodeURIComponent(item.s || '') + '&w=' + previewEdge;

  // --- Quellen -------------------------------------------------------------------------------
  // Eine Quelle beschreibt eine Liste von Bildern: Anzahl, Abschnitte, Bild an Stelle i, Nachladen.
  class ListSource {
    constructor(items) {
      this.items = items || [];
      this.total = this.items.length;
      this.sections = sectionsOf(this.items);
    }
    item(i) { return this.items[i]; }
    ensure() { return Promise.resolve(); }
  }
  // Die Zeitleiste: die Tage samt Anzahl kommen mit der ersten Seite, die Bilder seitenweise.
  class TimelineSource {
    constructor() { this.items = []; this.total = 0; this.sections = []; this.pages = new Map(); this.stamp = ''; this.pageSize = 200; }
    async init() {
      const first = await getJson('/api/m/timeline?offset=0&limit=' + this.pageSize + '&days=1', 60000);
      if (!first || !Array.isArray(first.items) || typeof first.total !== 'number') throw new Error('bad answer');
      this.stamp = first.stamp;
      this.total = first.total;
      this.sections = (first.days || []).map(d => ({ day: d[0], count: d[1] }));
      this.items = new Array(this.total);
      this.store(first);
      this.pages.set(0, Promise.resolve());
    }
    store(page) {
      (page.items || []).forEach((item, k) => { this.items[page.offset + k] = item; });
    }
    item(i) { return this.items[i]; }
    // Fehlt im Bereich ein Bild, dessen Seite noch nicht angefragt ist?
    missing(first, last) {
      for (let page = Math.floor(first / this.pageSize); page * this.pageSize <= last && page * this.pageSize < this.total; page++) {
        if (!this.pages.has(page)) return true;
      }
      return false;
    }
    ensure(first, last) {
      const waits = [];
      for (let page = Math.floor(first / this.pageSize); page * this.pageSize <= last && page * this.pageSize < this.total; page++) {
        if (!this.pages.has(page)) {
          const offset = page * this.pageSize;
          const pending = getJson('/api/m/timeline?offset=' + offset + '&limit=' + this.pageSize).then(answer => {
            if (answer.stamp !== this.stamp) { this.onChanged && this.onChanged(); return; }
            this.store(answer);
          }).catch(() => { this.pages.delete(page); });
          this.pages.set(page, pending);
        }
        waits.push(this.pages.get(page));
      }
      return Promise.all(waits);
    }
  }

  // --- Raster --------------------------------------------------------------------------------
  // Fenstertechnik: das Raster rechnet die Lage jeder Zeile aus Abschnitten und Spalten und haelt
  // nur die Kacheln im Dokument, die in der Naehe des sichtbaren Bereichs liegen.
  const HEAD = 44, GAP = 2;
  // So viele Bilder fasst eine Auswahl hoechstens, dieselbe Grenze wie der Server fuer Markieren,
  // Loeschen und eine Freigabe (BrowserBridge.MaxMobileSelection).
  const MAX_SELECTION = Number(T.maxSelection) || 2000;
  // Spalten nach Breite: eine Kachel soll auf dem Telefon etwa 105 Punkte breit sein (3 bis 4
  // Spalten), sonst etwa 190 (160 bis 220). zoom ist der Faktor aus STRG+Rad.
  function columnsFor(width, zoom) {
    const target = (width < 600 ? 105 : 190) * zoom;
    return Math.max(2, Math.min(40, Math.round((width + GAP) / (target + GAP))));
  }
  const ZOOM_KEY = 'fp-grid-zoom';
  function storedZoom() {
    try { const z = Number(localStorage.getItem(ZOOM_KEY)); return z >= 0.4 && z <= 3 ? z : 1; } catch (ignored) { return 1; }
  }
  class Grid {
    constructor(host, scroller, options) {
      this.host = host;
      this.scroller = scroller;
      this.options = options || {};
      this.root = el('div', 'grid');
      host.appendChild(this.root);
      this.tiles = new Map();
      this.heads = new Map();
      // Die Auswahl: Kennung zu Bild (das Bild traegt Etikett und Stufe "r" fuer die
      // Aktionsleiste). selecting ist der Auswahlmodus, auch mit null Bildern (Knopf in der
      // Kopfleiste); anchorIndex die Stelle fuer SHIFT+Klick.
      this.selected = new Map();
      this.selecting = false;
      this.anchorIndex = -1;
      this.source = null;
      this.raf = 0;
      this.zoom = storedZoom();
      this.renderedRows = 0;
      this.onScroll = () => { this.schedule(); if (this.scrubber) this.scrubber.flash(); };
      scroller.addEventListener('scroll', this.onScroll, { passive: true });
      this.resizeObserver = new ResizeObserver(() => { if (this.source) { this.layout(); this.schedule(); } });
      this.resizeObserver.observe(scroller);
      // STRG+Rad (und Spreizen auf dem Touchpad, das der Browser als STRG+Rad meldet) aendert die
      // Kachelgroesse des Rasters statt die ganze Seite zu vergroessern. Spreizen vergroesserte
      // sonst den sichtbaren Ausschnitt ohne neues Layout: riesige Kacheln, seitlich angeschnitten.
      scroller.addEventListener('wheel', event => {
        if (!event.ctrlKey || !this.source) return;
        event.preventDefault();
        const zoom = Math.min(3, Math.max(0.4, this.zoom * Math.exp(-event.deltaY * 0.002)));
        if (Math.abs(zoom - this.zoom) < 0.001) return;
        this.zoom = zoom;
        try { localStorage.setItem(ZOOM_KEY, String(zoom)); } catch (ignored) { }
        this.layout();
        this.schedule();
      }, { passive: false });
      if (this.options.scrubber) {
        host.classList.add('with-rail');
        scroller.classList.add('railed');
        this.scrubber = new Scrubber(this);
      }
      // ESC beendet die Auswahl, wenn kein Vollbild und kein Blatt offen ist.
      document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !this.selecting || !this.host.offsetParent) return;
        if (!$('viewer').hidden || !$('action-sheet').hidden) return;
        this.clearSelection();
      });
    }
    // keep: dieselbe Liste neu geladen (Zeitleiste nach einer Aenderung im Katalog); die Lage
    // bleibt, statt nach oben zu springen, und die Auswahl bleibt (die Kennungen gelten im selben
    // Lauf weiter).
    setSource(source, keep) {
      const anchor = keep ? this.anchor() : -1;
      this.clear();
      this.source = source;
      if (!keep) {
        this.selected.clear();
        this.selecting = false;
        this.anchorIndex = -1;
      }
      this.updateSelection();
      this.layout(anchor);
      this.schedule();
    }
    clear() {
      for (const tile of this.tiles.values()) this.dropTile(tile);
      this.tiles.clear();
      for (const head of this.heads.values()) head.remove();
      this.heads.clear();
      this.rows = [];
      this.headList = [];
    }
    // Das erste Bild der obersten sichtbaren Zeile, damit eine neue Spaltenzahl (Breite, Zoom)
    // oder ein Neuladen dort weitermacht und nicht irgendwo.
    anchor() {
      if (!this.rows || this.rows.length === 0 || this.scroller.scrollTop <= 0) return -1;
      const row = this.rows[Math.min(this.rowAt(this.scroller.scrollTop - this.root.offsetTop), this.rows.length - 1)];
      return row ? row.start : -1;
    }
    layout(anchorIndex) {
      const width = this.root.clientWidth || this.scroller.clientWidth;
      if (!width) return;
      const anchor = anchorIndex === undefined ? this.anchor() : anchorIndex;
      const cols = columnsFor(width, this.zoom);
      const size = (width - (cols - 1) * GAP) / cols;
      if (cols !== this.cols || Math.abs(size - (this.size || 0)) > 0.5) {
        for (const tile of this.tiles.values()) this.dropTile(tile);
        this.tiles.clear();
      }
      const changed = cols !== this.cols;
      this.cols = cols;
      this.size = size;
      const rows = [], heads = [];
      let y = 0, index = 0;
      const titled = this.options.titles !== false;
      for (const section of (this.source ? this.source.sections : [])) {
        if (titled) { heads.push({ y, day: section.day, index, count: section.count }); y += HEAD; }
        const count = section.count;
        for (let r = 0; r * cols < count; r++) {
          rows.push({ y, start: index + r * cols, count: Math.min(cols, count - r * cols), section: heads.length - 1, day: section.day });
          y += size + GAP;
        }
        index += count;
      }
      this.rows = rows;
      this.headList = heads;
      this.height = y + 16;
      this.root.style.height = this.height + 'px';
      for (const head of this.heads.values()) head.remove();
      this.heads.clear();
      if (anchor >= 0 && (changed || anchorIndex !== undefined)) {
        const row = this.rowOf(anchor);
        if (row) this.scroller.scrollTop = row.y + this.root.offsetTop;
      }
      if (this.scrubber) this.scrubber.rebuild();
    }
    // Die Zeile mit dem Bild an Stelle index (binaere Suche, die Zeilen sind nach start geordnet).
    rowOf(index) {
      let low = 0, high = this.rows.length - 1;
      while (low <= high) {
        const mid = (low + high) >> 1, row = this.rows[mid];
        if (index < row.start) high = mid - 1;
        else if (index >= row.start + row.count) low = mid + 1;
        else return row;
      }
      return null;
    }
    schedule() {
      if (this.raf) return;
      this.raf = requestAnimationFrame(() => { this.raf = 0; this.render(); });
    }
    // Erste Zeile, die bei y oder darunter endet.
    rowAt(y) {
      let low = 0, high = this.rows.length - 1, found = this.rows.length;
      while (low <= high) {
        const mid = (low + high) >> 1;
        if (this.rows[mid].y + this.size >= y) { found = mid; high = mid - 1; } else low = mid + 1;
      }
      return found;
    }
    render() {
      if (!this.source || !this.rows) return;
      if (this.scrubber) this.scrubber.update();
      const top = this.scroller.scrollTop - this.root.offsetTop;
      const view = this.scroller.clientHeight;
      const from = top - view * 0.6, to = top + view * 1.6;
      const wanted = new Set();
      let first = -1, last = -1;
      this.renderedRows = 0;
      for (let r = this.rowAt(from); r < this.rows.length && this.rows[r].y <= to; r++) {
        const row = this.rows[r];
        this.renderedRows++;
        for (let k = 0; k < row.count; k++) {
          const index = row.start + k;
          wanted.add(index);
          if (first < 0) first = index;
          last = index;
          let tile = this.tiles.get(index);
          if (!tile) { tile = this.makeTile(index); this.tiles.set(index, tile); }
          tile.node.style.transform = `translate(${k * (this.size + GAP)}px, ${row.y}px)`;
          this.fillTile(tile);
        }
      }
      for (const [index, tile] of this.tiles) {
        if (!wanted.has(index)) { this.dropTile(tile); this.tiles.delete(index); }
      }
      // Kopfzeilen der Abschnitte im Fenster.
      const wantedHeads = new Set();
      for (let h = 0; h < this.headList.length; h++) {
        const head = this.headList[h];
        if (head.y + HEAD < from || head.y > to) continue;
        wantedHeads.add(h);
        let node = this.heads.get(h);
        if (!node) {
          // Vorn der Kreis mit Haken: waehlt den ganzen Tag aus oder ab, auch Bilder, die noch
          // nicht geladen sind (selectRange holt sie). Zu sehen im Auswahlmodus und mit der Maus
          // beim Zeigen auf die Kopfzeile (app.css).
          node = el('div', 'grid-head');
          const check = el('button', 'day-check');
          check.type = 'button';
          check.appendChild(icon('check'));
          check.setAttribute('aria-label', T.selectDay || '');
          check.title = T.selectDay || '';
          check.addEventListener('click', event => { event.stopPropagation(); this.toggleDay(h); });
          node.appendChild(check);
          node.appendChild(el('span', null, dayTitle(head.day)));
          node.style.transform = `translateY(${head.y}px)`;
          this.root.appendChild(node);
          this.heads.set(h, node);
        }
        const state = this.dayState(head);
        node.classList.toggle('all', state === 'all');
        node.classList.toggle('some', state === 'some');
      }
      for (const [h, node] of this.heads) {
        if (!wantedHeads.has(h)) { node.remove(); this.heads.delete(h); }
      }
      // Fehlen Bilder im Fenster, werden ihre Seiten geholt und danach neu gezeichnet. Nur dann:
      // vorher stiess jedes Zeichnen das naechste an (auch wenn alles da war), und die Seite
      // zeichnete in jedem Bild der Anzeige neu, solange sie offen war.
      if (first >= 0 && this.source.missing && this.source.missing(first, last)) {
        this.source.ensure(first, last).then(() => this.schedule());
      }
      diag.renders++;
    }
    makeTile(index) {
      const node = el('div', 'tile');
      node.style.width = this.size + 'px';
      node.style.height = this.size + 'px';
      node.style.left = '0px';
      node.style.top = '0px';
      const img = el('img');
      img.alt = '';
      img.decoding = 'async';
      img.draggable = false;
      node.appendChild(img);
      const check = el('div', 'check');
      check.appendChild(icon('check'));
      node.appendChild(check);
      // Das Farbetikett als Punkt in der Ecke, wie es gesetzt ist (paintLabel).
      node.appendChild(el('div', 'tag'));
      const tile = { index, node, img, job: null, url: null, itemId: null, label: '' };
      this.attachPress(tile);
      this.root.appendChild(node);
      return tile;
    }
    fillTile(tile) {
      const item = this.source.item(tile.index);
      tile.node.classList.toggle('selected', !!item && this.selected.has(item.i));
      if (item && tile.label !== (item.l || '')) {
        tile.label = item.l || '';
        paintLabel(tile.node, tile.label);
      }
      if (!item || tile.itemId === item.i) return;
      if (tile.failedFor !== item.i) { tile.failedFor = null; tile.retries = 0; }
      tile.itemId = item.i;
      // Der Dateiname fuer Vorlesen und Zeiger; sichtbar steht er im Vollbild und im Infoblatt.
      tile.node.title = item.n || '';
      tile.node.setAttribute('aria-label', item.n || '');
      setFallback(tile, '');
      // Unten rechts: ein Video traegt das Abspielzeichen, ein Bewegtbild (m: 1 Motion Photo,
      // 2 Live Photo) das Zeichen des Bewegtbilds. Eine Kachel wird fuer andere Bilder
      // wiederverwendet; ein Zeichen, das nicht mehr passt, geht.
      const badgeKind = item.v ? 'video' : item.m ? 'motion' : '';
      const oldBadge = tile.node.querySelector('.badge');
      if (oldBadge && oldBadge.dataset.kind !== badgeKind) oldBadge.remove();
      if (badgeKind && !(oldBadge && oldBadge.dataset.kind === badgeKind)) {
        const badge = el('div', 'badge');
        badge.dataset.kind = badgeKind;
        badge.appendChild(icon(item.v ? 'play' : 'live'));
        badge.setAttribute('aria-label', item.v ? T.video : item.m === 2 ? T.livePhoto : T.motionPhoto);
        tile.node.appendChild(badge);
      }
      tile.job = tileLoader.load(thumbUrl(item), blob => {
        tile.job = null;
        if (tile.url) URL.revokeObjectURL(tile.url);
        tile.url = URL.createObjectURL(blob);
        tile.img.onload = () => tile.img.classList.add('loaded');
        tile.img.onerror = () => setFallback(tile, item.v ? 'video' : 'unreadable');
        tile.img.src = tile.url;
      }, error => {
        tile.job = null;
        const status = error && error.status;
        if (status === 404 && error.kind === 'missing') { setFallback(tile, 'missing'); return; }
        if (status === 422) { setFallback(tile, item.v ? 'video' : 'unreadable'); return; }
        // Abgebrochen, zu lange in der Schlange oder keine Verbindung: spaeter noch einmal, solange
        // die Kachel zu sehen ist. Nie endlos; danach das Symbol fuer ein nicht lesbares Bild.
        tile.failedFor = item.i;
        tile.retries = (tile.retries || 0) + 1;
        if (tile.retries > 5) { setFallback(tile, item.v ? 'video' : 'unreadable'); return; }
        tile.itemId = null;
        setTimeout(() => { if (this.tiles.get(tile.index) === tile) this.schedule(); }, 800 * tile.retries);
      });
    }
    dropTile(tile) {
      if (tile.job) tile.job.cancel();
      tile.job = null;
      if (tile.url) URL.revokeObjectURL(tile.url);
      tile.url = null;
      tile.node.remove();
    }
    // Tippen oeffnet, langer Druck waehlt aus; in der Auswahl schaltet Tippen um. Mit der Maus
    // schaltet STRG+Klick (auf dem Mac CMD) um und beginnt die Auswahl, SHIFT+Klick waehlt den
    // Bereich von der zuletzt umgeschalteten Kachel bis hierher (auch nicht geladene Bilder).
    attachPress(tile) {
      let timer = 0, startX = 0, startY = 0, long = false;
      tile.node.addEventListener('pointerdown', event => {
        long = false;
        startX = event.clientX; startY = event.clientY;
        clearTimeout(timer);
        timer = setTimeout(() => {
          long = true;
          if (navigator.vibrate) navigator.vibrate(15);
          this.toggle(tile.index, true);
        }, 480);
      });
      const stop = () => clearTimeout(timer);
      tile.node.addEventListener('pointermove', event => {
        if (Math.abs(event.clientX - startX) > 10 || Math.abs(event.clientY - startY) > 10) stop();
      });
      tile.node.addEventListener('pointerup', stop);
      tile.node.addEventListener('pointercancel', stop);
      tile.node.addEventListener('contextmenu', event => event.preventDefault());
      tile.node.addEventListener('click', event => {
        event.preventDefault();
        if (long) { long = false; return; }
        if (event.shiftKey && this.anchorIndex >= 0 && this.anchorIndex !== tile.index) {
          this.selectRange(Math.min(this.anchorIndex, tile.index), Math.max(this.anchorIndex, tile.index));
        } else if (this.selecting || event.ctrlKey || event.metaKey || event.shiftKey) this.toggle(tile.index);
        else if (this.options.onOpen) this.options.onOpen(this.source, tile.index);
      });
    }
    // only: nur auswaehlen, nie abwaehlen (langer Druck). Faellt die letzte Kachel heraus,
    // endet der Auswahlmodus wie in Galerien von Telefonen.
    toggle(index, only) {
      const item = this.source.item(index);
      if (!item) return;
      if (this.selected.has(item.i)) { if (!only) this.selected.delete(item.i); }
      else if (this.selected.size >= MAX_SELECTION) toast(fmt(T.selectionLimit, MAX_SELECTION));
      else this.selected.set(item.i, item);
      this.anchorIndex = index;
      this.selecting = this.selected.size > 0;
      this.updateSelection();
      this.schedule();
    }
    // Bilder first bis last (Stellen in der Liste) dazu; fehlende Seiten der Zeitleiste holt die
    // Quelle vorher. Hoechstens bis zur Grenze, mit Meldung.
    async selectRange(first, last) {
      const source = this.source;
      if (!source || first < 0 || last < first) return;
      last = Math.min(last, source.total - 1, first + MAX_SELECTION + this.selected.size);
      try { await source.ensure(first, last); } catch (ignored) { }
      if (this.source !== source) return;
      let limited = false;
      for (let i = first; i <= last; i++) {
        const item = source.item(i);
        if (!item || this.selected.has(item.i)) continue;
        if (this.selected.size >= MAX_SELECTION) { limited = true; break; }
        this.selected.set(item.i, item);
      }
      if (limited) toast(fmt(T.selectionLimit, MAX_SELECTION));
      this.selecting = true;
      this.updateSelection();
      this.schedule();
    }
    // Wie viele Bilder eines Tages ausgewaehlt sind: '' keines, 'some' ein Teil, 'all' alle.
    dayState(head) {
      if (!head || this.selected.size === 0 || !this.source) return '';
      let count = 0;
      for (let i = head.index; i < head.index + head.count; i++) {
        const item = this.source.item(i);
        if (item && this.selected.has(item.i)) count++;
      }
      return count === 0 ? '' : count === head.count ? 'all' : 'some';
    }
    // Der Kreis an der Kopfzeile: ein ganz gewaehlter Tag faellt heraus, sonst kommt er ganz dazu.
    toggleDay(h) {
      const head = this.headList && this.headList[h];
      if (!head) return;
      this.anchorIndex = head.index;
      if (this.dayState(head) !== 'all') { this.selectRange(head.index, head.index + head.count - 1); return; }
      for (let i = head.index; i < head.index + head.count; i++) {
        const item = this.source.item(i);
        if (item) this.selected.delete(item.i);
      }
      this.updateSelection();
      this.schedule();
    }
    // Der Knopf in der Kopfleiste: Auswahlmodus ohne Bild.
    startSelecting() {
      this.selecting = true;
      this.updateSelection();
      this.schedule();
    }
    // "Alle abwaehlen": leer, der Auswahlmodus bleibt.
    deselectAll() {
      this.selected.clear();
      this.updateSelection();
      this.schedule();
    }
    // "Auswahl aufheben": leer und der Auswahlmodus endet.
    clearSelection() {
      this.selected.clear();
      this.selecting = false;
      this.anchorIndex = -1;
      this.updateSelection();
      this.schedule();
    }
    selectedIds() { return Array.from(this.selected.keys()); }
    updateSelection() {
      this.host.classList.toggle('selecting', this.selecting);
      if (this.options.onSelection) this.options.onSelection(this);
    }
    // Lage eines Bildes im Raster, damit das Vollbild nach dem Schliessen dorthin scrollen kann.
    reveal(index) {
      const row = this.rows.find(r => index >= r.start && index < r.start + r.count);
      if (!row) return;
      const top = row.y + this.root.offsetTop;
      if (top < this.scroller.scrollTop || top + this.size > this.scroller.scrollTop + this.scroller.clientHeight) {
        this.scroller.scrollTop = Math.max(0, top - this.scroller.clientHeight / 3);
      }
    }
  }

  // Die Zeitleiste am rechten Rand, immer sichtbar: Jahreszahlen und bei genug Hoehe je Monat ein
  // Punkt, jeweils dort, wo das Jahr oder der Monat im Scrollbereich beginnt (proportional zur
  // Lage im Raster). Ein Strich zeigt die aktuelle Lage. Tippen oder Ziehen springt dorthin; beim
  // Ziehen, kurz nach dem Scrollen und beim Zeigen mit der Maus nennt eine Blase Monat und Jahr.
  const RAIL_PAD = 10;
  const yearFormat = new Intl.DateTimeFormat(lang, { year: 'numeric' });
  class Scrubber {
    constructor(grid) {
      this.grid = grid;
      this.node = el('div', 'rail');
      this.marks = el('div', 'marks');
      this.now = el('div', 'now');
      this.bubble = el('div', 'bubble');
      this.node.appendChild(this.marks);
      this.node.appendChild(this.now);
      this.node.appendChild(this.bubble);
      grid.scroller.parentElement.appendChild(this.node);
      this.hideTimer = 0;
      this.dragging = false;
      this.builtFor = '';
      this.node.addEventListener('pointerdown', event => {
        if (!this.grid.rows || this.grid.rows.length === 0) return;
        this.dragging = true;
        this.node.classList.add('dragging');
        this.node.setPointerCapture(event.pointerId);
        this.seek(event.clientY);
        event.preventDefault();
      });
      this.node.addEventListener('pointermove', event => {
        if (this.dragging) { this.seek(event.clientY); return; }
        // Maus ueber der Leiste: die Blase nennt den Monat unter dem Zeiger, ohne zu springen.
        if (event.pointerType !== 'mouse') return;
        const y = this.contentAt(event.clientY);
        if (y === null) return;
        this.node.classList.add('hover');
        this.bubble.style.top = this.railY(y) + 'px';
        this.bubble.textContent = this.monthAt(y);
      });
      this.node.addEventListener('pointerleave', () => { this.node.classList.remove('hover'); this.update(); });
      const end = () => { this.dragging = false; this.node.classList.remove('dragging'); this.flash(); };
      this.node.addEventListener('pointerup', end);
      this.node.addEventListener('pointercancel', end);
    }
    // Lage und Hoehe der Leiste: genau neben dem Scrollbereich.
    geometry() {
      const scroller = this.grid.scroller;
      const height = scroller.clientHeight;
      this.node.style.top = scroller.offsetTop + 'px';
      this.node.style.height = height + 'px';
      return { height, range: Math.max(1, (this.grid.height || 0) + this.grid.root.offsetTop - height) };
    }
    // Inhalt (y im Raster) auf die Leiste und zurueck; die Leiste steht fuer den Scrollbereich.
    railY(contentY) {
      const g = this.g || this.geometry();
      const f = Math.min(1, Math.max(0, (contentY + this.grid.root.offsetTop) / g.range));
      return RAIL_PAD + f * Math.max(1, g.height - 2 * RAIL_PAD);
    }
    contentAt(clientY) {
      const g = this.geometry();
      if (!g.height) return null;
      const rect = this.node.getBoundingClientRect();
      const f = Math.min(1, Math.max(0, (clientY - rect.top - RAIL_PAD) / Math.max(1, g.height - 2 * RAIL_PAD)));
      return f * g.range - this.grid.root.offsetTop;
    }
    monthAt(contentY) {
      const rows = this.grid.rows;
      const row = rows[Math.min(this.grid.rowAt(contentY), rows.length - 1)];
      return row ? monthTitle(row.day) : '';
    }
    seek(clientY) {
      const y = this.contentAt(clientY);
      if (y === null) return;
      this.grid.scroller.scrollTop = y + this.grid.root.offsetTop;
      this.update();
    }
    // Jahreszahlen und Monatspunkte neu setzen: nach jedem Layout und wenn sich die Hoehe aendert.
    rebuild() {
      this.g = this.geometry();
      const rows = this.grid.rows || [];
      this.builtFor = this.g.height + '/' + this.g.range + '/' + rows.length;
      this.marks.textContent = '';
      if (rows.length === 0 || !this.g.height) return;
      const years = [], months = [];
      let lastYear = '', lastMonth = '';
      for (const row of rows) {
        const day = row.day || '';
        const month = day.slice(0, 7);
        if (month === lastMonth) continue;
        lastMonth = month;
        const year = day.slice(0, 4);
        const at = this.railY(row.y);
        if (year !== lastYear) { lastYear = year; years.push({ at, day }); } else months.push(at);
      }
      // Jahreszahlen mit Mindestabstand; was zu dicht folgt, faellt weg (die Blase nennt es).
      const placed = [];
      for (const year of years) {
        if (placed.length > 0 && year.at - placed[placed.length - 1] < 16) continue;
        if (year.at > this.g.height - 6) continue;
        const date = parseDay(year.day);
        const node = el('div', 'year', date ? yearFormat.format(date) : T.noDate);
        node.style.top = year.at + 'px';
        this.marks.appendChild(node);
        placed.push(year.at);
      }
      // Monatspunkte nur, wenn genug Platz ist, und nicht auf einer Jahreszahl.
      if (months.length > 0 && this.g.height / (months.length + years.length) >= 7) {
        for (const at of months) {
          if (placed.some(p => Math.abs(p - at) < 9)) continue;
          const dot = el('div', 'dot');
          dot.style.top = at + 'px';
          this.marks.appendChild(dot);
        }
      }
    }
    update() {
      const rows = this.grid.rows || [];
      this.node.hidden = rows.length === 0;
      if (rows.length === 0) return;
      this.g = this.geometry();
      if (this.builtFor !== this.g.height + '/' + this.g.range + '/' + rows.length) this.rebuild();
      const contentY = this.grid.scroller.scrollTop - this.grid.root.offsetTop;
      const y = this.railY(contentY);
      this.now.style.top = y + 'px';
      if (!this.node.classList.contains('hover')) {
        this.bubble.style.top = y + 'px';
        this.bubble.textContent = this.monthAt(contentY);
      }
    }
    // Die Blase kurz zeigen, etwa beim Scrollen.
    flash() {
      this.node.classList.add('flash');
      clearTimeout(this.hideTimer);
      this.hideTimer = setTimeout(() => this.node.classList.remove('flash'), 1000);
    }
  }

  // --- Auswahl: Kopfleiste und Aktionsleiste ---------------------------------------------------
  // Was /api/m/me ueber das Konto sagt und die Auswahl braucht (ohne Papierkorb fragt das Loeschen
  // "endgueltig").
  const account = { deleteForever: false };
  // So viele Bilder schickt "An App senden" einer Auswahl hoechstens (Vorschauen, je eine Anfrage).
  const MAX_SEND = 30;

  // Jede Ansicht mit Raster bekommt einen Knopf "Auswaehlen" in der Kopfleiste. Im Auswahlmodus
  // steht dort statt Titel und Knoepfen die Anzahl mit "Auswahl aufheben" und "Alle abwaehlen",
  // unten ueber den Reitern die Aktionsleiste: Herunterladen (ZIP), Teilen, Sterne, Favorit,
  // Etikett, Behalten, Verwerfen und Loeschen. Ein Knopf, fuer den die Stufe bei keinem
  // gewaehlten Bild reicht (Feld "r" der Listen), fehlt; Teilen als Link nur, wenn jedes Bild es
  // erlaubt (der Server lehnt sonst die ganze Freigabe ab). Der Server prueft jedes Bild selbst
  // und meldet, was er nicht aendern durfte. Liefert die Rueckmeldung fuer grid.options.onSelection.
  function selectionUi(view, grid) {
    const bar = view.querySelector('.bar');
    const start = iconButton('select', T.select, () => grid.startSelecting());
    start.classList.add('select-start');
    bar.appendChild(start);
    const head = el('div', 'select-head');
    const count = el('div', 'count');
    const none = el('button', 'text-button', T.deselectAll);
    none.type = 'button';
    none.addEventListener('click', () => grid.deselectAll());
    for (const part of [iconButton('close', T.clearSelection, () => grid.clearSelection()), count, none]) head.appendChild(part);
    bar.appendChild(head);

    const actions = el('div', 'select-actions');
    actions.setAttribute('role', 'toolbar');
    view.appendChild(actions);
    // Was zuletzt auf DIESE Auswahl angewandt wurde: ein zweiter Druck auf Favorit, Behalten oder
    // Verwerfen nimmt es zurueck, wie im Vollbild. Eine geaenderte Auswahl beginnt von vorn.
    const applied = { favorite: null, pick: null };
    const level = item => (item && typeof item.r === 'number' ? item.r : 3);
    const items = () => Array.from(grid.selected.values());
    const kind = () => (view.id === 'view-search' ? 'search' : 'selection');

    async function markSelection(change) {
      const ids = grid.selectedIds();
      if (!ids.length) return false;
      try {
        const answer = await postJson('/api/m/mark', Object.assign({ ids }, change));
        // Das Etikett steht auf der Kachel: an den Bildern der Liste nachtragen.
        if (typeof change.label === 'string') {
          for (const id of answer.done || []) {
            const item = grid.selected.get(id);
            if (item) item.l = change.label;
          }
          grid.schedule();
        }
        toast(answer.message ? answer.message : answer.changed === 1 ? T.saved : fmt(T.changedMany, answer.changed));
        return true;
      } catch (error) {
        toast(error.message || T.saveFailed);
        return false;
      }
    }

    const zip = iconButton('download', T.zip, async () => {
      try {
        const answer = await postJson('/api/m/zip', { ids: grid.selectedIds() });
        download(answer.url);
      } catch (error) { toast(error.message || T.loadFailed); }
    });
    const share = iconButton('share', T.share, () => {
      const chosen = items();
      const ids = chosen.map(item => item.i);
      const canLink = !!window.FerrumPixShares && chosen.every(item => level(item) >= 2);
      const send = canSendSelection(chosen) ? () => sendSelectionToApp(chosen) : null;
      if (window.FerrumPixShares && window.FerrumPixShares.fromSelection) window.FerrumPixShares.fromSelection(ids, kind(), canLink, send);
      else if (send) send();
    });
    const stars = iconButton('star', T.rating, () => {
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', T.rating));
        const row = el('div', 'stars sheet-stars');
        for (let n = 1; n <= 5; n++) {
          const star = iconButton('star', T.rating + ' ' + n, () => { close(); markSelection({ rating: n }); });
          star.className = '';
          // Ein Stern n zeigt n gefuellte Sterne an: die Knoepfe davor leuchten beim Zeigen mit.
          star.dataset.n = n;
          star.addEventListener('pointerenter', () => { for (const s of row.children) s.classList.toggle('on', +s.dataset.n <= n); });
          row.appendChild(star);
        }
        row.addEventListener('pointerleave', () => { for (const s of row.children) s.classList.remove('on'); });
        body.appendChild(row);
        body.appendChild(sheetAction('close', T.noRating, () => { close(); markSelection({ rating: 0 }); }));
      });
    });
    const favorite = iconButton('heart', T.favorite, async () => {
      const value = applied.favorite !== true;
      if (await markSelection({ favorite: value })) { applied.favorite = value; paint(); }
    });
    const label = el('button', 'label-dot');
    label.type = 'button';
    label.setAttribute('aria-label', T.label);
    label.title = T.label;
    label.addEventListener('click', () => {
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', T.label));
        const row = el('div', 'swatches');
        for (const value of LABELS) {
          const swatch = el('button', 'swatch' + (value ? '' : ' none'));
          paintLabel(swatch, value);
          swatch.type = 'button';
          swatch.setAttribute('aria-label', LABEL_NAMES[value]);
          swatch.title = LABEL_NAMES[value];
          swatch.addEventListener('click', async () => {
            close();
            if (await markSelection({ label: value })) paintLabel(label, value);
          });
          row.appendChild(swatch);
        }
        body.appendChild(row);
      });
    });
    const pickButton = (name, value, text) => {
      const button = iconButton(name, text, async () => {
        const target = applied.pick === value ? 0 : value;
        if (await markSelection({ pick: target })) { applied.pick = target; paint(); }
      });
      button.classList.add('pick-' + name);
      return button;
    };
    const keep = pickButton('keep', 1, T.keep);
    const reject = pickButton('reject', -1, T.reject);
    const remove = iconButton('trash', T.delete, async () => {
      const chosen = items();
      if (!chosen.length) return;
      const question = chosen.length === 1
        ? fmt(account.deleteForever ? T.deleteForeverConfirm : T.deleteConfirm, chosen[0].n || '')
        : fmt(account.deleteForever ? T.deleteManyForeverConfirm : T.deleteManyConfirm, chosen.length);
      if (!window.confirm(question)) return;
      try {
        const answer = await postJson('/api/m/delete', { ids: chosen.map(item => item.i) });
        if (answer.message) toast(answer.message);
      } catch (error) { toast(error.message || T.saveFailed); }
      grid.clearSelection();
      afterFileChange();
    });
    remove.classList.add('danger');
    for (const button of [zip, share, stars, favorite, label, keep, reject, remove]) actions.appendChild(button);

    function paint() {
      favorite.classList.toggle('on', applied.favorite === true);
      keep.classList.toggle('on', applied.pick === 1);
      reject.classList.toggle('on', applied.pick === -1);
    }

    return () => {
      const chosen = items();
      const selecting = grid.selecting;
      bar.classList.toggle('selecting', selecting);
      view.classList.toggle('with-select-actions', selecting);
      actions.hidden = !selecting;
      start.hidden = selecting || !grid.source || !grid.source.total;
      count.textContent = fmt(T.selected, chosen.length);
      none.hidden = chosen.length === 0;
      applied.favorite = null;
      applied.pick = null;
      paintLabel(label, '');
      paint();
      // Ohne Bild stehen alle Knoepfe da, aber ohne Wirkung; mit Bildern nur die, fuer die die
      // Stufe bei mindestens einem reicht.
      const empty = chosen.length === 0;
      const canRate = empty || chosen.some(item => level(item) >= 2);
      const canManage = !empty && chosen.some(item => level(item) >= 3);
      const canLink = !!window.FerrumPixShares && chosen.every(item => level(item) >= 2);
      for (const button of [stars, favorite, label, keep, reject]) button.hidden = !canRate;
      share.hidden = !empty && !canLink && !canSendSelection(chosen);
      remove.hidden = !canManage;
      for (const button of actions.children) {
        button.disabled = empty;
        button.classList.toggle('disabled', empty);
      }
    };
  }

  // Ob der Browser mehrere Dateien an eine App geben kann (Teilen-Menue des Geraets), und ob die
  // Auswahl dafuer taugt: nur Bilder, hoechstens MAX_SEND.
  function canShareManyFiles() {
    try {
      const file = () => new File([new Blob()], 'x.jpg', { type: 'image/jpeg' });
      return !!(navigator.canShare && navigator.share && navigator.canShare({ files: [file(), file()] }));
    } catch (ignored) { return false; }
  }
  function canSendSelection(chosen) {
    return chosen.length > 0 && chosen.length <= MAX_SEND && chosen.every(item => !item.v) &&
      (chosen.length === 1 ? canShareFiles() : canShareManyFiles());
  }
  // An App senden fuer eine Auswahl: die Vorschauen mit Bearbeitung holen (wie das Teilen im
  // Vollbild) und erst danach einen eigenen Knopf zeigen. Das Teilen verlangt eine frische Geste
  // des Nutzers, die das Warten auf die Bilder verbraucht haette.
  function sendSelectionToApp(chosen) {
    showSheet((body, close) => {
      body.appendChild(el('div', 'sheet-title', T.sendToApp));
      const state = el('div', 'upload-item', T.preparing);
      body.appendChild(state);
      (async () => {
        const files = [];
        for (const item of chosen) {
          if ($('action-sheet').hidden) return;
          state.textContent = T.preparing + ' ' + (files.length + 1) + ' / ' + chosen.length;
          try {
            const response = await api(previewUrl(item));
            if (!response.ok) continue;
            const name = (item.n || 'FerrumPix').replace(/\.[^.]+$/, '') + '.jpg';
            files.push(new File([await response.blob()], name, { type: 'image/jpeg' }));
          } catch (ignored) { }
        }
        if ($('action-sheet').hidden) return;
        if (!files.length) { close(); toast(T.loadFailed); return; }
        state.textContent = fmt(T.selected, files.length);
        body.appendChild(sheetAction('share', T.share, async () => {
          close();
          try { await navigator.share({ files }); } catch (ignored) { }
        }));
      })();
    });
  }

  // --- Vollbild --------------------------------------------------------------------------------
  const viewer = {
    node: $('viewer'), stage: $('stage'), track: null, slides: [], source: null, index: 0,
    info: null, infoFor: null, blobs: new Map(), jobs: new Map(), grid: null,
    zoom: { scale: 1, x: 0, y: 0 },
    // Der laufende Film eines Bewegtbilds (playMotion) und ob er stumm laeuft.
    motion: null, motionMuted: true
  };
  // Die Farbetiketten der Galerie in ihrer Reihenfolge, vom Server ([Farbe, Name] je Etikett, aus
  // derselben Liste wie Kontextmenue und Filter der Galerie), dazu "Kein Etikett" zum Entfernen.
  const LABEL_LIST = (() => { try { return JSON.parse(T.labels || '[]'); } catch (ignored) { return []; } })();
  const LABELS = [''].concat(LABEL_LIST.map(entry => entry[0]));
  const LABEL_NAMES = Object.fromEntries([['', T.noLabel]].concat(LABEL_LIST));
  // Die Farbe kommt ueber eine CSS-Variable an das Element (CSSOM, mit der CSP vereinbar).
  function paintLabel(node, label) {
    if (label) node.style.setProperty('--label', label); else node.style.removeProperty('--label');
    node.classList.toggle('has-label', !!label);
  }

  function setupViewer() {
    viewer.track = el('div', 'track');
    for (let k = 0; k < 3; k++) {
      const slide = el('div', 'slide');
      viewer.track.appendChild(slide);
      viewer.slides.push(slide);
    }
    viewer.stage.appendChild(viewer.track);
    const close = $('viewer-close');
    close.appendChild(icon('back'));
    close.setAttribute('aria-label', T.back);
    close.addEventListener('click', closeViewer);
    $('viewer-download').appendChild(icon('download'));
    $('viewer-download').setAttribute('aria-label', T.download);
    $('viewer-download').addEventListener('click', showDownloadSheet);
    $('viewer-info').appendChild(icon('info'));
    $('viewer-info').setAttribute('aria-label', T.info);
    $('viewer-info').addEventListener('click', () => toggleInfo());
    const share = $('viewer-share');
    share.appendChild(icon('share'));
    share.setAttribute('aria-label', T.share);
    // Teilen: Link erstellen (shares.js) oder die Vorschau an eine App senden.
    share.addEventListener('click', () => {
      const item = viewer.source && viewer.source.item(viewer.index);
      if (window.FerrumPixShares) window.FerrumPixShares.fromViewer(item, viewer.info, shareCurrent, canShareFiles() && !!item && !item.v);
      else shareCurrent();
    });
    // Bewegtbild: der Knopf spielt den Film, waehrend er laeuft, haelt er ihn an; daneben der
    // Ton, nur solange der Film laeuft.
    $('viewer-live').appendChild(icon('live'));
    $('viewer-live').addEventListener('click', () => { if (viewer.motion) stopMotion(); else playMotion(); });
    $('viewer-sound').addEventListener('click', () => {
      viewer.motionMuted = !viewer.motionMuted;
      if (viewer.motion) viewer.motion.muted = viewer.motionMuted;
      updateMotionButtons();
    });
    updateMotionButtons();
    const manage = $('viewer-manage');
    manage.appendChild(icon('more'));
    manage.setAttribute('aria-label', T.manage);
    manage.addEventListener('click', showManageSheet);
    for (let n = 1; n <= 5; n++) {
      const star = iconButton('star', T.rating + ' ' + n, () => {
        const current = viewer.info ? viewer.info.rating : 0;
        mark({ rating: current === n ? 0 : n });
      });
      star.className = '';
      star.dataset.n = n;
      $('stars').appendChild(star);
    }
    $('mark-favorite').appendChild(icon('heart'));
    $('mark-favorite').setAttribute('aria-label', T.favorite);
    $('mark-favorite').addEventListener('click', () => mark({ favorite: !(viewer.info && viewer.info.favorite) }));
    $('mark-keep').appendChild(icon('keep'));
    $('mark-keep').setAttribute('aria-label', T.keep);
    $('mark-keep').addEventListener('click', () => mark({ pick: viewer.info && viewer.info.pick === 1 ? 0 : 1 }));
    $('mark-reject').appendChild(icon('reject'));
    $('mark-reject').setAttribute('aria-label', T.reject);
    $('mark-reject').addEventListener('click', () => mark({ pick: viewer.info && viewer.info.pick === -1 ? 0 : -1 }));
    $('mark-label').setAttribute('aria-label', T.label);
    $('mark-label').addEventListener('click', showLabelSheet);
    // Blaettern mit der Maus; sichtbar nur bei einem Zeiger, der schweben kann (app.css), und
    // nicht am ersten bzw. letzten Bild (showIndex). Die Pfeiltasten bleiben, wie sie sind.
    const prev = $('viewer-prev'), next = $('viewer-next');
    prev.appendChild(icon('back'));
    prev.setAttribute('aria-label', T.previous);
    prev.title = T.previous;
    prev.addEventListener('click', () => { if (viewer.index > 0) slideTo(-1); });
    next.appendChild(icon('forward'));
    next.setAttribute('aria-label', T.next);
    next.title = T.next;
    next.addEventListener('click', () => { if (viewer.source && viewer.index < viewer.source.total - 1) slideTo(1); });
    attachGestures();
    window.addEventListener('resize', () => { if (!viewer.node.hidden) placeTrack(0, false); });
  }

  function canShareFiles() {
    try {
      return !!(navigator.canShare && navigator.share &&
        navigator.canShare({ files: [new File([new Blob()], 'x.jpg', { type: 'image/jpeg' })] }));
    } catch (ignored) { return false; }
  }

  function openViewer(source, index, grid) {
    viewer.source = source;
    viewer.grid = grid;
    viewer.index = index;
    viewer.node.hidden = false;
    viewer.node.classList.remove('chrome-off');
    $('info-sheet').hidden = true;
    history.pushState({ viewer: true }, '');
    showIndex(index);
  }
  function closeViewer() {
    if (viewer.node.hidden) return;
    if (history.state && history.state.viewer) { history.back(); return; }
    hideViewer();
  }
  function hideViewer() {
    stopMotion();
    viewer.node.hidden = true;
    for (const job of viewer.jobs.values()) job.cancel();
    viewer.jobs.clear();
    for (const slide of viewer.slides) slide.replaceChildren();
    for (const url of viewer.blobs.values()) URL.revokeObjectURL(url.url);
    viewer.blobs.clear();
    if (viewer.grid) viewer.grid.reveal(viewer.index);
  }
  window.addEventListener('popstate', () => { if (!viewer.node.hidden) hideViewer(); });

  function placeTrack(offset, animate) {
    const width = viewer.stage.clientWidth;
    viewer.track.style.transition = animate ? 'transform 0.22s ease-out' : 'none';
    viewer.track.style.transform = `translateX(${offset}px)`;
    viewer.slides.forEach((slide, k) => { slide.style.transform = `translateX(${(k - 1) * width}px)`; });
  }

  async function showIndex(index) {
    const source = viewer.source;
    stopMotion();
    viewer.index = index;
    $('viewer-prev').hidden = index <= 0;
    $('viewer-next').hidden = index >= source.total - 1;
    resetZoom();
    placeTrack(0, false);
    await source.ensure(Math.max(0, index - 1), Math.min(source.total - 1, index + 1));
    if (viewer.source !== source || viewer.index !== index) return;
    // Nur das Bild davor, das Bild und das danach behalten; der Rest wird abgebrochen.
    const keep = new Set();
    for (let k = -1; k <= 1; k++) {
      const item = source.item(index + k);
      if (item) keep.add(item.i);
    }
    for (const [id, job] of viewer.jobs) { if (!keep.has(id)) { job.cancel(); viewer.jobs.delete(id); } }
    for (const [id, entry] of viewer.blobs) { if (!keep.has(id)) { URL.revokeObjectURL(entry.url); viewer.blobs.delete(id); } }
    fillSlide(viewer.slides[1], source.item(index), true);
    fillSlide(viewer.slides[0], source.item(index - 1), false);
    fillSlide(viewer.slides[2], source.item(index + 1), false);
    loadInfo(source.item(index));
  }

  function fillSlide(slide, item, current) {
    slide.replaceChildren();
    slide.dataset.id = item ? item.i : '';
    if (!item) return;
    const img = el('img');
    img.alt = '';
    img.draggable = false;
    // Erst die Kachel (liegt meist schon im Zwischenspeicher des Browsers), auf die volle Flaeche
    // hochgezogen wie die Uebergangskachel des Betrachters am Desktop; die Vorschau in
    // Bildschirmgroesse ersetzt sie, sobald sie fertig dekodiert ist (showPreview).
    img.src = thumbUrl(item);
    img.className = 'placeholder';
    // Ohne Kachel (Datei fehlt, Format nicht lesbar) das Symbol der Kachel statt einer leeren Flaeche.
    img.onerror = () => {
      if (!img.isConnected || !img.classList.contains('placeholder')) return;
      fetch(thumbUrl(item), { credentials: 'same-origin' }).then(response => {
        const kind = response.headers.get('X-FerrumPix-Thumb') === 'missing' ? 'missing' : (item.v ? 'video' : 'unreadable');
        if (!img.isConnected || response.ok) return;
        const box = el('div', 'slide-fallback');
        box.appendChild(icon(kind));
        box.appendChild(el('span', null, kind === 'missing' ? T.missing : kind === 'video' ? T.video : T.unreadable));
        img.replaceWith(box);
      }).catch(() => { });
    };
    slide.appendChild(img);
    if (item.v) {
      const play = el('button', 'play');
      play.type = 'button';
      play.appendChild(icon('play'));
      play.setAttribute('aria-label', T.video);
      play.addEventListener('click', event => { event.stopPropagation(); playVideo(slide, item); });
      slide.appendChild(play);
    }
    const ready = viewer.blobs.get(item.i);
    if (ready) { img.src = ready.url; img.className = ''; return; }
    if (viewer.jobs.has(item.i)) {
      const pending = viewer.jobs.get(item.i);
      pending.targets.add(img);
      // Aus der Vorbereitung wird das gezeigte Bild: noch nicht abgeschickt, geht sie nach vorn
      // und ohne den Vermerk der Vorbereitung an den Server.
      if (current && pending.low && !pending.ctrl) {
        pending.low = false;
        const waiting = previewLoader.queue.indexOf(pending);
        if (waiting > 0) { previewLoader.queue.splice(waiting, 1); previewLoader.queue.unshift(pending); }
      }
      return;
    }
    // Das Bild selbst vorn in der Schlange; die Nachbarn nur als Vorbereitung, die der Server
    // nach allem anderen rechnet. Ein Bild, das aus dem Fenster faellt, bricht showIndex ab.
    const job = previewLoader.load(previewUrl(item), blob => {
      viewer.jobs.delete(item.i);
      const url = URL.createObjectURL(blob);
      viewer.blobs.set(item.i, { url, blob });
      for (const target of job.targets) showPreview(target, url);
    }, () => {
      viewer.jobs.delete(item.i);
      // Fehlt die Datei, sagt das schon das Symbol auf der Scheibe.
      if (img.isConnected && current) toast(T.loadFailed);
    }, current, !current);
    job.targets = new Set([img]);
    viewer.jobs.set(item.i, job);
  }
  // Tauscht die Kachel gegen die Vorschau erst, wenn diese dekodiert ist: so steht nie eine
  // leere Flaeche zwischen beiden.
  function showPreview(img, url) {
    if (!img.isConnected) return;
    const probe = new Image();
    probe.src = url;
    const swap = () => {
      if (!img.isConnected) return;
      img.src = url;
      img.classList.remove('placeholder');
    };
    (probe.decode ? probe.decode() : Promise.resolve()).then(swap, swap);
  }

  function playVideo(slide, item) {
    const video = el('video');
    video.controls = true;
    video.autoplay = true;
    video.playsInline = true;
    video.setAttribute('playsinline', '');
    video.preload = 'metadata';
    video.src = '/api/m/original?id=' + encodeURIComponent(item.i);
    slide.replaceChildren(video);
  }

  // --- Bewegtbild ----------------------------------------------------------------------------
  // Ein Motion Photo (Film im JPEG) oder Live Photo (MOV daneben), wie es das Infoblatt meldet
  // (info.motion). Der Knopf oben oder ein langer Druck auf das Bild spielt den Film EINMAL ueber
  // dem Standbild ab, stumm voreingestellt; am Ende, bei einem Fehler, beim Blaettern und beim
  // Schliessen steht wieder das Bild. Der Film nimmt keine Gesten an (pointer-events: none), das
  // Bild darunter laesst sich weiter wischen.
  function motionAvailable() {
    const item = viewer.source && viewer.source.item(viewer.index);
    return !!(item && viewer.info && viewer.info.motion && viewer.infoFor === item.i);
  }
  function updateMotionButtons() {
    const live = $('viewer-live'), sound = $('viewer-sound');
    live.hidden = !motionAvailable();
    const label = viewer.motion ? T.stopMotion : T.playMotion;
    live.setAttribute('aria-label', label);
    live.title = label;
    live.classList.toggle('on', !!viewer.motion);
    sound.hidden = !viewer.motion;
    sound.replaceChildren(icon(viewer.motionMuted ? 'muted' : 'sound'));
    sound.setAttribute('aria-label', T.mute);
    sound.setAttribute('aria-pressed', viewer.motionMuted ? 'true' : 'false');
    sound.title = T.mute;
    // Der grosse Knopf mitten auf dem Bild, derselbe wie auf einem Video: nur in der Folie des
    // gezeigten Bildes, solange zum Bild ein Film gehoert und er nicht laeuft. Ein Tipp spielt den
    // Film einmal ab; der Knopf oben und der lange Druck bleiben daneben.
    const showBig = motionAvailable() && !viewer.motion;
    viewer.slides.forEach((slide, k) => {
      const big = slide.querySelector('.motion-play');
      if (k !== 1 || !showBig) { if (big) big.remove(); return; }
      if (big) return;
      const button = el('button', 'play motion-play');
      button.type = 'button';
      button.appendChild(icon('play'));
      button.setAttribute('aria-label', T.playMotion);
      button.title = T.playMotion;
      button.addEventListener('click', event => { event.stopPropagation(); playMotion(); });
      slide.appendChild(button);
    });
  }
  function playMotion() {
    if (viewer.motion || !motionAvailable()) return;
    const item = viewer.source.item(viewer.index);
    resetZoom();
    const video = el('video', 'motion');
    video.muted = viewer.motionMuted;
    if (viewer.motionMuted) video.setAttribute('muted', '');
    video.playsInline = true;
    video.setAttribute('playsinline', '');
    video.preload = 'auto';
    video.disablePictureInPicture = true;
    const failed = () => {
      if (viewer.motion !== video) return;
      stopMotion();
      toast(T.motionFailed);
    };
    video.addEventListener('ended', () => { if (viewer.motion === video) stopMotion(); });
    video.addEventListener('error', failed);
    video.src = '/api/m/motion?id=' + encodeURIComponent(item.i);
    viewer.slides[1].appendChild(video);
    viewer.motion = video;
    updateMotionButtons();
    const started = video.play();
    if (started && started.catch) {
      started.catch(error => {
        if (viewer.motion !== video || (error && error.name === 'AbortError')) return;
        // Mit Ton verlangt der Browser eine Geste, die ein langer Druck nicht immer ist: dann stumm.
        if (!video.muted) {
          video.muted = true;
          viewer.motionMuted = true;
          updateMotionButtons();
          video.play().catch(failed);
        } else failed();
      });
    }
  }
  function stopMotion() {
    const video = viewer.motion;
    if (!video) return;
    viewer.motion = null;
    try {
      video.pause();
      video.removeAttribute('src');
      video.load();
    } catch (ignored) { }
    video.remove();
    updateMotionButtons();
  }

  function currentImage() {
    return viewer.slides[1].querySelector('img');
  }
  function resetZoom() {
    viewer.zoom = { scale: 1, x: 0, y: 0 };
    applyZoom(false);
  }
  function applyZoom(animate) {
    const img = currentImage();
    if (!img) return;
    img.style.transition = animate ? 'transform 0.2s ease-out' : 'none';
    img.style.transform = `translate(${viewer.zoom.x}px, ${viewer.zoom.y}px) scale(${viewer.zoom.scale})`;
  }
  function clampZoom() {
    const img = currentImage();
    if (!img) return;
    const z = viewer.zoom;
    z.scale = Math.min(6, Math.max(1, z.scale));
    const maxX = Math.max(0, (img.clientWidth * z.scale - viewer.stage.clientWidth) / 2);
    const maxY = Math.max(0, (img.clientHeight * z.scale - viewer.stage.clientHeight) / 2);
    z.x = Math.min(maxX, Math.max(-maxX, z.x));
    z.y = Math.min(maxY, Math.max(-maxY, z.y));
  }
  // Zoom um einen Punkt (Bildschirm): der Punkt bleibt, wo er ist.
  function zoomAt(scale, clientX, clientY) {
    const rect = viewer.stage.getBoundingClientRect();
    const mx = clientX - rect.left - rect.width / 2;
    const my = clientY - rect.top - rect.height / 2;
    const z = viewer.zoom;
    const ratio = scale / z.scale;
    z.x = mx - (mx - z.x) * ratio;
    z.y = my - (my - z.y) * ratio;
    z.scale = scale;
  }

  // Gesten: ein Finger wischt (waagrecht blaettern, nach oben Infoblatt, nach unten schliessen) oder
  // verschiebt das gezoomte Bild; zwei Finger zoomen; Doppeltipp zoomt an der Stelle; Tippen
  // blendet die Leisten aus und ein.
  function attachGestures() {
    const pointers = new Map();
    let gesture = null, lastTap = { time: 0, x: 0, y: 0 }, tapTimer = 0, holdTimer = 0;
    const stage = viewer.stage;
    // Langer Druck auf ein Bewegtbild, ohne zu wischen: der Film laeuft (wie in Galerien von
    // Telefonen). Er laeuft einmal bis zum Ende, auch wenn der Finger vorher losgelassen wird.
    const holdMs = 480;
    stage.addEventListener('contextmenu', event => { if (motionAvailable()) event.preventDefault(); });
    stage.addEventListener('pointerdown', event => {
      if (event.target.closest('video:not(.motion), .play')) return;
      stage.setPointerCapture(event.pointerId);
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      clearTimeout(holdTimer);
      if (pointers.size === 1) {
        gesture = { kind: 'pending', x0: event.clientX, y0: event.clientY, t0: performance.now(), zx: viewer.zoom.x, zy: viewer.zoom.y, moved: false };
        const pressed = gesture;
        if (motionAvailable() && !viewer.motion && viewer.zoom.scale <= 1.01) {
          holdTimer = setTimeout(() => {
            if (gesture !== pressed || pressed.kind !== 'pending' || pressed.moved) return;
            pressed.kind = 'hold';
            clearTimeout(tapTimer);
            playMotion();
          }, holdMs);
        }
      } else if (pointers.size === 2) {
        const [a, b] = Array.from(pointers.values());
        gesture = { kind: 'pinch', d0: Math.hypot(a.x - b.x, a.y - b.y), s0: viewer.zoom.scale, moved: true,
          mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2, zx: viewer.zoom.x, zy: viewer.zoom.y };
        placeTrack(0, true);
      }
    });
    stage.addEventListener('pointermove', event => {
      if (!pointers.has(event.pointerId) || !gesture) return;
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (gesture.kind === 'pinch' && pointers.size >= 2) {
        const [a, b] = Array.from(pointers.values());
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
        viewer.zoom.x = gesture.zx + (mx - gesture.mx);
        viewer.zoom.y = gesture.zy + (my - gesture.my);
        viewer.zoom.scale = gesture.s0;
        zoomAt(Math.min(6, Math.max(0.8, gesture.s0 * d / Math.max(1, gesture.d0))), mx, my);
        applyZoom(false);
        return;
      }
      const dx = event.clientX - gesture.x0, dy = event.clientY - gesture.y0;
      if (gesture.kind === 'pending') {
        if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
        gesture.moved = true;
        if (viewer.zoom.scale > 1.01) gesture.kind = 'pan';
        else gesture.kind = Math.abs(dx) > Math.abs(dy) ? 'swipe' : 'vertical';
      }
      if (gesture.kind === 'pan') {
        viewer.zoom.x = gesture.zx + dx;
        viewer.zoom.y = gesture.zy + dy;
        clampZoom();
        applyZoom(false);
      } else if (gesture.kind === 'swipe') {
        const atStart = viewer.index === 0 && dx > 0, atEnd = viewer.index >= viewer.source.total - 1 && dx < 0;
        placeTrack(atStart || atEnd ? dx / 3 : dx, false);
      } else if (gesture.kind === 'vertical' && dy > 0 && $('info-sheet').hidden) {
        viewer.track.style.transition = 'none';
        viewer.track.style.transform = `translateY(${dy}px)`;
      }
    });
    const finish = event => {
      if (!pointers.has(event.pointerId)) return;
      pointers.delete(event.pointerId);
      if (!gesture) return;
      if (gesture.kind === 'pinch') {
        if (pointers.size === 0) {
          clampZoom();
          if (viewer.zoom.scale < 1.02) resetZoom(); else applyZoom(true);
          gesture = null;
        }
        return;
      }
      const dx = event.clientX - gesture.x0, dy = event.clientY - gesture.y0;
      const elapsed = performance.now() - gesture.t0;
      const width = viewer.stage.clientWidth;
      if (gesture.kind === 'swipe') {
        const fast = Math.abs(dx) / Math.max(1, elapsed) > 0.5;
        if ((dx < -width * 0.22 || (fast && dx < -30)) && viewer.index < viewer.source.total - 1) slideTo(1);
        else if ((dx > width * 0.22 || (fast && dx > 30)) && viewer.index > 0) slideTo(-1);
        else placeTrack(0, true);
      } else if (gesture.kind === 'vertical') {
        if (dy < -60) toggleInfo(true);
        else if (dy > 60 && !$('info-sheet').hidden) toggleInfo(false);
        else if (dy > 110) closeViewer();
        placeTrack(0, true);
      } else if (gesture.kind === 'pending' && !gesture.moved && elapsed < 300) {
        const now = performance.now();
        if (now - lastTap.time < 300 && Math.hypot(event.clientX - lastTap.x, event.clientY - lastTap.y) < 30) {
          clearTimeout(tapTimer);
          lastTap.time = 0;
          if (viewer.zoom.scale > 1.01) resetZoom();
          else { zoomAt(2.5, event.clientX, event.clientY); clampZoom(); }
          applyZoom(true);
        } else {
          lastTap = { time: now, x: event.clientX, y: event.clientY };
          clearTimeout(tapTimer);
          tapTimer = setTimeout(() => viewer.node.classList.toggle('chrome-off'), 300);
        }
      }
      gesture = null;
    };
    stage.addEventListener('pointerup', finish);
    stage.addEventListener('pointercancel', event => {
      pointers.delete(event.pointerId);
      gesture = null;
      placeTrack(0, true);
    });
    stage.addEventListener('wheel', event => {
      event.preventDefault();
      zoomAt(Math.min(6, Math.max(1, viewer.zoom.scale * (event.deltaY < 0 ? 1.15 : 0.87))), event.clientX, event.clientY);
      clampZoom();
      applyZoom(false);
    }, { passive: false });
    document.addEventListener('keydown', event => {
      if (viewer.node.hidden) return;
      if (event.key === 'ArrowRight' && viewer.index < viewer.source.total - 1) slideTo(1);
      else if (event.key === 'ArrowLeft' && viewer.index > 0) slideTo(-1);
      else if (event.key === 'Escape') closeViewer();
    });
  }

  function slideTo(direction) {
    const width = viewer.stage.clientWidth;
    placeTrack(-direction * width, true);
    // Nach der Bewegung stehen die Scheiben wieder auf ihrem Platz; die Bilder davor und danach
    // sind schon geladen (viewer.blobs) und erscheinen ohne neue Anfrage.
    setTimeout(() => showIndex(viewer.index + direction), 230);
  }

  // Der Titel des Vollbilds in zwei Zeilen: oben Datum und Uhrzeit der Aufnahme, darunter
  // kleiner der Dateiname, in der Mitte gekuerzt (voll im Tooltip und fuers Vorlesen). Ohne
  // Aufnahmezeit steht nur der Name.
  function setViewerTitle(item) {
    const title = $('viewer-title');
    title.replaceChildren();
    title.removeAttribute('title');
    title.removeAttribute('aria-label');
    if (!item) return;
    const date = parseIso(item.t);
    const name = item.n || '';
    if (date) title.appendChild(el('div', 'when', shortFormat.format(date)));
    if (name) {
      const line = el('div', date ? 'name' : 'name only');
      title.appendChild(line);
      line.textContent = middleEllipsis(name, charsFor(title, date ? 12 : 14));
      title.title = name;
      title.setAttribute('aria-label', (date ? shortFormat.format(date) + ', ' : '') + name);
    }
  }
  window.addEventListener('resize', () => {
    if (!viewer.node.hidden && viewer.source) setViewerTitle(viewer.source.item(viewer.index));
  });

  async function loadInfo(item) {
    viewer.info = null;
    viewer.infoFor = item ? item.i : null;
    renderMarks();
    updateMotionButtons();
    $('viewer-share').hidden = !canShareFiles() || !item || !!item.v;
    setViewerTitle(item);
    if (!item) return;
    try {
      const info = await getJson('/api/m/info?id=' + encodeURIComponent(item.i));
      if (viewer.infoFor !== item.i) return;
      viewer.info = info;
      renderMarks();
      updateMotionButtons();
      if (!$('info-sheet').hidden) renderInfo();
    } catch (ignored) { }
  }

  function renderMarks() {
    const info = viewer.info || { rating: 0, favorite: false, label: '', pick: 0 };
    for (const star of $('stars').children) star.classList.toggle('on', +star.dataset.n <= info.rating);
    $('mark-favorite').classList.toggle('on', !!info.favorite);
    $('mark-keep').classList.toggle('on', info.pick === 1);
    $('mark-reject').classList.toggle('on', info.pick === -1);
    paintLabel($('mark-label'), info.label || '');
    $('viewer-bottom').classList.toggle('disabled', !viewer.info);
    // Ohne "Mitarbeiten" gibt es nichts zu bewerten, ohne "Verwalten" nichts umzubenennen oder
    // zu loeschen: die Knoepfe fehlen dann. Der Server prueft jeden Weg trotzdem selbst.
    $('viewer-bottom').hidden = !!viewer.info && viewer.info.canRate === false;
    $('viewer-manage').hidden = !(viewer.info && viewer.info.canManage);
    // Mit "Mitarbeiten" gibt es Teilen auch ohne Teilen-Menue des Geraets: als Link (shares.js).
    if (viewer.info && viewer.info.canShare && window.FerrumPixShares) $('viewer-share').hidden = false;
  }

  // Bewertung und Markierungen: sofort angezeigt, bei einem Fehler zurueckgenommen.
  async function mark(change) {
    const item = viewer.source && viewer.source.item(viewer.index);
    if (!item || !viewer.info) return;
    const before = Object.assign({}, viewer.info);
    Object.assign(viewer.info, change);
    renderMarks();
    try {
      const answer = await postJson('/api/m/mark', Object.assign({ id: item.i }, change));
      if (viewer.infoFor === item.i) {
        Object.assign(viewer.info, answer);
        renderMarks();
        if (!$('info-sheet').hidden) renderInfo();
      }
      // Die Kachel zeigt das Etikett: in der Liste nachtragen, das Raster zeichnet nach.
      if (typeof answer.label === 'string') {
        item.l = answer.label;
        if (viewer.grid) viewer.grid.schedule();
      }
    } catch (error) {
      if (viewer.infoFor === item.i) { viewer.info = before; renderMarks(); }
      toast(T.saveFailed);
    }
  }

  function toggleInfo(show) {
    const sheet = $('info-sheet');
    const open = show === undefined ? sheet.hidden : show;
    sheet.hidden = !open;
    if (open) renderInfo();
  }

  function infoRow(key, value) {
    if (value === undefined || value === null || value === '' || (Array.isArray(value) && value.length === 0)) return null;
    const row = el('div', 'info-row');
    row.appendChild(el('div', 'k', key));
    const v = el('div', 'v');
    if (value instanceof Node) v.appendChild(value); else v.textContent = value;
    row.appendChild(v);
    return row;
  }
  function chips(list) {
    const box = el('div', 'chips');
    for (const entry of list || []) box.appendChild(el('span', 'chip', entry));
    return box;
  }

  function renderInfo() {
    const body = $('info-body');
    const info = viewer.info;
    body.replaceChildren();
    if (!info) return;
    // Oben der Dateiname fett und ungekuerzt (er bricht um), bei einem RAW-Paar mit dem Hinweis
    // auf den Partner; bei einer fehlenden Datei der Vermerk. Darunter je Angabe eine Zeile mit
    // Bezeichnung links und Wert rechts.
    const head = el('div', 'info-name', info.name || '');
    body.appendChild(head);
    if ((info.partners || []).length) body.appendChild(el('div', 'info-partners', '+ ' + info.partners.join(', ')));
    if (info.missing) body.appendChild(el('div', 'info-missing', T.missing));
    const rows = [];
    if (info.folder) {
      let value = info.folder;
      if (info.folderId) {
        value = el('button', 'link', info.folder);
        value.type = 'button';
        value.addEventListener('click', () => {
          closeViewer();
          showTab('folders');
          folders.open(info.folderId);
        });
      }
      rows.push(infoRow(T.folders, value));
    }
    rows.push(infoRow(T.resolution, resolutionText(info.width, info.height)));
    rows.push(infoRow(T.fileSize, formatBytes(info.bytes)));
    rows.push(infoRow(T.format, info.format));
    if (info.motion) rows.push(infoRow(T.motion, info.motion === 'photo' ? T.motionPhoto : T.livePhoto));
    const date = parseIso(info.taken);
    rows.push(infoRow(T.date, date ? fullFormat.format(date) : ''));
    rows.push(infoRow(T.camera, info.camera));
    rows.push(infoRow(T.lens, info.lens));
    rows.push(infoRow(T.exposure, (info.exposure || []).join(', ')));
    rows.push(infoRow(T.place, info.place));
    if ((info.people || []).length) rows.push(infoRow(T.people, chips(info.people)));
    if ((info.tags || []).length) rows.push(infoRow(T.keywords, chips(info.tags)));
    if (info.label) {
      const tag = el('span', 'info-label');
      const dot = el('span', 'dot');
      paintLabel(dot, info.label);
      tag.appendChild(dot);
      tag.appendChild(el('span', null, LABEL_NAMES[info.label] || info.label));
      rows.push(infoRow(T.label, tag));
    }
    const list = el('div', 'info-list');
    rows.filter(Boolean).forEach(row => list.appendChild(row));
    body.appendChild(list);
  }
  // "6000 x 4000, 24 MP"; Megapixel ab einer Million Bildpunkte, mit einer Nachkommastelle darunter.
  function resolutionText(width, height) {
    if (!(width > 0 && height > 0)) return '';
    const mp = width * height / 1e6;
    const number = new Intl.NumberFormat(lang, { maximumFractionDigits: mp >= 10 ? 0 : 1 }).format(mp);
    return width + ' x ' + height + (mp >= 0.1 ? ', ' + number + ' MP' : '');
  }

  function showSheet(build) {
    const sheet = $('action-sheet');
    const body = $('action-body');
    body.replaceChildren();
    build(body, () => { sheet.hidden = true; });
    sheet.hidden = false;
  }
  function sheetAction(name, text, onClick) {
    const button = el('button', 'sheet-action');
    button.type = 'button';
    button.appendChild(icon(name));
    button.appendChild(el('span', null, text));
    button.addEventListener('click', onClick);
    return button;
  }
  document.addEventListener('pointerdown', event => {
    const sheet = $('action-sheet');
    if (!sheet.hidden && !sheet.contains(event.target)) { sheet.hidden = true; event.preventDefault(); }
  }, true);

  function showDownloadSheet() {
    const item = viewer.source && viewer.source.item(viewer.index);
    if (!item) return;
    showSheet((body, close) => {
      body.appendChild(el('div', 'sheet-title', T.download));
      body.appendChild(sheetAction('download', T.original, () => {
        close();
        download('/api/m/original?dl=1&id=' + encodeURIComponent(item.i));
      }));
      if (!item.v) {
        body.appendChild(sheetAction('image', T.edited, () => {
          close();
          download('/api/m/jpeg?id=' + encodeURIComponent(item.i));
        }));
      }
    });
  }

  // Umbenennen und Loeschen, nur mit "Verwalten" (info.canManage). Ein RAW-Paar geht als Ganzes.
  // Danach schliesst das Vollbild, und die Ansicht darunter laedt neu.
  function showManageSheet() {
    const item = viewer.source && viewer.source.item(viewer.index);
    const info = viewer.info;
    if (!item || !info || !info.canManage) return;
    showSheet((body, close) => {
      body.appendChild(el('div', 'sheet-title', info.name || ''));
      body.appendChild(sheetAction('rename', T.rename, async () => {
        close();
        const stem = (info.name || '').replace(/\.[^.]+$/, '');
        const wanted = window.prompt(T.newName, stem);
        if (wanted === null || wanted.trim() === '' || wanted.trim() === stem) return;
        try {
          await postJson('/api/m/rename', { id: item.i, name: wanted.trim() });
          afterFileChange();
        } catch (error) { toast(error.message || T.saveFailed); }
      }));
      body.appendChild(sheetAction('trash', T.delete, async () => {
        close();
        // Ohne Papierkorb (Einstellung der Anwendung) sagt die Rueckfrage, dass es endgueltig ist.
        const question = info.deleteForever ? T.deleteForeverConfirm : T.deleteConfirm;
        if (!window.confirm(fmt(question, info.name || ''))) return;
        try {
          await postJson('/api/m/delete', { id: item.i });
          afterFileChange();
        } catch (error) { toast(error.message || T.saveFailed); }
      }));
    });
  }
  function afterFileChange() {
    closeViewer();
    if (currentTab === 'folders') folders.reload();
    else if (currentTab === 'photos') photos.reload();
  }

  function showLabelSheet() {
    if (!viewer.info) return;
    showSheet((body, close) => {
      body.appendChild(el('div', 'sheet-title', T.label));
      const row = el('div', 'swatches');
      for (const label of LABELS) {
        const swatch = el('button', 'swatch' + (label ? '' : ' none'));
        paintLabel(swatch, label);
        swatch.type = 'button';
        swatch.setAttribute('aria-label', LABEL_NAMES[label]);
        swatch.title = LABEL_NAMES[label];
        if ((viewer.info.label || '') === label) swatch.classList.add('current');
        swatch.addEventListener('click', () => { close(); mark({ label }); });
        row.appendChild(swatch);
      }
      body.appendChild(row);
    });
  }

  // Teilen: die Vorschau mit Bearbeitung, die schon geladen ist. Ein erneutes Laden kostete die
  // Geste des Nutzers, ohne die ein Browser das Teilen verweigert.
  async function shareCurrent() {
    const item = viewer.source && viewer.source.item(viewer.index);
    const ready = item && viewer.blobs.get(item.i);
    if (!ready) { toast(T.loadFailed); return; }
    const name = ((viewer.info && viewer.info.name) || 'FerrumPix').replace(/\.[^.]+$/, '') + '.jpg';
    try {
      await navigator.share({ files: [new File([ready.blob], name, { type: 'image/jpeg' })] });
    } catch (ignored) { }
  }

  // --- Ansichten -------------------------------------------------------------------------------
  const tabs = [
    { key: 'photos', icon: 'photos', text: T.photos },
    { key: 'folders', icon: 'folder', text: T.folders },
    { key: 'search', icon: 'search', text: T.search },
    { key: 'more', icon: 'more', text: T.more }
  ];
  let currentTab = 'photos';
  function showTab(key) {
    const again = key === currentTab;
    currentTab = key;
    for (const tab of tabs) {
      $('view-' + tab.key).hidden = tab.key !== key;
      tab.button.classList.toggle('active', tab.key === key);
      tab.button.setAttribute('aria-selected', tab.key === key ? 'true' : 'false');
    }
    const view = views[key];
    if (view && view.shown) view.shown(again);
  }

  function emptyNote(host, text) {
    const note = el('div', 'empty', text);
    host.appendChild(note);
    return note;
  }

  // Zeitleiste
  const photos = (() => {
    const view = $('view-photos');
    const scroller = view.querySelector('.scroller');
    const host = view.querySelector('.grid-host');
    view.querySelector('.title').textContent = T.photos;
    let grid = null, note = null, loadedAt = 0, loading = false;
    async function load() {
      if (loading) return;
      loading = true;
      // Solange die erste Seite unterwegs ist, steht ein Hinweis da statt einer leeren Flaeche.
      if (!loadedAt && !note) note = emptyNote(host, T.loading);
      try {
        const source = new TimelineSource();
        await source.init();
        source.onChanged = () => { loadedAt = 0; load(); };
        // Ein Neuladen (der Katalog hat sich geaendert) bleibt an der Stelle, an der man war.
        const again = !!grid;
        if (!grid) {
          grid = new Grid(host, scroller, { scrubber: true, onOpen: (s, i) => openViewer(s, i, grid) });
          grid.options.onSelection = selectionUi(view, grid);
        }
        grid.setSource(source, again);
        diag.grid = grid;
        diag.source = source;
        if (note) { note.remove(); note = null; }
        if (source.total === 0) note = emptyNote(host, T.noPhotos);
        loadedAt = Date.now();
      } catch (error) {
        // Sichtbar statt leer: was schiefging und ein Knopf zum erneuten Versuch.
        console.error('FerrumPix timeline', error);
        if (note) note.remove();
        note = emptyNote(host, error && error.message === 'signed out' ? T.offline : T.loadError);
        note.appendChild(el('div', 'detail', error && error.message ? error.message : ''));
        const retry = el('button', 'text-button', T.retry);
        retry.type = 'button';
        retry.addEventListener('click', () => { loadedAt = 0; load(); });
        note.appendChild(retry);
      } finally { loading = false; }
    }
    return {
      shown(again) {
        if (!loadedAt) { load(); return; }
        if (again) { scroller.scrollTo({ top: 0, behavior: 'smooth' }); if (Date.now() - loadedAt > 30000) load(); }
      },
      reload() { loadedAt = 0; load(); }
    };
  })();

  // Ordner
  const folders = (() => {
    const view = $('view-folders');
    const scroller = view.querySelector('.scroller');
    const list = view.querySelector('.folder-list');
    const host = view.querySelector('.grid-host');
    const title = view.querySelector('.title');
    const back = view.querySelector('.back');
    const actions = view.querySelector('.actions');
    back.appendChild(icon('back'));
    back.setAttribute('aria-label', T.back);
    let grid = null, current = null, note = null, started = false;
    back.addEventListener('click', () => { if (current && current.parent !== null) open(current.parent || ''); });
    let sequence = 0;
    async function open(id) {
      started = true;
      // Nur die zuletzt verlangte Antwort zaehlt, auch wenn eine fruehere spaeter ankommt.
      const mine = ++sequence;
      let answer;
      try { answer = await getJson('/api/m/folder?id=' + encodeURIComponent(id || '')); }
      catch (error) { if (mine !== sequence) return; toast(T.loadFailed); if (!current && id) open(''); return; }
      if (mine !== sequence) return;
      current = answer;
      title.textContent = answer.name || T.folders;
      back.hidden = answer.parent === null || answer.parent === undefined;
      actions.replaceChildren();
      if (answer.upload) {
        const button = el('button', 'text-button', T.uploadHere);
        button.type = 'button';
        button.addEventListener('click', () => uploads.pick(answer.id, answer.name));
        actions.appendChild(button);
      }
      // Den Ordner als Link teilen, mit "Mitarbeiten" (shares.js).
      if (answer.share && answer.id && window.FerrumPixShares) {
        actions.appendChild(iconButton('share', T.share, () => window.FerrumPixShares.fromFolder(answer.id, answer.name)));
      }
      list.replaceChildren();
      for (const folder of answer.folders || []) {
        const row = el('button', 'folder-row');
        row.type = 'button';
        row.appendChild(icon('folder'));
        row.appendChild(el('span', null, folder.n));
        row.addEventListener('click', () => open(folder.i));
        list.appendChild(row);
      }
      if (!grid) {
        grid = new Grid(host, scroller, { onOpen: (s, i) => openViewer(s, i, grid) });
        grid.options.onSelection = selectionUi(view, grid);
      }
      grid.setSource(new ListSource(answer.items || []));
      if (note) { note.remove(); note = null; }
      if (answer.id && !(answer.items || []).length && !(answer.folders || []).length) note = emptyNote(host, T.emptyFolder);
      scroller.scrollTop = 0;
    }
    return {
      open,
      get current() { return current; },
      // Erneutes Tippen auf den Reiter fuehrt zurueck zu den Wurzeln.
      shown(again) { if (!started || again) open(''); },
      reload() { if (current) open(current.id); }
    };
  })();

  // Suche, Personen, Orte
  const search = (() => {
    const view = $('view-search');
    const scroller = view.querySelector('.scroller');
    const host = view.querySelector('.grid-host');
    const home = view.querySelector('.search-home');
    const title = view.querySelector('.title');
    const back = view.querySelector('.back');
    const input = $('search-input');
    back.appendChild(icon('back'));
    back.setAttribute('aria-label', T.back);
    input.placeholder = T.searchHint;
    input.setAttribute('aria-label', T.search);
    $('people-title').textContent = T.people;
    $('places-title').textContent = T.places;
    title.textContent = T.search;
    let grid = null, note = null, started = false;
    function showHome() {
      home.hidden = false;
      back.hidden = true;
      title.textContent = T.search;
      if (grid) grid.setSource(new ListSource([]));
      if (note) { note.remove(); note = null; }
    }
    async function showResults(heading, url) {
      title.textContent = heading;
      back.hidden = false;
      home.hidden = true;
      let answer = { items: [] };
      try { answer = await getJson(url); } catch (error) { toast(T.loadFailed); }
      if (!grid) {
        grid = new Grid(host, scroller, { onOpen: (s, i) => openViewer(s, i, grid) });
        grid.options.onSelection = selectionUi(view, grid);
      }
      grid.setSource(new ListSource(answer.items || []));
      if (note) { note.remove(); note = null; }
      if (!(answer.items || []).length) note = emptyNote(host, T.noMatches);
      // Das ganze Ergebnis als Link teilen, als feste Liste von jetzt (shares.js).
      const results = (answer.items || []).map(entry => entry.i);
      const actions = view.querySelector('.actions');
      actions.replaceChildren();
      if (results.length && window.FerrumPixShares) {
        actions.appendChild(iconButton('share', T.share, () => window.FerrumPixShares.fromIds(results, 'search', heading)));
      }
      scroller.scrollTop = 0;
    }
    back.addEventListener('click', () => { input.value = ''; view.querySelector('.actions').replaceChildren(); showHome(); });
    // Die Syntax wie in der Galerie (derselbe Kurzhinweis), dazu Vorschlaege beim Tippen: Personen
    // und Orte zum letzten Wort, die der Benutzer sehen darf. Ein Tipp setzt +"Name" an seine Stelle.
    $('search-syntax').textContent = T.searchSyntax || '';
    const suggestionBox = $('search-suggestions');
    let suggestTimer = 0, suggestSequence = 0;
    function lastWord() {
      const match = /(?:^|\s)([+-]?"[^"]*"?|[^\s]+)$/.exec(input.value);
      return match ? match[1] : '';
    }
    async function suggest() {
      const word = lastWord().replace(/^[+-]/, '').replace(/"/g, '');
      const mine = ++suggestSequence;
      if (word.length < 2) { suggestionBox.replaceChildren(); return; }
      let answer = { suggestions: [] };
      try { answer = await getJson('/api/m/suggest?q=' + encodeURIComponent(word)); } catch (ignored) { }
      if (mine !== suggestSequence) return;
      suggestionBox.replaceChildren();
      for (const entry of answer.suggestions || []) {
        const chip = el('button', 'chip');
        chip.type = 'button';
        chip.appendChild(icon(entry.k === 'place' ? 'place' : 'person'));
        chip.appendChild(el('span', null, entry.n));
        chip.addEventListener('click', () => {
          const word = lastWord();
          const before = input.value.slice(0, input.value.length - word.length);
          input.value = before + '+"' + entry.n + '" ';
          suggestionBox.replaceChildren();
          input.focus();
        });
        suggestionBox.appendChild(chip);
      }
    }
    input.addEventListener('input', () => {
      clearTimeout(suggestTimer);
      suggestTimer = setTimeout(suggest, 200);
    });
    $('search-form').addEventListener('submit', event => {
      suggestionBox.replaceChildren();
      event.preventDefault();
      const query = input.value.trim();
      input.blur();
      if (!query) { showHome(); return; }
      showResults(query, '/api/m/search?q=' + encodeURIComponent(query));
    });
    async function loadLists() {
      started = true;
      try {
        const [people, places] = await Promise.all([getJson('/api/m/people'), getJson('/api/m/places')]);
        const peopleBox = $('people');
        peopleBox.replaceChildren();
        for (const person of people.people || []) {
          const button = el('button', 'person');
          button.type = 'button';
          const img = el('img');
          img.alt = '';
          img.loading = 'lazy';
          img.src = '/api/m/face?id=' + encodeURIComponent(person.i);
          button.appendChild(img);
          button.appendChild(el('span', null, person.n));
          button.addEventListener('click', () => showResults(person.n, '/api/m/person?id=' + encodeURIComponent(person.i)));
          peopleBox.appendChild(button);
        }
        $('people-title').hidden = peopleBox.children.length === 0;
        const placesBox = $('places');
        placesBox.replaceChildren();
        for (const place of places.places || []) {
          const row = el('button', 'place-row');
          row.type = 'button';
          row.appendChild(icon('place'));
          row.appendChild(el('span', null, place.n));
          row.appendChild(el('small', null, String(place.c)));
          row.addEventListener('click', () => showResults(place.n, '/api/m/place?id=' + encodeURIComponent(place.i)));
          placesBox.appendChild(row);
        }
        $('places-title').hidden = placesBox.children.length === 0;
      } catch (ignored) { started = false; }
    }
    return { shown() { if (!started) loadLists(); } };
  })();

  // Mehr, Zweiter Faktor (Services/BrowserBridgeTwoFactor.vb): der eigene zweite Faktor fuer die
  // Anmeldung im Browser. Einrichten mit QR-Code und Schluessel zum Abtippen, aktiv erst mit einem
  // gueltigen Code; danach einmal die Wiederherstellungscodes, auch als Textdatei. Abschalten nur
  // mit Kennwort und Code. Die Texte kommen mit dem Stand (?texts=1).
  function twoFactor(menu, before) {
    let F = {};
    const state = { enabled: false, left: 0 };
    const fmt = (text, value) => String(text || '').replace('{0}', value);
    function field(type, label, autocomplete) {
      const input = el('input', 'share-field');
      input.type = type;
      input.placeholder = label;
      input.setAttribute('aria-label', label);
      input.autocomplete = autocomplete;
      return input;
    }
    function codeField() {
      const input = field('text', F.code, 'one-time-code');
      input.inputMode = 'numeric';
      input.maxLength = 6;
      input.className = 'share-field twofactor-code';
      return input;
    }
    function buttonRow(...buttons) {
      const row = el('div', 'share-buttons');
      for (const button of buttons) row.appendChild(button);
      return row;
    }
    function button(text, primary, onClick) {
      const node = el('button', primary ? 'share-primary' : 'sheet-link', text);
      node.type = primary ? 'submit' : 'button';
      if (onClick) node.addEventListener('click', onClick);
      return node;
    }
    async function refresh() {
      try {
        const answer = await getJson('/api/m/twofactor');
        state.enabled = !!answer.enabled;
        state.left = answer.recoveryLeft || 0;
      } catch (ignored) { }
    }
    function open() {
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', F.title));
        body.appendChild(el('p', 'twofactor-text', F.intro));
        body.appendChild(el('p', 'twofactor-text', state.enabled ? fmt(F.statusOn, state.left) : F.statusOff));
        if (!state.enabled) {
          body.appendChild(sheetAction('shield', F.setUp, () => begin()));
          return;
        }
        body.appendChild(el('p', 'twofactor-text', F.disableHint));
        const form = el('form', 'share-form');
        const password = field('password', F.password, 'current-password');
        const code = codeField();
        const submit = button(F.disable, true);
        form.append(password, code, buttonRow(submit));
        form.addEventListener('submit', async event => {
          event.preventDefault();
          if (!code.value.trim()) { toast(F.empty); code.focus(); return; }
          submit.disabled = true;
          try {
            await postJson('/api/m/twofactor/disable', { password: password.value, code: code.value });
            await refresh();
            close();
            toast(F.disabled);
          } catch (error) {
            toast(error.message || T.saveFailed);
            code.value = '';
          } finally {
            submit.disabled = false;
          }
        });
        body.appendChild(form);
        password.focus();
      });
    }
    async function begin() {
      let answer;
      try { answer = await postJson('/api/m/twofactor/begin', {}); }
      catch (error) { toast(error.message || T.saveFailed); return; }
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', F.title));
        body.appendChild(el('p', 'twofactor-text', F.scan));
        // Der QR-Code kommt vom eigenen Server, nur mit Sitzung und nie zwischengespeichert.
        const qr = el('img', 'twofactor-qr');
        qr.alt = F.title;
        qr.src = '/api/m/twofactor/qr?r=' + Date.now();
        body.appendChild(qr);
        body.appendChild(el('div', 'twofactor-label', F.key));
        body.appendChild(el('div', 'twofactor-key', answer.secret || ''));
        const form = el('form', 'share-form');
        const code = codeField();
        const submit = button(F.confirm, true);
        const cancel = button(F.cancel, false, async () => {
          try { await postJson('/api/m/twofactor/cancel', {}); } catch (ignored) { }
          close();
        });
        form.append(code, buttonRow(cancel, submit));
        form.addEventListener('submit', async event => {
          event.preventDefault();
          if (!code.value.trim()) { toast(F.empty); code.focus(); return; }
          submit.disabled = true;
          try {
            const done = await postJson('/api/m/twofactor/confirm', { code: code.value });
            await refresh();
            showRecovery(done.recovery || []);
          } catch (error) {
            toast(error.message || T.saveFailed);
            code.value = '';
            code.focus();
          } finally {
            submit.disabled = false;
          }
        });
        body.appendChild(form);
      });
    }
    // Die Wiederherstellungscodes stehen am Server nur als Hash; dies ist das einzige Mal, dass
    // sie zu sehen sind.
    function showRecovery(codes) {
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', F.recoveryTitle));
        body.appendChild(el('p', 'twofactor-text', F.enabled));
        body.appendChild(el('p', 'twofactor-text', F.recoveryHint));
        body.appendChild(el('pre', 'twofactor-codes', codes.join('\n')));
        const save = button(F.download, false, () => {
          const text = [F.recoveryTitle, F.recoveryHint, '', ...codes, ''].join('\r\n');
          const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
          const link = document.createElement('a');
          link.href = url;
          link.download = 'ferrumpix-recovery-codes.txt';
          document.body.appendChild(link);
          link.click();
          link.remove();
          setTimeout(() => URL.revokeObjectURL(url), 10000);
        });
        const done = button(F.done, true, () => close());
        done.type = 'button';
        body.appendChild(buttonRow(save, done));
      });
    }
    getJson('/api/m/twofactor?texts=1').then(answer => {
      F = answer.texts || {};
      state.enabled = !!answer.enabled;
      state.left = answer.recoveryLeft || 0;
      menu.insertBefore(sheetAction('shield', F.title, () => open()), before);
    }).catch(() => { });
  }

  // Mehr: Hochladen, Desktop, Abmelden, Sperren
  const more = (() => {
    $('more-title').textContent = T.more;
    const menu = $('menu');
    menu.appendChild(sheetAction('upload', T.upload, () => {
      showTab('folders');
      const current = folders.current;
      if (current && current.upload) uploads.pick(current.id, current.name);
    }));
    // Die volle Oberflaeche und das Sperren gibt es nur fuer einen Administrator (/api/m/me);
    // bis die Antwort da ist, stehen beide nicht im Menue. Der Server prueft trotzdem selbst.
    const desktop = sheetAction('desktop', T.desktop, () => {
      try { localStorage.setItem(variantKey, 'desktop'); } catch (ignored) { }
      location.href = '/';
    });
    const lock = sheetAction('lock', T.lock, async () => {
      try { await postJson('/api/m/lock', {}); } catch (ignored) { }
      location.replace('/m/');
    });
    desktop.hidden = true;
    lock.hidden = true;
    menu.appendChild(desktop);
    menu.appendChild(sheetAction('signout', T.signOut, async () => {
      try { await fetch('/logout', { method: 'POST', credentials: 'same-origin', cache: 'no-store' }); } catch (ignored) { }
      location.replace('/m/');
    }));
    menu.appendChild(lock);
    twoFactor(menu, lock);
    getJson('/api/m/me').then(me => {
      desktop.hidden = !me.desktop;
      lock.hidden = !me.lock;
      // Fuer die Rueckfrage beim Loeschen einer Auswahl (ohne Papierkorb: endgueltig).
      account.deleteForever = !!me.deleteForever;
      // Ohne die volle Oberflaeche merkt sich der Browser die mobile Seite.
      if (!me.desktop) { try { localStorage.setItem(variantKey, 'mobile'); } catch (ignored) { } }
      // Die eigene Sprache: gespeichert sofort am Server, dann laedt die Seite in ihr neu.
      const languages = me.languages || [];
      if (languages.length) {
        const current = languages.find(entry => entry[0] === me.language);
        const button = sheetAction('language', T.language + (current ? ': ' + current[1] : ''), () => {
          showSheet((body, close) => {
            body.appendChild(el('div', 'sheet-title', T.language));
            for (const [key, name] of languages) {
              const choice = sheetAction(key === me.language ? 'check' : 'language', name, async () => {
                close();
                try { await postJson('/api/m/language', { language: key }); location.reload(); }
                catch (error) { toast(T.saveFailed); }
              });
              body.appendChild(choice);
            }
          });
        });
        menu.insertBefore(button, menu.firstChild);
      }
      // Der eigene Benutzername (POST /api/m/username): gleiche Regeln wie in der Verwaltung,
      // die Sitzung bleibt. Die Anmeldeseite merkt sich den neuen Namen fuer das naechste Mal.
      let ownName = me.name || '';
      const nameButton = sheetAction('person', T.userName + (ownName ? ': ' + ownName : ''), async () => {
        const wanted = window.prompt(T.newUserName, ownName);
        if (wanted === null || wanted.trim() === '' || wanted.trim() === ownName) return;
        try {
          const answer = await postJson('/api/m/username', { name: wanted.trim() });
          ownName = answer.name || wanted.trim();
          nameButton.lastChild.textContent = T.userName + ': ' + ownName;
          try { localStorage.setItem('ferrumpix.user', ownName); } catch (ignored) { }
          toast(T.saved);
        } catch (error) { toast(error.message || T.saveFailed); }
      });
      menu.insertBefore(nameButton, menu.firstChild);
    }).catch(() => { });
    return { shown() { } };
  })();

  // --- Hochladen -------------------------------------------------------------------------------
  // Eine Datei je Anfrage, eine nach der anderen, mit Fortschritt (XMLHttpRequest). Name,
  // Endungen, Grenzen und "nie ueberschreiben" prueft der Server wie beim Hochladen der
  // Desktop-Seite.
  const uploads = (() => {
    const input = $('upload-input');
    let target = null, running = null;
    input.addEventListener('change', () => {
      const files = Array.from(input.files || []);
      input.value = '';
      if (files.length && target) start(target, files);
    });
    function pick(folderId, folderName) {
      if (running) return;
      target = { id: folderId, name: folderName };
      input.click();
    }
    function start(folder, files) {
      const batch = randomId();
      const state = { cancelled: false, xhr: null, done: 0 };
      running = state;
      const items = files.map(file => ({ file, node: null, bar: null, status: null }));
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', T.uploading + ': ' + folder.name));
        const summary = el('div', 'upload-item');
        const list = el('div', 'upload-list');
        for (const item of items) {
          item.node = el('div', 'upload-item');
          item.node.appendChild(el('div', 'name', item.file.name));
          const progress = el('div', 'progress');
          item.bar = el('div');
          progress.appendChild(item.bar);
          item.node.appendChild(progress);
          item.status = el('div', 'state');
          item.node.appendChild(item.status);
          list.appendChild(item.node);
        }
        body.appendChild(summary);
        body.appendChild(list);
        const button = sheetAction('close', T.cancel, () => {
          if (running === state) {
            state.cancelled = true;
            if (state.xhr) state.xhr.abort();
          } else close();
        });
        body.appendChild(button);
        state.summary = summary;
        state.button = button;
      });
      run(folder, items, batch, state);
    }
    async function run(folder, items, batch, state) {
      const limit = Number(T.maxBytes) || Infinity;
      let failed = 0;
      for (const item of items) {
        if (state.cancelled) { item.status.textContent = T.cancelled; continue; }
        state.summary.textContent = fmt(T.progress, state.done, items.length);
        if (item.file.size > limit) { item.status.textContent = T.tooLarge; item.node.classList.add('failed'); failed++; continue; }
        const result = await send(folder, item, batch, state);
        if (result.ok) { state.done++; item.status.textContent = T.uploaded; item.bar.style.width = '100%'; }
        else { failed++; item.node.classList.add('failed'); item.status.textContent = result.error || T.failed; }
      }
      state.summary.textContent = fmt(T.progress, state.done, items.length);
      running = null;
      state.button.querySelector('span').textContent = T.close;
      folders.reload();
      if (failed === 0 && !state.cancelled) setTimeout(() => { $('action-sheet').hidden = true; toast(T.uploaded); }, 1200);
    }
    function send(folder, item, batch, state) {
      return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        state.xhr = xhr;
        const url = '/api/m/upload?folder=' + encodeURIComponent(folder.id) + '&name=' + encodeURIComponent(item.file.name) + '&batch=' + batch;
        xhr.open('POST', url);
        xhr.setRequestHeader('X-FerrumPix-Mobile', '1');
        xhr.upload.onprogress = event => {
          if (event.lengthComputable) item.bar.style.width = Math.round(event.loaded / event.total * 100) + '%';
        };
        xhr.onload = () => {
          let answer = {};
          try { answer = JSON.parse(xhr.responseText); } catch (ignored) { }
          if (xhr.status === 401) location.reload();
          resolve({ ok: xhr.status === 200 && answer.ok, error: answer.error });
        };
        xhr.onerror = () => resolve({ ok: false, error: T.failed });
        xhr.onabort = () => resolve({ ok: false, error: T.cancelled });
        xhr.send(item.file);
      });
    }
    return { pick };
  })();

  // Fuer die Freigaben per Link (shares.js, eigene Datei): die Helfer dieser Seite.
  window.FerrumPixMobile = { el, icon, getJson, postJson, toast, showSheet, sheetAction };

  const views = { photos, folders, search, more };

  // --- Start ------------------------------------------------------------------------------------
  const tabBar = $('tabs');
  tabBar.setAttribute('role', 'tablist');
  for (const tab of tabs) {
    const button = el('button');
    button.type = 'button';
    button.setAttribute('role', 'tab');
    button.appendChild(icon(tab.icon));
    button.appendChild(el('span', null, tab.text));
    button.addEventListener('click', () => showTab(tab.key));
    tab.button = button;
    tabBar.appendChild(button);
  }
  // Ein Fehler im Skript soll sichtbar sein, nicht eine leere Seite hinterlassen; und ein Fehler
  // beim Einrichten des Vollbilds darf die Zeitleiste nicht mitnehmen (sie startet danach).
  window.addEventListener('error', event => toast(T.loadError + ': ' + (event.message || '')));
  window.addEventListener('unhandledrejection', event => {
    const reason = event.reason;
    if (reason && reason.name === 'AbortError') return;
    toast(T.loadError + ': ' + (reason && reason.message ? reason.message : String(reason)));
  });
  try { setupViewer(); } catch (error) { console.error('FerrumPix viewer', error); toast(T.loadError + ': ' + error.message); }
  try { localStorage.setItem(variantKey, 'mobile'); } catch (ignored) { }
  showTab('photos');

  // Als Web-App: der Service Worker haelt nur die feste Huelle. Ohne sicheren Ursprung (HTTP im
  // Heimnetz) gibt es ihn nicht; dann bleibt die Seite, wie sie ist.
  if ('serviceWorker' in navigator && window.isSecureContext) {
    navigator.serviceWorker.register('/m/sw.js', { scope: '/m/' }).catch(() => { });
  }
})();
