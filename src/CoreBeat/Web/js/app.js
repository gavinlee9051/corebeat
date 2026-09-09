/* ===================================================================
   芯跳 CoreBeat · M2 前端逻辑
   1) 原生桥：标题栏拖动/窗口命令
   2) 「一次心跳」启动时序 → 主界面入场
   3) 数据：先仿真占位，收到原生 t:"tick" 后切换为真实传感器数据
   原生→前端协议：{t:"tick", cpu, freqGhz, mem{usedGB,totalGB,pct}, uptimeSec,
                    temps[{label,v,pct}], net{downMbps,upMbps}|null,
                    procs[{pid,name,cpu,memMB,icon}], awake{level,label,remainSec}}
                  {t:"awake", ...}
   前端→原生：{cmd:"drag|minimize|maximize|close|quit|ping"}
   =================================================================== */
"use strict";

const $ = (id) => document.getElementById(id);
const webview = window.chrome?.webview;

function send(cmd, extra) {
  if (!webview) return;
  try { webview.postMessage(JSON.stringify(Object.assign({ cmd }, extra || {}))); }
  catch (_) { /* ignore */ }
}

/* ---- 前端错误上报（用户事件上下文可达；注入上下文内可能被宿主抑制） ---- */
window.addEventListener("error", (ev) =>
  send("jslog", { text: `ERR ${ev.message} @${ev.lineno}:${ev.colno}` }));
window.addEventListener("unhandledrejection", (ev) =>
  send("jslog", { text: `PROMISE ${String(ev.reason)}` }));

