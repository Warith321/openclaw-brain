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

  // 用 GPT 批量翻译标题+摘要为中文（前60条）
  console.log('[Fetcher] Translating to Chinese via GPT...');
  const toTranslate = all.slice(0, 60);
  const translated = [];
  // 每批10条，减少 API 调用次数
  for (let i = 0; i < toTranslate.length; i += 10) {
    const batch = toTranslate.slice(i, i + 10);
    try {
      const input = batch.map((item, idx) =>
        `${idx + 1}. 标题: ${item.title}\n   摘要: ${item.summary ? item.summary.slice(0, 150) : ''}`
      ).join('\n');

      const result = await gptTranslateBatch(input, batch.length);
      for (let j = 0; j < batch.length; j++) {
        translated.push({ ...batch[j], titleZh: result[j]?.titleZh || '', summaryZh: result[j]?.summaryZh || '' });
      }
    } catch (e) {
      console.warn('[GPT] batch failed:', e.message);
      batch.forEach(item => translated.push({ ...item, titleZh: '', summaryZh: '' }));
    }
    await new Promise(r => setTimeout(r, 500));
  }
  const finalItems = [...translated, ...all.slice(60)];

  const cache = {
    updatedAt: new Date().toISOString(),
    total: finalItems.length,
    items: finalItems,
  };

  fs.writeFileSync(CACHE_FILE, JSON.stringify(cache, null, 2));
  console.log(`[Fetcher] Done. ${finalItems.length} items cached.`);
  return cache;
}

const GPT_API_KEY = 'sk-f95992ae53672c33dcc99d6b8b7709954e77ed2aecd364a363bee6b53b8b202b';
const GPT_API_URL = 'https://nekocode.ai/v1/chat/completions';
const GPT_MODEL = 'gpt5.4';

async function gptTranslateBatch(input, count) {
  const body = JSON.stringify({
    model: GPT_MODEL,
    messages: [
      {
        role: 'system',
        content: `你是一个AI新闻翻译助手。将用户提供的英文标题和摘要翻译成简洁的中文。
严格按照以下JSON格式返回，不要有其他内容：
[{"titleZh":"中文标题","summaryZh":"中文摘要"},...]
共${count}条，顺序对应。摘要控制在50字以内。`
      },
      { role: 'user', content: input }
    ],
    temperature: 0.3,
  });

  return new Promise((resolve, reject) => {
    const url = new URL(GPT_API_URL);
    const options = {
      hostname: url.hostname,
      path: url.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${GPT_API_KEY}`,
        'Content-Length': Buffer.byteLength(body),
      },
      timeout: 30000,
    };
    const req = https.request(options, (res) => {
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => {
        try {
          const json = JSON.parse(data);
          const content = json.choices[0].message.content.trim();
          const result = JSON.parse(content);
          resolve(result);
        } catch (e) {
          reject(new Error('GPT parse failed: ' + e.message));
        }
      });
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('GPT timeout')); });
    req.write(body);
    req.end();
  });
}

module.exports = { fetchAll, CACHE_FILE };

if (require.main === module) {
  fetchAll().catch(console.error);
}
