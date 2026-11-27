'use strict';

// ---------- Hằng số ----------
const TABS = [
  ['TotalTaskDue', 'Đến hạn hôm nay'],
  ['TotalTaskDueSoon', 'Sắp đến hạn'],
  ['TotalTaskExpire', 'Quá hạn'],
];
// match: lọc thêm khi một trạng thái thuộc nhiều cột (Queued trước phân tích ≠ Queued chờ code)
const COLS = [
  { id: 'analyze', title: 'Phân tích', hint: 'Kéo task AMIS vào đây — AI đọc yêu cầu (không đụng code). Cần bổ sung / Đã rõ yêu cầu hiện ở nhãn trên thẻ', statuses: ['Queued', 'Analyzing', 'NeedsInfo', 'Clarified'], match: j => j.status !== 'Queued' || !j.codeApproved, drop: true },
  { id: 'working', title: 'AI đang làm', hint: 'Code, ship-check, review, build, test e2e', statuses: ['Queued', 'Fetching', 'Coding', 'Fixing', 'Reviewing', 'Testing'], match: j => j.status !== 'Queued' || j.codeApproved },
  { id: 'approve', title: 'Chờ bạn', hint: 'Trả lời câu hỏi của AI, hoặc xem diff rồi duyệt', statuses: ['WaitingForUser', 'AwaitingApproval'] },
  { id: 'finished', title: 'Đã xong', hint: '', statuses: ['Approved', 'Failed', 'Cancelled', 'Rejected'], tabs: [
    { id: 'ok', label: '✓ Hoàn thành', hint: 'Đã duyệt', statuses: ['Approved'] },
    { id: 'stop', label: '⏹ Dừng/Huỷ', hint: 'Lỗi, bị huỷ hoặc bị từ chối — mở để chạy tiếp hoặc xem lỗi', statuses: ['Failed', 'Cancelled', 'Rejected'] },
  ] },
];
const STATUS_LABEL = {
  Queued: 'Chờ chạy', Analyzing: 'Đang phân tích', NeedsInfo: 'Cần bổ sung', Clarified: 'Đã rõ yêu cầu', Fetching: 'Đọc AMIS', Coding: 'Đang code', Fixing: 'Sửa theo review',
  Reviewing: 'Đang review', Testing: 'Đang test', WaitingForUser: 'Cần bạn trả lời', AwaitingApproval: 'Chờ duyệt',
  Approved: 'Đã duyệt', Rejected: 'Từ chối', Failed: 'Lỗi', Cancelled: 'Đã huỷ',
};
const NEEDS_YOU = new Set(['WaitingForUser', 'NeedsInfo', 'Clarified']);
const ACCEPT_FILES = '.docx,.xlsx,.xlsm,.pptx,.pdf,.txt,.md,.csv,.json,.sql,.png,.jpg,.jpeg,.gif,.webp,.bmp';
const RUNNING = new Set(['Analyzing', 'Fetching', 'Coding', 'Reviewing', 'Fixing', 'Testing']);
const FINAL = new Set(['Approved', 'Rejected', 'Failed', 'Cancelled']);
const DOCS = [
  ['analysis.md', 'Phân tích'], ['task.md', 'Yêu cầu'], ['build.txt', 'Build'],
  ['test-data.md', 'Dữ liệu test'], ['test-data.sql', 'SQL tạo dữ liệu'], ['test-cleanup.sql', 'SQL dọn'], ['plan.md', 'Kế hoạch'], ['questions.md', 'Câu hỏi'],
  ['assumptions.md', 'Giả định ⚠'], ['review.md', 'Review'], ['test.md', 'Test'], ['testcases.md', 'Test case'], ['code.result.md', 'Tóm tắt code'], ['pr.md', 'Mô tả PR'],
];

// ---------- State ----------
const S = {
  config: null,
  me: null,
  meError: null,
  tab: localGet('tab') || 'TotalTaskDue',
  tasks: {},          // tab -> list
  tasksError: {},     // tab -> message
  tasksLoading: {},
  jobs: new Map(),
  openJob: null,
  drawerTab: 'log',
  doc: 'task.md',
  dragging: false,
  pendingStart: null,
  repo: null,         // { info, busy }
  repoError: null,
  branches: null,
  branchFilter: 'local',
  chatTask: null,     // { taskId, taskName } của khung chat đang mở
  chats: new Map(),   // taskId -> { thread, busy }
  chatDrafts: {},
  chatFiles: {},
  taskQuery: '',      // ô tìm task trong cột AMIS
  batchSel: new Map(), // taskId -> task AMIS đã tick để chạy hàng loạt
  drift: new Map(),    // jobId -> { at, key, list } — nhánh cha của job nối tiếp đã đổi chưa
  autopilot: null,    // trạng thái chế độ tự động
  testdb: null,       // DB AI đọc + DB ghi dữ liệu test
  projectFilter: localGet('project') || '',  // '' = tất cả, '@branch' = khớp nhánh hiện tại, còn lại = mã dự án      // taskId -> File[] chờ gửi (đính kèm, dán ảnh, kéo thả)
};

// ---------- Tiện ích ----------
const $ = (sel, el = document) => el.querySelector(sel);
const $$ = (sel, el = document) => [...el.querySelectorAll(sel)];

// replaceChildren/append đổi null thành chữ "null" — bỏ qua null/false để viết `cond ? el : null` cho gọn
for (const name of ['replaceChildren', 'append']) {
  const orig = Element.prototype[name];
  Element.prototype[name] = function (...kids) { return orig.apply(this, kids.filter(k => k != null && k !== false)); };
}

// ---------- Trạng thái đang xử lý (loading) ----------
/**
 * Mọi nút: handler click trả về Promise (gọi server, chờ popup…) thì nút được khoá + hiện vòng xoay tới khi xong,
 * tránh bấm hai lần. Vòng xoay chỉ hiện sau 150ms (thao tác nhanh không bị nháy) và khi đang có request chạy
 * (lúc đang mở popup xác nhận thì chỉ khoá, không xoay).
 */
const NET = { pending: 0, writes: 0 };
(() => {
  const orig = HTMLButtonElement.prototype.addEventListener;
  HTMLButtonElement.prototype.addEventListener = function (type, fn, opts) {
    if (type !== 'click' || typeof fn !== 'function') return orig.call(this, type, fn, opts);
    return orig.call(this, type, function (ev) {
      if (this.classList.contains('pending')) { ev.preventDefault(); return; }
      const r = fn.call(this, ev);
      if (r && typeof r.then === 'function') {
        const btn = this;
        btn.classList.add('pending');
        btn.setAttribute('aria-busy', 'true');
        const t = setTimeout(() => btn.classList.add('spin'), 150);
        const done = () => { clearTimeout(t); btn.classList.remove('pending', 'spin'); btn.removeAttribute('aria-busy'); };
        r.then(done, done);
      }
      return r;
    }, opts);
  };
})();

/** Thanh tiến trình trên cùng: hiện khi có thao tác ghi (POST/PUT/DELETE) đang chạy — không hiện cho lần tự làm mới định kỳ. */
function netChanged() {
  if (!document.body) return;
  document.body.classList.toggle('net-busy', NET.pending > 0);
  let bar = document.getElementById('netBar');
  if (!bar) { bar = h('div', { id: 'netBar' }); document.body.append(bar); }
  clearTimeout(netChanged.t);
  if (NET.writes > 0) netChanged.t = setTimeout(() => NET.writes > 0 && bar.classList.add('on'), 120);
  else bar.classList.remove('on');
}

/** Task sửa lỗi QC: "SE xử lý" / "SE xử lý bug" (khớp với Job.QcFix ở server). */
const isQcFix = name => /\bSE\s*[-–:]?\s*x[ửưu]\s*l[ýyíi]/i.test(String(name || '').normalize('NFC'));
/** Vòng QC thứ mấy của task cha: đếm các job sửa lỗi QC cùng task cha theo thời gian tạo. */
function qcRound(j) {
  if (!j.qcFix || !j.parentTaskId) return null;
  const list = [...S.jobs.values()].filter(x => x.qcFix && x.parentTaskId === j.parentTaskId && x.taskId !== j.taskId);
  const earlier = new Set(list.filter(x => new Date(x.createdAt) < new Date(j.createdAt)).map(x => x.taskId));
  return earlier.size + 1;
}

function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

async function api(path, opts = {}) {
  const write = !!opts.method && opts.method.toUpperCase() !== 'GET';
  NET.pending++; if (write) NET.writes++;
  netChanged();
  try { return await apiRaw(path, opts); }
  finally { NET.pending--; if (write) NET.writes--; netChanged(); }
}

async function apiRaw(path, opts) {
  const res = await fetch('/api' + path, opts);
  const ct = res.headers.get('content-type') || '';
  const body = ct.includes('json') ? await res.json() : await res.text();
  if (!res.ok) {
    const msg = (body && (body.error || body.detail || body.title)) || (typeof body === 'string' && body) || res.statusText;
    const err = new Error(msg);
    err.status = res.status;
    throw err;
  }
  return body;
}

function localGet(k) { try { return localStorage.getItem('sa.' + k); } catch { return null; } }
function localSet(k, v) { try { localStorage.setItem('sa.' + k, v); } catch { /* trình duyệt chặn storage: chỉ mất ghi nhớ tab */ } }

let toastTimer;
function toast(msg, bad = false) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast show' + (bad ? ' bad' : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.className = 'toast'; }, bad ? 6000 : 3000);
}

function parseDate(s) { if (!s) return null; const d = new Date(s); return isNaN(d) ? null : d; }
function fmtDate(s) {
  const d = parseDate(s);
  if (!d) return '';
  return d.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit', year: 'numeric' }) +
    ' ' + d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
}
function dueClass(s) {
  const d = parseDate(s);
  if (!d) return '';
  const now = new Date();
  if (d < now) return 'overdue';
  if (d.toDateString() === now.toDateString()) return 'today';
  return '';
}
function fmtDuration(ms) {
  if (ms < 0 || !isFinite(ms)) return '';
  const s = Math.floor(ms / 1000), m = Math.floor(s / 60), hh = Math.floor(m / 60);
  return hh ? `${hh}g${String(m % 60).padStart(2, '0')}` : m ? `${m}p${String(s % 60).padStart(2, '0')}` : `${s}s`;
}
function elapsedText(j) {
  const start = parseDate(j.startedAt);
  if (!start) return '';
  const end = parseDate(j.finishedAt) || (RUNNING.has(j.status) ? new Date() : null);
  return end ? fmtDuration(end - start) : '';
}
function initials(name) {
  const parts = (name || '?').trim().split(/\s+/);
  return ((parts.length > 1 ? parts[parts.length - 2][0] : '') + parts[parts.length - 1][0]).toUpperCase();
}
function verdictChip(label, v) {
  if (!v) return null;
  const cls = v === 'PASS' || v === 'DONE' ? 'pass' : v === 'FAIL' || v === 'BLOCKED' ? 'fail' : 'skip';
  const tip = v === 'PARTIAL' ? 'Phần kiểm được đều đạt, nhưng còn tiêu chí giao diện chưa kiểm được (môi trường) — xem đầu test.md' : null;
  return h('span', { class: 'chip ' + cls, title: tip }, `${label}: ${v}`);
}
function activeJobForTask(taskId) {
  for (const j of S.jobs.values()) if (j.taskId === taskId && !FINAL.has(j.status)) return j;
  return null;
}

// ---------- Tải dữ liệu ----------
async function loadMe() {
  try {
    S.me = await api('/me');
    S.meError = null;
  } catch (e) {
    S.me = null;
    S.meError = e.message;
  }
  renderMe();
}

async function loadTasks(tab = S.tab) {
  S.tasksLoading[tab] = true;
  render();
  try {
    S.tasks[tab] = await api('/tasks?type=' + tab);
    S.tasksError[tab] = null;
  } catch (e) {
    S.tasksError[tab] = e.message;
  } finally {
    S.tasksLoading[tab] = false;
    render();
  }
}

async function loadJobs() {
  const list = await api('/jobs');
  S.jobs = new Map(list.map(j => [j.id, j]));
  render();
  renderBriefBadge();
}

// ---------- Header ----------
function renderMe() {
  const box = $('#me');
  box.classList.toggle('error', !!S.meError);
  const name = S.me ? S.me.fullName : S.meError ? 'Chưa kết nối AMIS' : 'Đang tải…';
  const sub = S.me ? [S.me.email, S.me.userName].filter(Boolean).join(' · ') : (S.meError || '');
  box.replaceChildren(
    h('div', { class: 'avatar', title: S.me ? S.me.tenantName || '' : '' }, S.me ? initials(S.me.fullName) : '!'),
    h('div', { class: 'me-text' }, h('b', { title: name }, name), h('small', { title: sub }, sub)),
  );
}

// ---------- Source code ----------
async function loadRepo() {
  try {
    S.repo = await api('/repo');
    S.repoError = null;
  } catch (e) {
    S.repoError = e.message;
  }
  renderRepo();
  if ($('#dlgBranch').open) renderRepoDetail();
  if ($('#dlgStart').open) updateStartWarning();
  render(); // dấu "Khác nhánh" trên thẻ task và bộ lọc "Khớp nhánh hiện tại" phụ thuộc nhánh
}

let repoTimer;
function loadRepoSoon() { clearTimeout(repoTimer); repoTimer = setTimeout(loadRepo, 600); }

function changesText(i) {
  const n = i.changed + i.untracked;
  return n ? `${i.changed} file sửa${i.untracked ? `, ${i.untracked} file mới` : ''}` : 'Sạch';
}

function renderRepo() {
  const btn = $('#repo');
  btn.classList.toggle('error', !!S.repoError);
  const i = S.repo && S.repo.info;
  btn.classList.toggle('ai', !!(i && i.isAiBranch));
  const text = $('.repo-text', btn);
  if (!i) {
    text.replaceChildren(h('b', {}, 'Source code'), h('small', {}, S.repoError || 'Đang đọc repo…'));
    return;
  }
  const sync = [i.ahead ? `↑${i.ahead}` : null, i.behind ? `↓${i.behind}` : null].filter(Boolean).join(' ');
  text.replaceChildren(
    h('b', { title: i.path }, i.name, ' · ', h('span', { class: 'br' }, i.detached ? `(detached ${i.shortSha})` : i.branch)),
    h('small', {},
      h('span', { class: i.changed + i.untracked ? 'dirty' : 'clean' }, changesText(i)),
      sync ? h('span', { title: `So với ${i.upstream}` }, sync) : null,
      S.repo.busy ? h('span', {}, '· AI đang dùng') : null,
      h('span', { title: i.subject }, `${i.shortSha} ${i.subject || ''}`)));
  btn.title = `${i.path}\nNhánh: ${i.branch}${i.upstream ? ` → ${i.upstream}` : ''}\nCommit: ${i.shortSha} ${i.subject}\nBấm để đổi nhánh`;
}

async function openBranchDialog(search) {
  $('#inpBranch').value = typeof search === 'string' ? search : '';
  $('#dlgBranch').showModal();
  renderRepoDetail();
  await loadBranches();
  $('#inpBranch').focus();
}

async function loadBranches() {
  const list = $('#branchList');
  list.replaceChildren(h('div', { class: 'loading' }, 'Đang đọc danh sách nhánh…'));
  try {
    S.branches = await api('/repo/branches');
    renderBranchList();
  } catch (e) {
    list.replaceChildren(h('div', { class: 'errbox' }, e.message));
  }
}

function renderRepoDetail() {
  const box = $('#repoDetail');
  const i = S.repo && S.repo.info;
  if (!i) { box.replaceChildren(h('div', { class: 'errbox' }, S.repoError || 'Chưa đọc được repo.')); return; }
  const dirtyN = i.changed + i.untracked;
  box.replaceChildren(h('div', { class: 'repo-card' },
    h('dl', { class: 'kv' },
      h('dt', {}, 'Repo'), h('dd', {}, h('b', {}, i.name), ' — ', h('code', {}, i.path)),
      i.remoteUrl ? [h('dt', {}, 'Remote'), h('dd', {}, h('code', {}, i.remoteUrl))] : null,
      h('dt', {}, 'Nhánh'), h('dd', {}, h('code', {}, i.branch), i.upstream ? ` → ${i.upstream}` : ' (chưa có upstream)',
        i.ahead || i.behind ? `  · ${i.ahead} commit chưa push, ${i.behind} commit chưa kéo về` : ''),
      h('dt', {}, 'Commit'), h('dd', {}, h('code', {}, i.shortSha), ' ', i.subject, h('span', { class: 'muted' }, ` — ${i.author}, ${fmtDate(i.date)}`)),
      h('dt', {}, 'Thay đổi'), h('dd', { class: dirtyN ? 'due today' : '' }, changesText(i))),
    i.changedFiles && i.changedFiles.length
      ? h('details', {}, h('summary', {}, 'Xem file đang sửa'), h('pre', {}, i.changedFiles.join('\n') + (i.changed > 50 ? '\n…' : '')))
      : null,
    i.changed ? h('div', { class: 'notice', style: 'margin-top:8px' }, 'Có thay đổi chưa commit: không đổi nhánh được và AI cũng không chạy được. Commit hoặc stash trước.') : null,
    S.repo.busy ? h('div', { class: 'notice', style: 'margin-top:8px' }, 'AI đang chạy job trên repo này — tạm khoá đổi nhánh.') : null));
}

function renderBranchList() {
  const list = $('#branchList');
  if (!S.branches) return;
  const q = $('#inpBranch').value.trim().toLowerCase();
  const f = S.branchFilter;
  const rows = S.branches.filter(b =>
    (f === 'local' ? !b.remote : f === 'remote' ? b.remote : b.ai) &&
    (!q || b.name.toLowerCase().includes(q)));
  document.querySelectorAll('#branchFilter button').forEach(b => b.classList.toggle('active', b.dataset.f === f));
  if (!rows.length) { list.replaceChildren(h('div', { class: 'empty' }, 'Không có nhánh phù hợp.')); return; }
  list.replaceChildren(...rows.slice(0, 300).map(b => h('button', {
    type: 'button',
    class: 'branch-row' + (b.current ? ' current' : ''),
    onclick: () => { if (!b.current) switchBranch(b.name, b.remote); },
  },
    h('span', { class: 'bn' }, b.current ? '✓ ' : '', b.name, b.remote ? h('span', { class: 'muted' }, '  (remote)') : null),
    h('span', { class: 'bd' }, fmtDate(b.date)),
    h('span', { class: 'bs' }, b.subject || ''))));
}

async function switchBranch(name, remote) {
  const i = S.repo && S.repo.info;
  const msg = remote
    ? `Tạo nhánh local "${name}" theo dõi origin/${name} và chuyển sang?`
    : `Chuyển ${i ? i.name : 'repo'} từ "${i ? i.branch : '?'}" sang "${name}"?`;
  if (!await ask(msg)) return;
  try {
    await api('/repo/switch', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ branch: name }) });
    toast('Đã chuyển sang ' + name);
    await Promise.all([loadRepo(), loadBranches()]);
  } catch (e) { toast(e.message, true); }
}

async function fetchRemote() {
  const btn = $('#btnFetch');
  btn.disabled = true;
  btn.textContent = 'Đang fetch…';
  try {
    await api('/repo/fetch', { method: 'POST' });
    toast('Đã cập nhật danh sách nhánh từ remote');
    await Promise.all([loadRepo(), loadBranches()]);
  } catch (e) { toast(e.message, true); }
  finally { btn.disabled = false; btn.textContent = 'Cập nhật từ remote'; }
}

// ---------- Board ----------
let renderQueued = false;
let lastBoardRender = 0;
function render() {
  if (S.dragging) return; // vẽ lại khi đang kéo sẽ làm mất phần tử nguồn
  if (renderQueued) return;
  renderQueued = true;
  // Gom các cập nhật dồn dập (nhiều job đổi trạng thái liền nhau) thành một lần vẽ, cách nhau ít nhất 250ms
  const wait = Math.max(0, 250 - (performance.now() - lastBoardRender));
  setTimeout(() => requestAnimationFrame(() => { renderQueued = false; lastBoardRender = performance.now(); renderBoard(); }), wait);
}

function renderBoard() {
  const board = $('#board');
  const scroll = [...board.querySelectorAll('.col-body')].map(b => b.scrollTop);
  const active = document.activeElement;
  const focusSearch = active && active.id === 'taskSearch' ? [active.selectionStart, active.selectionEnd] : null;
  syncPhase();
  const before = snapshotCards(board);
  board.replaceChildren(renderAmisColumn(), ...COLS.map(renderJobColumn));
  board.querySelectorAll('.col-body').forEach((b, i) => { b.scrollTop = scroll[i] || 0; });
  if (focusSearch) { const el = $('#taskSearch'); el.focus(); el.setSelectionRange(...focusSearch); }
  animateCards(board, before);
  renderOverview();
  animateCounts(board);
}

// ---------- Chuyển động kiểu Remotion: spring, FLIP, số chạy ----------
// Đường cong spring (vọt nhẹ rồi lắng) — dùng cho cả CSS (var(--spring)) lẫn Web Animations
const SPRING = 'linear(0, 0.009, 0.035 2.1%, 0.141, 0.281 6.7%, 0.723 12.9%, 0.938 16.7%, 1.017, 1.077, 1.121, 1.149 24.3%, 1.159, 1.163, 1.161, 1.154 29.9%, 1.129 32.8%, 1.051 39.6%, 1.017 43.1%, 0.991, 0.977 51%, 0.974 53.8%, 0.975 57.1%, 0.997 69.8%, 1.003 76.9%, 1)';
const motionOk = () => !matchMedia('(prefers-reduced-motion: reduce)').matches && !document.hidden;
let boardShown = false;
const COUNTS = new Map();
const PIPE_SEEN = new Map(); // jobId|full → số bước đã xong lần vẽ trước: chỉ bước MỚI xong mới chạy hiệu ứng mọc ra

/** Hiệu ứng lặp vô hạn khoá pha theo đồng hồ (animation-delay âm) — vẽ lại DOM liên tục không làm giật vòng xoay/vệt sáng. */
let phaseSet = false;
function syncPhase() {
  if (phaseSet) return;
  phaseSet = true;
  const now = Date.now(), r = document.documentElement.style;
  for (const p of [1200, 1300, 1400, 1500, 1600, 1800, 2400]) r.setProperty('--ph-' + p, -(now % p) + 'ms');
}

/** Vị trí + trạng thái mọi thẻ job trước khi vẽ lại. */
let lastShape = '';
function snapshotCards(board) {
  const m = new Map();
  // "Hình dạng" bảng: job nào ở trạng thái nào. Không đổi thì không cần đo vị trí (đo = ép trình duyệt tính lại bố cục)
  const shape = [...S.jobs.values()].map(j => j.id + ':' + j.status).sort().join('|');
  const changed = shape !== lastShape;
  lastShape = shape;
  if (!boardShown || !changed) return m;
  board.querySelectorAll('.card.job[data-id]').forEach(c => m.set(c.dataset.id, { r: c.getBoundingClientRect(), s: c.dataset.status }));
  return m;
}

