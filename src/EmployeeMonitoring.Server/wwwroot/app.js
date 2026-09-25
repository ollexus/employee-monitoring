"use strict";

const state = {
    clients: [],
    filter: "",
    onlyActive: false,
    autoRefresh: true,
    intervalMs: 5000,
    selected: null,
    timer: null,
    loading: false
};

const rows = new Map();

const el = {
    serverInfo: document.getElementById("serverInfo"),
    linkDot: document.getElementById("linkDot"),
    linkText: document.getElementById("linkText"),
    search: document.getElementById("searchBox"),
    onlyActive: document.getElementById("onlyActive"),
    autoRefresh: document.getElementById("autoRefresh"),
    interval: document.getElementById("refreshInterval"),
    refreshNow: document.getElementById("refreshNow"),
    statOnline: document.getElementById("statOnline"),
    statTotal: document.getElementById("statTotal"),
    statActive: document.getElementById("statActive"),
    statShots: document.getElementById("statShots"),
    tbody: document.getElementById("clientRows"),
    empty: document.getElementById("emptyState"),
    modal: document.getElementById("screenshotModal"),
    modalTitle: document.getElementById("modalTitle"),
    modalImage: document.getElementById("modalImage"),
    modalDetails: document.getElementById("modalDetails"),
    toasts: document.getElementById("toasts")
};

const ACTIVE_IDLE_SECONDS = 300;

document.addEventListener("DOMContentLoaded", () => {
    el.search.addEventListener("input", () => {
        state.filter = el.search.value.trim().toLowerCase();
        render();
    });
    el.onlyActive.addEventListener("change", () => {
        state.onlyActive = el.onlyActive.checked;
        render();
    });
    el.autoRefresh.addEventListener("change", () => {
        state.autoRefresh = el.autoRefresh.checked;
        scheduleRefresh();
    });
    el.interval.addEventListener("change", () => {
        state.intervalMs = Number(el.interval.value);
        scheduleRefresh();
    });
    el.refreshNow.addEventListener("click", () => load());
    el.modal.addEventListener("click", event => {
        if (event.target.dataset.close === "true") {
            closeModal();
        }
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && !el.modal.hidden) {
            closeModal();
        }
    });

    load();
});

function scheduleRefresh() {
    if (state.timer) {
        clearTimeout(state.timer);
        state.timer = null;
    }
    if (state.autoRefresh) {
        state.timer = setTimeout(() => load().finally(scheduleRefresh), state.intervalMs);
    }
}

async function load() {
    if (state.loading) {
        return;
    }
    state.loading = true;
    try {
        const response = await fetch("api/clients", { cache: "no-store" });
        if (!response.ok) {
            throw new Error("HTTP " + response.status);
        }
        const data = await response.json();
        state.clients = data.clients || [];
        setLinkState(true);
        el.serverInfo.textContent =
            `сервер: ${data.serverName || "—"} · шлюз агентов: TCP ${data.agentPort} · ` +
            `подключений: ${data.activeConnections} · интервал снимка: ${data.captureIntervalSeconds} с · ` +
            `обновлено: ${new Date().toLocaleTimeString("ru-RU")}`;
        render();
    } catch (error) {
        setLinkState(false, error.message);
    } finally {
        state.loading = false;
    }
}

function setLinkState(ok, message) {
    el.linkDot.className = "dot " + (ok ? "ok" : "bad");
    el.linkText.textContent = ok ? "сервер доступен" : "нет связи с сервером" + (message ? " (" + message + ")" : "");
}

function matches(client) {
    if (state.onlyActive && !client.isOnline) {
        return false;
    }
    if (state.onlyActive && client.idleSeconds > ACTIVE_IDLE_SECONDS) {
        return false;
    }
    if (!state.filter) {
        return true;
    }
    return [client.machineName, client.userName, client.domain, client.ipAddress, client.activeProcessName]
        .some(value => (value || "").toLowerCase().includes(state.filter));
}

function render() {
    const visible = state.clients.filter(matches);
    const seen = new Set();

    for (const client of visible) {
        seen.add(client.clientId);
        let row = rows.get(client.clientId);
        if (!row) {
            row = createRow(client);
            rows.set(client.clientId, row);
            el.tbody.appendChild(row.tr);
        }
        updateRow(row, client);
    }

    for (const [id, row] of rows) {
        if (!seen.has(id)) {
            row.tr.remove();
            rows.delete(id);
        }
    }

    el.statOnline.textContent = state.clients.filter(c => c.isOnline).length;
    el.statTotal.textContent = state.clients.length;
    el.statActive.textContent = state.clients.filter(c => c.isOnline && c.idleSeconds < ACTIVE_IDLE_SECONDS).length;
    el.statShots.textContent = state.clients.filter(c => c.hasScreenshot).length;
    el.empty.hidden = state.clients.length > 0;
}

