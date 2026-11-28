const number = new Intl.NumberFormat();
const clock = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit", second: "2-digit" });
const refreshInterval = 30_000;
const byId = id => document.getElementById(id);
const games = [
    { id: "nonogram", name: "Nonogram" },
    { id: "hashi", name: "Hashi", generator: "Bridges" },
    { id: "fill-a-pix", name: "Fill-a-Pix" },
    { id: "slitherlink", name: "Slitherlink", generator: "Loopy" },
    { id: "nurikabe", name: "Nurikabe", generator: "KoboLogicPuzzles" },
    { id: "galaxies", name: "Galaxies" },
    { id: "link-a-pix", name: "Link-a-Pix" },
    { id: "tents", name: "Tents and Trees", generator: "Simon Tatham's Tents" },
    { id: "battleships", name: "Battleships", generator: "KoboLogicPuzzles" },
];
const generators = games.filter(game => game.generator);
const state = { data: null, pending: false, online: false, game: "hashi", filters: {}, lastRefresh: 0 };

const resultNames = {
    stored: "Puzzle stored",
    verified: "Puzzle verified",
    duplicate: "Duplicate puzzle",
    generation_timeout: "Generation timed out",
    generation_failed: "Generation failed",
    generator_unavailable: "Generator unavailable",
    verification_timeout: "Uniqueness proof timed out",
    multiple_solutions: "Multiple solutions",
    invalid: "Invalid puzzle",
    outside_target: "Rating outside stock targets",
    validation_rejected: "Validation rejected",
    density_rejected: "Below density requirement",
    rating_rejected: "Rating outside stock targets",
};

function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}

function label(value) {
    if (value === "square") return "Squares";
    if (value === "penrose-kite-dart") return "Penrose kite and dart";
    return value.replaceAll("_", " ").replace(/(^|[\s-])\w/g, letter => letter.toUpperCase());
}

function sum(rows, field) {
    return rows.reduce((total, row) => total + row[field], 0);
}

function stockEntries(game, data) {
    if (["nurikabe", "tents", "battleships"].includes(game)) {
        const entries = new Map();
        const key = row => `${row.size}/${row.difficulty}`;
        for (const row of data[game].stock) {
            entries.set(key(row), { ...row, ready: row.unstarted, unstarted: 0 });
        }
        for (const row of data[game].actualStock) {
            const id = key(row);
            const entry = entries.get(id) ?? { ...row, ready: 0, target: 0, unstarted: 0 };
            entry.unstarted += row.unstarted;
            entries.set(id, entry);
        }
        return Array.from(entries.values());
    }
    if (game === "slitherlink") {
        return [
            ...data.slitherlink.stock.map(row => ({
                ...row, category: row.tiling, ready: row.unstarted,
            })),
            ...(data.slitherlink.untargetedStock ?? []).map(row => ({
                ...row, category: row.tiling, ready: 0, target: 0,
            })),
        ];
    }

    const entries = new Map();
    const key = row => JSON.stringify([row.size, row.difficulty, row.solvability]);
    for (const row of data.hashi.targets) {
        entries.set(key(row), {
            ...row, category: row.solvability, ready: row.unstarted, unstarted: 0,
        });
    }
    for (const row of data.hashi.stock) {
        const id = key(row);
        const target = entries.get(id);
        if (target) {
            target.unstarted = row.unstarted;
        } else {
            entries.set(id, { ...row, category: row.solvability, ready: 0, target: 0 });
        }
    }
    return Array.from(entries.values());
}

function fillCount(rows) {
    return rows.reduce((total, row) => total + Math.min(row.ready, row.target), 0);
}

function renderSummary(data) {
    const otherGames = data.catalog.filter(row => row.game !== "link-a-pix");
    const total = sum(otherGames, "total") + data.linkAPix.total;
    const unstarted = sum(otherGames, "unstarted") + data.linkAPix.unstarted;
    byId("catalog-total").textContent = number.format(total);
    byId("catalog-detail").textContent = `${number.format(unstarted)} unstarted across all games`;

    const stock = generators.flatMap(game => stockEntries(game.id, data));
    const totalTarget = sum(stock, "target");
    const filled = fillCount(stock);
    const coverage = totalTarget > 0 ? Math.round(100 * filled / totalTarget) : 0;
    byId("coverage").textContent = totalTarget > 0 ? `${coverage}%` : "—";
    byId("coverage-detail").textContent = totalTarget > 0
        ? `${number.format(filled)} of ${number.format(totalTarget)} target slots filled`
        : "No stock targets configured";
    byId("coverage-meter").style.width = `${coverage}%`;

    const statuses = generators.map(game => data[game.id]);
    const active = statuses.filter(status => status.activity?.current).length;
    const enabled = statuses.filter(status => status.enabled).length;
    byId("worker-total").textContent = `${active} / ${statuses.length}`;
    byId("worker-detail").textContent = enabled === 1 ? "1 generator enabled" : `${enabled} generators enabled`;
}

