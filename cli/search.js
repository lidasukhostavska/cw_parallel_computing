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

async function runKeywordSearch(query, url, top, jsonOutput) {
  console.log("Keyword search is not implemented yet.");
  return 1;
}

async function runWsSearch(query, url, top, jsonOutput) {
  console.log("WebSocket search is not implemented yet.");
  return 1;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  if (!options) {
    return process.exitCode || 1;
  }

  if (options.mode === "keyword") {
    return runKeywordSearch(
      options.query,
      options.url,
      options.top,
      options.json,
    );
  }

  return runWsSearch(
    options.query,
    options.url,
    options.top,
    options.json,
  );
}

main()
  .then((exitCode) => {
    process.exitCode = exitCode;
  })
  .catch((error) => {
    console.error(`Error: ${error.message}`);
    process.exitCode = 1;
  });
