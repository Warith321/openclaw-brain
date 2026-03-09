const https = require('https');
const http = require('http');
const fs = require('fs');
const path = require('path');

const CACHE_FILE = path.join(__dirname, 'cache.json');

const RSS_SOURCES = [
  { name: 'OpenAI Blog', url: 'https://openai.com/blog/rss.xml', tag: 'OpenAI' },
  { name: 'Anthropic Blog', url: 'https://www.anthropic.com/rss.xml', tag: 'Anthropic' },
  { name: 'Google AI Blog', url: 'https://blog.google/technology/ai/rss/', tag: 'Google AI' },
  { name: 'MIT Tech Review AI', url: 'https://www.technologyreview.com/topic/artificial-intelligence/feed/', tag: 'MIT TR' },
  { name: 'DeepMind Blog', url: 'https://deepmind.google/blog/rss.xml', tag: 'DeepMind' },
];

const AI_KEYWORDS = [
  'ai', 'gpt', 'llm', 'claude', 'gemini', 'openai', 'anthropic', 'deepmind',
  'mistral', 'llama', 'transformer', 'machine learning', 'neural', 'diffusion',
  'chatgpt', 'artificial intelligence', 'hugging face', 'sora', 'dall-e',
  'midjourney', 'model', 'inference', 'fine-tun',
];

function fetch(url) {
  return new Promise((resolve, reject) => {
    const mod = url.startsWith('https') ? https : http;
    const req = mod.get(url, {
      headers: { 'User-Agent': 'Mozilla/5.0 (compatible; yaopfhome/1.0)' },
      timeout: 10000,
    }, (res) => {
      if (res.statusCode === 301 || res.statusCode === 302) {
        return fetch(res.headers.location).then(resolve).catch(reject);
      }
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => resolve(data));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
  });
}

function parseRSS(xml, source) {
  const items = [];
  const re = /<item>([\s\S]*?)<\/item>/g;
  let m;
  while ((m = re.exec(xml)) !== null) {
    const b = m[1];
    const title = (b.match(/<title><!\[CDATA\[(.*?)\]\]><\/title>/) || b.match(/<title>(.*?)<\/title>/) || [])[1];
    const link = (b.match(/<link>(.*?)<\/link>/) || b.match(/<link\s+href="(.*?)"/) || [])[1];
    const pubDate = (b.match(/<pubDate>(.*?)<\/pubDate>/) || b.match(/<published>(.*?)<\/published>/) || [])[1];
    const desc = (b.match(/<description><!\[CDATA\[([\s\S]*?)\]\]><\/description>/) || b.match(/<description>([\s\S]*?)<\/description>/) || [])[1];
    if (title && link) {
      items.push({
        title: title.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').trim(),
        url: link.trim(),
        source: source.name,
        tag: source.tag,
        date: pubDate ? new Date(pubDate).toISOString() : new Date().toISOString(),
        summary: desc ? desc.replace(/<[^>]+>/g, '').slice(0, 200).trim() + '...' : '',
        type: 'blog',
      });
    }
  }
  return items.slice(0, 10);
}

async function fetchRSSSources() {
  const all = [];
  for (const s of RSS_SOURCES) {
    try {
      const xml = await fetch(s.url);
      const items = parseRSS(xml, s);
      all.push(...items);
      console.log(`[RSS] ${s.name}: ${items.length} items`);
    } catch (e) {
      console.warn(`[RSS] ${s.name} failed:`, e.message);
    }
  }
  return all;
}

async function fetchHackerNews() {
  const items = [];
  try {
    const ids = JSON.parse(await fetch('https://hacker-news.firebaseio.com/v0/topstories.json')).slice(0, 100);
    const results = await Promise.allSettled(ids.map(id => fetch(`https://hacker-news.firebaseio.com/v0/item/${id}.json`)));
    for (const r of results) {
      if (r.status !== 'fulfilled') continue;
      try {
        const item = JSON.parse(r.value);
        if (!item || !item.title) continue;
        if (!AI_KEYWORDS.some(kw => item.title.toLowerCase().includes(kw))) continue;
        items.push({
          title: item.title,
          url: item.url || `https://news.ycombinator.com/item?id=${item.id}`,
          source: 'Hacker News', tag: 'HN',
          date: new Date(item.time * 1000).toISOString(),
          summary: `${item.score || 0} points · ${item.descendants || 0} comments`,
          type: 'news', score: item.score || 0,
        });
      } catch (_) {}
    }
    items.sort((a, b) => b.score - a.score);
    console.log(`[HN] ${items.length} AI items`);
  } catch (e) {
    console.warn('[HN] failed:', e.message);
  }
  return items.slice(0, 20);
}

async function fetchReddit() {
  const items = [];
  for (const sub of ['MachineLearning', 'artificial', 'singularity', 'OpenAI']) {
    try {
      const data = JSON.parse(await fetch(`https://www.reddit.com/r/${sub}/hot.json?limit=10`));
      for (const p of data.data.children) {
        const d = p.data;
        if (d.stickied) continue;
        items.push({
          title: d.title,
          url: d.url.startsWith('/') ? `https://reddit.com${d.url}` : d.url,
          source: `Reddit r/${sub}`, tag: 'Reddit',
          date: new Date(d.created_utc * 1000).toISOString(),
          summary: d.selftext ? d.selftext.slice(0, 200) + '...' : `${d.score} upvotes · ${d.num_comments} comments`,
          type: 'forum', score: d.score,
        });
      }
      console.log(`[Reddit] r/${sub} ok`);
    } catch (e) {
      console.warn(`[Reddit] r/${sub} failed:`, e.message);
    }
  }
  return items;
}

async function translateToZh(text) {
  if (!text) return '';
  try {
    const encoded = encodeURIComponent(text.slice(0, 300));
    const res = await fetch(`https://api.mymemory.translated.net/get?q=${encoded}&langpair=en|zh`);
    const data = JSON.parse(res);
    if (data && data.responseData && data.responseData.translatedText) {
      return data.responseData.translatedText;
    }
  } catch (e) {}
  return '';
}

async function fetchAll() {
  console.log('[Fetcher] Starting fetch cycle...');
  const [rss, hn, reddit] = await Promise.all([fetchRSSSources(), fetchHackerNews(), fetchReddit()]);
  const all = [...rss, ...hn, ...reddit].sort((a, b) => new Date(b.date) - new Date(a.date));

  console.log('[Fetcher] Translating to Chinese...');
  const translated = [];
  for (const item of all.slice(0, 60)) {
    try {
      const titleZh = await translateToZh(item.title);
      const summaryZh = item.summary ? await translateToZh(item.summary.slice(0, 150)) : '';
      translated.push({ ...item, titleZh, summaryZh });
    } catch (_) {
      translated.push({ ...item, titleZh: '', summaryZh: '' });
    }
    await new Promise(r => setTimeout(r, 300));
  }
  const finalItems = [...translated, ...all.slice(60)];

  const cache = { updatedAt: new Date().toISOString(), total: finalItems.length, items: finalItems };
  fs.writeFileSync(CACHE_FILE, JSON.stringify(cache, null, 2));
  console.log(`[Fetcher] Done. ${finalItems.length} items cached.`);
  return cache;
}

module.exports = { fetchAll, CACHE_FILE };

if (require.main === module) {
  fetchAll().catch(console.error);
}