function stockCell(rows) {
    const td = element("td");
    if (rows.length === 0) {
        td.append(element("span", "no-cell", "—"));
        return td;
    }

    const target = sum(rows, "target");
    const available = sum(rows, "unstarted");
    const filled = fillCount(rows);
    const cell = element("div", "stock-cell");
    const count = element("div", "stock-count");
    cell.append(count);
    if (target === 0) {
        cell.dataset.tone = "untargeted";
        count.append(element("strong", "", number.format(available)), element("small", "", "unstarted"));
    } else {
        cell.dataset.tone = filled === target ? "met" : filled > 0 ? "partial" : "missing";
        cell.title = `${number.format(available)} unstarted puzzles in this group`;
        count.append(element("strong", "", number.format(filled)), element("small", "", `/ ${number.format(target)}`));
        const meter = element("div", "stock-meter");
        meter.setAttribute("aria-hidden", "true");
        const bar = element("span");
        bar.style.width = `${100 * filled / target}%`;
        meter.append(bar);
        cell.append(meter);
    }
    td.append(cell);
    return td;
}

function renderInventory() {
    const content = byId("stock-content");
    const note = byId("stock-note");
    const select = byId("stock-filter");
    const game = state.game;
    const isSlitherlink = game === "slitherlink";
    const rows = state.data ? stockEntries(game, state.data) : [];
    const categories = [...new Set(rows.map(row => row.category).filter(Boolean))].sort((a, b) => label(a).localeCompare(label(b)));
    const selection = categories.includes(state.filters[game]) ? state.filters[game] : "";
    byId("stock-filter-label").textContent = isSlitherlink ? "Tiling" : "Solvability";
    const options = [new Option(isSlitherlink ? "All tilings" : "All levels", "")];
    for (const category of categories) {
        options.push(new Option(label(category), category));
    }
    if (options.length !== select.options.length
        || options.some((option, index) => option.value !== select.options[index].value
            || option.text !== select.options[index].text)) {
        select.replaceChildren(...options);
    }
    select.value = selection;
    select.disabled = categories.length === 0;
    select.closest(".filter").hidden = categories.length === 0;

    const selected = rows.filter(row => !selection || row.category === selection);
    if (selected.length === 0) {
        const message = state.data ? "No stock or targets for this game." :
            state.online || !state.lastRefresh ? "Loading inventory…" : "Inventory data unavailable.";
        content.replaceChildren(element("p", "empty-state", message));
        note.hidden = true;
        return;
    }

    const levels = isSlitherlink ? ["easy", "normal", "tricky", "hard"] : ["easy", "medium", "hard", "expert"];
    if (selected.some(row => row.difficulty == null)) levels.push(null);
    const sizes = [...new Set(selected.map(row => row.size))].sort((a, b) => a - b);
    const table = element("table", "stock-table");
    table.setAttribute("aria-label", `${games.find(item => item.id === game).name} unstarted inventory`);
    const thead = table.createTHead();
    const headers = thead.insertRow();
    for (const name of ["Board size", ...levels.map(level => level == null ? "Unrated" : label(level))]) {
        const th = element("th", "", name);
        th.scope = "col";
        headers.append(th);
    }

    const tbody = table.createTBody();
    for (const size of sizes) {
        const row = tbody.insertRow();
        const sizeCell = element("th", "", `${size} × ${size}`);
        sizeCell.scope = "row";
        row.append(sizeCell);
        for (const level of levels) {
            row.append(stockCell(selected.filter(item => item.size === size && item.difficulty === level)));
        }
    }
    content.replaceChildren(table);
    const filled = fillCount(selected);
    const target = sum(selected, "target");
    const outside = Math.max(0, sum(selected, "unstarted") - filled);
    const coverage = target > 0
        ? `${number.format(filled)} of ${number.format(target)} target slots filled`
        : "No targets for this selection";
    note.textContent = outside > 0
        ? `${coverage} · ${number.format(outside)} ${target > 0 ? "additional " : ""}unstarted ${outside === 1 ? "puzzle" : "puzzles"}`
        : coverage;
    note.hidden = false;
}