/* ---------------- 状态 ---------------- */
const S = { live: false, upReal: false };
const R = {
  cpuCur: 24, cpuTgt: 24,
  memCur: 6.1, memTgt: 6.1, memTotal: 32,
  tempCur: 43, tempTgt: 43, tempLabel: "CPU 温度",
  gpuCur: 2, gpuTgt: 2, gpuName: "", gpuAvail: false,
  cores: [], freqGhz: null,
  upSec: 0,
  temps: [],            // 最近的真实温度行 [{label,v,pct}]
};
let selfPid = 0;
const diskBuf = [], netBufD = [], netBufU = [];
const START_TS = Date.now();
const MAX_PROCS = 6;
let uiTickN = 0;        // 前端实际收到的实时 tick 数
let uiRawCpu = null;    // 最近一次 tick 的原始 CPU 值

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const mix = (a, b, f) => [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
const rgb = (c) => `rgb(${Math.round(c[0])},${Math.round(c[1])},${Math.round(c[2])})`;

/* 分段渐变取色：v 在 stops 阈值间线性插值 */
function grad(v, stops) {
  if (v <= stops[0].t) return stops[0].c;
  for (let i = 0; i < stops.length - 1; i++) {
    const a = stops[i], b = stops[i + 1];
    if (v <= b.t) return mix(a.c, b.c, (v - a.t) / (b.t - a.t));
  }
  return stops[stops.length - 1].c;
}
const CPU_STOPS = [
  { t: 30, c: [107, 124, 255] }, { t: 70, c: [255, 180, 84] }, { t: 90, c: [255, 93, 93] },
];
const loadColor = (load) => grad(clamp(load, 0, 100), CPU_STOPS);
const tempColor = (t) => grad(clamp(t, 35, 95), [
  { t: 48, c: [107, 124, 255] }, { t: 70, c: [255, 180, 84] }, { t: 90, c: [255, 93, 93] },
]);

/* ---------------- 原生桥：接收 ---------------- */
function onNativeMessage(msg) {
  if (!msg || typeof msg !== "object") return;
  if (msg.t === "tick") applyTick(msg);
  else if (msg.t === "fast") onFast(msg);
  else if (msg.t === "awake") onAwake(msg);
  else if (msg.t === "killResult") onKillResult(msg);
  else if (msg.t === "history") showHistory(msg);
  else if (msg.t === "theme") setSoftwareTheme(msg.mode);
  else if (msg.t === "cards") applyCardConfig(msg.show);
  else if (msg.t === "cleanInfo") setCleanInfo(msg);
  else if (msg.t === "cleanDone") onCleanDone(msg);
  else if (msg.t === "st") onSpeedTest(msg);
}

/* 4Hz 快速 CPU：显示瞬时值（与任务管理器同样跟手） */
function onFast(m) {
  if (typeof m.cpu !== "number") return;
  const cpu = clamp(m.cpu, 0, 100);
  R.cpuTgt = cpu;
  R.cpuCur = cpu;
  updateCpuDisplay();
}

// 通道 A：原生 ExecuteScriptAsync 注入（window.cbOnNative）
window.cbOnNative = (obj) => {
  try { onNativeMessage(obj); }
  catch (err) { try { send("jslog", { text: `native-inj-fail ${err.message}` }); } catch (_) { } }
};
// 通道 B：原生 PostWebMessageAsJson（兼容保留）
webview?.addEventListener("message", (e) => {
  let msg = e.data;
  if (typeof msg === "string") { try { msg = JSON.parse(msg); } catch (_) { return; } }
  onNativeMessage(msg);
});

function applyTick(m) {
  const first = !S.live;
  S.live = true;
  uiTickN++;
  if (typeof m.cpu === "number") uiRawCpu = m.cpu;

  // 仿真 → 实时切换
  if (first) {
    $("modeTag").textContent = "实时数据 · 1s 刷新";
    $("procSub").textContent = "全部进程 · 可滚动 · 每 1s 更新";
    $("ecgCaption").innerHTML = "已接入真实传感器与进程数据 —— 波形随真实负载“呼吸”";
    diskBuf.length = 0;               // 磁盘速率 M2 暂缺：清空仿真数据
    document.body.classList.add("live");
    send("jslog", { text: `first-tick cpu=${m.cpu} procs=${Array.isArray(m.procs) ? m.procs.length : "?"} temps=${Array.isArray(m.temps) ? m.temps.length : "?"}` });
  }

  if (typeof m.cpu === "number") R.cpuTgt = clamp(m.cpu, 0, 100);
  if (Array.isArray(m.cores)) R.cores = m.cores;
  if (typeof m.freqGhz === "number") R.freqGhz = m.freqGhz;
  if (m.mem) {
    R.memTgt = clamp(m.mem.usedGB, 0, m.mem.totalGB || 64);
    R.memTotal = m.mem.totalGB || R.memTotal;
  }
  if (m.gpu && typeof m.gpu.pct === "number") {
    R.gpuTgt = clamp(m.gpu.pct, 0, 100);
    R.gpuName = m.gpu.name || "GPU";
    R.gpuAvail = true;
  } else if (first) {
    R.gpuAvail = false;
  }
  if (Array.isArray(m.temps) && m.temps.length > 0) {
    R.temps = m.temps;
    R.tempTgt = clamp(m.temps[0].v, 0, 120);
    R.tempLabel = m.temps[0].label;
  } else if (first) {
    R.temps = [];
    R.tempTgt = NaN;
  }
  if (typeof m.uptimeSec === "number") { R.upSec = m.uptimeSec; S.upReal = true; }

  // 磁盘读写（真实 MB/s）
  if (m.disk && typeof m.disk.readMB === "number") {
    diskBuf.push(m.disk.readMB); if (diskBuf.length > 70) diskBuf.shift();
    $("ioDiskVal").textContent = `读 ${m.disk.readMB.toFixed(2)} MB/s · 写 ${m.disk.writeMB.toFixed(2)} MB/s`;
  } else if (first) {
    diskBuf.length = 0;
    $("ioDiskVal").textContent = "读 — · 写 —";
  }

  // 网络 sparkline（真实）
  if (m.net) {
    R.netD = m.net.downMbps; R.netU = m.net.upMbps;
    netBufD.push(m.net.downMbps); if (netBufD.length > 70) netBufD.shift();
    netBufU.push(m.net.upMbps); if (netBufU.length > 70) netBufU.shift();
    $("ioNetVal").textContent = `↓ ${m.net.downMbps.toFixed(2)} Mbps · ↑ ${m.net.upMbps.toFixed(2)} Mbps`;
  } else {
    $("ioNetVal").textContent = "↓ — · ↑ —";
  }

  if (Array.isArray(m.procs)) {
    for (const p of m.procs) if (p.isSelf) selfPid = p.pid;
    renderApps(m.procs);
  }

  if (m.awake) onAwake(m.awake);
  updateCpuExtras();
}

function onAwake(a) {
  const tag = $("awakeTag");
  if (!a || a.level === 0) { tag.style.display = "none"; tag.textContent = ""; return; }
  let text = a.label || "防睡眠";
  if (typeof a.remainSec === "number" && a.remainSec > 0) {
    const mm = String(Math.floor(a.remainSec / 60)).padStart(2, "0");
    const ss = String(a.remainSec % 60).padStart(2, "0");
    text += ` · ${mm}:${ss}`;
  }
  tag.textContent = text;
  tag.style.display = "inline-block";
}

/* ---------------- 窗口拖动：标题栏 + 任意空白区（交互控件除外，逐帧上报屏幕坐标） ---------------- */
let dragSeed = null;
let dragging = false;
let lastDragAt = 0;
const DRAG_SKIP = "[data-nodrag], [data-dragcard], button, input, select, textarea, a, li[data-key], .modal, .toasts, .bubble, .love, .splash, .tb-actions";
document.addEventListener("pointerdown", (e) => {
  if (e.button !== 0) return;
  if (e.target.closest(DRAG_SKIP)) return;
  dragSeed = { x: e.screenX, y: e.screenY };
  dragging = false;
});
document.addEventListener("pointermove", (e) => {
  if (!dragSeed) return;
  if (!dragging) {
    if (Math.hypot(e.screenX - dragSeed.x, e.screenY - dragSeed.y) <= 4) return;
    dragging = true;
    send("dragBegin", { x: e.screenX, y: e.screenY });
    return;
  }
  const now = performance.now();
  if (now - lastDragAt < 12) return;      // ~80Hz 限频
  lastDragAt = now;
  send("dragMove", { x: e.screenX, y: e.screenY });
});
document.addEventListener("pointerup", () => {
  if (dragging) send("dragEnd");
  dragSeed = null;
  dragging = false;
});
document.querySelectorAll("[data-cmd]").forEach((btn) =>
  btn.addEventListener("click", () => send(btn.dataset.cmd)));

/* ---------------- 仿真（真实 tick 到达前占位） ---------------- */
function wanderSim() {
  if (S.live) return;
  R.cpuTgt = clamp(R.cpuTgt + (Math.random() * 16 - 8), 14, 74);
  R.memTgt = clamp(5.7 + R.cpuTgt / 14 + Math.random() * 1.1, 5.4, 12.5);
  R.tempTgt = clamp(32 + R.cpuTgt * 0.44 + Math.random() * 2.5, 33, 76);
  R.gpuTgt = clamp(R.cpuTgt * 0.35 + Math.random() * 4, 0.5, 24);

  diskBuf.push(6 + Math.random() * 34); if (diskBuf.length > 70) diskBuf.shift();
  netBufD.push(0.4 + Math.random() * 3.6); if (netBufD.length > 70) netBufD.shift();
  netBufU.push(0.08 + Math.random() * 0.7); if (netBufU.length > 70) netBufU.shift();
}
setInterval(wanderSim, 1300);

/* 平滑显示推进（110ms，数字滚动感；k 较大使数值更跟手） */
setInterval(() => {
  if (document.hidden) return;
  const k = 0.3;
  R.cpuCur += (R.cpuTgt - R.cpuCur) * k;
  R.memCur += (R.memTgt - R.memCur) * k;
  R.gpuCur += (R.gpuTgt - R.gpuCur) * k;
  if (!Number.isNaN(R.tempTgt)) R.tempCur += (R.tempTgt - R.tempCur) * k;
  renderNumbers();
}, 110);

/* ---------------- 体征/仪表渲染 ---------------- */
const C_RING = 163.4;   // 2π·26

function renderNumbers() {
  const cpu = R.cpuCur;
  const col = loadColor(cpu);
  updateCpuDisplay();

  // 内存
  $("memText").textContent = R.memCur.toFixed(1);
  const memPct = clamp(R.memCur / Math.max(1, R.memTotal) * 100, 0, 100);
  $("memBar").style.width = memPct.toFixed(1) + "%";
  $("memSub").textContent = `共 ${R.memTotal.toFixed(0)} GB · ${Math.round(memPct)}%`;

  // 顶部磁贴：网速（上/下行都显示）
  const nd = R.netD || 0, nu = R.netU || 0;
  const nel = $("netVal");
  if (nel) {
    nel.innerHTML = "↓ " + (nd >= 100 ? nd.toFixed(0) : nd.toFixed(1)) + "<em>Mbps</em>";
    nel.classList.remove("hot", "hotter");
    const uel = $("netUp");
    if (uel) uel.innerHTML = "↑ " + (nu >= 100 ? nu.toFixed(0) : nu.toFixed(1)) + "<em>Mbps</em>";
    $("netBar").style.width = clamp(nd, 0, 100) + "%";
    $("netBar").style.background = "linear-gradient(90deg, #6b7cff, #4ea1ff)";
  }

  // GPU 占用
  const gpuEl = $("gpuText");
  if (S.live && !R.gpuAvail) {
    gpuEl.innerHTML = "—<em>%</em>";
    $("gpuBar").style.width = "0%";
    $("gpuSub").textContent = "传感器不可读";
  } else {
    const gg = S.live ? R.gpuCur : Math.max(0, R.cpuCur * 0.4 + 2);
    const gCol = loadColor(gg);
    gpuEl.innerHTML = `${Math.round(gg)}<em>%</em>`;
    $("gpuBar").style.width = clamp(gg, 0, 100) + "%";
    $("gpuBar").style.background = `linear-gradient(90deg, rgba(43,245,196,.85), ${rgb(gCol)})`;
    $("gpuSub").textContent = S.live ? (R.gpuName || "GPU") : "演示数据";
  }

  // 运行时长
  if (S.upReal) {
    $("upText").textContent = fmtClock(R.upSec);
    $("upSub").textContent = "系统已运行";
  } else {
    $("upText").textContent = fmtClock(Math.floor((Date.now() - START_TS) / 1000));
    $("upSub").textContent = "本会话时长（演示）";
  }

  // 负载徽章
  const badge = $("loadBadge");
  const col2 = loadColor(cpu);
  badge.textContent = cpu >= 68 ? "负载较高" : cpu >= 45 ? "轻快运转" : "平稳";
  badge.style.color = rgb(col2);
  badge.style.background = `rgba(${col2[0]},${col2[1]},${col2[2]},.12)`;
  badge.style.borderColor = `rgba(${col2[0]},${col2[1]},${col2[2]},.3)`;

  // 温度仪表盘
  renderTempRows();
  renderGauge();

  updateCpuExtras();
  drawSparks();
}

/* CPU 环形/大数字/徽章（4Hz 快速通道直接调用，保证瞬时跟手） */
function updateCpuDisplay() {
  const cpu = R.cpuCur;
  const col = loadColor(cpu);
  $("cpuRingFg").style.strokeDashoffset = String(C_RING * (1 - clamp(cpu, 0, 100) / 100));
  $("cpuRingFg").style.stroke = rgb(col);
  $("cpuPctText").textContent = Math.round(cpu);
  const chip = $("cpuChip");
  chip.textContent = S.live ? (cpu >= 68 ? "高负载" : cpu >= 40 ? "活跃" : "平稳") : "唤醒中…";
  chip.style.color = rgb(col);

  const badge = $("loadBadge");
  badge.textContent = cpu >= 68 ? "负载较高" : cpu >= 45 ? "轻快运转" : "平稳";
  badge.style.color = rgb(col);
  badge.style.background = `rgba(${col[0]},${col[1]},${col[2]},.12)`;
  badge.style.borderColor = `rgba(${col[0]},${col[1]},${col[2]},.3)`;
}

/* CPU 频率 + 逐物理核占用柱 */
function updateCpuExtras() {
  const fs = $("cpuFreqSub");
  if (!fs) return;
  fs.textContent = R.freqGhz ? `${R.freqGhz.toFixed(2)} GHz` : "频率 —";
  const host = $("cores");
  const n = R.cores.length;
  while (host.childElementCount < n) host.appendChild(document.createElement("i"));
  while (host.childElementCount > n) host.lastChild.remove();
  R.cores.forEach((v, i) => {
    const bar = host.children[i];
    if (typeof v !== "number" || Number.isNaN(v)) { bar.style.background = "rgba(255,255,255,.07)"; return; }
    const c = loadColor(v);
    const alpha = v > 4 ? 0.95 : 0.35;
    bar.style.background = `rgba(${c[0]},${c[1]},${c[2]},${alpha})`;
  });
}

function fmtClock(totalSec) {  totalSec = Math.max(0, Math.floor(totalSec));
  const h = Math.floor(totalSec / 3600), m = Math.floor(totalSec / 60) % 60, s = totalSec % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}

function renderGauge() {
  const gn = $("gaugeNum");
  const t = R.tempCur;
  if (Number.isNaN(t) || (R.temps.length === 0 && S.live)) {
    gn.innerHTML = "—<em></em>";
    $("gaugeFg").style.strokeDashoffset = "144";
    gn.style.color = "var(--text-faint)";
    return;
  }
  const frac = clamp((t - 25) / 70, 0, 1);
  const c = tempColor(t);
  $("gaugeFg").style.strokeDashoffset = String(144 * (1 - frac));
  $("gaugeFg").style.stroke = rgb(c);
  gn.innerHTML = `${Math.round(t)}<em>°</em>`;
  gn.style.color = rgb(c);
}

/* 温度行（CPU 温度 / NVMe / GPU…），按 label 缓存节点避免重建闪烁 */
const rowCache = new Map();
function renderTempRows() {
  const host = $("tempRows");
  host.querySelectorAll(".mrow").forEach((n) => n.remove());

  const temps = S.live && R.temps.length === 0 ? [] : (S.live ? R.temps : [
    { label: R.tempLabel, v: R.tempCur, pct: clamp(R.tempCur, 0, 100) },
  ]);
  if (temps.length === 0) {
    host.innerHTML = '<div class="temp-empty">传感器暂不可读（可尝试管理员身份重启解锁全部温度）</div>';
    return;
  }

  host.innerHTML = "";
  for (const row of temps) {
    const div = document.createElement("div");
    div.className = "mrow";
    const bar = document.createElement("i");
    bar.className = "mbar";
    const b = document.createElement("b");
    bar.appendChild(b);
    div.appendChild(el("span", "mname", row.label));
    div.appendChild(bar);
    const val = el("span", "mval", `${Math.round(row.v)}°`);
    div.appendChild(val);
    host.appendChild(div);
    // 下一帧应用宽度以触发过渡
    requestAnimationFrame(() => { b.style.width = clamp(row.pct, 0, 100) + "%"; });
  }

  // 如实说明：CPU 温度在本机传感器栈不可用；NVMe 温度需管理员权限读取
  if (S.live) {
    const hasCpu = temps.some((r) => r.label.includes("CPU"));
    const hasNvme = temps.some((r) => r.label.includes("NVMe"));
    if (!hasCpu) {
      const note = el("div", "temp-empty",
        "CPU 温度：本机暂未通过 LibreHardwareMonitor 提供（Ring0 驱动未加载），如实显示 —");
      host.appendChild(note);
    }
    if (!hasNvme) {
      const note = el("div", "temp-empty", "NVMe 温度需管理员权限（托盘可一键提权重启）");
      host.appendChild(note);
    }
  }
}

function el(tag, cls, text) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined) n.textContent = text;
  return n;
}