/** FLIP: thẻ đổi cột bay từ chỗ cũ sang chỗ mới (bản sao nổi trên cùng, vì cột cắt phần tràn); thẻ mới bật ra; thẻ đổi trạng thái loé sáng. */
function animateCards(board, before) {
  const first = !boardShown;
  boardShown = true;
  if (first || !motionOk() || before.size === 0) return;
  board.querySelectorAll('.card.job[data-id]').forEach(c => {
    const p = before.get(c.dataset.id);
    if (!p) {
      c.animate([{ opacity: 0, transform: 'translateY(12px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 520, easing: SPRING });
      return;
    }
    if (p.s !== c.dataset.status) {
      c.classList.add('just-changed');
      setTimeout(() => c.classList.remove('just-changed'), 1600);
    }
    const r = c.getBoundingClientRect();
    const dx = p.r.left - r.left, dy = p.r.top - r.top;
    if (Math.abs(dx) < 2 && Math.abs(dy) < 2) return;
    if (Math.abs(dx) < 40) { // cùng cột: trượt lên/xuống
      c.animate([{ transform: `translateY(${dy}px)` }, { transform: 'none' }], { duration: 480, easing: SPRING });
      return;
    }
    const ghost = c.cloneNode(true);
    ghost.classList.add('card-ghost');
    Object.assign(ghost.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
    document.body.append(ghost);
    c.style.visibility = 'hidden';
    ghost.animate([
      { transform: `translate(${dx}px, ${dy}px) scale(1)`, boxShadow: '0 4px 12px rgba(20,30,60,.12)' },
      { transform: `translate(${dx * 0.35}px, ${dy * 0.35 - 18}px) scale(1.04) rotate(${dx > 0 ? 1.5 : -1.5}deg)`, boxShadow: '0 22px 48px rgba(20,30,60,.28)', offset: 0.45 },
      { transform: 'none', boxShadow: '0 4px 12px rgba(20,30,60,.12)' },
    ], { duration: 760, easing: 'cubic-bezier(.3,.7,.2,1)' }).finished.finally(() => { ghost.remove(); c.style.visibility = ''; });
  });
}

/** Số (data-ck = khoá) chạy từ giá trị cũ tới mới. data-fmt="usd" thì hiện $0.00. */
function animateCounts(root) {
  root.querySelectorAll('[data-ck]').forEach(el => {
    const key = el.dataset.ck, to = Number(el.dataset.val ?? el.textContent) || 0;
    const from = COUNTS.get(key);
    COUNTS.set(key, to);
    const fmt = v => el.dataset.fmt === 'usd' ? '$' + v.toFixed(2) : String(Math.round(v));
    if (from === undefined || from === to || !motionOk()) { el.textContent = fmt(to); return; }
    const t0 = performance.now(), dur = 700;
    el.classList.add('counting');
    const step = now => {
      const k = Math.min(1, (now - t0) / dur), e = 1 - Math.pow(1 - k, 3);
      el.textContent = fmt(from + (to - from) * e);
      if (k < 1) requestAnimationFrame(step); else el.classList.remove('counting');
    };
    requestAnimationFrame(step);
  });
}

// ---------- Tiến trình pipeline ----------
const PIPE = [
  ['analyze', 'Phân tích'], ['code', 'Code'], ['review', 'Review'], ['test', 'Test'], ['ship', 'Duyệt · PR'],
];

/** Trạng thái từng bước: done | active | you | fail | warn | stop | queue | todo, kèm chú thích ngắn. */
function pipelineSteps(j) {
  const st = j.status;
  const idx = k => PIPE.findIndex(p => p[0] === k);
  let cur, curState, note = '';
  if (st === 'Queued') { cur = j.codeApproved ? 'code' : 'analyze'; curState = 'queue'; note = 'chờ tới lượt'; }
  else if (st === 'Analyzing' || (st === 'Fetching' && !j.codeApproved)) { cur = 'analyze'; curState = 'active'; note = 'AI đang đọc yêu cầu'; }
  else if (st === 'NeedsInfo') { cur = 'analyze'; curState = 'you'; note = 'cần bạn bổ sung'; }
  else if (st === 'Clarified') { cur = 'code'; curState = 'you'; note = 'chờ bạn giao code'; }
  else if (st === 'Fetching' || st === 'Coding') { cur = 'code'; curState = 'active'; note = 'AI đang code'; }
  else if (st === 'WaitingForUser') { cur = 'code'; curState = 'you'; note = 'AI đang hỏi bạn'; }
  else if (st === 'Reviewing') { cur = 'review'; curState = 'active'; note = 'review độc lập'; }
  else if (st === 'Fixing') { cur = 'review'; curState = 'active'; note = `sửa theo review${j.fixRounds ? ' vòng ' + j.fixRounds : ''}`; }
  else if (st === 'Testing') { cur = 'test'; curState = 'active'; note = 'build + test'; }
  else if (st === 'AwaitingApproval') { cur = 'ship'; curState = 'you'; note = 'chờ bạn duyệt'; }
  else if (st === 'Approved') { cur = null; note = Object.keys(j.prUrls || {}).length ? 'đã có PR' : 'đã duyệt, chưa PR'; }
  else {
    cur = j.testVerdict ? 'ship' : j.reviewVerdict ? 'test' : (j.codeStatus || j.headSha || j.held) ? 'review' : j.codeApproved ? 'code' : 'analyze';
    curState = st === 'Failed' ? 'fail' : 'stop';
    note = st === 'Failed' ? 'lỗi' : st === 'Rejected' ? 'từ chối' : 'đã huỷ';
  }
  const ci = cur === null ? PIPE.length : idx(cur);
  return PIPE.map(([key, label], i) => {
    let state = i < ci ? 'done' : i === ci ? curState : 'todo';
    let sub = i === ci ? note : '';
    if (state === 'done' && key === 'review' && j.reviewVerdict && j.reviewVerdict !== 'PASS') { state = 'warn'; sub = 'review ' + j.reviewVerdict; }
    if (state === 'done' && key === 'test' && j.testVerdict && j.testVerdict !== 'PASS') { state = j.testVerdict === 'FAIL' ? 'fail-soft' : 'warn'; sub = 'test ' + j.testVerdict; }
    if (state === 'done' && key === 'ship' && cur === null) sub = note;
    if (state === 'done' && key === 'analyze' && j.requirement && j.requirement !== 'CLEAR') sub = 'yêu cầu thiếu';
    return { key, label, state, sub };
  });
}

/** Thanh pipeline: compact trên thẻ, đầy đủ (nhãn + chú thích) trong drawer. */
function renderPipe(j, full = false) {
  const steps = pipelineSteps(j);
  const doneN = steps.filter(s => s.state === 'done' || s.state === 'warn' || s.state === 'fail-soft').length;
  const seenKey = j.id + (full ? '|f' : '');
  const prevDone = PIPE_SEEN.has(seenKey) ? PIPE_SEEN.get(seenKey) : (boardShown ? 0 : doneN);
  PIPE_SEEN.set(seenKey, doneN);
  const title = steps.map(s => `${s.label}: ${s.sub || ({ done: 'xong', active: 'đang chạy', you: 'chờ bạn', todo: 'chưa tới', queue: 'chờ lượt', fail: 'lỗi', stop: 'dừng', warn: 'có lưu ý', 'fail-soft': 'FAIL' })[s.state]}`).join('\n');
  return h('div', { class: 'pipe' + (full ? ' full' : ''), title: full ? null : title, style: `--pipe-done:${doneN}` },
    steps.map((s, i) => h('div', { class: 'pipe-step ps-' + s.state + (i >= prevDone && i < doneN && motionOk() ? ' new' : ''), style: `--i:${i - prevDone}` },
      h('span', { class: 'pipe-bar' }),
      full ? h('span', { class: 'pipe-dot' }, s.state === 'done' ? '✓' : s.state === 'fail' || s.state === 'fail-soft' ? '!' : s.state === 'you' ? '●' : i + 1) : null,
      full ? h('span', { class: 'pipe-label' }, s.label) : null,
      full && s.sub ? h('span', { class: 'pipe-sub' }, s.sub) : null)));
}

/** Câu "bước tiếp theo" cho drawer: ai đang cần làm gì. tone: run | you | warn | done | bad. */
function nextStep(j) {
  const st = j.status, id = j.taskId;
  const pr = Object.keys(j.prUrls || {}).length;
  if (st === 'Queued') return ['run', 'Đang chờ tới lượt — tool chạy lần lượt từng task trong repo.'];
  if (RUNNING.has(st)) return ['run', `AI đang ${(STATUS_LABEL[st] || st).replace(/^Đang /i, '').toLowerCase()} — bạn chưa cần làm gì, xong tool sẽ báo.`];
  if (st === 'NeedsInfo') return ['you', 'Yêu cầu còn thiếu: trả lời câu hỏi trong tab Chat (hoặc "Copy tin gửi BA"), rồi "Gửi & phân tích lại".'];
  if (st === 'Clarified') return ['you', 'Yêu cầu đã rõ: đọc Tài liệu › Phân tích, đồng ý thì bấm "Bắt đầu code".'];
  if (st === 'WaitingForUser') return ['you', 'AI đang hỏi bạn giữa lúc code — trả lời trong tab Chat để AI làm tiếp.'];
  if (st === 'AwaitingApproval') {
    const warn = j.testVerdict === 'FAIL' || j.reviewVerdict === 'FAIL';
    return [warn ? 'warn' : 'you', (warn ? `⚠ ${j.reviewVerdict === 'FAIL' ? 'Review FAIL' : 'Test FAIL'} — đọc Tài liệu › ${j.reviewVerdict === 'FAIL' ? 'Review' : 'Test'} trước. ` : '') +
      `Xem tab Diff rồi ${j.held ? '"Tạo commit & push…"' : '"Duyệt & push"'}, hoặc gõ trong nhóm chat: #${id} push (commit + push + PR) · #${id} test lại.`];
  }
  if (st === 'Approved') return pr ? ['done', `Đã có ${pr} pull request — chờ review/merge trên TFS.`] : ['you', `Đã duyệt nhưng chưa push: bấm "Push" / "Tạo PR", hoặc gõ #${id} push.`];
  if (st === 'Failed') return ['bad', 'Job lỗi — đọc thông báo đỏ bên dưới, rồi "Chạy tiếp" / "Chạy lại bước code" (dùng lại phần đã làm).'];
  return ['done', st === 'Rejected' ? 'Đã từ chối — lý do được ghi lại để AI rút kinh nghiệm.' : 'Đã huỷ.'];
}

function renderDrawerFlow(j) {
  syncPhase();
  const el = $('#drawer .drawer-flow');
  if (!el) return;
  if (!j) { el.replaceChildren(); return; }
  const [tone, text] = nextStep(j);
  if (TL.id === j.id) scheduleTimeline(); // trạng thái đổi → chặng cuối đổi đang chạy/xong/lỗi
  const key = j.id + '|' + j.status;
  const changed = el.dataset.key !== key;
  el.dataset.key = key;
  el.replaceChildren(
    h('div', { class: 'next-step ns-' + tone + (changed && motionOk() ? ' enter' : '') },
      h('span', { class: 'ns-ic' }, { run: '⚙', you: '👉', warn: '⚠', done: '✓', bad: '!' }[tone]),
      h('span', {}, h('b', {}, 'Bước tiếp theo: '), text)));
}

// ---------- Dải tổng quan ----------
function renderOverview() {
  const el = $('#overview');
  if (!el) return;
  const jobs = [...S.jobs.values()];
  const today = new Date().toDateString();
  const isToday = s => { const d = parseDate(s); return d && d.toDateString() === today; };
  const running = jobs.filter(j => RUNNING.has(j.status));
  const queued = jobs.filter(j => j.status === 'Queued').length;
  const you = needsYou();
  const doneToday = jobs.filter(j => j.status === 'Approved' && isToday(j.finishedAt)).length;
  const costToday = jobs.filter(j => isToday(j.startedAt) || isToday(j.finishedAt)).reduce((a, j) => a + (j.costUsd || 0), 0);
  const live = running.find(j => !j.codeApproved ? false : true) || running[0];
  const kpi = (cls, icon, label, val, sub, onclick, fmt) => h('button', { type: 'button', class: 'ov-kpi ' + cls, onclick, disabled: !onclick },
    h('span', { class: 'ov-ic' }, icon),
    h('span', { class: 'ov-txt' },
      h('span', { class: 'ov-val', 'data-ck': 'ov-' + cls, 'data-val': val, 'data-fmt': fmt || null }, fmt === 'usd' ? '$' + val.toFixed(2) : val),
      h('span', { class: 'ov-label' }, label),
      sub ? h('span', { class: 'ov-sub' }, sub) : null));
  const scrollTo = id => () => { const c = [...$('#board').children][1 + COLS.findIndex(x => x.id === id)]; if (c) c.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' }); };
  el.classList.toggle('first', !el.dataset.shown);
  el.dataset.shown = '1';
  el.replaceChildren(
    kpi('run' + (running.length ? ' live' : ''), '⚙', 'AI đang làm', running.length, queued ? `+${queued} chờ lượt` : (live ? STATUS_LABEL[live.status] : 'đang rảnh'), running.length ? scrollTo('working') : null),
    kpi('you' + (you.length ? ' hot' : ''), '👉', 'Chờ bạn', you.length, you.length ? you.slice(0, 2).map(j => '#' + j.taskId).join(' · ') + (you.length > 2 ? ' …' : '') : 'không có gì', you.length ? scrollTo('approve') : null),
    kpi('done', '✓', 'Xong hôm nay', doneToday, '', null),
    kpi('cost', '$', 'Chi phí hôm nay', Math.round(costToday * 100) / 100, 'theo job chạy hôm nay', () => openCosts(), 'usd'),
    envPanel(queued),
    live ? h('div', { class: 'ov-live', title: live.lastLog || '' },
      h('span', { class: 'running-dot' }),
      h('b', {}, '#' + live.taskId + ' '), h('span', { class: 'ov-live-st' }, STATUS_LABEL[live.status] + ' · '),
      h('span', { class: 'ov-live-log' }, live.lastLog || '…'),
      renderPipe(live)) : null);
  animateCounts(el);
  renderFocus();
}

// ---------- Ô "Môi trường · Lịch" (hàng KPI) ----------
S.health = null;
let healthLoading = false, dueLoaded = false;
async function loadHealth(force = false) {
  if (healthLoading) return;
  healthLoading = true;
  try { S.health = await api('/health' + (force ? '?force=true' : '')); } catch (e) { S.health = { error: e.message, items: [] }; }
  finally { healthLoading = false; renderOverview(); }
}
// Chấm Portal trên ô Môi trường: bật dev server (vỏ / module) hoặc tắt các dev server do tool bật
async function portalAction(x) {
  try {
    if (x.action === 'portal-stop') {
      if (!confirm(`Tắt các dev server Portal do tool bật?\n${x.detail}\n(Dev server bạn tự chạy không bị đụng tới.)`)) return;
      const r = await api('/portal/stop-started', { method: 'POST' });
      toast(`Đã tắt ${r.stopped} dev server do tool bật.`);
    } else {
      const list = x.action.slice('portal-start:'.length).split(',').filter(Boolean);
      let picked = list;
      if (list.length > 1) {
        picked = await pickPortalModules(list);
        if (!picked?.length) return;
      } else if (!await ask(list[0] === 'shell' ? `Bật vỏ Portal (${x.name})?` : `Bật module ${list[0]}?`)) return;
      const rs = await Promise.all(picked.map(project =>
        api('/portal/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project }) })
          .then(r => r.message, e => `${project}: ${e.message}`)));
      toast(picked.length === 1 ? rs[0] : `Đang bật ${picked.length} module: ${picked.join(', ')} — build lần đầu 2–5 phút, bấm ↻ ở ô Môi trường để xem.`);
    }
  } catch (e) { toast(e.message, true); }
  setTimeout(() => loadHealth(true), 1500);
}
// Popup chọn nhiều module Portal để bật (checkbox + Chọn hết/Bỏ hết, đếm RAM ước tính). Trả mảng tên hoặc null khi Huỷ.
function pickPortalModules(list) {
  return pickList({
    icon: '▶', title: 'Bật module Portal', ok: 'Bật',
    text: 'Chọn các module đang tắt cần bật. Build lần đầu 2–5 phút, mỗi module ~1–2 GB RAM.',
    items: list.map(p => ({ value: p, label: p })),
    note: n => n ? `Đã chọn ${n} module · cần khoảng ${n}–${n * 2} GB RAM` : 'Chưa chọn module nào',
  });
}

/**
 * Popup chọn nhiều mục. opts: { icon, title, text, ok, items: [{ value, label, sub?, checked? }], note: n => chuỗi }.
 * Trả mảng value đã chọn, hoặc null nếu bấm Huỷ.
 */
function pickList(opts) {
  return new Promise(resolve => {
    const boxes = opts.items.map(it => h('input', { type: 'checkbox', value: it.value, checked: !!it.checked }));
    const note = h('p', { class: 'ask-text pm-note' });
    const okBtn = h('button', { class: 'btn primary', value: 'ok', type: 'submit' }, opts.ok || 'OK');
    const update = () => {
      const n = boxes.filter(b => b.checked).length;
      note.textContent = opts.note ? opts.note(n) : n ? `Đã chọn ${n} mục` : 'Chưa chọn mục nào';
      okBtn.disabled = !n;
    };
    const setAll = v => { boxes.forEach(b => { b.checked = v; }); update(); };
    const dlg = h('dialog', { class: 'dlg ask-dlg' },
      h('form', { method: 'dialog', class: 'dlg-inner' },
        h('div', { class: 'ask-body' },
          h('div', { class: 'ask-ic' }, opts.icon || '☑'),
          h('div', { class: 'ask-main' },
            h('div', { class: 'ask-title' }, opts.title),
            opts.text ? h('p', { class: 'ask-text' }, opts.text) : null,
            h('div', { class: 'pm-tools' },
              h('button', { class: 'btn sm ghost', type: 'button', onclick: () => setAll(true) }, 'Chọn hết'),
              h('button', { class: 'btn sm ghost', type: 'button', onclick: () => setAll(false) }, 'Bỏ hết')),
            h('div', { class: 'pm-list' }, ...boxes.map((b, i) => h('label', { class: 'pm-item' }, b,
              h('span', {}, opts.items[i].label, opts.items[i].sub ? h('small', { class: 'muted' }, ' ', opts.items[i].sub) : null)))),
            note)),
        h('div', { class: 'dlg-foot' },
          h('button', { class: 'btn ghost', value: 'cancel', type: 'submit' }, 'Huỷ bỏ'),
          okBtn)));
    boxes.forEach(b => b.addEventListener('change', update));
    dlg.addEventListener('close', () => {
      const r = dlg.returnValue === 'ok' ? boxes.filter(b => b.checked).map(b => b.value) : null;
      dlg.remove();
      resolve(r);
    });
    document.body.append(dlg);
    update();
    dlg.showModal();
    boxes[0]?.focus();
  });
}
function envPanel(queued) {
  // Đếm task AMIS đến hạn hôm nay / quá hạn: nạp nền một lần nếu cột AMIS chưa tải tab đó
  if (!dueLoaded) { dueLoaded = true; ['TotalTaskDue', 'TotalTaskExpire'].forEach(t => { if (!S.tasks[t]) loadTasks(t); }); }
  const items = S.health?.items || [];
  const amis = { group: 'AMIS', name: 'AMIS', ok: S.me ? true : S.meError ? false : null, detail: S.me ? 'đã đăng nhập: ' + S.me.fullName : (S.meError || 'đang kiểm…'), fix: S.meError ? 'Cấu hình › AMIS (dán lại phiên đăng nhập)' : null };
  const all = [...items, amis];
  const bad = all.filter(x => x.ok === false).length;
  const dot = x => h('button', {
    type: 'button', class: 'env-dot ' + (x.ok === true ? 'ok' : x.ok === false ? 'bad' : 'off'),
    title: `${x.name}: ${x.detail}` + (x.fix ? `\n→ ${x.fix}` : ''),
    onclick: e => { e.stopPropagation(); if (x.action) { portalAction(x); return; } if (x.fix) { navigator.clipboard?.writeText(x.fix.replace(/^.*?(yarn .*)$/, '$1')).catch(() => {}); toast(`${x.name}: ${x.detail}\n→ ${x.fix}`, x.ok === false); } else toast(`${x.name}: ${x.detail}`); },
  }, h('i'), x.name);
  const st = S.autopilot, o = st?.options;
  const due = S.tasks.TotalTaskDue, exp = S.tasks.TotalTaskExpire;
  const goTab = tab => () => { S.tab = tab; localSet('tab', tab); if (!S.tasks[tab]) loadTasks(tab); render(); $('#board')?.firstElementChild?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }); };
  return h('div', { class: 'ov-env' + (bad ? ' has-bad' : '') },
    h('div', { class: 'env-row' },
      h('span', { class: 'env-h' }, 'Môi trường', bad ? h('b', { class: 'env-bad' }, ` · ${bad} lỗi`) : null),
      h('span', { class: 'env-dots' }, S.health ? all.map(dot) : h('span', { class: 'muted small' }, 'đang kiểm…')),
      h('button', { type: 'button', class: 'env-refresh', title: 'Kiểm lại ngay', onclick: e => { e.stopPropagation(); loadHealth(true); } }, healthLoading ? '…' : '↻')),
    h('div', { class: 'env-row env-sched' },
      h('span', { class: 'env-h' }, 'Lịch'),
      h('button', { type: 'button', class: 'env-item', title: 'Cấu hình chế độ tự động', onclick: () => openSettings?.('autoGroup') },
        '⏱ ', !o?.enabled ? 'Tự động: tắt' : st.running ? 'Tự động: đang chạy…' : 'Tự động lần tới ' + (st.nextRun ? timeText(st.nextRun) : '—')),
      h('span', { class: 'env-item' + (queued ? ' warn' : '') }, `⏳ ${queued} chờ lượt`),
      h('button', { type: 'button', class: 'env-item' + (due?.length ? ' warn' : ''), onclick: goTab('TotalTaskDue') }, `📅 ${due ? due.length : '…'} đến hạn hôm nay`),
      h('button', { type: 'button', class: 'env-item' + (exp?.length ? ' bad' : ''), onclick: goTab('TotalTaskExpire') }, `⚠ ${exp ? exp.length : '…'} quá hạn`)));
}

// ---------- Cập nhật live theo log (không vẽ lại cả trang) ----------
const LIVE_DIRTY = new Set();
let liveTimer = null;
function scheduleLive(id) {
  LIVE_DIRTY.add(id);
  if (liveTimer) return;
  liveTimer = setTimeout(() => {
    liveTimer = null;
    const ids = [...LIVE_DIRTY];
    LIVE_DIRTY.clear();
    requestAnimationFrame(() => ids.forEach(updateLive));
  }, 320);
}
function updateLive(id) {
  const j = S.jobs.get(id);
  if (!j) return;
  document.querySelectorAll(`.card.job[data-id="${id}"] .lastlog`).forEach(el => {
    if (el.textContent !== j.lastLog) { el.textContent = j.lastLog || ''; el.title = j.lastLog || ''; }
  });
  const old = document.querySelector(`#focus .stage[data-id="${id}"]`);
  if (old && RUNNING.has(j.status)) {
    // Chỉ thay phần thực sự đổi (log, số liệu, file, pipeline) — giữ khung .stage để animation viền/caret chạy liền mạch, đỡ vẽ lại cả khối
    const fresh = stagePanel(j);
    for (const sel of ['.term', '.stage-side', '.stage-stats', '.pipe']) {
      const a = old.querySelector(sel), b = fresh.querySelector(sel);
      if (!a || !b) continue;
      const changed = sel === '.pipe' ? a.innerHTML !== b.innerHTML : a.textContent !== b.textContent;
      if (changed) a.replaceWith(b);
    }
    animateCounts(old);
  }
}

// ---------- Sân khấu job đang chạy + khu "Cần bạn" ----------
const LIVE = new Map(); // jobId → { lines, shown, files, lastFile, loaded }
function liveOf(id) {
  let l = LIVE.get(id);
  if (!l) LIVE.set(id, l = { lines: [], shown: 0, files: new Set(), lastFile: null, loaded: false });
  return l;
}
function trackFile(l, e) {
  if (e.kind !== 'tool') return;
  const m = /^(Edit|Write|MultiEdit|NotebookEdit):\s*(.+)$/.exec(e.text || '');
  if (m) { const f = m[2].trim(); l.files.add(f); l.lastFile = f; }
}
function pushLive(id, e) {
  const l = liveOf(id);
  trackFile(l, e);
  l.lines.push(e);
  if (l.lines.length > 80) { const cut = l.lines.length - 80; l.lines.splice(0, cut); l.shown = Math.max(0, l.shown - cut); }
}
/** Lần đầu thấy job đang chạy: nạp nhật ký sẵn có để sân khấu có log + danh sách file ngay. */
async function preloadLive(j) {
  const l = liveOf(j.id);
  if (l.loaded) return;
  l.loaded = true;
  try {
    const logs = await api(`/jobs/${j.id}/logs`);
    const fresh = l.lines;
    l.lines = []; l.files = new Set(); l.lastFile = null;
    logs.forEach(e => trackFile(l, e));
    l.lines = logs.slice(-60).concat(fresh.filter(e => !logs.some(x => x.at === e.at && x.text === e.text)));
    fresh.forEach(e => trackFile(l, e));
    l.shown = l.lines.length;
    render();
  } catch { l.loaded = false; }
}

/** Mã ngắn của task: [ABCVN_432] / XYZ293 … — không có thì #id. */
function taskCode(j) {
  const m = /\b([A-Z]{2,}[A-Z0-9]*[_-]?\d{2,})\b/.exec(j.parentTaskName || j.taskName || '');
  return m ? m[1] : '#' + j.taskId;
}
function shortPath(p) {
  const parts = String(p || '').replace(/\\/g, '/').split('/');
  return parts.length > 2 ? '…/' + parts.slice(-2).join('/') : parts.join('/');
}
async function openJobTab(id, tab, doc) {
  await openDrawer(id);
  S.drawerTab = tab;
  if (doc) S.doc = doc;
  renderDrawer();
  await showDrawerTab();
}

// Thẻ "Cần bạn" người dùng bấm Bỏ qua (chỉ job đã duyệt-chưa-PR / lỗi) — nhớ trên trình duyệt này
function dismissed() { try { return new Set(JSON.parse(localGet('dismiss') || '[]')); } catch { return new Set(); } }
function dismissJob(id) { const s = dismissed(); s.add(id); localSet('dismiss', JSON.stringify([...s].slice(-200))); render(); }

/** Việc đang chờ bạn, xếp theo độ gấp. Job lỗi chỉ lấy bản mới nhất của task, trong 3 ngày. */
function needsYou() {
  const skip = dismissed();
  const jobs = [...S.jobs.values()];
  const latest = new Map();
  jobs.forEach(j => { const o = latest.get(j.taskId); if (!o || (j.createdAt || '') > (o.createdAt || '')) latest.set(j.taskId, j); });
  const recent = j => { const d = parseDate(j.finishedAt || j.createdAt); return d && Date.now() - d < 3 * 864e5; };
  const rank = { WaitingForUser: 0, NeedsInfo: 1, AwaitingApproval: 2, Clarified: 3, Approved: 4, Failed: 5 };
  return jobs.filter(j =>
      NEEDS_YOU.has(j.status) || j.status === 'AwaitingApproval'
      || (j.status === 'Approved' && j.branch && !Object.keys(j.prUrls || {}).length && recent(j) && !skip.has(j.id))
      || (j.status === 'Failed' && latest.get(j.taskId) === j && recent(j) && !skip.has(j.id)))
    .sort((a, b) => rank[a.status] - rank[b.status] || (b.createdAt || '').localeCompare(a.createdAt || ''));
}

function focusCard(j) {
  const btn = (label, fn, cls = '') => h('button', { class: 'btn sm ' + cls, type: 'button', onclick: e => { e.stopPropagation(); return fn(); } }, label);
  let tone = 'you', reason, acts = [];
  const files = j.diffList?.length ? `${j.diffList.length} file · +${j.diffAdded} −${j.diffDeleted}` : '';
  if (j.status === 'WaitingForUser') { reason = 'AI đang hỏi bạn giữa lúc code'; acts = [btn('Trả lời AI', () => openJobTab(j.id, 'chat'), 'primary')]; }
  else if (j.status === 'NeedsInfo') {
    reason = 'Yêu cầu còn thiếu — cần bạn/BA trả lời';
    acts = [btn('Trả lời', () => openJobTab(j.id, 'chat'), 'primary'),
      j.baMessage ? btn('📋 Tin gửi BA', () => navigator.clipboard.writeText(j.baMessage).then(() => toast('Đã copy tin nhắn gửi BA'))) : null];
  } else if (j.status === 'Clarified') {
    tone = 'go'; reason = 'Yêu cầu đã rõ — chờ bạn giao code';
    acts = [btn('Bắt đầu code', () => proceed(j.id, 'code'), 'primary'), btn('Xem phân tích', () => openJobTab(j.id, 'docs', 'analysis.md'))];
  } else if (j.status === 'AwaitingApproval') {
    const bad = j.testVerdict === 'FAIL' || j.reviewVerdict === 'FAIL';
    tone = bad ? 'warn' : 'ok';
    reason = bad ? `Xong nhưng ${j.reviewVerdict === 'FAIL' ? 'review' : 'test'} FAIL — xem trước khi duyệt` : 'AI làm xong — chờ bạn duyệt';
    acts = [btn('Xem diff', () => openJobTab(j.id, 'diff'), bad ? '' : 'primary'),
      bad ? btn(j.reviewVerdict === 'FAIL' ? 'Đọc review' : 'Đọc test', () => openJobTab(j.id, 'docs', j.reviewVerdict === 'FAIL' ? 'review.md' : 'test.md'), 'primary') : null,
      btn('Push + PR', () => j.held ? openCommit(j.id, 'approve') : decide(j.id, true, true), bad ? '' : 'ok'),
      btn('Test lại', () => proceed(j.id, 'review'))];
  } else if (j.status === 'Approved') {
    tone = 'ok'; reason = 'Đã duyệt nhưng tool chưa thấy pull request';
    acts = [btn('Tạo PR', () => createPr(j), 'primary'), btn('Mở job', () => openDrawer(j.id)), btn('Bỏ qua', () => dismissJob(j.id), 'ghost')];
  } else {
    tone = 'bad'; reason = j.error ? 'Lỗi: ' + j.error : 'Job lỗi';
    acts = [btn('Xem lỗi', () => openDrawer(j.id), 'primary'), btn('Bỏ qua', () => dismissJob(j.id), 'ghost')];
  }
  return h('article', { class: 'focus-card fc-' + tone, 'data-id': j.id, onclick: () => openDrawer(j.id), title: j.taskName },
    h('div', { class: 'fc-top' },
      h('span', { class: 'fc-code' }, taskCode(j)),
      h('span', { class: 'badge s-' + j.status }, STATUS_LABEL[j.status] || j.status)),
    h('div', { class: 'fc-name' }, j.parentTaskName || j.taskName),
    h('div', { class: 'fc-reason' }, reason),
    h('div', { class: 'fc-meta' },
      files ? h('span', { class: 'mono' }, files) : null,
      verdictChip('Review', j.reviewVerdict), verdictChip('Test', j.testVerdict),
      j.costUsd ? h('span', { class: 'mono muted' }, '$' + j.costUsd.toFixed(2)) : null),
    h('div', { class: 'fc-acts' }, acts));
}

function stagePanel(j) {
  const l = liveOf(j.id);
  if (!l.loaded) preloadLive(j);
  const rows = l.lines.filter(e => ['tool', 'ai', 'stage', 'info', 'error'].includes(e.kind) && !/connectors are disabled|^Phiên Claude/i.test(e.text || '')).slice(-7);
  const firstNew = Math.max(0, rows.length - Math.max(0, l.lines.length - l.shown));
  l.shown = l.lines.length;
  const icon = e => e.kind === 'stage' ? '▸' : e.kind === 'ai' ? '◆' : e.kind === 'error' ? '✕' : '›';
  return h('section', { class: 'stage', 'data-id': j.id, onclick: () => openDrawer(j.id) },
    h('div', { class: 'stage-head' },
      h('div', { class: 'stage-id' },
        h('span', { class: 'stage-live' }, h('span', { class: 'running-dot' }), 'ĐANG CHẠY'),
        h('span', { class: 'stage-code' }, taskCode(j)),
        h('span', { class: 'stage-name' }, j.parentTaskName ? `${j.taskName} · ${j.parentTaskName}` : j.taskName)),
      h('div', { class: 'stage-stats' },
        h('div', {}, h('span', { class: 'elapsed big', 'data-job': j.id }, elapsedText(j)), h('small', {}, 'thời gian')),
        h('div', {}, h('span', { class: 'big', 'data-ck': 'cost-' + j.id, 'data-val': j.costUsd || 0, 'data-fmt': 'usd' }, '$' + (j.costUsd || 0).toFixed(2)), h('small', {}, 'chi phí')),
        h('div', {}, h('span', { class: 'big', 'data-ck': 'files-' + j.id }, l.files.size), h('small', {}, 'file đã sửa')))),
    renderPipe(j, true),
    h('div', { class: 'stage-body' },
      h('div', { class: 'term', role: 'log', 'aria-live': 'polite' },
        h('div', { class: 'term-bar' }, h('i'), h('i'), h('i'), h('span', {}, STATUS_LABEL[j.status] || j.status)),
        rows.length ? rows.map((e, i) => h('div', { class: 'term-row k-' + e.kind + (i >= firstNew && motionOk() ? ' new' : ''), style: `--d:${(i - firstNew) * 60}ms` },
          h('span', { class: 'term-ic' }, icon(e)), h('span', { class: 'term-tx' }, (e.text || '').replace(/\s+/g, ' ').slice(0, 220))))
          : h('div', { class: 'term-row muted' }, 'Đang khởi động…'),
        h('div', { class: 'term-caret' })),
      h('div', { class: 'stage-side' },
        h('div', { class: 'side-h' }, 'Đang sửa'),
        l.lastFile ? h('div', { class: 'side-file now', title: l.lastFile }, shortPath(l.lastFile)) : h('div', { class: 'muted small' }, 'chưa sửa file nào'),
        l.files.size > 1 ? h('div', { class: 'side-h' }, `Đã đụng ${l.files.size} file`) : null,
        [...l.files].filter(f => f !== l.lastFile).slice(-5).reverse().map(f => h('div', { class: 'side-file', title: f }, shortPath(f))))));
}

// ---------- Drawer: timeline dọc các bước (mỗi bước mở ra xem log + kết quả) ----------
const TL = { id: null, logs: [], open: new Map(), timer: null };
const TL_KIND = [
  [/^(analyze|fetch)/, 'analyze', 'Phân tích yêu cầu', 'analysis.md'],
  [/^(revise)/, 'revise', 'Sửa thêm theo yêu cầu', 'code.result.md'],
  [/^(code)/, 'code', 'Code', 'code.result.md'],
  [/^(fix)/, 'fix', 'Sửa theo review', 'review.md'],
  [/^(review)/, 'review', 'Review độc lập', 'review.md'],
  [/^(build|sql|data|test)/, 'test', 'Build · dữ liệu · test', 'test.md'],
  [/^(done|decision)/, 'ship', 'Duyệt · commit · PR', 'pr.md'],
];

async function loadTimeline() {
  const id = S.openJob, j = S.jobs.get(id), el = $('#drawer .drawer-tl');
  if (!el) return;
  $('#drawer').classList.toggle('has-tl', !!j);
  if (!j) { el.replaceChildren(); return; }
  if (TL.id === id) { renderTimeline(); return; }
  TL.id = id; TL.logs = []; TL.open = new Map();
  el.replaceChildren(h('div', { class: 'loading' }, 'Đang tải các bước…'));
  try {
    const logs = await api(`/jobs/${id}/logs`);
    if (TL.id !== id) return;
    TL.logs = logs;
    renderTimeline();
  } catch (e) { el.replaceChildren(h('div', { class: 'errbox' }, e.message)); }
}
function scheduleTimeline() {
  if (TL.timer) return;
  TL.timer = setTimeout(() => { TL.timer = null; requestAnimationFrame(renderTimeline); }, 400);
}

/** Cắt nhật ký thành các chặng liên tiếp theo stage (Phân tích → Code → Review → Sửa → Review → Test → Duyệt…). */
function timelineSegments(logs) {
  const segs = [];
  let cur = null;
  for (const e of logs) {
    const st = String(e.stage || '').toLowerCase();
    const k = TL_KIND.find(x => x[0].test(st));
    if (k && (!cur || cur.key !== k[1])) {
      // "fetch" ở đầu bước code (đọc lại AMIS) không mở chặng phân tích mới
      if (!(k[1] === 'analyze' && st.startsWith('fetch') && cur && cur.key !== 'analyze' && segs.some(s => s.key === 'analyze'))) {
        cur = { key: k[1], label: k[2], doc: k[3], logs: [] };
        segs.push(cur);
      }
    }
    if (!cur) { cur = { key: 'prep', label: 'Chuẩn bị', doc: null, logs: [] }; segs.push(cur); }
    cur.logs.push(e);
  }
  const seen = {};
  segs.forEach(s => { seen[s.key] = (seen[s.key] || 0) + 1; if (seen[s.key] > 1) s.label += ` (lần ${seen[s.key]})`; });
  return segs;
}

function segResults(seg) {
  const out = [];
  for (const e of seg.logs) {
    const t = e.text || '';
    let m;
    if ((m = /Kết luận review:\s*(\w+)/.exec(t))) out.push(verdictChip('Review', m[1]));
    else if ((m = /Kết luận test:\s*(\w+)/.exec(t))) out.push(verdictChip('Test', m[1]));
    else if ((m = /Kết luận dữ liệu test:\s*(\w+)/.exec(t))) out.push(h('span', { class: 'chip' }, 'Dữ liệu: ' + m[1]));
    else if ((m = /đã tạo PR #(\d+)/.exec(t))) out.push(h('span', { class: 'chip pass' }, 'PR #' + m[1]));
    else if ((m = /đã commit (\d+) file/.exec(t))) out.push(h('span', { class: 'chip' }, m[1] + ' file commit'));
    else if ((m = /Đã deploy (\d+) store/.exec(t))) out.push(h('span', { class: 'chip' }, m[1] + ' store deploy'));
  }
  const errs = seg.logs.filter(e => e.kind === 'error').length;
  if (errs) out.push(h('span', { class: 'chip fail' }, errs + ' lỗi'));
  return out.filter(Boolean).slice(-4);
}

function fmtSpan(a, b) {
  const s = parseDate(a), e = parseDate(b);
  if (!s) return '';
  const t = s.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' });
  return e && e - s > 30000 ? `${t} · ${fmtDuration(e - s)}` : t;
}

function renderTimeline() {
  const el = $('#drawer .drawer-tl');
  const j = S.jobs.get(TL.id);
  if (!el || !j || TL.id !== S.openJob) return;
  const segs = timelineSegments(TL.logs);
  const running = RUNNING.has(j.status) || j.status === 'Queued';
  const scroll = el.scrollTop, atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
  const items = segs.map((seg, i) => {
    const last = i === segs.length - 1;
    const hasErr = seg.logs.some(e => e.kind === 'error');
    const state = last && running ? 'active' : last && j.status === 'Failed' ? 'fail' : hasErr && last ? 'fail' : 'done';
    const key = i + ':' + seg.key;
    const open = TL.open.has(key) ? TL.open.get(key) : (last && (running || j.status === 'Failed'));
    const ai = [...seg.logs].reverse().find(e => e.kind === 'ai');
    const next = segs[i + 1];
    const endAt = next ? next.logs[0]?.at : (running ? new Date().toISOString() : seg.logs[seg.logs.length - 1]?.at);
    const toggle = () => { TL.open.set(key, !open); renderTimeline(); };
    const rows = open ? seg.logs.slice(-250).map(renderLogRow) : null;
    return h('li', { class: `tl-item tl-${state}` + (open ? ' open' : '') },
      h('span', { class: 'tl-dot' }, state === 'done' ? '✓' : state === 'fail' ? '!' : ''),
      h('div', { class: 'tl-card' },
        h('button', { type: 'button', class: 'tl-head', onclick: toggle, 'aria-expanded': open ? 'true' : 'false' },
          h('span', { class: 'tl-title' }, seg.label),
          h('span', { class: 'tl-time' }, fmtSpan(seg.logs[0]?.at, endAt)),
          h('span', { class: 'tl-caret' }, open ? '▾' : '▸')),
        h('div', { class: 'tl-res' }, segResults(seg),
          seg.doc ? h('button', { type: 'button', class: 'linkish', onclick: () => { S.drawerTab = seg.doc === 'test.md' ? 'test' : 'docs'; S.doc = seg.doc; showDrawerTab(); } }, 'mở ' + seg.doc) : null),
        ai && !open ? h('div', { class: 'tl-ai', title: ai.text }, ai.text) : null,
        open ? h('div', { class: 'log tl-log' }, seg.logs.length > 250 ? h('div', { class: 'muted small' }, `… ${seg.logs.length - 250} dòng trước`) : null, rows) : null));
  });
  el.replaceChildren(
    h('div', { class: 'tl-h' }, 'Các bước', h('small', {}, `${segs.length} chặng · ${TL.logs.length} dòng nhật ký`)),
    segs.length ? h('ol', { class: 'tl' }, items) : h('div', { class: 'empty' }, 'Chưa có nhật ký.'));
  el.scrollTop = atBottom && running ? el.scrollHeight : scroll;
}

function renderFocus() {
  const el = $('#focus');
  if (!el) return;
  const running = [...S.jobs.values()].filter(j => RUNNING.has(j.status))
    .sort((a, b) => (b.codeApproved ? 1 : 0) - (a.codeApproved ? 1 : 0));
  const you = needsYou();
  const prevIds = new Set([...el.querySelectorAll('.focus-card[data-id]')].map(c => c.dataset.id));
  const parts = [];
  if (running.length) parts.push(...running.slice(0, 2).map(stagePanel));
  if (you.length) parts.push(h('div', { class: 'focus-you' },
    h('div', { class: 'focus-h' }, h('span', { class: 'fh-dot' }), `Cần bạn (${you.length})`, h('small', {}, ' — việc AI đang chờ, bấm nút để xử lý luôn')),
    h('div', { class: 'focus-row' }, you.slice(0, 8).map(focusCard))));
  if (!running.length && !you.length) parts.push(h('div', { class: 'focus-empty' }, h('span', { class: 'fe-ic' }, '✓'), 'Không có gì chờ bạn — kéo task AMIS vào cột Phân tích, hoặc giao qua nhóm chat.'));
  el.replaceChildren(...parts);
  el.querySelectorAll('.focus-card[data-id]').forEach((c, i) => {
    if (!prevIds.has(c.dataset.id) && motionOk() && boardShown) c.animate([{ opacity: 0, transform: 'translateY(14px) scale(.96)' }, { opacity: 1, transform: 'none' }], { duration: 560, delay: i * 50, easing: SPRING, fill: 'backwards' });
  });
  animateCounts(el);
}

function renderAmisColumn() {
  const list = S.tasks[S.tab];
  const err = S.tasksError[S.tab];
  const loading = S.tasksLoading[S.tab];
  let body;
  if (err) {
    body = h('div', { class: 'errbox' }, err,
      h('div', {}, h('button', { class: 'btn sm', type: 'button', onclick: openSessionDialog }, 'Cập nhật session')));
  } else if (!list) {
    body = h('div', { class: 'empty' }, 'Đang tải…');
  } else if (list.length === 0) {
    body = h('div', { class: 'empty' }, 'Không có công việc nào.');
  } else {
    const shown = list.filter(t => matchProjectFilter(t) && matchQuery(t));
    const q = S.taskQuery.trim().replace(/^#/, '');
    body = shown.length ? shown.map(renderTaskCard)
      : h('div', { class: 'empty' }, S.taskQuery ? 'Không tìm thấy task phù hợp trong danh sách này. ' : 'Không có task nào thuộc bộ lọc dự án này. ',
        S.projectFilter ? h('button', { class: 'link-btn', type: 'button', onclick: () => setProjectFilter('') }, 'Bỏ lọc dự án') : null);
    // ID số không có trong danh sách hạn: vẫn mở được task bất kỳ trên AMIS
    if (/^\d{4,}$/.test(q) && !list.some(t => String(t.taskId) === q))
      body = [h('button', { class: 'btn sm open-id', type: 'button', onclick: () => openStartDialog({ taskId: +q, taskName: '', projectName: '' }) }, `Mở task #${q} từ AMIS`), ...(shown.length ? body : [])];
  }
  return h('section', { class: 'col amis' },
    h('div', { class: 'col-head' },
      h('div', { class: 'col-title' }, 'Công việc AMIS',
        h('span', { class: 'count' }, list ? (S.projectFilter ? `${list.filter(matchProjectFilter).length}/${list.length}` : list.length) : '…'),
        h('span', { class: 'spacer' }),
        h('button', { class: 'btn ghost icon', type: 'button', title: 'Tải lại', onclick: () => loadTasks() }, loading ? '…' : '↻')),
      h('div', { class: 'seg', role: 'tablist' }, TABS.map(([id, label]) =>
        h('button', {
          type: 'button', class: id === S.tab ? 'active' : '', role: 'tab',
          onclick: () => { S.tab = id; localSet('tab', id); if (!S.tasks[id]) loadTasks(id); render(); },
        }, label))),
      h('div', { class: 'task-search' },
        h('input', {
          id: 'taskSearch', class: 'input sm', type: 'search', value: S.taskQuery, autocomplete: 'off',
          placeholder: 'Tìm theo tên, ID task, dự án…',
          oninput: e => { S.taskQuery = e.target.value; render(); },
          onkeydown: e => { if (e.key === 'Escape') { S.taskQuery = ''; render(); } },
        })),
      list && list.length ? renderProjectFilter(list) : null,
      list && list.length ? renderBatchBar(list) : null),
    h('div', { class: 'col-body' }, body));
}

// ---------- Chạy hàng loạt ----------
function renderBatchBar(list) {
  const n = S.batchSel.size;
  const free = list.filter(t => matchProjectFilter(t) && matchQuery(t) && !activeJobForTask(t.taskId));
  if (!n) return free.length ? h('div', { class: 'batch-bar idle' },
    h('button', { class: 'link-btn', type: 'button', title: 'Hoặc tick từng task trong danh sách',
      onclick: () => { for (const t of free.slice(0, 30)) S.batchSel.set(t.taskId, t); render(); } }, `☐ Chọn ${Math.min(free.length, 30)} task đang hiển thị để chạy hàng loạt`)) : null;
  return h('div', { class: 'batch-bar' },
    h('b', {}, `Đã chọn ${n} task`),
    h('span', { class: 'spacer' }),
    h('button', { class: 'link-btn', type: 'button', onclick: () => { S.batchSel.clear(); render(); } }, 'Bỏ chọn'),
    h('button', { class: 'btn sm primary', type: 'button', onclick: openBatch }, 'Chạy hàng loạt…'));
}

function toggleBatch(t, on) {
  if (on) {
    if (S.batchSel.size >= 30) { toast('Tối đa 30 task mỗi lô.', true); render(); return; }
    S.batchSel.set(t.taskId, t);
  } else S.batchSel.delete(t.taskId);
  render();
}

function openBatch() {
  let dlg = $('#dlgBatch');
  if (!dlg) { dlg = h('dialog', { id: 'dlgBatch', class: 'dlg wide' }); document.body.append(dlg); }
  const tasks = [...S.batchSel.values()];
  const chain = h('input', { type: 'radio', name: 'batchMode', value: 'chain', checked: true });
  const warn = h('input', { type: 'radio', name: 'batchMode', value: 'warn' });
  const night = h('input', { type: 'radio', name: 'batchAsk', value: 'night', checked: true });
  const ask = h('input', { type: 'radio', name: 'batchAsk', value: 'ask' });
  const note = h('textarea', { class: 'input', rows: '2', style: 'width:100%', placeholder: 'Ghi chú chung cho AI (tuỳ chọn) — áp cho mọi task trong lô' });
  const repoInfo = S.repo && S.repo.info;
  const mismatch = tasks.filter(t => branchCheck(t.projectName).ok === false);
  const ok = h('button', { class: 'btn primary', type: 'button' }, `Giao ${tasks.length} task cho AI`);
  ok.addEventListener('click', async () => {
    ok.disabled = true;
    try {
      const r = await api('/jobs/batch', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tasks: tasks.map(t => ({ taskId: t.taskId, taskName: t.taskName, projectName: t.projectName, endDate: t.endDate })), chain: chain.checked, note: note.value, unattended: night.checked }),
      });
      for (const j of r.created) S.jobs.set(j.id, j);
      S.batchSel.clear();
      dlg.close();
      toast(`Đã giao ${r.created.length} task` + (r.skipped.length ? `, bỏ qua ${r.skipped.length} task đang có job` : ''));
      if (r.warn) toast(r.warn, true);
      render();
    } catch (e) { toast(e.message, true); ok.disabled = false; }
  });
  dlg.replaceChildren(h('div', { class: 'dlg-inner' },
    h('div', { class: 'dlg-head' }, h('b', {}, `Chạy hàng loạt ${tasks.length} task`), h('button', { class: 'btn ghost icon', type: 'button', onclick: () => dlg.close() }, '✕')),
    h('div', { class: 'dlg-body' },
      h('ul', { class: 'batch-list' }, tasks.map(t => h('li', {},
        h('b', {}, '#' + t.taskId), ' ', t.parentName || t.taskName, ' ', h('span', { class: 'muted small' }, t.projectName || ''),
        branchCheck(t.projectName).ok === false ? h('span', { class: 'chip warn' }, '⚠ Khác nhánh') : null))),
      mismatch.length ? h('div', { class: 'errbox' }, `${mismatch.length} task có vẻ không thuộc nhánh ${repoInfo ? repoInfo.branch : 'hiện tại'} — AI sẽ code trên nhánh này. Bỏ tick các task đó nếu không đúng.`) : null,
      h('div', { class: 'notice' },
        h('div', {}, '1. AI phân tích yêu cầu (2 task song song, chỉ đọc), rõ thì tự sang code.'),
        h('div', {}, `2. Code → review → test lần lượt TỪNG task. Mỗi task một nhánh AI riêng tạo từ ${repoInfo ? `"${repoInfo.branch}"` : 'nhánh hiện tại'}; xong tự commit vào nhánh đó (không push) rồi trả repo cho task sau.`),
        h('div', {}, '3. Lỗi kỹ thuật (timeout, mạng, key hết hạn mức) thì tự thử lại 1 lần. Máy được giữ không tự ngủ tới khi hết job — đừng gập máy / bấm Sleep.'),
        h('div', {}, '4. Sáng ra mở ☀ Bản tin: mỗi task một dòng, giả định AI tự chọn, Kiểm trên web, Duyệt & push.')),
      h('div', { class: 'field' }, h('span', {}, 'Khi yêu cầu chưa rõ / AI cần hỏi'),
        h('label', { class: 'check' }, night, h('span', {}, h('b', {}, 'Vẫn làm theo phương án đề xuất (chạy qua đêm)'), ' — AI chọn phương án nó đề xuất, ghi vào tab Giả định để bạn xác nhận khi review. Có thể phải sửa lại nếu đoán sai.')),
        h('label', { class: 'check' }, ask, h('span', {}, h('b', {}, 'Dừng chờ tôi trả lời'), ' — task đó đứng ở "Cần bổ sung" / "Cần bạn trả lời" tới khi bạn trả lời.'))),
      h('div', { class: 'field' }, h('span', {}, 'Khi hai task sửa cùng file'),
        h('label', { class: 'check' }, chain, h('span', {}, h('b', {}, 'Nối nhánh (khuyên dùng)'), ' — task sau tạo nhánh từ nhánh của task trước đã sửa file đó, nên không conflict. Khi merge: PR task trước vào trước.')),
        h('label', { class: 'check' }, warn, h('span', {}, h('b', {}, 'Tách nhánh, chỉ cảnh báo'), ' — mỗi task độc lập từ nhánh gốc; tool đánh dấu ⚠ các task trùng file để bạn xử lý conflict khi merge.'))),
      note,
      h('div', { class: 'small muted' }, 'Chạy lâu và tốn phí theo số task — đặt giới hạn chi phí mỗi job ở ⚙ Cấu hình › Claude nếu cần.')),
    h('div', { class: 'dlg-foot' }, h('span', { class: 'spacer' }),
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Huỷ'), ok)));
  dlg.showModal();
}

function renderProjectFilter(list) {
  const counts = new Map();
  for (const t of list) {
    const k = projectKey(t.projectName);
    const e = counts.get(k) || { key: k, name: t.projectName || '(không có dự án)', n: 0 };
    e.n++;
    counts.set(k, e);
  }
  const branch = S.repo && S.repo.info ? S.repo.info.branch : null;
  const sel = h('select', { class: 'input sm project-filter', title: 'Lọc task theo dự án', onchange: e => setProjectFilter(e.target.value) },
    h('option', { value: '' }, `Tất cả dự án (${list.length})`),
    branch ? h('option', { value: '@branch' }, `Khớp nhánh hiện tại — ${branch} (${list.filter(t => branchCheck(t.projectName).ok !== false).length})`) : null,
    [...counts.values()].sort((a, b) => a.name.localeCompare(b.name, 'vi')).map(e => h('option', { value: e.key }, `${e.name} (${e.n})`)));
  sel.value = S.projectFilter;
  if (sel.value !== S.projectFilter) sel.value = ''; // dự án đã lọc không còn trong danh sách
  return sel;
}

function matchQuery(t) {
  const q = S.taskQuery.trim().replace(/^#/, '');
  if (!q) return true;
  if (/^\d+$/.test(q)) return String(t.taskId).includes(q);
  const nq = normKey(q);
  // So từng từ để "def bao mat" khớp "Tính năng bảo mật … DEF"
  const hay = normKey(`${t.taskName} ${t.parentName || ''} ${t.projectName} ${t.taskId} ${t.parentId || ''}`);
  return q.split(/\s+/).map(normKey).filter(Boolean).every(w => hay.includes(w)) || hay.includes(nq);
}

function setProjectFilter(v) {
  S.projectFilter = v;
  localSet('project', v);
  render();
}

function matchProjectFilter(t) {
  const f = S.projectFilter;
  if (!f) return true;
  if (f === '@branch') return branchCheck(t.projectName).ok !== false;
  return projectKey(t.projectName) === f;
}

// ----- Dự án ↔ nhánh -----
/** Chuẩn hoá để so khớp: bỏ dấu, chữ hoa, chỉ giữ chữ và số. */
function normKey(s) {
  return String(s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/gi, 'D').toUpperCase().replace(/[^A-Z0-9]/g, '');
}

/** Mã dự án từ tên AMIS: phần trước " - ", bỏ ngoặc. "DEF - HĐ-2026/11" → DEF, "GHI (POC) - …" → GHI. */
function projectKey(name) {
  if (!name) return '';
  return normKey(String(name).split(' - ')[0].replace(/\(.*?\)/g, ''));
}

/** Từ khoá nhánh đã ghi nhớ cho dự án, null nếu chưa có. "*" = nhánh nào cũng được. */
function branchKeywords(key) {
  const map = (S.config && S.config.projectBranches) || {};
  const hit = Object.entries(map).find(([k]) => normKey(k) === key);
  return hit ? hit[1].split(',').map(s => s.trim()).filter(Boolean) : null;
}

/** Các từ trong mã dự án: "XYZ Power - …" → [XYZ, POWER], "Miền Nam.GĐ2 - …" → [MIEN, NAM, GD2]. */
function projectWords(name) {
  if (!name) return [];
  return String(name).split(' - ')[0].replace(/\(.*?\)/g, '').split(/[\s._\-\/]+/).map(normKey).filter(w => w.length >= 2);
}

/** Các đoạn của tên nhánh: "dev/XYZ_v8.12.46" → [DEV, XYZ, V8, 12, 46]. */
function branchTokens(branch) {
  return String(branch).normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/gi, 'D').toUpperCase().split(/[^A-Z0-9]+/).filter(Boolean);
}

/**
 * ok: true khớp, false lệch nhánh, null chưa xác định (chưa có repo/dự án).
 * Mặc định: từ đầu của mã dự án là đầu một đoạn nhánh (XYZ ↔ XYZ_v8…, ABC ↔ ABCVN_…), hoặc từ khác trùng hẳn một đoạn,
 * hoặc cả mã liền (ACMEAGRI) nằm trong tên nhánh. Đã ghi nhớ từ khoá thì chỉ theo từ khoá.
 */
function branchCheck(projectName, branch = S.repo && S.repo.info ? S.repo.info.branch : null) {
  const key = projectKey(projectName);
  const saved = key ? branchKeywords(key) : null;
  const words = projectWords(projectName);
  const keywords = saved || (words.length ? words : (key ? [key] : []));
  if (!key || !branch) return { key, keywords, branch, ok: null };
  const b = normKey(branch);
  const ok = saved
    ? saved.some(k => k === '*' || (normKey(k).length > 0 && b.includes(normKey(k))))
    : b.includes(key) || words.some((w, i) => branchTokens(branch).some(t => t === w || (i === 0 && t.startsWith(w))));
  return { key, keywords, branch, ok };
}

/** Khung cảnh báo lệch nhánh: đổi nhánh, hoặc ghi nhớ từ khoá nhánh của dự án. */
function renderBranchWarning(projectName, onChange) {
  const c = branchCheck(projectName);
  if (c.ok !== false) return null;
  const inp = h('input', { class: 'input sm mono', value: c.keywords.join(', '), placeholder: c.key, spellcheck: 'false' });
  const save = async () => {
    try {
      S.config = await api('/settings/project-branch', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: c.key, keywords: inp.value }) });
      toast(`Đã lưu: dự án ${c.key} dùng nhánh chứa "${inp.value || c.key}"`);
      render();
      onChange && onChange();
    } catch (e) { toast(e.message, true); }
  };
  return h('div', { class: 'branch-warn' },
    h('div', { class: 'bw-title' }, '⚠ Có thể sai nhánh'),
    h('div', {}, 'Task thuộc dự án ', h('b', {}, c.key), ' nhưng ', h('b', {}, S.repo.info.name), ' đang ở nhánh ',
      h('code', {}, c.branch), ' — tên nhánh không chứa ', c.keywords.map((k, i) => [i ? ' / ' : '', h('code', {}, k)]), '. ',
      'AI sẽ đọc và sửa code trên nhánh này.'),
    h('div', { class: 'bw-actions' },
      h('button', { class: 'btn sm', type: 'button', onclick: () => openBranchDialog(c.key) }, 'Đổi nhánh…'),
      h('span', { class: 'muted' }, 'hoặc ghi nhớ: dự án ', h('b', {}, c.key), ' dùng nhánh có chứa'),
      inp,
      h('button', { class: 'btn sm', type: 'button', title: 'Nhiều từ khoá cách nhau bằng dấu phẩy; "*" = nhánh nào cũng được', onclick: save }, 'Lưu')));
}

// ---------- Ảnh kéo tự vẽ: ảnh kéo mặc định của trình duyệt bị mờ, nên ẩn nó và cho một bản sao rõ nét bám theo chuột ----------
const BLANK_DRAG = new Image();
BLANK_DRAG.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
let dragGhost = null, dragSrc = null, dragOff = { x: 0, y: 0 };
function moveDragGhost(e) {
  if (!dragGhost || (!e.clientX && !e.clientY)) return; // sự kiện cuối có toạ độ 0,0
  dragGhost.style.transform = `translate3d(${e.clientX - dragOff.x}px, ${e.clientY - dragOff.y}px, 0) rotate(-2.5deg) scale(1.03)`;
}
function startDragPreview(e) {
  const card = e.currentTarget;
  const r = card.getBoundingClientRect();
  dragOff = { x: e.clientX - r.left, y: e.clientY - r.top };
  try { e.dataTransfer.setDragImage(BLANK_DRAG, 0, 0); } catch { /* trình duyệt không hỗ trợ: giữ ảnh mặc định */ return; }
  dragGhost = card.cloneNode(true);
  dragGhost.classList.add('drag-ghost');
  dragGhost.removeAttribute('draggable');
  dragGhost.style.width = r.width + 'px';
  document.body.append(dragGhost);
  moveDragGhost(e);
  dragSrc = card;
  requestAnimationFrame(() => dragSrc && dragSrc.classList.add('drag-src'));
  document.addEventListener('dragover', moveDragGhost);
}
function stopDragPreview() {
  document.removeEventListener('dragover', moveDragGhost);
  if (dragGhost) dragGhost.remove();
  if (dragSrc) dragSrc.classList.remove('drag-src');
  dragGhost = dragSrc = null;
}

function renderTaskCard(t) {
  const job = activeJobForTask(t.taskId);
  return h('article', {
    class: 'card task' + (job ? ' has-job' : ''),
    draggable: job ? null : 'true',
    title: job ? 'Task này đang được AI xử lý' : 'Kéo sang "Hàng đợi AI" hoặc bấm để giao cho AI',
    ondragstart: e => {
      e.dataTransfer.setData('application/x-amis-task', JSON.stringify(t));
      e.dataTransfer.effectAllowed = 'copy';
      startDragPreview(e);
      S.dragging = true;
      document.body.classList.add('dragging');
    },
    ondragend: () => { stopDragPreview(); S.dragging = false; document.body.classList.remove('dragging'); render(); },
    onclick: () => job ? openDrawer(job.id) : openStartDialog(t),
  },
    h('div', { class: 'card-title' },
      job ? null : h('input', { type: 'checkbox', class: 'batch-cb', title: 'Chọn để chạy hàng loạt', checked: S.batchSel.has(t.taskId),
        onclick: e => e.stopPropagation(), onchange: e => toggleBatch(t, e.target.checked) }),
      t.parentName || t.taskName),
    t.parentName ? h('div', { class: 'card-sub', title: 'Task con' }, '↳ ' + t.taskName) : null,
    h('div', { class: 'card-meta' },
      h('span', {
        class: 'task-id', title: 'ID task AMIS — bấm để copy',
        onclick: e => { e.stopPropagation(); navigator.clipboard.writeText(String(t.taskId)).then(() => toast('Đã copy ID ' + t.taskId)); },
      }, '#' + t.taskId),
      h('span', { class: 'proj', title: t.projectName || '' }, t.projectName || ''),
      h('span', { class: 'due ' + dueClass(t.endDate) }, fmtDate(t.endDate))),
    job || isQcFix(t.taskName) || branchCheck(t.projectName).ok === false
      ? h('div', { class: 'chips' },
        isQcFix(t.taskName) ? h('span', { class: 'chip qc-chip', title: 'QC trả về — AI đọc lỗi từ bình luận và file test case của task cha, chỉ sửa lỗi (không làm lại tính năng)' }, '🐞 Lỗi QC') : null,
        job ? h('span', { class: 'badge s-' + job.status }, 'AI: ' + STATUS_LABEL[job.status]) : null,
        branchCheck(t.projectName).ok === false
          ? h('span', { class: 'chip warn', title: `Repo đang ở nhánh ${S.repo.info.branch}, không khớp dự án ${projectKey(t.projectName)}` }, '⚠ Khác nhánh')
          : null)
      : null);
}

/** Thứ tự trong cột: việc cần bạn trước, rồi việc đang chạy, rồi đang chờ; cùng nhóm thì mới nhất lên trên. */
const jobRank = j => NEEDS_YOU.has(j.status) ? 0 : RUNNING.has(j.status) ? 1 : j.status === 'Queued' ? 2 : 3;

function doneTab(col, all) {
  let t = S.doneTab;
  if (!t) { try { t = localStorage.getItem('doneTab'); } catch { } }
  if (!col.tabs.some(x => x.id === t)) t = all.some(j => j.status === 'Failed') ? 'stop' : 'ok';
  return t;
}

function renderJobColumn(col) {
  const all = [...S.jobs.values()].filter(j => col.statuses.includes(j.status) && (!col.match || col.match(j)));
  const tab = col.tabs ? doneTab(col, all) : null;
  const curTab = col.tabs ? col.tabs.find(x => x.id === tab) : null;
  const jobs = all
    .filter(j => !curTab || curTab.statuses.includes(j.status))
    .sort((a, b) => jobRank(a) - jobRank(b) || (b.createdAt || '').localeCompare(a.createdAt || ''));
  const attrs = { class: 'col' + (col.drop ? ' drop' : '') };
  if (col.drop) {
    attrs.ondragover = e => {
      if (!e.dataTransfer.types.includes('application/x-amis-task')) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      e.currentTarget.classList.add('over');
    };
    attrs.ondragleave = e => { if (!e.currentTarget.contains(e.relatedTarget)) e.currentTarget.classList.remove('over'); };
    attrs.ondrop = e => {
      e.preventDefault();
      e.currentTarget.classList.remove('over');
      const raw = e.dataTransfer.getData('application/x-amis-task');
      if (raw) openStartDialog(JSON.parse(raw));
    };
  }
  const cards = jobs.map(renderJobCard);
  // Tab Dừng/Huỷ: nút dọn các job cũ trùng task (task đã có job mới hơn)
  if (curTab && curTab.id === 'stop') {
    const dup = dupJobs();
    cards.unshift(h('div', { class: 'dup-bar' },
      h('span', {}, dup.length ? `${dup.length} job cũ trùng task (đã có job mới hơn)` : 'Job đã xoá nằm trong thùng rác'),
      h('span', { class: 'dup-acts' },
        dup.length ? h('button', { type: 'button', class: 'btn sm', onclick: cleanupDuplicates }, '🧹 Dọn job trùng') : null,
        h('button', { type: 'button', class: 'btn sm ghost', onclick: openTrash }, '🗑 Thùng rác'))));
  }
  if (col.drop && cards.length === 0) cards.push(h('div', { class: 'dropzone-hint' }, 'Thả task AMIS vào đây để AI đọc và đánh giá yêu cầu trước khi code'));
  else if (cards.length === 0) cards.push(h('div', { class: 'empty' }, '—'));
  const tabs = col.tabs ? h('div', { class: 'col-tabs', role: 'tablist' }, col.tabs.map(t => h('button', {
    type: 'button', role: 'tab', class: t.id === tab ? 'active' : '', title: t.hint,
    onclick: () => { S.doneTab = t.id; try { localStorage.setItem('doneTab', t.id); } catch { } render(); },
  }, t.label, h('span', { class: 'count' }, all.filter(j => t.statuses.includes(j.status)).length)))) : null;
  return h('section', attrs,
    h('div', { class: 'col-head' },
      h('div', { class: 'col-title' }, col.title, h('span', { class: 'count', 'data-ck': 'col-' + col.id }, all.length)),
      tabs || h('div', { class: 'col-hint' }, col.hint)),
    h('div', { class: 'col-body' }, cards));
}

// ---------- Xoá job đã dừng/huỷ ----------
const DELETABLE = new Set(['Cancelled', 'Failed', 'Rejected']);
function canDelete(j) { return DELETABLE.has(j.status) && !j.held; }
/** Job dừng/huỷ/lỗi mà task đó còn job khác mới hơn. */
function dupJobs() {
  const newest = new Map();
  for (const j of S.jobs.values()) { const n = newest.get(j.taskId); if (!n || (j.createdAt || '') > (n.createdAt || '')) newest.set(j.taskId, j); }
  return [...S.jobs.values()].filter(j => canDelete(j) && newest.get(j.taskId) !== j);
}
async function deleteJob(j, ev) {
  ev?.stopPropagation();
  if (!confirm(`Xoá job ${taskCode(j)} (${STATUS_LABEL[j.status] || j.status}, ${j.id})?\nThư mục job chuyển vào data\\jobs-trash (khôi phục được). Nhánh git không bị đụng.`)) return;
  try { await api('/jobs/' + j.id, { method: 'DELETE' }); toast('Đã xoá job ' + taskCode(j)); }
  catch (e) { toast(e.message, true); }
}
/** Thùng rác: danh sách job đã xoá + nút khôi phục. */
async function openTrash() {
  let dlg = $('#dlgTrash');
  if (!dlg) {
    dlg = h('dialog', { id: 'dlgTrash', class: 'dlg trash-dlg' },
      h('div', { class: 'dlg-inner' },
        h('div', { class: 'dlg-head' }, h('b', {}, '🗑 Thùng rác job'),
          h('button', { type: 'button', class: 'btn sm ghost', onclick: () => dlg.close() }, '✕')),
        h('div', { class: 'dlg-body trash-body' })));
    document.body.append(dlg);
  }
  const body = $('.trash-body', dlg);
  body.replaceChildren(h('div', { class: 'loading' }, 'Đang tải…'));
  dlg.showModal();
  const load = async () => {
    try {
      const list = await api('/jobs-trash');
      if (!list.length) { body.replaceChildren(h('div', { class: 'empty' }, 'Thùng rác trống.')); return; }
      body.replaceChildren(
        h('div', { class: 'muted small' }, `${list.length} job — khôi phục thì job quay lại cột cũ (thư mục trong data\\jobs-trash chuyển về data\\jobs).`),
        ...list.map(j => h('div', { class: 'trash-row' },
          h('div', { class: 'trash-info' },
            h('div', { class: 'trash-title' }, h('b', {}, taskCode(j)), h('span', { class: 'badge s-' + j.status }, STATUS_LABEL[j.status] || j.status)),
            h('div', { class: 'muted small' }, (j.parentTaskName || j.taskName || '').slice(0, 90)),
            h('div', { class: 'muted small mono' }, `#${j.taskId} · ${j.id} · xoá lúc ${new Date(j.deletedAt).toLocaleString('vi-VN')}` + (j.costUsd ? ` · $${j.costUsd.toFixed(2)}` : ''))),
          h('button', { type: 'button', class: 'btn sm primary', onclick: async e => {
            e.target.disabled = true;
            try { await api(`/jobs-trash/${encodeURIComponent(j.id)}/restore`, { method: 'POST' }); toast('Đã khôi phục ' + taskCode(j)); load(); }
            catch (err) { toast(err.message, true); e.target.disabled = false; }
          } }, '↩ Khôi phục'))));
    } catch (e) { body.replaceChildren(h('div', { class: 'errbox' }, e.message)); }
  };
  load();
}

/** Hộp gửi tài liệu lên task cha AMIS: tick file → xác nhận → tool tải lên, nén zip file AMIS không nhận, nối vào danh sách đính kèm. */
async function openAmisDocs(j) {
  let dlg = $('#dlgAmisDocs');
  if (!dlg) {
    dlg = h('dialog', { id: 'dlgAmisDocs', class: 'dlg wide' },
      h('div', { class: 'dlg-inner' },
        h('div', { class: 'dlg-head docs-head' }, h('b', {}, '📎 Gửi tài liệu lên AMIS'), h('button', { type: 'button', class: 'btn sm ghost', onclick: () => dlg.close() }, '✕')),
        h('div', { class: 'dlg-body docs-body' }),
        h('div', { class: 'dlg-foot docs-foot' })));
    document.body.append(dlg);
  }
  const body = $('.docs-body', dlg), foot = $('.docs-foot', dlg);
  body.replaceChildren(h('div', { class: 'loading' }, 'Đang đọc file và danh sách đính kèm trên AMIS…'));
  foot.replaceChildren();
  dlg.showModal();
  let d;
  try { d = await api(`/jobs/${j.id}/amis-docs`); }
  catch (e) { body.replaceChildren(h('div', { class: 'errbox' }, e.message)); return; }
  if (!d.files.length) { body.replaceChildren(h('div', { class: 'empty' }, 'Không thấy tài liệu nào của job (thư mục specs/* sửa trong lúc job chạy, hoặc file Word test case).')); return; }
  const groups = new Map();
  for (const f of d.files) { if (!groups.has(f.group)) groups.set(f.group, []); groups.get(f.group).push(f); }
  const boxes = [];
  body.replaceChildren(
    h('div', { class: 'docs-target' }, 'Gửi vào ', h('b', {}, `${d.target.isParent ? 'task cha' : 'task'} #${d.target.id}`), ' — ', d.target.name),
    h('div', { class: 'muted small' }, 'File AMIS không nhận (.md, .sql…) sẽ được nén .zip. File đã có trên task được bỏ qua. Đính kèm sẵn có của người khác giữ nguyên.'),
    ...[...groups].map(([g, files]) => h('div', { class: 'docs-group' },
      h('div', { class: 'docs-g' }, g),
      ...files.map(f => {
        const cb = h('input', { type: 'checkbox', checked: !f.onAmis, 'data-key': f.key });
        boxes.push(cb);
        return h('label', { class: 'docs-row' + (f.onAmis ? ' on' : '') }, cb,
          h('span', { class: 'mono docs-name' }, f.name),
          h('span', { class: 'muted small' }, fmtSize(f.size)),
          f.zip ? h('span', { class: 'chip warn' }, '→ .zip') : null,
          f.onAmis ? h('span', { class: 'chip pass' }, 'đã có trên AMIS') : null);
      }))));
  const send = h('button', { type: 'button', class: 'btn primary', onclick: async () => {
    const keys = boxes.filter(b => b.checked).map(b => b.dataset.key);
    if (!keys.length) { toast('Chưa chọn file nào', true); return; }
    if (!confirm(`Gửi ${keys.length} file lên ${d.target.isParent ? 'task cha' : 'task'} AMIS #${d.target.id}?\n${d.target.name}`)) return;
    send.disabled = true; send.textContent = 'Đang gửi…';
    try {
      const res = await api(`/jobs/${j.id}/amis-docs`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ keys }) });
      body.replaceChildren(h('div', { class: 'docs-target' }, `Kết quả gửi lên #${d.target.id}`),
        ...res.map(r => h('div', { class: 'docs-row ' + (r.ok ? 'ok' : 'bad') }, h('span', {}, r.ok ? '✓' : '✕'),
          h('span', { class: 'mono docs-name' }, r.sent), h('span', { class: 'muted small' }, r.note))));
      foot.replaceChildren(h('button', { type: 'button', class: 'btn', onclick: () => dlg.close() }, 'Đóng'));
      toast(`Đã gửi lên AMIS: ${res.filter(r => r.ok).length}/${res.length} file`, res.some(r => !r.ok));
    } catch (e) { toast(e.message, true); send.disabled = false; send.textContent = 'Gửi lên AMIS'; }
  } }, 'Gửi lên AMIS');
  foot.replaceChildren(h('button', { type: 'button', class: 'btn ghost', onclick: () => dlg.close() }, 'Huỷ'), send);
}

async function cleanupDuplicates() {
  const n = dupJobs().length;
  if (!confirm(`Xoá ${n} job dừng/huỷ/lỗi cũ của các task đã có job mới hơn?\nThư mục chuyển vào data\\jobs-trash (khôi phục được).`)) return;
  try { const r = await api('/jobs/cleanup-duplicates', { method: 'POST' }); toast(`Đã dọn ${r.removed} job trùng` + (r.failed?.length ? ` (${r.failed.length} job không chuyển được)` : '')); }
  catch (e) { toast(e.message, true); }
}

/** Sắc thái thẻ: run | you | bad | ok | stop | queue — quyết định màu cột trái, nền, vòng tiến độ. */
function cardTone(j) {
  if (RUNNING.has(j.status)) return 'run';
  if (j.status === 'Failed') return 'bad';
  if ((j.status === 'AwaitingApproval' || j.status === 'Approved') && (j.testVerdict === 'FAIL' || j.reviewVerdict === 'FAIL')) return 'bad';
  if (NEEDS_YOU.has(j.status) || j.status === 'AwaitingApproval') return 'you';
  if (j.status === 'Approved') return 'ok';
  if (j.status === 'Queued') return 'queue';
  return 'stop';
}

/** Avatar dự án: 2 chữ của mã dự án (ABC, XYZ…) + màu riêng cố định theo mã. */
function projAvatar(name) {
  const key = (projectKey(name || '') || (name || '?')).toUpperCase();
  let hash = 0;
  for (const ch of key) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return h('span', { class: 'proj-av', style: `--hue:${hash % 360}`, title: name || 'Không rõ dự án' }, key.replace(/[^A-Z0-9]/g, '').slice(0, 2) || '?');
}

const RING_SEEN = new Map();
/** Vòng tiến độ: phần xong / 5 bước; số ở giữa = bước đang ở. Lần vẽ sau giá trị tăng thì vòng chạy từ cũ tới mới. */
function progressRing(j) {
  const steps = pipelineSteps(j);
  const done = steps.filter(x => ['done', 'warn', 'fail-soft'].includes(x.state)).length;
  const cur = steps.find(x => !['done', 'warn', 'fail-soft', 'todo'].includes(x.state));
  const C = 2 * Math.PI * 15, frac = done / steps.length;
  const prev = RING_SEEN.has(j.id) ? RING_SEEN.get(j.id) : (boardShown ? 0 : frac);
  RING_SEEN.set(j.id, frac);
  const grow = prev !== frac && motionOk();
  const center = cur ? (cur.state === 'fail' ? '!' : cur.state === 'you' ? '●' : String(steps.indexOf(cur) + 1)) : '✓';
  const svg = (tag, attrs) => { const el = document.createElementNS('http://www.w3.org/2000/svg', tag); for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v); return el; };
  const root = svg('svg', { viewBox: '0 0 36 36', class: 'ring-svg', 'aria-hidden': 'true' });
  root.append(svg('circle', { cx: 18, cy: 18, r: 15, class: 'ring-bg' }));
  const arc = svg('circle', { cx: 18, cy: 18, r: 15, class: 'ring-fg' + (grow ? ' grow' : ''), 'stroke-dasharray': C.toFixed(2),
    style: `--to:${(C * (1 - frac)).toFixed(2)};--from:${(C * (1 - prev)).toFixed(2)};stroke-dashoffset:${(C * (1 - frac)).toFixed(2)}` });
  root.append(arc);
  return h('span', { class: 'ring' + (cur && cur.state === 'active' ? ' spinning' : ''), title: steps.map(x => `${x.label}: ${x.sub || x.state}`).join('\n') },
    root, h('span', { class: 'ring-c' }, center));
}

