require('dotenv').config();
/**
 * wa-service.js
 * WhatsApp sender service. Runs whatsapp-web.js (headless Chromium) and
 * exposes a small HTTP API bound to localhost only.
 *
 *   GET  /health   -> { ready: true|false }
 *   GET  /groups   -> [{ id, name }]        (discover @g.us group ids)
 *   POST /send     -> { groupId | groupName, text }
 *
 * Every request must carry the shared secret in the X-Bridge-Token header.
 *
 * Chromium is located automatically, so this file needs no editing on a new
 * host. Override with CHROME_PATH in .env if required.
 */

const path = require("path");
const fs = require("fs");
const os = require("os");
const express = require("express");
const { Client, LocalAuth } = require("whatsapp-web.js");
const qrcode = require("qrcode-terminal");

const PORT = process.env.WA_PORT || 3000;
const TOKEN = process.env.BRIDGE_TOKEN || "change-me";

// ---------------------------------------------------------------
// Locate Chromium without hardcoding a user or version folder.
// Order: CHROME_PATH -> puppeteer cache -> local ./chromium
//        -> installed Google Chrome -> puppeteer default.
// ---------------------------------------------------------------
function firstChromeUnder(dir) {
  if (!dir || !fs.existsSync(dir)) return null;
  const stack = [dir];
  while (stack.length) {
    const cur = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(cur, { withFileTypes: true });
    } catch (e) {
      continue;
    }
    for (const e of entries) {
      const full = path.join(cur, e.name);
      if (e.isDirectory()) stack.push(full);
      else if (e.name.toLowerCase() === "chrome.exe") return full;
    }
  }
  return null;
}

function findChrome() {
  if (process.env.CHROME_PATH && fs.existsSync(process.env.CHROME_PATH)) {
    return process.env.CHROME_PATH;
  }
  const candidates = [
    path.join(os.homedir(), ".cache", "puppeteer"),
    path.join(__dirname, "chromium"),
    path.join(__dirname, "node_modules", "puppeteer", ".local-chromium"),
  ];
  for (const c of candidates) {
    const hit = firstChromeUnder(c);
    if (hit) return hit;
  }
  for (const p of [
    "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
    "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe",
  ]) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

const chromePath = findChrome();
console.log(
  chromePath
    ? "[wa] using Chromium: " + chromePath
    : "[wa] Chromium not found by search; using Puppeteer default"
);

let isReady = false;

const client = new Client({
  authStrategy: new LocalAuth({ dataPath: path.join(__dirname, "wa-auth") }),
  puppeteer: {
    headless: true,
    executablePath: chromePath || undefined,
    // These flags matter: without them Chromium fails to launch when the
    // service is started by Task Scheduler (non-interactive Session 0).
    args: [
      "--no-sandbox",
      "--disable-setuid-sandbox",
      "--disable-dev-shm-usage",
      "--disable-gpu",
      "--disable-software-rasterizer",
      "--no-first-run",
      "--no-zygote",
    ],
  },
});

client.on("qr", (qr) => {
  console.log("\n[wa] Scan this QR with WhatsApp > Linked Devices:\n");
  qrcode.generate(qr, { small: true });
});
client.on("authenticated", () => console.log("[wa] authenticated"));
client.on("auth_failure", (m) => console.error("[wa] auth failure:", m));
client.on("ready", () => {
  isReady = true;
  console.log("[wa] client ready");
});
client.on("disconnected", (r) => {
  isReady = false;
  console.error("[wa] disconnected:", r, "- attempting reinit");
  client.initialize().catch((e) => console.error("[wa] reinit failed:", e));
});

client.initialize();

const app = express();
app.use(express.json({ limit: "1mb" }));

app.use((req, res, next) => {
  if (req.get("X-Bridge-Token") !== TOKEN) {
    return res.status(401).json({ error: "unauthorized" });
  }
  next();
});

app.get("/health", (req, res) => res.json({ ready: isReady }));

app.get("/groups", async (req, res) => {
  if (!isReady) return res.status(503).json({ error: "not ready" });
  try {
    const chats = await client.getChats();
    res.json(
      chats
        .filter((c) => c.isGroup)
        .map((c) => ({ id: c.id._serialized, name: c.name }))
    );
  } catch (e) {
    res.status(500).json({ error: String(e) });
  }
});

async function resolveGroupId(groupName) {
  const chats = await client.getChats();
  const match = chats.find((c) => c.isGroup && c.name === groupName);
  return match ? match.id._serialized : null;
}

app.post("/send", async (req, res) => {
  if (!isReady) return res.status(503).json({ error: "not ready" });
  const { groupId, groupName, text } = req.body || {};
  if (!text || (!groupId && !groupName)) {
    return res.status(400).json({ error: "need text and groupId or groupName" });
  }
  try {
    let target = groupId;
    if (!target && groupName) {
      target = await resolveGroupId(groupName);
      if (!target) return res.status(404).json({ error: "group not found" });
    }
    await client.sendMessage(target, text);
    res.json({ ok: true, to: target });
  } catch (e) {
    console.error("[wa] send error:", e);
    res.status(500).json({ error: String(e) });
  }
});

app.listen(PORT, "127.0.0.1", () =>
  console.log("[wa] HTTP API on http://127.0.0.1:" + PORT)
);