/* ---------------- 运行软件（按 exe 名归并，如 chrome 的多进程=1 行） ---------------- */
let appOrder = [];                 // 稳定顺序（key = exe 小写名）
const appIconUrl = new Map();      // key -> data:url
const appNodes = new Map();        // key -> {count, cpu, mem, bar, nameEl, li}
const appInfo = new Map();         // key -> 最近一次聚合 {key,name,pids[],cpu,mem,icon,self,count}

function buildGroups(list) {
  const map = new Map();
  for (const p of list) {
    const key = (p.name || "?").toLowerCase();
    let g = map.get(key);
    if (!g) {
      g = { key, name: p.name || key, pids: [], cpu: 0, mem: 0, icon: p.icon, self: false, count: 0 };
      map.set(key, g);
    }
    g.pids.push(p.pid);
    g.cpu += p.cpu;
    g.mem += p.memMB;
    g.count++;
    if (p.isSelf) g.self = true;
    if (!g.icon && p.icon) g.icon = p.icon;
  }
  return [...map.values()].sort((a, b) => b.cpu - a.cpu);
}

function renderApps(list) {
  const host = $("procs");
  const groups = buildGroups(list);

  // 首次：全量建行（顺序稳定）
  if (appOrder.length === 0 && groups.length > 0) {
    host.innerHTML = "";
    appNodes.clear();
    appOrder = groups.map((g) => g.key);
    for (const g of groups) {
      appInfo.set(g.key, g);
      host.appendChild(buildAppRow(g));
    }
    applyFilter();
    return;
  }
  if (appOrder.length === 0) {
    host.innerHTML = '<li class="procs-empty">正在获取运行软件…</li>';
    return;
  }

  const byKey = new Map(groups.map((g) => [g.key, g]));

  // 更新既有行（不重排），退出则计数归零直至移除
  const toRemove = [];
  for (const key of appOrder) {
    const nodes = appNodes.get(key);
    if (!nodes) { toRemove.push(key); continue; }
    const g = byKey.get(key);
    if (!g) {
      nodes.absent = (nodes.absent || 0) + 1;
      nodes.cpu.textContent = "0.0%";
      nodes.mem.textContent = "0 MB";
      nodes.bar.style.width = "0%";
      nodes.nameEl.classList.add("exited");
      if (nodes.absent >= 2) toRemove.push(key);
      continue;
    }
    nodes.absent = 0;
    nodes.nameEl.classList.remove("exited");
    appInfo.set(key, g);
    const cpuTxt = `${g.cpu.toFixed(1)}%`;
    if (nodes.cpu.textContent !== cpuTxt) nodes.cpu.textContent = cpuTxt;
    const memTxt = `${Math.round(g.mem)} MB`;
    if (nodes.mem.textContent !== memTxt) nodes.mem.textContent = memTxt;
    nodes.bar.style.width = clamp(Math.max(g.cpu, 0.4), 0, 100) + "%";
    if (nodes.countEl && nodes.countEl.textContent !== (g.count > 1 ? `×${g.count}` : ""))
      nodes.countEl.textContent = g.count > 1 ? `×${g.count}` : "";
  }

  if (toRemove.length) {
    for (const key of toRemove) {
      const n = appNodes.get(key);
      if (n && n.li) n.li.remove();
      appNodes.delete(key);
    }
    appOrder = appOrder.filter((k) => !toRemove.includes(k));
  }

  // 新出现的软件追加到末尾
  const known = new Set(appOrder);
  for (const g of groups) {
    if (known.has(g.key)) continue;
    appOrder.push(g.key);
    appInfo.set(g.key, g);
    host.appendChild(buildAppRow(g));
    known.add(g.key);
  }
  applyFilter();
}