/** Thanh 5 bước ngang (Phân tích · Code · Review · Test · Duyệt): mỗi đoạn tô màu theo trạng thái, nhãn nhỏ bên dưới. */
const STEP_SHORT = { analyze: 'P.tích', code: 'Code', review: 'Review', test: 'Test', ship: 'Duyệt' };
function stepBar(j) {
  const steps = pipelineSteps(j);
  return h('div', { class: 'jc-steps', title: steps.map(x => `${x.label}: ${x.sub || x.state}`).join('\n') },
    steps.map(x => h('div', { class: 'jc-step st-' + x.state },
      h('i', {}),
      h('span', {}, STEP_SHORT[x.key] || x.label))));
}

/** Chip theo độ quan trọng (lỗi/cảnh báo trước) — hiện đủ, tự xuống dòng. */
function cardChips(j) {
  const list = [];
  const add = (pri, el) => { if (el) list.push([pri, el]); };
  const v = (label, val) => verdictChip(label, val);
  add(1, overlapChip(j));
  add(1, j.qcFix ? h('span', { class: 'chip qc-chip', title: 'Sửa lỗi QC trả về' }, `🐞 QC vòng ${qcRound(j)}`) : null);
  add(1, j.inspecting ? h('span', { class: 'chip warn' }, '🔍 Đang kiểm') : null);
  add(1, j.requirement && j.requirement !== 'CLEAR' ? h('span', { class: 'chip fail' }, 'Yêu cầu thiếu') : null);
  add(j.reviewVerdict === 'FAIL' ? 1 : 3, j.reviewVerdict ? v('Review', j.reviewVerdict) : null);
  add(j.testVerdict && j.testVerdict !== 'PASS' ? 1 : 3, j.testVerdict ? v('Test', j.testVerdict) : null);
  add(2, j.autoApproved ? h('span', { class: 'chip ok-chip' }, '🤖 Tự duyệt') : null);
  add(2, j.chainedFromTask ? h('span', { class: 'chip', title: `Nhánh nối tiếp từ ${j.chainedFromBranch}` }, `↳ nối #${j.chainedFromTask}`) : null);
  add(3, j.fixRounds ? h('span', { class: 'chip' }, `Sửa ${j.fixRounds} vòng`) : null);
  add(4, j.codeStatus ? v('Code', j.codeStatus) : null);
  add(4, j.analyzeRounds > 1 ? h('span', { class: 'chip' }, `Phân tích ${j.analyzeRounds} lần`) : null);
  add(4, j.autoRetries ? h('span', { class: 'chip' }, '↻ đã thử lại') : null);
  add(5, j.requirement === 'CLEAR' ? h('span', { class: 'chip pass' }, 'Yêu cầu rõ') : null);
  add(5, j.batch ? h('span', { class: 'chip' }, j.unattended ? '🌙 Qua đêm' : 'Hàng loạt') : null);
  list.sort((a, b) => a[0] - b[0]);
  return list.map(x => x[1]);
}

