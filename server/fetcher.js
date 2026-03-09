const https = require('https');
const http = require('http');
const fs = require('fs');
const path = require('path');

const CACHE_FILE = path.join(__dirname, 'cache.json');

// RSS 源列表
const RSS_SOURCES = [
  { name: 'OpenAI Blog', url: 'https://openai.com/blog/rss.xml', tag: 'OpenAI' },
  { name: 'Anthropic Blog', url: 'https://www.anthropic.com/rss.xml', tag: 'Anthropic' },
  { name: 'Google AI Blog', url: 'https://blog.google/technology/ai/rss/', tag: 'Google AI' },
  { name: 'MIT Tech Review AI', url: 'https://www.technologyreview.com/topic/artificial-intelligence/feed/', tag: 'MIT TR' },
  { name: 'DeepMind Blog', url: 'https://deepmind.google/blog/rss.xml', tag: 'DeepMind' },
];

// HN AI 关键词过滤
const AI_KEYWORDS = [
  'ai', 'gpt', 'llm', 'claude', 'gemini', 'openai', 'anthropic', 'deepmind',
  'mistral', 'llama', 'transformer', 'machine learning', 'neural', 'diffusion',
  'chatgpt', 'artificial intelligence', 'hugging face', 'stable diffusion',
  'sora', 'dall-e', 'midjourney', 'model', 'inference', 'fine-tun',
];

function fetch(url) {
  return new Promise((resolve, reject) => {
    const mod = url.startsWith('https') ? https : http;
    const req = mod.get(url, {
      headers: {
        'User-Agent': 'Mozilla/5.0 (compatible; yaopfhome-aggregator/1.0)',
      },
      timeout: 10000,
    }, (res) => {
      if (res.statusCode === 301 || res.statusCode === 302) {
        return fetch(res.headers.location).then(resolve).catch(reject);
      }
      let data = '';
      res.on('data', chunk => data += chunk);
      res.on('end', () => resolve(data));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
  });
}

// 简单 RSS XML 解析
function parseRSS(xml, source) {
  const items = [];
  const itemRegex = /<item>([\s\S]*?)<\/item>/g;
  let match;
  while ((match = itemRegex.exec(xml)) !== null) {
    const block = match[1];
    const title = (block.match(/<title><!\[CDATA\[(.*?)\]\]><\/title>/) ||
                   block.match(/<title>(.*?)<\/title>/) || [])[1];
    const link = (block.match(/<link>(.*?)<\/link>/) ||
                  block.match(/<link\s+href="(.*?)"/) || [])[1];
    const pubDate = (block.match(/<pubDate>(.*?)<\/pubDate>/) ||
                     block.match(/<published>(.*?)<\/published>/) || [])[1];
    const desc = (block.match(/<description><!\[CDATA\[([\s\S]*?)\]\]><\/description>/) ||
                  block.match(/<description>([\s\S]*?)<\/description>/) || [])[1];

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
  for (const source of RSS_SOURCES) {
    try {
      const xml = await fetch(source.url);
      const items = parseRSS(xml, source);
      all.push(...items);
      console.log(`[RSS] ${source.name}: ${items.length} items`);
    } catch (e) {
      console.warn(`[RSS] ${source.name} failed:`, e.message);
    }
  }
  return all;
}

async function fetchHackerNews() {
  const items = [];
  try {
    const topJson = await fetch('https://hacker-news.firebaseio.com/v0/topstories.json');
    const ids = JSON.parse(topJson).slice(0, 100);

    const results = await Promise.allSettled(
      ids.map(id => fetch(`https://hacker-news.firebaseio.com/v0/item/${id}.json`))
    );

    for (const r of results) {
      if (r.status !== 'fulfilled') continue;
      try {
        const item = JSON.parse(r.value);
        if (!item || !item.title) continue;
        const titleLow = item.title.toLowerCase();
        const isAI = AI_KEYWORDS.some(kw => titleLow.includes(kw));
        if (!isAI) continue;
        items.push({
          title: item.title,
          url: item.url || `https://news.ycombinator.com/item?id=${item.id}`,
          source: 'Hacker News',
          tag: 'HN',
          date: new Date(item.time * 1000).toISOString(),
          summary: `${item.score || 0} points · ${item.descendants || 0} comments`,
          type: 'news',
          score: item.score || 0,
        });
      } catch (_) {}
    }
    // 按分数排序取前20
    items.sort((a, b) => b.score - a.score);
    console.log(`[HN] ${items.length} AI items`);
  } catch (e) {
    console.warn('[HN] failed:', e.message);
  }
  return items.slice(0, 20);
}

async function fetchReddit() {
  const items = [];
  const subs = ['MachineLearning', 'artificial', 'singularity', 'OpenAI'];
  for (const sub of subs) {
    try {
      const json = await fetch(`https://www.reddit.com/r/${sub}/hot.json?limit=10`);
      const data = JSON.parse(json);
      const posts = data.data.children;
      for (const p of posts) {
        const d = p.data;
        if (d.stickied) continue;
        items.push({
          title: d.title,
          url: d.url.startsWith('/') ? `https://reddit.com${d.url}` : d.url,
          source: `Reddit r/${sub}`,
          tag: 'Reddit',
          date: new Date(d.created_utc * 1000).toISOString(),
          summary: d.selftext ? d.selftext.slice(0, 200).trim() + '...' : `${d.score} upvotes · ${d.num_comments} comments`,
          type: 'forum',
          score: d.score,
        });
      }
      console.log(`[Reddit] r/${sub}: ${posts.length} items`);
    } catch (e) {
      console.warn(`[Reddit] r/${sub} failed:`, e.message);
    }
  }
  return items;
}

async function fetchAll() {
  console.log('[Fetcher] Starting fetch cycle...');
  const [rss, hn, reddit] = await Promise.all([
    fetchRSSSources(),
    fetchHackerNews(),
    fetchReddit(),
  ]);

  const all = [...rss, ...hn, ...reddit];
  // 按时间排序
  all.sort((a, b) => new Date(b.date) - new Date(a.date));

  const cache = {
    updatedAt: new Date().toISOString(),
    total: all.length,
    items: all,
  };

  fs.writeFileSync(CACHE_FILE, JSON.stringify(cache, null, 2));
  console.log(`[Fetcher] Done. ${all.length} items cached.`);
  return cache;
}

module.exports = { fetchAll, CACHE_FILE };

if (require.main === module) {
  fetchAll().catch(console.error);
}
