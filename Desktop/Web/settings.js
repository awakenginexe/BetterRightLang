"use strict";

const bridge = window.chrome?.webview;
const language = document.querySelector("#language");
const startup = document.querySelector("#startup");
const notice = document.querySelector("#notice");
const labels = Array.from(document.querySelectorAll("[data-t]"));
const english = Object.fromEntries(labels.map(node => [node.dataset.t, node.textContent]));
Object.assign(english, { keep: "Whitelist original", kept: "Whitelisted", remove: "Remove",
  oneWord: "Enter one word without spaces.", added: "Added to the whitelist", removed: "Removed from the whitelist" });
const thai = {
  language: "ภาษาที่แสดง", mode: "โหมดการแก้ไข", auto: "อัตโนมัติ", manual: "แก้ด้วยตนเอง", paused: "หยุดชั่วคราว",
  autoHint: "สลับภาษาเมื่อคำที่พิมพ์ตรงกับอีกแป้นพิมพ์", manualHint: "กดปุ่มลัดเพื่อแก้ข้อความที่พิมพ์", pausedHint: "พักการแก้ไขและปิดปุ่มลัด",
  tester: "ฝึกพิมพ์", testerHint: "ลองพิมพ์ตัวอย่างโดยใช้แป้นพิมพ์ภาษาอังกฤษ โหมดอัตโนมัติจะสลับภาษาเมื่อจำคำที่พิมพ์ได้ ส่วนโหมดแก้ด้วยตนเองให้กด Shift + Backspace เพื่อแปลงคำ", sample: "ข้อความสำหรับฝึกพิมพ์",
  shortcuts: "ปุ่มลัด", current: "แปลงคำที่เพิ่งพิมพ์", selection: "แปลงข้อความที่เลือก", toggle: "สลับโหมดอัตโนมัติ / แก้ด้วยตนเอง",
  startup: "เริ่มพร้อม Windows", startupHint: "เปิดในถาดระบบเมื่อเข้าสู่ระบบ",
  settings: "การตั้งค่า", history: "ประวัติ", recent: "คำที่แก้ไขล่าสุด",
  historyHint: "แสดง 100 คำล่าสุดที่โปรแกรมแก้ไข ประวัติจะหายไปเมื่อออกจาก BetterRightLang",
  noHistory: "ยังไม่มีคำที่แก้ไขในครั้งนี้", whitelist: "คำยกเว้น",
  whitelistHint: "โหมดอัตโนมัติจะคงคำเหล่านี้ตามที่พิมพ์ รายการนี้บันทึกไว้ในเครื่อง",
  wordLabel: "คำที่จะเพิ่มเป็นข้อยกเว้น", add: "เพิ่ม", noWords: "ยังไม่มีคำยกเว้น",
  keep: "ยกเว้นคำเดิม", kept: "ยกเว้นแล้ว", remove: "ลบ", oneWord: "กรอกคำเดียวโดยไม่เว้นวรรค",
  added: "เพิ่มในรายการคำยกเว้นแล้ว", removed: "ลบจากรายการคำยกเว้นแล้ว"
};
const tabs = Array.from(document.querySelectorAll('[role="tab"]'));
const wordInput = document.querySelector("#whitelist-word");
const corrections = document.querySelector("#corrections");
const whitelist = document.querySelector("#whitelist");
const status = document.querySelector("#word-status");
let words = english, historyState = null, pendingWord = null;

function selectPage(tab) {
  for (const item of tabs) {
    const active = item === tab;
    item.setAttribute("aria-selected", String(active));
    item.tabIndex = active ? 0 : -1;
    document.getElementById(item.getAttribute("aria-controls")).hidden = !active;
  }
  const show = tab.id === "history-tab";
  if (!show) {
    historyState = pendingWord = null;
    corrections.replaceChildren();
    whitelist.replaceChildren();
    status.textContent = "";
  }
  bridge?.postMessage({ type: "history", value: show });
}
for (const tab of tabs) {
  tab.addEventListener("click", () => selectPage(tab));
  tab.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === "Home" ? tabs[0] : event.key === "End" ? tabs[1] : tabs.find(item => item !== tab);
    next.focus();
    selectPage(next);
  });
}