function renderJobCard(j) {
  const tone = cardTone(j);
  const diff = j.diffList?.length ? `${j.diffList.length}f +${j.diffAdded} −${j.diffDeleted}` : '';
  return h('article', { class: `card job jc jc-${tone}` + (NEEDS_YOU.has(j.status) ? ' needs-you' : ''), 'data-id': j.id, 'data-status': j.status, onclick: () => openDrawer(j.id) },
    h('div', { class: 'jc-head' },
      projAvatar(j.projectName),
      h('div', { class: 'jc-id' },
        h('div', { class: 'jc-code' }, taskCode(j), j.parentTaskName ? h('span', { class: 'jc-sub', title: 'Task con' }, '↳ #' + j.taskId) : null),
        h('div', { class: 'jc-name', title: j.parentTaskName ? `${j.parentTaskName}\n↳ ${j.taskName}` : j.taskName }, j.parentTaskName || j.taskName)),
      canDelete(j) ? h('button', { type: 'button', class: 'jc-del', title: 'Xoá job này (chuyển vào thùng rác)', onclick: e => deleteJob(j, e) }, '🗑') : null),
    stepBar(j),
    h('div', { class: 'jc-meta' },
      RUNNING.has(j.status) ? h('span', { class: 'running-dot' }) : null,
      h('span', { class: 'badge s-' + j.status }, STATUS_LABEL[j.status] || j.status),
      h('span', { class: 'jc-nums' },
        diff ? h('span', { class: 'num', title: (j.diffList || []).join('\n') }, diff) : null,
        j.costUsd ? h('span', { class: 'num' }, '$' + j.costUsd.toFixed(2)) : null,
        h('span', { class: 'num elapsed', 'data-job': j.id }, elapsedText(j)))),
    h('div', { class: 'chips' }, cardChips(j)),
    j.error ? h('div', { class: 'err' }, j.error) : (j.lastLog ? h('div', { class: 'lastlog', title: j.lastLog }, j.lastLog) : null));
}

/** Task khác (còn mở) cùng sửa file với job này. */
function openOverlaps(j) {
  return Object.entries(j.overlaps || {}).filter(([tid]) => [...S.jobs.values()].some(o => String(o.taskId) === tid && ['AwaitingApproval', 'WaitingForUser'].includes(o.status)));
}

function overlapChip(j) {
  const ov = openOverlaps(j);
  if (!ov.length) return null;
  return h('span', { class: 'chip warn', title: ov.map(([tid, files]) => `#${tid}: ${files.join(', ')}`).join('\n') }, `⚠ Trùng file với ${ov.map(([tid]) => '#' + tid).join(', ')}`);
}

setInterval(() => {
  document.querySelectorAll('.elapsed[data-job]').forEach(el => {
    const j = S.jobs.get(el.dataset.job);
    if (j && RUNNING.has(j.status)) el.textContent = elapsedText(j);
  });
}, 1000);

// ---------- Popup giao task ----------
async function openStartDialog(t) {
  const existing = activeJobForTask(t.taskId);
  if (existing) { openDrawer(existing.id); return; }
  const dlg = $('#dlgStart');
  const body = $('.dlg-body', dlg);
  S.pendingStart = { task: t, detail: null };
  $('#btnStart').disabled = true;
  body.replaceChildren(h('div', { class: 'loading' }, 'Đang đọc task từ AMIS…'));
  dlg.showModal();
  try {
    const d = await api('/tasks/' + t.taskId);
    if (S.pendingStart?.task.taskId !== t.taskId) return;
    S.pendingStart.detail = d;
    body.replaceChildren(h('div', { id: 'startWarn' }), ...renderStartBody(d).filter(Boolean));
    $('#btnStart').disabled = false;
    updateStartWarning();
  } catch (e) {
    body.replaceChildren(h('div', { class: 'errbox' }, 'Không đọc được task: ' + e.message));
  }
}

/** Vẽ lại cảnh báo nhánh trong popup giao task (khi mở, khi đổi nhánh, khi lưu từ khoá). */
function updateStartWarning() {
  const p = S.pendingStart;
  const box = $('#startWarn');
  const btn = $('#btnStart');
  if (!p || !p.detail || !box) return;
  const project = p.detail.projectName || p.task.projectName;
  box.replaceChildren(renderBranchWarning(project, updateStartWarning), renderDbNotice(project));
  const bad = branchCheck(project).ok === false;
  btn.textContent = bad ? 'Vẫn giao cho AI' : 'Giao cho AI';
  btn.classList.toggle('warn', bad);
}

function descFrame(html, title) {
  const frame = h('iframe', { class: 'desc-frame', sandbox: '', title });
  // Lấy màu theo giao diện sáng/tối đang dùng (iframe không kế thừa biến CSS của trang)
  const cs = getComputedStyle(document.documentElement);
  const v = n => cs.getPropertyValue(n).trim();
  const dark = cs.colorScheme === 'dark' || cs.getPropertyValue('color-scheme').trim() === 'dark';
  // Mô tả AMIS hay có màu chữ đen/nền trắng viết cứng — ở giao diện tối thì bỏ để vẫn đọc được
  const darkFix = dark ? 'body *{color:inherit!important;background-color:transparent!important}body a{color:' + v('--brand') + '!important}' : '';
  frame.srcdoc = `<!doctype html><meta charset="utf-8"><style>:root{color-scheme:${dark ? 'dark' : 'light'};color:${v('--text')}}body{font:14px/1.5 "Inter","Segoe UI",sans-serif;color:${v('--text')};background:${v('--panel-2')};margin:12px}a{color:${v('--brand')}}img{max-width:100%;border-radius:6px}table{border-collapse:collapse}td,th{border:1px solid ${v('--line')};padding:4px}${darkFix}</style>${html || '<i style="opacity:.6">(Không có mô tả)</i>'}`;
  return frame;
}

function renderStartBody(d) {
  const frame = descFrame(d.description, 'Mô tả task');
  const p = d.parent;
  const amisFiles = d.attachments || [];
  return [
    h('div', { class: 'task-title' }, d.taskName),
    h('dl', { class: 'kv' },
      h('dt', {}, 'Dự án'), h('dd', {}, d.projectName || '—'),
      h('dt', {}, 'Cột'), h('dd', {}, d.kanbanName || '—'),
      h('dt', {}, 'Hạn'), h('dd', { class: 'due ' + dueClass(d.endDate) }, fmtDate(d.endDate) || '—'),
      h('dt', {}, 'Người giao'), h('dd', {}, d.assignerName || d.ownerName || '—'),
      h('dt', {}, 'Thực hiện'), h('dd', {}, d.assigneeName || '—')),
    h('div', {}, h('div', { class: 'section-label' }, 'Mô tả'), frame),
    p ? h('div', {},
      h('div', { class: 'section-label' }, `Task cha #${p.taskId}: ${p.taskName}`),
      h('div', { class: 'muted' }, 'Đây là công việc con — AI đọc cả mô tả, bình luận' + (p.commentCount ? ` (${p.commentCount})` : '') + ' và tệp của task cha.'),
      descFrame(p.description, 'Mô tả task cha'))
      : null,
    (d.comments || []).length
      ? h('details', { class: 'comments' }, h('summary', {}, `Bình luận (${d.comments.length})`),
        d.comments.map(c => h('div', { class: 'comment' },
          h('div', { class: 'who' }, `${c.fullName} · ${fmtDate(c.logTime)}`),
          h('div', {}, htmlToText(c.content)),
          c.files.length ? h('div', { class: 'muted' }, 'Tệp: ' + c.files.join(', ')) : null)))
      : null,
    h('div', {},
      h('div', { class: 'section-label' }, `Tệp đính kèm trên AMIS (${amisFiles.length})`),
      amisFiles.length
        ? h('ul', { class: 'filelist' }, amisFiles.map(a => h('li', {}, a.fileName, ' ', h('span', { class: 'muted' }, `${a.fileType || ''} · ${Math.round((a.fileSize || 0) / 1024)} KB${a.fromParent ? ' · task cha' : ''}`))))
        : h('div', { class: 'muted' }, 'Không có.'),
      amisFiles.length ? h('div', { class: 'notice' }, 'Tool tự tải các tệp này (cả tệp trong bình luận) khi phân tích và trích nội dung Word/Excel/PowerPoint; PDF và ảnh AI đọc trực tiếp.') : null),
    h('div', {},
      h('div', { class: 'section-label' }, 'Tài liệu bổ sung cho AI'),
      h('input', { type: 'file', id: 'inpFiles', multiple: true, class: 'input', accept: ACCEPT_FILES })),
    h('div', {},
      h('div', { class: 'section-label' }, 'Mức test cho task này'),
      h('select', { id: 'inpTestMode', class: 'input sm' },
        h('option', { value: '' }, `Theo cấu hình (${TEST_MODE_VI[S.testMode || 'full']})`),
        h('option', { value: 'full' }, 'Đầy đủ — Playwright + ảnh + file Word cho QC'),
        h('option', { value: 'data' }, 'Chỉ kiểm dữ liệu — DB/API, rẻ hơn'),
        h('option', { value: 'off' }, 'Không test — task nhỏ (sửa label, mail…)'))),
    h('div', {},
      h('div', { class: 'section-label' }, 'Ghi chú thêm cho AI (tuỳ chọn)'),
      h('textarea', { id: 'inpNote', class: 'input', placeholder: 'Ví dụ: chỉ sửa màn X, không đụng store Y; ưu tiên làm theo cách…' })),
    h('div', { class: 'notice' },
      'Bước 1 — AI phân tích yêu cầu (chỉ đọc, không đụng repo): đủ rõ thì task sang cột "Đã rõ yêu cầu", thiếu thì sang "Cần bổ sung" kèm câu hỏi trong Chat. ' +
      `Bước 2 — khi bạn bấm "Bắt đầu code", AI tạo nhánh mới từ ${S.repo ? `"${S.repo.info.branch}" của ${S.repo.info.name}` : 'nhánh hiện tại'}, code, review, test rồi commit vào nhánh đó. ` +
      (S.repo && S.repo.info.changed ? `Repo đang có ${S.repo.info.changed} file sửa chưa commit — phải commit hoặc stash trước bước 2. ` : '') +
      'Tool không bao giờ push.'),
  ];
}

function htmlToText(html) {
  const doc = new DOMParser().parseFromString(html || '', 'text/html');
  return (doc.body.textContent || '').trim();
}

async function submitStart() {
  const p = S.pendingStart;
  if (!p || !p.detail) return;
  const btn = $('#btnStart');
  btn.disabled = true;
  const fd = new FormData();
  fd.append('taskId', p.task.taskId);
  fd.append('taskName', p.detail.taskName || p.task.taskName || '');
  fd.append('projectName', p.detail.projectName || '');
  fd.append('endDate', p.detail.endDate || '');
  fd.append('note', $('#inpNote').value);
  fd.append('testMode', $('#inpTestMode')?.value || '');
  for (const f of $('#inpFiles').files) fd.append('files', f, f.name);
  try {
    const job = await api('/jobs', { method: 'POST', body: fd });
    S.jobs.set(job.id, job);
    $('#dlgStart').close();
    S.pendingStart = null;
    toast('Đã giao cho AI: ' + job.taskName);
    render();
    // Đang chat về task này thì chuyển khung bên phải sang theo dõi job vừa tạo
    if (!$('#drawer').hidden && S.chatTask && S.chatTask.taskId === job.taskId) openDrawer(job.id);
  } catch (e) {
    toast(e.message, true);
    btn.disabled = false;
  }
}

// ---------- Drawer job ----------
async function openDrawer(id) {
  const j = S.jobs.get(id);
  S.openJob = id;
  S.chatTask = j ? { taskId: j.taskId, taskName: j.taskName } : null;
  S.drawerTab = !j ? 'chat'
    : (j.status === 'WaitingForUser' || j.status === 'NeedsInfo') ? 'chat'
    : (j.status === 'AwaitingApproval' || j.status === 'Approved' || (j.branch && (j.headSha || j.held))) ? 'diff'
    : 'docs';
  if (S.drawerTab === 'docs') S.doc = 'analysis.md';
  TL.id = null; // timeline nạp lại cho job mới
  $('#drawer').hidden = false;
  renderDrawer();
  await showDrawerTab();
}

/** Mở khung chat cho một task AMIS chưa (hoặc đã) giao cho AI. */
async function openTaskChat(t) {
  const job = activeJobForTask(t.taskId);
  if (job) { await openDrawer(job.id); S.drawerTab = 'chat'; await showDrawerTab(); return; }
  S.openJob = null;
  S.chatTask = { taskId: t.taskId, taskName: t.taskName };
  S.drawerTab = 'chat';
  $('#drawer').hidden = false;
  await showDrawerTab();
}

function closeDrawer() {
  S.openJob = null;
  S.chatTask = null;
  $('#drawer').hidden = true;
}

function renderDrawer() {
  const d = $('#drawer');
  const j = S.jobs.get(S.openJob);
  d.querySelectorAll('.tabs button').forEach(b => {
    b.hidden = (!j && b.dataset.tab !== 'chat') || b.dataset.tab === 'log'; // Nhật ký dài → thay bằng timeline bên trái
    b.classList.toggle('active', b.dataset.tab === S.drawerTab);
  });
  if (!j) {
    const t = S.chatTask;
    renderDrawerFlow(null);
    if (!t) return;
    $('.drawer-title', d).replaceChildren(t.taskName || `Task #${t.taskId}`);
    $('.drawer-meta', d).replaceChildren(h('span', {}, 'AMIS #', h('b', {}, t.taskId)), h('span', {}, 'Chưa giao cho AI'));
    $('.drawer-actions', d).replaceChildren(
      h('button', { class: 'btn primary', type: 'button', onclick: () => openStartDialog(t) }, 'Giao cho AI'),
      h('div', { class: 'note' }, 'Trao đổi để làm rõ yêu cầu trước. Khi giao cho AI, nội dung chat được đưa vào yêu cầu cho bước code.'));
    return;
  }
  $('.drawer-title', d).replaceChildren(h('span', { class: 'badge s-' + j.status }, STATUS_LABEL[j.status]), ' ', j.taskName);
  renderDrawerFlow(j);
  $('.drawer-meta', d).replaceChildren(
    h('span', {}, 'AMIS #', h('b', {}, j.taskId)),
    j.branch ? h('span', {}, 'Nhánh ', h('b', {}, j.branch)) : null,
    j.baseBranch ? h('span', {}, 'Từ ', h('b', {}, j.baseBranch)) : null,
    j.analyzedBranch && !j.baseBranch ? h('span', {}, 'Phân tích trên nhánh ', h('b', {}, j.analyzedBranch)) : null,
    j.testDataDb ? h('span', { class: j.testDataPending ? 'bad-text' : '' }, j.testDataPending ? 'Dữ liệu test CÒN trong ' : 'Đã dọn dữ liệu test ở ', h('b', {}, j.testDataDb)) : null,
    // Chỉ ý nghĩa trước khi code: lúc chạy repo đang ở nhánh ai/…, xong thì đã trả về nhánh gốc (hiện ở "Từ …")
    j.projectName && ['Queued', 'Analyzing', 'NeedsInfo', 'Clarified'].includes(j.status) && branchCheck(j.projectName).ok === false
      ? h('span', { class: 'bad-text' }, `⚠ Repo đang ở nhánh khác dự án ${projectKey(j.projectName)}`) : null,
    j.startedAt ? h('span', {}, 'Thời gian ', h('b', {}, elapsedText(j))) : null,
    j.costUsd ? h('span', {}, 'Chi phí ', h('b', {}, '$' + j.costUsd.toFixed(2))) : null,
    h('span', { class: 'chips' }, verdictChip('Code', j.codeStatus), verdictChip('Review', j.reviewVerdict), verdictChip('Test', j.testVerdict),
      j.testMode ? h('span', { class: 'chip', title: 'Mức test riêng của task này (chat: #id test lại đầy đủ / chỉ kiểm dữ liệu để đổi)' }, 'Mức test: ' + (TEST_MODE_VI[j.testMode] || j.testMode)) : null));

  const actions = [];
  if (j.qcFix && j.qcReply) {
    actions.push(h('button', {
      class: 'btn', type: 'button', title: 'Bình luận soạn sẵn trả lời QC (từng lỗi: nguyên nhân, đã sửa gì) — dán vào task cha trên AMIS',
      onclick: () => navigator.clipboard.writeText(j.qcReply).then(() => toast('Đã copy trả lời QC')),
    }, '📋 Copy trả lời QC'));
  }
  if (j.status === 'NeedsInfo' || j.status === 'Clarified') {
    const clear = j.status === 'Clarified';
    const files = h('input', { type: 'file', multiple: true, accept: ACCEPT_FILES, hidden: true, onchange: e => uploadJobFiles(j.id, e.target) });
    actions.push(
      clear
        ? h('button', { class: 'btn primary', type: 'button', onclick: () => proceed(j.id, 'code') }, 'Bắt đầu code')
        : h('button', { class: 'btn primary', type: 'button', onclick: () => { S.drawerTab = 'chat'; showDrawerTab(); } }, 'Trả lời câu hỏi'),
      !clear && j.baMessage ? h('button', {
        class: 'btn', type: 'button', title: 'Mọi câu hỏi gom trong một tin, kèm phương án đề xuất — dán gửi BA/khách một lần\n\n' + j.baMessage,
        onclick: () => navigator.clipboard.writeText(j.baMessage).then(() => toast('Đã copy tin nhắn gửi BA')),
      }, '📋 Copy tin gửi BA') : null,
      files,
      h('button', { class: 'btn', type: 'button', onclick: () => files.click() }, 'Bổ sung tài liệu'),
      h('button', { class: 'btn', type: 'button', onclick: () => proceed(j.id, 'reanalyze') }, 'Phân tích lại'),
      clear ? null : h('button', { class: 'btn', type: 'button', title: 'AI theo phương án đề xuất trong bản phân tích cho các điểm còn thiếu', onclick: () => proceed(j.id, 'code') }, 'Vẫn giao code'),
      h('button', { class: 'btn danger', type: 'button', onclick: () => cancelJob(j.id) }, 'Huỷ job'),
      h('div', { class: 'note' }, clear
        ? 'Xem tab Tài liệu › Phân tích. Đồng ý thì bấm "Bắt đầu code"; muốn chỉnh thì trao đổi trong Chat rồi "Phân tích lại".'
        : 'Trả lời trong Chat rồi bấm "Gửi & phân tích lại", hoặc bổ sung tài liệu còn thiếu rồi bấm "Phân tích lại".' +
          (j.inputFiles && j.inputFiles.length ? ` Đã có: ${j.inputFiles.join(', ')}.` : '')));
  }
  if (j.status === 'WaitingForUser') {
    actions.push(
      h('button', { class: 'btn primary', type: 'button', onclick: () => { S.drawerTab = 'chat'; showDrawerTab(); } }, 'Trả lời câu hỏi của AI'),
      h('button', { class: 'btn danger', type: 'button', onclick: () => cancelJob(j.id) }, 'Huỷ job'));
  }
  if (j.status === 'Queued' || RUNNING.has(j.status)) {
    actions.push(h('button', { class: 'btn danger', type: 'button', onclick: () => cancelJob(j.id) }, 'Huỷ job'));
  }
  if (j.held && !RUNNING.has(j.status) && j.status !== 'Queued') {
    // Code AI đang nằm CHƯA COMMIT trên nhánh AI: người dùng xem lại rồi tự bấm commit
    actions.push(h('div', { class: 'held-note', title: 'Web đang chạy code của AI nên bạn có thể kiểm tay trước khi commit.' },
      h('span', { class: 'held-ic' }, '⏸'),
      h('span', {}, h('b', {}, 'Code AI chưa commit'), ' · repo đang giữ ở nhánh AI — job khác chưa code được tới khi bạn commit hoặc trả nhánh.')));
    if (j.status === 'AwaitingApproval')
      actions.push(
        h('button', { class: 'btn ok', type: 'button', onclick: () => openCommit(j.id, 'approve') }, 'Tạo commit & push…'),
        h('button', { class: 'btn', type: 'button', onclick: () => openCommit(j.id, 'reject') }, 'Từ chối…'),
        selfBtn(j));
    else
      actions.push(h('button', { class: 'btn', type: 'button', onclick: () => openCommit(j.id, null) }, 'Commit & trả nhánh…'));
  } else if (j.status === 'AwaitingApproval') {
    actions.push(
      h('button', { class: 'btn ok', type: 'button', onclick: () => decide(j.id, true, true) }, 'Duyệt & push'),
      h('button', { class: 'btn', type: 'button', onclick: () => decide(j.id, true) }, 'Chỉ duyệt'),
      h('button', { class: 'btn', type: 'button', onclick: () => decide(j.id, false) }, 'Từ chối'),
      selfBtn(j),
      h('div', { class: 'note' }, `Code đã commit vào nhánh ${j.branch}. Xem tab Diff và Tài liệu trước khi duyệt. "Duyệt & push" đẩy nhánh lên origin; "Chỉ duyệt" để bạn tự push.`));
  }
  if (j.chainedFromTask && ['AwaitingApproval', 'Approved', 'WaitingForUser'].includes(j.status)) {
    actions.push(h('div', { class: 'note', style: 'width:100%' }, `↳ Nhánh này tạo nối tiếp từ ${j.chainedFromBranch} (task #${j.chainedFromTask}) vì hai task sửa cùng file — PR của #${j.chainedFromTask} phải merge trước, khi đó PR này chỉ còn phần của task này.`));
    const dr = chainDrift(j);
    if (dr && dr.length) {
      const why = dr.map(d => `${d.rel}: ${d.parent} ${d.rewritten ? 'đã bị viết lại (rebase/amend)' : `có thêm ${d.newCommits} commit`}`).join('; ');
      actions.push(h('div', { class: 'errbox', style: 'width:100%' },
        `⚠ Nhánh cha đã đổi sau khi tạo nhánh này (${why}). Nếu push bây giờ, PR này kéo theo bản cũ của nhánh cha và sẽ conflict khi merge.`, h('br'),
        h('button', { class: 'btn sm primary', type: 'button', style: 'margin-top:6px', title: 'Dựng lại các commit của task này lên bản mới nhất của nhánh cha (git replay, không đụng working copy). Conflict thì không đổi gì.', onclick: () => restack(j) }, 'Cập nhật theo nhánh cha')));
    }
  }
  if (j.inspecting) {
    actions.push(h('div', { class: 'errbox', style: 'width:100%' },
      `🔍 Đang kiểm trên web: repo ở nhánh ${j.branch}, web chạy code của task này (xem Nhật ký: build xong mới kiểm). Sửa thêm trong Visual Studio được. Hàng đợi AI tạm dừng tới khi bạn bấm Xong kiểm.`, h('br'),
      h('button', { class: 'btn sm primary', type: 'button', style: 'margin-top:6px', onclick: () => inspect(j, false) }, 'Xong kiểm — commit phần sửa thêm & trả repo')));
  } else if (j.headSha && j.branch && !j.held && ['AwaitingApproval', 'Approved', 'Failed'].includes(j.status)) {
    actions.push(h('button', { class: 'btn', type: 'button', title: 'Chuyển repo sang nhánh AI của task này và build để kiểm tay trên web', onclick: () => inspect(j, true) }, '🔍 Kiểm trên web'));
  }
  if (j.branch && Object.keys(j.prUrls || {}).length && ['AwaitingApproval', 'Approved'].includes(j.status)) {
    actions.push(h('button', { class: 'btn', type: 'button', title: 'Soạn bình luận kết quả, chuyển task sang Hoàn thành trên AMIS (xem trước khi gửi)', onclick: () => openAmisUpdate(j.id) },
      j.amisCommentAt || j.amisDoneAt ? 'AMIS ✓' : 'Cập nhật AMIS…'));
  }
  const prs = Object.entries(j.prUrls || {});
  if (prs.length) {
    actions.push(h('div', { class: 'note', style: 'width:100%' }, 'Pull Request: ', ...prs.flatMap(([rel, url], i) => [i ? ' · ' : null,
      h('a', { href: url, target: '_blank', rel: 'noopener' }, `${rel.split(/[\\/]/).pop()} #${url.split('/').pop()}`)])));
  } else if (j.headSha && j.branch && !j.held && ['AwaitingApproval', 'Approved'].includes(j.status)) {
    actions.push(h('button', { class: 'btn', type: 'button', title: 'Push nhánh AI (kiểm conflict trước) rồi tạo PR trên TFS', onclick: () => createPr(j) }, 'Tạo PR'));
  }
  if (openOverlaps(j).length) {
    actions.push(h('div', { class: 'errbox', style: 'width:100%' }, '⚠ Trùng file với task khác đang mở — PR merge sau sẽ phải xử lý conflict:',
      h('ul', {}, openOverlaps(j).map(([tid, files]) => h('li', {}, h('b', {}, '#' + tid), ': ', files.join(', '))))));
  }
  if (j.status === 'Approved' && j.branch && S.config) {
    // Mỗi repo có commit trên nhánh AI cần push riêng
    const cmd = [`git -C "${S.config.repoPath}" push -u origin ${j.branch}`,
      ...(j.extraRepos || []).filter(r => r.headSha && r.headSha !== r.baseSha).map(r => `git -C "${r.path}" push -u origin ${j.branch}`)].join('\n');
    actions.push(h('div', { class: 'pushcmd' }, h('code', {}, cmd),
      h('button', { class: 'btn sm', type: 'button', onclick: () => navigator.clipboard.writeText(cmd).then(() => toast('Đã copy lệnh push')) }, 'Copy')));
  }
  // Tiêu đề PR theo chuẩn công ty "[ID TASK] - Mô tả" (N1, N2); mô tả PR (N6) ở tab Tài liệu → "Mô tả PR"
  if (j.prTitle && (j.status === 'AwaitingApproval' || j.status === 'Approved')) {
    actions.push(h('div', { class: 'pushcmd' }, h('span', { class: 'muted small' }, 'Tiêu đề PR '), h('code', {}, j.prTitle),
      h('button', { class: 'btn sm', type: 'button', onclick: () => navigator.clipboard.writeText(j.prTitle).then(() => toast('Đã copy tiêu đề PR')) }, 'Copy')));
  }
  if (j.testDataPending && !RUNNING.has(j.status)) {
    actions.push(h('button', { class: 'btn danger', type: 'button', onclick: () => cleanupData(j.id) }, 'Dọn dữ liệu test'));
  }
  if (j.branch && !RUNNING.has(j.status)) {
    actions.push(h('button', { class: 'btn', type: 'button', title: 'Gửi tài liệu (specs: kế hoạch, hướng dẫn PE, thay đổi API cho App; file Word test case) lên task cha AMIS', onclick: () => openAmisDocs(j) }, '📎 Gửi tài liệu lên AMIS…'));
  }
  if (j.aiDbWrites > 0 && !RUNNING.has(j.status)) {
    actions.push(h('button', { class: 'btn danger', type: 'button', title: 'Chạy db-undo.sql do AI soạn để trả DB test về như trước (xem db-writes.sql trong tab Tài liệu)',
      onclick: async () => {
        if (!await ask(`AI đã ghi DB test ${j.aiDbWrites} lần trong job này.\nChạy db-undo.sql để hoàn tác? (xem trước db-writes.sql và db-undo.sql ở tab Tài liệu)`)) return;
        try { const job = await api(`/jobs/${j.id}/db-undo`, { method: 'POST' }); S.jobs.set(job.id, job); renderDrawer(); render(); toast('Đã hoàn tác thay đổi DB của AI.'); }
        catch (e) { toast(e.message, true); }
      } }, `Hoàn tác thay đổi DB của AI (${j.aiDbWrites})`));
  }
  if ((j.sqlDeployed || []).length && !RUNNING.has(j.status)) {
    actions.push(h('button', { class: 'btn danger', type: 'button', title: `Đang deploy trên ${j.sqlDeployDb}: ${j.sqlDeployed.join(', ')}`, onclick: () => restoreSql(j) },
      `Khôi phục store DB test (${j.sqlDeployed.length})`));
  }
  // Job lỗi/huỷ sau khi code xong: làm tiếp review → test trên code đã commit ở nhánh AI
  const other = activeJobForTask(j.taskId);
  // Bị huỷ/lỗi giữa bước code (chưa có commit): làm tiếp phiên AI cũ, hoặc code lại trên đúng nhánh đó
  if (['Failed', 'Cancelled'].includes(j.status) && j.codeApproved && !j.headSha && (!other || other.id === j.id)) {
    actions.push(
      h('button', { class: 'btn primary', type: 'button', onclick: () => proceed(j.id, 'recode') },
        j.branch && j.codeSessionId ? 'Chạy tiếp bước code' : 'Chạy lại bước code'),
      h('div', { class: 'note' }, !j.branch
        ? 'Lỗi xảy ra trước khi tạo nhánh AI — chạy lại bước code, dùng lại bản phân tích đã có (không phân tích lại).'
        : j.codeSessionId
          ? `AI làm tiếp phiên code bị ngắt trên nhánh ${j.branch} — phần đã sửa được giữ nguyên.`
          : `AI code lại trên nhánh ${j.branch} (lần trước bị ngắt trước khi có kết quả).`));
  }
  if ((['Failed', 'Cancelled', 'AwaitingApproval'].includes(j.status) || (j.status === 'Approved' && j.held)) && j.branch && (j.headSha || j.held) && (!other || other.id === j.id)) {
    actions.push(
      h('button', { class: 'btn primary', type: 'button', title: 'AI sửa/thêm code theo yêu cầu của bạn (và những gì đã chốt trong Chat) trên nhánh AI, rồi chạy lại review + test',
        onclick: () => openRevise(j.id) }, 'Yêu cầu AI sửa…'),
      h('button', { class: 'btn', type: 'button', onclick: () => proceed(j.id, 'review') },
        j.status === 'AwaitingApproval' ? 'Review + test lại' : 'Chạy tiếp: review + test'),
      h('div', { class: 'note' }, j.held
        ? 'Làm tiếp trên code AI đang giữ ở nhánh ' + j.branch + ', không code lại.'
        : `Dùng code AI đã commit trên nhánh ${j.branch}, không code lại. Repo phải sạch (file cấu hình local không tính).`));
  }
  const errorBox = j.error ? h('div', { class: 'errbox act-error' }, h('span', { class: 'act-error-ic' }, '!'), h('span', {}, j.error)) : null;
  $('.drawer-actions', d).replaceChildren(...arrangeActions(actions, errorBox));
}

/**
 * Sắp lại vùng hành động cho gọn: lỗi → thông báo → MỘT hàng nút (một nút chính, nút huỷ/xoá dồn phải)
 * → một dòng gợi ý của nút chính. Gợi ý của các nút khác thành chú thích khi rê chuột.
 */
function arrangeActions(items, errorBox) {
  const alerts = [], banners = [], buttons = [], hidden = [];
  let lastBtn = null;
  for (const el of items.filter(Boolean)) {
    if (el.tagName === 'BUTTON') { buttons.push(el); lastBtn = el; continue; }
    if (el.tagName === 'INPUT') { hidden.push(el); continue; }
    if (el.classList.contains('errbox')) { alerts.push(el); continue; }
    if (el.classList.contains('note') && !el.classList.contains('held-note') && !el.style.width) {
      if (lastBtn) { lastBtn._hint = el.textContent; if (!lastBtn.title) lastBtn.title = el.textContent; }
      continue;
    }
    banners.push(el);
  }
  const isMain = b => b.classList.contains('primary') || b.classList.contains('ok');
  const isDanger = b => b.classList.contains('danger');
  const main = buttons.filter(isMain), normal = buttons.filter(b => !isMain(b) && !isDanger(b)), danger = buttons.filter(isDanger);
  // Chỉ một nút được tô màu chính — các nút "chính" còn lại hạ xuống thường
  main.slice(1).forEach(b => b.classList.remove('primary'));
  const ordered = [...main, ...normal, ...danger];
  const first = ordered[0];
  const hint = first && first._hint ? h('div', { class: 'act-hint' }, first._hint) : null;
  return [
    errorBox, ...alerts, ...banners,
    ordered.length ? h('div', { class: 'act-bar' }, ...ordered, ...hidden) : (hidden.length ? h('div', { hidden: true }, ...hidden) : null),
    hint,
  ].filter(Boolean);
}

async function showDrawerTab() {
  const id = S.openJob;
  const body = $('.drawer-body');
  renderDrawer();
  body.classList.toggle('chat-mode', S.drawerTab === 'chat');
  body.replaceChildren(h('div', { class: 'loading' }, 'Đang tải…'));
  loadTimeline();
  try {
    if (S.drawerTab === 'test') {
      await showDoc(id, 'test.md');
    } else if (S.drawerTab === 'chat') {
      await showChat();
    } else if (S.drawerTab === 'log') {
      const logs = await api(`/jobs/${id}/logs`);
      if (S.openJob !== id || S.drawerTab !== 'log') return;
      const box = h('div', { class: 'log', id: 'logBox' }, logs.map(renderLogRow));
      body.replaceChildren(logs.length ? box : h('div', { class: 'empty' }, 'Chưa có nhật ký.'));
      if (!logs.length) body.append(box);
      body.scrollTop = body.scrollHeight;
    } else if (S.drawerTab === 'docs') {
      await showDoc(id, S.doc);
    } else if (S.drawerTab === 'diff') {
      const diff = await api(`/jobs/${id}/diff`);
      if (S.openJob !== id || S.drawerTab !== 'diff') return;
      body.replaceChildren(diff.trim() ? renderDiff(diff) : h('div', { class: 'empty' }, 'Chưa có diff (AI chưa tạo nhánh hoặc chưa thay đổi gì).'));
    }
  } catch (e) {
    body.replaceChildren(h('div', { class: 'errbox' }, e.message));
  }
}

