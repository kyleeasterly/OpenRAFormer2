/* Native Jev inspector. Uses the running dashboard's existing read-only APIs. */
"use strict";

window.JevViewer = (() => {
  const root = document.createElement("section");
  root.id = "jevViewer";
  root.hidden = true;
  root.setAttribute("role", "dialog");
  root.setAttribute("aria-modal", "true");
  root.setAttribute("aria-labelledby", "jvTitle");
  root.innerHTML = `
    <div class="jv-head">
      <button id="jvBack" type="button">← Map</button>
      <div class="jv-identity"><h1 id="jvTitle"></h1><div id="jvModel" class="jv-model"></div></div>
      <div class="jv-controls">
        <span id="jvMode" class="jv-mode"><span class="jv-dot"></span><span id="jvModeText">LIVE</span></span>
        <button id="jvFollow" type="button">Pause</button>
        <button id="jvHistoryButton" type="button" aria-expanded="false" aria-controls="jvHistory">Turn history</button>
      </div>
    </div>
    <div class="jv-metrics">
      <div><span class="jv-metric-label">Latest API time · live</span><span id="jvLatency" class="jv-metric-value">—</span></div>
      <div><span class="jv-metric-label">Average API time · live</span><span id="jvAverage" class="jv-metric-value">—</span></div>
      <div><span class="jv-metric-label">Calls / second · live</span><span id="jvRate" class="jv-metric-value">—</span></div>
    </div>
    <div id="jvFrame" class="jv-frame">
      <span id="jvTurn" class="jv-turn">Waiting for a turn</span>
      <span id="jvGameTime"></span><span id="jvQuestions"></span><span id="jvTokens"></span>
    </div>
    <div id="jvNotice" class="jv-notice" hidden></div>
    <div class="jv-panes">
      <section class="jv-pane" aria-label="Jev input">
        <div class="jv-pane-head"><h2>Input</h2><div class="jv-tabs" aria-label="Input format">
          <button type="button" data-input="state" aria-pressed="true">State</button>
          <button type="button" data-input="questions" aria-pressed="false">Questions</button>
          <button type="button" data-input="request" aria-pressed="false">Full request</button>
        </div></div>
        <div id="jvInput" class="jv-content" tabindex="0" aria-label="Input content"></div>
      </section>
      <section class="jv-pane" aria-label="Jev output">
        <div class="jv-pane-head"><h2>Output</h2><div class="jv-tabs" aria-label="Output format">
          <button type="button" data-output="decisions" aria-pressed="true">Decisions</button>
          <button type="button" data-output="response" aria-pressed="false">Raw response</button>
          <button type="button" data-output="orders" aria-pressed="false">Orders</button>
        </div></div>
        <div id="jvOutput" class="jv-content" tabindex="0" aria-label="Output content"></div>
      </section>
    </div>
    <aside id="jvHistory" class="jv-history" aria-label="Turn history" hidden>
      <div class="jv-history-head"><h2>Turn history</h2><button id="jvHistoryClose" type="button" aria-label="Close turn history">✕</button></div>
      <div id="jvHistoryList" class="jv-history-list"></div>
    </aside>`;
  document.body.append(root);
  const $ = id => root.querySelector("#" + id);
  const state = {
    player: null, mode: "live", latest: null, selected: null, displayed: null,
    data: null, status: null, input: "state", output: "decisions", samples: [],
    version: 0, busy: false, controller: null, timer: null, returnFocus: null,
    cache: new Map(), connected: true, finished: false, error: ""
  };
  const node = (tag, className, parent, text) => {
    const e = document.createElement(tag);
    if (className) e.className = className;
    if (text != null) e.textContent = text;
    parent.append(e);
    return e;
  };
  const json = value => JSON.stringify(value, null, 2);
  const turnNumber = seq => "#" + (seq?.replace(/^0+/, "") || "0");
  const duration = secs => typeof secs === "number" ? `${Math.round(secs * 1000)} ms` : "—";

  function invalidate() {
    state.version++;
    state.controller?.abort();
    state.busy = false;
    state.error = "";
  }

  function open(player) {
    invalidate();
    state.returnFocus = document.activeElement;
    state.player = player;
    state.mode = "live";
    state.latest = player.status?.seq || null;
    state.status = player.status;
    state.displayed = state.selected = state.data = null;
    state.cache.clear();
    state.samples = [];
    root.hidden = false;
    document.querySelector(".wrap").inert = true;
    $("jvTitle").textContent = player.display || player.slug;
    $("jvModel").textContent = `${player.model} · ${player.provider}`;
    $("jvHistory").hidden = true;
    $("jvHistoryButton").setAttribute("aria-expanded", "false");
    $("jvBack").focus();
    renderFrame();
    updateMetrics();
    loadLatest();
    clearInterval(state.timer);
    // Completed turns are fetched once. Only unfinished turns are re-read so a
    // request discovered before response.json arrives never stays stuck as pending.
    state.timer = setInterval(loadLatest, 200);
  }

  function close() {
    invalidate();
    clearInterval(state.timer);
    state.player = null;
    root.hidden = true;
    document.querySelector(".wrap").inert = false;
    state.returnFocus?.focus();
  }

  function update(data) {
    state.finished = !!data.finished;
    if (!state.player) return;
    const player = data.players?.find(p => p.slug === state.player.slug);
    if (!player) return;
    state.status = player.status;
    state.latest = player.status?.seq || state.latest;
    const now = Date.now();
    if (player.status) state.samples.push({ at: now, calls: player.status.turn || 0 });
    state.samples = state.samples.filter(s => now - s.at < 30000);
    updateMetrics();
    loadLatest();
  }

  function updateMetrics() {
    const st = state.status;
    $("jvLatency").textContent = duration(st?.lastTurnSeconds);
    $("jvAverage").textContent = duration(st?.avgTurnSeconds);
    const first = state.samples[0], last = state.samples.at(-1);
    const elapsed = first && last ? (last.at - first.at) / 1000 : 0;
    $("jvRate").textContent = elapsed > 1 ? ((last.calls - first.calls) / elapsed).toFixed(2) : "—";
    $("jvModeText").textContent = state.mode === "live" ? state.finished ? "FINISHED" : "LIVE" : state.mode === "history" ? "HISTORY" : "PAUSED";
    $("jvMode").dataset.live = String(state.mode === "live" && !state.finished && state.connected);
    $("jvFollow").textContent = state.mode === "live" ? "Pause" : "Back to live";
    const notice = [!state.connected ? "Live connection interrupted. Reconnecting…" : "", state.error,
      st?.lastError ? "Player error: " + st.lastError : "", state.data?.decisions?.discarded || ""].filter(Boolean).join(" · ");
    $("jvNotice").textContent = notice;
    $("jvNotice").hidden = !notice;
  }

  async function loadLatest() {
    if (!state.player || state.busy) return;
    const seq = state.mode === "live" ? state.latest : state.selected;
    if (!seq) return;
    if (state.displayed === seq && state.data && (state.data.decisions || state.mode !== "live" || state.status?.lastError)) return;
    if (state.cache.has(seq)) { showTurn(seq, state.cache.get(seq)); return; }
    const version = state.version, slug = state.player.slug;
    const controller = new AbortController();
    state.controller = controller;
    state.busy = true;
    const deadline = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetch(`api/agents/${encodeURIComponent(slug)}/turns/${encodeURIComponent(seq)}`, { cache: "no-store", signal: controller.signal });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();
      if (version !== state.version) return;
      if (state.mode === "live" && seq !== state.latest) return;
      state.error = "";
      if (data.decisions) {
        state.cache.set(seq, data);
        if (state.cache.size > 12) state.cache.delete(state.cache.keys().next().value);
      }
      // Compare the few completion phases rather than rebuilding a large JSON
      // view every 200ms while the same native request is still running.
      if (state.displayed !== seq || !state.data || !!data.request !== !!state.data.request
        || !!data.response !== !!state.data.response || !!data.decisions !== !!state.data.decisions)
        showTurn(seq, data);
    } catch {
      if (version === state.version) state.error = "Unable to load this turn. Retrying…";
    } finally {
      clearTimeout(deadline);
      if (version === state.version) { state.busy = false; updateMetrics(); }
    }
  }

  function showTurn(seq, data) {
    const arrived = !!data.response && (seq !== state.displayed || !state.data?.response);
    state.displayed = seq;
    state.data = data;
    renderFrame();
    if (arrived && state.mode === "live") {
      $("jvFrame").classList.remove("arrived");
      void $("jvFrame").offsetWidth;
      $("jvFrame").classList.add("arrived");
    }
  }

  function renderFrame() {
    const data = state.data, game = data?.request?.state?.game;
    $("jvTurn").textContent = state.displayed ? "TURN " + turnNumber(state.displayed) : "Waiting for a turn";
    $("jvGameTime").textContent = game ? `GAME ${Math.floor(game.second / 60)}:${String(game.second % 60).padStart(2, "0")} · TICK ${game.tick}` : "";
    $("jvQuestions").textContent = data?.request ? `${Object.keys(data.request.questions || {}).length} QUESTIONS` : "";
    const usage = data?.response?.usage;
    $("jvTokens").textContent = usage ? `${usage.input_tokens.toLocaleString()} IN · ${usage.output_tokens.toLocaleString()} OUT` : "";
    renderInput();
    renderOutput();
    updateMetrics();
  }

  function renderInput() {
    const panel = $("jvInput"), top = panel.scrollTop;
    const request = state.data?.request;
    const value = state.input === "state" ? request?.state : state.input === "questions" ? request?.questions : request;
    // Keep the scroll container and its position while a new observation arrives.
    panel.replaceChildren();
    node("pre", "", panel, value ? json(value) : "Waiting for recorded input…");
    panel.scrollTop = top;
  }

  function optionLabel(value, key) {
    if (typeof value === "string") return value;
    if (!value || typeof value !== "object") return key;
    if (value.item) return `${value.item}${value.count ? " × " + value.count : ""}${value.cost != null ? " · $" + value.cost : ""}`;
    if (value.name) return value.name;
    if (value.directive || value.task) return value.directive || value.task;
    if (value.cell) return `Cell [${value.cell.join(", ")}]`;
    return json(value);
  }

  function renderOutput() {
    const panel = $("jvOutput"), top = panel.scrollTop;
    const opened = new Set([...panel.querySelectorAll("details[open]")].map(e => e.dataset.question));
    panel.replaceChildren();
    const data = state.data;
    if (state.output === "response") node("pre", "", panel, data?.response ? json(data.response) : "Waiting for recorded output…");
    else if (state.output === "orders") node("pre", "", panel, json({
      ordersSubmittedThisTurn: data?.orders ?? null,
      validation: data?.decisions ?? null,
      engineFeedbackFromEarlierOrders: data?.results ?? null
    }));
    else if (!data?.request) node("div", "jv-empty", panel, "Waiting for Jev…");
    else {
      if (!data.response) node("div", "jv-empty", panel, state.mode === "live" && state.displayed === state.latest && !state.status?.lastError
        ? "Evaluating this input…" : "No response recorded for this turn.");
      for (const [id, question] of Object.entries(data.request.questions || {})) {
        const answer = data.response?.answers?.[id];
        const decision = data.decisions?.decisions?.find(d => d.question === id);
        const card = node("article", "jv-decision", panel);
        const head = node("div", "jv-decision-head", card);
        node("span", "jv-question-id mono", head, id);
        if (decision) node("span", "jv-outcome" + (decision.outcome === "submitted" ? " submitted" : ""), head, decision.outcome);
        const selected = answer?.choice != null ? optionLabel(question.criteria?.[answer.choice], answer.choice)
          : answer?.score != null ? answer.score.toFixed(2) + " / " + ((question.criteria?.length || 1) - 1)
          : answer?.noul != null ? (answer.noul * 100).toFixed(0) + "% yes" : "Pending";
        node("div", "jv-answer", card, selected);
        for (const [key, probability] of Object.entries(answer?.probabilities || {}).sort((a, b) => b[1] - a[1]).slice(0, 3)) {
          const row = node("div", "jv-probability", card);
          row.style.setProperty("--probability", `${Math.max(0, Math.min(1, probability)) * 100}%`);
          const label = optionLabel(question.criteria?.[key], key);
          node("span", "", row, label).title = label;
          node("span", "mono", row, `${(probability * 100).toFixed(0)}%`);
        }
        const detail = node("details", "", card);
        detail.dataset.question = id;
        detail.open = opened.has(id);
        node("summary", "", detail, "Question, all candidates, and answer");
        node("pre", "", detail, json({ question, answer: answer ?? null, execution: decision ?? null }));
      }
    }
    panel.scrollTop = top;
  }

  async function showHistory() {
    const version = state.version, slug = state.player.slug;
    $("jvHistory").hidden = false;
    $("jvHistoryButton").setAttribute("aria-expanded", "true");
    $("jvHistoryClose").focus();
    const list = $("jvHistoryList");
    list.textContent = "Loading turns…";
    try {
      const response = await fetch(`api/agents/${encodeURIComponent(slug)}/turns`, { cache: "no-store" });
      if (!response.ok) throw new Error("History unavailable");
      const turns = await response.json();
      if (version !== state.version || $("jvHistory").hidden) return;
      list.replaceChildren();
      if (!turns.length) node("div", "jv-empty", list, "No turns recorded yet.");
      for (const turn of turns) {
        const button = node("button", "", list);
        button.type = "button";
        button.dataset.seq = turn.seq;
        button.setAttribute("aria-current", String(turn.seq === state.displayed));
        node("span", "mono", button, "Turn " + turnNumber(turn.seq));
        node("time", "", button, new Date(turn.atUtc).toLocaleTimeString());
        button.addEventListener("click", () => {
          invalidate();
          state.mode = "history";
          state.selected = turn.seq;
          state.displayed = state.data = null;
          hideHistory();
          renderFrame();
          loadLatest();
        });
      }
    } catch {
      if (version === state.version) list.textContent = "Unable to load history. Close and reopen to retry.";
    }
  }

  function hideHistory() {
    $("jvHistory").hidden = true;
    $("jvHistoryButton").setAttribute("aria-expanded", "false");
    $("jvHistoryButton").focus();
  }

  $("jvBack").addEventListener("click", close);
  $("jvFollow").addEventListener("click", () => {
    invalidate();
    if (state.mode === "live") { state.mode = "paused"; state.selected = state.displayed; }
    else { state.mode = "live"; hideHistory(); }
    updateMetrics();
    loadLatest();
  });
  $("jvHistoryButton").addEventListener("click", () => $("jvHistory").hidden ? showHistory() : hideHistory());
  $("jvHistoryClose").addEventListener("click", hideHistory);
  for (const side of ["input", "output"]) {
    for (const button of root.querySelectorAll(`[data-${side}]`)) button.addEventListener("click", () => {
      state[side] = button.dataset[side];
      for (const tab of root.querySelectorAll(`[data-${side}]`)) tab.setAttribute("aria-pressed", String(tab === button));
      $(side === "input" ? "jvInput" : "jvOutput").scrollTop = 0;
      if (side === "input") renderInput(); else renderOutput();
    });
  }
  root.addEventListener("keydown", e => {
    if (e.key === "Escape") { e.stopPropagation(); $("jvHistory").hidden ? close() : hideHistory(); }
    if (e.key === "Tab") {
      const focusable = [...root.querySelectorAll("button, summary, [tabindex='0']")].filter(e => e.getClientRects().length);
      const first = focusable[0], last = focusable.at(-1);
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  });
  return { open, close, update, connection: connected => { state.connected = connected; if (state.player) updateMetrics(); } };
})();
