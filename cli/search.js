#!/usr/bin/env node

"use strict";

function printUsage() {
  console.log(`Usage: node search.js "query" [options]

Options:
  --mode <ws|keyword>  Search mode (default: ws)
  --url <url>          API base URL (default: ${process.env.SEARCH_API_URL || "http://localhost:8080"})
  --top <number>       Maximum results to display (default: 10)
  --json               Print raw JSON output
  --help               Show this help`);
}

function parseArguments(argv) {
  if (argv.includes("--help") || argv.includes("-h")) {
    printUsage();
    process.exitCode = 1;
    return null;
  }

  const options = {
    query: "",
    mode: "ws",
    url: process.env.SEARCH_API_URL || "http://localhost:8080",
    top: 10,
    json: false,
  };

  const positional = [];

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];

    if (arg === "--json") {
      options.json = true;
    } else if (arg === "--mode" || arg === "--url" || arg === "--top") {
      if (i + 1 >= argv.length) {
        console.error(`Error: ${arg} requires a value.`);
        printUsage();
        process.exitCode = 1;
        return null;
      }

      const value = argv[++i];

      if (arg === "--mode") {
        options.mode = value.toLowerCase();
      } else if (arg === "--url") {
        options.url = value;
      } else {
        const top = Number(value);
        if (!Number.isInteger(top) || top <= 0) {
          console.error("Error: --top must be a positive integer.");
          printUsage();
          process.exitCode = 1;
          return null;
        }
        options.top = top;
      }
    } else if (arg.startsWith("--")) {
      console.error(`Error: unknown option ${arg}`);
      printUsage();
      process.exitCode = 1;
      return null;
    } else {
      positional.push(arg);
    }
  }

  if (positional.length > 0) {
    options.query = positional.join(" ").trim();
  }

  if (!options.query) {
    console.error("Error: missing or empty query.");
    printUsage();
    process.exitCode = 1;
    return null;
  }

  if (options.mode !== "ws" && options.mode !== "keyword") {
    console.error("Error: --mode must be either 'ws' or 'keyword'.");
    printUsage();
    process.exitCode = 1;
    return null;
  }

  options.url = options.url.replace(/\/+$/, "");
  return options;
}

function cleanSnippet(snippet) {
  return String(snippet || "")
    .replace(/\s+/g, " ")
    .trim();
}

function printResults(results, top, timeMs) {
  if (timeMs !== undefined) {
    console.log(`time_ms: ${timeMs}`);
  }

  const limitedResults = Array.isArray(results)
    ? results.slice(0, top)
    : [];

  if (limitedResults.length === 0) {
    console.log("No results.");
    return;
  }

  limitedResults.forEach((result, index) => {
    const score = Number(result.score);
    console.log(
      `${index + 1}. ${result.doc_id} | score=${Number.isFinite(score) ? score : 0} | ${cleanSnippet(result.snippet)}`,
    );
  });
}

async function runKeywordSearch(options) {
  try {
    const response = await fetch(`${options.url}/api/search`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({
        query: options.query,
        mode: "keyword",
      }),
    });

    const text = await response.text();

    if (options.json) {
      console.log(text);
    }

    if (!response.ok) {
      if (!options.json) {
        console.error(`Error: backend returned HTTP ${response.status}.`);
      }
      return 1;
    }

    if (!options.json) {
      let payload;
      try {
        payload = JSON.parse(text);
      } catch {
        console.error("Error: backend returned invalid JSON.");
        return 1;
      }

      printResults(payload.results, options.top, payload.time_ms);
    }

    return 0;
  } catch (error) {
    console.error(
      `Error: unable to reach backend at ${options.url}: ${error.message}`,
    );
    return 1;
  }
}

async function runWsSearch(options) {
  if (typeof WebSocket !== "function") {
    console.error(
      "Error: WebSocket is not available in this Node.js runtime. Use Node.js 22 or newer.",
    );
    return 1;
  }

  let wsUrl;
  try {
    wsUrl = new URL(options.url);
    wsUrl.protocol = wsUrl.protocol === "https:" ? "wss:" : "ws:";
  } catch {
    console.error(`Error: invalid backend URL: ${options.url}`);
    return 1;
  }

  wsUrl.pathname = `${wsUrl.pathname.replace(/\/+$/, "")}/ws/search`;
  wsUrl.search = "";
  wsUrl.hash = "";

  return new Promise((resolve) => {
    let socket;
    let networkErrorReported = false;
    let settled = false;

    try {
      socket = new WebSocket(wsUrl.toString());
    } catch (error) {
      console.error(
        `Error: unable to connect to backend at ${wsUrl.toString()}: ${error.message}`,
      );
      resolve(1);
      return;
    }

    socket.addEventListener("open", () => {
      socket.send(JSON.stringify({ query: options.query }));
    });

    socket.addEventListener("message", (event) => {
      try {
        const text =
          typeof event.data === "string"
            ? event.data
            : event.data?.toString();
        const message = JSON.parse(text);

        if (options.json) {
          console.log(JSON.stringify(message));
          return;
        }

        if (message.stage === "query_expansion") {
          const expanded = Array.isArray(message.data?.expanded_terms)
            ? message.data.expanded_terms.join(", ")
            : "";
          console.log(`Query expansion: ${message.data?.original || options.query}`);
          console.log(`Expanded terms: ${expanded}`);
        } else if (message.stage === "keyword_search") {
          console.log("Keyword search:");
          printResults(
            message.data?.results,
            options.top,
            message.data?.time_ms,
          );
        } else if (message.stage === "done") {
          console.log("Search completed.");
          if (socket.readyState === WebSocket.OPEN) {
            socket.close(1000, "Search completed");
          }
        }
      } catch (error) {
        console.error(`Error: invalid WebSocket JSON: ${error.message}`);
      }
    });

    socket.addEventListener("error", (event) => {
      networkErrorReported = true;
      const detail =
        event && typeof event.message === "string" ? `: ${event.message}` : "";
      console.error(
        `Error: unable to reach backend WebSocket at ${wsUrl.toString()}${detail}`,
      );
    });

    socket.addEventListener("close", (event) => {
      if (settled) {
        return;
      }
      settled = true;

      if (networkErrorReported) {
        resolve(1);
      } else if (event.code !== 1000) {
        console.error(
          `Error: WebSocket closed unexpectedly (code ${event.code})`,
        );
        resolve(1);
      } else {
        resolve(0);
      }
    });
  });
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  if (!options) {
    return process.exitCode || 1;
  }

  if (options.mode === "keyword") {
    return runKeywordSearch(options);
  }

  return runWsSearch(options);
}

main()
  .then((exitCode) => {
    process.exitCode = exitCode;
  })
  .catch((error) => {
    console.error(`Error: ${error.message}`);
    process.exitCode = 1;
  });
