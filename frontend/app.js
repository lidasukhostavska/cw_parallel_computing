"use strict";

function setStepState(id, state) {
  const step = document.getElementById(id);
  if (!step) return;

  step.classList.remove("pending", "active", "completed");
  step.classList.add(state);
}

function resetPipeline() {
  setStepState("step-query_expansion", "pending");
  setStepState("step-keyword_search", "pending");
  setStepState("step-done", "pending");

  document.getElementById("query-expansion-status").textContent =
    "Waiting for a query";
  document.getElementById("original-query").textContent = "";
  document.getElementById("expanded-terms").replaceChildren();
  document.getElementById("keyword-search-status").textContent =
    "Waiting to search";
  document.getElementById("search-time").textContent = "";
  document.getElementById("done-status").textContent =
    "Waiting for completion";
}

function renderResults(results) {
  const container = document.getElementById("results-list");
  container.replaceChildren();

  if (!Array.isArray(results) || results.length === 0) {
    const empty = document.createElement("p");
    empty.className = "empty-state";
    empty.textContent = "No matching documents.";
    container.appendChild(empty);
    return;
  }

  results.forEach((result, index) => {
    const card = document.createElement("article");
    card.className = "result-card";

    const rank = document.createElement("div");
    rank.className = "result-rank";
    rank.textContent = String(index + 1);

    const body = document.createElement("div");

    const docId = document.createElement("p");
    docId.className = "result-doc";
    docId.textContent = result.doc_id;

    const snippet = document.createElement("p");
    snippet.className = "result-snippet";
    snippet.textContent = String(result.snippet || "")
      .replace(/\s+/g, " ")
      .trim();

    body.append(docId, snippet);

    const score = document.createElement("span");
    score.className = "result-score";
    score.textContent = `score ${Number(result.score).toFixed(4)}`;

    card.append(rank, body, score);
    container.appendChild(card);
  });
}

async function loadStats() {
  const badge = document.getElementById("stats-badge");

  try {
    const response = await fetch("/api/stats");
    if (!response.ok) throw new Error("Stats request failed");

    const stats = await response.json();
    if (!stats.documents) {
      badge.textContent = "Index empty";
      badge.className = "stats-badge";
    } else {
      badge.textContent =
        `Indexed: ${stats.documents} docs (${stats.threads_used || 0} threads)`;
      badge.className = "stats-badge completed";
    }
  } catch {
    badge.textContent = "Server unreachable";
    badge.className = "stats-badge";
  }
}

async function runKeywordSearch(query) {
  setStepState("step-keyword_search", "active");
  document.getElementById("keyword-search-status").textContent =
    "Searching...";

  try {
    const response = await fetch("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query, mode: "keyword" }),
    });

    if (!response.ok) throw new Error("Search request failed");

    const payload = await response.json();
    document.getElementById("search-time").textContent =
      `${payload.time_ms} ms`;
    document.getElementById("keyword-search-status").textContent =
      "Search complete";
    renderResults(payload.results);

    setStepState("step-keyword_search", "completed");
    setStepState("step-done", "completed");
    document.getElementById("done-status").textContent =
      "Completed";
  } catch (error) {
    document.getElementById("keyword-search-status").textContent =
      "Search failed";
    document.getElementById("done-status").textContent =
      error.message;
    setStepState("step-keyword_search", "pending");
    setStepState("step-done", "pending");
  }
}

function runWebSocketSearch(query) {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  const socket = new WebSocket(
    `${protocol}//${location.host}/ws/search`,
  );

  setStepState("step-query_expansion", "active");
  document.getElementById("query-expansion-status").textContent =
    "Expanding query...";

  socket.addEventListener("open", () => {
    socket.send(JSON.stringify({ query }));
  });

  socket.addEventListener("message", (event) => {
    let message;
    try {
      message = JSON.parse(event.data);
    } catch {
      return;
    }

    if (message.stage === "query_expansion") {
      const data = message.data || {};
      document.getElementById("original-query").textContent =
        data.original || query;
      document.getElementById("query-expansion-status").textContent =
        "Expansion complete";

      const termsContainer = document.getElementById("expanded-terms");
      termsContainer.replaceChildren();

      (data.expanded_terms || []).forEach((term) => {
        const badge = document.createElement("span");
        badge.className = "keyword-badge";
        badge.textContent = term;
        termsContainer.appendChild(badge);
      });

      setStepState("step-query_expansion", "completed");
      setStepState("step-keyword_search", "active");
      document.getElementById("keyword-search-status").textContent =
        "Searching...";
    } else if (message.stage === "keyword_search") {
      const data = message.data || {};
      document.getElementById("search-time").textContent =
        `${data.time_ms} ms`;
      document.getElementById("keyword-search-status").textContent =
        "Search complete";
      renderResults(data.results);

      setStepState("step-keyword_search", "completed");
      setStepState("step-done", "active");
      document.getElementById("done-status").textContent =
        "Finalizing...";
    } else if (message.stage === "done") {
      setStepState("step-done", "completed");
      document.getElementById("done-status").textContent =
        "Completed";
      socket.close(1000, "Search completed");
    }
  });

  socket.addEventListener("error", () => {
    document.getElementById("done-status").textContent =
      "Server unreachable";
  });
}

document.addEventListener("DOMContentLoaded", () => {
  loadStats();

  const reindexButton = document.getElementById("reindex-btn");
  const threadsSelect = document.getElementById("threads-select");

  reindexButton.addEventListener("click", async () => {
    const threads = Number(threadsSelect.value);
    const statsBadge = document.getElementById("stats-badge");

    reindexButton.disabled = true;
    threadsSelect.disabled = true;
    reindexButton.textContent = "Indexing...";
    statsBadge.textContent =
      `Indexing with ${threads} thread(s)...`;
    statsBadge.className = "stats-badge";

    try {
      const response = await fetch("/api/index", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ threads }),
      });

      const data = await response.json().catch(() => null);
      if (!response.ok) {
        const detail =
          data && data.status ? ` (${data.status})` : "";
        throw new Error(`Re-index request failed${detail}`);
      }

      statsBadge.textContent =
        `Indexed: ${data.documents_indexed} docs in ${data.time_ms} ms (${threads} threads)`;
      statsBadge.className = "stats-badge completed";
    } catch (error) {
      statsBadge.textContent =
        `Re-index failed: ${error.message}`;
      statsBadge.className = "stats-badge";
    } finally {
      reindexButton.disabled = false;
      threadsSelect.disabled = false;
      reindexButton.textContent = "Re-index";
    }
  });

  document
    .getElementById("search-form")
    .addEventListener("submit", (event) => {
      event.preventDefault();

      const query =
        document.getElementById("search-input").value.trim();
      if (!query) return;

      const mode = document.getElementById("search-mode").value;
      document.getElementById("results-list").replaceChildren();

      const empty = document.createElement("p");
      empty.className = "empty-state";
      empty.textContent = "Searching...";
      document.getElementById("results-list").appendChild(empty);

      resetPipeline();

      if (mode === "keyword") {
        setStepState("step-query_expansion", "pending");
        document.getElementById("query-expansion-status").textContent =
          "Skipped in keyword mode";
        runKeywordSearch(query);
      } else {
        runWebSocketSearch(query);
      }
    });
});
