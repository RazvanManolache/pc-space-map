# Local assistant access

PC Space Map exposes a semantic loopback API so an assistant can inspect and navigate the app without desktop automation.

## Discovery and authentication

Read `%LOCALAPPDATA%\PCSpaceMap\agent-session.json` while the app is running. It contains:

- `baseUrl`: a random local address such as `http://127.0.0.1:36971`;
- `token`: a random per-process bearer token;
- `processId`: used to detect a stale session file;
- `scope`: always `127.0.0.1 only`.

Send `Authorization: Bearer <token>` with every `/api` request. For viewing a screenshot directly in a local browser, GET requests also accept `?token=<token>`.

`/health` and the explanatory root page do not expose inventory data and do not require authentication.

## Read endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | Process identity and listener safety properties |
| `GET /api/help` | Machine-readable endpoint summary |
| `GET /api/status` | Scan activity, totals, current tab/view/selection |
| `GET /api/report` | Top-level breakdown, top 100 files, all grouped suggestions |
| `GET /api/tree?path=...&depth=2&limit=100` | Bounded hierarchical inventory navigation |
| `GET /api/largest?under=...&limit=100` | Largest files globally or below a path |
| `GET /api/suggestions` | Cleanup review groups with confidence and rationale |
| `GET /api/issues` | Scan errors and skipped-link count |
| `GET /api/changes` | Saved snapshot comparison, up to 5,000 largest differences |
| `GET /api/children?path=...&offset=0&limit=200` | Paged direct children; returns the total child count |
| `GET /api/screenshot` | PNG rendered directly from the WPF visual tree |

Tree depth is capped at 5 and the per-folder return limit at 500 to avoid accidental huge responses. Every tree node reports omitted child counts and size, allowing deliberate deeper navigation.

## Semantic actions

Start a scan:

```http
POST /api/scan
Content-Type: application/json
Authorization: Bearer <token>

{ "path": "C:\\Users\\example" }
```

Select or zoom to a path and optionally switch tabs:

```http
POST /api/navigate
Content-Type: application/json
Authorization: Bearer <token>

{
  "path": "C:\\Users\\example\\Downloads",
  "tab": "Space map",
  "selectOnly": false
}
```

Valid tabs are `Space map`, `Folder contents`, `Largest files`, `Cleanup review`, `Changes`, and `Scan notes`.

`POST /api/open-saved { "path": "..." }` opens an existing inventory. `POST /api/update` starts a change-journal update (or full scan when journal access is unavailable). `POST /api/update-folder { "path": "..." }` refreshes only the supplied scope inside the current inventory; it can also cover a newly created or deleted folder. These return an `accepted` flag; poll `/api/status` for completion. The report includes the snapshot ID, completion timestamp, scan method, per-phase timings, allocation totals, and saved size changes. Its `work` object reports `storageFormat`, `versionsWritten`, `foldersRecalculated`, `readerCount`, `readerProfileReused`, and optional reader-probe timings. `/api/largest` queries the index directly, so subtree queries are not limited to the globally largest 5,000 files.

Close the app:

```http
POST /api/shutdown
Authorization: Bearer <token>
```

There is intentionally no delete endpoint. If deletion is added later, it should require explicit user confirmation for exact resolved targets and prefer recoverable cleanup mechanisms.