function selectGame(game) {
    state.game = game;
    for (const tab of document.querySelectorAll(".tabs [role='tab']")) {
        const selected = tab.id === `tab-${game}`;
        tab.setAttribute("aria-selected", selected);
        tab.tabIndex = selected ? 0 : -1;
    }
    byId("stock-panel").setAttribute("aria-labelledby", `tab-${game}`);
    renderInventory();
}

function boardLabel(job) {
    return [`${job.size} × ${job.size}`, job.tiling, job.difficulty, job.solvability]
        .filter(Boolean).map(label).join(" · ");
}

function duration(start, end) {
    const seconds = Math.max(0, Math.floor((end - Date.parse(start)) / 1_000));
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    if (hours > 0) return `${hours}h ${minutes % 60}m`;
    if (minutes > 0) return `${minutes}m ${seconds % 60}s`;
    return `${seconds}s`;
}

function renderWorkers(data) {
    const target = byId("workers");
    const expanded = new Set(
        Array.from(target.querySelectorAll("details[open]"), detail => detail.closest(".worker").dataset.game),
    );
    const cards = generators.map(({ id, name, generator }) => {
        const status = data[id];
        const activity = status.activity ?? {};
        const current = activity.current;
        const article = element("article", "worker");
        article.dataset.game = id;
        const head = element("div", "worker-header");
        const nameplate = element("div", "worker-name");
        const mark = element("span", "worker-mark", name[0]);
        mark.setAttribute("aria-hidden", "true");
        nameplate.append(mark, element("h3", "", name));
        const verifying = current?.stage === "verifying";
        const pill = element("span", "pill", current ? (verifying ? "Verifying" : "Generating") : status.enabled ? "Idle" : "Disabled");
        pill.dataset.state = current ? (verifying ? "verifying" : "active") : "idle";
        head.append(nameplate, pill);
        article.append(head, element("p", "worker-meta", `Generated by ${generator}`));
        if (current) {
            const elapsed = element("p", "worker-meta");
            elapsed.dataset.startedAt = current.startedAt;
            article.append(element("p", "worker-current", boardLabel(current)), elapsed);
        }

        const latest = element("div", "worker-latest");
        if (activity.last) {
            const { status: outcome, job, finishedAt } = activity.last;
            const result = element("p", "result", resultNames[outcome] ?? label(outcome));
            result.dataset.tone = ["stored", "verified"].includes(outcome) ? "success" :
                outcome.startsWith("generation_") ? "error" : "neutral";
            latest.append(result, element("p", "worker-meta",
                `${boardLabel(job)} · ${clock.format(new Date(finishedAt))}`));
        } else {
            latest.append(element("p", "worker-meta", "No completed attempts in this session"));
        }
        article.append(latest);
        if (status.collection?.enabled || status.collection?.checkedAt) {
            const { checkedAt, nextIndex, cursor } = status.collection;
            const message = nextIndex > 0 ? `Janko refresh · ${number.format(nextIndex)} checked`
                : cursor ? `Janko refresh · through puzzle ${cursor.split(".")[0]}`
                : checkedAt ? `Janko checked ${new Date(checkedAt).toLocaleDateString()}`
                : "Janko refresh pending";
            article.append(element("p", "worker-meta", message));
        }

        const outcomes = Object.entries(activity.outcomes ?? {});
        if (outcomes.length > 0) {
            const details = element("details");
            details.open = expanded.has(id);
            const count = outcomes.reduce((total, [, value]) => total + value, 0);
            details.append(element("summary", "", `${number.format(count)} ${count === 1 ? "attempt" : "attempts"} this session`));
            const breakdown = element("dl", "outcomes");
            for (const [outcome, count] of outcomes.sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
                breakdown.append(element("dt", "", resultNames[outcome] ?? label(outcome)), element("dd", "", number.format(count)));
            }
            details.append(breakdown);
            article.append(details);
        }
        return article;
    });
    target.replaceChildren(...cards);
    updateElapsedTime();
}

function updateElapsedTime() {
    const end = state.online ? Date.now() : Date.parse(state.data?.sampledAt);
    if (!Number.isFinite(end)) return;
    for (const node of document.querySelectorAll("[data-started-at]")) {
        node.textContent = `Running for ${duration(node.dataset.startedAt, end)}`;
    }
}

