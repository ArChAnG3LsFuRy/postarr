'use strict';

const S = {
  view: 'movies',
  movies: [], shows: [], pending: [], activity: [],
  movieCollections: [], showCollections: [],
  settings: null,
  modal: null,
  selectMode: false,
  selected: new Set(),   // ids selected in the current grid (cleared on view change)
};

const el  = sel => document.querySelector(sel);
const els = sel => Array.from(document.querySelectorAll(sel));
const esc = str => { const d = document.createElement('div'); d.textContent = str ?? ''; return d.innerHTML; };

// ── Theme (logo-derived colours) ────────────────────────────────────────────────
// Two settings-driven colours recolour the whole UI (via the --accent/--coral tokens) AND the
// in-app logo, keeping the logo's shading. Defaults are the logo's own teal + coral.
const THEME_DEFAULT = { primary: '#10bec9', accent: '#f2586e' };
const _TEAL = [16, 190, 201], _CORAL = [242, 88, 110];               // retint gradient anchors
const _aA = _TEAL[0] / (_TEAL[0] + _TEAL[2]), _aB = _CORAL[0] / (_CORAL[0] + _CORAL[2]);
const _clampT = (v, a, b) => Math.max(a, Math.min(b, v));
function _hexRgb(h){ h = h.replace('#',''); return [parseInt(h.slice(0,2),16), parseInt(h.slice(2,4),16), parseInt(h.slice(4,6),16)]; }
function _rgbHsl(r,g,b){ r/=255;g/=255;b/=255; const mx=Math.max(r,g,b),mn=Math.min(r,g,b); let h,s,l=(mx+mn)/2;
  if(mx===mn){h=s=0;}else{const d=mx-mn;s=l>0.5?d/(2-mx-mn):d/(mx+mn);
    switch(mx){case r:h=(g-b)/d+(g<b?6:0);break;case g:h=(b-r)/d+2;break;default:h=(r-g)/d+4;}h/=6;} return [h,s,l]; }
function _hslRgb(h,s,l){ let r,g,b; if(s===0){r=g=b=l;} else { const q=l<0.5?l*(1+s):l+s-l*s,p=2*l-q,
    f=(p,q,t)=>{if(t<0)t+=1;if(t>1)t-=1;if(t<1/6)return p+(q-p)*6*t;if(t<1/2)return q;if(t<2/3)return p+(q-p)*(2/3-t)*6;return p;};
    r=f(p,q,h+1/3);g=f(p,q,h);b=f(p,q,h-1/3);} return [Math.round(r*255),Math.round(g*255),Math.round(b*255)]; }
const _hex = ([r,g,b]) => '#' + [r,g,b].map(x => Math.max(0,Math.min(255,x)).toString(16).padStart(2,'0')).join('');
// Button-text colour for a background: a dark tint on light colours, near-white on dark ones.
function _ink(hex){ const [r,g,b]=_hexRgb(hex); const L=(0.299*r+0.587*g+0.114*b)/255;
  if(L>0.5){ const [h,s]=_rgbHsl(r,g,b); return _hex(_hslRgb(h, Math.min(s,0.6), 0.1)); } return '#ffffff'; }

const LOGO_SRC = '/images/logo/postarr-logo.png?v=1.0.44';   // keep in step with index.html's logo ?v=
const _logoCache = {};
function retintLogo(primary, accent){
  const targets = els('.brand-logo, .theme-logo-preview');          // sidebar logo + settings preview
  if(!targets.length) return;
  const setAll = url => targets.forEach(t => t.src = url);
  if(primary.toLowerCase()===THEME_DEFAULT.primary && accent.toLowerCase()===THEME_DEFAULT.accent){
    setAll(LOGO_SRC); return;                                        // default → original artwork
  }
  const key = primary + accent;
  if(_logoCache[key]){ setAll(_logoCache[key]); return; }
  const A = _hexRgb(primary), B = _hexRgb(accent);
  const LbaseA = _rgbHsl(..._TEAL)[2], LbaseB = _rgbHsl(..._CORAL)[2];
  const img = new Image();
  img.onload = () => {
    const cv = document.createElement('canvas'); cv.width = img.naturalWidth; cv.height = img.naturalHeight;
    const ctx = cv.getContext('2d'); ctx.drawImage(img, 0, 0);
    let dat; try { dat = ctx.getImageData(0,0,cv.width,cv.height); } catch { return; }
    const d = dat.data;
    for(let i=0;i<d.length;i+=4){
      if(d[i+3]===0) continue;
      const r=d[i],g=d[i+1],b=d[i+2];
      if(_rgbHsl(r,g,b)[1] < 0.18) continue;                         // grey wordmark — leave neutral
      const rb=r+b; let t = rb>12 ? (r/rb-_aA)/(_aB-_aA) : 0.5; t=_clampT(t,0,1); t=_clampT((t-0.5)*2.4+0.5,0,1);
      const Lpx=_rgbHsl(r,g,b)[2], Lbase=LbaseA+(LbaseB-LbaseA)*t, shade=Lpx-Lbase;
      const bR=A[0]+(B[0]-A[0])*t, bG=A[1]+(B[1]-A[1])*t, bB=A[2]+(B[2]-A[2])*t;
      const [H,S,Lc]=_rgbHsl(bR,bG,bB);
      const [nr,ng,nb]=_hslRgb(H,S,_clampT(Lc+shade,0,1));
      d[i]=nr;d[i+1]=ng;d[i+2]=nb;
    }
    ctx.putImageData(dat,0,0);
    const url = cv.toDataURL('image/png'); _logoCache[key]=url; setAll(url);
  };
  img.src = LOGO_SRC;
}

function applyTheme(primary, accent){
  primary = primary || THEME_DEFAULT.primary;
  accent  = accent  || THEME_DEFAULT.accent;
  const root = document.documentElement.style;
  root.setProperty('--accent', primary);
  root.setProperty('--coral', accent);
  root.setProperty('--accent-text', _ink(primary));
  root.setProperty('--coral-text', _ink(accent));
  // r,g,b triplets so the card glows (which need rgba with alpha) track the theme too.
  root.setProperty('--accent-rgb', _hexRgb(primary).join(','));
  root.setProperty('--coral-rgb',  _hexRgb(accent).join(','));
  retintLogo(primary, accent);
}

// Route poster/background images through the server-side thumbnail proxy so grid cards load
// small, locally-cached JPEGs instead of full-res CDN originals (a ~180px card was pulling
// 1000–2000px images). Non-proxied hosts and any fetch/resize failure fall back to the
// original URL on the server side, so it's always safe to wrap a URL in this.
const thumbUrl = (u, w = 400) => u ? `/api/images/thumb?w=${w}&url=${encodeURIComponent(u)}` : u;

// Resolution badges are sized by HEIGHT (their source PNGs have differing aspect ratios, so
// width-based sizing made "4K UHD" taller than "1080P FHD"). This maps the badge-size % to a
// fraction of poster/canvas height in the settings-preview canvas. Mirrors --card-res-badge-height
// in CSS and the server renderer; tuned so variants render equal height at a legible size.
const RES_HEIGHT_FACTOR = 0.10;

async function api(path, opts = {}) {
  const r = await fetch(`/api${path}`, { headers: { 'Content-Type': 'application/json' }, ...opts });
  if (r.status === 401) { window.location.href = '/login'; return null; }
  if (!r.ok) {
    let detail = '';
    try { const j = await r.json(); detail = j.error || j.title || JSON.stringify(j); } catch { detail = await r.text().catch(() => ''); }
    throw new Error(`${opts.method || 'GET'} /api${path} → ${r.status}${detail ? ': ' + detail : ''}`);
  }
  const t = await r.text(); return t ? JSON.parse(t) : null;
}

function toast(msg, isErr = false) {
  const t = el('#toast');
  t.textContent = msg; t.className = 'toast' + (isErr ? ' error' : '');
  t.hidden = false; clearTimeout(t._t);
  t._t = setTimeout(() => t.hidden = true, 4000);
}

// ── Auth ──────────────────────────────────────────────────────────────────────
(async () => {
  try { const s = await api('/auth/status'); if (s?.authEnabled) el('#logout-btn').hidden = false; } catch {}
})();
el('#logout-btn').addEventListener('click', async () => { await api('/auth/logout', { method: 'POST' }); window.location.href = '/login'; });

// ── Apply saved badge size on load ─────────────────────────────────────────────
// Without this, poster cards use the CSS fallback until the user opens Settings and
// saves, since --card-badge-width is otherwise only set in saveSettings().
// The badge is sized to badgeSizePct% of the card — the SAME fraction the settings
// canvas preview and the server-side OverlayRenderer use — so the grid, the live
// preview, and the poster actually pushed to Plex all match.
(async () => {
  try {
    S.settings = await api('/settings');
    const pct = S.settings?.overlays?.badgeSizePct ?? 22;
    document.documentElement.style.setProperty('--card-badge-width', `${pct}%`);  // ribbons use this
    // Kometa-flavoured box: uniform HEIGHT (~6.6% at the default size of 22), radius, font —
    // all scaled by the badge-size slider. Width hugs the content (set per-badge).
    document.documentElement.style.setProperty('--card-box-height', `${(pct * 0.30).toFixed(2)}%`);
    document.documentElement.style.setProperty('--card-box-radius', `${(pct * 0.22).toFixed(1)}px`);
    document.documentElement.style.setProperty('--card-box-fontsize', `${(pct * 0.42).toFixed(1)}px`);
    // Fixed resolution-box width (fits the widest label, e.g. "1080P FHD") so variants left-align.
    document.documentElement.style.setProperty('--card-res-box-width', `${(pct * 1.52).toFixed(2)}%`);
  } catch {}
})();

// ── SignalR ───────────────────────────────────────────────────────────────────
const hub = (typeof signalR !== 'undefined') ? new signalR.HubConnectionBuilder().withUrl('/scanhub').withAutomaticReconnect().build() : null;

hub?.on('ScanStarted', ({ totalItems }) => {
  el('#scan-progress-wrap').hidden = false;
  el('#scan-progress-fill').style.width = '0%';
  el('#scan-progress-text').textContent = `0 / ${totalItems}`;
  el('#scan-status').textContent = 'Scanning…';
  el('#scan-btn').disabled = true;
  el('#live-dot').hidden = false;
});

hub?.on('ItemScanned', ({ processed, total, title }) => {
  const pct = total > 0 ? Math.round((processed / total) * 100) : 0;
  el('#scan-progress-fill').style.width = pct + '%';
  el('#scan-progress-text').textContent = `${processed} / ${total} — ${esc(title)}`;
});

hub?.on('ScanCompleted', ({ newItems, updatedItems, errors }) => {
  el('#scan-progress-fill').style.width = '100%';
  el('#scan-status').textContent = 'Idle';
  el('#scan-btn').disabled = false;
  el('#live-dot').hidden = true;
  setTimeout(() => { el('#scan-progress-wrap').hidden = true; }, 3000);
  toast(`Scan complete — ${newItems} new, ${updatedItems} updated${errors ? ', ' + errors + ' errors' : ''}.`);
  reloadCurrentView();
});

hub?.on('LogEntry', ({ level, message, timestamp }) => {
  S.activity.unshift({ level, message, timestampUtc: timestamp });
  if (S.activity.length > 500) S.activity.length = 500;
  if (S.view === 'activity') appendActivityRow({ level, message, timestampUtc: timestamp });
});

// The button's enabled state above is driven purely by transient SignalR events. SignalR does NOT
// replay events missed while disconnected, so a dropped connection during a scan loses the
// ScanCompleted event and the Scan button would stay disabled forever ("scan button not working").
// Reconcile against the server's real scan status on load, on reconnect, and on a slow timer so the
// UI always self-heals — this is the source of truth, the events are just the fast path.
async function syncScanState() {
  try {
    const s = await api('/library/scan/status');
    const scanning = !!s?.isScanning;
    el('#scan-btn').disabled = scanning;
    el('#live-dot').hidden = !scanning;
    if (scanning) {
      el('#scan-progress-wrap').hidden = false;
      if (!el('#scan-status').textContent || el('#scan-status').textContent === 'Idle')
        el('#scan-status').textContent = 'Scanning…';
    } else {
      el('#scan-status').textContent = 'Idle';
      el('#scan-progress-wrap').hidden = true;
    }
  } catch { /* offline / transient — try again next tick */ }
}

hub?.onreconnected(() => syncScanState());
hub?.start().catch(e => console.warn('SignalR:', e)).finally(syncScanState);
setInterval(syncScanState, 15000);

// ── Navigation ────────────────────────────────────────────────────────────────
const VIEW_TITLES = {
  movies: 'Movie Posters', 'movie-backgrounds': 'Movie Backgrounds', 'movie-collections': 'Movie Collections',
  shows: 'Show Posters', 'show-backgrounds': 'Show Backgrounds', 'show-collections': 'Show Collections',
  'auto-collections': 'Auto Collections', health: 'Health', activity: 'Activity', settings: 'Settings'
};

