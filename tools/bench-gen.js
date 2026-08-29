// 百万级基准库生成器:100 个源 × 每源 1 万篇 = 100 万篇 + Evidence 相关表。
// 近期(48h 内)1 万篇,其余历史(用于窗口类命令的基准)。
// 用法: node bench-gen.js <rss.db 路径>
const { DatabaseSync } = require('node:sqlite');
const db = new DatabaseSync(process.argv[2]);

const FEEDS = 100;
const PER_FEED = 10000;          // 100 万篇
const RECENT = 10000;            // 48h 内的篇数(窗口命令只处理这些)
const KEYWORDS = ['熊猫', '量子计算', 'RAG架构', '开源许可证', '终端安全', '数据库', '分布式', '编译器'];
const EVIDENCE_COUNT = 10000;    // 证据条数
const EVIDENCE_RECENT = 1000;    // 近期证据数
const TAG_COUNT = 50;            // 标签数
const GROUP_COUNT = 20;          // 主题分组数
const WATCH_COUNT = 500;         // 监控目标数

db.exec('PRAGMA journal_mode = WAL');
db.exec('PRAGMA synchronous = OFF');
db.exec('PRAGMA foreign_keys = ON');

console.log('建 Feeds...');
db.exec('BEGIN');
const feedStmt = db.prepare("INSERT INTO Feeds (Id, Title, FeedUrl, LastCheckedAt) VALUES (?, ?, ?, ?)");
for (let f = 1; f <= FEEDS; f++) {
  feedStmt.run(f, `源${f}`, `http://feed${f}.example.com/feed.xml`, null);
}
db.exec('COMMIT');

console.log('建 Items(100 万)...');
const kwPool = [];
for (let i = 0; i < 200; i++) {
  const kw = KEYWORDS[i % KEYWORDS.length];
  const body = [];
  for (let j = 0; j < 12; j++) body.push(`这是第 ${i}-${j} 段关于${kw}的正文内容,讨论${kw}的实践与${kw}的工程问题。`);
  kwPool.push(kw + ' ' + body.join('\n'));
}

const now = Date.now();
const HOUR = 3600_000;
const day = 24 * HOUR;
const itemStmt = db.prepare(
  "INSERT INTO Items (Id, FeedId, Title, Link, Description, Content, Guid, Status, Version, PublishDate) VALUES (?,?,?,?,?,?,?,?,1,?)"
);
let recentCount = 0;
let id = 0;
db.exec('BEGIN');
const t0 = Date.now();
for (let f = 1; f <= FEEDS; f++) {
  for (let k = 0; k < PER_FEED; k++) {
    id++;
    const pool = kwPool[(f * 31 + k) % kwPool.length];
    const kw = pool.split(' ')[0];
    // 前 RECENT 篇(按 id 顺序)为近期,其余历史
    const isRecent = id <= RECENT;
    const pub = isRecent
      ? new Date(now - (id % 40) * HOUR).toISOString()
      : new Date(now - (30 + (id % 700)) * day).toISOString();
    if (isRecent) recentCount++;
    const content = isRecent ? pool : `历史文章 ${id}:` + pool.substring(0, 200);
    itemStmt.run(
      id, f,
      `标题${id} ${kw}`, `http://feed${f}.example.com/a${id}`,
      `摘要 ${kw} 相关内容。`, content,
      `guid-${id}`, 'active', pub
    );
  }
  if (f % 20 === 0) {
    db.exec('COMMIT'); db.exec('BEGIN');
    console.log(`  ${f}/${FEEDS} 源,${id} 篇,${((Date.now() - t0) / 1000).toFixed(1)}s`);
  }
}
db.exec('COMMIT');
console.log(`完成: ${id} 篇(近期 ${recentCount}),耗时 ${((Date.now() - t0) / 1000).toFixed(1)}s`);

// ══════════ Evidence 相关表 ══════════
console.log('\n建 Evidence 相关表...');

// 创建 Groups 表
db.exec('BEGIN');
db.exec(`
  CREATE TABLE IF NOT EXISTS Groups (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Label       TEXT NOT NULL,
    Centroid    BLOB,
    ModelId     INTEGER,
    CreatedAt   TEXT,
    UpdatedAt   TEXT
  )
`);
const groupStmt = db.prepare("INSERT INTO Groups (Id, Label, CreatedAt) VALUES (?, ?, ?)");
for (let g = 1; g <= GROUP_COUNT; g++) {
  const label = `主题${g}-${KEYWORDS[g % KEYWORDS.length]}`;
  groupStmt.run(g, label, new Date(now - g * day).toISOString());
}
db.exec('COMMIT');
console.log(`  Groups: ${GROUP_COUNT} 个主题`);