function buildAppRow(g) {
  const li = document.createElement("li");
  li.dataset.key = g.key;
  if (g.self) li.classList.add("self");

  const pico = document.createElement("span");
  pico.className = "pico";
  if (g.icon) {
    let url = appIconUrl.get(g.key);
    if (!url) { url = `data:image/png;base64,${g.icon}`; appIconUrl.set(g.key, url); }
    const img = document.createElement("img");
    img.src = url;
    img.alt = "";
    pico.appendChild(img);
  } else {
    pico.style.setProperty("--c", hashColor(g.name));
    pico.textContent = (g.name[0] || "?").toUpperCase();
  }

  const nameEl = el("span", "pname");
  nameEl.textContent = g.name;
  const countEl = document.createElement("i");
  countEl.className = "pcount";
  countEl.textContent = g.count > 1 ? `×${g.count}` : "";
  nameEl.appendChild(countEl);

  const cpuCell = el("span", "pstat", "CPU ");
  const cpuB = document.createElement("b");
  cpuCell.appendChild(cpuB);
  const memCell = el("span", "pstat prow-mem");
  const memB = document.createElement("b");
  memCell.appendChild(memB);
  const pbar = document.createElement("i");
  pbar.className = "pbar";
  const pbarB = document.createElement("b");
  pbar.appendChild(pbarB);

  li.append(pico, nameEl, cpuCell, memCell, pbar);
  appNodes.set(g.key, { countEl, cpu: cpuB, mem: memB, bar: pbarB, nameEl, li, absent: 0 });
  return li;
}

function hashColor(str) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
  const palette = ["#2BF5C4", "#4EA1FF", "#FF9F43", "#FF5D8F", "#B083FF", "#3ED6FF", "#FFD166"];
  return palette[h % palette.length];
}

/* ---------------- 软件详情浮层 + 一键结束（整组进程） ---------------- */
let modalKey = "";
let killArmed = false;
let killArmTimer = null;

$("procs").addEventListener("click", (e) => {
  const li = e.target.closest("li[data-key]");
  if (!li) return;
  openApp(li.dataset.key);
});

function openApp(key) {
  modalKey = key;
  killArmed = false;
  const g = appInfo.get(key);
  if (!g) return;
  $("procModal").hidden = false;
  fillApp(g);
}

function fillApp(g) {
  $("pmName").textContent = g.count > 1 ? `${g.name}（${g.count} 个进程）` : g.name;
  const rows = [
    ["进程数", g.count],
    ["合计内存", `${Math.round(g.mem)} MB`],
    ["占用 CPU", `${g.cpu.toFixed(1)}%`],
    ["说明", g.count > 1 ? "将结束该软件的全部进程（会关闭对应窗口）" : "将结束该进程"],
  ];
  $("pmRows").innerHTML = rows
    .map(([k, v]) => `<div class="pm-row"><span class="k">${k}</span><span class="v">${v}</span></div>`)
    .join("");

  const killBtn = $("pmKill");
  killBtn.textContent = g.count > 1 ? `结束软件（${g.count} 个进程）` : "结束进程";
  killBtn.classList.remove("armed");
  killBtn.disabled = !!g.self;
  if (g.self) killBtn.textContent = "不能结束自己";
}

$("pmKill").addEventListener("click", () => {
  if (!modalKey) return;
  if (!killArmed) {
    killArmed = true;
    const btn = $("pmKill");
    btn.textContent = "确认结束？再点一次";
    btn.classList.add("armed");
    clearTimeout(killArmTimer);
    killArmTimer = setTimeout(() => { killArmed = false; btn.textContent = btn.dataset.base || "结束进程"; btn.classList.remove("armed"); }, 3000);
    return;
  }
  clearTimeout(killArmTimer);
  killArmed = false;
  const g = appInfo.get(modalKey);
  if (!g || g.pids.length === 0) return;
  const btn = $("pmKill");
  btn.dataset.base = btn.textContent;
  btn.textContent = "正在结束…";
  btn.disabled = true;
  send("kill", { pids: g.pids.slice() });
  // 兜底：3s 未收到系统响应则提示
  setTimeout(() => {
    if ($("procModal").hidden || !$("pmKill").disabled) return;
    $("pmRows").innerHTML += '<div class="pm-row"><span class="k">状态</span><span class="v">未收到系统响应，请重试</span></div>';
    $("pmKill").disabled = false;
    $("pmKill").textContent = "结束进程";
  }, 3000);
});