function setView(v) {
  if (S.selectMode) setSelectMode(false);   // selection is per-view; leaving cancels it
  S.view = v;
  if (document.body.classList.contains('nav-open')) setNavOpen(false); // close mobile drawer on navigate
  hideAzBar(); // grid renders re-show it; non-grid views (activity/settings) stay clean
  els('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.view === v));
  els('.view').forEach(s => { s.hidden = s.id !== `view-${v}`; });
  el('#view-title').textContent = VIEW_TITLES[v] || v;
  const searchable = Object.keys(VIEW_TITLES).filter(k => !['auto-collections','health','activity','settings'].includes(k));
  const isGrid = searchable.includes(v);
  el('#search-box').style.visibility = isGrid ? 'visible' : 'hidden';
  syncFilterUi(v, isGrid);
  if (v === 'movies')             renderMovies();
  if (v === 'movie-backgrounds')  renderBackgrounds('movie');
  if (v === 'movie-collections')  renderCollections('movie');
  if (v === 'shows')              renderShows();
  if (v === 'show-backgrounds')   renderBackgrounds('show');
  if (v === 'show-collections')   renderCollections('show');
  if (v === 'auto-collections')   renderAutoCollections();
  if (v === 'health')             renderHealth();
  if (v === 'activity')           renderActivity();
  if (v === 'settings')           renderSettings();
}

function reloadCurrentView() {
  S.movies = []; S.shows = [];
  setView(S.view);
}

// ── Grid filters ──────────────────────────────────────────────────────────────
const RECENT_LIMIT = 100;
// The filter dropdown composes with the title search: both just toggle card visibility, so they
// stack. Options adapt to the view — backgrounds filter on background state, collections have no
// ratings/resolution, etc.
const FILTER_OPTIONS = {
  poster: [
    ['all', 'All items'],
    ['recent-added', `Recently added (newest ${RECENT_LIMIT})`], ['recent-changed', `Recently changed (newest ${RECENT_LIMIT})`],
    ['no-art', 'No poster'], ['pending', 'Not applied yet'],
    ['no-ratings', 'Missing ratings'],
    ['res-4k', 'Resolution: 4K'], ['res-1080', 'Resolution: 1080p'],
    ['res-720', 'Resolution: 720p'], ['res-sd', 'Resolution: SD'],
  ],
  background: [['all', 'All items'], ['no-art', 'No background'], ['pending', 'Not applied yet']],
  collection: [['all', 'All collections'], ['no-art', 'No poster']],
};

// "Recently added / changed": the newest RECENT_LIMIT items, newest first. Shows count a newly arrived season as
// an addition and a season poster as a change. Returns id → position, or null for any other filter.
const _ts = v => (v ? Date.parse(v) || 0 : 0);
function recentRanks(items, f) {
  if (f !== 'recent-added' && f !== 'recent-changed') return null;
  const when = f === 'recent-added'
    ? i => Math.max(_ts(i.addedAtUtc), _ts(i.latestSeasonAddedUtc))
    : i => Math.max(_ts(i.lastPosterAppliedUtc), ...(i.seasons || []).map(s => _ts(s.lastPosterAppliedUtc)));
  return new Map(items.map(i => [i.id, when(i)]).filter(([, t]) => t > 0)
    .sort((a, b) => b[1] - a[1]).slice(0, RECENT_LIMIT).map(([id], n) => [id, n]));
}

function currentViewMeta() {
  switch (S.view) {
    case 'movies':            return { items: S.movies, kind: 'poster' };
    case 'shows':             return { items: S.shows,  kind: 'poster' };
    case 'movie-backgrounds': return { items: S.movies, kind: 'background' };
    case 'show-backgrounds':  return { items: S.shows,  kind: 'background' };
    case 'movie-collections': return { items: S.movieCollections, kind: 'collection' };
    case 'show-collections':  return { items: S.showCollections,  kind: 'collection' };
  }
  return { items: [], kind: null };
}

function matchesRes(res, target) {
  const r = (res || '').toLowerCase();
  if (target === '4k')   return r === '4k' || r === '2160' || r === 'uhd';
  if (target === '1080') return r.includes('1080');
  if (target === '720')  return r.includes('720');
  // SD = anything that isn't one of the HD tiers (covers sd/480/576 and blanks).
  if (target === 'sd')   return !['4k', '2160', 'uhd', '1080', '720'].some(x => r.includes(x));
  return false;
}

function passesFilter(item, kind, f) {
  if (!f || f === 'all') return true;
  const art     = kind === 'background' ? item.currentBackgroundUrl    : item.currentPosterUrl;
  const applied = kind === 'background' ? item.backgroundAppliedToPlex : item.posterAppliedToPlex;
  switch (f) {
    case 'no-art':     return !art;
    case 'pending':    return !!art && !applied;
    case 'no-ratings': return item.imdbRating == null && item.rottenTomatoesScore == null && item.audienceScore == null;
    case 'res-4k':     return matchesRes(item.videoResolution, '4k');
    case 'res-1080':   return matchesRes(item.videoResolution, '1080');
    case 'res-720':    return matchesRes(item.videoResolution, '720');
    case 'res-sd':     return matchesRes(item.videoResolution, 'sd');
  }
  return true;
}

// Rebuild the filter dropdown for the active view (and hide it on non-grid views). Resets to "All".
function syncFilterUi(view, isGrid) {
  const box = el('#filter-box');
  const selBtn = el('#select-mode-btn');
  const sizeCtl = el('#size-control');
  if (selBtn) selBtn.style.display = isGrid ? '' : 'none';
  if (sizeCtl) sizeCtl.style.display = isGrid ? '' : 'none';
  if (!box) return;
  if (!isGrid) { box.style.display = 'none'; return; }
  box.style.display = '';
  const { kind } = currentViewMeta();
  const opts = FILTER_OPTIONS[kind] || FILTER_OPTIONS.poster;
  box.innerHTML = opts.map(([v, l]) => `<option value="${v}">${l}</option>`).join('');
  box.value = 'all';
}

// Apply title-search + dropdown filter to the current grid by toggling card visibility.
function applyGridFilters() {
  const { items, kind } = currentViewMeta();
  if (!kind) return;
  const term = (el('#search-box')?.value || '').toLowerCase();
  const f    = el('#filter-box')?.value || 'all';
  const byId = new Map(items.map(i => [i.id, i]));
  const sel  = `#view-${S.view}`;
  const ranks = recentRanks(items, f);   // date-ordered views reorder the cards (grid `order`) instead of A–Z
  els(`${sel} .poster-card, ${sel} .bg-card`).forEach(card => {
    const id   = +(card.dataset.id ?? card.dataset.colId);
    const item = byId.get(id);
    const titleOk  = (card.querySelector('.item-title')?.textContent.toLowerCase() ?? '').includes(term);
    const filterOk = ranks ? ranks.has(id) : item ? passesFilter(item, kind, f) : true;
    card.style.display = (titleOk && filterOk) ? '' : 'none';
    card.style.order   = ranks && ranks.has(id) ? String(ranks.get(id)) : '';
  });
  // Letter jumps make no sense in date order; bring the A–Z bar back for the other filters.
  const viewEl = el(sel);
  if (ranks) { if (_azView === viewEl) hideAzBar(); }
  else if (viewEl) buildAzBar(viewEl);
}

// ── Multi-select bulk actions ─────────────────────────────────────────────────
function setSelectMode(on) {
  S.selectMode = on;
  S.selected.clear();
  document.body.classList.toggle('select-mode', on);
  const btn = el('#select-mode-btn');
  if (btn) { btn.classList.toggle('active', on); btn.textContent = on ? 'Cancel' : 'Select'; }
  els(`#view-${S.view} .poster-card.selected, #view-${S.view} .bg-card.selected`)
    .forEach(c => c.classList.remove('selected'));
  updateSelectionBar();
}

function toggleCardSelected(card) {
  const id = +(card.dataset.id ?? card.dataset.colId);
  if (!id) return;
  if (S.selected.has(id)) { S.selected.delete(id); card.classList.remove('selected'); }
  else                    { S.selected.add(id);    card.classList.add('selected'); }
  updateSelectionBar();
}

function updateSelectionBar() {
  const bar = el('#selection-bar');
  if (!bar) return;
  const n = S.selected.size;
  bar.hidden = !S.selectMode;
  el('#selection-count').textContent = `${n} selected`;
  const busy = bar.dataset.busy === '1';
  el('#sel-apply').disabled   = busy || n === 0;
  el('#sel-dismiss').disabled = busy || n === 0;
  el('#sel-clear').disabled   = busy || n === 0;
}

// Runs apply/dismiss over every selected card, a few at a time (same pattern as Apply All).
async function runBulk(action) {
  const meta = currentViewMeta();
  if (!meta.kind || S.selected.size === 0) return;
  const byId = new Map(meta.items.map(i => [i.id, i]));
  const ids  = [...S.selected];
  const bar  = el('#selection-bar');
  bar.dataset.busy = '1'; updateSelectionBar();

  const jobFor = id => {
    const it = byId.get(id);
    if (meta.kind === 'collection') {
      return action === 'dismiss'
        ? { path: `/collections/dismiss/${id}` }
        : { path: `/collections/apply/${id}`,
            body: { imageUrl: it?.currentPosterUrl, source: SOURCE_MAP[it?.currentPosterSource] ?? 0 } };
    }
    const base = meta.kind === 'background' ? 'backgrounds' : 'posters';
    return action === 'dismiss'
      ? { path: `/${base}/dismiss/item/${id}` }
      : { path: `/${base}/apply-current/item/${id}?force=true` };
  };

  let done = 0, ok = 0, failed = 0; const failures = [];
  const verb = action === 'apply' ? 'Applying' : 'Dismissing';
  const tick = () => el('#selection-count').textContent = `${verb} ${done}/${ids.length}…`;
  tick();

  let next = 0;
  async function worker() {
    while (next < ids.length) {
      const job = jobFor(ids[next++]);
      try {
        await api(job.path, { method: 'POST', ...(job.body ? { body: JSON.stringify(job.body) } : {}) });
        ok++;
      } catch (e) { failed++; if (failures.length < 3) failures.push(e.message); }
      done++;
      if (done % 3 === 0 || done === ids.length) tick();
    }
  }
  await Promise.all(Array.from({ length: 3 }, worker));

  bar.dataset.busy = '0';
  toast(failed ? `${ok} ${action === 'apply' ? 'applied' : 'dismissed'}, ${failed} failed. ${failures[0] || ''}`
               : `${ok} ${action === 'apply' ? `applied to ${srvName()}` : 'dismissed'}.`, !!failed);
  setSelectMode(false);
  setView(S.view);   // re-fetch + re-render the grid to reflect the changes
}

const OVERLAY_TICKS = [
  ['resolutionEnabled',      'Resolution',        '4K-HDR · 1080P · 720P (Kometa-style PNG badges)'],
  ['dynamicRangeEnabled',    'Dynamic Range',     'HDR · DV · HDR10+ (PNG badge)'],
  ['audioCodecEnabled',      'Audio Codec',       'TrueHD · DTS · Dolby (PNG badge images)'],
  ['contentRatingEnabled',   'Content Rating',    'US rating PNG badges (G, PG, PG-13, R, TV-MA…)'],
  ['editionEnabled',         'Edition',           "Director's Cut · Extended · IMAX · Criterion (PNG)"],
  ['languageEnabled',        'Language',          'Flag badge by ISO language code (PNG)'],
  ['imdbRatingEnabled',      'IMDb Rating',       'IMDb logo + score'],
  ['rottenTomatoesEnabled',  'Rotten Tomatoes',   'Critics score — icon + % or Certified Fresh ribbon (see style below)'],
  ['audienceScoreEnabled',   'Audience Score',    'Audience score — icon + % or Verified Hot ribbon (see style below)'],
  ['oscarWinnerEnabled',     'Oscar Winner',      'Ribbon badge — Academy Award winner'],
  ['oscarNomineeEnabled',    'Oscar Nominee',     'Text badge — Academy Award nominee'],
  ['emmyWinnerEnabled',      'Emmy Winner',       'Ribbon badge — Emmy Award winner'],
  ['streamingServiceEnabled','Streaming Service', 'Netflix · Disney+ · Max · Prime (PNG logo)'],
  ['networkEnabled',         'Network',           'TV network logo (PNG)'],
  ['studioEnabled',          'Studio',            'Production studio logo (PNG)'],
  ['showStatusEnabled',      'Show Status',       'Returning · Ended · Cancelled (text badge)'],
  ['episodeCountEnabled',    'Episode Count',     'Total episode count (text badge)'],
  ['trendingEnabled',        'Trending',          '🔥 Currently trending on TMDB'],
  ['popularEnabled',         'Popular',           '⭐ Popular on TMDB'],
  ['metacriticEnabled',      'Metacritic',        'Metacritic logo + score'],
  ['newBadgeEnabled',        'New / New Season',  'Recently added movie or show, or a new season — disappears after the days set below'],
  ['videoSourceEnabled',     'Video Source',      'REMUX · BLU-RAY · WEB · HDTV · DVD (read from the file name)'],
  ['runtimeEnabled',         'Runtime',           'Movie length, e.g. 2h 12m (text badge)'],
  ['versionsEnabled',        'Multiple Versions', 'When a movie has more than one version (PNG)'],
  ['audioLanguagesEnabled',  'Dual / Multi Audio','2 or 3+ audio languages (PNG)'],
  ['subtitleLanguagesEnabled','Dual / Multi Subtitles','2 or 3+ subtitle languages (PNG)'],
  ['letterboxdEnabled',      'Letterboxd',        'Letterboxd logo + average out of 5 (needs MDBList key)'],
  ['traktEnabled',           'Trakt',             'Trakt logo + rating % (needs MDBList key)'],
  ['imdbTop250Enabled',      'IMDb Top 250',      'Corner ribbon for films in IMDb\'s Top 250 chart (needs MDBList key)'],
];

const BADGE_KEYS = [
  'resolutionEnabled',     'dynamicRangeEnabled',   'audioCodecEnabled',
  'contentRatingEnabled',  'editionEnabled',         'languageEnabled',
  'imdbRatingEnabled',     'rottenTomatoesEnabled',  'audienceScoreEnabled',
  'oscarWinnerEnabled',    'oscarNomineeEnabled',    'emmyWinnerEnabled',
  'streamingServiceEnabled','networkEnabled',         'studioEnabled',
  'showStatusEnabled',     'episodeCountEnabled',    'trendingEnabled',     'popularEnabled',
  'metacriticEnabled',     'newBadgeEnabled',        'videoSourceEnabled',  'runtimeEnabled',
  'versionsEnabled',       'audioLanguagesEnabled',  'subtitleLanguagesEnabled',
  'letterboxdEnabled',     'traktEnabled',           'imdbTop250Enabled'
];

// Map badge key → model position field name (camelCase to match JSON)
const POS_FIELD = {
  resolutionEnabled:      'resolutionPos',     dynamicRangeEnabled:    'dynamicRangePos',
  audioCodecEnabled:      'audioCodecPos',     contentRatingEnabled:   'contentRatingPos',
  editionEnabled:         'editionPos',        languageEnabled:        'languagePos',
  imdbRatingEnabled:      'imdbRatingPos',     rottenTomatoesEnabled:  'rottenTomatoesPos',
  audienceScoreEnabled:   'audienceScorePos',  oscarWinnerEnabled:     'oscarWinnerPos',
  oscarNomineeEnabled:    'oscarNomineePos',   emmyWinnerEnabled:      'emmyWinnerPos',
  streamingServiceEnabled:'streamingServicePos',networkEnabled:        'networkPos',
  studioEnabled:          'studioPos',         showStatusEnabled:      'showStatusPos',
  episodeCountEnabled:    'episodeCountPos',   trendingEnabled:        'trendingPos',
  popularEnabled:         'popularPos',
  metacriticEnabled:      'metacriticPos',     newBadgeEnabled:        'newBadgePos',
  videoSourceEnabled:     'videoSourcePos',    runtimeEnabled:         'runtimePos',
  versionsEnabled:        'versionsPos',       audioLanguagesEnabled:  'audioLanguagesPos',
  subtitleLanguagesEnabled:'subtitleLanguagesPos',
  letterboxdEnabled:      'letterboxdPos',     traktEnabled:           'traktPos',
  imdbTop250Enabled:      'imdbTop250Pos',
};

// Default positions (normalised 0–1, mirroring C# defaults)
// Kometa default positions (normalised 0–1 fractions of poster size)
// Based on Kometa source: resolution=bottom-left, audio=top-center,
// streaming=bottom-left, ratings=bottom-center, awards=top-right etc.
// Kometa default positions (normalised 0–1 fractions of poster size).
// Researched from Kometa's actual overlay YAML defaults on GitHub:
//   resolution=bottom-left, audio_codec=top-center, streaming=bottom-left,
//   network=bottom-left, ratings=bottom-center, ribbon(awards)=bottom-right,
//   status=top-left, episode_info=bottom-right
// Badges sharing a corner are pre-spaced vertically so nothing overlaps by
// default, while still being independently draggable afterwards.
// Kometa default layout (centre fractions), spaced for the wide 30.5%-boxes so they don't overlap.
// Left column ≈0.17, centre 0.5, right ≈0.83; rows ~8% apart (box is 7% tall).
const DEFAULT_POSITIONS = {
  resolutionPos:       { x: 0.04, y: 0.05 },  // top-left; x is the LEFT EDGE (left-anchored)
  dynamicRangePos:     { x: 0.17, y: 0.13 },  // top-left, stacked
  audioCodecPos:       { x: 0.50, y: 0.05 },  // top-centre (Kometa)
  contentRatingPos:    { x: 0.83, y: 0.05 },  // top-right
  editionPos:          { x: 0.50, y: 0.13 },  // top-centre, stacked
  languagePos:         { x: 0.83, y: 0.13 },  // top-right, stacked
  // Ratings stack down the left side (Kometa's default) — a single bottom row can't fit six score boxes.
  imdbRatingPos:       { x: 0.17, y: 0.30 },
  rottenTomatoesPos:   { x: 0.17, y: 0.38 },
  audienceScorePos:    { x: 0.17, y: 0.46 },
  oscarWinnerPos:      { x: 0.85, y: 0.86 },  // bottom-right sash (ribbon)
  oscarNomineePos:     { x: 0.83, y: 0.78 },  // bottom-right, stacked
  emmyWinnerPos:       { x: 0.85, y: 0.86 },  // bottom-right sash (ribbon)
  streamingServicePos: { x: 0.17, y: 0.94 },  // bottom-left
  networkPos:          { x: 0.17, y: 0.86 },  // bottom-left, stacked
  studioPos:           { x: 0.17, y: 0.78 },  // bottom-left, stacked
  showStatusPos:       { x: 0.17, y: 0.05 },  // top-left (Kometa status)
  episodeCountPos:     { x: 0.83, y: 0.94 },  // bottom-right (Kometa episode_info)
  trendingPos:         { x: 0.50, y: 0.86 },  // bottom-centre, stacked
  popularPos:          { x: 0.50, y: 0.78 },  // bottom-centre, stacked
  metacriticPos:       { x: 0.17, y: 0.54 },
  newBadgePos:         { x: 0.50, y: 0.21 },  // top-centre, third row
  videoSourcePos:      { x: 0.83, y: 0.21 },  // top-right, third row
  runtimePos:          { x: 0.17, y: 0.21 },  // top-left, third row
  versionsPos:         { x: 0.83, y: 0.29 },  // right column, stacked
  audioLanguagesPos:   { x: 0.83, y: 0.37 },
  subtitleLanguagesPos:{ x: 0.83, y: 0.45 },
  letterboxdPos:       { x: 0.17, y: 0.62 },
  traktPos:            { x: 0.17, y: 0.70 },
  imdbTop250Pos:       { x: 0.85, y: 0.86 },  // bottom-right sash, same corner as the award ribbons
};

let _previewPositions = JSON.parse(JSON.stringify(DEFAULT_POSITIONS));
let _dragState = null; // { posKey, offNx, offNy }
let _selectedBadge = null; // posField of the badge selected for arrow-key nudging
const _badgeImgCache = {};
els('.nav-item').forEach(b => b.addEventListener('click', () => setView(b.dataset.view)));

// ── Mobile nav drawer ───────────────────────────────────────────────────────
function setNavOpen(open) {
  document.body.classList.toggle('nav-open', open);
  el('#nav-scrim').hidden = !open;
  const t = el('#nav-toggle');
  if (t) t.setAttribute('aria-expanded', open ? 'true' : 'false');
}
el('#nav-toggle')?.addEventListener('click', () => setNavOpen(!document.body.classList.contains('nav-open')));
el('#nav-scrim')?.addEventListener('click', () => setNavOpen(false));
document.addEventListener('keydown', e => { if (e.key === 'Escape') setNavOpen(false); });
// Growing past the mobile breakpoint (rotate/resize) must drop the drawer state.
window.addEventListener('resize', () => { if (window.innerWidth > 820) setNavOpen(false); });

// ── A–Z jump index ────────────────────────────────────────────────────────────
const AZ_LETTERS = ['#','A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z'];
let _azView = null, _azFirst = {}, _azTick = false;

// Per-view scroll memory: each grid remembers where you were so navigating away and back
// (or a re-render after a scan/action) restores your position instead of jumping to the top.
// NB: read the remembered position BEFORE rebuilding innerHTML — replacing content fires a
// spurious scroll→0 event that would otherwise clobber the saved value.
const _scrollMem = {};

// Ignore leading articles the way media servers do ("The Boys" → sorts under B).
const azStrip = t => (t || '').trim().replace(/^(the|a|an)\s+/i, '');
const azKey   = t => { const c = azStrip(t).charAt(0).toUpperCase(); return (c >= 'A' && c <= 'Z') ? c : '#'; };
const sortByTitle = arr => [...arr].sort((a, b) => azStrip(a.title).toLowerCase().localeCompare(azStrip(b.title).toLowerCase()));

function setAzActive(L) { els('#az-bar button').forEach(b => b.classList.toggle('active', b.dataset.az === L)); }

// Highlight the letter of whichever card currently sits at the top of the scroll area.
function updateAzActive() {
  if (!_azView || el('#az-bar').hidden) return;
  const cards = _azView.querySelectorAll('[data-az-letter]');
  const vTop = _azView.getBoundingClientRect().top;
  let current = null;
  for (const c of cards) {
    if (c.style.display === 'none') continue;
    if (c.getBoundingClientRect().top - vTop <= 12) current = c.dataset.azLetter; else break;
  }
  setAzActive(current || (cards[0] && cards[0].dataset.azLetter));
}

// Build the letter strip for a freshly-rendered grid. Letters with no (visible)
// item are disabled. Hidden for tiny/empty grids.
function buildAzBar(viewEl) {
  // A grid that finishes loading after the user has moved to another page mustn't bring the bar back.
  if (viewEl.id !== `view-${S.view}`) return;
  const bar = el('#az-bar');
  const cards = [...viewEl.querySelectorAll('.poster-card, .bg-card')];
  _azFirst = {};
  const present = new Set();
  cards.forEach(card => {
    const k = azKey(card.querySelector('.item-title')?.textContent);
    card.dataset.azLetter = k;
    if (card.style.display === 'none') return;
    if (!(k in _azFirst)) _azFirst[k] = card;
    present.add(k);
  });
  if (present.size < 2) { hideAzBar(); return; }
  bar.innerHTML = AZ_LETTERS.map(L => `<button type="button" data-az="${L}"${present.has(L) ? '' : ' disabled'}>${L}</button>`).join('');
  bar.hidden = false;
  el('.content').classList.add('az-on');
  _azView = viewEl;
  updateAzActive();
}

function hideAzBar() {
  el('#az-bar').hidden = true;
  el('.content').classList.remove('az-on');
  _azView = null;
}

el('#az-bar').addEventListener('click', e => {
  const b = e.target.closest('button[data-az]');
  if (!b || b.disabled || !_azView) return;
  const card = _azFirst[b.dataset.az];
  if (!card) return;
  const top = card.getBoundingClientRect().top - _azView.getBoundingClientRect().top + _azView.scrollTop - 6;
  _azView.scrollTo({ top, behavior: 'smooth' });
  setAzActive(b.dataset.az);
});

// Scroll-spy + scroll memory: remember each grid's position, and update the highlighted letter.
els('.view').forEach(v => v.addEventListener('scroll', () => {
  _scrollMem[v.id] = v.scrollTop;
  if (v !== _azView || _azTick) return;
  _azTick = true;
  requestAnimationFrame(() => { _azTick = false; updateAzActive(); });
}));

// ── Helpers ───────────────────────────────────────────────────────────────────
function kometaRes(res, dynRange) {
  const r  = (res || '').toLowerCase();
  const dr = (dynRange || '').toUpperCase();
  const is4k = r === '4k' || r === '2160' || r === 'uhd';
  const is1080 = r === '1080', is720 = r === '720';
  const isDV = dr.includes('DV'), isHDR = dr.includes('HDR');
  if (is4k   && isDV && isHDR) return '4K-DV-HDR';
  if (is4k   && isDV)          return '4K-DV';
  if (is4k   && isHDR)         return '4K-HDR';
  if (is4k)                    return '4K';
  if (is1080 && isDV)          return '1080P-DV';
  if (is1080 && isHDR)         return '1080P-HDR';
  if (is1080)                  return '1080P';
  if (is720  && isHDR)         return '720P-HDR';
  if (is720)                   return '720P';
  return (res || '').toUpperCase();
}

// ── Badge image/text lookups — mirrors Overlays/OverlayRenderer.cs exactly so the
// grid preview matches what actually gets baked into the poster on Apply. ─────────
// Mirrors OverlayRenderer.ProviderCandidates — Plex's provider names don't match our asset
// filenames ("Amazon Prime Video" vs Prime Video.png, "Disney Plus" vs Disney+.png) and carry
// reseller/tier suffixes ("HBO Max Amazon Channel", "Peacock Premium"). Returns candidates
// best-first; the <img> onerror walks them so a logo wins over falling back to text.
function providerCandidates(raw) {
  let n = String(raw || '').trim()
    .replace(/\s+(amazon|apple\s*tv|roku|google\s*play)\s+channel\s*$/i, '')
    .replace(/\s+(premium|essential|basic|standard|with\s+ads)\s*$/i, '')
    .trim();
  if (!n) return [];
  const out = [n];
  const plus = n.replace(/\s*plus\b/i, '+').trim();
  if (plus.toLowerCase() !== n.toLowerCase()) out.push(plus);
  out.push(n + '+', n.replace(/\s+/g, ''));
  const key = n.toLowerCase();
  if (key.includes('prime video') || key.startsWith('amazon')) out.push('Prime Video', 'Amazon');
  if (key.startsWith('disney'))    out.push('Disney+', 'Disney');
  if (key.startsWith('apple'))     out.push('Apple TV+', 'AppleTV');
  if (key.startsWith('hbo') || key.includes('max')) out.push('Max', 'HBO');
  if (key.startsWith('paramount')) out.push('Paramount+');
  if (key.startsWith('peacock'))   out.push('Peacock');
  if (key.startsWith('discovery')) out.push('discovery+');
  const first = n.split(' ')[0];
  if (first.toLowerCase() !== key) out.push(first);
  return [...new Set(out)];
}

// Strict brand key (mirrors OverlayRenderer.ServiceKey): "Disney Plus" = "Disney+", but
// "Disney Channel" ≠ "Disney+". Used to avoid drawing the same network + streaming logo twice.
function serviceKey(raw) {
  const n = (providerCandidates(raw)[0] || String(raw || ''))
    .replace(/\s*\bplus\b/i, '+').toLowerCase().replace(/[^a-z0-9+]/g, '');
  if (n === 'appletv+') return 'appletv';   // Apple TV+ was renamed Apple TV; TMDB has both spellings
  if (['amazon', 'amazonprime', 'amazonprimevideo'].includes(n)) return 'primevideo';
  return n;
}

// Full image paths for every candidate in a folder.
function providerSrcs(folder, raw) {
  return providerCandidates(raw).map(c => `/images/${folder}/${encodeURIComponent(c)}.png`);
}

function dynamicRangeKey(dr) {
  const u = (dr || '').toUpperCase();
  return u.includes('DV') && u.includes('HDR') ? 'dvhdr' : u.includes('DV') ? 'dv' : 'hdr';
}

function audioCodecFile(codec) {
  const c = (codec || '').toLowerCase();
  if (c.includes('truehd') && c.includes('atmos')) return 'truehd_atmos.png';
  if (c.includes('truehd'))                        return 'truehd.png';
  if (c.includes('atmos'))                          return 'dolby_atmos.png';
  if (c === 'eac3' || c === 'e-ac3' || c === 'dd+') return 'plus.png';
  if (c === 'ac3'  || c === 'dd')                   return 'digital.png';
  if (c.includes('dtsx'))                           return 'dtsx.png';
  if (c.includes('dtses') || c.includes('dts-es'))  return 'dtses.png';
  if (c.includes('dts') && c.includes('ma'))        return 'ma.png';
  if (c.includes('dts') && c.includes('hra'))       return 'hra.png';
  if (c.includes('dts'))                            return 'dts.png';
  if (c === 'aac')  return 'aac.png';
  if (c === 'flac') return 'flac.png';
  if (c === 'mp3')  return 'mp3.png';
  if (c === 'opus') return 'opus.png';
  if (c === 'pcm' || c === 'lpcm') return 'pcm.png';
  return null;
}

// USA content ratings — movie (MPAA) and TV (US TV Parental Guidelines).
const CONTENT_RATING_FILES = {
  // Movie (MPAA)
  'g':'usg.png', 'pg':'uspg.png', 'pg-13':'uspg-13.png', 'pg13':'uspg-13.png',
  'r':'usr.png', 'nc-17':'usnc-17.png', 'nc17':'usnc-17.png',
  'nr':'usnr.png', 'unrated':'usnr.png', 'notrated':'usnr.png',
  // TV (US TV Parental Guidelines) — TV-Y7 maps to the TV-Y badge (no dedicated Y7 image)
  'tv-y':'ustv-y.png', 'tvy':'ustv-y.png', 'tv-y7':'ustv-y.png', 'tvy7':'ustv-y.png',
  'tv-g':'ustv-g.png', 'tvg':'ustv-g.png',
  'tv-pg':'ustv-pg.png', 'tvpg':'ustv-pg.png',
  'tv-14':'ustv-14.png', 'tv14':'ustv-14.png',
  'tv-ma':'ustv-ma.png', 'tvma':'ustv-ma.png',
};
function contentRatingFile(rating) {
  const key = (rating || '').toLowerCase().trim().replace(/\s+/g, '').replace(/^us\//, '');
  return CONTENT_RATING_FILES[key] || null;
}

function editionFile(edition) {
  const e = (edition || '').toLowerCase().replace(/\s+/g, '').replace(/'/g, '');
  if (e.includes('director'))    return 'directors.png';
  if (e.includes('extended'))    return 'extended.png';
  if (e.includes('unrated'))     return 'unrated.png';
  if (e.includes('theatrical'))  return 'theatrical.png';
  if (e.includes('imax'))        return 'imax.png';
  if (e.includes('ultimate'))    return 'ultimate.png';
  if (e.includes('criterion'))   return 'criterion.png';
  if (e.includes('collector'))   return 'collector.png';
  if (e.includes('remaster'))    return 'remastered.png';
  if (e.includes('definitive'))  return 'definitive.png';
  if (e.includes('special'))     return 'special.png';
  if (e.includes('anniversary')) return 'anniversary.png';
  if (e.includes('alternate'))   return 'alternate.png';
  if (e.includes('final'))       return 'final.png';
  if (e.includes('diamond'))     return 'diamond.png';
  if (e.includes('platinum'))    return 'platinum.png';
  return null;
}

function streamingColour(service) {
  const s = (service || '').toLowerCase();
  if (s.includes('netflix')) return 'rgba(229,9,20,0.86)';
  if (s.includes('disney'))  return 'rgba(17,60,166,0.86)';
  if (s.includes('hbo'))     return 'rgba(90,30,140,0.86)';
  if (s.includes('apple'))   return 'rgba(50,50,50,0.86)';
  if (s.includes('prime'))   return 'rgba(0,168,225,0.86)';
  if (s.includes('hulu'))    return 'rgba(28,231,131,0.86)';
  return 'rgba(20,130,130,0.86)';
}

function statusBadge(status) {
  const s = (status || '').toLowerCase();
  if (s.includes('return'))     return ['Returning', 'rgba(30,100,55,0.86)'];
  if (s.includes('ended'))      return ['Ended', 'rgba(20,20,20,0.86)'];
  if (s.includes('cancel'))     return ['Cancelled', 'rgba(185,30,30,0.86)'];
  if (s.includes('production')) return ['In Production', 'rgba(25,75,155,0.86)'];
  return [status, 'rgba(20,20,20,0.86)'];
}

function resTextColour(res, dr) {
  const r = (res || '').toLowerCase(), u = (dr || '').toUpperCase();
  const is4k = r === '4k' || r === '2160' || r === 'uhd';
  if (is4k)             return 'rgba(30,100,55,0.86)';
  if (u.includes('DV')) return 'rgba(110,35,155,0.86)';
  if (u.includes('HDR'))return 'rgba(25,75,155,0.86)';
  return 'rgba(20,20,20,0.86)';
}

// ── Compute the full set of enabled badges for one real item ──────────────────
// Mirrors OverlayRenderer.BuildBadges() so the grid matches what gets applied to Plex.
function itemBadges(item, ov) {
  if (!ov) return [];
  const badges = [];
  const pos = key => ov[POS_FIELD[key]] || DEFAULT_POSITIONS[POS_FIELD[key]];

  if (ov.resolutionEnabled && item.videoResolution) {
    const resKey = buildPreviewResKey(item.videoResolution, item.videoDynamicRange || '');
    const p = pos('resolutionEnabled');
    badges.push({ x: p.x, y: p.y, hasPill: true, imgSrc: `/images/resolution/${resKey}.png`,
      fallbackText: kometaRes(item.videoResolution, item.videoDynamicRange) });
  }

  // Shown whenever enabled — it used to be suppressed unless the resolution badge was off,
  // so turning it on appeared to do nothing.
  if (ov.dynamicRangeEnabled && item.videoDynamicRange
      && item.videoDynamicRange.toUpperCase() !== 'SDR') {
    const key = dynamicRangeKey(item.videoDynamicRange);
    const p = pos('dynamicRangeEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/resolution/${key}.png`,
      fallbackText: item.videoDynamicRange.toUpperCase(), bg: 'rgba(110,35,155,0.86)' });
  }

  if (ov.audioCodecEnabled && item.audioCodec) {
    const file = audioCodecFile(item.audioCodec);
    const folder = ov.audioCodecStyle === 'compact' ? 'compact' : 'standard';
    const p = pos('audioCodecEnabled');
    badges.push({ x: p.x, y: p.y,
      imgSrc: file ? `/images/audio_codec/${folder}/${file}` : null,
      fallbackText: item.audioCodec.toUpperCase(), bg: 'rgba(25,75,155,0.86)' });
  }

  if (ov.contentRatingEnabled && item.contentRating) {
    const file = contentRatingFile(item.contentRating);
    const p = pos('contentRatingEnabled');
    // The image set only covers AU movie ratings; TV ratings (TV-MA, TV-14, …) and any other
    // unmapped value render as a text pill so the rating still shows (mirrors OverlayRenderer).
    if (file) badges.push({ x: p.x, y: p.y, imgSrc: `/images/cr/${file}` });
    else      badges.push({ x: p.x, y: p.y, textOnly: true, text: item.contentRating, bg: 'rgba(20,20,20,0.86)' });
  }

  if (ov.editionEnabled && item.edition) {
    const file = editionFile(item.edition);
    const p = pos('editionEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: file ? `/images/edition/${file}` : null,
      fallbackText: item.edition, bg: 'rgba(20,20,20,0.86)' });
  }

  if (ov.streamingServiceEnabled && item.streamingService) {
    const folder = ov.streamingStyle === 'white' ? 'white' : 'color';
    const srcs = providerSrcs(`streaming/${folder}`, item.streamingService);
    const p = pos('streamingServiceEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: srcs[0], altSrcs: srcs.slice(1),
      fallbackText: item.streamingService, bg: streamingColour(item.streamingService) });
  }

  // Skip the network badge when the streaming badge already shows the same service (streaming originals).
  const sameAsStreaming = ov.streamingServiceEnabled && item.streamingService
    && serviceKey(item.streamingService) === serviceKey(item.network);
  if (ov.networkEnabled && item.network && !sameAsStreaming) {
    const folder = ov.networkStyle === 'white' ? 'white' : 'color';
    const srcs = providerSrcs(`network/${folder}`, item.network);
    const p = pos('networkEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: srcs[0], altSrcs: srcs.slice(1),
      fallbackText: item.network, bg: 'rgba(20,20,20,0.86)' });
  }

  if (ov.studioEnabled && item.studio) {
    const folder = ov.studioStyle === 'bigger' ? 'bigger' : 'standard';
    const srcs = providerSrcs(`studio/${folder}`, item.studio);
    const p = pos('studioEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: srcs[0], altSrcs: srcs.slice(1),
      fallbackText: item.studio, bg: 'rgba(20,20,20,0.86)' });
  }

  const ribbonFolder = ['gray','red','yellow'].includes(ov.ribbonColour) ? ov.ribbonColour : 'black';
  if (ov.oscarWinnerEnabled && item.isOscarWinner) {
    const p = pos('oscarWinnerEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/ribbon/${ribbonFolder}/oscars.png`, isRibbon: true });
  } else if (ov.oscarNomineeEnabled && item.isOscarNominee) {
    const p = pos('oscarNomineeEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: 'Oscar Nominee', bg: 'rgba(175,130,20,0.86)' });
  }

  if (ov.emmyWinnerEnabled && item.isEmmyWinner) {
    const p = pos('emmyWinnerEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/ribbon/${ribbonFolder}/emmys.png`, isRibbon: true });
  }

  if (ov.imdbTop250Enabled && item.imdbTop250Rank != null) {
    const p = pos('imdbTop250Enabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/ribbon/${ribbonFolder}/imdb.png`, isRibbon: true });
  }

  if (ov.imdbRatingEnabled && item.imdbRating != null) {
    const p = pos('imdbRatingEnabled');
    const rating = item.imdbRating.toFixed(1);
    badges.push({ x: p.x, y: p.y, imgSrc: '/images/rating/IMDb.png', subtext: rating,
      fallbackText: `IMDb ${rating}`, bg: 'rgba(175,130,20,0.86)' });
  }

  if (ov.rottenTomatoesEnabled && item.rottenTomatoesScore != null) {
    const fresh = item.rottenTomatoesScore >= 60;
    const p = pos('rottenTomatoesEnabled');
    if (ov.rottenTomatoesStyle === 'ribbon') {
      // Mirrors OverlayRenderer: the Certified Fresh ribbon carries no score, so a Rotten title
      // gets no ribbon at all. Keeps the card preview honest about what lands on the poster.
      if (fresh) {
        // Same corner-pin rule as the renderer: the sash only reads right in the bottom-right
        // corner, so an untouched (factory rating-row) position is placed there.
        const atDefault = isFactoryPos(p, [[0.17, 0.38], [0.42, 0.95], [0.44, 0.94]]);
        const rx = atDefault ? 0.92 : p.x, ry = atDefault ? 0.95 : p.y;
        badges.push({ x: rx, y: ry, imgSrc: `/images/ribbon/${ribbonFolder}/rotten.png`, isRibbon: true });
      }
    } else {
      // Critics icon + score, matching the settings label and the Audience badge.
      badges.push({ x: p.x, y: p.y, imgSrc: `/images/rating/${fresh ? 'RT-Crit-Fresh' : 'RT-Crit-Rotten'}.png`,
        subtext: `${item.rottenTomatoesScore}%`, fallbackText: `${item.rottenTomatoesScore}%`, bg: 'rgba(200,50,20,0.86)' });
    }
  }

  if (ov.audienceScoreEnabled && item.audienceScore != null) {
    const fresh = item.audienceScore >= 60;
    const p = pos('audienceScoreEnabled');
    if (ov.audienceScoreStyle === 'ribbon') {
      // Mirrors OverlayRenderer: "Verified Hot" seal, fresh only, corner-pinned at the default pos.
      if (fresh) {
        const atDefault = isFactoryPos(p, [[0.17, 0.46], [0.52, 0.95], [0.58, 0.94]]);
        const rx = atDefault ? 0.92 : p.x, ry = atDefault ? 0.95 : p.y;
        badges.push({ x: rx, y: ry, imgSrc: `/images/ribbon/${ribbonFolder}/rottenverified.png`, isRibbon: true });
      }
    } else {
      badges.push({ x: p.x, y: p.y, imgSrc: `/images/rating/${fresh ? 'RT-Aud-Fresh' : 'RT-Aud-Rotten'}.png`,
        subtext: `${item.audienceScore}%`, fallbackText: `${item.audienceScore}%`, bg: 'rgba(200,90,20,0.86)' });
    }
  }

  if (ov.languageEnabled && item.contentLanguage) {
    const folder = ov.flagStyle === 'square' ? 'square' : 'round';
    const p = pos('languageEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/flag/${folder}/${item.contentLanguage.toLowerCase()}.png`,
      fallbackText: item.contentLanguage.toUpperCase(), bg: 'rgba(20,20,20,0.86)' });
  }

  if (ov.showStatusEnabled && item.showStatus) {
    const [lbl, bg] = statusBadge(item.showStatus);
    const p = pos('showStatusEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: lbl, bg });
  }

  if (ov.episodeCountEnabled && item.episodeCount != null) {
    const p = pos('episodeCountEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: `${item.episodeCount} Episodes`, bg: 'rgba(20,20,20,0.86)' });
  }

  if (ov.trendingEnabled && item.isTrending) {
    const p = pos('trendingEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: 'Trending', bg: 'rgba(200,90,20,0.86)' });
  }

  if (ov.popularEnabled && item.isPopular) {
    const p = pos('popularEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: 'Popular', bg: 'rgba(25,75,155,0.86)' });
  }

  if (ov.metacriticEnabled && item.metacriticScore != null) {
    const p = pos('metacriticEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: '/images/rating/Metacritic.png', subtext: `${item.metacriticScore}`,
      fallbackText: `Metacritic ${item.metacriticScore}`, bg: 'rgba(40,40,40,0.86)' });
  }
  if (ov.letterboxdEnabled && item.letterboxdRating != null) {
    const p = pos('letterboxdEnabled'), score = item.letterboxdRating.toFixed(1);
    badges.push({ x: p.x, y: p.y, imgSrc: '/images/rating/Letterboxd.png', subtext: score,
      fallbackText: `Letterboxd ${score}`, bg: 'rgba(40,40,40,0.86)' });
  }
  if (ov.traktEnabled && item.traktRating != null) {
    const p = pos('traktEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: '/images/rating/Trakt.png', subtext: `${item.traktRating}%`,
      fallbackText: `Trakt ${item.traktRating}%`, bg: 'rgba(40,40,40,0.86)' });
  }
  const newLbl = newBadgeLabel(item, ov);
  if (newLbl) {
    const p = pos('newBadgeEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: newLbl, bg: 'rgba(22,150,90,0.88)' });
  }
  if (ov.videoSourceEnabled && item.videoSource) {
    const p = pos('videoSourceEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: item.videoSource, bg: 'rgba(20,20,20,0.86)' });
  }
  if (ov.runtimeEnabled && item.mediaType === 0 && item.runtimeMinutes > 0) {
    const p = pos('runtimeEnabled');
    badges.push({ x: p.x, y: p.y, textOnly: true, text: formatRuntime(item.runtimeMinutes), bg: 'rgba(20,20,20,0.86)' });
  }
  if (ov.versionsEnabled && item.versionCount >= 2) {
    const p = pos('versionsEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: '/images/versions.png', fallbackText: `${item.versionCount} Versions`, bg: 'rgba(20,20,20,0.86)' });
  }
  if (ov.audioLanguagesEnabled && item.audioLanguageCount >= 2) {
    const multi = item.audioLanguageCount >= 3, p = pos('audioLanguagesEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/${multi ? 'multi' : 'dual'}_audio.png`,
      fallbackText: multi ? 'Multi Audio' : 'Dual Audio', bg: 'rgba(20,20,20,0.86)' });
  }
  if (ov.subtitleLanguagesEnabled && item.subtitleLanguageCount >= 2) {
    const multi = item.subtitleLanguageCount >= 3, p = pos('subtitleLanguagesEnabled');
    badges.push({ x: p.x, y: p.y, imgSrc: `/images/${multi ? 'multi' : 'dual'}_subs.png`,
      fallbackText: multi ? 'Multi Subs' : 'Dual Subs', bg: 'rgba(20,20,20,0.86)' });
  }

  return oneRibbonPerCorner(badges, b => b.x, b => b.y);
}

// Any factory position a badge has had (current + older defaults saved settings may still hold).
function isFactoryPos(p, spots) { return spots.some(([x, y]) => Math.abs(p.x - x) < 0.005 && Math.abs(p.y - y) < 0.005); }

// Mirrors OverlayRenderer: ribbons are corner sashes, so two in the same spot would overlap. Keep the
// first-added (highest-priority: awards, then ratings); a ribbon moved elsewhere is left alone.
function oneRibbonPerCorner(badges, fx, fy) {
  const kept = [];
  return badges.filter(b => {
    if (!b.isRibbon) return true;
    if (kept.some(k => Math.abs(fx(k) - fx(b)) < 0.12 && Math.abs(fy(k) - fy(b)) < 0.12)) return false;
    kept.push(b); return true;
  });
}

// Mirrors OverlayRenderer.NewBadgeLabel. Dates come back without a zone marker but are UTC.
function newBadgeLabel(item, ov) {
  if (!ov?.newBadgeEnabled) return null;
  const since = Date.now() - Math.min(365, Math.max(1, ov.newBadgeDays || 14)) * 864e5;
  const utc = v => v ? Date.parse(/[zZ]|[+-]\d\d:\d\d$/.test(v) ? v : v + 'Z') : NaN;
  if (utc(item.addedAtUtc) >= since) return 'NEW';
  if (item.mediaType === 1 && utc(item.latestSeasonAddedUtc) >= since) return 'NEW SEASON';
  return null;
}

function formatRuntime(m) { return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m}m`; }

// Horizontal placement for a grid badge, matching anchorBoxX() in the settings preview and
// AnchorX() in OverlayRenderer: left side anchors its left edge and grows right, right side
// anchors its right edge and grows left, the middle stays centred. The old code centre-anchored
// everything and guessed a fixed 12% half-width, so any badge wider than that (long studio or
// status text) was sliced off the left edge of the card.
function badgeAnchorStyle(b) {
  const xPct = `${(b.x * 100).toFixed(2)}%`;
  const top  = `${(b.y * 100).toFixed(2)}%`;
  if (b.isRibbon) {                       // fixed-width corner sash — keep it centred
    const half = 'calc(var(--card-badge-width, 22%) / 2)';
    return `left:clamp(${half}, ${xPct}, calc(100% - ${half}));top:${top};transform:translate(-50%,-50%)`;
  }
  const tx = b.x < 0.45 ? 'translate(0,-50%)'        // expand right
           : b.x > 0.55 ? 'translate(-100%,-50%)'    // expand left
           : 'translate(-50%,-50%)';                 // centred
  // Cap the width so a long badge can't run off the opposite edge either.
  const maxW = b.x < 0.45 ? `calc(100% - ${xPct} - 2%)`
             : b.x > 0.55 ? `calc(${xPct} - 2%)`
             : '96%';
  return `left:${xPct};top:${top};transform:${tx};max-width:${maxW}`;
}

// ── Render one badge descriptor as absolutely-positioned HTML ─────────────────
function badgeElHtml(b) {
  const top = `${(b.y * 100).toFixed(2)}%`;

  // Text-only badges use the same dark Kometa box (no per-type colour).
  if (b.textOnly) return `<div class="item-badge text-pill" style="${badgeAnchorStyle(b)}">${esc(b.text)}</div>`;

  if (!b.imgSrc) return '';

  const style = badgeAnchorStyle(b);

  const cls = b.hasPill ? 'item-badge has-pill'
            : b.isRibbon ? 'item-badge ribbon'   // corner sash (RT / awards) — no dark box
            : 'item-badge plain-img';
  // Subtext span always renders (even empty) so the onerror handler below can rely on a
  // fixed sibling count regardless of whether this particular badge type has subtext.
  const subtext = `<span class="item-badge-subtext">${b.subtext ? esc(b.subtext) : ''}</span>`;
  const fallback = b.fallbackText != null
    ? `<span class="item-badge-fallback" style="display:none;background:${b.bg || '#222'}">${esc(b.fallbackText)}</span>`
    : '';
  // onerror: hide the broken image and its subtext, then reveal the fallback pill
  // (or just disappear entirely when there's no server-side fallback, e.g. content rating).
  const giveUp = fallback
    ? `this.style.display='none';this.nextElementSibling.style.display='none';this.nextElementSibling.nextElementSibling.style.display='inline-block'`
    : `this.style.display='none'`;
  // Providers have several possible asset filenames — try each before falling back to text,
  // so a logo is always preferred when one exists.
  const alts = (b.altSrcs || []).filter(Boolean);
  const onerror = alts.length
    ? `var a=this.dataset.alts?this.dataset.alts.split('|'):[];if(a.length){this.src=a.shift();this.dataset.alts=a.join('|');return;}${giveUp}`
    : giveUp;
  const altAttr = alts.length ? ` data-alts="${esc(alts.join('|'))}"` : '';

  return `<div class="${cls}" style="${style}">
    <img src="${esc(b.imgSrc)}"${altAttr} alt="${esc(b.fallbackText || '')}" onerror="${onerror}" />
    ${subtext}
    ${fallback}
  </div>`;
}

function allBadgesHtml(item) {
  return itemBadges(item, S.settings?.overlays).map(badgeElHtml).join('');
}

function sourceLabel(n) { return ['TMDB','FanArt.tv','TVDB',srvName(),'Local','Postarr','Kometa'][n] ?? 'Unknown'; }
const SOURCE_MAP = { TMDB:0,Tmdb:0,'FanArt.tv':1,FanArt:1,TVDB:2,Tvdb:2,Plex:3,Local:4,Generated:5,Postarr:5,Kometa:6 };

// ── Poster card with Apply / Change / Dismiss ─────────────────────────────────
function posterCardHtml(item) {
  const src  = item.currentPosterUrl || '';
  // Status shown as a faint glow behind the card: green = applied to Plex, amber = proposed.
  const state = item.posterAppliedToPlex ? ' applied' : (src ? ' pending' : '');
  const stateTitle = item.posterAppliedToPlex ? `Applied to ${srvName()}`
    : (src ? `Proposed — click Apply to push to ${srvName()}` : '');

  const imgEl = src
    ? `<img src="${esc(thumbUrl(src, 400))}" loading="lazy" decoding="async" data-change-id="${item.id}"
          style="cursor:pointer" title="Click to change poster"
          onerror="this.style.display='none';this.nextElementSibling.hidden=false" />
       <div class="no-poster" hidden><svg width="32" height="32" viewBox="0 0 24 24" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" stroke="currentColor" stroke-width="1.5"/><circle cx="8.5" cy="8.5" r="1.5" fill="currentColor"/><path d="M3 15l5-4 4 3 3-4 6 5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg><span>Load failed</span></div>`
    : `<div class="no-poster" data-change-id="${item.id}" style="cursor:pointer" title="Click to choose a poster">
         <svg width="32" height="32" viewBox="0 0 24 24" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" stroke="currentColor" stroke-width="1.5"/><circle cx="8.5" cy="8.5" r="1.5" fill="currentColor"/><path d="M3 15l5-4 4 3 3-4 6 5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
         <span>Click to choose</span>
       </div>`;

  const actions = `<div class="card-actions">
    <button class="card-btn card-btn-apply"   data-action="apply"   data-id="${item.id}">✓ Apply</button>
    <button class="card-btn card-btn-change"  data-action="change"  data-id="${item.id}">↕ Change</button>
    <button class="card-btn card-btn-dismiss" data-action="dismiss" data-id="${item.id}">✕ Dismiss</button>
  </div>`;

  // Badges only make sense painted over an actual poster. When there's none (e.g. after Dismiss),
  // render an empty, badge-free "Click to choose" card instead of leaving badges floating on it.
  // Only visible when "Show dismissed items" is on — labels the tile so it's obvious why it's blank.
  const dismissedTag = item.posterDismissed ? '<span class="dismissed-tag">Dismissed</span>' : '';

  return `<div class="poster-card${state}${item.posterDismissed ? ' is-dismissed' : ''}" data-id="${item.id}"${stateTitle ? ` title="${esc(stateTitle)}"` : ''}>
    <div class="poster-card-inner">
      ${dismissedTag}${src ? allBadgesHtml(item) : ''}${imgEl}
      <div class="overlay-info"><div class="item-title">${esc(item.title)}</div><div class="item-year">${item.year ?? ''}</div></div>
      ${actions}
    </div>
  </div>`;
}

function bgCardHtml(item) {
  const src = item.currentBackgroundUrl || '';
  const state = item.backgroundAppliedToPlex ? ' applied' : (src ? ' pending' : '');
  const tag = item.currentBackgroundSource
    ? `<span class="source-tag">${esc(item.currentBackgroundSource)}${item.backgroundAppliedToPlex ? ' ✓' : ''}</span>` : '';
  const imgEl = src
    ? `<img src="${esc(thumbUrl(src, 600))}" loading="lazy" decoding="async" data-bg-change-id="${item.id}" style="cursor:pointer" title="Click to change background" onerror="this.style.display='none'" />`
    : `<div class="no-poster" data-bg-change-id="${item.id}" style="cursor:pointer" title="Click to choose a background"><svg width="40" height="40" viewBox="0 0 24 24" fill="none"><rect x="2" y="5" width="20" height="14" rx="2" stroke="currentColor" stroke-width="1.5"/><circle cx="8" cy="10" r="1.5" fill="currentColor"/><path d="M2 16l5-4 4 3 4-5 7 6" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg><span>No background</span></div>`;
  const actions = `<div class="card-actions">
    <button class="card-btn card-btn-apply"   data-bg-action="apply"   data-id="${item.id}">✓ Apply</button>
    <button class="card-btn card-btn-change"  data-bg-action="change"  data-id="${item.id}">↕ Change</button>
    <button class="card-btn card-btn-dismiss" data-bg-action="dismiss" data-id="${item.id}">✕ Dismiss</button>
  </div>`;
  // The click target lives on the CARD, not just the <img>: .bg-card .overlay-info is stretched
  // across the whole card (inset:0) and swallowed every click, so only the sliver of bare image
  // was clickable. Now clicking anywhere on the card opens the picker, like poster cards do.
  const dismissedTag = item.backgroundDismissed ? '<span class="dismissed-tag">Dismissed</span>' : '';
  return `<div class="bg-card${state}${item.backgroundDismissed ? ' is-dismissed' : ''}" data-id="${item.id}" data-bg-change-id="${item.id}"
               title="Click to change background">
    <div class="bg-card-inner">${dismissedTag}${tag}${imgEl}
      <div class="overlay-info"><div class="item-title">${esc(item.title)}</div></div>
      ${actions}
    </div>
  </div>`;
}

function collectionCardHtml(col) {
  const src   = col.currentPosterUrl || '';
  const imgEl = src
    ? `<img src="${esc(thumbUrl(src, 400))}" loading="lazy" decoding="async" data-col-change-id="${col.id}" style="cursor:pointer" title="Click to change poster" onerror="this.style.display='none';this.nextElementSibling.hidden=false" /><div class="no-poster" hidden><span>No poster</span></div>`
    : `<div class="no-poster" data-col-change-id="${col.id}" style="cursor:pointer" title="Click to choose a poster"><span>No poster</span></div>`;
  const actions = `<div class="card-actions">
    <button class="card-btn card-btn-apply"   data-col-action="apply"   data-id="${col.id}">✓ Apply</button>
    <button class="card-btn card-btn-change"  data-col-action="change"  data-id="${col.id}">↕ Change</button>
    <button class="card-btn card-btn-dismiss" data-col-action="dismiss" data-id="${col.id}">✕ Dismiss</button>
  </div>`;
  const dismissedTag = col.posterDismissed ? '<span class="dismissed-tag">Dismissed</span>' : '';
  return `<div class="poster-card${col.posterDismissed ? ' is-dismissed' : ''}" data-col-id="${col.id}">
    <div class="poster-card-inner">${dismissedTag}${imgEl}
      <div class="badge-stack"><span class="badge">${col.itemCount} items</span></div>
      <div class="overlay-info"><div class="item-title">${esc(col.title)}</div></div>
      ${actions}
    </div>
  </div>`;
}

// ── Render views ──────────────────────────────────────────────────────────────
// opts.keepScroll: preserve the grid's scroll position across the re-render (and skip the
// "Loading…" flash) — used when refreshing after an in-place action so the user keeps their spot.
// Grids hide dismissed entries so a dismiss actually removes the tile. The "Show dismissed items"
// setting reveals them again (marked, and still clickable) so an accidental dismiss is recoverable.
function visibleItems(list, dismissedKey) {
  return S.settings?.showDismissed ? list : list.filter(x => !x[dismissedKey]);
}

async function renderMovies(opts = {}) {
  const c = el('#view-movies');
  const targetY = _scrollMem[c.id] || 0;
  if (!opts.keepScroll) c.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const [movies] = await Promise.all([api('/library/movies'), ensureSettingsLoaded()]);
    // Keep S.movies complete (Apply All and card lookups need every item) but hide the ones whose
    // poster the user dismissed — only from this poster view; their background card still shows.
    S.movies = sortByTitle(movies ?? []);
    const shown = visibleItems(S.movies, 'posterDismissed');
    c.innerHTML = shown.length ? shown.map(posterCardHtml).join('') : '<div class="empty-state">No movies yet — run a library scan.</div>';
    c.scrollTop = _scrollMem[c.id] = targetY;
    buildAzBar(c);
    applyGridFilters();
  } catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}

async function renderShows(opts = {}) {
  const c = el('#view-shows');
  const targetY = _scrollMem[c.id] || 0;
  if (!opts.keepScroll) c.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const [shows] = await Promise.all([api('/library/shows'), ensureSettingsLoaded()]);
    S.shows = sortByTitle(shows ?? []);
    const shown = visibleItems(S.shows, 'posterDismissed');
    c.innerHTML = shown.length ? shown.map(posterCardHtml).join('') : '<div class="empty-state">No shows yet — run a library scan.</div>';
    c.scrollTop = _scrollMem[c.id] = targetY;
    buildAzBar(c);
    applyGridFilters();
  } catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}

// Badges need S.settings.overlays to know what's enabled and where — the boot-time
// fetch usually wins the race, but library data can occasionally resolve first.
async function ensureSettingsLoaded() {
  if (!S.settings) S.settings = await api('/settings');
}

// Name of the configured media server for user-facing text ("Apply to Plex" / "Apply to Jellyfin").
function srvName() { return ['Jellyfin', 'Emby'].includes(S.settings?.mediaServerType) ? S.settings.mediaServerType : 'Plex'; }

// Static text that names the server (the sidebar's Apply All button) — refreshed at boot and after a
// settings save. Left alone while Apply All is running, since it shows progress then.
function refreshServerLabels() {
  const b = el('#apply-all-btn');
  if (b && !b.disabled) b.innerHTML = applyAllLabel();
}

async function renderBackgrounds(kind, opts = {}) {
  const id = `#view-${kind}-backgrounds`;
  const c  = el(id);
  const targetY = _scrollMem[c.id] || 0;
  if (!opts.keepScroll) c.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const items = kind === 'movie'
      ? (S.movies.length ? S.movies : sortByTitle(await api('/library/movies') ?? []))
      : (S.shows.length  ? S.shows  : sortByTitle(await api('/library/shows')  ?? []));
    if (kind === 'movie' && !S.movies.length) S.movies = items;
    if (kind === 'show'  && !S.shows.length)  S.shows  = items;
    // Background views honour the *background* dismissal, independently of the poster one.
    const shown = visibleItems(items, 'backgroundDismissed');
    c.innerHTML = shown.length ? shown.map(bgCardHtml).join('') : '<div class="empty-state">No items yet — run a library scan.</div>';
    c.scrollTop = _scrollMem[c.id] = targetY;
    buildAzBar(c);
    applyGridFilters();
  } catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}

async function renderCollections(kind, opts = {}) {
  const id = `#view-${kind}-collections`;
  const c  = el(id);
  const targetY = _scrollMem[c.id] || 0;
  if (!opts.keepScroll) c.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const cols = sortByTitle(await api(`/library/collections/${kind === 'movie' ? 'movies' : 'shows'}`) ?? []);
    // Keep the full list in state (Apply All needs it); hide the ones the user dismissed.
    if (kind === 'movie') S.movieCollections = cols; else S.showCollections = cols;
    const shown = visibleItems(cols, 'posterDismissed');
    c.innerHTML = shown.length ? shown.map(collectionCardHtml).join('') : `<div class="empty-state">No collections found. Create collections in ${srvName()}, then run a scan.</div>`;
    c.scrollTop = _scrollMem[c.id] = targetY;
    buildAzBar(c);
    applyGridFilters();
  } catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}

// ── Card action handler ───────────────────────────────────────────────────────
document.addEventListener('click', async e => {
  // In multi-select mode, a click anywhere on a card toggles its selection instead of running the
  // card's normal action (open picker / apply / dismiss). Clicks outside a card fall through.
  if (S.selectMode) {
    const card = e.target.closest('.poster-card, .bg-card');
    if (card) { e.preventDefault(); e.stopPropagation(); toggleCardSelected(card); return; }
  }

  // Click on poster image or no-poster placeholder to change poster
  const imgClick = e.target.closest('[data-change-id]');
  if (imgClick && !e.target.closest('[data-action]') && !e.target.closest('[data-bg-action]')) {
    const id = +imgClick.dataset.changeId;
    const item = [...S.movies, ...S.shows].find(i => i.id === id);
    if (item) {
      if (item.mediaType !== 0 && item.seasons?.length) openArtModal(id, 'poster');
      else openArtModal(id, 'poster');
    }
    return;
  }

  // Click on background image or placeholder to change background
  const bgImgClick = e.target.closest('[data-bg-change-id]');
  if (bgImgClick && !e.target.closest('[data-bg-action]')) {
    const id = +bgImgClick.dataset.bgChangeId;
    openArtModal(id, 'background');
    return;
  }

  // Click on collection poster image or placeholder to change it
  const colImgClick = e.target.closest('[data-col-change-id]');
  if (colImgClick && !e.target.closest('[data-col-action]')) {
    const id = +colImgClick.dataset.colChangeId;
    openArtModal(id, 'collection');
    return;
  }

  // Poster card actions
  const btn = e.target.closest('[data-action]');
  if (btn) {
    e.stopPropagation();
    const id = +btn.dataset.id, action = btn.dataset.action;
    const item = [...S.movies, ...S.shows].find(i => i.id === id);
    if (!item) return;
    if (action === 'apply')   await cardApply(id, 'poster');
    if (action === 'change')  openArtModal(id, 'poster');
    if (action === 'dismiss') await cardDismiss(id, 'poster');
    return;
  }

  // Background card actions
  const bgBtn = e.target.closest('[data-bg-action]');
  if (bgBtn) {
    e.stopPropagation();
    const id = +bgBtn.dataset.id, action = bgBtn.dataset.bgAction;
    if (action === 'apply')   await cardApply(id, 'background');
    if (action === 'change')  openArtModal(id, 'background');
    if (action === 'dismiss') await cardDismiss(id, 'background');
    return;
  }

  // Collection card actions
  const colBtn = e.target.closest('[data-col-action]');
  if (colBtn) {
    e.stopPropagation();
    const id = +colBtn.dataset.id, action = colBtn.dataset.colAction;
    if (action === 'change')  { openArtModal(id, 'collection'); return; }
    if (action === 'apply')   { await cardApplyCollection(id); return; }
    if (action === 'dismiss') { await cardDismissCollection(id); return; }
  }
});

async function cardApplyCollection(colId) {
  const all = [...S.movieCollections, ...S.showCollections];
  const col = all.find(c => c.id === colId);
  if (!col?.currentPosterUrl) { toast('No poster chosen yet — click the card to pick one first.', true); return; }
  try {
    await api(`/collections/apply/${colId}`, {
      method: 'POST',
      body: JSON.stringify({ imageUrl: col.currentPosterUrl, source: SOURCE_MAP[col.currentPosterSource] ?? 0 })
    });
    toast(`Collection poster pushed to ${srvName()}!`);
    if (S.view === 'movie-collections') renderCollections('movie', { keepScroll: true });
    if (S.view === 'show-collections')  renderCollections('show',  { keepScroll: true });
  } catch(e) { toast(`Apply failed: ${e.message}`, true); }
}

async function cardDismissCollection(colId) {
  try {
    await api(`/collections/dismiss/${colId}`, { method: 'POST' });
    toast('Collection proposal dismissed.');
    if (S.view === 'movie-collections') renderCollections('movie', { keepScroll: true });
    if (S.view === 'show-collections')  renderCollections('show',  { keepScroll: true });
  } catch(e) { toast(`Dismiss failed: ${e.message}`, true); }
}

async function cardApply(itemId, kind) {
  try {
    // force=true: clicking Apply is an explicit "push this now", same as Apply All. Without it the
    // server short-circuits with "unchanged" for an already-applied item and uploads nothing —
    // which made re-applying after a badge change silently do nothing.
    const path = kind === 'background'
      ? `/backgrounds/apply-current/item/${itemId}?force=true`
      : `/posters/apply-current/item/${itemId}?force=true`;
    const r = await api(path, { method: 'POST' });
    const label = kind === 'background' ? 'Background' : 'Poster';
    // Report what actually happened rather than always claiming success.
    toast(r?.status === 'unchanged'
      ? `${label} is already up to date — nothing to push.`
      : `${label} pushed to ${srvName()}!`);
    // Refresh card to show applied state
    if (S.view === 'movies' || S.view === 'movie-backgrounds') { S.movies = []; renderMovies({ keepScroll: true }); if (S.view === 'movie-backgrounds') renderBackgrounds('movie', { keepScroll: true }); }
    if (S.view === 'shows'  || S.view === 'show-backgrounds')  { S.shows  = []; renderShows({ keepScroll: true });  if (S.view === 'show-backgrounds')  renderBackgrounds('show', { keepScroll: true }); }
  } catch(e) { toast(`Apply failed: ${e.message}`, true); }
}

async function cardDismiss(itemId, kind) {
  try {
    const path = kind === 'background'
      ? `/backgrounds/dismiss/item/${itemId}`
      : `/posters/dismiss/item/${itemId}`;
    await api(path, { method: 'POST' });
    toast('Proposal dismissed.');
    if (S.view === 'movies' || S.view === 'movie-backgrounds') { S.movies = []; renderMovies({ keepScroll: true }); if (S.view === 'movie-backgrounds') renderBackgrounds('movie', { keepScroll: true }); }
    if (S.view === 'shows'  || S.view === 'show-backgrounds')  { S.shows  = []; renderShows({ keepScroll: true });  if (S.view === 'show-backgrounds')  renderBackgrounds('show', { keepScroll: true }); }
  } catch(e) { toast(`Dismiss failed: ${e.message}`, true); }
}

// ── Art modal (Change picker) ─────────────────────────────────────────────────

function showModalLayer(layer) {
  // layer: 'detail' | 'picker'
  el('#show-detail').hidden  = layer !== 'detail';
  el('#modal-grid').hidden   = layer !== 'picker';
  el('#modal-toolbar').style.display = layer === 'picker' ? '' : 'none';
  el('#modal-tabs').innerHTML = '';
}

async function openArtModal(itemId, kind) {
  const item = kind === 'collection'
    ? [...S.movieCollections, ...S.showCollections].find(c => c.id === itemId)
    : [...S.movies, ...S.shows].find(i => i.id === itemId);
  if (!item) return;

  S.modal = { kind, itemId, seasonId: null, candidates: [], activeTab: 'item', item };

  el('#modal-title').textContent    = item.title;
  el('#modal-subtitle').textContent = '';
  el('#modal-actions-right').innerHTML = '';
  el('#modal-back-btn').hidden = true;
  el('#art-modal').hidden = false;
  el('#modal-textless-only').checked = false;
  const mst = el('#match-set-toggle'); if (mst) mst.hidden = true;   // only the season picker offers "match the other seasons"

  // TV shows in poster mode → show the detail view with show poster + seasons
  if (kind === 'poster' && item.mediaType !== 0 /* not Movie */ && item.seasons?.length) {
    showModalLayer('detail');
    // The show page needs the custom-import button too, so keep the actions bar visible here —
    // only the "textless" filter is meaningless without a candidate list.
    el('#modal-toolbar').style.display = '';
    const tl = el('.textless-toggle'); if (tl) tl.style.display = 'none';
    renderModalActions({ restoreId: itemId, restoreKind: 'poster' });
    renderShowDetail(item);
    wireModalDragDrop();
  } else {
    // Movies, backgrounds, collections → go straight to picker
    showModalLayer('picker');
    el('#modal-grid').className = kind === 'background' ? 'modal-grid bg-mode' : 'modal-grid';
    el('#modal-grid').hidden = false;
    // Collections have no textless concept (they're not a single piece of media)
    el('#modal-toolbar').style.display = kind === 'collection' ? 'none' : '';
    const tl = el('.textless-toggle'); if (tl) tl.style.display = '';
    renderModalActions({ restoreId: itemId, restoreKind: kind });
    await loadCandidates(item, 'item', kind);
    wireModalDragDrop();
  }
}

// Right-hand modal actions (Restore + custom folder import). Shared by the item/collection
// picker, the show-poster page and the season picker so every one of them can import a file.
function renderModalActions({ restoreId = null, restoreKind = null } = {}) {
  el('#modal-actions-right').innerHTML = `
    ${restoreId != null ? '<button class="btn-restore" id="modal-restore-btn">↩ Restore original</button>' : ''}
    <label class="btn-restore" id="modal-upload-label" title="Import a poster image from your computer" style="cursor:pointer;margin-left:8px">
      📁 Custom poster
      <input type="file" id="modal-upload-input" accept="image/*" style="display:none" />
    </label>`;
  if (restoreId != null)
    el('#modal-restore-btn').addEventListener('click', () => restoreArt(restoreId, restoreKind));
  el('#modal-upload-input').addEventListener('change', e => {
    const file = e.target.files?.[0];
    if (file) handleCustomPosterUpload(file);
  });
}

function renderShowDetail(item) {
  const seasons = [...(item.seasons || [])].sort((a, b) => a.seasonNumber - b.seasonNumber);

  const showThumb = item.currentPosterUrl
    ? `<img src="${esc(thumbUrl(item.currentPosterUrl, 300))}" loading="lazy" decoding="async" onerror="this.style.display='none'" />`
    : `<div class="no-poster"><svg width="24" height="24" viewBox="0 0 24 24" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" stroke="currentColor" stroke-width="1.5"/></svg></div>`;

  // Season posters get the parent show's badges baked in on Apply (see
  // PosterApplyService.ApplySeasonPosterAsync), gated by the season-overlay toggle.
  // Mirror that here so the preview matches — only over an actual poster, and only
  // when the scope is enabled.
  const seasonBadges = S.settings?.overlays?.seasonPosterOverlaysEnabled !== false
    ? allBadgesHtml(item) : '';

  const seasonCards = seasons.map(s => {
    const thumb = s.currentPosterUrl
      ? `<img src="${esc(thumbUrl(s.currentPosterUrl, 300))}" loading="lazy" decoding="async" onerror="this.style.display='none'" />`
      : `<div class="no-poster"><svg width="20" height="20" viewBox="0 0 24 24" fill="none"><rect x="3" y="3" width="18" height="18" rx="2" stroke="currentColor" stroke-width="1.5"/></svg><span>No poster</span></div>`;
    return `<div class="season-card">
      <div class="season-thumb" data-season-id="${s.id}" data-season-num="${s.seasonNumber}"
           style="cursor:pointer" title="Click to choose this season's poster">
        ${thumb}${s.currentPosterUrl ? seasonBadges : ''}
      </div>
      <div class="season-label">S${String(s.seasonNumber).padStart(2,'0')}</div>
      <div class="season-actions">
        <button class="card-btn card-btn-apply"   data-s-action="apply"   data-season-id="${s.id}">✓</button>
        <button class="card-btn card-btn-change"  data-s-action="change"  data-season-id="${s.id}">↕</button>
        <button class="card-btn card-btn-dismiss" data-s-action="dismiss" data-season-id="${s.id}">✕</button>
      </div>
    </div>`;
  }).join('');

  el('#show-detail').innerHTML = `
    <div class="show-detail-section">
      <h4>Show Poster</h4>
      <div class="show-poster-row">
        <div class="show-poster-thumb" style="cursor:pointer" title="Click to change the show poster">${showThumb}</div>
        <div>
          <div class="show-poster-actions">
            <button class="card-btn card-btn-apply"   data-action="apply"   data-id="${item.id}">✓ Apply to ${srvName()}</button>
            <button class="card-btn card-btn-change"  data-action="change"  data-id="${item.id}">↕ Change poster</button>
            <button class="card-btn card-btn-dismiss" data-action="dismiss" data-id="${item.id}">✕ Dismiss</button>
          </div>
          <div class="show-poster-source">${item.currentPosterSource ? `Source: ${esc(item.currentPosterSource)}` : 'No poster selected yet'}</div>
        </div>
      </div>
    </div>
    <div class="show-detail-section">
      <h4>Season Posters — click ↕ to change, ✓ to apply, ✕ to dismiss</h4>
      <div class="season-grid">${seasonCards}</div>
    </div>`;

  // Wire show-level actions
  el('#show-detail').querySelectorAll('[data-action]').forEach(btn => {
    btn.addEventListener('click', async e => {
      e.stopPropagation();
      const action = btn.dataset.action;
      const id     = +btn.dataset.id;
      if (action === 'apply')   { await cardApply(id, 'poster'); renderShowDetail({ ...item, posterAppliedToPlex: true }); }
      if (action === 'dismiss') { await cardDismiss(id, 'poster'); renderShowDetail({ ...item, currentPosterUrl: null, posterAppliedToPlex: false }); }
      if (action === 'change')  { openSeasonPicker(item, null); }
    });
  });

  // Wire season actions
  el('#show-detail').querySelectorAll('[data-s-action]').forEach(btn => {
    btn.addEventListener('click', async e => {
      e.stopPropagation();
      const seasonId = +btn.dataset.seasonId;
      const action   = btn.dataset.sAction;
      const season   = seasons.find(s => s.id === seasonId);
      if (action === 'change')  { openSeasonPicker(item, season); return; }
      if (action === 'apply')   { await applySeasonCurrent(seasonId, season); return; }
      if (action === 'dismiss') { await dismissSeason(seasonId, item, seasons); return; }
    });
  });

  // Clicking the season poster itself opens the picker (previously only the ↕ button did).
  el('#show-detail').querySelectorAll('.season-thumb').forEach(th => {
    th.addEventListener('click', () => {
      const season = seasons.find(s => s.id === +th.dataset.seasonId);
      if (season) openSeasonPicker(item, season);
    });
  });

  // Clicking the show poster itself opens the picker too, matching the season posters.
  el('#show-detail').querySelector('.show-poster-thumb')
    ?.addEventListener('click', () => openSeasonPicker(item, null));
}

async function applySeasonCurrent(seasonId, season) {
  if (!season?.currentPosterUrl) { toast('No poster proposed for this season — click ↕ to choose one.', true); return; }
  try {
    await api(`/posters/apply/season/${seasonId}`, {
      method: 'POST',
      body: JSON.stringify({ imageUrl: season.currentPosterUrl, source: SOURCE_MAP[season.currentPosterSource] ?? 0 })
    });
    toast(`Season poster applied to ${srvName()}.`);
    // Refresh state
    S.shows = []; await renderShows({ keepScroll: true });
    const updated = S.shows.find(s => s.id === S.modal?.itemId);
    if (updated) renderShowDetail(updated);
  } catch(e) { toast(`Failed: ${e.message}`, true); }
}

async function dismissSeason(seasonId, item, seasons) {
  // No dedicated dismiss endpoint for seasons yet — just clear from state
  try {
    await api(`/posters/dismiss/season/${seasonId}`, { method: 'POST' });
    toast('Season proposal dismissed.');
  } catch {
    // Endpoint may not exist yet — just show feedback
    toast('Season dismissed.');
  }
  S.shows = []; await renderShows({ keepScroll: true });
  const updated = S.shows.find(s => s.id === item.id);
  if (updated) renderShowDetail(updated);
}

function openSeasonPicker(item, season) {
  // season = null means picking for the show itself
  const isShow    = season === null;
  const title     = isShow ? `${item.title} — Show Poster` : `${item.title} — S${String(season.seasonNumber).padStart(2,'0')}`;
  el('#modal-subtitle').textContent = 'Choose a poster';
  el('#modal-title').textContent    = title;

  // Show back button to return to show detail
  el('#modal-back-btn').hidden = false;

  showModalLayer('picker');
  el('#modal-grid').className = 'modal-grid';
  el('#modal-grid').hidden = false;
  el('#modal-toolbar').style.display = '';
  const tl = el('.textless-toggle'); if (tl) tl.style.display = '';

  S.modal.pickerMode  = isShow ? 'show' : 'season';
  const ms = el('#match-set-toggle');
  if (ms) {
    ms.hidden = isShow;
    try { el('#modal-match-set').checked = localStorage.getItem('matchSeasonSet') === '1'; } catch { /* storage unavailable */ }
  }
  S.modal.seasonId    = isShow ? null : season.seasonNumber; // reuse for season number lookup
  S.modal.seasonItemId = isShow ? null : season.id;

  // Custom folder import for the show/season poster (was drag-and-drop only).
  renderModalActions(isShow ? { restoreId: item.id, restoreKind: 'poster' } : {});
  wireModalDragDrop();

  // Load candidates
  const grid = el('#modal-grid');
  grid.innerHTML = '<div class="empty-state">Searching TMDB, FanArt.tv and TVDB…</div>';

  const path = isShow
    ? `/posters/candidates/item/${item.id}`
    : `/posters/candidates/season/${season.id}`;

  api(path).then(candidates => {
    S.modal.candidates = candidates ?? [];
    renderCandidateGrid('poster');
  }).catch(e => {
    grid.innerHTML = `<div class="empty-state">Could not load: ${esc(e.message)}</div>`;
  });
}

async function switchTab(tabEl, item) {
  els('.modal-tab').forEach(t => t.classList.toggle('active', t === tabEl));
  const tab = tabEl.dataset.tab;
  S.modal.activeTab = tab;
  if (tab === 'item') await loadCandidates(item, 'item', S.modal.kind);
  else {
    const seasonId = parseInt(tab.replace('season-', ''));
    S.modal.seasonId = seasonId;
    await loadCandidates(item, 'season', 'poster', seasonId);
  }
}

async function loadCandidates(item, scope, kind, seasonId = null) {
  const grid = el('#modal-grid');
  grid.innerHTML = '<div class="empty-state">Searching TMDB, FanArt.tv and TVDB…</div>';
  try {
    const path = kind === 'collection'         ? `/collections/candidates/${item.id}`
      : kind === 'background'                  ? `/backgrounds/candidates/item/${item.id}`
      : scope === 'season' && seasonId         ? `/posters/candidates/season/${seasonId}`
      : `/posters/candidates/item/${item.id}`;
    S.modal.candidates = await api(path) ?? [];
    renderCandidateGrid(kind);
  } catch(e) { grid.innerHTML = `<div class="empty-state">Could not load: ${esc(e.message)}</div>`; }
}

function renderCandidateGrid(kind) {
  const grid = el('#modal-grid');
  const textlessOnly = el('#modal-textless-only').checked && kind !== 'background';
  const candidates   = S.modal.candidates.filter(c => !textlessOnly || c.isTextless);
  if (!candidates.length) { grid.innerHTML = '<div class="empty-state">No images found.</div>'; return; }
  grid.innerHTML = candidates.map((c, i) => `
    <div class="candidate-card ${kind === 'background' ? 'bg-mode' : ''}" data-idx="${i}">
      <div class="candidate-card-inner">
        <img src="${esc(c.thumbnailUrl || c.imageUrl)}" loading="lazy" />
        <span class="candidate-tag">${sourceLabel(c.source)}</span>
        ${c.isTextless ? '<span class="textless-flag">No text</span>' : ''}
        ${c.setMatches ? `<span class="set-flag" title="Part of a FanArt.tv set: it has matching posters for ${c.setMatches} of this show's ${c.setOtherSeasons} other season${c.setOtherSeasons === 1 ? '' : 's'}. Tick “Match the other seasons” to apply them all.">Set · ${c.setMatches + 1}/${c.setOtherSeasons + 1}</span>` : ''}
      </div>
    </div>`).join('') +
    `<div class="modal-drop-hint">📁 Drag & drop your own image here, or use the "Custom poster" button above</div>`;
  els('.candidate-card').forEach(card => card.addEventListener('click', () => applyCandidate(candidates[+card.dataset.idx])));
}

async function applyCandidate(c) {
  const { kind, itemId, seasonItemId, pickerMode, item } = S.modal;

  let path;
  if (kind === 'collection') {
    path = `/collections/apply/${itemId}`;
  } else if (kind === 'background') {
    path = `/backgrounds/apply/item/${itemId}`;
  } else if (pickerMode === 'season' && seasonItemId) {
    path = `/posters/apply/season/${seasonItemId}`;
  } else {
    path = `/posters/apply/item/${itemId}`;
  }

  // Show spinner while uploading to Plex
  const grid = el('#modal-grid');
  const prev = grid.innerHTML;
  grid.innerHTML = `<div class="empty-state upload-spinner">
    <div class="spinner"></div>
    <div style="margin-top:12px;color:var(--text-secondary)">Uploading to ${srvName()}…</div>
  </div>`;

  try {
    await api(path, { method: 'POST', body: JSON.stringify({ imageUrl: c.imageUrl, source: c.source }) });
    toast(`${kind === 'background' ? 'Background' : kind === 'collection' ? 'Collection poster' : 'Poster'} applied to ${srvName()}.`);
    if (pickerMode === 'season' && el('#modal-match-set')?.checked) await applySeasonSet(seasonItemId, c, grid);

    // If we were in season picker mode, go back to show detail
    if (pickerMode === 'season' || pickerMode === 'show') {
      S.shows = []; await renderShows({ keepScroll: true });
      const updated = S.shows.find(s => s.id === itemId);
      if (updated) {
        showModalLayer('detail');
        el('#modal-back-btn').hidden = true;
        el('#modal-title').textContent = updated.title;
        renderShowDetail(updated);
      } else { closeModal(); }
    } else if (kind === 'collection') {
      closeModal();
      if (S.view === 'movie-collections') renderCollections('movie', { keepScroll: true });
      if (S.view === 'show-collections')  renderCollections('show',  { keepScroll: true });
    } else {
      closeModal();
      S.movies = []; S.shows = [];
      // Redraw the grid actually on screen — this used to redraw the *poster* grid while you were on a
      // Backgrounds page, so a newly chosen background never appeared until you navigated away.
      refreshCurrentGrid({ keepScroll: true });
    }
  } catch(e) { grid.innerHTML = prev; toast(`Failed: ${e.message}`, true); }
}

// "Match the other seasons": the posters from the same FanArt.tv set go on the show's other seasons, one at a time.
async function applySeasonSet(seasonItemId, c, grid) {
  if (c.source !== 1) { toast('Matching the other seasons works with FanArt.tv posters only — this one is from ' + sourceLabel(c.source) + '.', true); return; }
  let set;
  try { set = await api(`/posters/season-set/${seasonItemId}?imageUrl=${encodeURIComponent(c.imageUrl)}`); }
  catch (e) { toast(`Couldn't match the other seasons: ${e.message.replace(/^.*?→ \d+: /, '')}`, true); return; }
  let done = 0; const failed = [];
  for (const m of set.matches) {
    grid.innerHTML = `<div class="empty-state upload-spinner"><div class="spinner"></div>
      <div style="margin-top:12px;color:var(--text-secondary)">Matching season ${m.seasonNumber} (${done + 1} of ${set.matches.length})…</div></div>`;
    try {
      await api(`/posters/apply/season/${m.seasonId}`, { method: 'POST', body: JSON.stringify({ imageUrl: m.imageUrl, source: m.source }) });
      done++;
    } catch { failed.push(m.seasonNumber); }
  }
  const parts = [`Matched ${done} other season${done === 1 ? '' : 's'} from the same set`];
  if (set.missing.length) parts.push(`no matching poster for season ${set.missing.join(', ')}`);
  if (failed.length) parts.push(`couldn't apply season ${failed.join(', ')}`);
  toast(parts.join(' · '), failed.length > 0);
}

async function restoreArt(itemId, kind) {
  const path = kind === 'collection' ? `/collections/restore/${itemId}`
    : kind === 'background'          ? `/backgrounds/restore/item/${itemId}`
    : `/posters/restore/item/${itemId}`;
  try {
    const r = await api(path, { method: 'POST' });
    toast(r?.success ? `Original ${kind === 'collection' ? 'poster' : kind} restored.` : `No backup found.`, !r?.success);
    if (r?.success) {
      closeModal();
      if (kind !== 'collection') { S.movies = []; S.shows = []; }
      refreshCurrentGrid({ keepScroll: true });
    }
  } catch(e) { toast(`Restore failed: ${e.message}`, true); }
}

// Re-render whichever grid view is currently active (used by in-place actions that want
// to keep the user's scroll position via { keepScroll: true }).
function refreshCurrentGrid(opts = {}) {
  switch (S.view) {
    case 'movies':            return renderMovies(opts);
    case 'shows':             return renderShows(opts);
    case 'movie-backgrounds': return renderBackgrounds('movie', opts);
    case 'show-backgrounds':  return renderBackgrounds('show', opts);
    case 'movie-collections': return renderCollections('movie', opts);
    case 'show-collections':  return renderCollections('show', opts);
  }
}

// Where a custom upload should go, based on what the picker is currently showing. Seasons need
// their own endpoint — previously every drop was sent to the parent show.
function customUploadPath() {
  const m = S.modal;
  if (!m) return null;
  if (m.kind === 'collection') return `/collections/upload/${m.itemId}`;
  if (m.kind === 'background') return `/backgrounds/upload/item/${m.itemId}`;
  if (m.pickerMode === 'season' && m.seasonItemId) return `/posters/upload/season/${m.seasonItemId}`;
  return `/posters/upload/item/${m.itemId}`;
}

async function handleCustomPosterUpload(file) {
  const path = customUploadPath();
  if (!path) return;
  const grid = el('#modal-grid');
  const prev = grid.innerHTML;
  grid.innerHTML = `<div class="empty-state upload-spinner">
    <div class="spinner"></div>
    <div style="margin-top:12px;color:var(--text-secondary)">Uploading custom poster to ${srvName()}…</div>
  </div>`;

  try {
    const formData = new FormData();
    formData.append('file', file);

    const r = await fetch(`/api${path}`, { method: 'POST', body: formData });
    if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || `HTTP ${r.status}`);
    toast(`Custom poster applied to ${srvName()}!`);
    closeModal();
    S.movies = []; S.shows = [];
    refreshCurrentGrid({ keepScroll: true });   // redraw the grid on screen (Backgrounds pages included)
  } catch(e) { grid.innerHTML = prev; toast(`Upload failed: ${e.message}`, true); }
}

// Drag-and-drop onto the modal grid. Wired ONCE — re-wiring on every modal open stacked up
// duplicate listeners, so a single drop fired several concurrent uploads (flicker + failures).
let _dragDropWired = false;
function wireModalDragDrop() {
  const grid = el('#modal-grid');
  if (!grid || _dragDropWired) return;
  _dragDropWired = true;
  ['dragover','dragenter'].forEach(ev => grid.addEventListener(ev, e => {
    e.preventDefault();
    grid.classList.add('drag-over');
  }));
  ['dragleave','drop'].forEach(ev => grid.addEventListener(ev, e => {
    grid.classList.remove('drag-over');
  }));
  grid.addEventListener('drop', e => {
    e.preventDefault();
    const file = e.dataTransfer?.files?.[0];
    if (file && file.type.startsWith('image/') && S.modal) {
      handleCustomPosterUpload(file);
    }
  });
}

function closeModal() {
  el('#art-modal').hidden = true; S.modal = null;
  el('#modal-textless-only').checked = false;
  el('#modal-back-btn').hidden = true;
}

el('#modal-close').addEventListener('click', closeModal);
el('#art-modal').addEventListener('click', e => { if (e.target.id === 'art-modal') closeModal(); });
el('#modal-textless-only').addEventListener('change', () => renderCandidateGrid(S.modal?.kind || 'poster'));
el('#modal-match-set')?.addEventListener('change', e => { try { localStorage.setItem('matchSeasonSet', e.target.checked ? '1' : '0'); } catch { /* storage unavailable */ } });
el('#modal-back-btn').addEventListener('click', async () => {
  // Return to show detail from season picker
  el('#modal-back-btn').hidden = true;
  el('#modal-title').textContent = S.modal?.item?.title ?? '';
  showModalLayer('detail');
  if (!S.modal?.item) return;
  renderShowDetail(S.modal.item); // paint immediately from what we have
  // …then re-fetch so season posters changed during this visit aren't shown stale.
  const id = S.modal.itemId;
  S.shows = []; await renderShows({ keepScroll: true });
  const fresh = S.shows.find(s => s.id === id);
  if (fresh && S.modal) { S.modal.item = fresh; renderShowDetail(fresh); }
});

// ── Apply All ─────────────────────────────────────────────────────────────────
const APPLY_ALL_LABEL = `<svg viewBox="0 0 24 24" width="16" height="16"><path d="M5 12l5 5L20 7" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg> Apply All to `;
const applyAllLabel = () => APPLY_ALL_LABEL + srvName();

el('#apply-all-btn').addEventListener('click', async () => {
  const btn = el('#apply-all-btn');

  // Apply All is an explicit "push everything to Plex now" action: it re-applies every item that
  // has a poster/background, even ones already applied (force=true). This is what lets a change to
  // badges/labels take effect across the whole library — the server re-renders and re-uploads each
  // one. (The per-item Apply button and the on-scan auto-apply stay idempotent, so background work
  // doesn't pile up; only this button forces.)
  //
  // Fetch the FULL datasets from the server rather than reusing S.movies/S.shows: those are loaded
  // lazily per view, so if you clicked Apply All from the Movie Posters page, S.shows was empty and
  // TV posters/backgrounds/collections silently never got applied. Cover all six views here.
  btn.disabled = true;
  btn.textContent = 'Gathering…';
  let movies = [], shows = [], movieCols = [], showCols = [];
  try {
    [movies, shows, movieCols, showCols] = await Promise.all([
      api('/library/movies'), api('/library/shows'),
      api('/library/collections/movies'), api('/library/collections/shows'),
    ]);
  } catch (e) {
    btn.disabled = false; btn.innerHTML = applyAllLabel();
    toast(`Couldn't load library: ${e.message}`, true); return;
  }

  const jobs = [];
  for (const item of [...(movies ?? []), ...(shows ?? [])]) {
    if (item.currentPosterUrl)
      jobs.push({ path: `/posters/apply-current/item/${item.id}?force=true`, title: item.title });
    if (item.currentBackgroundUrl)
      jobs.push({ path: `/backgrounds/apply-current/item/${item.id}?force=true`, title: `${item.title} (background)` });
  }
  // Season posters too, so they get their badges (and badge-setting changes) like shows — they used
  // to be left out. Custom uploads have no source URL to re-render from, so they're left as they are.
  for (const show of shows ?? []) {
    for (const season of show.seasons ?? []) {
      if (/^https?:/i.test(season.currentPosterUrl || ''))
        jobs.push({ path: `/posters/apply-current/season/${season.id}`, title: `${show.title} – ${season.title}` });
    }
  }
  // Collections use their own endpoint (poster only, no overlays) and re-apply the stored current
  // poster by posting it back with its source.
  for (const col of [...(movieCols ?? []), ...(showCols ?? [])]) {
    if (col.currentPosterUrl)
      jobs.push({
        path: `/collections/apply/${col.id}`, title: `${col.title} (collection)`,
        body: { imageUrl: col.currentPosterUrl, source: SOURCE_MAP[col.currentPosterSource] ?? 0 },
      });
  }
  if (!jobs.length) {
    btn.disabled = false; btn.innerHTML = applyAllLabel();
    toast('Nothing to apply — no posters selected yet. Run a scan first.'); return;
  }

  const total = jobs.length;
  let done = 0, ok = 0, failed = 0;
  const failures = [];
  const tick = () => { btn.textContent = `Applying ${done}/${total}…`; };
  tick();

  // Run several at a time — each item takes ~3s (download art, render overlay, upload to Plex),
  // so a sequential pass over a large library ran for hours and never finished. Most of that is
  // network wait, so modest concurrency cuts it dramatically.
  let next = 0;
  async function worker() {
    while (next < jobs.length) {
      const job = jobs[next++];
      const opts = { method: 'POST' };
      if (job.body) opts.body = JSON.stringify(job.body);
      try { await api(job.path, opts); ok++; }
      catch (e) { failed++; if (failures.length < 3) failures.push(`${job.title}: ${e.message}`); }
      done++;
      if (done % 5 === 0 || done === total) tick();
    }
  }
  await Promise.all(Array.from({ length: 3 }, worker));

  btn.disabled = false;
  btn.innerHTML = applyAllLabel();
  if (failed) {
    console.warn('Apply All failures:', failures);
    toast(`Applied ${ok} of ${total} — ${failed} failed. First: ${failures[0]}`, true);
  } else {
    toast(`Applied ${ok} of ${total} to ${srvName()} (movies, TV, seasons, collections).`);
  }
  S.movies = []; S.shows = []; reloadCurrentView();
});

// ── Scan & Cancel ─────────────────────────────────────────────────────────────
el('#scan-btn').addEventListener('click', async () => {
  try {
    const r = await api('/library/scan', { method: 'POST' });
    toast(r?.status === 'already-running'
      ? 'A scan is already in progress — watch the progress bar below.'
      : 'Scan started.');
  }
  catch(e) { toast(`Could not start scan: ${e.message}`, true); }
});

el('#cancel-scan-btn').addEventListener('click', async () => {
  try { await api('/library/scan/cancel', { method: 'POST' }); toast('Cancelling scan…'); }
  catch(e) { toast(`Cancel failed: ${e.message}`, true); }
});

// ── Activity ──────────────────────────────────────────────────────────────────
// ── Health / Needs attention ────────────────────────────────────────────────
async function renderHealth() {
  const c = el('#view-health');
  c.innerHTML = '<div class="empty-state">Checking…</div>';
  let h;
  try { h = await api('/health'); }
  catch (e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; return; }

  const svcDot = s => s.configured === false ? '<span class="hs-dot hs-off" title="Not configured"></span>'
    : s.ok === true  ? '<span class="hs-dot hs-ok" title="Connected"></span>'
    : s.ok === false ? '<span class="hs-dot hs-bad" title="Could not connect"></span>'
    :                  '<span class="hs-dot hs-off" title="Not configured"></span>';
  const svcText = s => s.configured === false ? 'Not configured'
    : s.ok === true ? 'Connected' : s.ok === false ? 'Could not connect' : 'Not configured';

  const services = h.services.map(s =>
    `<div class="hs-row">${svcDot(s)}<span class="hs-name">${esc(s.name)}</span><span class="hs-state">${svcText(s)}</span></div>`
  ).join('');

  const last = h.scan?.lastFullScanUtc ? localTime(h.scan.lastFullScanUtc) : 'never';
  const scanLine = h.scan?.isScanning ? 'Scan in progress…' : `Last full scan: ${last}`;

  // Each issue group is collapsible; the header shows the count and links jump to the item's grid.
  const ISSUES = [
    ['noPoster',   'No poster',          'Items with no poster selected yet — pick one or run a scan.'],
    ['notApplied', `Not applied to ${srvName()}`,'A poster is chosen but hasn’t been pushed — use Apply or Apply All.'],
    ['noRatings',  'Missing ratings',    'No IMDb / Rotten Tomatoes / Audience score — those badges can’t show. Check OMDb & MDBList keys, then rescan.'],
    ['noId',       'No TMDb / TVDB id',  `Postarr couldn’t match these to a metadata provider, so it can’t fetch art or data. Often a ${srvName()} match problem.`],
  ];
  const groups = ISSUES.map(([key, title, desc]) => {
    const g = h.issues[key] || { total: 0, items: [] };
    const rows = g.items.map(it =>
      `<button class="hi-item" data-health-goto="${it.mediaType}" data-health-id="${it.id}">
         ${esc(it.title)}${it.year ? ` <span class="hi-year">(${it.year})</span>` : ''}
       </button>`).join('');
    const more = g.total > g.items.length ? `<div class="hi-more">…and ${g.total - g.items.length} more</div>` : '';
    return `<details class="hi-group"${g.total ? '' : ' data-empty="1"'}>
      <summary><span class="hi-count ${g.total ? 'has' : ''}">${g.total}</span> ${esc(title)}</summary>
      <div class="hi-desc">${esc(desc)}</div>
      <div class="hi-list">${rows || '<div class="hi-none">Nothing — all good ✓</div>'}${more}</div>
    </details>`;
  }).join('');

  // Libraries the media server no longer has (e.g. a temporary library from a recovery). Scans never prune
  // these on their own, so they'd linger as duplicates; the user removes them here.
  const stale = (h.staleLibraries || []).map(l => {
    const when = l.newestAddedUtc ? ` · newest added ${localTime(l.newestAddedUtc)}` : '';
    const cols = l.collections ? ` and ${l.collections} collection${l.collections === 1 ? '' : 's'}` : '';
    return `<div class="hst-lib">
      <div class="hst-head"><b>Library ${esc(l.sectionId)}</b> — ${l.items} item${l.items === 1 ? '' : 's'}${cols}${when}</div>
      <div class="hst-titles">${esc(l.sampleTitles.join(', '))}${l.items > l.sampleTitles.length ? '…' : ''}</div>
      <button class="btn-restore" data-stale-remove="${esc(l.sectionId)}" data-stale-count="${l.items}">Remove from Postarr</button>
    </div>`;
  }).join('');
  const staleCard = stale ? `
      <div class="hst-card">
        <div class="hst-title">Libraries no longer in ${esc(srvName())}</div>
        <div class="hi-desc">Postarr still has items from these libraries, but ${esc(srvName())} doesn’t list them any more — so they
          show up as duplicates or blank cards. Scans keep them in case the library comes back (a restore, a server that’s
          still starting). If it’s really gone, remove it here. Only Postarr’s records are removed; nothing on ${esc(srvName())} changes.</div>
        ${stale}
      </div>` : '';

  c.innerHTML = `
    <div class="health-wrap">
      <div class="health-card">
        <h3>Connections</h3>
        <div class="hs-list">${services}</div>
        <div class="hs-scan">${scanLine}</div>
      </div>
      <div class="health-card">
        <h3>Library — ${h.counts.movies} movies · ${h.counts.shows} shows · ${h.counts.collections} collections</h3>
        ${staleCard}
        <div class="hi-groups">${groups}</div>
      </div>
    </div>`;

  updateHealthBadge(h);
}

// ── Auto Collections ──────────────────────────────────────────────────────────
// Kometa-style collection sets: tick what you want, preview what the server would get, apply. The server only
// ever changes collections Postarr created itself.
const AC_REQUIRES = { mdblist: 'an MDBList key', tmdb: 'a TMDB key', omdb: 'an OMDb key' };
const AC_ACTIONS  = { create: 'Create', update: 'Update', unchanged: 'No change', delete: 'Remove', skip: 'Skip' };
let _acPoll = null;

// Per-set picks for the dynamic sets: { onlyListed, keys:Set }. onlyListed=false → keys are the ones left OUT.
let _acPicks = {};
function acPickSummary(key) {
  const p = _acPicks[key];
  const parts = [];
  if (!p || (!p.onlyListed && p.keys.size === 0)) parts.push('all of them');
  else parts.push(p.onlyListed ? `only ${p.keys.size} chosen` : `all except ${p.keys.size}`);
  const merges = Object.keys(p?.merges || {}).length;
  if (merges) parts.push(`${merges} merged`);
  if (p?.titleFormat) parts.push(`named “${p.titleFormat}”`);
  return parts.join(' · ');
}

async function renderAutoCollections() {
  const c = el('#view-auto-collections');
  c.innerHTML = '<div class="empty-state">Loading…</div>';
  let d;
  try { d = await api('/auto-collections'); }
  catch (e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; return; }
  _acCustom = (d.custom || []).map(x => ({ ...x }));
  _acCustomInfo = { hasMdbList: d.hasMdbList, hasTrakt: d.hasTrakt, hasTmdb: d.hasTmdb, customOrderSupported: d.customOrderSupported, server: d.server };
  _acPicks = {};
  d.sets.filter(s => s.dynamic).forEach(s => {
    _acPicks[s.key] = { onlyListed: !!s.onlyListed, keys: new Set(s.keys || []), merges: { ...(s.merges || {}) }, titleFormat: s.titleFormat || null };
  });

  const groups = [...new Set(d.sets.map(s => s.group))];
  const setRow = s => {
    const tags = [s.movies ? 'Movies' : null, s.shows ? 'TV' : null].filter(Boolean)
      .map(t => `<span class="ac-tag">${t}</span>`).join('');
    const blocked = !s.requirementMet;
    const min = s.defaultMinItems > 1
      ? `<label class="ac-min" title="Only make a collection when it would hold at least this many titles">min
           <input type="number" min="1" max="999" data-ac-min="${s.key}" value="${s.minItems ?? s.defaultMinItems}" /></label>` : '';
    const made = s.managedCount ? `<span class="ac-made">${s.managedCount} on ${esc(d.server)}</span>` : '';
    const choose = s.dynamic
      ? `<button type="button" class="ac-choose" data-ac-choose="${s.key}">Choose…</button><span class="ac-picked" data-ac-picked="${s.key}">${acPickSummary(s.key)}</span>` : '';
    return `<div class="overlay-tick ac-row${blocked ? ' ac-blocked' : ''}">
      <input type="checkbox" data-ac-key="${s.key}" ${s.enabled ? 'checked' : ''} ${blocked && !s.enabled ? 'disabled' : ''} />
      <div class="ac-body">
        <div class="overlay-tick-label">${esc(s.name)} ${tags} ${made}</div>
        <div class="overlay-tick-desc">${esc(s.description)}${blocked ? ` — <b>needs ${AC_REQUIRES[s.requires] || s.requires}</b> (Settings)` : ''}</div>
        ${choose ? `<div class="ac-choose-row">${choose}</div>` : ''}
      </div>${min}
    </div>`;
  };
  const awardHint = d.sets.some(s => s.group === 'Awards' && s.key !== 'award.best_picture' && s.enabled) && d.awardDataChecked < d.libraryTitles
    ? `<div class="ac-hint">Award data has been checked for ${d.awardDataChecked} of ${d.libraryTitles} titles so far — OMDb allows about 1,000 lookups a day, so the Oscar/Emmy collections fill in over the next few days.</div>` : '';

  c.innerHTML = `
    <div class="ac-wrap">
      <div class="settings-section">
        <h3>Collections built for you on ${esc(d.server)}</h3>
        <div class="section-desc">Tick the sets you want, preview what ${esc(d.server)} would get, then apply. Postarr only changes collections it made itself — yours are never touched, and if you already have one with the same name it's skipped. Untick a set and apply to remove its collections. New collections appear under Movie / Show Collections, where you can pick their posters.</div>
        <label class="ac-sync"><input type="checkbox" id="ac-sync-after-scan" ${d.syncAfterScan ? 'checked' : ''} />
          Keep them up to date after every library scan (charts change, new titles arrive)</label>
        <label class="ac-sync"><input type="checkbox" id="ac-kometa-images" ${d.useKometaImages ? 'checked' : ''} />
          <span>Use <a href="https://github.com/Kometa-Team/Default-Images" target="_blank" rel="noopener">Kometa's default posters</a> where one exists
          (each image is downloaded once from Kometa's GitHub and kept in Postarr's data folder, so later changes on GitHub don't affect your posters — their artwork, unlicensed, so off by default). Otherwise Postarr draws its own; franchises use TMDB's poster.
          A poster you pick yourself is never replaced.</span></label>
        <div class="ac-actions">
          <button class="btn-test" id="ac-save">Save choices</button>
          <button class="btn-test" id="ac-preview">Preview changes</button>
          <button class="btn-save" id="ac-apply">Apply to ${esc(d.server)}</button>
          <span class="ac-status" id="ac-status"></span>
        </div>
        ${awardHint}
      </div>
      <div class="settings-section">
        <h3>Your own collections</h3>
        <div class="section-desc">Build a collection from a list — an MDBList or TMDB list, or titles you type in order (Marvel movies in story order, a franchise watch order…). Only titles you own are added.</div>
        <div id="ac-custom-list"></div>
        <div class="ac-actions"><button class="btn-test" id="ac-custom-add">+ Add a collection</button></div>
      </div>
      ${groups.map(g => `<div class="settings-section"><h3>${esc(g)}</h3>
        <div class="overlay-grid ac-grid">${d.sets.filter(s => s.group === g).map(setRow).join('')}</div></div>`).join('')}
      <div class="settings-section" id="ac-results" hidden></div>
    </div>`;

  els('[data-ac-choose]').forEach(b => b.onclick = () => acOpenPicker(b.dataset.acChoose, d.sets.find(s => s.key === b.dataset.acChoose)));
  acRenderCustom();
  el('#ac-custom-add').onclick = () => acEditCustom(null);
  el('#ac-save').onclick    = async () => { if (await acSave()) toast('Collection choices saved'); };
  el('#ac-preview').onclick = acPreview;
  el('#ac-apply').onclick   = () => acApply(d.server);
  acPollStatus(true);   // a sync may already be running (e.g. after a scan)
}

// ── Custom collections: the user's own lists ─────────────────────────────────
// A typed list (in order), an MDBList list or a TMDB list → one collection, optionally kept in that order.
let _acCustom = [];
let _acCustomInfo = { hasMdbList: false, hasTrakt: false, hasTmdb: false, customOrderSupported: true, server: '' };

const AC_MCU_EXAMPLE = [
  'Captain America: The First Avenger (2011)', 'Captain Marvel (2019)', 'Iron Man (2008)', 'Iron Man 2 (2010)',
  'The Incredible Hulk (2008)', 'Thor (2011)', 'The Avengers (2012)', 'Thor: The Dark World (2013)', 'Iron Man 3 (2013)',
  'Captain America: The Winter Soldier (2014)', 'Guardians of the Galaxy (2014)', 'Guardians of the Galaxy Vol. 2 (2017)',
  'Avengers: Age of Ultron (2015)', 'Ant-Man (2015)', 'Captain America: Civil War (2016)', 'Black Widow (2021)',
  'Black Panther (2018)', 'Spider-Man: Homecoming (2017)', 'Doctor Strange (2016)', 'Thor: Ragnarok (2017)',
  'Ant-Man and the Wasp (2018)', 'Avengers: Infinity War (2018)', 'Avengers: Endgame (2019)',
  'Shang-Chi and the Legend of the Ten Rings (2021)', 'Eternals (2021)', 'Spider-Man: Far From Home (2019)',
  'Spider-Man: No Way Home (2021)', 'Doctor Strange in the Multiverse of Madness (2022)',
  'Black Panther: Wakanda Forever (2022)', 'Thor: Love and Thunder (2022)', 'Ant-Man and the Wasp: Quantumania (2023)',
  'Guardians of the Galaxy Vol. 3 (2023)', 'The Marvels (2023)', 'Deadpool & Wolverine (2024)',
  'Captain America: Brave New World (2025)', 'Thunderbolts* (2025)', 'The Fantastic Four: First Steps (2025)',
].join('\n');

const AC_SOURCES = { titles: 'Titles I type in order', mdblist: 'An MDBList list', trakt: 'A Trakt list', tmdb: 'A TMDB list' };
const AC_ORDERS  = { list: 'The list’s own order', release: 'Release date, oldest first', default: 'Leave it to the server (its own default)' };

function acCustomSummary(c) {
  const src = c.source === 'titles' ? `${(c.titles || '').split('\n').filter(l => l.trim() && !l.trim().startsWith('#')).length} typed titles`
            : c.source === 'mdblist' ? 'MDBList list' : c.source === 'trakt' ? 'Trakt list' : 'TMDB list';
  const kinds = [c.movies ? 'Movies' : null, c.shows ? 'TV' : null].filter(Boolean).join(' + ');
  return `${src} · ${kinds} · ${AC_ORDERS[c.order] || c.order}`;
}

function acRenderCustom() {
  const box = el('#ac-custom-list'); if (!box) return;
  box.innerHTML = _acCustom.length ? _acCustom.map((c, n) => `
    <div class="overlay-tick ac-row">
      <input type="checkbox" data-ac-custom-on="${n}" ${c.enabled ? 'checked' : ''} />
      <div class="ac-body">
        <div class="overlay-tick-label">${esc(c.name)} ${c.managedCount ? `<span class="ac-made">on ${esc(_acCustomInfo.server)}</span>` : ''}</div>
        <div class="overlay-tick-desc">${esc(acCustomSummary(c))}</div>
      </div>
      <button type="button" class="btn-test" data-ac-custom-edit="${n}">Edit</button>
      <button type="button" class="btn-test" data-ac-custom-del="${n}">Delete</button>
    </div>`).join('')
    : '<div class="ac-hint">None yet. Add one — for example “Marvel movies in story order”.</div>';
  els('[data-ac-custom-on]').forEach(cb => cb.onchange = async () => {
    _acCustom[+cb.dataset.acCustomOn].enabled = cb.checked; await acSaveCustom();
  });
  els('[data-ac-custom-edit]').forEach(b => b.onclick = () => acEditCustom(+b.dataset.acCustomEdit));
  els('[data-ac-custom-del]').forEach(b => b.onclick = async () => {
    const c = _acCustom[+b.dataset.acCustomDel];
    if (!confirm(`Delete “${c.name}”?\n\nIts collection is removed from ${_acCustomInfo.server} the next time you apply (or after the next scan).`)) return;
    _acCustom.splice(+b.dataset.acCustomDel, 1); await acSaveCustom(); acRenderCustom();
  });
}

async function acSaveCustom() {
  try {
    const r = await api('/auto-collections/custom', { method: 'POST', body: JSON.stringify(_acCustom) });
    _acCustom.forEach((c, n) => { if (r.custom[n]) c.id = r.custom[n].id; });
    return true;
  } catch (e) { toast(e.message, true); return false; }
}

// Search MDBList's public lists (or show its top ones) and pick one to follow or copy.
function acBrowseLists({ provider = 'mdblist', follow, copy }) {
  const pname = provider === 'trakt' ? 'Trakt' : 'MDBList';
  const ov = document.createElement('div');
  ov.className = 'modal-overlay ac-picker-overlay ac-browse-overlay';
  ov.innerHTML = `<div class="modal ac-picker">
    <header class="modal-header"><div><h2>Community lists — ${pname}</h2>
      <div class="modal-subtitle">Public lists on ${pname}. <b>Follow</b> keeps a collection in step with the list; <b>Copy titles</b> takes a snapshot you can edit.</div></div>
      <button class="modal-close" data-bl-close>&times;</button></header>
    <div class="ac-picker-tools"><input type="search" placeholder="Search lists — e.g. marvel chronological" data-bl-q autofocus /></div>
    <div class="ac-picker-list" data-bl-list><div class="empty-state">Loading…</div></div></div>`;
  document.body.appendChild(ov);
  const list = ov.querySelector('[data-bl-list]');
  const close = () => ov.remove();
  ov.querySelector('[data-bl-close]').onclick = close;
  ov.addEventListener('click', e => { if (e.target === ov) close(); });

  let shown = [], seq = 0;
  const load = async q => {
    const mine = ++seq;
    list.innerHTML = '<div class="empty-state">Searching…</div>';
    try {
      const r = await api(`/auto-collections/community-lists?source=${provider}${q ? `&query=${encodeURIComponent(q)}` : ''}`);
      if (mine !== seq || !ov.isConnected) return;   // a newer search superseded this one
      shown = r;
      list.innerHTML = r.length ? r.map((l, n) => `
        <div class="ac-bl-row">
          <div class="ac-bl-main">
            <div class="ac-bl-name">${esc(l.name)}</div>
            <div class="ac-bl-meta">${l.userName ? `by ${esc(l.userName)} · ` : ''}${l.items.toLocaleString()} titles${l.likes ? ` · ♥ ${l.likes.toLocaleString()}` : ''}</div>
            ${l.description ? `<div class="ac-bl-desc">${esc(l.description.length > 160 ? l.description.slice(0, 160) + '…' : l.description)}</div>` : ''}
          </div>
          <div class="ac-bl-actions">
            <button type="button" class="btn-save" data-bl-follow="${n}">Follow</button>
            <button type="button" class="btn-test" data-bl-copy="${n}">Copy titles</button>
          </div>
        </div>`).join('') : '<div class="empty-state">No public lists match that.</div>';
    } catch (e) { if (mine === seq) list.innerHTML = `<div class="empty-state">${esc(e.message.replace(/^.*?→ \d+: /, ''))}</div>`; }
  };
  list.addEventListener('click', e => {
    const fb = e.target.closest('[data-bl-follow]'), cb = e.target.closest('[data-bl-copy]');
    if (fb) { close(); follow(shown[+fb.dataset.blFollow]); }
    else if (cb) { close(); copy(shown[+cb.dataset.blCopy]); }
  });
  let t; const qEl = ov.querySelector('[data-bl-q]');
  qEl.oninput = () => { clearTimeout(t); t = setTimeout(() => load(qEl.value.trim()), 350); };
  load('');
}

function acEditCustom(n) {
  const c = n == null
    ? { id: '', name: '', enabled: true, source: 'titles', listRef: '', titles: '', movies: true, shows: false, order: 'list' }
    : { ..._acCustom[n] };
  const info = _acCustomInfo;
  const ov = document.createElement('div');
  ov.className = 'modal-overlay ac-picker-overlay';
  ov.innerHTML = `<div class="modal ac-picker ac-custom-modal">
    <header class="modal-header"><div><h2>${n == null ? 'New collection' : 'Edit collection'}</h2>
      <div class="modal-subtitle">Only titles you already have are added, in the order you choose.</div></div>
      <button class="modal-close" data-cc-close>&times;</button></header>
    <div class="ac-custom-form">
      ${n == null ? `<label class="ac-field">Start from <select data-cc="example">
          <option value="">Blank</option><option value="mcu">Marvel movies — story order (a starting point)</option></select></label>` : ''}
      <label class="ac-field">Name <input type="text" data-cc="name" maxlength="100" value="${esc(c.name)}" placeholder="Marvel — story order" /></label>
      <label class="ac-field">Where do the titles come from?
        <select data-cc="source">${Object.entries(AC_SOURCES).map(([k, v]) => `<option value="${k}" ${c.source === k ? 'selected' : ''}>${v}</option>`).join('')}</select></label>
      <label class="ac-field" data-cc-row="ref">List address
        <input type="text" data-cc="listRef" maxlength="500" value="${esc(c.listRef)}" />
        <small data-cc="refHelp"></small></label>
      <div class="ac-ref-tools" data-cc-row="reftools">
        <button type="button" class="btn-test" data-cc-browse>Browse community lists…</button>
        <button type="button" class="btn-test" data-cc-copy title="Save the list’s titles into the box below as plain text you can edit — it will no longer follow the list">Copy its titles instead</button>
      </div>
      <label class="ac-field" data-cc-row="titles">Titles, one per line, in the order you want
        <textarea data-cc="titles" rows="9" spellcheck="false" placeholder="Iron Man (2008)&#10;Iron Man 2 (2010)&#10;tmdb:1726&#10;imdb:tt0371746">${esc(c.titles)}</textarea>
        <small>“Title (Year)”, “tmdb:123” or “imdb:tt1234567”. Lines starting with # are ignored.</small></label>
      <div class="ac-field">Include
        <span class="ac-inline"><label><input type="checkbox" data-cc="movies" ${c.movies ? 'checked' : ''} /> Movies</label>
        <label><input type="checkbox" data-cc="shows" ${c.shows ? 'checked' : ''} /> TV shows</label></span></div>
      <label class="ac-field">Order
        <select data-cc="order">${Object.entries(AC_ORDERS).map(([k, v]) => `<option value="${k}" ${c.order === k ? 'selected' : ''}>${v}</option>`).join('')}</select>
        <small data-cc="orderHelp"></small></label>
      <div class="ac-cc-check" data-cc="result"></div>
    </div>
    <div class="ac-picker-foot">
      <button type="button" class="btn-test" data-cc-check>Check list</button>
      <button type="button" class="btn-save" data-cc-save>Save</button>
    </div></div>`;
  document.body.appendChild(ov);
  const f = k => ov.querySelector(`[data-cc="${k}"]`);
  const close = () => ov.remove();
  ov.querySelector('[data-cc-close]').onclick = close;
  ov.addEventListener('click', e => { if (e.target === ov) close(); });

  const sync = () => {
    const src = f('source').value;
    ov.querySelector('[data-cc-row="ref"]').hidden = src === 'titles';
    ov.querySelector('[data-cc-row="titles"]').hidden = src !== 'titles';
    ov.querySelector('[data-cc-row="reftools"]').hidden = src === 'titles';
    ov.querySelector('[data-cc-browse]').hidden = src !== 'mdblist' && src !== 'trakt';
    f('listRef').placeholder = src === 'mdblist' ? 'https://mdblist.com/lists/username/list-name'
      : src === 'trakt' ? 'https://trakt.tv/users/username/lists/list-name' : 'https://www.themoviedb.org/list/8136';
    const have = src === 'mdblist' ? info.hasMdbList : src === 'trakt' ? info.hasTrakt : info.hasTmdb;
    const need = src === 'mdblist' ? 'an MDBList key' : src === 'trakt' ? 'a Trakt Client ID' : 'a TMDB key';
    f('refHelp').textContent = have ? 'Public lists only. Keeps the list’s own order.' : `Needs ${need} in Settings.`;
    f('orderHelp').textContent = f('order').value === 'list' && !info.customOrderSupported
      ? `${info.server} can’t keep a custom order — it only sorts collections by release date or A–Z. The titles are still added, shown by release date.` : '';
  };
  f('source').onchange = sync; f('order').onchange = sync; sync();
  // Copy a list's titles into the typed box (a snapshot you can edit), instead of following the list.
  const copyFrom = async (source, listRef, name) => {
    try {
      const r = await api('/auto-collections/custom/import', { method: 'POST', body: JSON.stringify({ source, listRef, name: 'import', movies: true, shows: true }) });
      f('source').value = 'titles'; f('titles').value = r.titles;
      if (name && !f('name').value.trim()) f('name').value = name;
      sync(); toast(`Copied ${r.count} titles — edit the list however you like`);
      ov.querySelector('[data-cc-check]').click();
    } catch (e) { toast(e.message, true); }
  };
  ov.querySelector('[data-cc-copy]').onclick = () => {
    if (!f('listRef').value.trim()) { toast('Paste a list address first.', true); return; }
    copyFrom(f('source').value, f('listRef').value.trim(), '');
  };
  ov.querySelector('[data-cc-browse]').onclick = () => {
    const provider = f('source').value === 'trakt' ? 'trakt' : 'mdblist';
    acBrowseLists({
      provider,
      follow: l => {
        f('source').value = provider; f('listRef').value = l.url || String(l.id);
        if (!f('name').value.trim()) f('name').value = l.name;
        sync(); ov.querySelector('[data-cc-check]').click();
      },
      copy: l => copyFrom(provider, l.url || String(l.id), l.name),
    });
  };

  if (f('example')) f('example').onchange = () => {
    if (f('example').value !== 'mcu') return;
    f('name').value = 'Marvel — story order'; f('source').value = 'titles'; f('titles').value = AC_MCU_EXAMPLE;
    f('movies').checked = true; f('shows').checked = false; f('order').value = 'list'; sync();
  };

  const read = () => ({
    id: c.id, enabled: c.enabled, name: f('name').value.trim(), source: f('source').value, listRef: f('listRef').value.trim(),
    titles: f('titles').value, movies: f('movies').checked, shows: f('shows').checked, order: f('order').value,
  });
  ov.querySelector('[data-cc-check]').onclick = async () => {
    const out = f('result'); out.innerHTML = '<div class="ac-hint">Reading the list…</div>';
    try {
      const r = await api('/auto-collections/custom/check', { method: 'POST', body: JSON.stringify(read()) });
      if (r.error) { out.innerHTML = `<div class="ac-hint ac-err">${esc(r.error)}</div>`; return; }
      out.innerHTML = `<div class="ac-hint"><b>${r.found} of ${r.total}</b> titles are in your library.` +
        (r.missing.length ? `<br>Not found: ${esc(r.missing.slice(0, 12).join(', '))}${r.missing.length > 12 ? ` and ${r.missing.length - 12} more` : ''}` : '') +
        (r.note ? `<br><b>Note:</b> ${esc(r.note)}.` : '') +
        `<br><small>Titles are matched by ID where the list has one, otherwise by name and year (±1).</small></div>`;
    } catch (e) { out.innerHTML = `<div class="ac-hint ac-err">${esc(e.message)}</div>`; }
  };
  ov.querySelector('[data-cc-save]').onclick = async () => {
    const v = read();
    if (!v.name) { toast('Give the collection a name.', true); return; }
    if (v.source === 'titles' ? !v.titles.trim() : !v.listRef) { toast(v.source === 'titles' ? 'Add some titles.' : 'Paste the list address.', true); return; }
    if (n == null) _acCustom.push({ ...v, managedCount: 0 }); else _acCustom[n] = { ..._acCustom[n], ...v };
    if (await acSaveCustom()) { close(); acRenderCustom(); toast('Saved — use Preview or Apply to see it on ' + info.server); }
  };
}

function acChoices() {
  return {
    syncAfterScan: el('#ac-sync-after-scan')?.checked ?? true,
    useKometaImages: el('#ac-kometa-images')?.checked ?? false,
    sets: els('[data-ac-key]').map(cb => ({
      key: cb.dataset.acKey, enabled: cb.checked,
      minItems: parseInt(el(`[data-ac-min="${cb.dataset.acKey}"]`)?.value) || null,
      onlyListed: _acPicks[cb.dataset.acKey]?.onlyListed || false,
      keys: [...(_acPicks[cb.dataset.acKey]?.keys || [])],
      merges: _acPicks[cb.dataset.acKey]?.merges || {},
      titleFormat: _acPicks[cb.dataset.acKey]?.titleFormat || null,
    })),
  };
}

// "Choose…": every collection the set could make from this library, ticked = made.
async function acOpenPicker(key, set) {
  const pick = _acPicks[key] || { onlyListed: false, keys: new Set(), merges: {}, titleFormat: null };
  const min  = parseInt(el(`[data-ac-min="${key}"]`)?.value) || '';
  const ov = document.createElement('div');
  ov.className = 'modal-overlay ac-picker-overlay';
  ov.innerHTML = `<div class="modal ac-picker">
    <header class="modal-header"><div><h2>${esc(set.name)}</h2>
      <div class="modal-subtitle">Tick the collections you want. Numbers are titles in your library; ones under the set’s minimum are hidden unless you ask.</div></div>
      <button class="modal-close" data-ac-close>&times;</button></header>
    <div class="ac-picker-tools">
      <input type="search" placeholder="Filter…" data-ac-filter />
      <button type="button" class="btn-test" data-ac-all>Tick all</button>
      <button type="button" class="btn-test" data-ac-none>Untick all</button>
      <label class="ac-under-toggle" data-ac-under-wrap hidden><input type="checkbox" /> Also show <span data-ac-under-n></span> below the minimum</label>
    </div>
    <div class="ac-picker-list" data-ac-list><div class="empty-state">Finding them in your library…</div></div>
    <div class="ac-picker-foot">
      <label class="ac-format">Collection name <input type="text" data-ac-format maxlength="120"
        placeholder="${esc(set.defaultTitleFormat)}" value="${esc(pick.titleFormat || '')}" />
        <small>{name} is the value — e.g. “{name} Movies”. Leave blank for “${esc(set.defaultTitleFormat)}”.</small></label>
      <label><input type="radio" name="ac-new" value="auto" ${pick.onlyListed ? '' : 'checked'} /> New ones (a new studio, genre…) join automatically</label>
      <label><input type="radio" name="ac-new" value="off" ${pick.onlyListed ? 'checked' : ''} /> New ones stay out until I tick them</label>
      <button type="button" class="btn-save" data-ac-done>Done</button>
    </div></div>`;
  document.body.appendChild(ov);
  const close = () => ov.remove();
  ov.querySelector('[data-ac-close]').onclick = close;
  ov.addEventListener('click', e => { if (e.target === ov) close(); });

  let values;
  const listEl = ov.querySelector('[data-ac-list]');
  try {
    values = await acWhenReady(`/auto-collections/values/${encodeURIComponent(key)}${min ? `?minItems=${min}` : ''}`, {},
      t => {
        if (!ov.isConnected) throw new Error('closed');   // dialog dismissed — stop polling (the fetch carries on server-side)
        listEl.innerHTML = `<div class="empty-state">${esc(t)}<br><small>This only happens once — you can close this and come back.</small></div>`;
      });
  }
  catch (e) { ov.querySelector('[data-ac-list]').innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; return; }
  const isIn = k => pick.onlyListed ? pick.keys.has(k) : !pick.keys.has(k);
  const list = ov.querySelector('[data-ac-list]');
  // Every choice lives in this state, not in the page: a set like Actors has thousands of values, so only some are
  // drawn (an earlier version drew a "merge into" menu of every value on every row and froze the browser).
  const state  = new Map(values.map(v => [v.key, isIn(v.key)]));
  const merges = { ...(pick.merges || {}) };
  const targets = [...new Set([...values.map(v => v.title), ...Object.values(merges)])].sort((a, b) => a.localeCompare(b));
  const canMerge = values.length <= 300;   // merging thousands of actors isn't meaningful (and would be enormous)
  const under = values.filter(v => !v.meetsMinimum).length;
  let showAll = false, query = '';
  const MAX_ROWS = 1000;
  const matching = () => values.filter(v => (showAll || v.meetsMinimum) && (!query || v.title.toLowerCase().includes(query)));
  const mergeSel = v => !canMerge ? '' : `<select class="ac-merge" data-merge-for="${esc(v.key)}" title="Merge into another collection">
      <option value="">—</option>${targets.filter(t => t.toLowerCase() !== v.title.toLowerCase())
        .map(t => `<option value="${esc(t)}" ${merges[v.key] === t ? 'selected' : ''}>→ ${esc(t)}</option>`).join('')}</select>`;
  const renderList = () => {
    const rows = matching(), shown = rows.slice(0, MAX_ROWS);
    list.innerHTML = (shown.length ? shown.map(v => `
    <div class="ac-val${v.meetsMinimum ? '' : ' ac-under'}"
           title="${v.meetsMinimum ? '' : 'Below the minimum on its own — won’t be made unless you lower it (or merge it)'}">
      <label class="ac-val-main"><input type="checkbox" value="${esc(v.key)}" ${state.get(v.key) ? 'checked' : ''} />
      <span class="ac-val-name">${esc(v.title)}</span><span class="ac-val-n">${v.count}</span></label>${mergeSel(v)}</div>`).join('')
      : `<div class="empty-state">${values.length ? 'Nothing matches.' : 'Nothing in your library for this set yet.'}</div>`)
      + (rows.length > shown.length ? `<div class="ac-hint">Showing the first ${MAX_ROWS} of ${rows.length} — type in the filter to find the rest.</div>` : '');
  };
  const underWrap = ov.querySelector('[data-ac-under-wrap]');
  if (under > 0) {
    underWrap.hidden = false; underWrap.querySelector('[data-ac-under-n]').textContent = under;
    underWrap.querySelector('input').onchange = e => { showAll = e.target.checked; renderList(); };
  }
  list.addEventListener('change', e => {
    const t = e.target;
    if (t.matches('input[type=checkbox]')) state.set(t.value, t.checked);
    else if (t.matches('[data-merge-for]')) { if (t.value) merges[t.dataset.mergeFor] = t.value; else delete merges[t.dataset.mergeFor]; }
  });
  renderList();

  let filterTimer;
  ov.querySelector('[data-ac-filter]').oninput = e => {
    clearTimeout(filterTimer);
    filterTimer = setTimeout(() => { query = e.target.value.trim().toLowerCase(); renderList(); }, 150);
  };
  const setAll = on => { matching().forEach(v => state.set(v.key, on)); renderList(); };
  ov.querySelector('[data-ac-all]').onclick  = () => setAll(true);
  ov.querySelector('[data-ac-none]').onclick = () => setAll(false);
  ov.querySelector('[data-ac-done]').onclick = async () => {
    const onlyListed = ov.querySelector('input[name=ac-new]:checked').value === 'off';
    // Keep picks for values not in the library right now (e.g. a studio whose films were removed).
    const shown = new Set(values.map(v => v.key));
    const kept  = [...pick.keys].filter(k => !shown.has(k) && onlyListed === pick.onlyListed);
    const keys  = values.filter(v => onlyListed ? state.get(v.key) : !state.get(v.key)).map(v => v.key);
    const outMerges = {};
    Object.entries(pick.merges || {}).forEach(([k, v]) => { if (!shown.has(k)) outMerges[k] = v; });   // values not in the library now
    Object.entries(merges).forEach(([k, v]) => { if (v && shown.has(k)) outMerges[k] = v; });
    const fmt = ov.querySelector('[data-ac-format]').value.trim();
    if (fmt && !fmt.includes('{name}')) { toast('The collection name needs {name} in it (or leave it blank).', true); return; }
    _acPicks[key] = { onlyListed, keys: new Set([...kept, ...keys]), merges: outMerges, titleFormat: fmt || null };
    const s = el(`[data-ac-picked="${key}"]`); if (s) s.textContent = acPickSummary(key);
    close();
    if (await acSave()) toast(`${set.name}: choices saved — Preview or Apply to see the effect`);
  };
}

async function acSave() {
  try { await api('/auto-collections/config', { method: 'POST', body: JSON.stringify(acChoices()) }); return true; }
  catch (e) { toast(e.message, true); return false; }
}

// A first-time run on a big library fetches TMDB details for minutes — longer than a proxy (e.g. Cloudflare) waits
// on one request — so the server does it in the background and answers { preparing } until it's done.
async function acWhenReady(path, opts, onProgress) {
  for (;;) {
    const r = await api(path, opts);
    if (!r || !r.preparing) return r;
    onProgress(r.progress || 'Fetching genre, franchise, country & cast data from TMDB (first time only)…');
    await new Promise(res => setTimeout(res, 2500));
  }
}

function acBusy(on, text) {
  ['#ac-save', '#ac-preview', '#ac-apply'].forEach(s => { const b = el(s); if (b) b.disabled = on; });
  const st = el('#ac-status'); if (st) st.textContent = text || '';
}

async function acPreview() {
  if (!await acSave()) return;
  acBusy(true, 'Working it out… (the first time can take a minute while genre data is fetched)');
  try {
    const r = await acWhenReady('/auto-collections/preview', { method: 'POST' }, t => acBusy(true, t));
    acShowResults(r.rows, r.notes, [], 'Preview — nothing has been changed yet');
  } catch (e) { toast(e.message, true); }
  finally { acBusy(false); }
}

async function acApply(server) {
  if (!await acSave()) return;
  if (!confirm(`Create, update and remove Postarr's collections on ${server} now?\n\nOnly collections Postarr made are changed — yours are left alone. Tip: use Preview first to see exactly what will happen.`)) return;
  let before = null;
  try {
    before = (await api('/auto-collections/status')).lastRunUtc;
    await api('/auto-collections/sync', { method: 'POST' });
  } catch (e) { toast(e.message, true); return; }
  acBusy(true, 'Starting…');
  acPollStatus(false, before);
}

// sinceRun: the lastRunUtc from before an Apply — the background sync starts a moment after the request
// returns, so keep waiting until a NEW run has finished rather than reporting the previous one.
function acPollStatus(quietIfIdle, sinceRun) {
  clearTimeout(_acPoll);
  let idleWaits = 0;
  const tick = async () => {
    let st;
    try { st = await api('/auto-collections/status'); } catch { return; }
    if (S.view !== 'auto-collections') return;
    const notStartedYet = sinceRun !== undefined && st.lastRunUtc === sinceRun && !st.isRunning && ++idleWaits < 20;
    if (st.isRunning || notStartedYet) {
      acBusy(true, st.progress || 'Working…');
      _acPoll = setTimeout(tick, 1500);
      quietIfIdle = false;
      return;
    }
    acBusy(false);
    if (!quietIfIdle) {
      acShowResults(st.rows, [], st.errors, `Applied ${st.lastRunUtc ? localTime(st.lastRunUtc) : ''}`);
      toast(st.errors?.length ? `Collections synced with ${st.errors.length} problem(s)` : 'Collections synced', !!st.errors?.length);
      renderAutoCollectionsCounts();
    }
  };
  tick();
}

// Refresh the "N on server" counts without redrawing the ticks the user may be editing.
async function renderAutoCollectionsCounts() {
  try {
    const d = await api('/auto-collections');
    d.sets.forEach(s => {
      const row = el(`[data-ac-key="${s.key}"]`)?.closest('.ac-row');
      const lbl = row?.querySelector('.overlay-tick-label');
      if (!lbl) return;
      lbl.querySelector('.ac-made')?.remove();
      if (s.managedCount) lbl.insertAdjacentHTML('beforeend', `<span class="ac-made">${s.managedCount} on ${esc(d.server)}</span>`);
    });
  } catch { /* cosmetic */ }
}

function acShowResults(rows, notes, errors, heading) {
  const box = el('#ac-results'); if (!box) return;
  rows = rows || [];
  const count = a => rows.filter(r => r.action === a).length;
  const summary = ['create', 'update', 'delete', 'skip', 'unchanged'].filter(a => count(a))
    .map(a => `<span class="ac-chip ac-${a}">${count(a)} ${AC_ACTIONS[a].toLowerCase()}</span>`).join(' ');
  const order = { create: 0, update: 1, delete: 2, skip: 3, unchanged: 4 };
  const body = rows.slice().sort((a, b) => order[a.action] - order[b.action] || a.setName.localeCompare(b.setName) || a.title.localeCompare(b.title))
    .map(r => `<tr>
      <td><span class="ac-chip ac-${r.action}">${AC_ACTIONS[r.action] || r.action}</span></td>
      <td>${esc(r.title)}</td><td class="ac-dim">${esc(r.setName)}</td><td class="ac-dim">${esc(r.section || '')}</td>
      <td class="ac-num">${r.count}</td>
      <td class="ac-dim">${r.action === 'update' ? `+${r.adds} / −${r.removes}` : ''}${r.note ? esc(r.note) : ''}</td></tr>`).join('');
  box.hidden = false;
  box.innerHTML = `<h3>${esc(heading)}</h3>
    <div class="ac-summary">${summary || 'Nothing to do — no sets are ticked and no Postarr collections exist.'}</div>
    ${(notes || []).map(n => `<div class="ac-hint">${esc(n)}</div>`).join('')}
    ${(errors || []).map(e => `<div class="ac-hint ac-err">${esc(e)}</div>`).join('')}
    ${body ? `<div class="ac-table-wrap"><table class="ac-table"><thead><tr><th></th><th>Collection</th><th>Set</th><th>Library</th><th class="ac-num">Titles</th><th></th></tr></thead><tbody>${body}</tbody></table></div>` : ''}`;
  box.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

// Sidebar badge = total items needing a poster or an apply. Silent when clean.
function updateHealthBadge(h) {
  const n = (h?.issues?.noPoster?.total || 0) + (h?.issues?.notApplied?.total || 0);
  const b = el('#health-badge');
  if (!b) return;
  b.textContent = n > 999 ? '999+' : String(n);
  b.hidden = n === 0;
}

// Health → "Libraries no longer in <server>" → Remove.
document.addEventListener('click', async e => {
  const b = e.target.closest('[data-stale-remove]');
  if (!b) return;
  const id = b.dataset.staleRemove, n = b.dataset.staleCount;
  if (!confirm(`Remove library ${id} from Postarr?

Its ${n} item(s), their chosen posters and stored original-poster backups are removed from Postarr. ` +
               `Nothing changes on ${srvName()}, and items in your current libraries are not touched.`)) return;
  b.disabled = true; b.textContent = 'Removing…';
  try {
    const r = await api(`/health/stale-libraries/${encodeURIComponent(id)}/remove`, { method: 'POST' });
    toast(`Removed ${r.items} item(s)${r.collections ? ` and ${r.collections} collection(s)` : ''} from library ${id}.`);
    S.movies = []; S.shows = [];   // grids reload without the removed items
    renderHealth();
  } catch (err) {
    toast(err.message, true);
    b.disabled = false; b.textContent = 'Remove from Postarr';
  }
});

// Jump from a Health list item straight to its grid, filtered so it's easy to find.
document.addEventListener('click', e => {
  const goto = e.target.closest('[data-health-goto]');
  if (!goto) return;
  const view = goto.dataset.healthGoto === 'movie' ? 'movies' : 'shows';
  setView(view);
  // Drop a title filter so the grid narrows to roughly what they clicked.
});

async function renderActivity() {
  const c = el('#view-activity');
  c.innerHTML = `<div class="activity-live-header"><span class="live-dot" style="display:inline-block;flex-shrink:0"></span>Live feed — updates in real time</div><div class="activity-list" id="activity-list"></div>`;
  try {
    S.activity = await api('/library/activity?take=200') ?? [];
    el('#activity-list').innerHTML = S.activity.map(activityRowHtml).join('');
  } catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}
function appendActivityRow(e) { const list = el('#activity-list'); if (list) list.insertAdjacentHTML('afterbegin', activityRowHtml(e)); }
// Timestamps are stored UTC, but SQLite loses DateTimeKind so the API returns them with no
// "Z" — JS would then parse them as local time and show the wrong hour. Treat a marker-less
// timestamp as UTC, then render in the user's local timezone.
function localTime(ts) {
  if (!ts) return '';
  const s = /[Zz]$|[+-]\d{2}:?\d{2}$/.test(ts) ? ts : ts + 'Z';
  const d = new Date(s);
  return isNaN(d) ? String(ts) : d.toLocaleString();
}
function activityRowHtml(e) {
  return `<div class="activity-row level-${e.level}"><div class="timestamp">${localTime(e.timestampUtc)}</div><div class="message">${esc(e.message)}</div></div>`;
}

// ── Settings ──────────────────────────────────────────────────────────────────
async function renderSettings() {
  const c = el('#view-settings'); c.innerHTML = '<div class="empty-state">Loading…</div>';
  try { S.settings = await api('/settings'); c.innerHTML = settingsHtml(S.settings); wireSettings(); }
  catch(e) { c.innerHTML = `<div class="empty-state">Error: ${esc(e.message)}</div>`; }
}

function ov(k) { return S.settings?.overlays?.[k] ? 'checked' : ''; }

function settingsHtml(s) {
  const ovGrid = OVERLAY_TICKS.map(([k,lbl,desc]) => `
    <div class="overlay-tick">
      <input type="checkbox" id="ov-${k}" ${ov(k)} />
      <div><div class="overlay-tick-label">${lbl}</div><div class="overlay-tick-desc">${desc}</div></div>
    </div>`).join('');

  const themePrimary = s.themePrimary || '#10bec9';
  const themeAccent  = s.themeAccent  || '#f2586e';
  const THEME_PRESETS = [
    ['Postarr',        '#10bec9', '#f2586e'],
    ['Grape · Lime',   '#8b5cf6', '#84cc16'],
    ['Ocean · Amber',  '#2f9be0', '#f0a825'],
    ['Emerald · Rose', '#10b981', '#fb7185'],
    ['Slate · Gold',   '#64748b', '#eab308'],
    ['Magenta · Cyan', '#e0409e', '#22d3ee'],
  ];
  const presetHtml = THEME_PRESETS.map(([name,p,a]) =>
    `<button type="button" class="theme-preset" data-p="${p}" data-a="${a}">
       <span class="tp-sw" style="background:linear-gradient(90deg,${p},${a})"></span>${name}</button>`).join('');

  return `<div class="settings-with-preview">
  <div class="settings-form" id="settings-form-inner">

    <div class="settings-section">
      <h3>Appearance</h3>
      <div class="section-desc">Two colours from the logo drive the whole interface — and the logo re-tints to match, keeping its shading. Changes preview live; click Save to keep them.</div>
      <div class="theme-row">
        <div class="theme-logo-wrap"><img class="theme-logo-preview" src="${LOGO_SRC}" alt="logo preview" /></div>
        <div class="theme-pickers">
          <div class="theme-pick"><input type="color" id="set-theme-primary" value="${esc(themePrimary)}"><label>Primary<small>buttons, active nav, film strip</small></label></div>
          <div class="theme-pick"><input type="color" id="set-theme-accent" value="${esc(themeAccent)}"><label>Accent<small>badges, dismiss, the P &amp; star</small></label></div>
          <button type="button" class="btn-restore" id="theme-reset" style="margin-top:6px">↺ Reset to logo colours</button>
        </div>
      </div>
      <div class="theme-presets">${presetHtml}</div>
    </div>

    <div class="settings-section">
      <h3>Media Server</h3>
      <div class="section-desc">Which server Postarr manages, and how to reach it.</div>
      <div class="field-row"><label>Server type</label>
        <select id="set-server-type">
          <option value="Plex" ${['Jellyfin','Emby'].includes(s.mediaServerType) ? '' : 'selected'}>Plex</option>
          <option value="Jellyfin" ${s.mediaServerType === 'Jellyfin' ? 'selected' : ''}>Jellyfin</option>
          <option value="Emby" ${s.mediaServerType === 'Emby' ? 'selected' : ''}>Emby</option>
        </select></div>
      <div id="server-plex" ${['Jellyfin','Emby'].includes(s.mediaServerType) ? 'hidden' : ''}>
        <div class="field-row"><label>Server URL</label>
          <input type="text" id="set-plex-url" value="${esc(s.plexBaseUrl)}" placeholder="http://192.168.1.50:32400" /></div>
        <div class="field-row"><label>Plex Token</label>
          <div class="field-with-test"><input type="password" id="set-plex-token" value="${esc(s.plexToken)}" />
          <button class="btn-test" data-test="plex">Test</button></div>
          <div class="test-result" id="test-plex"></div></div>
      </div>
      <div id="server-jellyfin" ${s.mediaServerType === 'Jellyfin' ? '' : 'hidden'}>
        <div class="field-row"><label>Server URL</label>
          <input type="text" id="set-jellyfin-url" value="${esc(s.jellyfinBaseUrl)}" placeholder="http://192.168.1.50:8096" /></div>
        <div class="field-row"><label>API Key</label>
          <div class="field-with-test"><input type="password" id="set-jellyfin-key" value="${esc(s.jellyfinApiKey)}" />
          <button class="btn-test" data-test="jellyfin">Test</button></div>
          <div class="test-result" id="test-jellyfin"></div></div>
        <div class="section-desc">Create a key in Jellyfin under Dashboard → API Keys.</div>
      </div>
      <div id="server-emby" ${s.mediaServerType === 'Emby' ? '' : 'hidden'}>
        <div class="field-row"><label>Server URL</label>
          <input type="text" id="set-emby-url" value="${esc(s.embyBaseUrl)}" placeholder="http://192.168.1.50:8096" /></div>
        <div class="field-row"><label>API Key</label>
          <div class="field-with-test"><input type="password" id="set-emby-key" value="${esc(s.embyApiKey)}" />
          <button class="btn-test" data-test="emby">Test</button></div>
          <div class="test-result" id="test-emby"></div></div>
        <div class="section-desc">Create a key in Emby under Settings → Advanced → API Keys.</div>
      </div>
    </div>

    <div class="settings-section">
      <h3>Poster Sources</h3>
      <div class="section-desc">Leave blank to skip a source.</div>
      <div class="field-row"><label>TMDB API Key</label>
        <div class="field-with-test"><input type="password" id="set-tmdb" value="${esc(s.tmdbApiKey)}" />
        <button class="btn-test" data-test="tmdb">Test</button></div>
        <div class="test-result" id="test-tmdb"></div></div>
      <div class="field-row"><label>FanArt.tv API Key</label>
        <div class="field-with-test"><input type="password" id="set-fanart" value="${esc(s.fanArtApiKey)}" />
        <button class="btn-test" data-test="fanart">Test</button></div>
        <div class="test-result" id="test-fanart"></div></div>
      <div class="field-row"><label>TheTVDB API Key</label>
        <div class="field-with-test"><input type="password" id="set-tvdb" value="${esc(s.tvdbApiKey)}" />
        <button class="btn-test" data-test="tvdb">Test</button></div>
        <div class="test-result" id="test-tvdb"></div></div>
      <div class="field-row"><label>OMDB API Key <a href="https://www.omdbapi.com/apikey.aspx" target="_blank" style="font-size:11px;color:var(--accent)">(free key)</a></label>
        <div class="section-desc" style="font-size:11px;color:var(--text-tertiary);margin-bottom:4px">Required for IMDb ratings and Rotten Tomatoes scores. TMDB does not provide these.</div>
        <div class="field-with-test"><input type="password" id="set-omdb" value="${esc(s.omdbApiKey??'')}" />
        <button class="btn-test" data-test="omdb">Test</button></div>
        <div class="test-result" id="test-omdb"></div></div>
      <div class="field-row"><label>MDBList API Key <a href="https://mdblist.com/preferences" target="_blank" style="font-size:11px;color:var(--accent)">(free key)</a></label>
        <div class="section-desc" style="font-size:11px;color:var(--text-tertiary);margin-bottom:4px">Rotten Tomatoes scores for TV shows (and movies). OMDB only provides RT for movies.</div>
        <div class="field-with-test"><input type="password" id="set-mdblist" value="${esc(s.mdbListApiKey??'')}" />
        <button class="btn-test" data-test="mdblist">Test</button></div>
        <div class="test-result" id="test-mdblist"></div></div>
      <div class="field-row"><label>Trakt Client ID <a href="https://trakt.tv/oauth/applications" target="_blank" style="font-size:11px;color:var(--accent)">(free key)</a></label>
        <div class="section-desc" style="font-size:11px;color:var(--text-tertiary);margin-bottom:4px">Lets Auto Collections search and follow public Trakt lists. Create an app there and paste its Client ID — no login needed, and nothing is ever written to your Trakt account.</div>
        <div class="field-with-test"><input type="password" id="set-trakt" value="${esc(s.traktClientId??'')}" />
        <button class="btn-test" data-test="trakt">Test</button></div>
        <div class="test-result" id="test-trakt"></div></div>
    </div>

    <div class="settings-section">
      <h3>Poster Overlays</h3>
      <div class="section-desc">Tick each badge type. Where a PNG exists in the image set it is used automatically; text badges are used as fallback.</div>
      <div class="overlay-grid">${ovGrid}</div>
      <div style="margin-top:12px;display:grid;grid-template-columns:1fr 1fr;gap:12px">
        <div class="field-row"><label>Audio codec style</label>
          <select id="set-ov-audiostyle">
            <option value="standard" ${(s.overlays?.audioCodecStyle??'standard')==='standard'?'selected':''}>Standard</option>
            <option value="compact"  ${s.overlays?.audioCodecStyle==='compact'?'selected':''}>Compact</option>
          </select></div>
        <div class="field-row"><label>Streaming badge style</label>
          <select id="set-ov-streamingstyle">
            <option value="color" ${(s.overlays?.streamingStyle??'color')==='color'?'selected':''}>Colour</option>
            <option value="white" ${s.overlays?.streamingStyle==='white'?'selected':''}>White</option>
          </select></div>
        <div class="field-row"><label>Network badge style</label>
          <select id="set-ov-networkstyle">
            <option value="color" ${(s.overlays?.networkStyle??'color')==='color'?'selected':''}>Colour</option>
            <option value="white" ${s.overlays?.networkStyle==='white'?'selected':''}>White</option>
          </select></div>
        <div class="field-row"><label>Studio badge style</label>
          <select id="set-ov-studiostyle">
            <option value="standard" ${(s.overlays?.studioStyle??'standard')==='standard'?'selected':''}>Standard</option>
            <option value="bigger"   ${s.overlays?.studioStyle==='bigger'?'selected':''}>Bigger</option>
          </select></div>
        <div class="field-row"><label>Rotten Tomatoes style</label>
          <select id="set-ov-rtstyle">
            <option value="badge"  ${(s.overlays?.rottenTomatoesStyle??'badge')==='badge' ?'selected':''}>Badge — icon + score</option>
            <option value="ribbon" ${s.overlays?.rottenTomatoesStyle==='ribbon'?'selected':''}>Corner ribbon — Certified Fresh</option>
          </select></div>
        <div class="field-row"><label>Audience Score style</label>
          <select id="set-ov-audstyle">
            <option value="badge"  ${(s.overlays?.audienceScoreStyle??'badge')==='badge' ?'selected':''}>Badge — icon + score</option>
            <option value="ribbon" ${s.overlays?.audienceScoreStyle==='ribbon'?'selected':''}>Corner ribbon — Verified Hot</option>
          </select></div>
        <div class="field-row"><label>Award ribbon colour</label>
          <select id="set-ov-ribboncolour">
            <option value="black"  ${(s.overlays?.ribbonColour??'black')==='black' ?'selected':''}>Black</option>
            <option value="gray"   ${s.overlays?.ribbonColour==='gray'  ?'selected':''}>Gray</option>
            <option value="red"    ${s.overlays?.ribbonColour==='red'   ?'selected':''}>Red</option>
            <option value="yellow" ${s.overlays?.ribbonColour==='yellow'?'selected':''}>Yellow</option>
          </select></div>
        <div class="field-row"><label>NEW badge shows for (days)</label>
          <input type="number" id="set-ov-newdays" min="1" max="365" value="${s.overlays?.newBadgeDays ?? 14}" /></div>
        <div class="field-row"><label>Language flag style</label>
          <select id="set-ov-flagstyle">
            <option value="round"  ${(s.overlays?.flagStyle??'round')==='round' ?'selected':''}>Round</option>
            <option value="square" ${s.overlays?.flagStyle==='square'?'selected':''}>Square</option>
          </select></div>
        <div class="field-row">
          <label for="set-ov-fontsize">Text badge font size (px)</label>
          <div class="field-desc" style="font-size:11px;color:var(--text-tertiary);margin-bottom:4px">Controls text size inside pill badges (Status, Trending, Ratings score etc.) — not PNG image badges</div>
          <input type="number" id="set-ov-fontsize" min="12" max="60" value="${s.overlays?.fontSizePx??28}" /></div>
      </div>

      <!-- Badge size slider with live preview -->
      <div class="slider-row" style="margin-top:18px">
        <div class="slider-label-row">
          <label for="set-ov-badgesize">Badge image size</label>
          <span class="slider-value" id="set-ov-badgesize-val">${s.overlays?.badgeSizePct??22}%</span>
        </div>
        <input type="range" id="set-ov-badgesize" min="10" max="45" step="1"
          value="${s.overlays?.badgeSizePct??22}"
          style="width:100%;accent-color:var(--accent)" />
        <div class="slider-hints"><span>Smaller</span><span>Larger</span></div>
      </div>
      <div class="slider-row" style="margin-top:14px">
        <div class="slider-label-row">
          <label for="set-ov-pillopacity">Badge background opacity</label>
          <span class="slider-value" id="set-ov-pillopacity-val">${Math.round((s.overlays?.pillOpacity??0.78)*100)}%</span>
        </div>
        <input type="range" id="set-ov-pillopacity" min="0" max="100" step="5"
          value="${Math.round((s.overlays?.pillOpacity??0.78)*100)}"
          style="width:100%;accent-color:var(--accent)" />
        <div class="slider-hints"><span>Transparent (no background)</span><span>Solid black</span></div>
      </div>
      <div class="toggle-row" style="margin-top:16px">
        <div><div class="toggle-label">Apply badges to season posters</div>
          <div class="toggle-desc">When ON, the enabled badges above are also shown on (and baked into) TV-show season posters, using the parent show's metadata. When OFF, season posters stay clean. Movie and show posters are unaffected.</div></div>
        <label class="switch"><input type="checkbox" id="set-ov-season-overlays" ${s.overlays?.seasonPosterOverlaysEnabled!==false?'checked':''}/><span class="switch-track"></span></label>
      </div>
    </div>

    <div class="settings-section">
      <h3>Poster Selection</h3>
      <div class="toggle-row">
        <div><div class="toggle-label">Prefer textless posters</div>
          <div class="toggle-desc">When the scan auto-picks a poster, prefer versions with no title text if available</div></div>
        <label class="switch"><input type="checkbox" id="set-textless" ${s.preferTextlessPosters?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="toggle-row">
        <div><div class="toggle-label">Prefer matching season sets</div>
          <div class="toggle-desc">When the scan picks season posters for a new show, use one matching FanArt.tv set for all its seasons where one exists (and continue that set when a new season arrives). Seasons the set doesn't cover get the usual pick. Season posters you chose yourself are never changed.</div></div>
        <label class="switch"><input type="checkbox" id="set-season-sets" ${s.preferSeasonSets?'checked':''}/><span class="switch-track"></span></label>
      </div>
    </div>

    <div class="settings-section">
      <h3>Backup</h3>
      <div class="section-desc">Saves original poster/background before overwriting so you can restore it from the picker.</div>
      <div class="toggle-row">
        <div><div class="toggle-label">Back up originals before applying</div></div>
        <label class="switch"><input type="checkbox" id="set-backup-enabled" ${s.backup?.backupEnabled!==false?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="field-row" style="margin-top:10px"><label>Backup folder (leave blank for default)</label>
        <input type="text" id="set-backup-dir" value="${esc(s.backup?.backupDirectory??'')}" placeholder="leave blank for default" /></div>
    </div>

    <div class="settings-section">
      <h3>Library Scanning</h3>
      <div class="field-row"><label>Scan interval (minutes, minimum 5)</label>
        <input type="number" id="set-interval" min="5" value="${s.scanIntervalMinutes??60}" /></div>
      <div class="toggle-row" id="webhook-row">
        <div><div class="toggle-label" id="webhook-label">${instantUpdatesText(s.mediaServerType).label}</div>
          <div class="toggle-desc" id="webhook-desc">${instantUpdatesText(s.mediaServerType).desc}</div></div>
        <label class="switch"><input type="checkbox" id="set-webhook" ${s.webhookEnabled!==false?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="toggle-row">
        <div><div class="toggle-label">Auto-apply on scan</div>
          <div class="toggle-desc">When ON, a poster, background or season poster found during scan is pushed straight to ${srvName()} with overlays rendered. When OFF (default), it's only proposed on the card — you click Apply yourself.</div></div>
        <label class="switch"><input type="checkbox" id="set-auto-apply-scan" ${s.autoApplyOnScan?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="toggle-row">
        <div><div class="toggle-label">Incremental scan only</div>
          <div class="toggle-desc">When ON, scans skip items already known and unchanged since the last scan, checking only for new or modified media — much faster on large libraries. The first scan after enabling is always a full scan.</div></div>
        <label class="switch"><input type="checkbox" id="set-incremental-scan" ${s.incrementalScanOnly?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="toggle-row">
        <div><div class="toggle-label">Show dismissed items</div>
          <div class="toggle-desc">When ON, items and collections you've dismissed stay visible in the grids, marked "Dismissed", so you can pick a poster to bring one back. When OFF (default), dismissing hides it.</div></div>
        <label class="switch"><input type="checkbox" id="set-show-dismissed" ${s.showDismissed?'checked':''}/><span class="switch-track"></span></label>
      </div>
    </div>

    <div class="settings-section">
      <h3>Authentication</h3>
      <div class="toggle-row">
        <div><div class="toggle-label">Require login</div>
          <div class="toggle-desc">Strongly recommended. Postarr holds your media server token and API keys — with login off, anyone who can reach this page can use them.</div></div>
        <label class="switch"><input type="checkbox" id="set-auth-enabled" ${s.auth?.authEnabled?'checked':''}/><span class="switch-track"></span></label>
      </div>
      <div class="field-row" style="margin-top:10px"><label>Username</label>
        <input type="text" id="set-auth-username" value="${esc(s.auth?.username??'admin')}" /></div>
      <div class="field-row"><label>New password (leave blank to keep current)</label>
        <input type="password" id="set-auth-password" placeholder="Enter new password…" autocomplete="new-password" /></div>
      <div class="field-row"><label>Session length (days)</label>
        <input type="number" id="set-auth-days" min="1" max="365" value="${s.auth?.sessionDaysValid??30}" /></div>
    </div>

    <div class="settings-section support-card">
      <h3>Support Postarr</h3>
      <div class="section-desc">Postarr is free. If it has saved you some time, you're welcome to buy me a coffee — thank you!</div>
      <div class="support-row">
        <svg class="support-cup" viewBox="0 0 48 48" width="44" height="44" aria-hidden="true">
          <path d="M12 6c-2 3 2 4 0 7M20 4c-2 3 2 4 0 8M28 6c-2 3 2 4 0 7" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" opacity=".55"/>
          <path d="M7 19h29v10a11 11 0 0 1-11 11h-7A11 11 0 0 1 7 29z" fill="#f2b254"/>
          <path d="M36 22h3a5 5 0 0 1 0 10h-4" fill="none" stroke="#f2b254" stroke-width="3.5" stroke-linecap="round"/>
          <rect x="3" y="41" width="37" height="3.5" rx="1.75" fill="currentColor" opacity=".7"/>
        </svg>
        <div class="support-text">
          <div class="support-line">PayPal &middot; <span id="support-email" class="support-email">mathew_kerr@hotmail.com</span></div>
          <div class="support-note">Opens PayPal in a new tab. Nothing is sent from your server.</div>
        </div>
        <a class="btn-save support-btn" id="support-paypal" target="_blank" rel="noopener noreferrer"
           href="https://www.paypal.com/donate/?business=mathew_kerr%40hotmail.com&item_name=Postarr%20-%20buy%20me%20a%20coffee&currency_code=AUD">Buy me a coffee</a>
        <button class="btn-test" id="support-copy" type="button">Copy email</button>
      </div>
    </div>

    <div class="settings-section about-card">
      <h3>About</h3>
      <div class="about-version">Postarr <span id="about-version">…</span> · Media Artwork Manager · MIT License</div>
      <div class="section-desc">This product uses the TMDB API but is not endorsed or certified by TMDB. Artwork and data also come from
        FanArt.tv, TheTVDB, OMDb, MDBList and Trakt. Badge images are from Kometa (MIT License); the Inter font is under the SIL Open Font License.
        Logos in badges are trademarks of their owners. Full details are in <code>THIRD-PARTY-NOTICES.md</code> in the install folder.</div>
      <div class="about-links">
        <a href="https://www.themoviedb.org" target="_blank" rel="noopener noreferrer">TMDB</a>
        <a href="https://fanart.tv" target="_blank" rel="noopener noreferrer">FanArt.tv</a>
        <a href="https://thetvdb.com" target="_blank" rel="noopener noreferrer">TheTVDB</a>
        <a href="https://github.com/Kometa-Team/Kometa" target="_blank" rel="noopener noreferrer">Kometa</a>
      </div>
    </div>

    <button class="btn-save" id="settings-save-btn">Save all settings</button>
  </div>

  <!-- Live badge preview -->
  <div class="badge-preview-wrap">
    <div class="badge-preview-label">Live Preview — drag each badge to position it</div>
    <canvas id="badge-preview-canvas" width="800" height="1200"></canvas>
    <button id="reset-preview-btn" class="btn-restore" style="margin-top:6px;width:100%">↺ Reset positions</button>
    <div class="badge-preview-hint">Drag a badge to move it, or click to select and nudge with the arrow keys (hold Shift for larger steps).<br>Positions save with settings.</div>
  </div>

</div>`;
}

// The "new content" toggle means a webhook for Plex but a live connection for Jellyfin (same setting).
function instantUpdatesText(serverType) {
  return (serverType === 'Jellyfin' || serverType === 'Emby')
    ? { label: 'Instant updates',
        desc:  `Postarr keeps a live connection to ${serverType} and adds new movies, shows and seasons about a minute after ${serverType} finishes adding them. Nothing to set up in ${serverType}.` }
    : { label: 'Plex webhooks',
        desc:  `Instant updates on new content (Plex Pass required). In Plex → Settings → Webhooks, add:<br><code class="webhook-url">${esc(location.origin)}/api/webhooks/plex?token=${esc(S.settings?.webhookToken || '…')}</code><br>The token lets Plex in while login is on — keep it private.` };
}

function wireSettings() {
  els('[data-test]').forEach(b => b.addEventListener('click', () => testConn(b.dataset.test)));
  el('#set-server-type')?.addEventListener('change', e => {
    const v = e.target.value;
    el('#server-plex').hidden     = v !== 'Plex';
    el('#server-jellyfin').hidden = v !== 'Jellyfin';
    el('#server-emby').hidden     = v !== 'Emby';
    const t = instantUpdatesText(e.target.value);
    el('#webhook-label').textContent = t.label;
    el('#webhook-desc').innerHTML    = t.desc;
  });
  el('#settings-save-btn').addEventListener('click', saveSettings);
  api('/settings/about').then(a => { const v = el('#about-version'); if (v && a?.version) v.textContent = a.version; }).catch(() => {});
  el('#set-auth-enabled')?.addEventListener('change', e => {
    if (!e.target.checked && !confirm('Turn login off?\n\nAnyone who can reach Postarr will be able to use it — and the media server token and API keys it holds. Only do this if Postarr is reachable from your own network and nowhere else.'))
      e.target.checked = true;
  });
  el('#support-copy')?.addEventListener('click', async () => {
    const text = el('#support-email').textContent.trim();
    try { await navigator.clipboard.writeText(text); toast('PayPal email copied — thank you!'); }
    catch {
      const r = document.createRange(); r.selectNodeContents(el('#support-email'));
      const sel = getSelection(); sel.removeAllRanges(); sel.addRange(r);
      toast('Email selected — press Ctrl+C to copy it.');
    }
  });

  // Theme colour pickers — preview the whole UI + logo live as they change.
  const previewTheme = () => applyTheme(el('#set-theme-primary')?.value, el('#set-theme-accent')?.value);
  el('#set-theme-primary')?.addEventListener('input', previewTheme);
  el('#set-theme-accent')?.addEventListener('input', previewTheme);
  els('.theme-preset').forEach(btn => btn.addEventListener('click', () => {
    el('#set-theme-primary').value = btn.dataset.p;
    el('#set-theme-accent').value  = btn.dataset.a;
    previewTheme();
  }));
  el('#theme-reset')?.addEventListener('click', () => {
    el('#set-theme-primary').value = THEME_DEFAULT.primary;
    el('#set-theme-accent').value  = THEME_DEFAULT.accent;
    previewTheme();
  });

  // Init live preview
  initBadgePreview();

  // Reset positions button
  const resetBtn = el('#reset-preview-btn');
  if (resetBtn) resetBtn.addEventListener('click', () => {
    _previewPositions = JSON.parse(JSON.stringify(DEFAULT_POSITIONS));
    renderBadgePreview();
    toast('Badge positions reset to defaults.');
  });

  // Re-render preview on any overlay change
  OVERLAY_TICKS.forEach(([k]) => {
    const cb = el(`#ov-${k}`);
    if (cb) cb.addEventListener('change', renderBadgePreview);
  });
  ['set-ov-audiostyle','set-ov-streamingstyle','set-ov-networkstyle',
   'set-ov-studiostyle','set-ov-ribboncolour','set-ov-flagstyle','set-ov-rtstyle','set-ov-audstyle'
  ].forEach(id => { const e = el(`#${id}`); if (e) e.addEventListener('change', renderBadgePreview); });

  el('#set-ov-fontsize')?.addEventListener('input', renderBadgePreview);

  // Pill opacity slider
  const pillOpacitySlider = el('#set-ov-pillopacity');
  const pillOpacityVal    = el('#set-ov-pillopacity-val');
  if (pillOpacitySlider) {
    pillOpacitySlider.addEventListener('input', () => {
      if (pillOpacityVal) pillOpacityVal.textContent = pillOpacitySlider.value + '%';
      renderBadgePreview();
    });
  }
  const badgeSizeSlider = el('#set-ov-badgesize');
  const badgeSizeVal    = el('#set-ov-badgesize-val');
  if (badgeSizeSlider) {
    badgeSizeSlider.addEventListener('input', () => {
      if (badgeSizeVal) badgeSizeVal.textContent = badgeSizeSlider.value + '%';
      renderBadgePreview();
    });
  }
}

// ── Live badge preview ────────────────────────────────────────────────────────
// Per-badge positions stored as fractions of canvas size (mirrors BadgeXY model)

function initBadgePreview() {
  const canvas = el('#badge-preview-canvas');
  if (!canvas) return;
  _selectedBadge = null;   // fresh canvas each time settings renders

  // Load positions from current settings
  if (S.settings?.overlays) {
    for (const [field, def] of Object.entries(DEFAULT_POSITIONS)) {
      const saved = S.settings.overlays[field];
      _previewPositions[field] = saved ? { x: saved.x ?? def.x, y: saved.y ?? def.y } : { ...def };
    }
  }

  // Fixed 2:3 canvas so the preview matches the poster GRID cards (which are also 2:3 and
  // cover-cropped) — that's the "result" the user compares against. renderBadgePreview draws
  // the sample cover-cropped, exactly like the cards' object-fit:cover.
  canvas.width = 800;
  canvas.height = 1200;
  const applySample = im => { canvas._sampleImg = im; renderBadgePreview(); };

  // Prefer the bundled Ad Astra sample (lighter, recognisable, keeps badges legible) so the
  // preview isn't too dark. Fall back to a library poster, then a plain gradient.
  const bundled = new Image();
  bundled.onload  = () => applySample(bundled);
  bundled.onerror = () => {
    const libUrl = (S.movies.find(m => m.currentPosterUrl) || S.shows.find(s => s.currentPosterUrl))?.currentPosterUrl;
    if (libUrl) { const im = new Image(); im.onload = () => applySample(im); im.onerror = () => applySample(null); im.src = libUrl; }
    else applySample(null);
  };
  bundled.src = '/images/sample-poster.jpg';

  // Mouse drag — find closest badge within 80px of click
  canvas.addEventListener('mousedown', e => {
    const rect = canvas.getBoundingClientRect();
    // Pixel coords on the canvas element (display size)
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    const dw = rect.width;
    const dh = rect.height;

    let closest = null, closestDist = 80; // 80px hit radius
    for (const [key, enabled] of Object.entries(getEnabledBadges())) {
      if (!enabled) continue;
      const posField = POS_FIELD[key];
      const pos = _previewPositions[posField];
      if (!pos) continue;
      // Convert normalised pos to display pixels
      const bpx = pos.x * dw;
      const bpy = pos.y * dh;
      const dist = Math.hypot(px - bpx, py - bpy);
      if (dist < closestDist) { closestDist = dist; closest = posField; }
    }
    // Clicking a badge also selects it, so the arrow keys can nudge it afterwards. Clicking empty
    // space clears the selection. Focus the canvas so it receives keydown events.
    _selectedBadge = closest;
    canvas.focus({ preventScroll: true });
    if (closest) {
      const pos = _previewPositions[closest];
      _dragState = {
        posKey: closest,
        offX: px - pos.x * dw,  // pixel offset from badge centre
        offY: py - pos.y * dh,
      };
      canvas.style.cursor = 'grabbing';
    }
    renderBadgePreview();   // reflect the new selection ring
  });

  // Arrow keys nudge the selected badge. A tap is a very fine step — 0.1% of the poster, about
  // 1–1.5px on a 1000×1500 poster — for pixel-level placement the drag can't give; hold the key to
  // glide. Shift jumps in bigger 1% steps (~10–15px) to cross the poster quickly.
  canvas.setAttribute('tabindex', '0');
  canvas.addEventListener('keydown', e => {
    if (!_selectedBadge) return;
    const step = e.shiftKey ? 0.01 : 0.001;
    let dx = 0, dy = 0;
    if      (e.key === 'ArrowLeft')  dx = -step;
    else if (e.key === 'ArrowRight') dx =  step;
    else if (e.key === 'ArrowUp')    dy = -step;
    else if (e.key === 'ArrowDown')  dy =  step;
    else return;
    e.preventDefault();   // don't scroll the settings page
    const p = _previewPositions[_selectedBadge] || { ...DEFAULT_POSITIONS[_selectedBadge] };
    _previewPositions[_selectedBadge] = {
      x: Math.max(0.02, Math.min(0.98, p.x + dx)),
      y: Math.max(0.02, Math.min(0.98, p.y + dy)),
    };
    renderBadgePreview();
  });

  canvas.addEventListener('mousemove', e => {
    if (!_dragState) return;
    const rect = canvas.getBoundingClientRect();
    const px = e.clientX - rect.left;
    const py = e.clientY - rect.top;
    _previewPositions[_dragState.posKey] = {
      x: Math.max(0.02, Math.min(0.98, (px - _dragState.offX) / rect.width)),
      y: Math.max(0.02, Math.min(0.98, (py - _dragState.offY) / rect.height)),
    };
    renderBadgePreview();
  });

  canvas.addEventListener('mouseup',   () => { _dragState = null; canvas.style.cursor = 'crosshair'; });
  canvas.addEventListener('mouseleave',() => { _dragState = null; canvas.style.cursor = 'crosshair'; });

  // Touch support
  canvas.addEventListener('touchstart', e => {
    e.preventDefault();
    const touch = e.touches[0];
    canvas.dispatchEvent(new MouseEvent('mousedown', { clientX: touch.clientX, clientY: touch.clientY }));
  }, { passive: false });
  canvas.addEventListener('touchmove', e => {
    e.preventDefault();
    const touch = e.touches[0];
    canvas.dispatchEvent(new MouseEvent('mousemove', { clientX: touch.clientX, clientY: touch.clientY }));
  }, { passive: false });
  canvas.addEventListener('touchend', () => canvas.dispatchEvent(new MouseEvent('mouseup')));

  renderBadgePreview();
}

function canvasNorm(canvas, e) {
  const rect = canvas.getBoundingClientRect();
  const nx = (e.clientX - rect.left) / rect.width;
  const ny = (e.clientY - rect.top)  / rect.height;
  return { nx, ny };
}

function getEnabledBadges() {
  const result = {};
  for (const key of BADGE_KEYS) result[key] = el(`#ov-${key}`)?.checked ?? false;
  return result;
}

function renderBadgePreview() {
  const canvas = el('#badge-preview-canvas');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  const W = canvas.width, H = canvas.height;

  // Draw background / poster — cover-cropped to fill the 2:3 canvas, matching the grid cards'
  // object-fit:cover (so the preview frames the poster the same way the poster page does).
  ctx.clearRect(0, 0, W, H);
  if (canvas._sampleImg) {
    const im = canvas._sampleImg, ia = im.naturalWidth / im.naturalHeight, ca = W / H;
    let sw, sh, sx, sy;
    if (ia > ca) { sh = im.naturalHeight; sw = sh * ca; sx = (im.naturalWidth - sw) / 2; sy = 0; }
    else         { sw = im.naturalWidth;  sh = sw / ca; sx = 0; sy = (im.naturalHeight - sh) / 2; }
    ctx.drawImage(im, sx, sy, sw, sh, 0, 0, W, H);
  } else {
    const g = ctx.createLinearGradient(0, 0, W, H);
    g.addColorStop(0, '#1d212c'); g.addColorStop(1, '#0e1015');
    ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
    ctx.fillStyle = '#5f6573'; ctx.font = '20px sans-serif';
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText('No poster — run a scan first', W/2, H/2);
  }

  const fontSize = parseInt(el('#set-ov-fontsize')?.value || 28);
  const scale    = W / 400; // base design width
  const fs       = Math.max(10, fontSize * scale);
  const px       = Math.round(14 * scale), py = Math.round(6 * scale);
  const r        = Math.round(8  * scale);
  const pillPad  = Math.round(6  * scale);
  const gap      = Math.round(8  * scale);

  ctx.font = `bold ${fs}px Arial, sans-serif`;

  const enabled = getEnabledBadges();
  const badges  = collectPreviewBadgesWithPos(W, H, scale, enabled);

  for (const badge of badges) {
    drawPreviewBadge(ctx, badge, W, H, fs, px, py, r, pillPad, gap);
  }

  // Draw grab handles on enabled badge positions (small circle indicator)
  ctx.globalAlpha = 0.5;
  for (const badge of badges) {
    ctx.beginPath();
    ctx.arc(badge.cx, badge.cy, 5 * scale, 0, Math.PI * 2);
    ctx.fillStyle = '#10bec9';   // teal drag handle — matches the brand accent
    ctx.fill();
  }
  ctx.globalAlpha = 1;

  // Ring around the selected badge so it's clear which one the arrow keys will move.
  if (_selectedBadge) {
    const sel = badges.find(b => POS_FIELD[b.key] === _selectedBadge);
    if (sel) {
      ctx.beginPath();
      ctx.arc(sel.cx, sel.cy, 15 * scale, 0, Math.PI * 2);
      ctx.strokeStyle = '#f2586e';           // coral — stands out against the teal handles
      ctx.lineWidth = Math.max(2, 2.5 * scale);
      ctx.stroke();
    }
  }
}

// Badge text size from the setting, scaled from a 1500px-tall reference poster (mirrors
// OverlayRenderer so the preview matches the applied poster).
function badgeFontPx(H) {
  const px = parseInt(el('#set-ov-fontsize')?.value || 28);
  return Math.max(Math.round(H / 1500 * px), 8);
}
const BADGE_FONT = "'InterBadge', Arial, sans-serif";   // Kometa's Inter-Medium

// Anchor by zone: left side expands right, right side expands left, middle stays centred —
// keeps a consistent edge as text length changes and stops badges running off the poster.
function anchorBoxX(cx, boxW, W) {
  const frac = cx / W;
  const x = frac < 0.45 ? cx : frac > 0.55 ? cx - boxW : cx - boxW / 2;
  return Math.round(Math.max(0, Math.min(W - boxW, x)));
}

function drawPreviewBadge(ctx, badge, W, H, fs, px, py, r, pillPad, gap) {
  const badgePct = parseInt(el('#set-ov-badgesize')?.value || 22) / 100;
  const opacity = (parseInt(el('#set-ov-pillopacity')?.value) || 78) / 100;
  // Font size comes from Settings > Text Badge Font Size, scaled from a 1500px-tall reference
  // poster (mirrors OverlayRenderer). It used to be boxH * 0.5, so the setting did nothing.
  const font    = badgeFontPx(H);
  // Box is the configured height, but never shorter than the text needs.
  const boxH    = Math.max(Math.max(Math.round(H * badgePct * 0.30), 14), Math.round(font * 1.55));
  const radius  = Math.max(boxH * 0.22, 4);
  const padX    = Math.round(boxH * 0.18);

  if (badge.imgSrc) {
    const img = getPreviewImage(badge.imgSrc);
    if (img && img.complete && img.naturalWidth > 0) {
      // Corner ribbon — no box, sized by width.
      if (badge.isRibbon) {
        const rw = Math.round(W * badgePct);
        const rh = Math.round(rw * img.naturalHeight / img.naturalWidth);
        ctx.drawImage(img, Math.round(badge.cx - rw / 2), Math.round(badge.cy - rh / 2), rw, rh);
        return;
      }
      // Uniform-height box; image (resolution a touch shorter) + optional inline score.
      const ih = Math.round(boxH * (badge.hasPill ? 0.48 : 0.70));
      const iw = Math.round(ih * img.naturalWidth / img.naturalHeight);
      ctx.font = `${font}px ${BADGE_FONT}`;
      const gapW   = Math.round(boxH * 0.15);
      const scoreW = badge.text ? Math.round(ctx.measureText(badge.text).width + boxH * 0.32) : 0;
      const boxW = iw + scoreW + padX * 2;   // box hugs content
      const bx = anchorBoxX(badge.cx, boxW, W);
      const by = Math.round(Math.max(0, Math.min(H - boxH, badge.cy - boxH / 2)));
      ctx.beginPath(); ctx.roundRect(bx, by, boxW, boxH, radius);
      ctx.fillStyle = `rgba(0,0,0,${opacity})`; ctx.fill();
      ctx.drawImage(img, bx + padX, by + Math.round((boxH - ih) / 2), iw, ih);
      if (badge.text && !badge.hasPill) {
        // Centre the score in the space left beside the icon (both axes).
        ctx.fillStyle = '#fff';
        ctx.textBaseline = 'middle';
        ctx.textAlign = 'center';
        const scoreLeft = bx + padX + iw + gapW;
        ctx.fillText(badge.text, scoreLeft + (boxW - padX - (scoreLeft - bx)) / 2, by + boxH / 2);
        ctx.textAlign = 'left';
      }
    } else if (badge.fallbackText) {
      drawTextBox(ctx, badge.fallbackText, badge.cx, badge.cy, boxH, radius, padX, opacity, font, W, H);
    }
  } else if (badge.text) {
    drawTextBox(ctx, badge.text, badge.cx, badge.cy, boxH, radius, padX, opacity, font, W, H);
  }
}

// Text-only badge in the same uniform dark box (no border).
function drawTextBox(ctx, text, cx, cy, boxH, radius, padX, opacity, font, W, H) {
  ctx.font = `${font}px ${BADGE_FONT}`;
  const boxW = Math.round(ctx.measureText(text).width + padX * 2);
  const bx = anchorBoxX(cx, boxW, W);
  const by = Math.round(Math.max(0, Math.min(H - boxH, cy - boxH / 2)));
  ctx.beginPath(); ctx.roundRect(bx, by, boxW, boxH, radius);
  ctx.fillStyle = `rgba(0,0,0,${opacity})`; ctx.fill();
  // Explicitly centre on both axes — textAlign could otherwise carry over from earlier draws.
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.textAlign = 'center';
  ctx.fillText(text, bx + boxW / 2, by + boxH / 2);
  ctx.textAlign = 'left';
}

function collectPreviewBadgesWithPos(W, H, scale, enabled) {
  const badges = [];
  const sample = S.movies[0] || S.shows[0] || {};
  const audioStyle  = el('#set-ov-audiostyle')?.value     || 'standard';
  const streamStyle = el('#set-ov-streamingstyle')?.value || 'color';
  const ribbonCol   = el('#set-ov-ribboncolour')?.value   || 'black';

  function add(key, imgSrc, text, color, fallbackText, opts = {}) {
    if (!enabled[key]) return;
    const posField = POS_FIELD[key];
    const pos = _previewPositions[posField] || DEFAULT_POSITIONS[posField];
    badges.push({ key, cx: pos.x * W, cy: pos.y * H, imgSrc, text, color, fallbackText,
      hasPill: opts.hasPill || false, isRibbon: opts.isRibbon || false });
  }

  const res = sample.videoResolution || '4k';
  const dr  = sample.videoDynamicRange || 'HDR';
  const resKey = buildPreviewResKey(res, dr);
  // hasPill → resolution is sized by consistent height (see drawPreviewBadge)
  add('resolutionEnabled', `/images/resolution/${resKey}.png`, null, '#1e6437', kometaRes(res, dr), { hasPill: true });

  add('dynamicRangeEnabled', `/images/resolution/hdr.png`, null, '#6e2390');

  const codec = (sample.audioCodec || 'truehd').toLowerCase();
  const af = previewAudioFile(codec);
  add('audioCodecEnabled', af ? `/images/audio_codec/${audioStyle}/${af}` : null,
      af ? null : codec.toUpperCase(), '#1a4b9b');

  add('contentRatingEnabled', '/images/cr/uspg-13.png', null, '#333');
  add('editionEnabled', '/images/edition/directors.png', null, '#222');
  add('languageEnabled', '/images/flag/round/en.png', null, '#333');
  add('imdbRatingEnabled', '/images/rating/IMDb.png', '8.5', '#af8414');
  // Rotten Tomatoes — follow the chosen style. This used to be hardcoded to a yellow ribbon while
  // the renderer drew the icon+score badge, so the preview disagreed with the actual poster.
  if ((el('#set-ov-rtstyle')?.value ?? 'badge') === 'ribbon')
    add('rottenTomatoesEnabled', `/images/ribbon/${ribbonCol}/rotten.png`, null, '#b91e1e', null, { isRibbon: true });
  else
    add('rottenTomatoesEnabled', '/images/rating/RT-Crit-Fresh.png', '87%', '#c83214');
  if ((el('#set-ov-audstyle')?.value ?? 'badge') === 'ribbon')
    add('audienceScoreEnabled', `/images/ribbon/${ribbonCol}/rottenverified.png`, null, '#c86414', null, { isRibbon: true });
  else
    add('audienceScoreEnabled', '/images/rating/RT-Aud-Fresh.png', '87%', '#c86414');
  add('oscarWinnerEnabled', `/images/ribbon/${ribbonCol}/oscars.png`, null, '#af8414', null, { isRibbon: true });
  add('oscarNomineeEnabled', null, 'Oscar Nominee', '#af8414');
  add('emmyWinnerEnabled', `/images/ribbon/${ribbonCol}/emmys.png`, null, '#af8414', null, { isRibbon: true });
  add('imdbTop250Enabled', `/images/ribbon/${ribbonCol}/imdb.png`, null, '#af8414', null, { isRibbon: true });
  add('streamingServiceEnabled', `/images/streaming/${streamStyle}/Netflix.png`, null, '#e5091e');
  add('networkEnabled', null, sample.network || 'HBO', '#222');
  add('studioEnabled', null, sample.studio || 'Studio', '#222');
  add('showStatusEnabled', null, sample.showStatus || 'Returning', '#1e6437');
  add('episodeCountEnabled', null, `${sample.episodeCount || 24} Episodes`, '#222');
  add('trendingEnabled', null, 'Trending', '#c85a14');
  add('popularEnabled', null, 'Popular', '#1a4b9b');
  add('metacriticEnabled', '/images/rating/Metacritic.png', '81', '#282828');
  add('newBadgeEnabled', null, 'NEW', '#16965a');
  add('videoSourceEnabled', null, 'REMUX', '#222');
  add('runtimeEnabled', null, '2h 12m', '#222');
  add('versionsEnabled', '/images/versions.png', null, '#222');
  add('audioLanguagesEnabled', '/images/dual_audio.png', null, '#222');
  add('subtitleLanguagesEnabled', '/images/multi_subs.png', null, '#222');
  add('letterboxdEnabled', '/images/rating/Letterboxd.png', '4.1', '#282828');
  add('traktEnabled', '/images/rating/Trakt.png', '82%', '#282828');

  return oneRibbonPerCorner(badges, b => b.cx / W, b => b.cy / H);
}

function getPreviewImage(src) {
  if (_badgeImgCache[src]) return _badgeImgCache[src];
  const img = new Image();
  img.onload = () => renderBadgePreview();
  img.src = src;
  _badgeImgCache[src] = img;
  return img;
}

function buildPreviewResKey(res, dr) {
  const r = (res||'').toLowerCase(), dru = (dr||'').toUpperCase();
  const isDV = dru.includes('DV'), isHDR = dru.includes('HDR'), isPlus = dru.includes('+') || dru.includes('PLUS');
  const rk = r==='4k'||r==='2160'||r==='uhd' ? '4k' : r==='1080'?'1080p' : r==='720'?'720p' : r==='576'?'576p' : r==='480'?'480p' : r;
  const sf = (isDV&&isHDR&&isPlus) ? 'dvhdrplus'
    : (isDV&&isHDR) ? 'dvhdr'
    : isDV          ? 'dv'
    : (isHDR&&isPlus)? 'plus'   // HDR10+ files under "plus", matches C# renderer
    : isHDR         ? 'hdr'
    : isPlus        ? 'plus'
    : '';
  return sf ? rk+sf : rk;
}

function previewAudioFile(codec) {
  if (codec.includes('truehd')&&codec.includes('atmos')) return 'truehd_atmos.png';
  if (codec.includes('truehd'))  return 'truehd.png';
  if (codec.includes('atmos'))   return 'atmos.png';
  if (codec==='eac3')            return 'plus.png';
  if (codec==='ac3')             return 'digital.png';
  if (codec.includes('dtsx'))    return 'dtsx.png';
  if (codec.includes('dts')&&codec.includes('ma')) return 'ma.png';
  if (codec.includes('dts'))     return 'dts.png';
  if (codec==='aac')             return 'aac.png';
  if (codec==='flac')            return 'flac.png';
  return null;
}


async function testConn(provider) {
  const r = el(`#test-${provider}`); r.textContent = 'Testing…'; r.className = 'test-result';
  try {
    const body = provider === 'plex'
      ? { value: el('#set-plex-token')?.value ?? '', value2: el('#set-plex-url')?.value.trim() ?? '' }
      : provider === 'jellyfin' ? { value: el('#set-jellyfin-key')?.value ?? '', value2: el('#set-jellyfin-url')?.value.trim() ?? '' }
      : provider === 'emby'     ? { value: el('#set-emby-key')?.value     ?? '', value2: el('#set-emby-url')?.value.trim()     ?? '' }
      : provider === 'tmdb'   ? { value: el('#set-tmdb')?.value   ?? '', value2: null }
      : provider === 'fanart' ? { value: el('#set-fanart')?.value ?? '', value2: null }
      : provider === 'omdb'   ? { value: el('#set-omdb')?.value    ?? '', value2: null }
      : provider === 'mdblist'? { value: el('#set-mdblist')?.value ?? '', value2: null }
      : provider === 'trakt'  ? { value: el('#set-trakt')?.value   ?? '', value2: null }
      :                         { value: el('#set-tvdb')?.value   ?? '', value2: null };
    const res = await api(`/settings/test/${provider}`, { method: 'POST', body: JSON.stringify(body) });
    r.textContent = res?.success ? 'Connected ✓' : 'Could not connect ✗';
    r.classList.add(res?.success ? 'ok' : 'fail');
  } catch(e) { r.textContent = `Error: ${e.message}`; r.classList.add('fail'); }
}

function collectSettings() {
  const ovKeys = OVERLAY_TICKS.map(([k]) => k);
  const overlays = {};
  ovKeys.forEach(k => { overlays[k] = el(`#ov-${k}`)?.checked ?? false; });

  // Style variants
  overlays.audioCodecStyle  = el('#set-ov-audiostyle')?.value     ?? 'standard';
  overlays.streamingStyle   = el('#set-ov-streamingstyle')?.value ?? 'color';
  overlays.networkStyle     = el('#set-ov-networkstyle')?.value   ?? 'color';
  overlays.studioStyle      = el('#set-ov-studiostyle')?.value    ?? 'standard';
  overlays.ribbonColour     = el('#set-ov-ribboncolour')?.value   ?? 'black';
  overlays.newBadgeDays     = Math.min(365, Math.max(1, parseInt(el('#set-ov-newdays')?.value) || 14));
  overlays.rottenTomatoesStyle = el('#set-ov-rtstyle')?.value     ?? 'badge';
  overlays.audienceScoreStyle  = el('#set-ov-audstyle')?.value    ?? 'badge';
  overlays.flagStyle        = el('#set-ov-flagstyle')?.value      ?? 'round';
  overlays.fontSizePx       = parseInt(el('#set-ov-fontsize')?.value) || 28;
  overlays.badgeSizePct     = parseInt(el('#set-ov-badgesize')?.value) || 22;
  overlays.pillOpacity      = (parseInt(el('#set-ov-pillopacity')?.value) || 78) / 100;
  overlays.seasonPosterOverlaysEnabled = el('#set-ov-season-overlays')?.checked ?? true;

  // Per-badge positions from live preview (saved as { x, y } fractions)
  for (const [posField, pos] of Object.entries(_previewPositions)) {
    overlays[posField] = { x: parseFloat(pos.x.toFixed(4)), y: parseFloat(pos.y.toFixed(4)) };
  }

  return {
    settings: {
      mediaServerType:       el('#set-server-type')?.value       ?? 'Plex',
      plexBaseUrl:           el('#set-plex-url')?.value.trim()  ?? '',
      plexToken:             el('#set-plex-token')?.value        ?? '',
      jellyfinBaseUrl:       el('#set-jellyfin-url')?.value.trim() ?? '',
      jellyfinApiKey:        el('#set-jellyfin-key')?.value      ?? '',
      embyBaseUrl:           el('#set-emby-url')?.value.trim()   ?? '',
      embyApiKey:            el('#set-emby-key')?.value          ?? '',
      tmdbApiKey:            el('#set-tmdb')?.value              ?? '',
      fanArtApiKey:          el('#set-fanart')?.value            ?? '',
      tvdbApiKey:            el('#set-tvdb')?.value              ?? '',
      omdbApiKey:            el('#set-omdb')?.value              ?? '',
      mdbListApiKey:         el('#set-mdblist')?.value           ?? '',
      traktClientId:         el('#set-trakt')?.value             ?? '',
      applyMode:             'AutoApply', // retained for backward compatibility; superseded by autoApplyOnScan below
      preferTextlessPosters: el('#set-textless')?.checked        ?? false,
      preferSeasonSets:      el('#set-season-sets')?.checked     ?? false,
      scanIntervalMinutes:   parseInt(el('#set-interval')?.value) || 60,
      webhookEnabled:        el('#set-webhook')?.checked          ?? true,
      autoApplyOnScan:       el('#set-auto-apply-scan')?.checked  ?? false,
      incrementalScanOnly:   el('#set-incremental-scan')?.checked ?? false,
      showDismissed:         el('#set-show-dismissed')?.checked   ?? false,
      themePrimary:          el('#set-theme-primary')?.value      || '#10bec9',
      themeAccent:           el('#set-theme-accent')?.value       || '#f2586e',
      overlays,
      backup: {
        backupEnabled:   el('#set-backup-enabled')?.checked ?? true,
        backupDirectory: el('#set-backup-dir')?.value.trim() ?? '',
      },
      auth: {
        authEnabled:      el('#set-auth-enabled')?.checked      ?? false,
        username:         el('#set-auth-username')?.value.trim() ?? 'admin',
        passwordHash:     '',
        sessionDaysValid: parseInt(el('#set-auth-days')?.value)  || 30,
      },
    },
    newPassword: el('#set-auth-password')?.value || null,
  };
}

async function saveSettings() {
  try {
    const payload = collectSettings();
    const res = await api('/settings', { method: 'POST', body: JSON.stringify(payload) });
    toast(res?.ratingsFetchStarted
      ? 'Settings saved. Fetching ratings & awards in the background — watch Activity, then use Apply All.'
      : 'Settings saved.');
    // Refresh the in-memory settings from the server so the poster/show grids
    // (which build badges from S.settings.overlays via allBadgesHtml) reflect the
    // new overlay choices — enabled types, styles, positions, opacity — next time
    // they render. Without this, only badge size propagated (via the CSS var below)
    // and every other overlay change silently required a full page reload.
    S.settings = await api('/settings') ?? payload.settings;
    applyTheme(S.settings?.themePrimary, S.settings?.themeAccent); refreshServerLabels();   // recolour UI + logo from saved theme
    // Update card badge CSS variables immediately so all cards reflect new size/opacity.
    // Uses badgeSizePct directly (no multiplier) so grid badges match the settings
    // preview and the poster rendered onto Plex.
    const pct = S.settings?.overlays?.badgeSizePct ?? 22;
    document.documentElement.style.setProperty('--card-badge-width', `${pct}%`);  // ribbons use this
    // Kometa-flavoured box: uniform HEIGHT (~6.6% at the default size of 22), radius, font —
    // all scaled by the badge-size slider. Width hugs the content (set per-badge).
    document.documentElement.style.setProperty('--card-box-height', `${(pct * 0.30).toFixed(2)}%`);
    document.documentElement.style.setProperty('--card-box-radius', `${(pct * 0.22).toFixed(1)}px`);
    document.documentElement.style.setProperty('--card-box-fontsize', `${(pct * 0.42).toFixed(1)}px`);
    // Fixed resolution-box width (fits the widest label, e.g. "1080P FHD") so variants left-align.
    document.documentElement.style.setProperty('--card-res-box-width', `${(pct * 1.52).toFixed(2)}%`);
  } catch(e) { toast(`Save failed: ${e.message}`, true); }
}

// ── Search ────────────────────────────────────────────────────────────────────
el('#search-box').addEventListener('input', applyGridFilters);
el('#filter-box').addEventListener('change', applyGridFilters);
el('#select-mode-btn').addEventListener('click', () => setSelectMode(!S.selectMode));
el('#sel-apply').addEventListener('click', () => runBulk('apply'));
el('#sel-dismiss').addEventListener('click', () => runBulk('dismiss'));
el('#sel-clear').addEventListener('click', () => setSelectMode(true)); // re-enter clears selection

// ── Init ──────────────────────────────────────────────────────────────────────
// Check if a scan is already running (e.g. after page refresh mid-scan)
(async () => {
  try {
    const status = await api('/library/scan/status');
    if (status?.isScanning) {
      el('#scan-progress-wrap').hidden = false;
      el('#scan-progress-text').textContent = 'Scan in progress…';
      el('#scan-progress-fill').style.width = '50%';
      el('#scan-status').textContent = 'Scanning…';
      el('#scan-btn').disabled = true;
      el('#live-dot').hidden = false;
    }
  } catch { /* non-fatal */ }
})();

// Populate the sidebar Health badge on boot (quick=1 skips the slow provider tests).
(async () => { try { updateHealthBadge(await api('/health?quick=true')); } catch { /* non-fatal */ } })();

// Apply the saved theme colours as early as possible (before the first grid renders) so the UI and
// logo come up in-theme rather than flashing the default teal/coral.
(async () => { try { await ensureSettingsLoaded(); applyTheme(S.settings?.themePrimary, S.settings?.themeAccent); refreshServerLabels(); } catch { /* non-fatal */ } })();

// Poster-size slider — drives the grid column width. Stored per-browser (a viewing preference, not a
// server setting), so it's instant and applies to every grid page. Backgrounds scale proportionally.
function applyPosterSize(px){
  px = Math.max(90, Math.min(300, px | 0)) || 180;
  document.documentElement.style.setProperty('--poster-min', px + 'px');
  document.documentElement.style.setProperty('--bg-min', Math.round(px * 1.55) + 'px');
}
(function initPosterSize(){
  const saved = parseInt(localStorage.getItem('posterSize')) || 180;
  const sl = el('#size-slider');
  if (sl){
    sl.value = saved;
    sl.addEventListener('input', () => { applyPosterSize(+sl.value); localStorage.setItem('posterSize', sl.value); });
  }
  applyPosterSize(saved);
})();

setView('movies');