async function showDoc(id, name) {
  S.doc = name;
  const body = $('.drawer-body');
  const picker = h('div', { class: 'doc-picker' }, DOCS.map(([n, label]) =>
    h('button', { class: 'btn sm' + (n === name ? ' active' : ''), type: 'button', onclick: () => pickDoc(id, n) }, label)),
    h('button', { class: 'btn sm' + (name === '@artifacts' ? ' active' : ''), type: 'button', onclick: () => pickDoc(id, '@artifacts') }, 'Bằng chứng test'));
  if (name === '@artifacts') { S.doc = name; return showArtifacts(id, picker); }
  // Test / Test case: thanh tải file Word gửi QC
  const job = S.jobs.get(id);
  body.replaceChildren(picker, h('div', { class: 'loading' }, 'Đang tải…'));
  try {
    const text = await api(`/jobs/${id}/files/${name}`);
    // Tab "Test" và "Tài liệu" đều hiện tài liệu qua hàm này
    if (S.openJob !== id || !['docs', 'test'].includes(S.drawerTab) || S.doc !== name) return;
    const bar = (name === 'test.md' || name === 'testcases.md') && job ? testCaseBar(job) : null;
    body.replaceChildren(picker, bar || '', name.endsWith('.md') ? renderMarkdown(text, 'md doc') : h('pre', { class: 'pre' }, text));
  } catch (e) {
    if (S.openJob !== id) return;
    body.replaceChildren(picker, h('div', { class: 'empty' }, e.status === 404 ? 'Chưa có tài liệu này.' : e.message));
  }
}

/** Thanh test case cho QC: tóm tắt + tải Word + dựng lại từ testcases.json. */
function testCaseBar(j) {
  const has = !!j.testCaseSummary;
  const regen = async () => {
    try { const r = await api(`/jobs/${j.id}/testcases/export`, { method: 'POST' }); j.testCaseSummary = r.summary; toast('Đã dựng lại file test case: ' + r.summary); showDrawerTab(); }
    catch (e) { toast(e.message, true); }
  };
  return h('div', { class: 'tc-bar' + (has ? '' : ' empty') },
    h('span', { class: 'tc-ic' }, '🧪'),
    h('div', { class: 'tc-txt' },
      h('b', {}, 'Test case cho QC'),
      h('span', {}, has ? j.testCaseSummary : 'Chưa có — bước test sẽ tự sinh test case và điền kết quả')),
    has ? h('a', { class: 'btn sm primary', href: `/api/jobs/${j.id}/testcases.docx`, download: '' }, '⬇ Tải Word (.docx)') : null,
    has ? h('button', { class: 'btn sm ghost', type: 'button', title: 'Dựng lại file Word từ testcases.json (sau khi sửa tay)', onclick: regen }, 'Xuất lại') : null);
}

/** Bấm một tài liệu trong thanh chọn: ở tab Test mà chọn tài liệu khác test.md/bằng chứng thì chuyển sang tab Tài liệu. */
function pickDoc(id, n) {
  const tab = (n === 'test.md' || n === '@artifacts') && S.drawerTab === 'test' ? 'test' : 'docs';
  if (tab !== S.drawerTab) { S.drawerTab = tab; renderDrawer(); }
  return showDoc(id, n);
}

/** Ảnh chụp, trace… mà bước test lưu trong test-artifacts. */
async function showArtifacts(id, picker) {
  const body = $('.drawer-body');
  body.replaceChildren(picker, h('div', { class: 'loading' }, 'Đang tải…'));
  try {
    const files = await api(`/jobs/${id}/artifacts`);
    if (S.openJob !== id || S.doc !== '@artifacts') return;
    if (!files.length) { body.replaceChildren(picker, h('div', { class: 'empty' }, 'Chưa có bằng chứng test (ảnh chụp màn hình, trace) — bước test chưa chạy hoặc không test giao diện.')); return; }
    const url = n => `/api/jobs/${id}/artifacts/${n.split('/').map(encodeURIComponent).join('/')}`;
    const imgs = files.filter(f => IMG_EXT.test(f.name));
    const others = files.filter(f => !IMG_EXT.test(f.name));
    body.replaceChildren(picker,
      imgs.length ? h('div', { class: 'gallery' }, imgs.map(f => h('a', { href: url(f.name), target: '_blank', rel: 'noopener', title: f.name },
        h('img', { src: url(f.name), alt: f.name, loading: 'lazy' }), h('span', {}, f.name)))) : null,
      others.length ? h('ul', { class: 'filelist' }, others.map(f => h('li', {}, h('a', { href: url(f.name), target: '_blank', rel: 'noopener' }, f.name), ' ', h('span', { class: 'muted' }, fmtSize(f.size))))) : null);
  } catch (e) { body.replaceChildren(picker, h('div', { class: 'errbox' }, e.message)); }
}

function renderLogRow(e) {
  const t = parseDate(e.at);
  // Nhật ký cũ: cảnh báo "connector claude.ai bị tắt" khi chạy bằng API key là vô hại → hiện như dòng thông tin
  const kind = e.kind === 'stderr' && /claude\.ai connectors are disabled/i.test(e.text || '') ? 'info' : e.kind;
  return h('div', { class: 'log-row k-' + kind },
    h('span', { class: 't' }, t ? t.toLocaleTimeString('vi-VN') : ''),
    h('span', { class: 'st', title: e.stage }, e.stage),
    h('span', { class: 'x' }, e.text));
}

function appendLog(entry) {
  if (TL.id === S.openJob) { TL.logs.push(entry); scheduleTimeline(); }
  const box = $('#logBox');
  if (!box || S.drawerTab !== 'log') return;
  const body = $('.drawer-body');
  const nearBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 80;
  box.append(renderLogRow(entry));
  const empty = $('.drawer-body > .empty');
  if (empty) empty.remove();
  if (nearBottom) body.scrollTop = body.scrollHeight;
}

function renderDiff(text) {
  const box = h('div', { class: 'diff' });
  for (const line of text.split('\n')) {
    let cls = '';
    if (line.startsWith('diff --git')) cls = 'file';
    else if (line.startsWith('@@')) cls = 'hunk';
    else if (line.startsWith('+') && !line.startsWith('+++')) cls = 'add';
    else if (line.startsWith('-') && !line.startsWith('---')) cls = 'del';
    box.append(h('div', { class: cls }, line));
  }
  return box;
}

// ---------- Model ----------
const MODELS = [
  ['claude-opus-5-5', 'Opus 5.5 — mạnh nhất, đắt nhất'],
  ['claude-sonnet-5-5', 'Sonnet 5.5 — cân bằng, rẻ hơn Opus nhiều'],
  ['claude-haiku-5-5', 'Haiku 5.5 — nhanh, rẻ nhất, hợp câu hỏi đơn giản'],
  ['claude-fable-5-2', 'Fable 5.2'],
];

/** Điền <select> model: dòng đầu là lựa chọn mặc định; giá trị lạ đã lưu trước đây vẫn giữ được. */
function fillModelSelect(sel, defaultLabel, value) {
  const opts = [['', defaultLabel], ...MODELS];
  if (value && !opts.some(o => o[0] === value)) opts.push([value, value]);
  sel.replaceChildren(...opts.map(([v, label]) => h('option', { value: v }, label)));
  sel.value = value || '';
}

function chatModel() {
  try { return localStorage.getItem('chatModel') || ''; } catch { return ''; }
}

// ---------- Chat theo task ----------
async function showChat() {
  const t = S.chatTask;
  if (!t) return;
  const snap = await api(`/chats/${t.taskId}`);
  if (S.chatTask?.taskId !== t.taskId || S.drawerTab !== 'chat') return;
  S.chats.set(t.taskId, snap);
  const input = h('textarea', {
    id: 'chatInput', class: 'input',
    placeholder: 'Nhập tin nhắn… (Enter để gửi, Shift+Enter xuống dòng)',
    oninput: e => { S.chatDrafts[t.taskId] = e.target.value; },
    onkeydown: e => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); primaryChatAction(); }
    },
  });
  input.value = S.chatDrafts[t.taskId] || '';
  input.addEventListener('paste', e => {
    const files = [...(e.clipboardData?.files || [])];
    if (!files.length) return; // dán chữ: để trình duyệt xử lý bình thường
    e.preventDefault();
    addChatFiles(files.map(pastedName));
  });
  const picker = h('input', { type: 'file', multiple: true, hidden: true, accept: ACCEPT_FILES, onchange: e => { addChatFiles([...e.target.files]); e.target.value = ''; } });
  input.rows = 2;
  input.addEventListener('input', () => autoGrow(input));
  // Model cho chat — nhớ theo trình duyệt; đổi giữa chừng vẫn giữ ngữ cảnh phiên
  const modelSel = h('select', { id: 'chatModel', class: 'chat-model', title: 'Model trả lời tin nhắn chat (đổi model giữa phiên: tin đầu tiên sau khi đổi tốn hơn một chút vì phải nạp lại ngữ cảnh)' });
  fillModelSelect(modelSel, 'Model: theo Cấu hình', chatModel());
  modelSel.addEventListener('change', () => { try { localStorage.setItem('chatModel', modelSel.value); } catch { } });
  const composer = h('div', { class: 'composer', onclick: e => { if (e.target === composer) input.focus(); } },
    h('div', { class: 'chat-attach', id: 'chatAttach' }),
    input,
    h('div', { class: 'composer-bar' },
      h('button', { class: 'tool-btn', type: 'button', title: 'Đính kèm tài liệu hoặc ảnh (Word, Excel, PowerPoint, PDF, ảnh…)', onclick: () => picker.click() },
        clipIcon(), h('span', {}, 'Đính kèm')),
      h('span', { class: 'composer-hint' }, 'Ctrl+V để dán ảnh · kéo thả tệp vào đây'),
      h('span', { class: 'spacer' }),
      modelSel,
      h('div', { class: 'send-btns', id: 'chatSend' })),
    picker);
  const chat = h('div', { class: 'chat' },
    h('div', { class: 'chat-msgs', id: 'chatMsgs' }),
    h('div', { class: 'chat-foot' },
      h('div', { id: 'chatNotice' }),
      composer,
      h('div', { class: 'chat-meta', id: 'chatMeta' })));
  chat.addEventListener('dragover', e => {
    if (!e.dataTransfer.types.includes('Files')) return;
    e.preventDefault();
    chat.classList.add('drop-over');
  });
  chat.addEventListener('dragleave', e => { if (!chat.contains(e.relatedTarget)) chat.classList.remove('drop-over'); });
  chat.addEventListener('drop', e => {
    if (!e.dataTransfer.files.length) return;
    e.preventDefault();
    chat.classList.remove('drop-over');
    addChatFiles([...e.dataTransfer.files]);
  });
  $('.drawer-body').replaceChildren(chat);
  renderChatAttach();
  updateChat(true);
  autoGrow(input);
  input.focus();
}

/** Ô nhập cao theo nội dung, tối đa ~10 dòng rồi cuộn. */
function autoGrow(el) {
  el.style.height = 'auto';
  el.style.height = Math.min(el.scrollHeight, 240) + 'px';
}

function clipIcon() {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', '18');
  svg.setAttribute('height', '18');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', 'M21.44 11.05l-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48');
  path.setAttribute('fill', 'none');
  path.setAttribute('stroke', 'currentColor');
  path.setAttribute('stroke-width', '2');
  path.setAttribute('stroke-linecap', 'round');
  path.setAttribute('stroke-linejoin', 'round');
  svg.append(path);
  return svg;
}

const MAX_CHAT_FILE = 50 * 1024 * 1024;

/** Ảnh dán từ clipboard thường tên "image.png" — đặt tên theo thời điểm để không trùng. */
function pastedName(f) {
  if (f.name && f.name !== 'image.png') return f;
  const d = new Date(), p = n => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
  const ext = (f.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
  return new File([f], `anh-dan-${stamp}-${Math.random().toString(36).slice(2, 5)}.${ext}`, { type: f.type });
}

function addChatFiles(files) {
  const t = S.chatTask;
  if (!t) return;
  const list = S.chatFiles[t.taskId] || (S.chatFiles[t.taskId] = []);
  for (const f of files) {
    if (f.size > MAX_CHAT_FILE) { toast(`${f.name} quá 50 MB`, true); continue; }
    list.push(f);
  }
  renderChatAttach();
  $('#chatInput')?.focus();
}

function renderChatAttach() {
  const box = $('#chatAttach');
  if (!box || !S.chatTask) return;
  const list = S.chatFiles[S.chatTask.taskId] || [];
  box.replaceChildren(...list.map((f, i) => {
    const isImg = f.type.startsWith('image/');
    const url = isImg ? URL.createObjectURL(f) : null;
    return h('div', { class: 'att-chip', title: f.name },
      isImg ? h('img', { src: url, alt: '', onload: () => URL.revokeObjectURL(url) }) : h('span', { class: 'att-icon' }, fileIcon(f.name)),
      h('span', { class: 'att-name' }, f.name),
      h('span', { class: 'muted' }, fmtSize(f.size)),
      h('button', { class: 'att-x', type: 'button', title: 'Bỏ tệp này', onclick: () => { list.splice(i, 1); renderChatAttach(); } }, '×'));
  }));
  box.hidden = list.length === 0;
}

function fileIcon(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  return { docx: 'W', doc: 'W', xlsx: 'X', xlsm: 'X', xls: 'X', pptx: 'P', pdf: 'PDF' }[ext] || '📄';
}
function fmtSize(n) { return n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB'; }
const IMG_EXT = /\.(png|jpe?g|gif|webp|bmp)$/i;

function chatJob() {
  if (!S.chatTask) return null;
  const j = S.openJob ? S.jobs.get(S.openJob) : null;
  return j && j.taskId === S.chatTask.taskId ? j : activeJobForTask(S.chatTask.taskId);
}

function updateChat(forceBottom = false) {
  const box = $('#chatMsgs');
  if (!box || !S.chatTask || S.drawerTab !== 'chat') return;
  const snap = S.chats.get(S.chatTask.taskId) || { thread: { messages: [] }, busy: false };
  const job = chatJob();
  const waiting = job && job.status === 'WaitingForUser';
  const analyzing = job && (job.status === 'NeedsInfo' || job.status === 'Clarified');
  const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
  const msgs = snap.thread.messages || [];
  box.replaceChildren(...(msgs.length ? msgs.map(renderMsg) : [h('div', { class: 'empty' },
    'Hỏi AI về task này: yêu cầu nghĩa là gì, nên sửa ở đâu, kết quả review/test ra sao… ',
    'Những gì hai bên chốt ở đây sẽ được đưa vào yêu cầu khi giao cho AI code.')]));
  if (forceBottom || nearBottom) box.scrollTop = box.scrollHeight;

  $('#chatNotice').replaceChildren(waiting
    ? h('div', { class: 'notice' }, 'AI đang dừng chờ bạn xác nhận. Trả lời các câu hỏi phía trên (ví dụ "1: A, 2: đồng ý") rồi bấm "Gửi & tiếp tục". Muốn hỏi lại cho rõ trước thì bấm "Chỉ hỏi".')
    : job && job.status === 'NeedsInfo'
      ? h('div', { class: 'notice' }, 'Yêu cầu còn thiếu. Trả lời các mục AI hỏi phía trên rồi bấm "Gửi & phân tích lại" — AI đọc lại task kèm câu trả lời của bạn.')
      : '');
  $('#chatMeta').replaceChildren(
    snap.busy ? h('span', { class: 'busy' }, h('span', { class: 'running-dot' }), 'AI đang trả lời…') : null,
    snap.busy ? h('button', { class: 'link-btn danger', type: 'button', onclick: cancelChat }, 'Dừng') : null,
    h('span', { class: 'spacer' }),
    snap.thread.costUsd ? h('span', {}, `Chi phí chat $${snap.thread.costUsd.toFixed(2)}`) : null,
    h('button', { class: 'link-btn', type: 'button', disabled: snap.busy, title: 'AI quên phần trao đổi cũ, đọc lại task từ AMIS (lịch sử vẫn hiển thị)', onclick: resetChat }, 'Phiên chat mới'));
  const btns = [];
  if (job && job.status === 'Clarified') {
    // Yêu cầu đã rõ: trao đổi thêm là chính, phân tích lại (tốn phí) là lựa chọn phụ
    btns.push(h('button', { class: 'btn', type: 'button', onclick: () => sendChat(true) }, 'Gửi & phân tích lại'));
    btns.push(h('button', { class: 'btn primary', type: 'button', disabled: snap.busy, onclick: () => sendChat(false) }, 'Gửi'));
  } else if (waiting || analyzing) {
    btns.push(h('button', { class: 'btn', type: 'button', disabled: snap.busy, onclick: () => sendChat(false) }, 'Chỉ hỏi'));
    btns.push(h('button', { class: 'btn primary', type: 'button', onclick: () => sendChat(true) }, waiting ? 'Gửi & tiếp tục' : 'Gửi & phân tích lại'));
  } else {
    btns.push(h('button', { class: 'btn primary', type: 'button', disabled: snap.busy, onclick: () => sendChat(false) }, 'Gửi'));
  }
  $('#chatSend').replaceChildren(...btns);
}

function renderMsg(m) {
  const who = m.role === 'user' ? (m.jobId ? 'Bạn — trả lời cho job' : 'Bạn')
    : m.role === 'pipeline' ? 'AI pipeline' : 'AI';
  const cls = 'msg ' + m.role + (m.error ? ' error' : '');
  const bubble = h('div', { class: 'bubble' });
  if (m.pending && !m.text) bubble.append(h('span', { class: 'typing' }, 'Đang đọc task và suy nghĩ…'));
  else if (m.role === 'user') { if (m.text) bubble.append(...renderRichText(m.text)); }
  else { bubble.classList.add('rich'); bubble.append(renderMarkdown(m.text, 'md')); }
  if (m.pending && m.text) bubble.append(h('div', { class: 'typing' }, '…'));
  if ((m.files || []).length && S.chatTask) {
    const base = `/api/chats/${S.chatTask.taskId}/files/`;
    bubble.append(h('div', { class: 'msg-files' }, m.files.map(n => IMG_EXT.test(n)
      ? h('a', { href: base + encodeURIComponent(n), target: '_blank', rel: 'noopener', title: n }, h('img', { src: base + encodeURIComponent(n), alt: n, loading: 'lazy' }))
      : h('a', { class: 'att-chip', href: base + encodeURIComponent(n), target: '_blank', rel: 'noopener', title: n }, h('span', { class: 'att-icon' }, fileIcon(n)), h('span', { class: 'att-name' }, n)))));
  }
  const tools = (m.tools || []).length
    ? h('details', { class: 'tools' }, h('summary', {}, `Đã dùng ${m.tools.length} công cụ` + (m.pending ? ` — mới nhất: ${m.tools[m.tools.length - 1].slice(0, 80)}` : '')),
      m.tools.map(t => h('div', { title: t }, t)))
    : null;
  return h('div', { class: cls }, h('div', { class: 'who' }, `${who} · ${fmtDate(m.at)}`), bubble, tools);
}

/**
 * Markdown → DOM (không dùng innerHTML nên nội dung do AI viết không chèn được HTML):
 * tiêu đề, đoạn, danh sách lồng nhau, bảng, khối code, trích dẫn, đường kẻ; inline: `code`, **đậm**, *nghiêng*, [link](http…).
 */
function renderMarkdown(text, cls = 'md') {
  const root = h('div', { class: cls });
  const lines = (text || '').replace(/\r\n?/g, '\n').split('\n');
  let para = null;          // đoạn văn đang gom
  let lists = [];           // ngăn xếp { indent, el } của danh sách đang mở
  const endPara = () => { para = null; };
  const endLists = () => { lists = []; };
  const isTableSep = l => /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(l);
  const cells = l => l.trim().replace(/^\|/, '').replace(/\|$/, '').split(/(?<!\\)\|/).map(c => c.trim().replace(/\\\|/g, '|'));

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    let m;
    if ((m = line.match(/^\s*(```|~~~)/))) {
      const fence = m[1], buf = [];
      while (++i < lines.length && !lines[i].trim().startsWith(fence)) buf.push(lines[i]);
      endPara(); endLists();
      root.append(h('pre', {}, buf.join('\n')));
      continue;
    }
    if (!line.trim()) { endPara(); continue; }
    if ((m = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/))) {
      endPara(); endLists();
      root.append(h('h' + Math.min(m[1].length + 1, 6), {}, ...inlineMd(m[2])));
      continue;
    }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { endPara(); endLists(); root.append(h('hr')); continue; }
    if (line.trim().startsWith('|') && i + 1 < lines.length && isTableSep(lines[i + 1])) {
      endPara(); endLists();
      const head = cells(line);
      const rows = [];
      i++;
      while (i + 1 < lines.length && lines[i + 1].trim().startsWith('|')) rows.push(cells(lines[++i]));
      root.append(h('div', { class: 'md-table' }, h('table', {},
        h('thead', {}, h('tr', {}, head.map(c => h('th', {}, ...inlineMd(c))))),
        h('tbody', {}, rows.map(r => h('tr', {}, r.map(c => h('td', {}, ...inlineMd(c)))))))));
      continue;
    }
    if ((m = line.match(/^(\s*)([-*+•]|\d+[.)])\s+(.*)$/))) {
      endPara();
      const indent = m[1].replace(/\t/g, '    ').length;
      const ordered = /\d/.test(m[2]);
      while (lists.length && lists[lists.length - 1].indent > indent) lists.pop();
      let top = lists[lists.length - 1];
      if (!top || top.indent < indent || top.ordered !== ordered) {
        if (top && top.indent === indent) lists.pop();
        const el = h(ordered ? 'ol' : 'ul', ordered && parseInt(m[2], 10) > 1 ? { start: parseInt(m[2], 10) } : {});
        const parent = lists[lists.length - 1];
        const host = parent && parent.indent < indent ? parent.el.lastElementChild : null;
        (host || root).append(el);
        top = { indent, ordered, el };
        lists.push(top);
      }
      top.el.append(h('li', {}, ...inlineMd(m[3])));
      continue;
    }
    if ((m = line.match(/^\s*>\s?(.*)$/))) {
      endPara(); endLists();
      const last = root.lastElementChild;
      if (last && last.tagName === 'BLOCKQUOTE') last.append(h('br'), ...inlineMd(m[1]));
      else root.append(h('blockquote', {}, ...inlineMd(m[1])));
      continue;
    }
    // Dòng thụt vào ngay sau một mục danh sách là phần tiếp của mục đó
    if (lists.length && /^\s{2,}/.test(line) && !para) {
      const li = lists[lists.length - 1].el.lastElementChild;
      if (li) { li.append(h('br'), ...inlineMd(line.trim())); continue; }
    }
    endLists();
    if (para) para.append(h('br'), ...inlineMd(line.trim()));
    else { para = h('p', {}, ...inlineMd(line.trim())); root.append(para); }
  }
  return root;
}

function inlineMd(text) {
  const out = [];
  for (const seg of String(text).split(/(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\([^)\s]+\)|(?<![\w*])\*[^*\s][^*]*\*(?![\w*]))/g)) {
    if (!seg) continue;
    let m;
    if (seg.length > 2 && seg.startsWith('`') && seg.endsWith('`')) out.push(h('code', {}, seg.slice(1, -1)));
    else if (seg.length > 4 && seg.startsWith('**') && seg.endsWith('**')) out.push(h('b', {}, ...inlineMd(seg.slice(2, -2))));
    else if ((m = seg.match(/^\[([^\]]+)\]\(([^)\s]+)\)$/))) {
      out.push(/^https?:\/\//i.test(m[2]) ? h('a', { href: m[2], target: '_blank', rel: 'noopener noreferrer' }, m[1]) : h('span', {}, m[1]));
    } else if (seg.length > 2 && seg.startsWith('*') && seg.endsWith('*')) out.push(h('i', {}, seg.slice(1, -1)));
    else out.push(document.createTextNode(seg));
  }
  return out;
}

/** Markdown tối giản và an toàn: khối ```code```, `code`, **đậm** — mọi thứ khác là text. */
function renderRichText(text) {
  const out = [];
  const parts = (text || '').split(/```[\w-]*\n?([\s\S]*?)```/g);
  parts.forEach((p, i) => {
    if (i % 2 === 1) { out.push(h('pre', {}, p.replace(/\n$/, ''))); return; }
    for (const seg of p.split(/(`[^`\n]+`|\*\*[^*\n]+\*\*)/g)) {
      if (!seg) continue;
      if (seg.startsWith('`') && seg.endsWith('`') && seg.length > 2) out.push(h('code', {}, seg.slice(1, -1)));
      else if (seg.startsWith('**') && seg.endsWith('**') && seg.length > 4) out.push(h('b', {}, seg.slice(2, -2)));
      else out.push(document.createTextNode(seg));
    }
  });
  return out;
}

function primaryChatAction() {
  const job = chatJob();
  // Enter = nút chính: đang chờ trả lời thì gửi kèm chạy tiếp/phân tích lại, còn lại chỉ hỏi chat
  sendChat(!!(job && (job.status === 'WaitingForUser' || job.status === 'NeedsInfo')));
}

async function sendChat(asAnswer) {
  const t = S.chatTask;
  const input = $('#chatInput');
  const text = input.value.trim();
  const files = S.chatFiles[t?.taskId] || [];
  if (!t || (!text && !files.length)) return;
  const snap = S.chats.get(t.taskId);
  if (!asAnswer && snap && snap.busy) { toast('AI đang trả lời tin trước, đợi một chút.', true); return; }
  const fd = new FormData();
  fd.append('text', text);
  fd.append('taskName', t.taskName || '');
  for (const f of files) fd.append('files', f, f.name);
  if (!asAnswer) fd.append('model', $('#chatModel')?.value || '');
  try {
    if (asAnswer) {
      const job = chatJob();
      await api(`/jobs/${job.id}/answer`, { method: 'POST', body: fd });
      toast(job.status === 'WaitingForUser' ? 'Đã gửi câu trả lời — AI làm tiếp' : 'Đã gửi — AI phân tích lại yêu cầu');
    } else {
      await api(`/chats/${t.taskId}/messages`, { method: 'POST', body: fd });
    }
    input.value = '';
    autoGrow(input);
    S.chatDrafts[t.taskId] = '';
    S.chatFiles[t.taskId] = [];
    renderChatAttach();
  } catch (e) { toast(e.message, true); }
}

async function proceed(id, mode) {
  const j = S.jobs.get(id);
  if (mode === 'code' && j && S.repo && S.repo.info) {
    const cur = S.repo.info.branch;
    const warns = [];
    if (branchCheck(j.projectName).ok === false)
      warns.push(`Task thuộc dự án ${projectKey(j.projectName)} nhưng repo đang ở nhánh "${cur}".`);
    if (j.analyzedBranch && j.analyzedBranch !== cur)
      warns.push(`AI đã phân tích trên nhánh "${j.analyzedBranch}", giờ repo đang ở "${cur}" — nơi cần sửa có thể khác.`);
    if (warns.length && !await ask(warns.join('\n\n') + `\n\nAI sẽ tạo nhánh mới từ "${cur}". Vẫn bắt đầu code?`)) return;
  }
  try {
    await api(`/jobs/${id}/proceed`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode }) });
    toast(mode === 'code' ? 'Đã xếp hàng cho AI code' : mode === 'review' ? 'Đã xếp hàng chạy tiếp review + test'
      : mode === 'recode' ? 'Đã xếp hàng chạy lại bước code' : 'Đã xếp hàng phân tích lại');
  } catch (e) { toast(e.message, true); }
}

/** Người dùng muốn sửa/thêm logic sau khi job xong: AI sửa trên nhánh AI (đọc cả Chat) rồi chạy lại review → test. */
function openRevise(id) {
  let dlg = $('#dlgRevise');
  if (!dlg) { dlg = h('dialog', { id: 'dlgRevise', class: 'dlg wide' }); document.body.append(dlg); }
  const text = h('textarea', { class: 'input', rows: '8', style: 'width:100%;resize:vertical',
    placeholder: 'Ví dụ: Đặt timelog vào hàm FillterLimitOvertimePlan theo phương án đã chốt trong chat, chỉ ghi log khi danh sách từ 1.000 dòng.\n\nĐể trống = làm theo những gì đã chốt ở cuối khung Chat.' });
  const ok = h('button', { class: 'btn primary', type: 'button' }, 'Gửi cho AI sửa');
  ok.addEventListener('click', async () => {
    ok.disabled = true;
    try {
      await api(`/jobs/${id}/proceed`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: 'revise', text: text.value }) });
      dlg.close();
      toast('Đã xếp hàng: AI sửa code → review → test');
    } catch (e) { toast(e.message, true); ok.disabled = false; }
  });
  dlg.replaceChildren(h('div', { class: 'dlg-inner' },
    h('div', { class: 'dlg-head' }, h('b', {}, 'Yêu cầu AI sửa code'), h('button', { class: 'btn ghost icon', type: 'button', onclick: () => dlg.close() }, '✕')),
    h('div', { class: 'dlg-body' },
      h('div', { class: 'small muted' }, 'AI đọc yêu cầu này + toàn bộ khung Chat + code hiện có trên nhánh AI, sửa tiếp (không code lại từ đầu), chạy ship-check, rồi tool chạy lại review và test. Chưa commit — xong vẫn chờ bạn bấm "Tạo commit".'),
      text),
    h('div', { class: 'dlg-foot' }, h('span', { class: 'spacer' }),
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Huỷ'), ok)));
  dlg.showModal();
  text.focus();
}

async function uploadJobFiles(id, input) {
  if (!input.files.length) return;
  const fd = new FormData();
  for (const f of input.files) fd.append('files', f, f.name);
  try {
    const job = await api(`/jobs/${id}/files`, { method: 'POST', body: fd });
    S.jobs.set(job.id, job);
    renderDrawer();
    if (await ask(`Đã thêm ${input.files.length} tệp. Phân tích lại ngay để AI đọc tài liệu mới?`)) await proceed(id, 'reanalyze');
  } catch (e) { toast(e.message, true); }
  finally { input.value = ''; }
}

async function cancelChat() {
  try { await api(`/chats/${S.chatTask.taskId}/cancel`, { method: 'POST' }); } catch (e) { toast(e.message, true); }
}

async function resetChat() {
  if (!await ask('Bắt đầu phiên chat mới? AI sẽ đọc lại task và không nhớ phần trao đổi trước (lịch sử vẫn hiển thị).')) return;
  try { await api(`/chats/${S.chatTask.taskId}/reset`, { method: 'POST' }); } catch (e) { toast(e.message, true); }
}

async function cancelJob(id) {
  const held = S.config && S.config.confirmCommit;
  if (!await ask(held
    ? 'Huỷ job này?\nThay đổi dở dang được GIỮ chưa commit trên nhánh AI — sau đó bạn chọn commit hoặc chạy tiếp.'
    : 'Huỷ job này?\nThay đổi dở dang sẽ được commit WIP vào nhánh AI rồi trả repo về nhánh gốc.', { ok: 'Huỷ job', cancel: 'Không' })) return;
  try { await api(`/jobs/${id}/cancel`, { method: 'POST' }); toast('Đã gửi yêu cầu huỷ'); }
  catch (e) { toast(e.message, true); }
}

/** Hộp xác nhận commit: danh sách file, message chuẩn "[ID TASK] - Mô tả" sửa được, tuỳ chọn push. mode: approve | reject | null */
async function openCommit(id, mode) {
  let info;
  try { info = await api(`/jobs/${id}/commit`); } catch (e) { toast(e.message, true); return; }
  let dlg = $('#dlgCommit');
  if (!dlg) { dlg = h('dialog', { id: 'dlgCommit', class: 'dlg wide' }); document.body.append(dlg); }
  const msg = h('textarea', { class: 'input mono', rows: '12', spellcheck: 'false', style: 'width:100%;resize:vertical' });
  msg.value = info.message;
  const push = h('input', { type: 'checkbox' });
  push.checked = mode === 'approve';
  const picks = [], skips = [];
  const repoBlocks = info.repos.map(r => {
    const mod = r.modified.map(m => {
      const cb = h('input', { type: 'checkbox' });
      cb.checked = m.selected;
      skips.push({ rel: r.rel, path: m.path, cb });
      return h('li', {}, h('label', { class: 'check inline mono small' }, cb, ' ', m.path,
        m.selected ? null : h('span', { class: 'muted' }, ' (file sinh khi chạy app — mặc định không commit)')));
    });
    const unt = r.untracked.map(u => {
      const cb = h('input', { type: 'checkbox' });
      cb.checked = u.selected;
      picks.push({ rel: r.rel, path: u.path, cb });
      return h('li', {}, h('label', { class: 'check inline mono small' }, cb, ' ', u.path,
        u.knownBefore ? h('span', { class: 'muted' }, ' (có từ trước khi AI chạy)') : null));
    });
    return h('div', { class: 'commit-repo' },
      h('div', {}, h('b', {}, r.rel), ' — nhánh ', h('code', {}, r.branch),
        r.onAiBranch ? null : h('span', { class: 'bad-text' }, ` ⚠ không phải ${info.branch}`)),
      h('div', { class: 'small muted' }, mod.length
        ? `File đã sửa (${mod.length}) — bỏ tick file không muốn commit (vẫn giữ trên máy); file cấu hình local đã loại:`
        : 'Không có file đã sửa.'),
      mod.length ? h('ul', { class: 'commit-files' }, mod) : null,
      unt.length ? h('div', { class: 'small muted' }, r.untrackedKnown
        ? 'File mới chưa track — tick file muốn đưa vào commit (đã tick sẵn file AI tạo):'
        : 'File mới chưa track — tool không biết file nào có từ trước, hãy tick đúng file AI tạo:') : null,
      unt.length ? h('ul', { class: 'commit-files' }, unt) : null);
  });
  const title = mode === 'approve' ? 'Tạo commit & push' : mode === 'reject' ? 'Từ chối — commit tạm, không push' : 'Commit & trả nhánh';
  const ok = h('button', { class: mode === 'reject' ? 'btn danger' : 'btn ok', type: 'button' }, 'Commit tạm & trả nhánh');
  const syncOk = () => { if (mode === 'approve') ok.textContent = push.checked ? 'Commit & push' : 'Chỉ commit'; };
  push.addEventListener('change', syncOk);
  syncOk();
  ok.addEventListener('click', async () => {
    const newFiles = {};
    for (const p of picks) if (p.cb.checked) (newFiles[p.rel] = newFiles[p.rel] || []).push(p.path);
    const skip = {};
    for (const p of skips) if (!p.cb.checked) (skip[p.rel] = skip[p.rel] || []).push(p.path);
    if (!picks.some(p => p.cb.checked) && !skips.some(p => p.cb.checked)) { toast('Chưa chọn file nào để commit.', true); return; }
    ok.disabled = true;
    try {
      const job = await api(`/jobs/${id}/commit`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ message: msg.value, push: mode === 'approve' && push.checked, newFiles, skip, decision: mode }),
      });
      S.jobs.set(job.id, job);
      dlg.close();
      renderDrawer(); render(); loadRepoSoon();
      toast(mode === 'approve' ? (push.checked ? 'Đã commit và push — xem Nhật ký' : 'Đã commit') : 'Đã commit tạm và trả nhánh');
      if (mode === 'approve' && Object.keys(job.prUrls || {}).length) openAmisUpdate(job.id);
    } catch (e) { toast(e.message, true); ok.disabled = false; }
  });
  dlg.replaceChildren(h('div', { class: 'dlg-inner' },
    h('div', { class: 'dlg-head' }, h('b', {}, title), h('button', { class: 'btn ghost icon', type: 'button', onclick: () => dlg.close() }, '✕')),
    h('div', { class: 'dlg-body' },
      h('div', { class: 'small' }, 'Nhánh ', h('code', {}, info.branch), ' → sau khi commit, repo trả về ', h('code', {}, info.returnBranch || '?')),
      ...repoBlocks,
      h('div', { class: 'field' }, h('span', {}, 'Commit message ', h('small', { class: 'muted' }, '— dòng đầu "[ID TASK] - Mô tả" (N9/N10), thân "Mục đích:/Thay đổi:" làm mô tả PR')), msg),
      mode === 'approve' ? h('label', { class: 'check' }, push, h('span', {}, 'Push nhánh lên origin ', h('code', {}, info.branch))) : null),
    h('div', { class: 'dlg-foot' }, h('span', { class: 'spacer' }),
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Để sau'), ok)));
  dlg.showModal();
}

/** Kết quả kiểm nhánh cha (cache 20 giây); lần đầu trả null rồi vẽ lại khi có kết quả. */
function chainDrift(j) {
  const key = `${j.baseSha}|${j.headSha}`;
  const c = S.drift.get(j.id);
  if (!c || c.key !== key || Date.now() - c.at > 20000) {
    if (!c || !c.loading) {
      S.drift.set(j.id, { ...(c || {}), loading: true, key: c ? c.key : key, at: c ? c.at : 0 });
      api(`/jobs/${j.id}/chain`).then(r => {
        const before = JSON.stringify(c && c.list);
        S.drift.set(j.id, { at: Date.now(), key, list: r.drift });
        if (JSON.stringify(r.drift) !== before && S.openJob === j.id) renderDrawer();
      }).catch(() => S.drift.set(j.id, { at: Date.now(), key, list: c ? c.list : [] }));
    }
  }
  return c ? c.list : null;
}

async function restack(j) {
  if (!await ask(`Dựng lại nhánh ${j.branch} lên bản mới nhất của ${j.chainedFromBranch}?\nCommit của task này được tạo lại (mã commit đổi); working copy không bị đụng tới.`)) return;
  try {
    const nj = await api(`/jobs/${j.id}/restack`, { method: 'POST' });
    S.jobs.set(nj.id, nj);
    S.drift.delete(nj.id);
    toast('Đã cập nhật nhánh theo nhánh cha');
    render();
    if (S.openJob === nj.id) { renderDrawer(); await showDrawerTab(); }
  } catch (e) { toast(e.message, true); }
}

// Người dùng tự làm lại: code AI commit tạm vào nhánh AI (không push), repo về nhánh cũ, job rời cột "Chờ bạn"
function selfBtn(j) {
  return h('button', { class: 'btn', type: 'button', title: 'Kết quả AI chưa vừa ý, bạn tự làm — nhả repo cho task khác', onclick: () => selfDo(j) }, '✋ Tôi tự làm');
}
async function selfDo(j) {
  const back = j.returnBranch ? ` về nhánh ${j.returnBranch}` : ' về nhánh ban đầu';
  const what = j.held
    ? `Toàn bộ thay đổi đang có trên nhánh ${j.branch} (kể cả phần bạn sửa thêm ở đó) được commit tạm vào nhánh đó — KHÔNG push — rồi repo chuyển${back}.`
    : `Nhánh ${j.branch || 'AI'} được giữ nguyên, không push.`;
  const reason = await askText(`Bạn tự làm task #${j.taskId}?
${what}
Job chuyển sang "Từ chối".
AI chưa vừa ý ở đâu? (tuỳ chọn — để AI rút kinh nghiệm)`, '',
    { ok: 'Tôi tự làm', placeholder: 'Ví dụ: sai cách lấy dữ liệu, làm thừa màn…' });
  if (reason === null) return;
  try {
    const job = await api(`/jobs/${j.id}/self`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ reason }),
    });
    S.jobs.set(job.id, job);
    renderDrawer();
    render();
    toast(`#${job.taskId}: đã nhả repo — bạn tự làm tiếp.`);
  } catch (e) { toast(e.message, true); }
}