// 创建 Tags 表
db.exec('BEGIN');
db.exec(`
  CREATE TABLE IF NOT EXISTS Tags (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL UNIQUE,
    Color       TEXT,
    CreatedAt   TEXT,
    UpdatedAt   TEXT
  )
`);
const tagStmt = db.prepare("INSERT INTO Tags (Id, Name, Color, CreatedAt) VALUES (?, ?, ?, ?)");
const tagColors = ['#FF5722', '#4CAF50', '#2196F3', '#FF9800', '#9C27B0', '#00BCD4', '#795548', '#607D8B'];
for (let t = 1; t <= TAG_COUNT; t++) {
  tagStmt.run(t, `标签${t}-${KEYWORDS[t % KEYWORDS.length]}`, tagColors[t % tagColors.length], new Date(now - t * 3600_000).toISOString());
}
db.exec('COMMIT');
console.log(`  Tags: ${TAG_COUNT} 个标签`);

// 创建 Evidence 表
db.exec('BEGIN');
db.exec(`
  CREATE TABLE IF NOT EXISTS Evidence (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Schema      TEXT NOT NULL DEFAULT 'sip-evidence-v1',
    SourceType  TEXT NOT NULL,
    SourceKey   TEXT NOT NULL,
    SourceName  TEXT,
    SourceUrl   TEXT,
    Title       TEXT,
    Excerpt     TEXT,
    Content     TEXT,
    Hash        TEXT,
    Version     INTEGER DEFAULT 1,
    Status      TEXT DEFAULT 'active',
    StatusNote  TEXT,
    PrevId      INTEGER,
    Grade       TEXT,
    Reversed    INTEGER DEFAULT 0,
    CapturedAt  TEXT,
    ObservedAt  TEXT,
    Verified    INTEGER DEFAULT 0,
    ConfirmedAt TEXT,
    Freshness   TEXT DEFAULT 'fresh',
    TtlDays     INTEGER DEFAULT 7,
    Consensus   REAL  DEFAULT 0,
    ProducerMeta TEXT,
    GroupId     INTEGER,
    DynamicPage INTEGER DEFAULT 0,
    FragmentId  TEXT,
    Platform    TEXT,
    ContentId   TEXT,
    Author      TEXT,
    CanonicalUrl TEXT,
    Context     TEXT,
    Snapshot    TEXT,
    Note        TEXT,
    WatchEnabled INTEGER DEFAULT 0,
    WatchInterval INTEGER DEFAULT 5,
    WatchLastCheckedAt TEXT,
    WatchLastHash TEXT,
    ViewCount    INTEGER DEFAULT 0,
    LastViewedAt TEXT
  )
`);
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_status    ON Evidence (Status, Freshness)');
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_url       ON Evidence (SourceUrl)');
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_group     ON Evidence (GroupId)');
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_sourcekey ON Evidence (SourceKey)');
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_fragment  ON Evidence (FragmentId)');
db.exec('CREATE INDEX IF NOT EXISTS idx_evidence_platform  ON Evidence (Platform)');

// 生成 Evidence 数据
const evidenceKwPool = [];
for (let i = 0; i < 100; i++) {
  const kw = KEYWORDS[i % KEYWORDS.length];
  const body = [];
  for (let j = 0; j < 8; j++) body.push(`证据内容 ${i}-${j}: 关于${kw}的深度分析和讨论。`);
  evidenceKwPool.push({ kw, body: body.join('\n') });
}

const evidenceStmt = db.prepare(`
  INSERT INTO Evidence (Id, Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash, Version, Status, Grade, Reversed, CapturedAt, ObservedAt, Verified, Freshness, TtlDays, GroupId, Platform, ContentId, Author, FragmentId, ViewCount)
  VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
`);
let evidenceRecentCount = 0;
let eid = 0;
db.exec('BEGIN');
const t1 = Date.now();
for (let i = 0; i < EVIDENCE_COUNT; i++) {
  eid++;
  const pool = evidenceKwPool[i % evidenceKwPool.length];
  const kw = pool.kw;
  const isRecent = i < EVIDENCE_RECENT;
  const platform = ['bilibili', 'twitter', 'reddit', 'github', 'zhihu'][i % 5];
  const captured = isRecent
    ? new Date(now - (i % 40) * HOUR).toISOString()
    : new Date(now - (30 + (i % 700)) * day).toISOString();
  if (isRecent) evidenceRecentCount++;
  
  // 模拟版本链:每10条证据有一个前版本
  const prevId = (i % 10 === 0 && i > 0) ? i - 9 : null;
  const version = prevId ? 2 : 1;
  
  evidenceStmt.run(
    eid,
    'sip-evidence-v1',
    'evidence',
    `evidence:bench-${eid}`,
    `来源${(i % 20) + 1}`,
    `https://example.com/evidence/${eid}`,
    `证据标题${eid} ${kw}`,
    pool.body.substring(0, 100),
    pool.body,
    `hash-${eid}-${version}`,
    version,
    'active',
    ['⚪', '🟡', '🔴'][i % 3],
    i % 20 === 0 ? 1 : 0,
    captured,
    captured,
    i % 10 === 0 ? 1 : 0,
    isRecent ? 'fresh' : 'stale',
    isRecent ? 7 : 30,
    (i % GROUP_COUNT) + 1,
    platform,
    `content-${eid}`,
    `作者${(i % 30) + 1}`,
    i % 5 === 0 ? `reply:${i - 5 > 0 ? i - 5 : 1}` : null,
    Math.floor(Math.random() * 100)
  );
}
db.exec('COMMIT');
console.log(`  Evidence: ${eid} 条(近期 ${evidenceRecentCount}),耗时 ${((Date.now() - t1) / 1000).toFixed(1)}s`);