function onKillResult(m) {
  $("pmKill").disabled = true;
  $("pmRows").innerHTML = `<div class="pm-row"><span class="k">结果</span><span class="v">${m.msg || (m.ok ? "已结束" : "失败")}</span></div>`;
  if (!m.ok && m.needElevated) {
    $("pmRows").innerHTML += '<div class="pm-row"><span class="k">建议</span><span class="v">托盘 → 以管理员身份重启后再试</span></div>';
  }
  setTimeout(() => { $("procModal").hidden = true; }, m.ok ? 1200 : 3500);
}

function closeModal() {
  clearTimeout(killArmTimer);
  killArmed = false;
  $("procModal").hidden = true;
}
$("pmClose").addEventListener("click", closeModal);
$("procModal").addEventListener("click", (e) => { if (e.target === e.currentTarget) closeModal(); });

/* ---------------- 迷你曲线 ---------------- */
function fitCanvas(cv) {
  const r = cv.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(10, Math.round(r.width * dpr));
  const h = Math.max(10, Math.round(r.height * dpr));
  if (cv.width !== w || cv.height !== h) { cv.width = w; cv.height = h; }
  return { w, h };
}

function drawSparks() {
  const specs = [
    [$("sparkDisk"), [diskBuf], [255, 180, 84]],
    [$("sparkNet"), [netBufD, netBufU], [78, 161, 255], [255, 180, 84]],
  ];
  for (const [cv, series, ...colors] of specs) {
    if (!cv) continue;
    const { w, h } = fitCanvas(cv);
    const ctx = cv.getContext("2d");
    ctx.clearRect(0, 0, w, h);
    if (w <= 0 || series.every((s) => s.length < 2)) continue;
    series.forEach((buf, si) => {
      if (buf.length < 2) return;
      const mx = Math.max(...buf, 1e-6);
      const col = rgb(colors[si] || colors[0]);
      ctx.beginPath();
      for (let i = 0; i < buf.length; i++) {
        const x = (i / (buf.length - 1)) * w;
        const y = h - 2 - (buf[i] / mx) * (h - 6);
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.5;
      ctx.lineJoin = "round";
      ctx.shadowColor = col;
      ctx.shadowBlur = 5;
      ctx.stroke();
      ctx.shadowBlur = 0;
    });
  }
}

/* ---------------- 心电图 ---------------- */
const ecgCv = $("ecg");
const ecgCtx = ecgCv.getContext("2d");
const gauss = (x, m, s) => Math.exp(-((x - m) * (x - m)) / (2 * s * s));

function ecgValue(tMs) {
  const ph = (tMs % 980) / 980;
  return (
    -0.20 * gauss(ph, 0.10, 0.028) - 1.30 * gauss(ph, 0.295, 0.019)
    + 0.15 * gauss(ph, 0.225, 0.018) + 0.33 * gauss(ph, 0.355, 0.024)
    - 0.50 * gauss(ph, 0.655, 0.075) + 0.08 * Math.sin(2 * Math.PI * tMs / 2600)
  );
}

let ecgT = 0, ecgLast = 0, ecgSpeed = 1;

function drawEcg(ts) {
  const { w, h } = fitCanvas(ecgCv);
  if (w <= 0 || h <= 0) return;
  const ctx = ecgCtx;
  ctx.clearRect(0, 0, w, h);

  const cpu = S.live ? R.cpuCur : Math.max(8, R.cpuCur * 0.35);
  const col = loadColor(cpu);
  const colCss = rgb(col);
  const mid = h * 0.56;
  const amp = h * (0.30 + (cpu / 100) * 0.22) * (1 + 0.05 * Math.sin(ecgT / 3800));
  const sPerPx = 2400 / w;

  // 负载越高心跳越快：时间轴按 load 加速滚动（带平滑）
  const dt = ecgLast ? Math.min(64, ts - ecgLast) : 16;
  ecgLast = ts;
  ecgSpeed += ((1 + (cpu / 100) * 2.6) - ecgSpeed) * 0.25;
  ecgT += dt * ecgSpeed;

  ctx.strokeStyle = "rgba(255,255,255,.04)";
  ctx.lineWidth = 1;
  for (let i = 1; i < 4; i++) {
    ctx.beginPath(); ctx.moveTo(0, (h / 4) * i); ctx.lineTo(w, (h / 4) * i); ctx.stroke();
  }

  ctx.beginPath();
  for (let x = 0; x <= w; x++) {
    const t = ecgT - (w - x) * sPerPx;
    const y = mid + ecgValue(t) * amp;
    if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = colCss;
  ctx.lineWidth = Math.max(2, Math.min(3.4, h / 60));
  ctx.lineJoin = "round";
  ctx.lineCap = "round";
  ctx.shadowColor = `rgba(${col[0]},${col[1]},${col[2]},.9)`;
  ctx.shadowBlur = 12;
  ctx.stroke();
  ctx.shadowBlur = 0;

  const grad = ctx.createLinearGradient(0, mid - amp * 1.4, 0, h);
  grad.addColorStop(0, `rgba(${col[0]},${col[1]},${col[2]},.16)`);
  grad.addColorStop(1, `rgba(${col[0]},${col[1]},${col[2]},0)`);
  ctx.fillStyle = grad;
  ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath(); ctx.fill();

  const ly = mid + ecgValue(ecgT) * amp;
  ctx.beginPath();
  ctx.arc(w, ly, 3.2, 0, Math.PI * 2);
  ctx.fillStyle = "#fff";
  ctx.shadowColor = colCss;
  ctx.shadowBlur = 14;
  ctx.fill();
  ctx.shadowBlur = 0;
}

/* ---------------- 启动时序 ---------------- */
window.addEventListener("load", () => {
  setTimeout(() => document.body.classList.add("live"), 1500);
  setTimeout(() => { $("splash").classList.add("hide"); maybeShowOnb(); }, 1560);
  send("ping");
  requestAnimationFrame(frame);
});

/* 调试：单击波形说明区 → 回传前端内部状态（配合原生日志定位 UI 卡点） */
$("ecgCaption").addEventListener("click", () => {
  try {
    send("jslog", {
      text: `ui-state live=${S.live} ticks=${uiTickN} rawCpu=${uiRawCpu} curCpu=${Math.round(R.cpuCur)} tgtCpu=${R.cpuTgt} mem=${R.memCur.toFixed(1)} temp=${Math.round(R.tempCur)} uptimeReal=${S.upReal}`,
    });
  } catch (_) { /* ignore */ }
});

function frame(ts) {
  if (!document.hidden) drawEcg(ts);
  requestAnimationFrame(frame);
}

new ResizeObserver(() => {
  if (!document.hidden) { drawEcg(performance.now()); drawSparks(); }
}).observe(document.querySelector(".ecg-wrap"));

/* ================================================================
   M4：24h 历史曲线回看
   ================================================================ */
function showHistory(m) {
  $("histModal").hidden = false;
  drawHistory(m && m.pts ? m.pts : []);
}
function drawHistory(pts) {
  const cv = $("histCv");
  const dpr = window.devicePixelRatio || 1;
  const rect = cv.getBoundingClientRect();
  const w = Math.max(10, Math.round(rect.width * dpr));
  const h = Math.max(10, Math.round(rect.height * dpr));
  if (cv.width !== w || cv.height !== h) { cv.width = w; cv.height = h; }
  const ctx = cv.getContext("2d");
  ctx.clearRect(0, 0, w, h);
  if (pts.length < 2) {
    ctx.fillStyle = "rgba(255,255,255,.4)";
    ctx.font = `${12 * dpr}px sans-serif`;
    ctx.fillText("数据积累中…", w / 2 - 40 * dpr, h / 2);
    return;
  }
  const pad = 8 * dpr;
  const X = (i) => pad + (i / (pts.length - 1)) * (w - pad * 2);
  const Y = (v) => h - pad - (v / 100) * (h - pad * 2);
  const acc = getComputedStyle(document.body).getPropertyValue("--accent").trim() || "#6b7cff";

  ctx.strokeStyle = "rgba(255,255,255,.05)";
  ctx.lineWidth = dpr;
  ctx.beginPath();
  for (let i = 0; i <= 4; i++) {
    ctx.moveTo(pad, (h / 5) * i);
    ctx.lineTo(w - pad, (h / 5) * i);
  }
  ctx.stroke();

  const line = (key, color) => {
    ctx.beginPath();
    pts.forEach((p, i) => {
      const x = X(i), y = Y(Math.max(0, Math.min(100, p[key] || 0)));
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    });
    ctx.strokeStyle = color;
    ctx.lineWidth = Math.max(dpr, 1.5 * dpr);
    ctx.stroke();
  };
  line("cpu", acc);
  line("mem", "#ffb454");
}
$("histClose").addEventListener("click", () => { $("histModal").hidden = true; });
$("histModal").addEventListener("click", (e) => { if (e.target === e.currentTarget) $("histModal").hidden = true; });

/* ---------- 软件搜索过滤 ---------- */
const searchEl = $("procSearch");
function applyFilter() {
  const kw = (searchEl && searchEl.value ? searchEl.value.trim().toLowerCase() : "");
  let shown = 0;
  for (const [key, nodes] of appNodes) {
    const name = (appInfo.get(key)?.name || key).toLowerCase();
    const hit = !kw || name.includes(kw);
    nodes.li.style.display = hit ? "" : "none";
    if (hit) shown++;
  }
  const empty = document.getElementById("procEmpty");
  if (!shown) {
    if (!empty) {
      const li = document.createElement("li");
      li.id = "procEmpty";
      li.className = "procs-empty";
      li.textContent = "没有匹配的软件";
      $("procs").appendChild(li);
    }
  } else if (empty) {
    empty.remove();
  }
}
searchEl && searchEl.addEventListener("input", applyFilter);

/* ---------- 主题色预设（持久化到 localStorage） ---------- */
const THEME_KEY = "corebeat.theme";
let curTheme = localStorage.getItem(THEME_KEY) || "cyan";
function applyTheme(t) {
  curTheme = t;
  localStorage.setItem(THEME_KEY, t);
  document.body.dataset.theme = t;
  document.querySelectorAll(".t-dot").forEach((b) => {
    b.classList.toggle("on", b.dataset.theme === t);
  });
  // 同步主色到原生（设置窗口跟随）
  const hex = getComputedStyle(document.body).getPropertyValue("--accent").trim() || "#6b7cff";
  try { send("accent", { hex }); } catch (_) { }
}
document.querySelectorAll(".t-dot").forEach((b) =>
  b.addEventListener("click", () => applyTheme(b.dataset.theme)));
applyTheme(curTheme);

/* ---------- 软件主题：深色 / 浅色 / 跟随系统（由原生设置窗口下发） ---------- */
let softwareTheme = "dark";
const mqLight = window.matchMedia("(prefers-color-scheme: light)");
function effectiveLight() {
  if (softwareTheme === "light") return true;
  if (softwareTheme === "system") return !!mqLight.matches;
  return false;
}
function applySoftwareTheme() {
  document.body.classList.toggle("theme-light", effectiveLight());
}
function setSoftwareTheme(mode) {
  softwareTheme = mode === "light" ? "light" : mode === "system" ? "system" : "dark";
  applySoftwareTheme();
}
mqLight.addEventListener && mqLight.addEventListener("change", () => { if (softwareTheme === "system") applySoftwareTheme(); });
applySoftwareTheme();

/* ---------- 主界面卡片显隐（设置 → 通用 下发） ---------- */
const CARD_ID = {
  tileCpu: "tileCpu", tileMem: "tileMem", tileTemp: "tileTemp", tileGpu: "tileGpu", tileUp: "tileUp",
  ecg: "cardEcg", gauge: "cardGauge", procs: "cardProcs", disk: "cardDisk", clean: "cardClean",
};
function applyCardConfig(show) {
  const set = new Set(Array.isArray(show) ? show : Object.keys(CARD_ID));
  Object.entries(CARD_ID).forEach(([key, id]) => {
    const el = document.getElementById(id);
    if (el) el.style.display = set.has(key) ? "" : "none";
  });
}

/* ---------- 垃圾清理快捷卡 ---------- */
function fmtCleanSize(b) {
  if (!b || b <= 0) return "0 KB";
  if (b >= 1 << 30) return (b / (1 << 30)).toFixed(1) + " GB";
  if (b >= 1 << 20) return (b / (1 << 20)).toFixed(1) + " MB";
  return (b / 1024).toFixed(0) + " KB";
}
function setCleanInfo(msg) {
  const el = $("cleanTotal");
  if (el && msg && typeof msg.total === "number") el.textContent = fmtCleanSize(msg.total);
  const note = $("cleanNote");
  if (note && msg && msg.busy) { note.textContent = "正在清理…请稍候"; }
}
function onCleanDone(msg) {
  const card = $("cardClean");
  if (card) card.classList.remove("working");
  const note = $("cleanNote");
  if (note && msg) {
    note.textContent = msg.freed > 0
      ? `已释放 ${fmtCleanSize(msg.freed)}（成功 ${msg.ok} / 跳过 ${msg.fail}）`
      : `本次未释放：文件被占用（跳过 ${msg.fail} 项）；关闭使用中的浏览器/程序后再清即可`;
  }
  setCleanInfo(msg); // 更新剩余可清理量
  if (msg && msg.freed > 0) showToast("垃圾清理", `已释放 ${fmtCleanSize(msg.freed)}`);
}
$("cleanGo") && $("cleanGo").addEventListener("click", () => {
  const card = $("cardClean");
  if (card && card.classList.contains("working")) return;
  if (card) card.classList.add("working");
  const note = $("cleanNote"); if (note) note.textContent = "正在清理…请稍候";
  send("cleanQuick");
});
$("cleanDeep") && $("cleanDeep").addEventListener("click", () => send("openCleanSettings"));

/* ---------- 网络测速弹层 ---------- */
const speedModal = $("speedModal");
function speedOpen() { speedModal.hidden = false; }
function speedClose() { speedModal.hidden = true; speedStopUI(); }
$("speedOpen") && $("speedOpen").addEventListener("click", speedOpen);
$("speedClose") && $("speedClose").addEventListener("click", speedClose);
speedModal && speedModal.addEventListener("click", (e) => { if (e.target === speedModal) speedClose(); });
function speedStopUI() {
  const c = speedModal.querySelector(".speed-card");
  if (c) c.classList.remove("running");
  const g = $("speedGo"); if (g) { g.disabled = false; g.querySelector(".clean-tx").textContent = "开始测速"; g.style.opacity = ""; }
}
$("speedGo") && $("speedGo").addEventListener("click", () => {
  setHero(0, "Mbps"); $("stCap").textContent = "正在选择节点…";
  $("stDown").textContent = $("stUp").textContent = $("stLat").textContent = $("stJit").textContent = $("stLoss").textContent = "—";
  const c = speedModal.querySelector(".speed-card");
  if (c) c.classList.add("running");
  const g = $("speedGo"); if (g) { g.disabled = true; g.querySelector(".clean-tx").textContent = "测速中…"; }
  send("speedtest");
});
function setHero(v, unit) {
  const big = $("stBig"); if (big) big.textContent = Math.round(v);
  const u = $("stUnit"); if (u) u.textContent = unit;
  const ring = $("stRing"); if (ring) {
    const pct = unit === "ms" ? Math.min(100, v / 200 * 100)
      : (v <= 0 ? 0 : Math.min(100, (Math.log10(1 + v) / Math.log10(1001)) * 100)); // 对数映射，常规速率也明显
    ring.style.background = `conic-gradient(var(--accent) ${pct}%, rgba(128,128,128,.14) 0)`;
  }
}
function onSpeedTest(msg) {
  const card = speedModal.querySelector(".speed-card");
  if (msg.phase === "node") { $("stNode").textContent = "节点：" + msg.text; }
  else if (msg.phase === "ping") { $("stNode").textContent = msg.text; setHero(msg.val || 0, "ms"); $("stCap").textContent = "延迟"; }
  else if (msg.phase === "down") { setHero(msg.val, "Mbps"); $("stCap").textContent = "下行 " + msg.val.toFixed(1) + " Mbps"; }
  else if (msg.phase === "up") { setHero(msg.val, "Mbps"); $("stCap").textContent = "上行 " + msg.val.toFixed(1) + " Mbps"; }
  else if (msg.phase === "done" && typeof msg.text === "string" && msg.text.includes("|")) {
    const p = msg.text.split("|");
    $("stDown").textContent = (+p[0]).toFixed(1) + " Mbps";
    $("stUp").textContent = (+p[1]) > 0 ? (+p[1]).toFixed(1) + " Mbps" : "不可达";
    $("stLat").textContent = (+p[2]).toFixed(0) + " ms";
    $("stJit").textContent = (+p[3]).toFixed(1) + " ms";
    $("stLoss").textContent = (+p[4]).toFixed(0) + " %";
    $("stNode").textContent = "节点：" + (p[5] || "");
    setHero(+p[0], "Mbps"); $("stCap").textContent = "下行 Mbps"; $("stStage").textContent = "测速完成";
    if (card) card.classList.remove("running");
    const g = $("speedGo"); if (g) { g.disabled = false; g.querySelector(".clean-tx").textContent = "重新测速"; }
  }
}


/* ---------- 告警：温度 ≥85℃ / 内存 ≥90%，同条件 10 分钟内不重复 ---------- */
let lastAlert = { temp: 0, mem: 0 };

/* ---------- 首启引导 ---------- */
const ONB_KEY = "corebeat.onb";
const onbEl = $("onb");
let onbIdx = 0;
const ONB_STEPS = 4;
function onbFinish() { try { localStorage.setItem(ONB_KEY, "1"); } catch (_) { } if (onbEl) onbEl.hidden = true; }
function maybeShowOnb() {
  let done = false; try { done = localStorage.getItem(ONB_KEY) === "1"; } catch (_) { }
  if (done || !onbEl) return;
  onbEl.hidden = false; onbIdx = 0; renderOnb();
}
function onbDots() {
  const d = $("onbDots"); if (!d) return; d.innerHTML = "";
  for (let i = 0; i < ONB_STEPS; i++) { const b = document.createElement("span"); b.className = "od" + (i === onbIdx ? " on" : ""); d.appendChild(b); }
}
function renderOnb() {
  const s = $("onbStage"); if (!s) return;
  const pv = $("onbPrev"), nx = $("onbNext");
  if (pv) pv.hidden = onbIdx === 0;
  if (nx) nx.textContent = onbIdx === ONB_STEPS - 1 ? "开始使用" : "下一步";
  const st = document.createElement("div"); st.className = "onb-st";
  if (onbIdx === 0) {
    st.innerHTML = `
      <div class="onb-logo"><span class="lg">♥</span></div>
      <div class="onb-h1">欢迎使用 芯跳 CoreBeat</div>
      <div class="onb-sub">一块会呼吸的心率仪表 —— 实时掌握 CPU / GPU / 温度 / 网络，防睡防锁，还有桌面宠物伴你左右。</div>
      <div class="onb-chips">
        <div class="onb-chip"><i>⚡</i><div><b>实时监测</b><span>负载·温度·内存·网速一屏看清</span></div></div>
        <div class="onb-chip"><i>🛡️</i><div><b>防睡眠</b><span>三档防睡防锁，倒计时自由</span></div></div>
        <div class="onb-chip"><i>🐾</i><div><b>桌面宠物</b><span>多款像素伙伴，双击释放内存</span></div></div>
        <div class="onb-chip"><i>🧹</i><div><b>清理·测速</b><span>一键清垃圾、真实网速测试</span></div></div>
      </div>`;
  } else if (onbIdx === 1) {
    const cur = document.body.dataset.theme || "cyan";
    st.innerHTML = `
      <div class="onb-h1">挑一个你喜欢的颜色</div>
      <div class="onb-sub">主题与主色即刻生效，随时可在「设置 → 通用」里再改。</div>
      <div class="onb-swatches" id="obSwatches">
        <button class="ob-dot ${cur === "cyan" ? "on" : ""}" style="background:#6b7cff" data-th="cyan" title="靛蓝"></button>
        <button class="ob-dot ${cur === "blue" ? "on" : ""}" style="background:#4ea1ff" data-th="blue" title="深海蓝"></button>
        <button class="ob-dot ${cur === "violet" ? "on" : ""}" style="background:#9a6bff" data-th="violet" title="心动紫"></button>
      </div>
      <div class="onb-seg" id="obSegs">
        <button class="ob-seg ${softwareTheme === "dark" ? "on" : ""}" data-m="dark">深色</button>
        <button class="ob-seg ${softwareTheme === "light" ? "on" : ""}" data-m="light">浅色</button>
        <button class="ob-seg ${softwareTheme === "system" ? "on" : ""}" data-m="system">跟随系统</button>
      </div>`;
  } else if (onbIdx === 2) {
    st.innerHTML = `
      <div class="onb-h1">按你的习惯来</div>
      <div class="onb-sub">这些都能在设置里随时修改。</div>
      <div class="onb-rows">
        <label class="sw"><span><b>开机自启</b><span>登录后后台驻留托盘</span></span><span class="sw-track" data-k="autostart"></span></label>
        <label class="sw"><span><b>开机以管理员运行</b><span>解锁 NVMe 温度与系统清理</span></span><span class="sw-track" data-k="adminRun"></span></label>
        <label class="sw on"><span><b>显示桌面宠物</b><span>右下角一位像素伙伴</span></span><span class="sw-track" data-k="petShow" data-on="1"></span></label>
      </div>`;
  } else {
    st.innerHTML = `
      <div class="onb-done">
        <div class="ob-ring">
          <svg width="96" height="96" viewBox="0 0 96 96">
            <circle class="rtrk" cx="48" cy="48" r="40"></circle>
            <circle class="rfg" cx="48" cy="48" r="40"></circle>
          </svg>
          <div class="ob-donecheck">✓</div>
        </div>
        <div class="onb-h1">准备就绪</div>
        <div class="onb-sub">点击下方开始，进入你的第一块心跳面板。</div>
      </div>`;
  }
  s.innerHTML = ""; s.appendChild(st);
  onbDots();
  if (onbIdx === 1) {
    st.querySelectorAll(".ob-dot").forEach((d) => d.addEventListener("click", () => {
      st.querySelectorAll(".ob-dot").forEach((x) => x.classList.remove("on")); d.classList.add("on");
      applyTheme(d.dataset.th);
    }));
    st.querySelectorAll(".ob-seg").forEach((b) => b.addEventListener("click", () => {
      st.querySelectorAll(".ob-seg").forEach((x) => x.classList.remove("on")); b.classList.add("on");
      setSoftwareTheme(b.dataset.m);
      try { send("setThemeMode", { mode: b.dataset.m }); } catch (_) { }
    }));
  } else if (onbIdx === 2) {
    st.querySelectorAll(".sw").forEach((row) => {
      row.addEventListener("click", () => {
        const tr = row.querySelector(".sw-track"); const on = tr.dataset.on !== "1";
        tr.dataset.on = on ? "1" : "0"; row.classList.toggle("on", on);
        try { send("setOption", { key: tr.dataset.k, on }); } catch (_) { }
      });
    });
  }
}
function onbGo(delta) {
  if (delta > 0 && onbIdx === ONB_STEPS - 1) { onbFinish(); return; }
  if (delta < 0 && onbIdx === 0) return;
  onbIdx += delta; renderOnb();
}
$("onbNext") && $("onbNext").addEventListener("click", () => onbGo(1));
$("onbPrev") && $("onbPrev").addEventListener("click", () => onbGo(-1));
$("onbSkip") && $("onbSkip").addEventListener("click", onbFinish);
let audioCtx = null;
function beep() {
  try {
    audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
    const o = audioCtx.createOscillator();
    const g = audioCtx.createGain();
    o.frequency.value = 880;
    g.gain.value = 0.06;
    o.connect(g); g.connect(audioCtx.destination);
    o.start(); o.stop(audioCtx.currentTime + 0.35);
  } catch (_) { /* 无音频设备则静默 */ }
}
function showToast(title, msg) {
  const t = document.createElement("div");
  t.className = "toast";
  t.innerHTML = `<div class="tt">${title}</div><div class="tm">${msg}</div>`;
  $("toasts").appendChild(t);
  setTimeout(() => { t.classList.add("out"); setTimeout(() => t.remove(), 320); }, 6000);
}
function checkAlerts(m) {
  if (!S.live || !m) return;
  const now = Date.now();
  const memPct = m.mem && typeof m.mem.pct === "number" ? m.mem.pct : NaN;
  const hotTemp = Array.isArray(m.temps)
    ? m.temps.filter((r) => typeof r.v === "number" && r.v >= 85).map((r) => `${r.label} ${Math.round(r.v)}°`)
    : [];
  if (hotTemp.length && now - lastAlert.temp > 10 * 60 * 1000) {
    lastAlert.temp = now;
    showToast("温度告警", `${hotTemp.join("、")}：请留意散热`);
    beep();
  }
  if (memPct >= 90 && now - lastAlert.mem > 10 * 60 * 1000) {
    lastAlert.mem = now;
    showToast("内存告警", `内存占用已达 ${memPct.toFixed(0)}%，建议释放`);
    beep();
  }
}
// 注入到 tick 处理
const __applyTick = applyTick;
function applyTick2(m) { try { __applyTick(m); } finally { try { checkAlerts(m); } catch (_) { } } }
applyTick = applyTick2;

/* ---------- 彩蛋：Logo 连点 5 次 → 跳动爱心 ---------- */
const LOVE_LINES = [
  "这颗芯，为你 24/7 保持清醒",
  "每一个心跳，都在为你的代码喝彩",
  "发热的灵魂，也需要呼吸",
  "芯里有你，永远不宕机",
  "就算世界关机，它也为你待机",
];
let logoClicks = 0, logoFirstTs = 0;
const logo = document.querySelector(".tb-logo");
logo && logo.addEventListener("click", () => {
  const now = Date.now();
  if (now - logoFirstTs > 3000) { logoClicks = 0; logoFirstTs = now; }
  logoClicks++;
  if (logoClicks < 5) return;
  logoClicks = 0;
  const love = $("loveOverlay");
  love.hidden = false;
  $("loveLine").textContent = LOVE_LINES[Math.floor(Math.random() * LOVE_LINES.length)];
  setTimeout(() => { love.hidden = true; }, 25000);
});

/* ---------- 彩蛋：温度卡连点 3 次 → 俏皮气泡 ---------- */
const TEMP_TALKS = [
  "温度还好，我还能再战三百年~",
  "风扇：今天也是努力的一天",
  "别盯了，再盯我也不会凉得快",
];
const TEMP_HOT_TALK = "这芯在发烧，建议去吹吹风～";
let tempClicks = 0, tempFirstTs = 0;
const tempBig = $("gaugeNum"); // 温度彩蛋改绑到「温度仪表盘」大数字
tempBig && tempBig.addEventListener("click", (ev) => {
  const now = Date.now();
  if (now - tempFirstTs > 2500) { tempClicks = 0; tempFirstTs = now; }
  tempClicks++;
  if (tempClicks < 3) return;
  tempClicks = 0;
  const bubble = $("tempBubble");
  const hot = S.live && R.tempCur >= 85;
  bubble.textContent = hot ? TEMP_HOT_TALK : TEMP_TALKS[Math.floor(Math.random() * TEMP_TALKS.length)];
  const appRect = document.querySelector(".app").getBoundingClientRect();
  const t = tempBig.getBoundingClientRect();
  bubble.style.left = Math.max(8, Math.min(t.left - appRect.left, appRect.width - 250)) + "px";
  bubble.style.top = Math.max(30, t.bottom - appRect.top + 6) + "px";
  bubble.hidden = false;
  clearTimeout(bubble._t);
  bubble._t = setTimeout(() => { bubble.hidden = true; }, 4200);
});