async function decide(id, approve, push = false) {
  if (push && !await ask('Duyệt và push nhánh AI lên origin?')) return;
  let reason = null;
  if (!approve) {
    reason = await askText('Từ chối kết quả của AI?\nLý do (tuỳ chọn) — được ghi lại để AI rút kinh nghiệm cho lần sau.', '', { ok: 'Từ chối', danger: true, placeholder: 'Ví dụ: sai nghiệp vụ, thiếu xử lý…' });
    if (reason === null) return;
  }
  try {
    const job = await api(`/jobs/${id}/decision`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ approve, reason, push }),
    });
    S.jobs.set(job.id, job);
    renderDrawer();
    render();
    if (approve && push && Object.keys(job.prUrls || {}).length) openAmisUpdate(job.id);
  } catch (e) { toast(e.message, true); }
}

// ---------- Kiểm trên web ----------
async function inspect(j, start) {
  if (start && !await ask(`Chuyển repo sang nhánh ${j.branch} và build để kiểm task #${j.taskId} trên web?\nRepo phải sạch (không có file sửa dở). Hàng đợi AI tạm dừng tới khi bạn bấm "Xong kiểm".`)) return;
  try {
    const nj = await api(`/jobs/${j.id}/inspect${start ? '' : '/done'}`, { method: 'POST' });
    S.jobs.set(nj.id, nj);
    toast(start ? 'Đã chuyển nhánh — đang build, xem Nhật ký' : 'Đã trả repo về nhánh cũ');
    render();
    if (S.openJob === nj.id) { renderDrawer(); await showDrawerTab(); }
    if (start && S.openJob !== nj.id) openDrawer(nj.id);
    loadRepoSoon();
  } catch (e) { toast(e.message, true); }
}

// ---------- Bản tin sáng ----------
function briefPending() {
  return [...S.jobs.values()].filter(j => j.batch && j.status === 'AwaitingApproval');
}

function renderBriefBadge() {
  const b = $('#briefBadge');
  if (!b) return;
  const n = briefPending().length;
  b.hidden = !n;
  b.textContent = n;
}

/** Lần đầu mở trang: có task hàng loạt xong sau lần xem bản tin trước → mở bản tin. */
function maybeAutoBrief() {
  let seen = 0;
  try { seen = Number(localStorage.getItem('briefSeen') || 0); } catch { }
  const fresh = briefPending().filter(j => new Date(j.finishedAt || 0).getTime() > seen);
  if (fresh.length) openBrief();
}

async function openBrief() {
  let dlg = $('#dlgBrief');
  if (!dlg) { dlg = h('dialog', { id: 'dlgBrief', class: 'dlg wide costs-dlg' }); document.body.append(dlg); }
  let r;
  try { r = await api('/brief?days=3'); } catch (e) { toast(e.message, true); return; }
  try { localStorage.setItem('briefSeen', String(Date.now())); } catch { }
  const verdict = (label, v) => v ? h('span', { class: 'chip ' + (v === 'PASS' ? 'pass' : v === 'FAIL' ? 'fail' : '') }, `${label} ${v}`) : null;
  const sections = t => (t || '').split('\n').filter(l => /^##\s/.test(l)).length;
  const row = j => {
    const live = S.jobs.get(j.id) || {};
    const asm = sections(j.assumptions);
    return h('tr', {},
      h('td', {},
        h('div', {}, h('b', {}, '#' + j.taskId), ' ', h('span', {}, j.parentTaskName || j.taskName)),
        j.parentTaskName ? h('div', { class: 'small muted' }, j.taskName) : null,
        j.error && j.status === 'Failed' ? h('div', { class: 'small bad-text' }, j.error.slice(0, 220)) : null,
        j.assumptions ? h('details', { class: 'small' }, h('summary', { class: 'warn-text' }, `⚠ ${asm || 1} điểm AI tự quyết — cần bạn xác nhận`),
          h('pre', { class: 'brief-asm' }, j.assumptions.slice(0, 4000))) : null,
        j.summary ? h('details', { class: 'small' }, h('summary', {}, 'Tóm tắt thay đổi'), h('pre', { class: 'brief-asm' }, j.summary.slice(0, 3000))) : null),
      h('td', {},
        h('span', { class: 'badge s-' + j.status }, STATUS_LABEL[j.status] || j.status), h('br'),
        verdict('Review', j.reviewVerdict), ' ', verdict('Test', j.testVerdict),
        j.fixRounds ? h('div', { class: 'small muted' }, `${j.fixRounds} vòng sửa theo review`) : null,
        j.autoRetries ? h('div', { class: 'small muted' }, '↻ đã tự thử lại') : null,
        j.chainedFromTask ? h('div', { class: 'small muted' }, `↳ nối #${j.chainedFromTask}`) : null,
        Object.keys(j.overlaps || {}).length ? h('div', { class: 'small warn-text' }, `⚠ trùng file với ${Object.keys(j.overlaps).map(k => '#' + k).join(', ')}`) : null),
      h('td', { class: 'num' }, usd(j.costUsd), h('div', { class: 'small muted' }, `${j.files} file`)),
      h('td', { class: 'kit-act' },
        h('button', { class: 'btn sm', type: 'button', onclick: () => { dlg.close(); openDrawer(j.id); } }, 'Mở'),
        live.headSha && !live.held && !live.inspecting && ['AwaitingApproval', 'Approved', 'Failed'].includes(j.status)
          ? h('button', { class: 'btn sm', type: 'button', onclick: async () => { dlg.close(); await inspect(live, true); } }, '🔍 Kiểm trên web') : null,
        live.inspecting ? h('button', { class: 'btn sm', type: 'button', onclick: async () => { await inspect(live, false); openBrief(); } }, 'Xong kiểm') : null,
        j.status === 'AwaitingApproval' && !live.held && !live.inspecting
          ? h('button', { class: 'btn sm ok', type: 'button', onclick: async () => { dlg.close(); await decide(j.id, true, true); } }, 'Duyệt & push') : null));
  };
  const all = r.batches.flatMap(b => b.jobs);
  const body = r.batches.length ? r.batches.slice(0, 5).map(b => h('section', { class: 'cost-sec' },
    h('h4', {}, `Lô ${b.batchId.replace(/^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2}).*/, '$4:$5 $3/$2')} `,
      h('small', { class: 'muted' }, `— ${b.jobs.length} task${b.unattended ? ' · 🌙 qua đêm' : ''}${b.running ? ` · còn ${b.running} đang chạy/chờ` : ' · đã chạy xong'}`)),
    h('table', { class: 'cost-table brief-table' }, h('tbody', {}, b.jobs.map(row)))))
    : [h('div', { class: 'empty' }, 'Chưa có lô chạy hàng loạt nào trong 3 ngày qua. Tick task ở cột AMIS rồi bấm "Chạy hàng loạt…".')];
  const count = (f) => all.filter(f).length;
  dlg.replaceChildren(h('div', { class: 'dlg-inner' },
    h('div', { class: 'dlg-head' }, h('b', {}, '☀ Bản tin chạy hàng loạt'), h('button', { class: 'btn ghost icon', type: 'button', onclick: () => dlg.close() }, '✕')),
    h('div', { class: 'dlg-body' },
      all.length ? h('div', { class: 'cost-tiles' },
        h('div', { class: 'cost-tile' }, h('small', {}, 'Chờ bạn duyệt'), h('b', {}, String(count(j => j.status === 'AwaitingApproval')))),
        h('div', { class: 'cost-tile' }, h('small', {}, 'Có giả định cần xác nhận'), h('b', {}, String(count(j => j.assumptions && j.status === 'AwaitingApproval')))),
        h('div', { class: 'cost-tile' }, h('small', {}, 'Lỗi / cần bạn'), h('b', {}, String(count(j => ['Failed', 'NeedsInfo', 'WaitingForUser'].includes(j.status))))),
        h('div', { class: 'cost-tile' }, h('small', {}, 'Chi phí'), h('b', {}, usd(all.reduce((s, j) => s + (j.costUsd || 0), 0))))) : null,
      r.keepAwake ? h('div', { class: 'small muted' }, '🌙 Đang giữ máy không tự ngủ vì còn job chạy.') : null,
      h('div', { class: 'small muted' }, 'Thứ tự review gợi ý: đọc "điểm AI tự quyết" → Mở xem Diff/Test → (Kiểm trên web nếu cần) → Duyệt & push, hoặc Yêu cầu AI sửa trong job.'),
      ...body)));
  if (!dlg.open) dlg.showModal();
}

// ---------- Gửi kết quả lên AMIS ----------
/** Bình luận soạn sẵn (xem/sửa được) + tuỳ chọn chuyển task con sang Hoàn thành. Không gửi gì nếu người dùng không bấm Gửi. */
async function openAmisUpdate(id) {
  let d;
  try { d = await api(`/jobs/${id}/amis`); } catch (e) { toast(e.message, true); return; }
  const j = S.jobs.get(id);
  let dlg = $('#dlgAmis');
  if (!dlg) { dlg = h('dialog', { id: 'dlgAmis', class: 'dlg wide' }); document.body.append(dlg); }
  const fmt = t => new Date(t).toLocaleString('vi-VN', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  const text = h('textarea', { class: 'input', rows: '12', style: 'width:100%' }, d.comment);
  const sendComment = h('input', { type: 'checkbox', checked: !d.commentedAt });
  const markDone = h('input', { type: 'checkbox', checked: !d.doneAt && !!j && Object.keys(j.prUrls || {}).length > 0 });
  const target = h('select', { class: 'input sm', style: 'width:auto' }, d.targets.map(t =>
    h('option', { value: String(t.id), selected: t.id === d.defaultTarget }, `Task ${t.kind} #${t.id}${t.name ? ' — ' + t.name.slice(0, 70) : ''}`)));
  const ok = h('button', { class: 'btn primary', type: 'button' }, 'Gửi lên AMIS');
  ok.addEventListener('click', async () => {
    if (!sendComment.checked && !markDone.checked) { toast('Chưa chọn việc nào để gửi', true); return; }
    ok.disabled = true;
    try {
      const r = await api(`/jobs/${id}/amis`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ comment: sendComment.checked ? text.value : '', targetTaskId: Number(target.value), markDone: markDone.checked }),
      });
      S.jobs.set(r.job.id, r.job);
      dlg.close();
      toast('Đã ' + r.done.join(', '));
      render();
      if (S.openJob === id) renderDrawer();
    } catch (e) { toast(e.message, true); ok.disabled = false; }
  });
  dlg.replaceChildren(h('div', { class: 'dlg-inner' },
    h('div', { class: 'dlg-head' }, h('b', {}, `Cập nhật AMIS — task #${j ? j.taskId : ''}`), h('button', { class: 'btn ghost icon', type: 'button', onclick: () => dlg.close() }, '✕')),
    h('div', { class: 'dlg-body' },
      d.commentedAt ? h('div', { class: 'notice' }, `Đã gửi bình luận lên task #${d.commentTask} lúc ${fmt(d.commentedAt)} — gửi nữa sẽ thành bình luận mới.`) : null,
      h('label', { class: 'check' }, sendComment, h('span', {}, 'Gửi bình luận lên ', target)),
      text,
      h('div', { class: 'small muted' }, 'Gửi dưới tên tài khoản AMIS đang đăng nhập trong tool. Sửa nội dung tuỳ ý — mỗi dòng thành một đoạn.'),
      h('label', { class: 'check' }, markDone, h('span', {}, `Chuyển task #${j ? j.taskId : ''} sang `, h('b', {}, 'Hoàn thành'),
        d.doneAt ? h('small', { class: 'muted' }, ` — đã chuyển lúc ${fmt(d.doneAt)}`) : null))),
    h('div', { class: 'dlg-foot' }, h('span', { class: 'spacer' }),
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Để sau'), ok)));
  dlg.showModal();
}

// ---------- Cấu hình ----------
const SET = { view: null, localFiles: {}, keys: [], keyStatus: {} };

// ---------- Thống kê chi phí ----------
const STAGE_NAMES = {
  analyze: 'Phân tích', code: 'Code', review: 'Review', fix: 'Sửa theo review', revise: 'Sửa thêm theo yêu cầu',
  'test-data': 'Dữ liệu test', test: 'Test', chat: 'Chat', older: 'Lần chạy cũ',
};
const COSTS = { days: 30, paid: 'all', tab: 'overview', open: new Set(), r: null, t: null };
const usd = x => '$' + (x || 0).toFixed(2);
const pct = (x, total) => total > 0 ? Math.round(x / total * 100) : 0;
const fmtMin = m => m >= 60 ? `${Math.floor(m / 60)}g${String(Math.round(m % 60)).padStart(2, '0')}` : `${Math.round(m)}p`;
const TIME_NAMES = { ...STAGE_NAMES, code: 'Code / sửa thêm', build: 'Build', git: 'Git (nhánh, commit)' };

async function openCosts() {
  try {
    localStorage.getItem('costDays') && (COSTS.days = Number(localStorage.getItem('costDays')));
    localStorage.getItem('costTab') && (COSTS.tab = localStorage.getItem('costTab'));
  } catch { }
  $('#dlgCosts').showModal();
  await loadCosts(true);
}

/** Cache kết quả theo bộ lọc (30 giây) — bấm qua lại giữa các lựa chọn không phải gọi lại server. */
const COST_CACHE = new Map();
function cachedApi(url) {
  const hit = COST_CACHE.get(url);
  if (hit && Date.now() - hit.at < 30000) return hit.p;
  const p = api(url);
  COST_CACHE.set(url, { at: Date.now(), p });
  p.catch(() => COST_CACHE.delete(url));
  return p;
}

let costSeq = 0;
async function loadCosts(fresh = false) {
  if (fresh) COST_CACHE.clear();
  for (const b of $$('#costDays button')) b.classList.toggle('active', Number(b.dataset.v) === COSTS.days);
  for (const b of $$('#costPaid button')) b.classList.toggle('active', b.dataset.v === COSTS.paid);
  const body = $('#costsBody');
  const seq = ++costSeq;
  // Giữ nội dung cũ (làm mờ) trong lúc tải — không xoá trắng rồi vẽ lại.
  if (body.firstChild) body.classList.add('busy');
  else body.replaceChildren(h('div', { class: 'cs-loading' }, h('span', { class: 'spinner' }), 'Đang tính…'));
  let r, t;
  try {
    [r, t] = await Promise.all([
      cachedApi(`/costs?days=${COSTS.days}&paid=${COSTS.paid}`),
      cachedApi(`/insights/stats?days=${COSTS.days}`).catch(() => null)]);
  } catch (e) {
    if (seq === costSeq) { body.classList.remove('busy'); body.replaceChildren(h('div', { class: 'bad-text' }, e.message)); }
    return;
  }
  if (seq !== costSeq) return; // người dùng đã bấm lựa chọn khác — bỏ kết quả cũ
  COSTS.r = r; COSTS.t = t;
  renderCostsView(false);
  body.classList.remove('busy');
}

function renderCostsView(animate = true) {
  for (const b of $$('#costTabs button')) b.classList.toggle('active', b.dataset.v === COSTS.tab);
  // "Loại phí" chỉ lọc tiền — tab Thời gian không dùng tới.
  $('#costPaidWrap').classList.toggle('dim', COSTS.tab === 'time');
  const body = $('#costsBody');
  const r = COSTS.r, t = COSTS.t;
  if (!r) return;
  const view = COSTS.tab === 'tasks' ? costTasksView(r)
    : COSTS.tab === 'time' ? (t && t.jobs ? renderTimeStats(t) : csEmpty('Chưa có job nào có nhật ký thời gian trong khoảng này.'))
      : costOverview(r);
  if (animate) view.classList.add('fade');
  const scroller = body.closest('.dlg-body');
  const top = scroller.scrollTop;
  body.replaceChildren(view);
  // Đổi bộ lọc: giữ nguyên vị trí cuộn. Đổi tab: về đầu.
  scroller.scrollTop = animate ? 0 : top;
}

const csEmpty = text => h('div', { class: 'cs-empty' }, h('div', { class: 'cs-empty-ic' }, '🌱'), text);

/** Thẻ số liệu: nhãn nhỏ, số lớn, ghi chú. */
function csKpi(icon, label, value, note, cls = '') {
  return h('div', { class: 'cs-kpi ' + cls },
    h('div', { class: 'cs-kpi-ic' }, icon),
    h('div', { class: 'cs-kpi-main' },
      h('div', { class: 'cs-kpi-label' }, label),
      h('div', { class: 'cs-kpi-val' }, value),
      note ? h('div', { class: 'cs-kpi-note' }, note) : null));
}

/** Khung có tiêu đề + phụ chú. */
function csCard(title, sub, ...children) {
  return h('section', { class: 'cs-card' },
    h('div', { class: 'cs-card-head' }, h('b', {}, title), sub ? h('span', {}, sub) : null),
    ...children);
}

/** Danh sách thanh ngang: nhãn — thanh — giá trị (+ % trên tổng). */
function costBars(items, labelOf, opts = {}) {
  const val = opts.value || (x => x.cost);
  const fmt = opts.format || (x => usd(val(x)));
  const total = items.reduce((s, x) => s + val(x), 0);
  const max = Math.max(...items.map(val), 0.0001);
  if (!items.length) return h('div', { class: 'muted small' }, 'Không có dữ liệu.');
  return h('div', { class: 'cs-bars' }, items.map(x =>
    h('div', { class: 'cs-bar-row', title: opts.title ? opts.title(x) : `${labelOf(x)}: ${fmt(x)}` },
      h('span', { class: 'cs-bar-label' }, labelOf(x)),
      h('span', { class: 'cs-bar' }, h('span', { style: `width:${Math.max(2, val(x) / max * 100)}%` })),
      h('span', { class: 'cs-bar-val' }, fmt(x)),
      h('span', { class: 'cs-bar-pct' }, (opts.share ? opts.share(x) : pct(val(x), total)) + '%'))));
}

function costOverview(r) {
  if (!r.byTask.length) return csEmpty('Chưa có chi phí nào trong khoảng này.');
  const paidPct = pct(r.paidTotal, r.total);
  const kpis = h('div', { class: 'cs-kpis' },
    csKpi('💰', 'Tổng chi phí', usd(r.total), `${r.tasks} task · ${r.runs} phiên AI`, 'main'),
    csKpi('💳', 'Tiền thật', usd(r.paidTotal), 'API key / Agent SDK — trừ vào tài khoản', 'paid'),
    csKpi('🧮', 'Ước tính', usd(r.estTotal), 'tài khoản đăng nhập — không trừ tiền', 'est'),
    csKpi('📌', 'Trung bình / task', usd(r.tasks ? r.total / r.tasks : 0), `${r.tasks ? (r.runs / r.tasks).toFixed(1) : 0} phiên AI mỗi task`));
  const split = h('div', { class: 'cs-split', title: `Tiền thật ${paidPct}% · Ước tính ${100 - paidPct}%` },
    h('span', { class: 'paid', style: `width:${paidPct}%` }), h('span', { class: 'est', style: `width:${100 - paidPct}%` }));

  return h('div', { class: 'cs-view' },
    kpis,
    h('div', { class: 'cs-split-wrap' }, split,
      h('div', { class: 'cs-legend' },
        h('span', {}, h('i', { class: 'paid' }), `Tiền thật ${paidPct}%`),
        h('span', {}, h('i', { class: 'est' }), `Ước tính ${100 - paidPct}%`))),
    costDayChart(r),
    h('div', { class: 'cs-grid3' },
      csCard('Theo bước', null, costBars(r.byStage, x => STAGE_NAMES[x.key] || x.key)),
      csCard('Theo model', null, costBars(r.byModel, x => x.key.replace(/^claude-/, ''))),
      csCard('Theo key / xác thực', null, costBars(r.byAuth, x => x.key))));
}

function costDayChart(r) {
  if (r.byDay.length < 2) return null;
  const max = Math.max(...r.byDay.map(d => d.cost), 0.0001);
  const dm = d => d.slice(8) + '/' + d.slice(5, 7);
  return csCard('Chi phí theo ngày', `${r.byDay.length} ngày có chạy AI`,
    h('div', { class: 'cs-chart' },
      h('div', { class: 'cs-chart-grid' }, h('i'), h('i'), h('i')),
      h('div', { class: 'cs-days' }, r.byDay.map(d =>
        h('div', { class: 'cs-day', title: `${dm(d.day)}: ${usd(d.cost)}\nTiền thật ${usd(d.paid)} · Ước tính ${usd(d.cost - d.paid)}` },
          h('span', { class: 'cs-day-val' }, usd(d.cost)),
          h('span', { class: 'cs-day-bar', style: `height:calc((100% - 40px) * ${Math.max(0.02, d.cost / max).toFixed(3)})` },
            h('span', { class: 'paid', style: `height:${pct(d.paid, d.cost)}%` }),
            h('span', { class: 'est', style: `height:${100 - pct(d.paid, d.cost)}%` })),
          h('small', {}, dm(d.day)))))),
    h('div', { class: 'cs-legend' }, h('span', {}, h('i', { class: 'paid' }), 'Tiền thật'), h('span', {}, h('i', { class: 'est' }), 'Ước tính')));
}

function costTasksView(r) {
  if (!r.byTask.length) return csEmpty('Chưa có chi phí nào trong khoảng này.');
  const max = Math.max(...r.byTask.map(t => t.total), 0.0001);
  const rows = [];
  for (const t of r.byTask) {
    const open = COSTS.open.has(t.taskId);
    rows.push(h('tr', { class: 'cs-row' + (open ? ' open' : ''), onclick: () => { open ? COSTS.open.delete(t.taskId) : COSTS.open.add(t.taskId); renderCostsView(false); } },
      h('td', {}, h('div', { class: 'cs-task' },
        h('span', { class: 'caret' }, open ? '▾' : '▸'),
        h('div', {}, h('b', {}, '#' + t.taskId), h('div', { class: 'cs-task-name', title: t.taskName || '' }, t.taskName || '')))),
      h('td', { class: 'num' }, usd(t.job)),
      h('td', { class: 'num' }, t.chat ? usd(t.chat) : h('span', { class: 'muted' }, '—')),
      h('td', { class: 'num cs-total' },
        h('b', {}, usd(t.total)),
        h('span', { class: 'cs-mini' }, h('span', { style: `width:${t.total / max * 100}%` })),
        t.paid > 0 ? h('div', { class: 'cs-paid-note' }, 'thật ' + usd(t.paid)) : null),
      h('td', { class: 'num muted' }, new Date(t.last).toLocaleString('vi-VN', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }))));
    if (open) {
      const stages = Object.entries(t.stages).map(([key, cost]) => ({ key, cost })).sort((a, b) => b.cost - a.cost);
      rows.push(h('tr', { class: 'cs-detail' }, h('td', { colspan: '5' }, costBars(stages, x => STAGE_NAMES[x.key] || x.key))));
    }
  }
  return h('div', { class: 'cs-view' },
    csCard('Chi phí theo task', 'bấm một dòng để xem chi phí từng bước',
      h('div', { class: 'cs-table-wrap' },
        h('table', { class: 'cs-table' },
          h('thead', {}, h('tr', {}, h('th', {}, 'Task'), h('th', { class: 'num' }, 'Pipeline'), h('th', { class: 'num' }, 'Chat'), h('th', { class: 'num' }, 'Tổng'), h('th', { class: 'num' }, 'Gần nhất'))),
          h('tbody', {}, rows)))));
}

/** Thời gian máy chạy theo bước, thời gian chờ người, và gợi ý nên tối ưu chỗ nào. */
function renderTimeStats(t) {
  const bars = costBars(t.byStage, x => TIME_NAMES[x.key] || x.key, {
    value: x => x.minutes, format: x => fmtMin(x.avg) + '/job', share: x => x.share,
    title: x => `${TIME_NAMES[x.key] || x.key}: tổng ${fmtMin(x.minutes)} trên ${x.jobs} job${x.cost ? ' · ' + usd(x.cost) : ''}`,
  });
  const jobs = h('table', { class: 'cs-table' },
    h('thead', {}, h('tr', {}, h('th', {}, 'Task'), h('th', { class: 'num' }, 'Máy chạy'), h('th', { class: 'num' }, 'Chờ bạn'), h('th', { class: 'num', title: 'Vòng sửa theo review / lần bạn yêu cầu sửa thêm' }, 'Sửa'), h('th', { class: 'num' }, 'Chi phí'))),
    h('tbody', {}, t.byJob.map(j => h('tr', {
      class: 'cs-row', title: Object.entries(j.stages).map(([k, v]) => `${TIME_NAMES[k] || k}: ${fmtMin(v)}`).join('\n'),
      onclick: () => S.jobs.has(j.id) && ($('#dlgCosts').close(), openDrawer(j.id)),
    },
      h('td', {}, h('div', { class: 'cs-task' }, h('div', {}, h('b', {}, '#' + j.taskId), h('div', { class: 'cs-task-name', title: j.taskName || '' }, j.taskName || '')))),
      h('td', { class: 'num' }, fmtMin(j.machine)),
      h('td', { class: 'num' + (j.wait > j.machine ? ' warn-text' : '') }, fmtMin(j.wait)),
      h('td', { class: 'num' }, `${j.fixRounds} / ${j.revisions}`),
      h('td', { class: 'num' }, usd(j.cost))))));
  const rate = (v, good) => v == null ? '' : v >= good ? 'good' : 'bad';
  return h('div', { class: 'cs-view' },
    h('div', { class: 'cs-kpis' },
      csKpi('⚙', 'Máy chạy / job', fmtMin(t.machineAvg), `trung bình ${t.jobs} job, không tính lúc chờ`, 'main'),
      csKpi('⏳', 'Chờ bạn / job', fmtMin(t.waitAvg), 'từ lúc máy dừng tới khi bạn thao tác', t.waitAvg > t.machineAvg ? 'bad' : ''),
      csKpi('✅', 'Review đạt lần đầu', t.reviewFirstPass == null ? '—' : t.reviewFirstPass + '%', 'không cần vòng sửa', rate(t.reviewFirstPass, 70)),
      csKpi('❓', 'Phải hỏi lại yêu cầu', t.askedAgain == null ? '—' : t.askedAgain + '%', `sửa thêm TB ${t.revisionsAvg} lần/job`, t.askedAgain == null ? '' : t.askedAgain > 40 ? 'bad' : 'good')),
    t.hints.length ? h('section', { class: 'cs-hints' },
      h('div', { class: 'cs-hints-head' }, '💡 Gợi ý tối ưu'),
      h('ul', {}, t.hints.map(x => h('li', {}, x)))) : null,
    h('div', { class: 'cs-grid2' },
      csCard('Thời gian theo bước', 'trung bình mỗi job có bước đó', bars),
      csCard('Theo job', 'rê chuột xem từng bước · bấm để mở', h('div', { class: 'cs-table-wrap' }, jobs))));
}

// ---------- Cấu hình chia tab ----------
/** Mỗi fieldset trong Cấu hình thành một tab (tên lấy từ legend); nhớ tab mở gần nhất. */
const SETTINGS_META = {
  amisGroup: { sec: 'Kết nối', icon: '🔑', desc: 'Tài khoản AMIS dùng để đọc task, bình luận và cập nhật trạng thái.' },
  tfsGroup: { sec: 'Kết nối', icon: '🔀', desc: 'Tạo Pull Request trên TFS sau khi duyệt & push.' },
  chatBotGroup: { sec: 'Kết nối', icon: '💬', desc: 'Nhận thông báo và điều khiển AI qua một nhóm AMIS Chat — kể cả từ điện thoại.' },
  claudeGroup: { sec: 'AI', icon: '✨', desc: 'Cách xác thực (tài khoản Claude Code hoặc API key), model và giới hạn chi phí.' },
  kitGroup: { sec: 'AI', icon: '🧩', desc: 'Bộ skill/agent HRM đi kèm tool — thiếu thì bước code và review yếu hẳn.' },
  lessonGroup: { sec: 'AI', icon: '📚', desc: 'Bài học rút ra từ góp ý trước đây — AI đọc trước khi code và review.' },
  srcGroup: { sec: 'Làm việc', icon: '📁', desc: 'Thư mục workspace (CLAUDE.md, wiki, skill) và repo chính AI làm việc.' },
  procGroup: { sec: 'Làm việc', icon: '🧭', desc: 'Khi nào AI tự chuyển sang code, khi nào chờ bạn xác nhận trước khi commit.' },
  autoGroup: { sec: 'Làm việc', icon: '⏱', desc: 'Tự làm mới danh sách task AMIS theo chu kỳ và tự giao task mới cho AI phân tích.' },
  testGroup: { sec: 'Kiểm thử', icon: '🧪', desc: 'Build và chạy web IIS trên nhánh AI để AI test thật trên giao diện.' },
  dbGroup: { sec: 'Kiểm thử', icon: '🗄', desc: 'DB test để AI đọc dữ liệu và (nếu cho phép) tạo dữ liệu thử.' },
};

function setupSettingsTabs() {
  const body = $('#dlgSettings .dlg-body');
  const groups = [...body.querySelectorAll(':scope > fieldset.group')];
  const meta = g => SETTINGS_META[g.id] || { sec: 'Khác', icon: '⚙', desc: '' };
  const secs = [...new Set(groups.map(g => meta(g).sec))];
  const nav = h('nav', { class: 'set-nav', role: 'tablist' }, ...secs.flatMap(s => [
    h('div', { class: 'set-nav-sec' }, s),
    ...groups.filter(g => meta(g).sec === s).map(g =>
      h('button', { type: 'button', role: 'tab', 'data-tab': g.id, onclick: () => showSettingsTab(g.id) },
        h('span', { class: 'set-ic' }, meta(g).icon), h('span', {}, g.querySelector('legend').textContent))),
  ]));
  const head = h('div', { class: 'set-head' }, h('h3', { class: 'set-title' }), h('p', { class: 'set-desc' }));
  const main = h('div', { class: 'set-main' }, head);
  // Chuyển các nhóm và ghi chú cuối vào khung nội dung bên phải
  for (const el of [...body.children]) main.append(el);
  body.append(nav, main);
  body.classList.add('tabbed');
  let last = null;
  try { last = localStorage.getItem('settingsTab'); } catch { }
  showSettingsTab(groups.some(g => g.id === last) ? last : groups[0].id);
}

function showSettingsTab(id) {
  const body = $('#dlgSettings .dlg-body');
  const main = body.querySelector('.set-main') || body;
  for (const g of main.querySelectorAll(':scope > fieldset.group')) {
    g.hidden = g.id !== id;
    if (g.id === id) {
      const m = SETTINGS_META[id] || {};
      $('.set-title', main).textContent = ((m.icon || '') + ' ' + g.querySelector('legend').textContent).trim();
      $('.set-desc', main).textContent = m.desc || '';
    }
  }
  for (const b of body.querySelectorAll('.set-nav button')) b.classList.toggle('active', b.dataset.tab === id);
  main.scrollTop = 0;
  if (id === 'kitGroup') loadKit();
  if (id === 'lessonGroup') loadLessons();
  if (id === 'tfsGroup') loadTfs();
  if (id === 'chatBotGroup') loadChatBot();
  body.scrollTop = 0;
  try { localStorage.setItem('settingsTab', id); } catch { }
}

// ---------- Bot AMIS Chat ----------
async function loadChatBot() {
  try {
    const c = await api('/amischat');
    $('#cbConv').value = c.conversationId ? 'https://amisapp.misa.vn/chat/cv/' + c.conversationId : '';
    $('#cbEnabled').checked = c.enabled;
    $('#cbAsk').checked = c.notifyAsk; $('#cbBatch').checked = c.notifyBatch; $('#cbDone').checked = c.notifyDone; $('#cbFail').checked = c.notifyFail;
    $('#cbStatus').textContent = c.lastError ? '⚠ ' + c.lastError
      : c.enabled && c.lastPoll ? 'Đang chạy — kiểm tin lúc ' + new Date(c.lastPoll).toLocaleTimeString('vi-VN') : c.enabled ? 'Đang khởi động…' : 'Đang tắt';
    $('#cbStatus').className = 'small ' + (c.lastError ? 'bad-text' : 'muted');
  } catch (e) { $('#cbStatus').textContent = e.message; }
}

async function saveChatBot() {
  try {
    const r = await api('/amischat', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: $('#cbEnabled').checked, conversation: $('#cbConv').value,
        notifyAsk: $('#cbAsk').checked, notifyBatch: $('#cbBatch').checked, notifyDone: $('#cbDone').checked, notifyFail: $('#cbFail').checked }),
    });
    $('#cbName').textContent = r.name ? '✓ Nhóm: ' + r.name : '';
    toast('Đã lưu cấu hình bot');
    loadChatBot();
  } catch (e) { toast(e.message, true); $('#cbEnabled').checked = false; }
}

