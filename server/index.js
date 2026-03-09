const express = require('express');
const fs = require('fs');
const path = require('path');
const { fetchAll, CACHE_FILE } = require('./fetcher');

const app = express();
const PORT = 3001;
const INTERVAL_MS = 30 * 60 * 1000; // 30 分钟

app.use((req, res, next) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  next();
});

app.get('/api/news', (req, res) => {
  if (!fs.existsSync(CACHE_FILE)) {
    return res.status(503).json({ error: 'Cache not ready, please wait...' });
  }
  try {
    const data = JSON.parse(fs.readFileSync(CACHE_FILE, 'utf-8'));
    const { tag, type, limit = 50 } = req.query;
    let items = data.items;
    if (tag) items = items.filter(i => i.tag === tag);
    if (type) items = items.filter(i => i.type === type);
    items = items.slice(0, parseInt(limit));
    res.json({ updatedAt: data.updatedAt, total: items.length, items });
  } catch (e) {
    res.status(500).json({ error: 'Failed to read cache' });
  }
});

app.get('/api/status', (req, res) => {
  if (!fs.existsSync(CACHE_FILE)) {
    return res.json({ ready: false });
  }
  const data = JSON.parse(fs.readFileSync(CACHE_FILE, 'utf-8'));
  res.json({ ready: true, updatedAt: data.updatedAt, total: data.total });
});

app.listen(PORT, () => {
  console.log(`[Server] API running on port ${PORT}`);
  // 启动时立即拉一次
  fetchAll().catch(console.error);
  // 定时 30 分钟刷新
  setInterval(() => fetchAll().catch(console.error), INTERVAL_MS);
});