// 创建 EvidenceTags 表并生成数据
db.exec('BEGIN');
db.exec(`
  CREATE TABLE IF NOT EXISTS EvidenceTags (
    EvidenceId  INTEGER NOT NULL,
    TagId       INTEGER NOT NULL,
    CreatedAt   TEXT,
    PRIMARY KEY (EvidenceId, TagId),
    FOREIGN KEY (EvidenceId) REFERENCES Evidence(Id) ON DELETE CASCADE,
    FOREIGN KEY (TagId) REFERENCES Tags(Id) ON DELETE CASCADE
  )
`);
db.exec('CREATE INDEX IF NOT EXISTS idx_evidencetags_tag ON EvidenceTags (TagId)');
const evidenceTagStmt = db.prepare("INSERT INTO EvidenceTags (EvidenceId, TagId, CreatedAt) VALUES (?, ?, ?)");
let tagCount = 0;
for (let i = 1; i <= eid; i++) {
  // 每条证据关联 1-3 个标签
  const numTags = 1 + (i % 3);
  for (let t = 0; t < numTags; t++) {
    const tagId = ((i * 7 + t * 13) % TAG_COUNT) + 1;
    evidenceTagStmt.run(i, tagId, new Date(now - i * HOUR).toISOString());
    tagCount++;
  }
}
db.exec('COMMIT');
console.log(`  EvidenceTags: ${tagCount} 条关联`);

// 创建 WatchTargets 表并生成数据
db.exec('BEGIN');
db.exec(`
  CREATE TABLE IF NOT EXISTS WatchTargets (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Url             TEXT NOT NULL UNIQUE,
    FirstEvidenceId INTEGER,
    LastCheckedAt   TEXT,
    LastHash        TEXT,
    CreatedAt       TEXT
  )
`);
const watchStmt = db.prepare("INSERT INTO WatchTargets (Id, Url, FirstEvidenceId, LastCheckedAt, LastHash, CreatedAt) VALUES (?, ?, ?, ?, ?, ?)");
for (let w = 1; w <= WATCH_COUNT; w++) {
  const evidenceId = Math.floor((w / WATCH_COUNT) * eid) + 1;
  watchStmt.run(
    w,
    `https://watch-target-${w}.example.com/article`,
    evidenceId,
    new Date(now - w * 10 * 60_000).toISOString(),
    `watch-hash-${w}`,
    new Date(now - w * day).toISOString()
  );
}
db.exec('COMMIT');
console.log(`  WatchTargets: ${WATCH_COUNT} 个监控目标`);

// 创建 EvidenceVectors 表(空表,向量需运行时生成)
db.exec(`
  CREATE TABLE IF NOT EXISTS EvidenceVectors (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    EvidenceId  INTEGER NOT NULL,
    ModelId     INTEGER NOT NULL,
    Vector      BLOB    NOT NULL,
    CreatedAt   TEXT,
    UNIQUE (EvidenceId, ModelId)
  )
`);
console.log(`  EvidenceVectors: 已创建(空表)`);

console.log('\n===== 总计 =====');
const totalItems = db.prepare('SELECT COUNT(*) as n FROM Items').get();
const totalEvidence = db.prepare('SELECT COUNT(*) as n FROM Evidence').get();
const totalTags = db.prepare('SELECT COUNT(*) as n FROM Tags').get();
const totalGroups = db.prepare('SELECT COUNT(*) as n FROM Groups').get();
const totalWatch = db.prepare('SELECT COUNT(*) as n FROM WatchTargets').get();
console.log(`Items: ${totalItems.n} 篇`);
console.log(`Evidence: ${totalEvidence.n} 条`);
console.log(`Tags: ${totalTags.n} 个`);
console.log(`Groups: ${totalGroups.n} 个`);
console.log(`WatchTargets: ${totalWatch.n} 个`);

db.close();
