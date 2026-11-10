const defaultDevices = [
  { id: "mac", name: "MacBook Pro 14″", label: "MacBook Pro", type: "mac", os: "macOS Sequoia", host: "MacBook-Pro.local", state: "Connected" },
  { id: "android", name: "Pixel 8", label: "Pixel 8", type: "android", os: "Android 15", host: "Pixel-8.local", state: "Connected" },
];

const state = {
  devices: loadDevices(),
  mouse: false,
  keyboard: false,
  edgeEnabled: true,
  holding: false,
  clipboard: false,
  sourceIndex: 0,
  currentView: "desk",
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const esc = (value) => String(value).replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));

function loadDevices() {
  try {
    const saved = JSON.parse(localStorage.getItem("drift.devices") || "null");
    return Array.isArray(saved) && saved.length ? saved : defaultDevices.map((device) => ({ ...device }));
  } catch {
    return defaultDevices.map((device) => ({ ...device }));
  }
}

function persist() {
  localStorage.setItem("drift.devices", JSON.stringify(state.devices));
}

function setToggle(button, enabled) {
  button.classList.toggle("on", enabled);
  button.setAttribute("aria-checked", String(enabled));
}

function syncShareState() {
  const active = state.mouse || state.keyboard;
  const badge = $("#sharing-badge");
  badge.textContent = active ? "SHARING" : "PAUSED";
  badge.classList.toggle("live", active);
  setToggle($("#mouse-toggle"), state.mouse);
  setToggle($("#keyboard-toggle"), state.keyboard);
  if (active) showToast("Input sharing is on in this preview");
}

let toastTimer;
function showToast(message) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("show"), 2400);
}

function showView(name) {
  state.currentView = name;
  $$(".view").forEach((view) => view.classList.remove("active-view"));
  $$(".nav-item[data-view]").forEach((item) => item.classList.toggle("active", item.dataset.view === name));
  $(`#${name}-view`).classList.add("active-view");
  if (name === "devices") renderExpandedDevices();
}

function deviceRow(device, removable = false) {
  const icon = device.type === "android" ? '<div class="phone-glyph"><i></i></div>' : '<div class="laptop-glyph"><i></i><b></b></div>';
  return `<div class="device-row" data-id="${esc(device.id)}"><div class="device-product">${icon}</div><div class="device-info"><strong>${esc(device.name)}</strong><small>${esc(device.os || titleFor(device.type))} <span class="middot">·</span> ${esc(device.host || "Paired device")}</small></div><span class="device-state"><i></i>${esc(device.state || "Connected")}</span><span class="last-seen">Active now</span><button class="row-more" data-device-menu="${esc(device.id)}" aria-label="${esc(device.name)} options">···</button>${removable ? `<div class="device-menu" hidden><button data-remove-device="${esc(device.id)}">Remove device</button></div>` : ""}</div>`;
}

function titleFor(type) {
  return type === "android" ? "Android" : type === "mac" ? "macOS" : "Windows";
}

function renderExpandedDevices() {
  const target = $("#expanded-device-list");
  if (!target) return;
  target.innerHTML = state.devices.map((device) => deviceRow(device, true)).join("");
  target.querySelectorAll("[data-device-menu]").forEach((button) => button.addEventListener("click", () => {
    const menu = button.closest(".device-row").querySelector(".device-menu");
    menu.hidden = !menu.hidden;
  }));
  target.querySelectorAll("[data-remove-device]").forEach((button) => button.addEventListener("click", () => removeDevice(button.dataset.removeDevice)));
}

function renderDeviceCounts() {
  const total = state.devices.length + 1;
  $("#nav-device-count").textContent = total;
  $(".page-heading .eyebrow").innerHTML = `<span class="live-indicator"></span> YOUR DESK <span class="eyebrow-divider">/</span> ${total} DEVICES`;
  $(".devices-panel .heading-count").textContent = total;
  const addRow = $(".add-device-row");
  addRow.innerHTML = `<span>＋</span> Pair another device`;
}

