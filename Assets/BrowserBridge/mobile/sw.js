'use strict';
// Der Service Worker der mobilen Seite (Geltungsbereich /m/). Er haelt NUR die feste Huelle:
// Skript, Stil, Offline-Seite, Symbole und Manifest, unter einem Versionsschluessel, den der
// Server beim Ausliefern einsetzt. Eine neue Fassung tauscht den Zwischenspeicher aus.
//
// NIE zwischengespeichert werden: Bilder, Antworten der Schnittstelle (/api/), die Seite selbst
// (/m/, sie haengt an der Sitzung), Anmeldung und Abmeldung. Fuer alles davon greift der Worker
// gar nicht erst ein; ohne Verbindung zeigt eine Navigation die Offline-Seite.
const VERSION = '__FERRUMPIX_VERSION__';
const CACHE = 'ferrumpix-m-' + VERSION;
const SHELL = [
  '/m/app.js?v=' + VERSION,
  '/m/app.css?v=' + VERSION,
  '/m/offline.html?v=' + VERSION,
  '/m/manifest.webmanifest',
  '/m/icon-192.png',
  '/m/icon-512.png'
];
const OFFLINE = '/m/offline.html?v=' + VERSION;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(key => key.startsWith('ferrumpix-m-') && key !== CACHE).map(key => caches.delete(key))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  // Schnittstelle und alles ausserhalb von /m/: nie aus dem Zwischenspeicher.
  if (url.pathname.startsWith('/api/') || !url.pathname.startsWith('/m/')) return;
  if (request.mode === 'navigate') {
    event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    return;
  }
  if (SHELL.includes(url.pathname + url.search)) {
    event.respondWith(caches.match(request).then(hit => hit || fetch(request)));
  }
});
