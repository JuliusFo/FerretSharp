// CDP calls into a FerretSharp test instance (Start-TestInstance.ps1). Picks the target with the app page
// (https://0.0.0.1/); a permission prompt can be another target in front of it.
//
//   node tools/e2e/cdp.mjs <port> eval <file.js>                 Runtime.evaluate (awaits promises), prints the value as JSON
//   node tools/e2e/cdp.mjs <port> calls <file.json> [out.png]     [{ "method": ..., "params": ... }, ...] in one connection
//                                                                 (an emulation ends with it); saves the last screenshot
//   node tools/e2e/cdp.mjs <port> screenshot <out.png> [x y w h]  Page.captureScreenshot from the surface (also when the
//                                                                 window is covered), optional clip in CSS pixels
//
// Expressions and calls come from files: PowerShell 5.1 drops quotes in arguments.
import { readFileSync, writeFileSync } from 'node:fs';

const [port, command, file, ...rest] = process.argv.slice(2);
if (!port || !command || !file) {
  console.error('usage: node cdp.mjs <port> eval <file.js> | calls <file.json> [out.png] | screenshot <out.png> [x y w h]');
  process.exit(2);
}

const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
const page = targets.find(t => t.type === 'page' && t.url === 'https://0.0.0.1/');
if (!page) throw new Error(`No FerretSharp page on port ${port}.`);

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.addEventListener('open', resolve); ws.addEventListener('error', reject); });
const pending = new Map();
let nextId = 1;
ws.addEventListener('message', e => {
  const msg = JSON.parse(e.data);
  const call = pending.get(msg.id);
  if (!call) return;
  pending.delete(msg.id);
  msg.error ? call.reject(new Error(`${call.method}: ${msg.error.message}`)) : call.resolve(msg.result);
});
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const id = nextId++;
  pending.set(id, { method, resolve, reject });
  ws.send(JSON.stringify({ id, method, params }));
});

try {
  if (command === 'eval') {
    const result = await send('Runtime.evaluate', { expression: readFileSync(file, 'utf8'), returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) {
      console.error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text);
      process.exitCode = 1;
    } else {
      console.log(JSON.stringify(result.result.value, null, 1));
    }
  } else if (command === 'calls') {
    let shot;
    for (const { method, params } of JSON.parse(readFileSync(file, 'utf8'))) {
      const result = await send(method, params);
      if (result.data) { shot = result.data; result.data = `<${result.data.length} base64 chars>`; }
      console.log(method, JSON.stringify(result));
    }
    if (rest[0] && shot) writeFileSync(rest[0], Buffer.from(shot, 'base64'));
  } else if (command === 'screenshot') {
    const params = { format: 'png', fromSurface: true };
    if (rest.length === 4) {
      const [x, y, width, height] = rest.map(Number);
      params.clip = { x, y, width, height, scale: 1 };
    }
    writeFileSync(file, Buffer.from((await send('Page.captureScreenshot', params)).data, 'base64'));
    console.log(`Saved ${file}`);
  } else {
    throw new Error(`Unknown command ${command}.`);
  }
} finally {
  ws.close();
}