// ---------- TFS ----------
function showTfs(v) {
  $('#tfsAutoPr').checked = v.autoPr;
  $('#tfsPat').placeholder = v.hasPat ? `Đang dùng PAT ${v.masked} — nhập PAT mới để thay` : 'Chưa có PAT — đang dùng đăng nhập Windows';
}

async function loadTfs() {
  try { showTfs(await api('/tfs')); } catch (e) { $('#tfsResult').replaceChildren(h('span', { class: 'bad-text' }, e.message)); }
}

async function saveTfs(body) {
  try {
    showTfs(await api('/tfs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }));
    return true;
  } catch (e) { toast(e.message, true); return false; }
}

async function testTfs() {
  const box = $('#tfsResult');
  box.replaceChildren(h('span', { class: 'muted' }, 'Đang kiểm…'));
  try {
    const r = await api('/tfs/test', { method: 'POST' });
    box.replaceChildren(...r.lines.map(l => h('div', { class: r.ok ? 'ok-text' : (l.includes('Kết nối được') ? '' : 'bad-text') }, (l.includes('Kết nối được') ? '✓ ' : '✗ ') + l)));
  } catch (e) { box.replaceChildren(h('span', { class: 'bad-text' }, e.message)); }
}

async function createPr(j) {
  try {
    const nj = await api(`/jobs/${j.id}/pr`, { method: 'POST' });
    S.jobs.set(nj.id, nj);
    toast('Đã tạo PR');
    render();
    if (S.openJob === nj.id) renderDrawer();
    if (Object.keys(nj.prUrls || {}).length) openAmisUpdate(nj.id);
  } catch (e) { toast(e.message, true); }
}

// ---------- Bài học ----------
async function loadLessons() {
  const box = $('#lessonBody');
  if (!box) return;
  let list;
  try { list = await api('/lessons'); } catch (e) { box.replaceChildren(h('div', { class: 'bad-text' }, e.message)); return; }
  const src = { revise: 'yêu cầu sửa', reject: 'lý do từ chối', manual: 'tự thêm' };
  const from = l => l.taskId ? h('span', { class: 'small muted', title: l.taskName || '' }, ` · từ ${src[l.source] || l.source} task #${l.taskId}`) : null;
  const active = list.filter(l => !l.pending), pending = list.filter(l => l.pending);
  const activeRow = l => {
    const ta = h('textarea', { class: 'input', rows: String(Math.min(6, Math.max(1, Math.ceil(l.text.length / 90)))), style: 'width:100%' }, l.text);
    const on = h('input', { type: 'checkbox', checked: l.enabled, title: 'Bật/tắt (tắt thì không đưa vào prompt)', onchange: () => saveLesson(l.id, ta.value, on.checked) });
    return h('li', { class: 'lesson' + (l.enabled ? '' : ' off') },
      h('div', { class: 'row' }, on, h('span', { class: 'small muted' }, new Date(l.createdAt).toLocaleDateString('vi-VN')), from(l), h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm', type: 'button', onclick: () => saveLesson(l.id, ta.value, on.checked) }, 'Lưu'),
        h('button', { class: 'btn ghost sm', type: 'button', title: 'Xoá', onclick: () => delLesson(l) }, '✕')),
      ta);
  };
  const pendingRow = l => {
    const ta = h('textarea', { class: 'input', rows: String(Math.min(6, Math.max(2, Math.ceil(l.text.length / 90)))), style: 'width:100%' }, l.text);
    return h('li', { class: 'lesson pending' },
      h('div', { class: 'row' }, h('span', { class: 'chip warn' }, 'chờ duyệt'), from(l), h('span', { class: 'spacer' }),
        h('button', { class: 'btn sm primary', type: 'button', title: 'Sửa lời thành quy tắc chung trước khi lưu', onclick: () => saveLesson(l.id, ta.value, true) }, 'Lưu thành bài học'),
        h('button', { class: 'btn ghost sm', type: 'button', onclick: () => delLesson(l) }, 'Bỏ')),
      ta);
  };
  box.replaceChildren(
    h('h4', { class: 'lesson-h' }, `Đang áp dụng (${active.filter(l => l.enabled).length})`),
    active.length ? h('ul', { class: 'lesson-list' }, active.map(activeRow)) : h('div', { class: 'small muted' }, 'Chưa có bài học nào.'),
    pending.length ? h('h4', { class: 'lesson-h' }, `Gợi ý chờ duyệt (${pending.length})`) : null,
    pending.length ? h('div', { class: 'small muted' }, 'Chưa đưa vào prompt. Chỉ giữ góp ý có thể áp cho task khác; góp ý riêng của một task thì bấm Bỏ.') : null,
    pending.length ? h('ul', { class: 'lesson-list' }, pending.map(pendingRow)) : null);
}

async function saveLesson(id, text, enabled) {
  if (!text.trim()) { toast('Nội dung trống', true); return false; }
  try {
    await api('/lessons', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id, text, enabled }) });
    toast('Đã lưu bài học');
    loadLessons();
    return true;
  } catch (e) { toast(e.message, true); return false; }
}

async function delLesson(l) {
  if (!l.pending && !await ask('Xoá bài học này?')) return;
  try { await api('/lessons/' + l.id, { method: 'DELETE' }); } catch (e) { toast(e.message, true); }
  loadLessons();
}

// ---------- Bộ skill/agent ----------
const KIT_STATUS = {
  same: ['pass', 'Đã cài, khớp bộ cài'], different: ['warn', 'Đã cài, khác bộ cài'],
  missing: ['fail', 'Chưa cài'], empty: ['', 'Bộ cài trống'],
};

async function loadKit() {
  const box = $('#kitBody');
  if (!box) return;
  let v;
  try { v = await api('/kit?target=' + $('#kitTarget').value); } catch (e) { box.replaceChildren(h('div', { class: 'bad-text' }, e.message)); return; }
  KIT.view = v;
  const priv = new Set(v.privateIds || []);
  const fmt = d => d ? new Date(d).toLocaleString('vi-VN', { day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';
  const row = it => {
    const [cls, label] = KIT_STATUS[it.status] || ['', it.status];
    return h('tr', {},
      h('td', {},
        h('div', {}, h('span', { class: 'chip' }, it.kind === 'agent' ? 'agent' : 'skill'), ' ', h('b', { class: 'mono' }, it.name),
          it.required ? h('span', { class: 'chip fail', title: 'Prompt của tool gọi tới mục này' }, 'bắt buộc') : null,
          priv.has(it.id) ? h('span', { class: 'chip', title: 'Không có trên git (bị .gitignore) — chuyển sang máy khác bằng 📦 Xuất gói' }, 'nội bộ') : null),
        it.note ? h('div', { class: 'small' }, it.note) : null,
        it.description ? h('div', { class: 'small muted kit-desc', title: it.description }, it.description) : null),
      h('td', {}, h('span', { class: 'chip ' + cls }, label),
        it.status === 'different' ? h('div', { class: 'small muted', title: it.diff.join('\n') }, `${it.diff.length}/${it.files} file khác · máy ${fmt(it.localTime)} · bộ cài ${fmt(it.kitTime)}`) : null),
      h('td', { class: 'kit-act' },
        it.status === 'missing' ? h('button', { class: 'btn sm primary', type: 'button', onclick: () => kitInstall([it.id], false) }, 'Cài') : null,
        it.status === 'different' ? h('button', { class: 'btn sm', type: 'button', title: 'Ghi đè bản trên máy bằng bản trong bộ cài (bản cũ được sao lưu)', onclick: () => kitInstall([it.id], true) }, 'Cài đè') : null,
        it.status === 'different' && v.target === 'user' ? h('button', { class: 'btn sm', type: 'button', title: 'Bản trên máy mới hơn: chép vào bộ cài để gửi cho người khác', onclick: () => kitExport([it.id]) }, 'Lấy bản máy → bộ cài') : null,
        !it.required ? h('button', { class: 'btn ghost sm', type: 'button', title: 'Bỏ khỏi bộ cài (không xoá bản đã cài trên máy)', onclick: () => kitRemove(it) }, '✕') : null));
  };
  const missingReq = v.items.filter(i => i.required && i.status === 'missing');
  const emptyItems = v.items.filter(i => i.status === 'empty');
  box.replaceChildren(
    h('div', { class: 'small muted', style: 'margin:6px 0' }, 'Thư mục đích: ', h('code', {}, v.home), ' · Bộ cài: ', h('code', {}, v.kitDir),
      ` · Chuẩn công ty (standards/): ${v.standards ? v.standards + ' file' : 'chưa có'}`),
    emptyItems.length ? h('div', { class: 'errbox' }, `${emptyItems.length} mục chưa có file trong bộ cài (${emptyItems.map(i => i.name).join(', ')}) — thường do clone từ git: xin gói .zip từ người đang dùng rồi bấm "📥 Nhập gói".`) : null,
    missingReq.length ? h('div', { class: 'errbox' }, `Thiếu ${missingReq.length} mục bắt buộc: ${missingReq.map(i => i.name).join(', ')} — bấm "Cài mục còn thiếu".`) : null,
    h('table', { class: 'kit-table' }, h('tbody', {}, v.items.map(row))),
    v.local.length ? h('details', { class: 'kit-local' },
      h('summary', {}, `Skill/agent khác trên máy này chưa có trong bộ cài (${v.local.length})`),
      h('div', { class: 'small muted' }, 'Thêm vào bộ cài để gửi kèm tool cho người khác. Đường dẫn ~/.claude trong nội dung được đổi thành {{CLAUDE_HOME}}, lúc cài sẽ thay bằng thư mục của máy đích. Kiểm tra skill không chứa mật khẩu/dữ liệu riêng trước khi thêm.'),
      h('ul', {}, v.local.map(l => h('li', {},
        h('span', { class: 'chip' }, l.kind), ' ', h('b', { class: 'mono' }, l.name), ' ',
        h('button', { class: 'btn sm', type: 'button', onclick: () => kitExport([l.id]) }, 'Thêm vào bộ cài'),
        l.description ? h('div', { class: 'small muted kit-desc' }, l.description) : null)))) : null,
    h('div', { class: 'small muted', style: 'margin-top:8px' }, 'Skill có thể cần thêm công cụ ngoài: dich-nguoc-dll cần ilspycmd; vnr-reviewer dùng GitNexus MCP nếu có (không có vẫn chạy, chỉ thiếu tra cứu đồ thị code).'));
}

async function kitInstall(ids, overwrite) {
  if (overwrite && !await ask('Ghi đè bản đang cài bằng bản trong bộ cài? Bản cũ được sao lưu vào .claude\\backup.')) return;
  try {
    const r = await api('/kit/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ids, target: $('#kitTarget').value, overwrite }) });
    toast(r.done.length ? `Đã cài ${r.done.length} mục` + (r.backup ? ` — bản cũ sao lưu ở ${r.backup}` : '') : 'Không có mục nào cần cài');
  } catch (e) { toast(e.message, true); }
  loadKit();
}

async function kitExport(ids) {
  try {
    const r = await api('/kit/export', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ids }) });
    toast(`Đã cập nhật bộ cài: ${r.done.join(', ') || 'không có gì'}`);
  } catch (e) { toast(e.message, true); }
  loadKit();
}

const KIT = { view: null };

/** Xuất gói .zip các skill/agent (mặc định chọn sẵn mục nội bộ — không có trên git) + standards/. */
async function kitPackage() {
  const v = KIT.view;
  if (!v) return;
  const priv = new Set(v.privateIds || []);
  const items = v.items.filter(i => i.status !== 'empty').map(i => ({
    value: i.id, label: i.name, sub: `${i.kind}${i.required ? ' · bắt buộc' : ''}${priv.has(i.id) ? ' · nội bộ' : ''}`, checked: priv.has(i.id),
  }));
  if (v.standards) items.unshift({ value: '@standards', label: 'standards/', sub: `chuẩn PR/commit công ty · ${v.standards} file${v.standardsPrivate ? ' · nội bộ' : ''}`, checked: !!v.standardsPrivate });
  const picked = await pickList({
    icon: '📦', title: 'Xuất gói skill', ok: 'Tải .zip', items,
    text: 'Đóng gói để gửi cho đồng nghiệp (Teams/mail nội bộ). Mục "nội bộ" không có trên git nên được chọn sẵn. Đừng đăng gói lên chỗ công khai.',
  });
  if (!picked) return;
  const ids = picked.filter(x => x !== '@standards');
  const q = new URLSearchParams({ ids: ids.join(','), standards: picked.includes('@standards') });
  // Tải qua thẻ <a> để trình duyệt tự lưu file (server chỉ báo lỗi khi không chọn gì — pickList đã chặn trường hợp đó)
  const a = h('a', { href: '/api/kit/package?' + q, download: '' });
  document.body.append(a); a.click(); a.remove();
  toast(`Đang tải gói ${ids.length} mục${picked.includes('@standards') ? ' + standards/' : ''}`);
}

/** Nhập gói .zip vào bộ cài (claude-kit/ + standards/), sau đó cài như thường. */
async function kitImport(file) {
  if (!file) return;
  if (!await ask(`Nhập gói ${file.name} vào bộ cài?\nSkill/agent trùng tên trong bộ cài sẽ được thay (bản cũ sao lưu vào data/kit-backup). Bản đã cài trong ~/.claude chưa bị đụng — nhập xong bấm "Cài mục còn thiếu" hoặc "Cài đè".`, { ok: 'Nhập' })) return;
  const fd = new FormData();
  fd.append('file', file);
  try {
    const r = await api('/kit/package', { method: 'POST', body: fd });
    toast(`Đã nhập ${r.done.length} mục${r.standards.length ? ` + ${r.standards.length} file standards` : ''}` + (r.backup ? ` — bản cũ ở ${r.backup}` : ''));
  } catch (e) { toast(e.message, true); }
  loadKit();
}

async function kitRemove(it) {
  if (!await ask(`Bỏ ${it.name} khỏi bộ cài? Bản đã cài trên máy không bị xoá.`)) return;
  try { await api('/kit/remove', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ids: [it.id] }) }); }
  catch (e) { toast(e.message, true); }
  loadKit();
}

// ---------- Popup xác nhận (thay confirm/prompt của trình duyệt) ----------
/**
 * Hỏi xác nhận bằng popup của tool. Trả về true/false.
 * opts: { title, ok, cancel, danger } — danger=true tô đỏ nút đồng ý (huỷ, xoá…).
 */
function ask(message, opts = {}) {
  return askDialog(message, opts, false);
}

/** Hỏi nhập một dòng chữ. Trả về chuỗi, hoặc null nếu bấm Huỷ. */
function askText(message, def = '', opts = {}) {
  return askDialog(message, { ...opts, input: def }, true);
}

function askDialog(message, opts, withInput) {
  return new Promise(resolve => {
    const lines = String(message).split('\n');
    const danger = opts.danger ?? /^(Huỷ|Hủy|Xoá|Xóa|Bỏ|Ghi đè)/i.test(message);
    const input = withInput ? h('input', { class: 'input', value: opts.input || '', placeholder: opts.placeholder || '' }) : null;
    const dlg = h('dialog', { class: 'dlg ask-dlg' + (danger ? ' danger' : '') },
      h('form', { method: 'dialog', class: 'dlg-inner' },
        h('div', { class: 'ask-body' },
          h('div', { class: 'ask-ic' }, opts.icon || (withInput ? '✎' : danger ? '!' : '?')),
          h('div', { class: 'ask-main' },
            h('div', { class: 'ask-title' }, opts.title || lines[0]),
            ...(opts.title ? lines : lines.slice(1)).filter((l, i, a) => l.trim() || (i > 0 && i < a.length - 1))
              .map(l => l.trim() ? h('p', { class: 'ask-text' }, l) : h('div', { class: 'ask-gap' })),
            input)),
        h('div', { class: 'dlg-foot' },
          h('button', { class: 'btn ghost', value: 'cancel', type: 'submit' }, opts.cancel || 'Huỷ bỏ'),
          h('button', { class: 'btn ' + (danger ? 'danger-solid' : 'primary'), value: 'ok', type: 'submit' }, opts.ok || (danger ? 'Đồng ý' : 'OK')))));
    dlg.addEventListener('close', () => {
      const ok = dlg.returnValue === 'ok';
      dlg.remove();
      resolve(withInput ? (ok ? input.value : null) : ok);
    });
    // Enter trong ô nhập = đồng ý (mặc định form sẽ bấm nút đầu tiên là "Huỷ bỏ")
    input?.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); dlg.close('ok'); } });
    document.body.append(dlg);
    dlg.showModal();
    (input || dlg.querySelector('button[value=ok]')).focus();
    if (input) input.select();
  });
}

// ---------- Giao diện sáng/tối ----------
// [id, biểu tượng, tên, nền sáng/tối, skin, hiệu ứng, mô tả]
const THEMES = [
  ['auto', '🖥', 'Theo hệ thống', null, null, null, 'Sáng/tối theo Windows'],
  ['light', '☀', 'Sáng', 'light', null, null, 'Mặc định'],
  ['dark', '🌙', 'Tối', 'dark', null, null, 'Mặc định'],
  ['sakura', '🌸', 'Sakura', 'light', 'sakura', 'petal', 'Hồng phấn, cánh hoa rơi'],
  ['sunset', '🌅', 'Hoàng hôn', 'light', 'sunset', null, 'Trời chiều cam tím'],
  ['summer', '🎐', 'Mùa hè', 'light', 'summer', 'cloud', 'Trời xanh, mây trôi'],
  ['neon', '🌃', 'Đêm Neon', 'dark', 'neon', null, 'Phố đêm hồng – xanh'],
  ['starry', '🌌', 'Trời sao', 'dark', 'starry', 'star', 'Đêm xanh thẳm, sao lấp lánh'],
  ['bamboo', '🎋', 'Rừng tre', 'dark', 'bamboo', 'firefly', 'Xanh rêu, đom đóm'],
  ['ninja', '🍃', 'Ninja', 'dark', 'ninja', 'leaf', 'Cam – đen, lá bay · cảm hứng Naruto'],
  ['saiyan', '⚡', 'Chiến binh', 'light', 'saiyan', 'spark', 'Cam – xanh, hào quang vàng · cảm hứng Dragon Ball'],
];

function currentTheme() {
  try { return localStorage.getItem('theme') || 'auto'; } catch { return 'auto'; }
}

function applyTheme(t) {
  const th = THEMES.find(x => x[0] === t) || THEMES[0];
  const [, icon, label, base, skin, fx] = th;
  const root = document.documentElement;
  if (base) root.dataset.theme = base; else delete root.dataset.theme;
  if (skin) root.dataset.skin = skin; else delete root.dataset.skin;
  setThemeFx(fx);
  const b = $('#btnTheme');
  if (b) { b.textContent = icon; b.title = 'Giao diện: ' + label + ' — bấm để đổi'; }
}

/** Lớp hiệu ứng nền (cánh hoa, mây, sao, đom đóm) — nằm sau bảng, không bắt chuột. */
function setThemeFx(kind) {
  document.getElementById('themeFx')?.remove();
  if (!kind || matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  const n = { petal: 12, cloud: 5, star: 30, firefly: 12, leaf: 10, spark: 14 }[kind] || 0; // giảm số hạt: đỡ tải GPU khi để tab mở lâu
  const r = (a, b) => a + Math.random() * (b - a);
  const layer = h('div', { id: 'themeFx', class: 'fx-' + kind, 'aria-hidden': 'true' },
    ...Array.from({ length: n }, () => h('i', {
      style: `--x:${r(0, 100)}vw;--y:${r(0, 100)}vh;--s:${r(.6, 1.4)};--d:${r(kind === 'cloud' ? 60 : 9, kind === 'cloud' ? 120 : 20)}s;--w:${r(-8, 0) * (kind === 'cloud' ? 12 : 1)}s;--sway:${r(-60, 60)}px`,
    })));
  document.body.prepend(layer);
}

function cycleTheme(e) {
  // Bảng chọn theme nhỏ dưới nút
  const old = document.getElementById('themeMenu');
  if (old) { old.remove(); return; }
  const cur = currentTheme();
  const btn = $('#btnTheme').getBoundingClientRect();
  const menu = h('div', { id: 'themeMenu', class: 'theme-menu', role: 'menu', style: `top:${btn.bottom + 8}px;right:${Math.max(8, innerWidth - btn.right)}px` },
    h('div', { class: 'theme-menu-title' }, 'Giao diện'),
    h('div', { class: 'theme-grid' }, THEMES.map(([id, icon, label, , skin, , desc]) =>
      h('button', {
        type: 'button', role: 'menuitemradio', class: 'theme-opt' + (id === cur ? ' active' : ''), 'data-sw': skin || id,
        onclick: () => {
          try { localStorage.setItem('theme', id); } catch { }
          applyTheme(id);
          menu.remove();
        },
      }, h('span', { class: 'theme-sw' }, icon), h('span', { class: 'theme-name' }, label), h('small', {}, desc)))));
  document.body.append(menu);
  setTimeout(() => document.addEventListener('click', function close(ev) {
    if (!menu.contains(ev.target)) { menu.remove(); document.removeEventListener('click', close); }
  }), 0);
  e?.stopPropagation();
}

async function openSettings(tab) {
  let v;
  try { v = await api('/settings'); } catch (e) { toast(e.message, true); return; }
  SET.view = v;
  SET.keys = (v.apiKeys || []).map(k => ({ ...k }));
  SET.keyStatus = {};
  $('#setKeyStrategy').value = v.keyStrategy || 'failover';
  $('#newKeyLabel').value = '';
  $('#setWorkspace').value = v.workspaceRoot || '';
  fillModelSelect($('#setModel'), 'Mặc định của Claude Code (hiện là Opus 5.5)', v.model || '');
  $('#setAutoCode').checked = !!v.autoCodeWhenClear;
  $('#setAutoApprove').checked = !!v.autoApproveSmall;
  $('#setPullBefore').checked = v.pullBeforeTask !== false;
  $('#setReviewMode').value = v.reviewMode || 'auto';
  $('#setReviewerModel').value = v.reviewerModel ?? 'sonnet';
  $('#reviewModeNote').textContent = `— "diff lớn" = quá ${v.reviewLightMaxFiles ?? 6} file hoặc ${v.reviewLightMaxLines ?? 150} dòng. Review lại sau vòng sửa luôn chỉ xét phần vừa sửa.`;
  $('#autoApproveNote').textContent = `— chỉ khi review PASS, test PASS, yêu cầu rõ, AI không tự quyết điểm nào, không sửa SQL/config, tối đa ${v.autoApproveMaxFiles ?? 3} file và ${v.autoApproveMaxLines ?? 80} dòng. Tool commit vào nhánh AI rồi đánh dấu Đã duyệt — KHÔNG push; bạn vẫn tự push.`;
  $('#setChatWrite').checked = !!v.chatCanWriteFiles;
  $('#setConfirmCommit').checked = v.confirmCommit !== false;
  $('#setMaxJobCost').value = v.maxJobCostUsd || 0;
  $('#setMaxChatCost').value = v.maxChatCostUsd || 0;
  $('#setCostPaidOnly').checked = v.costLimitPaidOnly !== false;
  try { S.autopilot = await api('/autopilot'); } catch (e) { toast(e.message, true); }
  fillAutoForm();
  try { fillTestForm(await api('/testenv')); } catch (e) { toast(e.message, true); }
  try { S.testdb = await api('/testdb'); fillTestDbForm(S.testdb); } catch (e) { toast(e.message, true); }
  loadDbProfiles();
  $('#setApiKey').value = '';
  $('#setApiKey').type = 'password';
  document.querySelectorAll('input[name=authMode]').forEach(r => { r.checked = r.value === v.authMode; });
  $('#testClaudeResult').replaceChildren();
  $('#setAmisInfo').textContent = S.me ? `Đang dùng: ${S.me.fullName} (${S.me.email || S.me.userName})` : (S.meError || 'Chưa kết nối AMIS');
  updateAuthUI();
  if (tab) showSettingsTab(tab);
  $('#dlgSettings').showModal();
  await scanRepos(v.repoRelativePath);
  renderExtraRepos(v.extraRepos || []);
  SET.localFiles = JSON.parse(JSON.stringify(v.localFiles || {}));
  $('#setLocalFiles').replaceChildren();
  renderLocalFiles();
}

function authMode() {
  const r = document.querySelector('input[name=authMode]:checked');
  return r ? r.value : 'login';
}

function updateAuthUI() {
  const paid = ['apikey', 'sdk'].includes(authMode());
  $('#apiKeyField').hidden = !paid;
  $('#btnTestKeys').hidden = !paid;
  renderKeys();
}

// ---------- Danh sách API key (xoay tua) ----------
function renderKeys() {
  const box = $('#keyList');
  const info = $('#setApiKeyInfo');
  if (!SET.keys.length) {
    box.replaceChildren();
    info.textContent = 'Chưa có key. Tạo ở console.anthropic.com → API Keys, dán vào ô trên rồi bấm "+ Thêm key".';
    return;
  }
  info.textContent = 'Thứ tự trên xuống là thứ tự dùng (chế độ "Dùng lần lượt"). Thay đổi chỉ có hiệu lực sau khi bấm Lưu.';
  box.replaceChildren(...SET.keys.map((k, i) => {
    const st = SET.keyStatus[k.id || k.tmp];
    let status;
    if (st) status = h('span', { class: st.ok ? 'ok-text' : 'bad-text', title: st.text }, st.ok ? '✓ dùng được' : '✗ ' + st.text);
    else if (!k.id) status = h('span', { class: 'muted' }, 'mới — chưa lưu');
    else if (k.cooldownUntil) status = h('span', { class: 'warn-text', title: k.cooldownReason || '' },
      `tạm nghỉ tới ${new Date(k.cooldownUntil).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' })} (${k.cooldownReason || 'lỗi'})`);
    else status = h('span', { class: 'muted' }, k.enabled ? 'sẵn sàng' : 'đang tắt');
    const en = h('input', { type: 'checkbox', title: 'Bật/tắt key này' });
    en.checked = k.enabled !== false;
    en.addEventListener('change', () => { k.enabled = en.checked; renderKeys(); });
    const label = h('input', { class: 'input key-label', maxlength: '60', value: k.label || '' });
    label.addEventListener('input', () => { k.label = label.value; });
    const move = d => { const j = i + d; if (j < 0 || j >= SET.keys.length) return; [SET.keys[i], SET.keys[j]] = [SET.keys[j], SET.keys[i]]; renderKeys(); };
    return h('div', { class: 'key-row' + (k.enabled === false ? ' off' : '') },
      en, label,
      h('code', { class: 'key-mask' }, k.masked || '••••'),
      h('span', { class: 'key-status' }, status),
      h('button', { class: 'btn ghost icon', type: 'button', title: 'Lên', disabled: i === 0, onclick: () => move(-1) }, '↑'),
      h('button', { class: 'btn ghost icon', type: 'button', title: 'Xuống', disabled: i === SET.keys.length - 1, onclick: () => move(1) }, '↓'),
      h('button', { class: 'btn ghost icon', type: 'button', title: 'Xoá key này (có hiệu lực khi bấm Lưu)',
        onclick: async () => { if (await ask(`Xoá key "${k.label}"?`)) { SET.keys.splice(i, 1); renderKeys(); } } }, '🗑'));
  }));
}

/** Thêm key đang gõ vào danh sách (chưa lưu). Trả false nếu key không hợp lệ. */
function addTypedKey() {
  const input = $('#setApiKey');
  const key = input.value.trim();
  if (!key) return true;
  if (!key.startsWith('sk-ant-')) { toast('API key Anthropic phải bắt đầu bằng "sk-ant-".', true); return false; }
  const label = $('#newKeyLabel').value.trim() || `Key ${SET.keys.length + 1}`;
  SET.keys.push({ id: null, tmp: 'new' + Date.now(), label, key, masked: key.slice(0, 10) + '…' + key.slice(-4), enabled: true });
  input.value = '';
  input.type = 'password';
  $('#newKeyLabel').value = '';
  renderKeys();
  return true;
}

async function testAllKeys() {
  const btn = $('#btnTestKeys');
  if (SET.keys.some(k => !k.id)) { toast('Có key mới chưa lưu — bấm Lưu trước rồi kiểm tra.', true); return; }
  btn.disabled = true;
  const out = $('#testClaudeResult');
  out.replaceChildren(h('span', { class: 'muted' }, `Đang gọi thử ${SET.keys.filter(k => k.enabled !== false).length} key…`));
  try {
    const r = await api('/settings/test-keys', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ authMode: authMode(), model: $('#setModel').value.trim() || null }),
    });
    for (const x of r.results) SET.keyStatus[x.id] = { ok: x.r.ok, text: x.r.ok ? 'OK' : x.r.result };
    // Cập nhật trạng thái tạm nghỉ mới nhất, giữ phần người dùng đang sửa (tên, bật/tắt, thứ tự)
    for (const k of SET.keys) {
      const fresh = (r.view.apiKeys || []).find(x => x.id === k.id);
      if (fresh) { k.cooldownUntil = fresh.cooldownUntil; k.cooldownReason = fresh.cooldownReason; }
    }
    const okCount = r.results.filter(x => x.r.ok).length;
    out.replaceChildren(h('span', { class: okCount === r.results.length ? 'ok-text' : 'bad-text' },
      `${okCount}/${r.results.length} key dùng được (${r.mode === 'sdk' ? 'qua Agent SDK' : 'qua Claude CLI'})`));
    renderKeys();
  } catch (e) {
    out.replaceChildren(h('span', { class: 'bad-text' }, e.message));
  } finally { btn.disabled = false; }
}

async function scanRepos(selected) {
  const root = $('#setWorkspace').value.trim();
  const sel = $('#setRepo');
  const info = $('#setWorkspaceInfo');
  const keep = selected ?? sel.value;
  info.textContent = 'Đang quét…';
  try {
    const r = await api('/settings/repos?root=' + encodeURIComponent(root));
    info.replaceChildren(
      r.hasClaudeMd ? h('span', { class: 'ok-text' }, '✓ Có CLAUDE.md') : h('span', { class: 'bad-text' }, '⚠ Không có CLAUDE.md — AI sẽ thiếu quy ước và wiki của dự án'),
      ` · tìm thấy ${r.repos.length} git repo`);
    // Chính thư mục workspace (".") chứa wiki/tool, không phải source sản phẩm — không đưa vào lựa chọn
    const repos = r.repos.filter(p => p !== '.');
    sel.replaceChildren(...repos.map(p => h('option', { value: p }, p)));
    if (!repos.length) sel.append(h('option', { value: '' }, '— không có git repo con nào —'));
    else sel.value = repos.includes(keep) ? keep : (repos.find(p => /HRM9/i.test(p)) || repos[0]);
    SET.repos = repos;
  } catch (e) {
    info.replaceChildren(h('span', { class: 'bad-text' }, e.message));
    sel.replaceChildren();
    SET.repos = [];
  }
  renderExtraRepos();
  renderLocalFiles();
  updateRepoPath();
}