function openPairModal() {
  $("#pair-step").hidden = false;
  $("#info-step").hidden = true;
  $("#modal-mark").textContent = "✦";
  $("#modal-eyebrow").textContent = "ADD TO YOUR DESK";
  $("#modal-title").textContent = "Bring another screen in.";
  $("#modal-description").textContent = "Pair a nearby device and set it beside your PC. Your cursor will flow across the edge.";
  $("#modal-backdrop").hidden = false;
  $("#device-name").focus();
}

function openInfoModal(kind) {
  const content = {
    privacy: {
      eyebrow: "YOUR PRIVACY", title: "Your desk stays yours.", description: "Drift is designed for devices you can see and approve.", body: `<h3>How pairing works</h3><p>In a production build, devices would verify a one-time pairing code, then establish an encrypted connection on your local network. A device can be removed here at any time. The preview does not connect to other devices or transmit input.</p><button class="button button-secondary" data-close-modal>Got it</button>`,
    },
    shortcuts: {
      eyebrow: "QUICK CONTROL", title: "Keep your hands moving.", description: "A couple of ways to stay in control while you work.", body: `<h3>Proposed shortcuts</h3><p><b>Ctrl + Alt + ← / →</b> Switch to the previous or next screen<br /><b>Ctrl + Alt + Esc</b> Return pointer to this PC<br /><b>Hold at screen edge</b> Pause automatic handoff</p><button class="button button-secondary" data-close-modal>Got it</button>`,
    },
  }[kind];
  $("#pair-step").hidden = true;
  const info = $("#info-step");
  info.hidden = false;
  info.innerHTML = content.body;
  $("#modal-mark").textContent = kind === "privacy" ? "⌑" : "⌨";
  $("#modal-eyebrow").textContent = content.eyebrow;
  $("#modal-title").textContent = content.title;
  $("#modal-description").textContent = content.description;
  $("#modal-backdrop").hidden = false;
  info.querySelector("[data-close-modal]").addEventListener("click", closeModal);
}

function closeModal() {
  $("#modal-backdrop").hidden = true;
}

function pairDevice() {
  const nameInput = $("#device-name");
  const name = nameInput.value.trim();
  if (!name) {
    nameInput.focus();
    nameInput.placeholder = "Add a name to continue";
    nameInput.classList.add("input-error");
    setTimeout(() => nameInput.classList.remove("input-error"), 1000);
    return;
  }
  const type = $("#device-type").value;
  const code = `DRFT-${Math.floor(10 + Math.random() * 90)}`;
  const id = `custom-${Date.now()}`;
  const device = { id, name, label: name, type, os: titleFor(type), host: `${name.trim().replace(/\s+/g, "-")}.local`, state: "Connected" };
  state.devices.push(device);
  persist();
  renderDeviceCounts();
  if (state.currentView === "devices") renderExpandedDevices();
  $("#pair-step").hidden = true;
  $("#modal-mark").textContent = "✓";
  $("#modal-mark").classList.add("success-mark");
  $("#modal-eyebrow").textContent = "DEVICE ADDED";
  $("#modal-title").textContent = `${esc(name)} is on your desk.`;
  $("#modal-description").textContent = "The device is now in your paired-device list. The screen map shows the three-device desk preview.";
  $("#info-step").hidden = false;
  $("#info-step").innerHTML = `<div class="pair-success"><span>✦</span><div><strong>Paired in this preview</strong><small>Pair code ${code} · no device connection was made</small></div></div><button class="button button-secondary" data-close-modal>Done</button>`;
  $("#info-step [data-close-modal]").addEventListener("click", closeModal);
  showToast(`${name} added to your desk`);
  nameInput.value = "";
}

function removeDevice(id) {
  const removed = state.devices.find((device) => device.id === id);
  state.devices = state.devices.filter((device) => device.id !== id);
  persist();
  renderDeviceCounts();
  renderExpandedDevices();
  showToast(`${removed?.name || "Device"} removed from this preview`);
}

function animateEdge() {
  const cursor = $("#map-cursor");
  cursor.style.opacity = "1";
  cursor.style.left = "44%";
  cursor.style.top = "47%";
  cursor.animate([{ transform: "translate(0,0)" }, { transform: "translate(80px,-25px)" }, { transform: "translate(170px,-4px)" }], { duration: 1350, easing: "ease-in-out" });
  setTimeout(() => { cursor.style.left = "76%"; cursor.style.top = "44%"; }, 760);
  setTimeout(() => { cursor.style.opacity = "0"; }, 1900);
  showToast("Edge switching preview — no input is being sent");
}

