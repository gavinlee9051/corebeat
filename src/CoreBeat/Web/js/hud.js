/* 桌面宠物：单击弹气泡显示可配置指标（CPU/内存/GPU）；拖拽；双击回主界面 */
"use strict";
const $ = (id) => document.getElementById(id);
const webview = window.chrome?.webview;
function send(cmd, extra) {
  if (!webview) return;
  try { webview.postMessage(JSON.stringify(Object.assign({ cmd }, extra || {}))); }
  catch (_) { }
}

let cpu = null, memPct = null, gpuPct = null;
let show = ["cpu", "mem", "gpu"];
try {
  const s = localStorage.getItem("corebeat.hud.show");
  if (s) show = JSON.parse(s);
} catch (_) { }
let popTimer = null;

const pet = $("pet"), pop = $("pop");

function cfg() {
  try { localStorage.setItem("corebeat.hud.show", JSON.stringify(show)); } catch (_) { }
}
window.__applyCfg = () => {
  try {
    const s = localStorage.getItem("corebeat.hud.show");
    if (s) show = JSON.parse(s);
  } catch (_) { }
};
window.cbOnNative = (obj) => {
  if (!obj) return;
  if (obj.t === "cfg" && Array.isArray(obj.show)) { show = obj.show.slice(); cfg(); if (pop.classList.contains("show")) renderPop(); return; }
  if (obj.t === "fast" && typeof obj.cpu === "number") cpu = obj.cpu;
  else if (obj.t === "tick") {
    if (typeof obj.cpu === "number") cpu = obj.cpu;
    if (obj.mem) memPct = obj.mem.pct;
    if (obj.gpu && typeof obj.gpu.pct === "number") gpuPct = obj.gpu.pct;
  }
  glow();
  if (pop.classList.contains("show")) renderPop();
};
webview?.addEventListener("message", () => { /* 兼容通道保留 */ });

function accent() {
  if (cpu == null) return "#2bf5c4";
  return cpu > 85 ? "#ff5d5d" : cpu > 70 ? "#ffb454" : "#2bf5c4";
}
function rgba(hex, a) {
  const h = hex.replace("#", "");
  return `rgba(${parseInt(h.substring(0, 2), 16)},${parseInt(h.substring(2, 4), 16)},${parseInt(h.substring(4, 6), 16)},${a})`;
}
function glow() {
  const c = accent();
  const rs = document.documentElement.style;
  rs.setProperty("--accent", c);
  rs.setProperty("--glow", rgba(c, 0.28));
}

function fmt(v, suffix) { return v == null ? "--" : Math.round(v) + suffix; }
function renderPop() {
  const rows = [];
  if (show.includes("cpu")) rows.push(["CPU", fmt(cpu, "%")]);
  if (show.includes("mem")) rows.push(["内存", fmt(memPct, "%")]);
  if (show.includes("gpu")) rows.push(["GPU", fmt(gpuPct, "%")]);
  if (rows.length === 0) rows.push(["状态", "未配置显示项"]);
  pop.innerHTML = rows.map(([k, v]) => `<div class="row"><span class="k">${k}</span><span class="v">${v}</span></div>`).join("");
}
function showPop() {
  renderPop();
  pop.classList.add("show");
  clearTimeout(popTimer);
  popTimer = setTimeout(() => pop.classList.remove("show"), 4000);
}

/* 拖拽（逐帧屏幕坐标，原生移窗） / 单击弹气泡 / 双击回主界面 */
let seed = null, dragging = false, lastDragAt = 0, clickTimer = null;
pet.addEventListener("pointerdown", (e) => { if (e.button === 0) { seed = { x: e.screenX, y: e.screenY }; dragging = false; } });
pet.addEventListener("pointermove", (e) => {
  if (!seed) return;
  if (!dragging) {
    if (Math.hypot(e.screenX - seed.x, e.screenY - seed.y) <= 4) return;
    dragging = true;
    send("dragBegin", { x: e.screenX, y: e.screenY });
    return;
  }
  const now = performance.now();
  if (now - lastDragAt < 12) return;
  lastDragAt = now;
  send("dragMove", { x: e.screenX, y: e.screenY });
});
pet.addEventListener("pointerup", (e) => {
  if (dragging) { send("dragEnd"); seed = null; dragging = false; return; }
  seed = null;
  if (e.button !== 0) return;
  if (clickTimer) clearTimeout(clickTimer);
  clickTimer = setTimeout(showPop, 300);   // 留出双击判定时间后弹气泡
});
pet.addEventListener("dblclick", () => {
  if (clickTimer) { clearTimeout(clickTimer); clickTimer = null; }
  send("main");
});
glow();