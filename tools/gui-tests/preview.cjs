const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const root = path.resolve(__dirname, '../../docs/gui-prototype');
http.createServer(async (req, res) => {
  const file = {'/':'index.html','/index.html':'index.html','/style.css':'style.css','/app.js':'app.js'}[new URL(req.url,'http://localhost').pathname];
  if (!file) { res.writeHead(404).end(); return; }
  try {
    const body = await fs.readFile(path.join(root,file));
    res.setHeader('Content-Type',file.endsWith('.css')?'text/css':file.endsWith('.js')?'text/javascript':'text/html; charset=utf-8');
    res.end(body);
  } catch { res.writeHead(500).end(); }
}).listen(4180,'127.0.0.1',()=>console.log('SmartMetrix GUI: http://127.0.0.1:4180'));