function renderCatalog(catalog, linkAPix) {
    const cards = games.map(({ id, name }) => {
        const sources = catalog.filter(row => row.game === id && row.total > 0);
        const card = element("article", "catalog-card");
        const total = id === "link-a-pix" ? linkAPix.total : sum(sources, "total");
        const unstarted = id === "link-a-pix" ? linkAPix.unstarted : sum(sources, "unstarted");
        card.append(element("h3", "", name),
            element("p", "catalog-card-total", number.format(total)),
            element("p", "catalog-card-detail", `${number.format(unstarted)} unstarted`));
        if (sources.length > 0) {
            const breakdown = element("dl", "sources");
            for (const source of sources) {
                const name = source.sourceKind === "generated" ? `Generated by ${source.sourceName}` : source.sourceName;
                breakdown.append(element("dt", "", name), element("dd", "", number.format(source.total)));
            }
            card.append(breakdown);
        }
        if (id === "link-a-pix") {
            const labels = { janko: "Janko", playsudoku: "PlaySudoku", graycell: "GrayCell", grandgames: "GrandGames" };
            for (const [source, status] of Object.entries(linkAPix.sources)) {
                if (!status.enabled && !status.checkedAt && !status.cursor) continue;
                const activity = status.cursor ? "Scan in progress" : status.checkedAt
                    ? `Checked ${new Date(status.checkedAt).toLocaleDateString()}` : "Refresh pending";
                const unresolved = status.inconclusive > 0 ? ` · ${number.format(status.inconclusive)} inconclusive` : "";
                card.append(element("p", "catalog-card-detail", `${labels[source]} · ${activity}${unresolved}`));
            }
        }
        return card;
    });
    byId("catalog-content").replaceChildren(...cards);
}

async function refresh() {
    if (state.pending) return;
    state.pending = true;
    const button = byId("refresh");
    button.disabled = true;
    button.textContent = "Refreshing…";
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10_000);
    const connection = byId("connection");
    const error = byId("fetch-error");
    try {
        const response = await fetch("api/v2/dashboard", {
            cache: "no-store",
            headers: { Accept: "application/json" },
            signal: controller.signal,
        });
        if (!response.ok) throw new Error(`Dashboard request failed with ${response.status}`);
        const data = await response.json();
        state.data = data;
        state.online = true;
        renderSummary(data);
        renderInventory();
        renderWorkers(data);
        renderCatalog(data.catalog, data.linkAPix);
        connection.dataset.state = "online";
        byId("connection-label").textContent = "Server connected";
        byId("updated").textContent = `Updated ${clock.format(new Date(data.sampledAt))} · Refreshes every 30 seconds`;
        error.hidden = true;
    } catch {
        state.online = false;
        connection.dataset.state = "offline";
        byId("connection-label").textContent = state.data ? "Showing last update" : "Server data unavailable";
        error.textContent = state.data
            ? "Could not refresh server data. The dashboard shows the last successful update."
            : "Could not load server data. Use Refresh to try again.";
        error.hidden = false;
        if (!state.data) {
            byId("updated").textContent = "Automatic refresh will retry";
            byId("stock-content").replaceChildren(element("p", "empty-state", "Inventory data unavailable."));
            byId("workers").replaceChildren(element("p", "empty-state", "Worker status unavailable."));
            byId("catalog-content").replaceChildren(element("p", "empty-state", "Catalog data unavailable."));
        }
        updateElapsedTime();
    } finally {
        clearTimeout(timeout);
        state.lastRefresh = Date.now();
        state.pending = false;
        button.disabled = false;
        button.textContent = "Refresh";
    }
}

for (const tab of document.querySelectorAll(".tabs [role='tab']")) {
    tab.addEventListener("click", () => selectGame(tab.id.slice(4)));
    tab.addEventListener("keydown", event => {
        if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        const index = generators.findIndex(game => game.id === state.game);
        const direction = event.key === "ArrowLeft" ? -1 : 1;
        const nextIndex = event.key === "Home" ? 0 : event.key === "End" ? generators.length - 1
            : (index + direction + generators.length) % generators.length;
        const next = generators[nextIndex].id;
        selectGame(next);
        byId(`tab-${next}`).focus();
    });
}

byId("stock-filter").addEventListener("change", event => {
    state.filters[state.game] = event.target.value;
    renderInventory();
});
byId("refresh").addEventListener("click", refresh);

function markPage() {
    const section = location.hash || "#overview";
    for (const link of document.querySelectorAll(".navigation a")) {
        if (link.hash === section) link.setAttribute("aria-current", "location");
        else link.removeAttribute("aria-current");
    }
}

window.addEventListener("hashchange", markPage);
document.addEventListener("visibilitychange", () => {
    if (!document.hidden && Date.now() - state.lastRefresh >= refreshInterval) refresh();
});
setInterval(() => { if (!document.hidden) refresh(); }, refreshInterval);
setInterval(() => { if (!document.hidden) updateElapsedTime(); }, 1_000);
markPage();
refresh();
