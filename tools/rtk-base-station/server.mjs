import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { Station } from './station.mjs';
const root = path.dirname(fileURLToPath(import.meta.url));
const station = new Station(process.env.RTK_DATA || path.join(root, 'data', 'station.json'));
const host = process.env.RTK_HOST || '127.0.0.1';
const port = Number(process.env.PORT || 8092);
const password = process.env.RTK_PASSWORD;
if (!['127.0.0.1', 'localhost', '::1'].includes(host) && !password) throw Error('Для доступа по сети задайте RTK_PASSWORD.');
const token = randomBytes(24).toString('hex');
const equal = (a, b) => { const x = Buffer.from(a), y = Buffer.from(b); return x.length === y.length && timingSafeEqual(x, y); };
const server = http.createServer(async (req, res) => {
  res.setHeader('Cache-Control', 'no-store'); res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
  const json = (code, body) => { res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' }); res.end(JSON.stringify(body)); };
  if (password && !equal(req.headers.authorization || '', 'Basic ' + Buffer.from('admin:' + password).toString('base64'))) { res.writeHead(401, { 'WWW-Authenticate': 'Basic realm="RTK Base", charset="UTF-8"' }); return res.end('Authentication required'); }
  const url = new URL(req.url, 'http://localhost');
  if (req.method === 'GET' && url.pathname === '/api/status') return json(200, { ...station.status(), token });
  if (req.method === 'GET' && url.pathname === '/api/backup') { res.setHeader('Content-Disposition', 'attachment; filename="rtk-base-backup.json"'); return json(200, { version: 1, simulator: true, ...station.saved }); }
  if (req.method === 'POST' && url.pathname === '/api/action') {
    if (!equal(req.headers['x-rtk-token'] || '', token)) return json(403, { error: 'Обновите страницу перед выполнением команды.' });
    try {
      let body = ''; for await (const chunk of req) { body += chunk; if (Buffer.byteLength(body) > 16384) return json(413, { error: 'Слишком большой запрос.' }); }
      const input = JSON.parse(body); return json(200, station.action(input.action, input.data));
    } catch (error) { return json(400, { error: error.message }); }
  }
  const files = { '/': ['index.html', 'text/html'], '/app.js': ['app.js', 'text/javascript'], '/style.css': ['style.css', 'text/css'], '/favicon.svg': ['favicon.svg', 'image/svg+xml'] };
  if (req.method === 'GET' && files[url.pathname]) { const [name, type] = files[url.pathname]; res.writeHead(200, { 'Content-Type': type + '; charset=utf-8' }); return res.end(fs.readFileSync(path.join(root, 'public', name))); }
  json(404, { error: 'Не найдено' });
});
server.listen(port, host, () => console.log(`RTK Base simulator: http://${host}:${port}`));