function createRow(client) {
    const tr = document.createElement("tr");

    const tdStatus = document.createElement("td");
    const tdMachine = document.createElement("td");
    const tdDomain = document.createElement("td");
    const tdUser = document.createElement("td");
    const tdIp = document.createElement("td");
    const tdActivity = document.createElement("td");
    const tdResources = document.createElement("td");
    const tdSeen = document.createElement("td");
    const tdShot = document.createElement("td");
    tdShot.className = "shot-cell";
    const tdActions = document.createElement("td");

    const windowTitle = document.createElement("div");
    windowTitle.className = "window-title";
    const activityMeta = document.createElement("div");
    activityMeta.className = "muted mono";
    const idle = document.createElement("div");
    idle.className = "muted";
    tdActivity.append(windowTitle, activityMeta, idle);

    const cpu = document.createElement("div");
    const memory = document.createElement("div");
    memory.className = "muted";
    tdResources.append(cpu, memory);

    const seen = document.createElement("div");
    const uptime = document.createElement("div");
    uptime.className = "muted";
    tdSeen.append(seen, uptime);

    const actions = document.createElement("div");
    actions.className = "actions";
    const shotButton = document.createElement("button");
    shotButton.textContent = "Снимок";
    shotButton.title = "Запросить снимок экрана у агента";
    const openButton = document.createElement("button");
    openButton.className = "ghost";
    openButton.textContent = "Открыть";
    const removeButton = document.createElement("button");
    removeButton.className = "danger";
    removeButton.textContent = "Удалить";
    actions.append(shotButton, openButton, removeButton);
    tdActions.append(actions);

    tr.append(tdStatus, tdMachine, tdDomain, tdUser, tdIp, tdActivity, tdResources, tdSeen, tdShot, tdActions);

    return {
        tr, tdStatus, tdMachine, tdDomain, tdUser, tdIp, tdActivity,
        windowTitle, activityMeta, idle, cpu, memory, seen, uptime,
        tdShot, shotButton, openButton, removeButton, shotStamp: null, shotNode: null
    };
}

function updateRow(row, client) {
    setCell(row.tdMachine, client.machineName || "—", true);
    setCell(row.tdDomain, client.domain || "—");
    setCell(row.tdUser, client.userName || "—", true);
    setCell(row.tdIp, client.ipAddress || "—", true);

    const status = document.createElement("span");
    let label;
    let className;
    if (!client.isOnline) {
        label = "не в сети";
        className = "badge offline";
    } else if (client.screenLocked) {
        label = "экран заблокирован";
        className = "badge locked";
    } else if (client.idleSeconds >= ACTIVE_IDLE_SECONDS) {
        label = "бездействует";
        className = "badge afk";
    } else {
        label = "активен";
        className = "badge online";
    }
    status.className = className;
    status.textContent = label;
    row.tdStatus.replaceChildren(status);

    row.windowTitle.textContent = client.activeWindowTitle || "—";
    row.windowTitle.title = client.activeWindowTitle || "";
    row.activityMeta.textContent = client.activeProcessName || "";
    row.idle.textContent = "бездействие: " + formatDuration(client.idleSeconds);

    row.cpu.textContent = "CPU " + (client.cpuLoadPercent || 0).toFixed(0) + " %";
    row.memory.textContent = "ОЗУ " + formatBytes(client.usedMemoryBytes) + " / " + formatBytes(client.totalMemoryBytes);

    row.seen.textContent = formatDateTime(client.lastSeenUtc);
    row.uptime.textContent = client.uptimeSeconds ? "работает: " + formatDuration(client.uptimeSeconds) : "";

    row.shotButton.disabled = !client.isOnline;
    row.openButton.disabled = !client.hasScreenshot;
    row.removeButton.onclick = () => removeClient(client);
    row.shotButton.onclick = () => sendCommand(client, "captureScreenshot", "Запрос снимка экрана отправлен");
    row.openButton.onclick = () => openModal(client);

    updateScreenshotCell(row, client);
}

function updateScreenshotCell(row, client) {
    const stamp = client.screenshotAtUtc || null;
    if (row.shotStamp === stamp && row.shotNode) {
        if (row.shotNode.dataset.locked !== String(client.screenshotScreenLocked)) {
            row.shotNode.dataset.locked = String(client.screenshotScreenLocked);
        }
        return;
    }

    row.shotStamp = stamp;
    if (!client.hasScreenshot) {
        const placeholder = document.createElement("div");
        placeholder.className = "shot-placeholder";
        placeholder.textContent = client.screenshotScreenLocked ? "экран заблокирован" : "снимок не получен";
        row.tdShot.replaceChildren(placeholder);
        row.shotNode = null;
        return;
    }

    const img = document.createElement("img");
    img.className = "shot";
    img.alt = "Снимок экрана: " + client.machineName;
    img.loading = "lazy";
    img.dataset.locked = String(client.screenshotScreenLocked);
    img.src = `api/clients/${encodeURIComponent(client.clientId)}/screenshot?v=${encodeURIComponent(stamp || "0")}`;
    img.onclick = () => openModal(client);
    row.tdShot.replaceChildren(img);
    row.shotNode = img;
}

