# Parallel Search Engine

## Overview & Stack

This is a C#/.NET 8 parallel full-text search engine. `CustomThreadPool` executes indexing work with fixed `Thread` workers, and `ThreadSafeInvertedIndex` stores postings in custom arrays and 131072 bucket-linked structures rather than `ConcurrentDictionary` or the system thread pool. Ollama `llama3.2:1b` provides optional query expansion, while REST, WebSocket, a static Web UI, and a Node.js CLI expose search operations.

- C# / .NET 8 / ASP.NET Core Minimal APIs
- `Thread`, `lock`, `Monitor`, `SemaphoreSlim`, `T[]` arrays
- Ollama `llama3.2:1b`
- Vanilla HTML, CSS, and JavaScript
- Node.js built-in `fetch` and `WebSocket`; no npm dependencies
- Python 3 standard library; no pip dependencies
- Docker Engine and Docker Compose v2

## Dataset

The dataset is plain text: `data/D1.7GB/` contains 2808 numeric `.txt` files, such as `data/D1.7GB/10.txt` and `data/D1.7GB/1001.txt`. The sample file starts with Project Gutenberg metadata and a document title, with the remaining content stored as UTF-8 text.

Docker Compose mounts `./data` read-only at `/app/data`. On startup, `IndexService` recursively searches `/app/data` for `*.txt`, reads each file, uses its relative path as `DocId`, and stores the first 200 characters as a snippet.

The indexer:

1. Reads all matching files with `CustomThreadPool`.
2. Normalizes alphanumeric tokens to lowercase.
3. Counts term frequencies in a per-document open-addressing table.
4. Adds each unique term and its document frequency to the global bucketed index once.
5. Starts automatically after application startup when the index is empty and `.txt` files exist.

Startup uses 4 worker threads by default. `DEFAULT_INDEX_THREADS` overrides this default. Manual re-indexing accepts 1, 2, 4, or 8 threads.

## Prerequisites

Docker Compose:

- Docker Engine or Docker Desktop
- Docker Compose v2

Local execution:

- .NET 8 SDK
- Ollama
- Node.js for `cli/search.js`
- Python 3 for `tests/`

Node.js 22 or newer is recommended because the CLI uses the built-in global `WebSocket`; keyword-only search uses built-in `fetch`.

## How to Run

### Run with Docker Compose

```bash
docker compose up --build -d
docker compose ps
docker compose logs -f
```

Open `http://localhost:8080/`. Docker publishes the backend on port 8080 and Ollama on port 11434.

The Ollama initializer waits for the service and runs `ollama pull llama3.2:1b` automatically.

Stop the stack:

```bash
docker compose down
```

Stop the stack and remove the persisted Ollama model volume:

```bash
docker compose down -v
```

### Run Locally

Start Ollama and download the model:

```bash
ollama serve
ollama pull llama3.2:1b
```

In a second terminal, run the backend from the repository root:

```bash
export DATA_DIR="$PWD/data"
export OLLAMA_URL="http://localhost:11434"
export OLLAMA_MODEL="llama3.2:1b"
dotnet run --project backend/Backend.csproj
```

PowerShell:

```powershell
$env:DATA_DIR = "$PWD\data"
$env:OLLAMA_URL = "http://localhost:11434"
$env:OLLAMA_MODEL = "llama3.2:1b"
dotnet run --project backend/Backend.csproj
```

Set `ASPNETCORE_URLS=http://localhost:8080` if the local launch process does not already use port 8080.

## How to Use & Verify

### Web UI

1. Open `http://localhost:8080/`.
2. Check `#stats-badge` for indexed document count and thread count.
3. Select 1, 2, 4, or 8 threads and click `Re-index`.
4. Choose `WebSocket Pipeline` for staged Ollama expansion and search, or `Keyword Only` for direct `/api/search` keyword matching.
5. For WebSocket Pipeline, observe `query_expansion`, `keyword_search`, and `done`.

### Node.js CLI

WebSocket pipeline:

```bash
node cli/search.js "space"
```

Keyword-only REST search:

```bash
node cli/search.js "space" --mode keyword
```

Display only five results:

```bash
node cli/search.js "space" --top 5
```

Use a custom server:

```bash
node cli/search.js "space" --url http://localhost:8080
```

Print raw JSON:

```bash
node cli/search.js "space" --mode keyword --json
node cli/search.js "space" --mode ws --json
```

CLI flags:

- `--mode ws|keyword`; default `ws`
- `--url`; default `SEARCH_API_URL` or `http://localhost:8080`
- `--top`; positive integer; default `10`
- `--json`
- `--help`

### REST API

Bash/curl:

```bash
curl http://localhost:8080/api/health
curl http://localhost:8080/api/stats

curl -X POST http://localhost:8080/api/index \
  -H "Content-Type: application/json" \
  -d '{"threads":4}'

curl -X POST http://localhost:8080/api/search \
  -H "Content-Type: application/json" \
  -d '{"query":"space","mode":"keyword"}'
```

Windows PowerShell:

```powershell
curl.exe http://localhost:8080/api/health
curl.exe http://localhost:8080/api/stats

curl.exe -X POST http://localhost:8080/api/index `
  -H "Content-Type: application/json" `
  -d '{\"threads\":4}'

curl.exe -X POST http://localhost:8080/api/search `
  -H "Content-Type: application/json" `
  -d '{\"query\":\"space\",\"mode\":\"keyword\"}'
```

Typical response shapes:

```json
{"status":"ok","ollama":true}
{"documents":2808,"index_build_time_ms":148145,"threads_used":4}
{"status":"ok","documents_indexed":2808,"time_ms":148145}
{"results":[{"doc_id":"D1.7GB/123.txt","score":1.0,"snippet":"First 200 characters..."}],"time_ms":12}
```

An already-running indexing request returns HTTP 409:

```json
{"status":"busy"}
```

### Python Performance Scripts

Indexing scalability:

```bash
python tests/index_performance.py --url http://localhost:8080
```

The script tests 1, 2, 4, and 8 threads sequentially and prints threads, documents, milliseconds, speedup, and efficiency. It writes `tests/index_performance.json`.

Current `tests/index_performance.json` records 2808 documents:

| Threads | Time (ms) | Speedup | Efficiency |
|---:|---:|---:|---:|
| 1 | 364980 | 1.00 | 100.00% |
| 2 | 268237 | 1.36 | 68.03% |
| 4 | 148145 | 2.46 | 61.59% |
| 8 | 111525 | 3.27 | 40.91% |

Concurrent search throughput:

```bash
python tests/search_performance.py --url http://localhost:8080
python tests/search_performance.py --url http://localhost:8080 --requests 200
```

The script defaults to 100 requests per concurrency level and tests 1, 5, 10, and 20 clients. It prints total time, throughput, average latency, and p95 latency and writes `tests/search_performance.json`.

Both scripts use Python 3 standard-library modules only.

## API Reference

| Method | Endpoint | Payload | Response |
|---|---|---|---|
| GET | `/api/health` | None | HTTP 200: `{"status":"ok","ollama":true\|false}` |
| GET | `/api/stats` | None | `{"documents":<int>,"index_build_time_ms":<long>,"threads_used":<int>}` |
| POST | `/api/index` | `{"threads":4}` | `{"status":"ok","documents_indexed":<int>,"time_ms":<long>}`; 409 when indexing is busy |
| POST | `/api/search` | `{"query":"text","mode":"keyword"}` | `{"results":[{"doc_id":"...","score":0.95,"snippet":"..."}],"time_ms":<long>}` |
| WebSocket | `/ws/search` | `{"query":"text"}` | Three text stages: `query_expansion` with original query and expanded terms, `keyword_search` with results and time, and `done` |

## Repository Structure

```text
.
├── backend/
│   ├── Core/
│   │   ├── CustomThreadPool.cs
│   │   ├── IndexingState.cs
│   │   ├── IndexService.cs
│   │   ├── OllamaQueryExpander.cs
│   │   └── ThreadSafeInvertedIndex.cs
│   ├── Backend.csproj
│   ├── Dockerfile
│   └── Program.cs
├── cli/
│   ├── search.js
│   └── search.py
├── data/
│   └── D1.7GB/
├── frontend/
│   ├── app.js
│   ├── index.html
│   └── style.css
├── tests/
│   ├── index_performance.py
│   ├── index_performance.json
│   ├── search_performance.py
│   └── search_performance.json
├── docker-compose.yml
└── README.md
```
