'use strict';
// Freigaben per Link in der mobilen Seite, Gegenstelle zu Services/BrowserBridgeShares.vb: einen
// Link erstellen (Vollbild, Auswahl, Ordner, Suchergebnis) und die eigenen Freigaben verwalten
// (Mehr, Meine Freigaben). Eigene Datei neben app.js; app.js stellt seine Helfer unter
// window.FerrumPixMobile bereit und ruft window.FerrumPixShares an den Stellen, an denen es
// geteilt werden kann.
//
// Gespeichert ist am Server nur der Hash eines Links. Den Link selbst merkt sich dieses Geraet
// beim Erstellen (localStorage); ein anderes Geraet kann nur einen neuen erzeugen.
(() => {
  const M = window.FerrumPixMobile;
  if (!M) return;
  const { el, icon, getJson, postJson, toast, showSheet, sheetAction } = M;
  const fmt = (text, ...args) => String(text || '').replace(/\{(\d+)\}/g, (m, i) => (args[+i] ?? ''));
  const lang = document.documentElement.lang || 'en';
  const dateFormat = new Intl.DateTimeFormat(lang, { dateStyle: 'medium' });
  const linksKey = 'ferrumpix.shareLinks';
  let S = {};
  let state = { admin: false, paused: false };

  function rememberLink(id, url) {
    try {
      const links = JSON.parse(localStorage.getItem(linksKey) || '{}');
      links[id] = url;
      localStorage.setItem(linksKey, JSON.stringify(links));
    } catch (ignored) { }
  }
  function knownLink(id) {
    try { return JSON.parse(localStorage.getItem(linksKey) || '{}')[id] || ''; } catch (ignored) { return ''; }
  }
  function forgetLink(id) {
    try {
      const links = JSON.parse(localStorage.getItem(linksKey) || '{}');
      delete links[id];
      localStorage.setItem(linksKey, JSON.stringify(links));
    } catch (ignored) { }
  }

  async function copyText(text) {
    try { await navigator.clipboard.writeText(text); toast(S.copied); return; } catch (ignored) { }
    // Ohne Zwischenablage (HTTP im Heimnetz): zum Abschreiben anbieten.
    window.prompt(S.copy, text);
  }
  async function shareUrl(title, url) {
    if (navigator.share) {
      try { await navigator.share({ title, url }); return; } catch (error) { if (error && error.name === 'AbortError') return; }
    }
    copyText(url);
  }

  // --- Formular ---------------------------------------------------------------------------------
  function checkRow(text, checked) {
    const label = el('label', 'share-check');
    const box = el('input');
    box.type = 'checkbox';
    box.checked = !!checked;
    label.appendChild(box);
    label.appendChild(el('span', null, text));
    return { label, box };
  }
  function dayChoices(current) {
    const select = el('select', 'share-field');
    for (const days of [7, 30, 90, 365, 0]) {
      const option = el('option', null, days === 0 ? S.never : days === 365 ? S.oneYear : fmt(S.days, days));
      option.value = String(days);
      if (days === current) option.selected = true;
      select.appendChild(option);
    }
    return select;
  }
  function daysLeft(share) {
    if (!share.expires) return 0;
    const left = (new Date(share.expires) - Date.now()) / 86400000;
    const choices = [7, 30, 90, 365];
    return choices.find(days => days >= left) || 365;
  }

  // Ein Formular fuer Neu und Bearbeiten. target: was geteilt wird; share: die bestehende Freigabe.
  function openForm(target, share) {
    showSheet((body, close) => {
      body.classList.add('share-form');
      body.appendChild(el('div', 'sheet-title', share ? S.edit : S.create));
      const title = el('input', 'share-field');
      title.type = 'text';
      title.maxLength = 120;
      title.value = share ? share.title : (target.title || '');
      title.placeholder = S.title;
      title.setAttribute('aria-label', S.title);
      body.appendChild(title);
      const expiryLabel = el('label', 'share-label', S.expiry);
      const days = dayChoices(share ? (share.expires ? daysLeft(share) : 0) : 30);
      expiryLabel.appendChild(days);
      body.appendChild(expiryLabel);
      const sub = target && target.kind === 'folder' ? checkRow(S.subfolders, false) : null;
      if (sub) body.appendChild(sub.label);
      const download = checkRow(S.allowDownload, share ? share.download : true);
      const place = checkRow(S.sharePlace, share ? share.place : false);
      const people = checkRow(S.sharePeople, share ? share.people : false);
      const camera = checkRow(S.shareCamera, share ? share.camera : true);
      for (const row of [download, place, people, camera]) body.appendChild(row.label);
      const locked = checkRow(S.withPassword, share ? share.password : false);
      body.appendChild(locked.label);
      const password = el('input', 'share-field');
      password.type = 'password';
      password.autocomplete = 'new-password';
      password.placeholder = share && share.password ? S.passwordKeep : S.password;
      password.setAttribute('aria-label', S.password);
      password.hidden = !locked.box.checked;
      locked.box.addEventListener('change', () => { password.hidden = !locked.box.checked; if (!password.hidden) password.focus(); });
      body.appendChild(password);
      const buttons = el('div', 'share-buttons');
      const cancel = el('button', 'text-button', S.cancel);
      cancel.type = 'button';
      cancel.addEventListener('click', close);
      const submit = el('button', 'share-primary', share ? S.save : S.createButton);
      submit.type = 'button';
      buttons.appendChild(cancel);
      buttons.appendChild(submit);
      body.appendChild(buttons);
      submit.addEventListener('click', async () => {
        const secret = locked.box.checked ? password.value : '';
        if (secret && secret.length < 8) { toast(S.tooShort); return; }
        if (locked.box.checked && !secret && !(share && share.password)) { toast(S.tooShort); return; }
        const values = {
          title: title.value.trim(), days: +days.value, download: download.box.checked,
          place: place.box.checked, people: people.box.checked, camera: camera.box.checked
        };
        submit.disabled = true;
        try {
          if (share) {
            Object.assign(values, { share: share.id, password: secret, removePassword: !locked.box.checked });
            await postJson('/api/m/share-edit', values);
            close();
            manage.refresh();
          } else {
            const request = Object.assign({}, target, values, { password: secret, sub: sub ? sub.box.checked : false });
            const answer = await postJson('/api/m/share', request);
            close();
            rememberLink(answer.share.id, answer.url);
            showCreated(answer.share, answer.url);
          }
        } catch (error) {
          submit.disabled = false;
          toast(error.message || S.saveFailed);
        }
      });
    });
  }

  function showCreated(share, url) {
    showSheet((body, close) => {
      body.appendChild(el('div', 'sheet-title', S.created));
      const field = el('input', 'share-field share-url');
      field.type = 'text';
      field.readOnly = true;
      field.value = url;
      field.addEventListener('focus', () => field.select());
      body.appendChild(field);
      body.appendChild(sheetAction('download', S.copy, () => { close(); copyText(url); }));
      body.appendChild(sheetAction('upload', S.share, () => { close(); shareUrl(share.title, url); }));
    });
  }

  // --- Meine Freigaben ----------------------------------------------------------------------------
  const manage = (() => {
    let panel = null, list = null, pause = null;
    function build() {
      panel = el('section', 'shares-panel');
      panel.hidden = true;
      const bar = el('header', 'bar');
      const back = el('button', 'icon back');
      back.type = 'button';
      back.appendChild(icon('back'));
      back.setAttribute('aria-label', S.cancel);
      back.addEventListener('click', () => history.back());
      bar.appendChild(back);
      bar.appendChild(el('h1', 'title', S.myShares));
      panel.appendChild(bar);
      const scroller = el('div', 'scroller');
      pause = checkRow(S.pauseAll, state.paused);
      pause.label.classList.add('share-pause');
      pause.box.addEventListener('change', async () => {
        try {
          const answer = await postJson('/api/m/shares-pause', { paused: pause.box.checked });
          state.paused = !!answer.paused;
          refresh();
        } catch (error) { pause.box.checked = !pause.box.checked; toast(S.saveFailed); }
      });
      scroller.appendChild(pause.label);
      list = el('div', 'share-list');
      scroller.appendChild(list);
      panel.appendChild(scroller);
      document.body.appendChild(panel);
      window.addEventListener('popstate', () => { if (panel && !panel.hidden) panel.hidden = true; });
    }
    function details(share) {
      const parts = [];
      parts.push(share.kind === 'folder' ? share.folder || S.folder : fmt(S.count, share.count));
      if (!share.active) parts.push(share.expired ? S.expired : S.inactive);
      else parts.push(share.expires ? fmt(S.until, dateFormat.format(new Date(share.expires))) : S.noEnd);
      parts.push(fmt(S.views, share.views));
      return parts.join(', ');
    }
    async function refresh() {
      let answer;
      try { answer = await getJson('/api/m/shares'); } catch (error) { toast(S.loadFailed); return; }
      state.paused = !!answer.paused;
      state.admin = !!answer.admin;
      pause.label.hidden = !state.admin;
      pause.box.checked = state.paused;
      list.replaceChildren();
      if (state.paused) list.appendChild(el('div', 'share-paused', S.paused));
      const shares = answer.shares || [];
      if (!shares.length) { list.appendChild(el('div', 'empty', S.none)); return; }
      for (const share of shares) {
        const row = el('div', 'share-row');
        const text = el('div', 'share-text');
        text.appendChild(el('div', 'share-title', share.title));
        text.appendChild(el('div', 'share-details', details(share)));
        row.appendChild(text);
        const more = el('button', 'icon');
        more.type = 'button';
        more.appendChild(icon('more'));
        more.setAttribute('aria-label', share.title);
        more.addEventListener('click', () => rowActions(share));
        row.appendChild(more);
        list.appendChild(row);
      }
    }
    // Den Link gibt es nur auf dem Geraet, das ihn erstellt hat; sonst einen neuen erzeugen.
    async function linkOf(share) {
      const known = knownLink(share.id);
      if (known) return known;
      if (!window.confirm(S.relinkConfirm)) return '';
      try {
        const answer = await postJson('/api/m/share-link', { share: share.id });
        rememberLink(share.id, answer.url);
        return answer.url;
      } catch (error) { toast(error.message || S.saveFailed); return ''; }
    }
    function rowActions(share) {
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', share.title));
        body.appendChild(sheetAction('download', S.copy, async () => {
          close();
          const url = await linkOf(share);
          if (url) copyText(url);
        }));
        body.appendChild(sheetAction('upload', S.share, async () => {
          close();
          const url = await linkOf(share);
          if (url) shareUrl(share.title, url);
        }));
        body.appendChild(sheetAction('rename', S.edit, () => { close(); openForm(null, share); }));
        body.appendChild(sheetAction('trash', S.revoke, async () => {
          close();
          if (!window.confirm(S.revokeConfirm)) return;
          try {
            await postJson('/api/m/share-revoke', { share: share.id });
            forgetLink(share.id);
            refresh();
          } catch (error) { toast(error.message || S.saveFailed); }
        }));
      });
    }
    function open() {
      if (!panel) build();
      panel.hidden = false;
      history.pushState({ shares: true }, '');
      refresh();
    }
    return { open, refresh() { if (panel && !panel.hidden) refresh(); } };
  })();

  // --- Einstiege fuer app.js ---------------------------------------------------------------------
  window.FerrumPixShares = {
    // Der Knopf Teilen im Vollbild: Link erstellen (mit Mitarbeiten) und An App senden.
    fromViewer(item, info, sendToApp, canSend) {
      if (!item) return;
      const canLink = !!(info && info.canShare);
      if (!canLink) { if (canSend) sendToApp(); return; }
      showSheet((body, close) => {
        body.appendChild(el('div', 'sheet-title', S.share || ''));
        body.appendChild(sheetAction('share', S.create, () => {
          close();
          openForm({ kind: 'image', ids: [item.i], title: (info.name || '').replace(/\.[^.]+$/, '') });
        }));
        if (canSend) body.appendChild(sheetAction('upload', S.sendToApp, () => { close(); sendToApp(); }));
      });
    },
    // Eine Auswahl oder ein Suchergebnis als feste Liste.
    fromIds(ids, kind, title) {
      if (!ids || !ids.length) return;
      openForm({ kind: kind === 'search' ? 'search' : 'selection', ids, title: title || '' });
    },
    // Ein Ordner, auf Wunsch mit Unterordnern.
    fromFolder(id, name) {
      openForm({ kind: 'folder', folder: id, title: name || '' });
    },
    manage: () => manage.open()
  };

  // Die Texte kommen mit der ersten Liste; danach steht "Meine Freigaben" im Menue unter Mehr.
  getJson('/api/m/shares?texts=1').then(answer => {
    S = answer.texts || {};
    state.admin = !!answer.admin;
    state.paused = !!answer.paused;
    const menu = document.getElementById('menu');
    if (menu) {
      const entry = sheetAction('share', S.myShares, () => manage.open());
      menu.insertBefore(entry, menu.children[1] || null);
    }
  }).catch(() => { });
})();