function setCell(td, text, strong) {
    if (td.textContent !== text || td.dataset.strong !== String(!!strong)) {
        td.textContent = text;
        td.dataset.strong = String(!!strong);
        td.style.fontWeight = strong ? "600" : "";
    }
}

function findClient(clientId) {
    return state.clients.find(client => client.clientId === clientId) || null;
}

async function sendCommand(client, action, successMessage) {
    try {
        const response = await fetch(`api/clients/${encodeURIComponent(client.clientId)}/command`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ action })
        });
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
            throw new Error(data.message || "HTTP " + response.status);
        }
        toast(successMessage, "ok");
        setTimeout(load, 1200);
        setTimeout(load, 3000);
    } catch (error) {
        toast("Ошибка: " + error.message, "error");
    }
}

async function removeClient(client) {
    if (!confirm(`Удалить ${client.machineName} \\ ${client.userName} из списка?`)) {
        return;
    }
    try {
        const response = await fetch(`api/clients/${encodeURIComponent(client.clientId)}`, { method: "DELETE" });
        if (!response.ok) {
            throw new Error("HTTP " + response.status);
        }
        toast("Агент удалён из списка", "ok");
        load();
    } catch (error) {
        toast("Ошибка: " + error.message, "error");
    }
}

function openModal(client) {
    state.selected = client;
    el.modalTitle.textContent = `${client.machineName} \\ ${client.userName} — снимок экрана`;
    el.modalImage.src = `api/clients/${encodeURIComponent(client.clientId)}/screenshot?v=${encodeURIComponent(client.screenshotAtUtc || "0")}&full=1`;
    el.modalDetails.replaceChildren(...detailPairs(client));
    el.modal.hidden = false;
}

function closeModal() {
    el.modal.hidden = true;
    el.modalImage.removeAttribute("src");
    state.selected = null;
}

function detailPairs(client) {
    const entries = [
        ["Компьютер", client.machineName],
        ["Домен", client.domain || "—"],
        ["Пользователь", client.userName],
        ["IP-адрес", client.ipAddress],
        ["MAC-адрес", client.macAddress || "—"],
        ["Состояние", client.isOnline ? "в сети" : "не в сети"],
        ["Активное окно", client.activeWindowTitle || "—"],
        ["Процесс", client.activeProcessName || "—"],
        ["Бездействие", formatDuration(client.idleSeconds)],
        ["CPU", (client.cpuLoadPercent || 0).toFixed(1) + " %"],
        ["ОЗУ", formatBytes(client.usedMemoryBytes) + " / " + formatBytes(client.totalMemoryBytes)],
        ["В сети с", formatDateTime(client.connectedAtUtc)],
        ["Последняя связь", formatDateTime(client.lastSeenUtc)],
        ["Последний снимок", formatDateTime(client.screenshotAtUtc)],
        ["Разрешение снимка", client.screenshotWidth && client.screenshotHeight ? `${client.screenshotWidth}×${client.screenshotHeight}` : "—"],
        ["Мониторов", client.screenCount],
        ["Сессия Windows", client.sessionId],
        ["Версия агента", client.agentVersion],
        ["ОС", client.osDescription],
        ["Агент запущен", formatDateTime(client.agentStartedAtUtc)]
    ];

    return entries.flatMap(([term, value]) => {
        const dt = document.createElement("dt");
        dt.textContent = term;
        const dd = document.createElement("dd");
        dd.textContent = value === null || value === undefined || value === "" ? "—" : String(value);
        return [dt, dd];
    });
}

function toast(message, kind) {
    const node = document.createElement("div");
    node.className = "toast " + (kind || "");
    node.textContent = message;
    el.toasts.append(node);
    setTimeout(() => node.remove(), 4200);
}

function formatDateTime(value) {
    if (!value) {
        return "—";
    }
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return "—";
    }
    return date.toLocaleString("ru-RU") + " (" + relativeTime(date) + ")";
}

function relativeTime(date) {
    const seconds = Math.round((Date.now() - date.getTime()) / 1000);
    if (seconds < 60) {
        return seconds + " с назад";
    }
    if (seconds < 3600) {
        return Math.round(seconds / 60) + " мин назад";
    }
    if (seconds < 86400) {
        return Math.round(seconds / 3600) + " ч назад";
    }
    return Math.round(seconds / 86400) + " дн назад";
}

function formatDuration(seconds) {
    const value = Number(seconds) || 0;
    if (value < 60) {
        return value + " с";
    }
    if (value < 3600) {
        return Math.floor(value / 60) + " мин " + (value % 60) + " с";
    }
    const hours = Math.floor(value / 3600);
    return hours + " ч " + Math.floor((value % 3600) / 60) + " мин";
}

function formatBytes(bytes) {
    const value = Number(bytes) || 0;
    if (value <= 0) {
        return "—";
    }
    const units = ["Б", "КБ", "МБ", "ГБ"];
    let index = 0;
    let result = value;
    while (result >= 1024 && index < units.length - 1) {
        result /= 1024;
        index++;
    }
    return result.toFixed(result >= 100 || index === 0 ? 0 : 1) + " " + units[index];
}