function element(tag, text, style) {
  const node = document.createElement(tag);
  node.textContent = text;
  if (style) node.className = style;
  return node;
}
function changeWord(word, add) {
  pendingWord = { word, add };
  bridge.postMessage({ type: add ? "whitelistAdd" : "whitelistRemove", value: word });
}
function wordButton(word, add, kept = false) {
  const label = kept ? words.kept : add ? words.keep : words.remove;
  const button = element("button", label);
  button.type = "button";
  button.dataset.word = word;
  button.dataset.action = add ? "add" : "remove";
  button.setAttribute("aria-label", label + " “" + word + "”");
  if (kept) button.setAttribute("aria-disabled", "true");
  else button.addEventListener("click", () => changeWord(word, add));
  return button;
}
function renderHistory() {
  if (!historyState || document.querySelector("#history-page").hidden) return;
  const { entries, whitelist: saved } = historyState;
  const listed = new Set(saved.map(word => word.toLowerCase()));
  const previous = document.activeElement;
  const rows = entries.map(entry => {
    const row = document.createElement("li");
    const pair = document.createElement("div");
    pair.append(element("bdi", entry.original), element("span", "→", "arrow"), element("bdi", entry.replacement, "result"),
      element("small", entry.automatic ? words.auto : words.manual));
    row.append(pair, wordButton(entry.original, true, listed.has(entry.original.toLowerCase())));
    return row;
  });
  corrections.replaceChildren(...rows);
  whitelist.replaceChildren(...saved.map(word => {
    const row = document.createElement("li");
    row.append(element("bdi", word), wordButton(word, false));
    return row;
  }));
  document.querySelector("#no-history").hidden = entries.length !== 0;
  document.querySelector("#no-words").hidden = saved.length !== 0;
  if (previous?.dataset.action) {
    const same = Array.from(document.querySelectorAll(".word-list button")).find(b =>
      b.dataset.word === previous.dataset.word && b.dataset.action === previous.dataset.action);
    (same ?? wordInput).focus({ preventScroll: true });
  }
  if (pendingWord && listed.has(pendingWord.word.toLowerCase()) === pendingWord.add) {
    status.textContent = (pendingWord.add ? words.added : words.removed) + " “" + pendingWord.word + "”";
    if (pendingWord.add && wordInput.value.trim() === pendingWord.word) wordInput.value = "";
    pendingWord = null;
  }
}
if (bridge) {
  bridge.addEventListener("message", ({ data }) => {
    if (data.type === "history") {
      if (!document.querySelector("#history-page").hidden) { historyState = data.state; renderHistory(); }
      return;
    }
    if (data.type !== "state") return;
    const state = data.state;
    document.documentElement.lang = language.value = state.language;
    words = state.language === "th" ? thai : english;
    for (const node of labels) node.textContent = words[node.dataset.t];
    for (const radio of document.querySelectorAll("input[name=mode]")) radio.checked = radio.value === state.mode;
    startup.checked = state.startup;
    notice.hidden = !state.error;
    notice.textContent = state.error ?? "";
    if (state.error) pendingWord = null;
    renderHistory();
  });
  for (const radio of document.querySelectorAll("input[name=mode]")) {
    radio.addEventListener("change", () => {
      if (radio.checked) bridge.postMessage({ type: "mode", value: radio.value });
    });
  }
  language.addEventListener("change", () => bridge.postMessage({ type: "language", value: language.value }));
  startup.addEventListener("change", () => bridge.postMessage({ type: "startup", value: startup.checked }));
  wordInput.addEventListener("input", () => wordInput.setCustomValidity(""));
  document.querySelector("#whitelist-form").addEventListener("submit", event => {
    event.preventDefault();
    const word = wordInput.value.trim();
    if (!word || /[\s\u0000-\u001f\u007f-\u009f]/u.test(word)) {
      wordInput.setCustomValidity(words.oneWord);
      wordInput.reportValidity();
      return;
    }
    changeWord(word, true);
  });
  bridge.postMessage({ type: "ready" });
} else {
  notice.hidden = false;
  notice.textContent = "Open BetterRightLang.exe to use these settings.";
  for (const control of document.querySelectorAll("input:not(#sample), select, #whitelist-form button")) control.disabled = true;
}