/** Danh sách file cấu hình local của từng repo đang dùng (repo chính + repo phụ đã tick). */
function renderLocalFiles() {
  const box = $('#setLocalFiles');
  // Giữ nội dung đang gõ khi vẽ lại (đổi repo chính / tick repo phụ)
  box.querySelectorAll('textarea[data-repo]').forEach(t => { SET.localFiles[t.dataset.repo] = t.value.split('\n'); });
  const repos = [$('#setRepo').value, ...[...document.querySelectorAll('#setExtraRepos input:checked')].map(c => c.value)].filter(Boolean);
  box.replaceChildren(...repos.map(repo => {
    const ta = h('textarea', { class: 'input sm mono', rows: 3, 'data-repo': repo, spellcheck: 'false', placeholder: 'mỗi dòng một file, đường dẫn trong repo, ví dụ src/assets/config.json' });
    ta.value = (SET.localFiles[repo] || []).filter(Boolean).join('\n');
    const note = h('small', { class: 'muted' });
    const pick = async () => {
      note.textContent = 'Đang đọc…';
      try {
        const files = await api('/settings/modified?repo=' + encodeURIComponent(repo));
        if (!files.length) { note.textContent = 'Repo không có file đã track nào đang sửa.'; return; }
        const cur = new Set(ta.value.split('\n').map(s => s.trim()).filter(Boolean));
        files.forEach(f => cur.add(f.path));
        ta.value = [...cur].join('\n');
        note.textContent = `Đã thêm ${files.length} file đang sửa — xoá dòng nào là thay đổi thật cần commit.`;
      } catch (e) { note.textContent = e.message; }
    };
    return h('div', { class: 'local-repo' },
      h('div', { class: 'row' }, h('b', { class: 'mono', style: 'flex:1' }, repo),
        h('button', { class: 'btn sm', type: 'button', title: 'Thêm mọi file đã track đang sửa của repo này', onclick: pick }, 'Lấy các file đang sửa')),
      ta, note);
  }));
}

function readLocalFiles() {
  const out = {};
  document.querySelectorAll('#setLocalFiles textarea[data-repo]').forEach(t => {
    out[t.dataset.repo] = t.value.split('\n').map(s => s.trim()).filter(Boolean);
  });
  return out;
}

/** Ô tick repo phụ: mọi repo con trừ repo chính đang chọn. Giữ lựa chọn khi đổi repo chính. */
function renderExtraRepos(selected) {
  const box = $('#setExtraRepos');
  const prev = selected ?? [...box.querySelectorAll('input:checked')].map(c => c.value);
  const main = $('#setRepo').value;
  const list = (SET.repos || []).filter(p => p !== main);
  box.replaceChildren(...(list.length
    ? list.map(p => h('label', { class: 'check inline' },
      h('input', { type: 'checkbox', value: p, checked: prev.includes(p) }), h('span', { class: 'mono' }, p)))
    : [h('span', { class: 'muted small' }, 'Không có repo nào khác trong workspace.')]));
}

function updateRepoPath() {
  const root = $('#setWorkspace').value.trim().replace(/[\\/]+$/, '');
  const rel = $('#setRepo').value;
  $('#setRepoPath').textContent = rel ? (rel === '.' ? root : `${root}\\${rel}`) : '';
}

async function saveSettings() {
  // Key đang gõ mà chưa bấm "+ Thêm key" cũng được thêm khi Lưu
  if (!addTypedKey()) return;
  const btn = $('#btnSaveSettings');
  btn.disabled = true;
  try {
    const v = await api('/settings', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        workspaceRoot: $('#setWorkspace').value.trim(),
        repoRelativePath: $('#setRepo').value,
        extraRepos: [...document.querySelectorAll('#setExtraRepos input:checked')].map(c => c.value),
        localFiles: readLocalFiles(),
        model: $('#setModel').value.trim(),
        autoCodeWhenClear: $('#setAutoCode').checked,
        autoApproveSmall: $('#setAutoApprove').checked,
        pullBeforeTask: $('#setPullBefore').checked,
        reviewMode: $('#setReviewMode').value,
        reviewerModel: $('#setReviewerModel').value,
        chatCanWriteFiles: $('#setChatWrite').checked,
        confirmCommit: $('#setConfirmCommit').checked,
        maxJobCostUsd: Number($('#setMaxJobCost').value) || 0,
        maxChatCostUsd: Number($('#setMaxChatCost').value) || 0,
        costLimitPaidOnly: $('#setCostPaidOnly').checked,
        authMode: authMode(),
        apiKeys: SET.keys.map(k => ({ id: k.id || null, label: k.label, key: k.id ? null : k.key, enabled: k.enabled !== false })),
        keyStrategy: $('#setKeyStrategy').value,
      }),
    });
    S.config = v;
    SET.view = v;
    S.autopilot = await api('/autopilot', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readAutoForm()) });
    await api('/testenv', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readTestForm()) });
    S.testdb = await api('/testdb', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readTestDbForm()) });
    await saveDbProfiles();
    renderDbPill();
    renderAutoPill();
    $('#dlgSettings').close();
    toast('Đã lưu cấu hình');
    loadRepo();
  } catch (e) { toast(e.message, true); }
  finally { btn.disabled = false; }
}

// ---------- Môi trường test ----------
function fillTestForm(v) {
  $('#teBuild').checked = v.buildBeforeTest;
  $('#teRebuild').checked = v.rebuildAfterReturn;
  const mode = v.enabled === false ? 'off' : v.uiTests ? 'full' : 'data';
  document.querySelectorAll('input[name="teMode"]').forEach(r => { r.checked = r.value === mode; });
  $('#teBudget').value = v.maxTestCostUsd ?? 8;
  $('#teAutoFix').checked = v.autoFixOnTestFail !== false;
  $('#teFixRounds').value = v.maxTestFixRounds ?? 2;
  $('#teAutoPortal').checked = v.autoStartPortal !== false;
  $('#teTempAcc').checked = !!v.tempAccounts;
  S.testMode = mode;
  $('#teBaseUrl').value = v.baseUrl || '';
  $('#teBaseUrl').placeholder = v.effectiveBaseUrl ? `tự động: ${v.effectiveBaseUrl}` : 'https://…/';
  $('#teUser').value = v.user || '';
  $('#tePass').value = '';
  $('#tePass').placeholder = v.hasPassword ? 'đã lưu — để trống nếu không đổi' : 'chưa có';
  $('#teExtra').value = v.extraSites || '';
  $('#teAccounts').replaceChildren(...(v.accounts || []).map(teAccRow));
  const rows =[...v.sites.map(s => [s.project, s.url]), ...(v.extraSiteList || []).map(s => [s.name, s.url])];
  $('#teSites').replaceChildren(rows.length
    ? h('table', {}, h('tbody', {}, rows.map(([n, u]) => h('tr', {}, h('td', {}, n), h('td', {}, h('a', { href: u, target: '_blank', rel: 'noopener' }, u))))))
    : h('div', { class: 'muted small' }, 'Không thấy site IIS nào trỏ vào repo (hoặc không đọc được cấu hình IIS).'),
    v.buildScriptExists ? null : h('div', { class: 'bad-text small' }, `Không thấy script build ${v.buildScript}`));
  syncTestForm();
}

function readTestForm() {
  const pass = $('#tePass').value;
  return {
    buildBeforeTest: $('#teBuild').checked,
    rebuildAfterReturn: $('#teRebuild').checked,
    enabled: teMode() !== 'off',
    uiTests: teMode() === 'full',
    maxTestCostUsd: Number($('#teBudget').value || 0),
    autoFixOnTestFail: $('#teAutoFix').checked,
    autoStartPortal: $('#teAutoPortal').checked,
    maxTestFixRounds: Math.min(5, Math.max(1, parseInt($('#teFixRounds').value, 10) || 2)),
    tempAccounts: $('#teTempAcc').checked,
    baseUrl: $('#teBaseUrl').value.trim(),
    user: $('#teUser').value.trim(),
    password: pass ? pass : null, // null = giữ mật khẩu đã lưu
    extraSites: $('#teExtra').value,
    accounts: [...$('#teAccounts').children].map(r => ({
      role: r.querySelector('.acc-role').value.trim(),
      user: r.querySelector('.acc-user').value.trim(),
      password: r.querySelector('.acc-pass').value || null, // null = giữ mật khẩu đã lưu của tài khoản này
    })).filter(a => a.user),
  };
}

// Một dòng tài khoản test thêm: vai trò · tài khoản · mật khẩu · xoá
function teAccRow(a = {}) {
  const row = h('div', { class: 'te-acc' },
    h('input', { class: 'input sm acc-role', placeholder: 'Vai trò, vd: Người duyệt cấp 1', value: a.role || '', spellcheck: 'false' }),
    h('input', { class: 'input sm acc-user', placeholder: 'Tài khoản', value: a.user || '', autocomplete: 'off', spellcheck: 'false' }),
    h('input', { class: 'input sm acc-pass', type: 'password', autocomplete: 'new-password',
      placeholder: a.hasPassword ? 'đã lưu — để trống nếu không đổi' : 'mật khẩu' }),
    h('button', { class: 'btn sm', type: 'button', title: 'Xoá tài khoản này', onclick: () => row.remove() }, '✕'));
  return row;
}

function teMode() { return document.querySelector('input[name="teMode"]:checked')?.value || 'full'; }
function syncTestForm() { $('#teUiSub').classList.toggle('disabled', teMode() !== 'full'); }
const TEST_MODE_VI = { full: 'Đầy đủ (giao diện + ảnh)', data: 'Chỉ kiểm dữ liệu', off: 'Không test' };

// ---------- DB dữ liệu test ----------
async function loadTestDb() {
  try { S.testdb = await api('/testdb'); } catch { S.testdb = null; }
  renderDbPill();
}

function dbLabel(server, database) { return database ? `${database}${server ? ' @ ' + server : ''}` : '(chưa cấu hình)'; }

/** Ô trên thanh trên cùng: DB AI đọc và trạng thái ghi — để luôn thấy đang thao tác DB nào. */
function renderDbPill() {
  const pill = $('#btnDb');
  const t = S.testdb;
  const r = t && t.readDb;
  const write = t && t.enabled;
  const mismatch = write && !t.sameAsReadDb;
  pill.classList.toggle('write', !!write);
  pill.classList.toggle('warn', !!mismatch || !r);
  $('.db-text', pill).textContent = !t ? 'DB: …'
    : `${r ? r.database : 'chưa có DB'}${write ? (mismatch ? ' · ghi: ' + t.database + ' ⚠' : ' · được ghi') : ' · chỉ đọc'}`;
  pill.title = !t ? '' : [
    `AI đọc (tools/db/ro.local.json): ${r ? dbLabel(r.server, r.database) + ', login ' + r.user : 'chưa cấu hình'}`,
    write ? `Tool ghi dữ liệu test: ${dbLabel(t.server, t.database)}${mismatch ? ' — KHÁC DB AI đọc, tool sẽ không ghi' : ''}` : 'Ghi dữ liệu test: tắt',
    'Bấm để cấu hình'].join('\n');
}

/** Dòng trong popup giao task: DB sẽ dùng, cảnh báo nếu tên DB không khớp dự án. */
function renderDbNotice(project) {
  const t = S.testdb;
  if (!t || !t.readDb) return null;
  const db = t.enabled ? t.database : t.readDb.database;
  const words = projectWords(project);
  const tokens = branchTokens(db);
  const key = projectKey(project);
  const match = !project || normKey(db).includes(key) || words.some((w, i) => tokens.some(x => x === w || (i === 0 && x.startsWith(w))));
  const warn = !match && !(t.enabled && t.anyProject);
  return h('div', { class: 'db-notice' + (warn ? ' warn' : '') },
    warn ? '⚠ ' : '', 'DB: ', h('b', {}, dbLabel(t.enabled ? t.server : t.readDb.server, db)),
    t.enabled ? (t.sameAsReadDb ? ' — AI đọc và tool được ghi dữ liệu test' : ' — ghi khác DB AI đọc, tool sẽ không ghi') : ' — AI chỉ đọc',
    warn ? h('span', {}, ` · tên DB không khớp dự án ${key}${t.enabled ? ', tool sẽ không tạo dữ liệu' : ''}. `,
      h('button', { class: 'link-btn', type: 'button', onclick: () => openSettings('dbGroup') }, 'Đổi DB')) : null);
}

function fillTestDbForm(v) {
  const r = v.readDb;
  $('#tdReadDb').replaceChildren(r
    ? h('span', {}, 'AI đang đọc: ', h('b', { class: 'mono' }, dbLabel(r.server, r.database)), h('span', { class: 'muted' }, ` (login ${r.user}, tools/db/ro.local.json)`),
      v.sameAsReadDb || !v.database ? null : h('span', { class: 'bad-text' }, ' — khác DB bên dưới; tick "AI đọc DB này" rồi Lưu để đồng bộ'))
    : h('span', { class: 'bad-text' }, 'Chưa có tools/db/ro.local.json — AI không truy vấn được DB.'));
  $('#tdEnabled').checked = v.enabled;
  $('#tdSync').checked = v.syncReadDb !== false;
  $('#tdServer').value = v.server || '';
  $('#tdDatabase').value = v.database || '';
  $('#tdUser').value = v.user || '';
  $('#tdPass').value = '';
  $('#tdPass').placeholder = v.hasPassword ? 'đã lưu — để trống nếu không đổi' : 'chưa có';
  $('#tdMaxRows').value = v.maxRows || 500;
  $('#tdTrust').checked = v.trustServerCertificate;
  $('#tdAnyProject').checked = v.anyProject;
  $('#tdAllowExec').checked = v.allowExec;
  $('#tdKeep').checked = v.keepData;
  $('#tdDeploySql').checked = !!v.deploySql;
  $('#tdAiWrite').checked = !!v.allowAiWrite;
  $('#tdCheckResult').textContent = '';
  syncTestDbForm();
}

function readTestDbForm() {
  const pass = $('#tdPass').value;
  return {
    enabled: $('#tdEnabled').checked,
    server: $('#tdServer').value.trim(), database: $('#tdDatabase').value.trim(),
    user: $('#tdUser').value.trim(), password: pass ? pass : null,
    trustServerCertificate: $('#tdTrust').checked, maxRows: +$('#tdMaxRows').value || 500,
    allowExec: $('#tdAllowExec').checked, keepData: $('#tdKeep').checked, deploySql: $('#tdDeploySql').checked, allowAiWrite: $('#tdAiWrite').checked, anyProject: $('#tdAnyProject').checked,
    syncReadDb: $('#tdSync').checked,
  };
}

// ---------- Hồ sơ DB theo dự án (mẫu VnrConfigTool + login SQL cho AI) ----------
const DP = { view: null, rows: [], removed: [] };

async function loadDbProfiles() {
  try { DP.view = await api('/dbprofiles'); } catch (e) { DP.view = null; toast(e.message, true); }
  DP.removed = [];
  DP.rows = (DP.view?.profiles || []).map(p => ({ project: p.project, orig: p.project, template: p.template || '', text: p.text, aiUser: p.aiUser || '', aiPassword: '', hasAiPassword: p.hasAiPassword }));
  $('#dpDir').value = DP.view?.templateDir || '';
  const c = DP.view?.current;
  $('#dpCurrent').textContent = c ? `Web đang dùng: ${c.database} trên ${c.server} · ${DP.view.templates.length} mẫu trong thư mục` : 'Không đọc được DB web đang dùng (WebSettings.json chưa khai là file cấu hình local?)';
  renderDbProfiles();
}

function renderDbProfiles() {
  const tpls = DP.view?.templates || [];
  const norm = s => (s || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
  $('#dpRows').replaceChildren(...(DP.rows.length ? DP.rows.map(r => {
    const key = norm(r.project);
    // Mẫu khớp dự án (tên mẫu / tên DB chứa mã) lên đầu
    const sorted = [...tpls].sort((a, b) => (key && (norm(b.name) + norm(b.database)).includes(key)) - (key && (norm(a.name) + norm(a.database)).includes(key)));
    const sel = h('select', { class: 'input sm', onchange: e => { r.template = e.target.value; } },
      h('option', { value: '' }, r.template === '' && r.text && r.orig ? `(DB đã lưu: ${r.text})` : '— chọn mẫu —'),
      ...sorted.map(t => h('option', { value: t.name }, `${t.name} — ${t.database}`)));
    sel.value = r.template;
    const out = h('span', { class: 'muted small dp-out' });
    const pass = h('input', { class: 'input sm', type: 'password', autocomplete: 'new-password', placeholder: r.hasAiPassword ? 'đã lưu' : 'mật khẩu', value: r.aiPassword, oninput: e => { r.aiPassword = e.target.value; } });
    return h('div', { class: 'dp-row' },
      h('input', { class: 'input sm mono dp-proj', placeholder: 'ABC', value: r.project, oninput: e => { r.project = e.target.value.trim(); }, onchange: renderDbProfiles }),
      sel,
      h('input', { class: 'input sm dp-user', placeholder: 'login AI (trống = login DB test)', autocomplete: 'off', spellcheck: 'false', value: r.aiUser, oninput: e => { r.aiUser = e.target.value.trim(); } }),
      pass,
      h('button', { class: 'btn sm', type: 'button', onclick: () => checkDbProfile(r, out) }, 'Kiểm tra'),
      r.orig ? h('button', { class: 'btn sm', type: 'button', title: 'Lưu hồ sơ này rồi ghi WebSettings.json + chuyển DB của AI ngay', onclick: () => applyDbProfile(r) }, 'Áp ngay') : null,
      h('button', { class: 'btn sm ghost', type: 'button', title: 'Xoá hồ sơ', onclick: () => { if (r.orig) DP.removed.push(r.orig); DP.rows.splice(DP.rows.indexOf(r), 1); renderDbProfiles(); } }, '✕'),
      out);
  }) : [h('div', { class: 'muted small' }, 'Chưa có hồ sơ nào. Bấm "＋ Thêm dự án", hoặc gõ trong chat AMIS: db ABC → dùng db 1.')]));
}

function dpBody(r) {
  return { project: r.project, template: r.template, aiUser: r.aiUser, aiPassword: r.aiPassword ? r.aiPassword : null };
}

async function checkDbProfile(r, out) {
  out.className = 'muted small dp-out';
  out.textContent = 'Đang kết nối…';
  try {
    const x = await api('/dbprofiles/check', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(dpBody(r)) });
    out.className = 'small dp-out ' + (x.ok ? 'ok-text' : 'bad-text');
    out.textContent = x.info;
  } catch (e) { out.className = 'small dp-out bad-text'; out.textContent = e.message; }
}

async function saveDbProfiles() {
  if ($('#dpDir').value.trim() !== (DP.view?.templateDir || '')) await api('/dbprofiles/dir', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ dir: $('#dpDir').value.trim() }) });
  for (const p of DP.removed) await api('/dbprofiles/' + encodeURIComponent(p), { method: 'DELETE' });
  for (const r of DP.rows) {
    if (!r.project) continue;
    if (r.orig && r.orig.toUpperCase() !== r.project.toUpperCase()) await api('/dbprofiles/' + encodeURIComponent(r.orig), { method: 'DELETE' });
    try { await api('/dbprofiles', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(dpBody(r)) }); }
    catch (e) { throw new Error(`Hồ sơ DB ${r.project}: ${e.message}`); }
  }
  DP.removed = [];
}

async function applyDbProfile(r) {
  if (!await ask(`Áp hồ sơ DB ${r.project}?\nTool ghi chuỗi kết nối vào các WebSettings.json (sao lưu bản cũ), chạm Web.config để IIS nạp lại, và chuyển DB của AI.`)) return;
  try {
    await api('/dbprofiles', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(dpBody(r)) });
    const x = await api('/dbprofiles/' + encodeURIComponent(r.project) + '/apply', { method: 'POST' });
    toast(x.message);
    await loadDbProfiles();
    S.testdb = await api('/testdb'); fillTestDbForm(S.testdb); renderDbPill();
  } catch (e) { toast(e.message, true); }
}

function syncTestDbForm() { $('#tdSub').classList.toggle('disabled', !$('#tdEnabled').checked); }

async function checkTestDb() {
  const out = $('#tdCheckResult');
  out.className = 'muted small';
  out.textContent = 'Đang kết nối…';
  try {
    const r = await api('/testdb/check', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(readTestDbForm()) });
    out.className = 'small ' + (r.ok ? 'ok-text' : 'bad-text');
    out.textContent = (r.ok ? '✓ ' : '✗ ') + r.info;
  } catch (e) { out.className = 'small bad-text'; out.textContent = e.message; }
}

async function restoreSql(j) {
  if (!await ask(`Trả ${j.sqlDeployed.length} store trên DB test ${j.sqlDeployDb} về bản trước khi job deploy?\n${j.sqlDeployed.join('\n')}\n(Store mới tạo sẽ bị xoá.)`)) return;
  try {
    const job = await api(`/jobs/${j.id}/restore-sql`, { method: 'POST' });
    S.jobs.set(job.id, job);
    renderDrawer();
    render();
    toast('Đã khôi phục store trên DB test.');
  } catch (e) { toast(e.message, true); }
}

async function cleanupData(id) {
  if (!await ask('Chạy test-cleanup.sql để xoá dữ liệu test của job này?')) return;
  try {
    const r = await api(`/jobs/${id}/cleanup-data`, { method: 'POST' });
    toast(r.message);
  } catch (e) { toast(e.message, true); }
}

// ---------- Chế độ tự động ----------
function fillAutoForm() {
  const o = (S.autopilot && S.autopilot.options) || {};
  $('#apEnabled').checked = !!o.enabled;
  $('#apInterval').value = String(o.intervalMinutes || 15);
  if (!$('#apInterval').value) $('#apInterval').value = '15';
  document.querySelectorAll('input[name=apType]').forEach(c => { c.checked = (o.taskTypes || ['TotalTaskDue']).includes(c.value); });
  $('#apAnalyze').checked = !!o.autoAnalyze;
  $('#apMatchBranch').checked = o.onlyMatchingBranch !== false;
  $('#apProjects').value = o.projects || '';
  $('#apMax').value = o.maxPerRun || 3;
  syncAutoForm();
  renderAutoStatus();
}

function readAutoForm() {
  return {
    enabled: $('#apEnabled').checked,
    intervalMinutes: +$('#apInterval').value,
    taskTypes: [...document.querySelectorAll('input[name=apType]:checked')].map(c => c.value),
    autoAnalyze: $('#apAnalyze').checked,
    onlyMatchingBranch: $('#apMatchBranch').checked,
    projects: $('#apProjects').value.trim(),
    maxPerRun: +$('#apMax').value || 3,
  };
}

/** Làm mờ các tuỳ chọn con khi tuỳ chọn cha đang tắt. */
function syncAutoForm() {
  $('#apSub').classList.toggle('disabled', !$('#apEnabled').checked);
  $('#apAnalyzeSub').classList.toggle('disabled', !$('#apEnabled').checked || !$('#apAnalyze').checked);
}

function timeText(s) { const d = parseDate(s); return d ? d.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit' }) : ''; }

function renderAutoStatus() {
  const st = S.autopilot;
  if (!st) return;
  $('#apStatus').textContent = st.running ? 'Đang chạy…'
    : [st.lastRun ? `Lần chạy gần nhất ${timeText(st.lastRun)}` : 'Chưa chạy lần nào', st.nextRun ? `lần tới ${timeText(st.nextRun)}` : null].filter(Boolean).join(' · ');
  $('#apLog').replaceChildren(...(st.actions.length ? st.actions.map(a =>
    h('div', { class: 'ap-row' + (a.error ? ' bad-text' : '') }, h('span', { class: 'muted' }, timeText(a.at)), ' ', a.text))
    : [h('div', { class: 'muted' }, 'Chưa có hoạt động.')]));
}

function renderAutoPill() {
  const st = S.autopilot;
  const pill = $('#btnAuto');
  const o = st && st.options;
  const on = !!(o && o.enabled);
  pill.classList.toggle('on', on);
  pill.classList.toggle('busy', !!(st && st.running));
  $('.auto-text', pill).textContent = !on ? 'Tự động: tắt'
    : st.running ? 'Tự động: đang chạy…'
      : `Tự động${o.autoAnalyze ? ' + giao AI' : ''} · ${st.nextRun ? 'lần tới ' + timeText(st.nextRun) : ''}`;
  pill.title = on
    ? `Làm mới mỗi ${o.intervalMinutes} phút${o.autoAnalyze ? ', tự giao task mới cho AI phân tích' : ''}. ${st.actions[0] ? 'Gần nhất: ' + st.actions[0].text : ''}\nBấm để cấu hình.`
    : 'Chế độ tự động đang tắt — bấm để cấu hình';
}

async function runAutoNow() {
  try {
    await api('/autopilot/run', { method: 'POST' });
    toast('Đang chạy một lượt tự động (theo cấu hình đã lưu)…');
  } catch (e) { toast(e.message, true); }
}

async function testClaude() {
  const out = $('#testClaudeResult');
  const btn = $('#btnTestClaude');
  btn.disabled = true;
  out.replaceChildren(h('span', { class: 'muted' }, 'Đang gọi Claude…'));
  try {
    const r = await api('/settings/test-claude', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ authMode: authMode(), apiKey: $('#setApiKey').value.trim() || null, model: $('#setModel').value.trim() || null }),
    });
    out.replaceChildren(r.ok
      ? h('span', { class: 'ok-text' }, `✓ Kết nối được (${r.auth})${r.info ? ' — ' + r.info.replace(/^Phiên Claude \S+ — /, '') : ''}`)
      : h('span', { class: 'bad-text' }, `✗ ${r.result}`));
  } catch (e) {
    out.replaceChildren(h('span', { class: 'bad-text' }, e.message));
  } finally { btn.disabled = false; }
}

// ---------- Session ----------
function openSessionDialog() {
  $('#inpSession').value = '';
  $('#dlgSession').showModal();
  $('#inpSession').focus();
}

async function saveSession() {
  const sid = $('#inpSession').value.trim();
  if (!sid) return;
  try {
    await api('/session', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sessionId: sid }) });
    $('#dlgSession').close();
    toast('Đã lưu session AMIS');
    S.tasks = {};
    await Promise.all([loadMe(), loadTasks()]);
  } catch (e) { toast(e.message, true); }
}

// ---------- Realtime ----------
function connectEvents() {
  const conn = $('#conn');
  const es = new EventSource('/api/events');
  es.onopen = () => { conn.className = 'conn on'; conn.title = 'Đang nhận cập nhật realtime'; loadJobs().catch(() => {}); };
  es.onerror = () => { conn.className = 'conn off'; conn.title = 'Mất kết nối realtime — đang thử lại'; };
  es.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.kind === 'repo') {
      loadRepoSoon();
    } else if (m.kind === 'tasks') {
      // Chế độ tự động vừa làm mới danh sách từ AMIS
      S.tasks[m.type] = m.list;
      S.tasksError[m.type] = null;
      render();
    } else if (m.kind === 'autopilot') {
      S.autopilot = m.state;
      renderAutoPill();
      if ($('#dlgSettings').open) renderAutoStatus();
    } else if (m.kind === 'chat') {
      S.chats.set(m.taskId, { thread: m.thread, busy: m.busy });
      if (S.chatTask && S.chatTask.taskId === m.taskId) updateChat();
    } else if (m.kind === 'jobRemoved') {
      S.jobs.delete(m.id);
      if (S.openJob === m.id) closeDrawer();
      render();
    } else if (m.kind === 'job') {
      const prev = S.jobs.get(m.job.id);
      S.jobs.set(m.job.id, m.job);
      renderBriefBadge();
      if (m.job.status === 'WaitingForUser' && prev && prev.status !== 'WaitingForUser') {
        toast('AI cần bạn xác nhận: ' + m.job.taskName);
      }
      if (S.openJob === m.job.id) { renderDrawer(); updateChat(); }
      render();
      loadRepoSoon(); // pipeline tạo/trả nhánh trong lúc chạy
    } else if (m.kind === 'log') {
      const j = S.jobs.get(m.jobId);
      if (j && m.entry.kind !== 'stderr' && m.entry.kind !== 'tool-error') j.lastLog = m.entry.text.slice(0, 200);
      pushLive(m.jobId, m.entry);
      if (S.openJob === m.jobId) appendLog(m.entry);
      scheduleLive(m.jobId);
    }
  };
}

// ---------- Khởi động ----------
function wire() {
  $('#btnSession').addEventListener('click', openSessionDialog);
  $('#btnSettings').addEventListener('click', () => openSettings());
  $('#btnCosts').addEventListener('click', openCosts);
  $('#btnBrief').addEventListener('click', openBrief);
  $('#kitTarget').addEventListener('change', loadKit);
  $('#tfsAutoPr').addEventListener('change', e => saveTfs({ autoPr: e.target.checked }));
  $('#btnTfsSave').addEventListener('click', async () => {
    const v = $('#tfsPat').value.trim();
    if (!v) { toast('Nhập PAT trước', true); return; }
    if (await saveTfs({ pat: v })) { $('#tfsPat').value = ''; toast('Đã lưu PAT'); testTfs(); }
  });
  $('#btnTfsClear').addEventListener('click', async () => {
    if (!await ask('Xoá PAT? Tool sẽ dùng đăng nhập Windows của máy.')) return;
    if (await saveTfs({ clearPat: true })) toast('Đã xoá PAT');
  });
  $('#btnTfsTest').addEventListener('click', testTfs);
  $('#btnCbSave').addEventListener('click', () => saveChatBot());
  $('#cbEnabled').addEventListener('change', () => saveChatBot());
  $('#btnCbTest').addEventListener('click', async () => {
    try { await api('/amischat/test', { method: 'POST' }); toast('Đã gửi tin thử — xem nhóm AMIS Chat'); }
    catch (e) { toast(e.message, true); }
  });
  $('#btnLessonAdd').addEventListener('click', async () => {
    const t = $('#lessonNew');
    if (await saveLesson(null, t.value, true)) t.value = '';
  });
  $('#btnKitInstall').addEventListener('click', () => kitInstall(null, false));
  $('#btnKitUpdate').addEventListener('click', () => kitInstall(null, true));
  $('#btnKitPack').addEventListener('click', kitPackage);
  $('#btnKitImport').addEventListener('click', () => $('#kitImportFile').click());
  $('#kitImportFile').addEventListener('change', e => { const f = e.target.files[0]; e.target.value = ''; kitImport(f); });
  for (const b of $$('#costDays button')) b.addEventListener('click', () => { COSTS.days = Number(b.dataset.v); try { localStorage.setItem('costDays', b.dataset.v); } catch { } loadCosts(); });
  for (const b of $$('#costPaid button')) b.addEventListener('click', () => { COSTS.paid = b.dataset.v; loadCosts(); });
  for (const b of $$('#costTabs button')) b.addEventListener('click', () => { COSTS.tab = b.dataset.v; try { localStorage.setItem('costTab', b.dataset.v); } catch { } renderCostsView(); });
  setupSettingsTabs();
  applyTheme(currentTheme());
  $('#btnTheme').addEventListener('click', cycleTheme);
  $('#btnSaveSettings').addEventListener('click', saveSettings);
  $('#btnScanRepos').addEventListener('click', () => scanRepos());
  $('#setWorkspace').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); scanRepos(); } });
  $('#setWorkspace').addEventListener('change', () => scanRepos());
  $('#setRepo').addEventListener('change', () => { updateRepoPath(); renderExtraRepos(); renderLocalFiles(); });
  $('#setExtraRepos').addEventListener('change', renderLocalFiles);
  $('#btnTestClaude').addEventListener('click', testClaude);
  $('#btnToggleKey').addEventListener('click', () => { const i = $('#setApiKey'); i.type = i.type === 'password' ? 'text' : 'password'; });
  $('#btnAddKey').addEventListener('click', addTypedKey);
  $('#setApiKey').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); addTypedKey(); } });
  $('#btnTestKeys').addEventListener('click', testAllKeys);
  document.querySelectorAll('input[name=authMode]').forEach(r => r.addEventListener('change', updateAuthUI));
  $('#repo').addEventListener('click', openBranchDialog);
  $('#btnFetch').addEventListener('click', fetchRemote);
  $('#inpBranch').addEventListener('input', renderBranchList);
  $('#inpBranch').addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
  document.querySelectorAll('#branchFilter button').forEach(b =>
    b.addEventListener('click', () => { S.branchFilter = b.dataset.f; renderBranchList(); }));
  // Bắt thay đổi làm ngoài tool (đổi nhánh trong VS, commit…) khi người dùng quay lại tab
  document.addEventListener('visibilitychange', () => { if (!document.hidden) loadRepo(); });
  setInterval(() => { if (!document.hidden) loadRepo(); }, 20000);
  $('#btnSaveSession').addEventListener('click', saveSession);
  $('#inpSession').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); saveSession(); } });
  $('#btnStart').addEventListener('click', submitStart);
  $('#btnAuto').addEventListener('click', () => openSettings('autoGroup'));
  $('#btnApRun').addEventListener('click', runAutoNow);
  document.querySelectorAll('input[name="teMode"]').forEach(r => r.addEventListener('change', syncTestForm));
  $('#teAccAdd').addEventListener('click', () => { const r = teAccRow(); $('#teAccounts').append(r); r.querySelector('.acc-role').focus(); });
  $('#tdEnabled').addEventListener('change', syncTestDbForm);
  $('#btnTdCheck').addEventListener('click', checkTestDb);
  $('#btnDpAdd').addEventListener('click', () => { DP.rows.push({ project: '', orig: '', template: '', aiUser: '', aiPassword: '', hasAiPassword: false }); renderDbProfiles(); $('#dpRows .dp-row:last-child .dp-proj')?.focus(); });
  $('#btnDb').addEventListener('click', () => openSettings('dbGroup'));
  loadTestDb();
  loadHealth();
  setInterval(() => { if (!document.hidden) loadHealth(); }, 60000);
  api('/testenv').then(v => { S.testMode = v.enabled === false ? 'off' : v.uiTests ? 'full' : 'data'; }).catch(() => {});
  setInterval(loadTestDb, 60000); // ro.local.json có thể được sửa tay
  ['apEnabled', 'apAnalyze'].forEach(id => $('#' + id).addEventListener('change', syncAutoForm));
  api('/autopilot').then(st => { S.autopilot = st; renderAutoPill(); }).catch(() => {});
  setInterval(renderAutoPill, 30000);
  $('#btnChatTask').addEventListener('click', () => {
    const p = S.pendingStart;
    if (!p) return;
    const t = { taskId: p.task.taskId, taskName: (p.detail && p.detail.taskName) || p.task.taskName };
    $('#dlgStart').close();
    openTaskChat(t);
  });
  $('#dlgStart').addEventListener('close', () => { S.pendingStart = null; });
  $('#drawer [data-close]').addEventListener('click', closeDrawer);
  document.querySelectorAll('#drawer .tabs button').forEach(b =>
    b.addEventListener('click', () => { S.drawerTab = b.dataset.tab; showDrawerTab(); }));
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && !$('#drawer').hidden && !document.querySelector('dialog[open]')) closeDrawer(); });
}

(async function main() {
  wire();
  renderMe();
  render();
  api('/config').then(c => { S.config = c; }).catch(() => {});
  connectEvents();
  await Promise.all([loadMe(), loadRepo(), loadTasks(), loadJobs().catch(e => toast(e.message, true))]);
  maybeAutoBrief();
})();
