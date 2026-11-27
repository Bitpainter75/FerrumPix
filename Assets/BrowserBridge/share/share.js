'use strict';
// Die Seite einer Freigabe per Link, Gegenstelle zu Services/BrowserBridgeShares.vb. Ohne fremde
// Bibliotheken. Ein Raster, ein Vollbild mit Wischen und Pfeilknoepfen, Herunterladen einzeln und
// als ZIP, wenn die Freigabe es erlaubt. Die Seite spricht nur mit den Wegen unter ihrem eigenen
// Pfad (/s/<token>/...); Bilder tragen Kennungen, die nur in dieser Freigabe gelten.
//
// Kacheln laedt eine kleine Schlange mit AbortController: was aus dem Blick scrollt, wird
// abgebrochen und dem Server gemeldet (cancel), damit er es nicht mehr rechnet.
(() => {
  const T = JSON.parse(document.getElementById('fp-texts').textContent || '{}');
  const $ = id => document.getElementById(id);
  const lang = document.documentElement.lang || 'en';
  const fmt = (text, ...args) => String(text || '').replace(/\{(\d+)\}/g, (m, i) => (args[+i] ?? ''));
  const base = location.pathname.replace(/\/+$/, '');
  const ICONS = {
    back: '<path d="M15 18l-6-6 6-6"/>',
    forward: '<path d="M9 18l6-6-6-6"/>',
    close: '<path d="M18 6L6 18M6 6l12 12"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 16v-5M12 8h.01"/>',
    download: '<path d="M12 3v12M7 10l5 5 5-5M5 21h14"/>',
    play: '<path d="M8 5v14l11-7z"/>',
    live: '<circle cx="12" cy="12" r="2.5"/><circle cx="12" cy="12" r="6"/><circle cx="12" cy="12" r="9.5" stroke-dasharray="1.5 2.6"/>',
    sound: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12"/>',
    muted: '<path d="M4 9h4l5-4v14l-5-4H4z"/><path d="M17 9.5l5 5M22 9.5l-5 5"/>'
  };
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.innerHTML = ICONS[name] || '';
    return svg;
  }
  function el(tag, cls, text) {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }
  function setIcon(button, name, label) {
    button.replaceChildren(icon(name));
    button.setAttribute('aria-label', label);
    button.title = label;
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
  const dateFormat = new Intl.DateTimeFormat(lang, { dateStyle: 'long' });
  const fullFormat = new Intl.DateTimeFormat(lang, { dateStyle: 'full', timeStyle: 'short' });
  function parseIso(text) {
    if (!text) return null;
    const date = new Date(text);
    return isNaN(date) ? null : date;
  }

  $('brand').textContent = T.sharedWith || '';
  if (T.state !== 'ok') {
    $('title').textContent = 'FerrumPix';
    showNote(T.state === 'busy' ? T.busy : T.gone);
    return;
  }

  function showNote(text) {
    const note = $('note');
    note.textContent = text;
    note.hidden = !text;
  }

  // --- Laden mit Abbruch ---------------------------------------------------------------------
  const pendingCancels = new Set();
  let cancelTimer = 0;
  function reportCancel(id) {
    pendingCancels.add(id);
    clearTimeout(cancelTimer);
    cancelTimer = setTimeout(() => {
      const list = Array.from(pendingCancels);
      pendingCancels.clear();
      fetch(base + '/cancel', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ r: list })
      }).catch(() => { });
    }, 150);
  }
  function randomId() {
    const bytes = new Uint8Array(12);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  }
  class Loader {
    constructor(limit) { this.limit = limit; this.active = 0; this.queue = []; }
    load(url) {
      const job = { url, ctrl: new AbortController(), id: randomId(), started: false };
      job.promise = new Promise((resolve, reject) => { job.resolve = resolve; job.reject = reject; });
      this.queue.push(job);
      this.pump();
      return job;
    }
    cancel(job) {
      if (!job) return;
      const index = this.queue.indexOf(job);
      if (index >= 0) { this.queue.splice(index, 1); job.reject(new DOMException('aborted', 'AbortError')); return; }
      if (job.started && !job.done) { job.ctrl.abort(); reportCancel(job.id); }
    }
    pump() {
      while (this.active < this.limit && this.queue.length) {
        const job = this.queue.shift();
        job.started = true;
        this.active++;
        fetch(job.url, { credentials: 'same-origin', signal: job.ctrl.signal, headers: { 'X-FerrumPix-Request': job.id } })
          .then(response => { if (!response.ok) throw new Error('HTTP ' + response.status); return response.blob(); })
          .then(blob => { job.done = true; job.resolve(blob); }, error => { job.done = true; job.reject(error); })
          .finally(() => { this.active--; this.pump(); });
      }
    }
  }
  const tiles = new Loader(4);
  const previews = new Loader(2);

  // --- Liste -----------------------------------------------------------------------------------
  let items = [];
  async function loadList() {
    let response;
    try { response = await fetch(base + '/list', { credentials: 'same-origin', cache: 'no-store' }); }
    catch (error) { showNote(T.loadFailed); return; }
    let answer = {};
    try { answer = await response.json(); } catch (ignored) { }
    if (response.status === 401 && answer.password) { showLock(); return; }
    if (response.status === 404) { $('title').textContent = 'FerrumPix'; showNote(T.gone); return; }
    if (!response.ok) { showNote(response.status === 429 ? T.busy : T.loadFailed); return; }
    $('lock').hidden = true;
    items = answer.items || [];
    const title = answer.title || 'FerrumPix';
    $('title').textContent = title;
    document.title = title;
    const parts = [fmt(T.count, items.length)];
    const until = parseIso(answer.expires);
    if (until) parts.push(fmt(T.until, dateFormat.format(until)));
    $('meta').textContent = parts.join(', ');
    const zip = $('zip');
    zip.hidden = !answer.zip;
    zip.replaceChildren(icon('download'), el('span', null, T.zip));
    zip.onclick = () => download(base + '/zip');
    showNote(items.length ? '' : T.noPhotos);
    buildGrid();
  }

  function showLock() {
    $('title').textContent = 'FerrumPix';
    const form = $('lock');
    form.hidden = false;
    $('lock-title').textContent = T.passwordTitle;
    const input = $('lock-password');
    input.placeholder = T.password;
    input.setAttribute('aria-label', T.password);
    $('lock-open').textContent = T.open;
    input.focus();
  }
  $('lock').addEventListener('submit', async event => {
    event.preventDefault();
    const error = $('lock-error');
    error.hidden = true;
    const input = $('lock-password');
    let response;
    try {
      response = await fetch(base + '/unlock', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: input.value })
      });
    } catch (ignored) { error.textContent = T.loadFailed; error.hidden = false; return; }
    if (response.ok) { input.value = ''; loadList(); return; }
    let answer = {};
    try { answer = await response.json(); } catch (ignored) { }
    if (response.status === 404) { $('lock').hidden = true; showNote(T.gone); return; }
    error.textContent = answer.error || (response.status === 429 ? T.busy : T.wrongPassword);
    error.hidden = false;
    input.select();
  });

  // --- Raster ----------------------------------------------------------------------------------
  const tileJobs = new Map();
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      const tile = entry.target;
      const index = +tile.dataset.index;
      if (entry.isIntersecting) startTile(tile, index);
      else stopTile(tile);
    }
  }, { rootMargin: '400px 0px' });

  function buildGrid() {
    const grid = $('grid');
    observer.disconnect();
    grid.replaceChildren();
    items.forEach((item, index) => {
      const tile = el('button', 'tile');
      tile.type = 'button';
      tile.dataset.index = index;
      tile.setAttribute('aria-label', item.n || '');
      const img = el('img');
      img.alt = '';
      tile.appendChild(img);
      if (item.v) { const badge = el('span', 'badge'); badge.appendChild(icon('play')); tile.appendChild(badge); }
      tile.addEventListener('click', () => openViewer(index));
      grid.appendChild(tile);
      observer.observe(tile);
    });
  }
  function startTile(tile, index) {
    const img = tile.querySelector('img');
    if (img.classList.contains('loaded') || tileJobs.has(tile)) return;
    const item = items[index];
    const job = tiles.load(base + '/thumb?i=' + encodeURIComponent(item.i) + '&s=' + encodeURIComponent(item.s || ''));
    tileJobs.set(tile, job);
    job.promise.then(blob => {
      img.onload = () => { img.classList.add('loaded'); URL.revokeObjectURL(img.src); };
      img.src = URL.createObjectURL(blob);
    }, () => { }).finally(() => tileJobs.delete(tile));
  }
  function stopTile(tile) {
    const job = tileJobs.get(tile);
    if (job) { tiles.cancel(job); tileJobs.delete(tile); }
  }

  // --- Vollbild --------------------------------------------------------------------------------
  const viewer = { index: -1, job: null, info: null, motion: null, motionMuted: true };
  setIcon($('close'), 'back', T.close);
  setIcon($('prev'), 'back', T.previous);
  setIcon($('next'), 'forward', T.next);
  setIcon($('download'), 'download', T.download);
  setIcon($('info'), 'info', T.info);
  $('close').addEventListener('click', () => history.back());
  $('prev').addEventListener('click', () => step(-1));
  $('next').addEventListener('click', () => step(1));
  $('download').addEventListener('click', () => {
    const item = items[viewer.index];
    if (item && item.x) download(base + '/original?dl=1&i=' + encodeURIComponent(item.i));
  });
  $('info').addEventListener('click', toggleInfo);
  $('live').addEventListener('click', () => { if (viewer.motion) stopMotion(); else playMotion(); });
  $('sound').addEventListener('click', () => {
    viewer.motionMuted = !viewer.motionMuted;
    if (viewer.motion) viewer.motion.muted = viewer.motionMuted;
    updateMotionButtons();
  });
  window.addEventListener('popstate', () => { if (!$('viewer').hidden) hideViewer(); });
  document.addEventListener('keydown', event => {
    if ($('viewer').hidden) return;
    if (event.key === 'ArrowRight') step(1);
    else if (event.key === 'ArrowLeft') step(-1);
    else if (event.key === 'Escape') history.back();
  });

  function openViewer(index) {
    $('viewer').hidden = false;
    history.pushState({ viewer: true }, '');
    show(index);
  }
  function hideViewer() {
    stopMotion();
    $('viewer').hidden = true;
    $('sheet').hidden = true;
    previews.cancel(viewer.job);
    viewer.job = null;
    const stage = $('stage');
    for (const node of stage.querySelectorAll('img')) if (node.src.startsWith('blob:')) URL.revokeObjectURL(node.src);
    stage.replaceChildren();
  }
  function step(direction) {
    const next = viewer.index + direction;
    if (next >= 0 && next < items.length) show(next);
  }
  const previewEdge = Math.min(2048, Math.round(Math.max(screen.width || 1024, screen.height || 1024) * (window.devicePixelRatio || 1)));
  function show(index) {
    stopMotion();
    viewer.index = index;
    viewer.info = null;
    $('sheet').hidden = true;
    const item = items[index];
    $('prev').classList.toggle('off', index <= 0);
    $('next').classList.toggle('off', index >= items.length - 1);
    $('download').hidden = !item.x;
    updateMotionButtons();
    setViewerTitle(item);
    previews.cancel(viewer.job);
    viewer.job = null;
    const stage = $('stage');
    for (const node of stage.querySelectorAll('img')) if (node.src.startsWith('blob:')) URL.revokeObjectURL(node.src);
    stage.replaceChildren();
    if (item.v && item.p) {
      const video = el('video');
      video.controls = true;
      video.playsInline = true;
      video.preload = 'metadata';
      video.src = base + '/original?i=' + encodeURIComponent(item.i);
      stage.appendChild(video);
      return;
    }
    const img = el('img');
    img.alt = '';
    stage.appendChild(img);
    const job = previews.load(base + '/preview?i=' + encodeURIComponent(item.i) + '&s=' + encodeURIComponent(item.s || '') + '&w=' + previewEdge);
    viewer.job = job;
    job.promise.then(blob => {
      if (viewer.job !== job) return;
      img.src = URL.createObjectURL(blob);
    }, error => {
      if (viewer.job !== job || (error && error.name === 'AbortError')) return;
      stage.replaceChildren(el('div', 'failed', T.loadFailed));
    });
  }

  // Bewegtbild (Motion Photo, Live Photo): nur, wo die Liste es meldet (m), und das tut sie nur,
  // wenn der Film hinausgehen darf. Der Knopf spielt ihn einmal stumm ueber dem Bild ab.
  function updateMotionButtons() {
    const item = items[viewer.index];
    $('live').hidden = !(item && item.m);
    setIcon($('live'), 'live', viewer.motion ? T.stopMotion : T.playMotion);
    $('live').classList.toggle('on', !!viewer.motion);
    $('sound').hidden = !viewer.motion;
    setIcon($('sound'), viewer.motionMuted ? 'muted' : 'sound', T.mute);
    $('sound').setAttribute('aria-pressed', viewer.motionMuted ? 'true' : 'false');
  }
  function playMotion() {
    const item = items[viewer.index];
    if (viewer.motion || !item || !item.m) return;
    const video = el('video', 'motion');
    video.muted = viewer.motionMuted;
    if (viewer.motionMuted) video.setAttribute('muted', '');
    video.playsInline = true;
    video.setAttribute('playsinline', '');
    video.preload = 'auto';
    // Laesst sich der Film nicht spielen, steht einfach wieder das Bild.
    const failed = () => { if (viewer.motion === video) stopMotion(); };
    video.addEventListener('ended', () => { if (viewer.motion === video) stopMotion(); });
    video.addEventListener('error', failed);
    video.src = base + '/motion?i=' + encodeURIComponent(item.i);
    $('stage').appendChild(video);
    viewer.motion = video;
    updateMotionButtons();
    const started = video.play();
    if (started && started.catch) started.catch(error => { if (!(error && error.name === 'AbortError')) failed(); });
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

  // Der Titel in zwei Zeilen: oben Datum und Uhrzeit der Aufnahme, darunter kleiner der
  // Dateiname, in der Mitte gekuerzt (voll im Tooltip). Ohne Aufnahmezeit nur der Name.
  const shortFormat = new Intl.DateTimeFormat(lang, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  function middleEllipsis(text, max) {
    text = String(text || '');
    if (max < 8 || text.length <= max) return text;
    const tail = Math.max(4, Math.floor((max - 1) * 0.45));
    return text.slice(0, max - 1 - tail) + '…' + text.slice(text.length - tail);
  }
  function setViewerTitle(item) {
    const title = $('viewer-title');
    title.replaceChildren();
    title.removeAttribute('title');
    if (!item) return;
    const date = parseIso(item.t);
    const name = item.n || '';
    if (date) title.appendChild(el('div', 'when', shortFormat.format(date)));
    if (name) {
      const fontPx = date ? 12 : 14;
      const max = title.clientWidth > 0 ? Math.floor(title.clientWidth / (fontPx * 0.56)) : 40;
      title.appendChild(el('div', date ? 'name' : 'name only', middleEllipsis(name, max)));
      title.title = name;
      title.setAttribute('aria-label', (date ? shortFormat.format(date) + ', ' : '') + name);
    }
  }
  window.addEventListener('resize', () => { if (!$('viewer').hidden && viewer.index >= 0) setViewerTitle(items[viewer.index]); });
  function formatBytes(bytes) {
    if (!(bytes > 0)) return '';
    const units = ['B', 'KB', 'MB', 'GB'];
    let value = bytes, unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return new Intl.NumberFormat(lang, { maximumFractionDigits: unit === 0 ? 0 : 1 }).format(value) + ' ' + units[unit];
  }
  function resolutionText(width, height) {
    if (!(width > 0 && height > 0)) return '';
    const mp = width * height / 1e6;
    const number = new Intl.NumberFormat(lang, { maximumFractionDigits: mp >= 10 ? 0 : 1 }).format(mp);
    return width + ' x ' + height + (mp >= 0.1 ? ', ' + number + ' MP' : '');
  }

  // Wischen: waagrecht blaettern, Tippen blendet die Leisten aus.
  (() => {
    const stage = $('viewer');
    let startX = 0, startY = 0, startTime = 0, tracking = false;
    stage.addEventListener('touchstart', event => {
      if (event.touches.length !== 1 || event.target.closest('.sheet, .viewer-top, .nav')) { tracking = false; return; }
      tracking = true;
      startX = event.touches[0].clientX;
      startY = event.touches[0].clientY;
      startTime = Date.now();
    }, { passive: true });
    stage.addEventListener('touchend', event => {
      if (!tracking) return;
      tracking = false;
      const touch = event.changedTouches[0];
      const dx = touch.clientX - startX, dy = touch.clientY - startY;
      if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) step(dx < 0 ? 1 : -1);
      else if (Math.abs(dx) < 10 && Math.abs(dy) < 10 && Date.now() - startTime < 300) $('viewer').classList.toggle('chrome-off');
    });
  })();

  async function toggleInfo() {
    const sheet = $('sheet');
    if (!sheet.hidden) { sheet.hidden = true; return; }
    const item = items[viewer.index];
    if (!item) return;
    sheet.replaceChildren(el('div', 'row plain', T.loading));
    sheet.hidden = false;
    let info = null;
    try {
      const response = await fetch(base + '/info?i=' + encodeURIComponent(item.i), { credentials: 'same-origin', cache: 'no-store' });
      if (response.ok) info = await response.json();
    } catch (ignored) { }
    if (items[viewer.index] !== item) return;
    sheet.replaceChildren();
    if (!info) { sheet.appendChild(el('div', 'row plain', T.loadFailed)); return; }
    // Oben der Dateiname fett und ungekuerzt; ein Ordner geht nie nach aussen.
    sheet.appendChild(el('div', 'info-name', info.name || ''));
    const row = (key, value) => {
      if (value === undefined || value === null || value === '' || (Array.isArray(value) && !value.length)) return;
      const node = el('div', 'row');
      node.appendChild(el('div', 'k', key));
      node.appendChild(el('div', 'v', Array.isArray(value) ? value.join(', ') : String(value)));
      sheet.appendChild(node);
    };
    row(T.resolution, resolutionText(info.width, info.height));
    row(T.fileSize, formatBytes(info.bytes));
    row(T.format, info.format);
    const date = parseIso(info.taken);
    row(T.date, date ? fullFormat.format(date) : '');
    row(T.camera, info.camera);
    row(T.lens, info.lens);
    row(T.exposure, info.exposure);
    row(T.place, info.place);
    row(T.people, info.people);
  }

  loadList();
})();
