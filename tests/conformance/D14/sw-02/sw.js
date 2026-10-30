self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (event) { event.waitUntil(self.clients.claim()); });
self.addEventListener('fetch', function (event) { });
self.addEventListener('fetch', function (event) { if (new URL(event.request.url).pathname.endsWith('/api/status')) event.respondWith(Promise.resolve(new Response('api response'))); });