const inputSources = [
  { icon: "⊞", label: "This PC · Logitech MX Keys" },
  { icon: "●", label: "MacBook Pro · Built-in keyboard" },
  { icon: "◉", label: "Pixel 8 · Touchscreen + keyboard" },
];

$("#mouse-toggle").addEventListener("click", (event) => { state.mouse = !state.mouse; syncShareState(); });
$("#keyboard-toggle").addEventListener("click", () => { state.keyboard = !state.keyboard; syncShareState(); });
$("#settings-edge-toggle").addEventListener("click", (event) => { state.edgeEnabled = !state.edgeEnabled; setToggle(event.currentTarget, state.edgeEnabled); showToast(`Edge switching ${state.edgeEnabled ? "enabled" : "paused"}`); });
$("#hold-toggle").addEventListener("click", (event) => { state.holding = !state.holding; setToggle(event.currentTarget, state.holding); showToast(`Edge hold ${state.holding ? "enabled" : "disabled"}`); });
$("#clipboard-toggle").addEventListener("click", (event) => { state.clipboard = !state.clipboard; setToggle(event.currentTarget, state.clipboard); showToast(`Clipboard sharing ${state.clipboard ? "enabled in settings" : "disabled"}`); });
$("#source-cycle").addEventListener("click", () => {
  state.sourceIndex = (state.sourceIndex + 1) % inputSources.length;
  const source = inputSources[state.sourceIndex];
  $("#source-label").textContent = source.label;
  $("#source-icon").textContent = source.icon;
  showToast(`Input source: ${source.label.split(" · ")[0]}`);
});
$("#try-edge").addEventListener("click", animateEdge);
$("#add-device-top").addEventListener("click", openPairModal);
$("#add-device-row").addEventListener("click", openPairModal);
$("#add-device-devices").addEventListener("click", openPairModal);
$("#pair-confirm").addEventListener("click", pairDevice);
$("#device-name").addEventListener("keydown", (event) => { if (event.key === "Enter") pairDevice(); });
$("#modal-close").addEventListener("click", closeModal);
$("#modal-backdrop").addEventListener("click", (event) => { if (event.target === event.currentTarget) closeModal(); });
$(document).addEventListener("keydown", (event) => { if (event.key === "Escape") closeModal(); });
$("#reset-layout").addEventListener("click", () => {
  const map = $("#screen-map");
  ["mac", "windows", "android"].forEach((id) => map.append($(`.device-screen[data-device="${id}"]`, map)));
  showToast("Screen layout reset");
});

$$(".nav-item[data-view]").forEach((item) => item.addEventListener("click", () => showView(item.dataset.view)));
$$('[data-view="devices"].text-button').forEach((item) => item.addEventListener("click", () => showView("devices")));
$$('[data-open="privacy"]').forEach((item) => item.addEventListener("click", () => openInfoModal("privacy")));
$$('[data-open="shortcuts"]').forEach((item) => item.addEventListener("click", () => openInfoModal("shortcuts")));

// The map can be rearranged locally so the preview can reflect different desk layouts.
let draggedDevice = null;
$$('.device-screen').forEach((screen) => {
  screen.draggable = true;
  screen.addEventListener("dragstart", () => { draggedDevice = screen; screen.classList.add("dragging"); });
  screen.addEventListener("dragend", () => { draggedDevice = null; screen.classList.remove("dragging"); });
  screen.addEventListener("dragover", (event) => event.preventDefault());
  screen.addEventListener("drop", (event) => {
    event.preventDefault();
    if (draggedDevice && draggedDevice !== screen) {
      const map = $("#screen-map");
      const screens = $$(".device-screen", map);
      const from = screens.indexOf(draggedDevice);
      const to = screens.indexOf(screen);
      if (from < to) screen.after(draggedDevice); else screen.before(draggedDevice);
      showToast("Screen layout updated");
    }
  });
});

renderDeviceCounts();
