# ArchLens viewer

React app for ArchLens. You paste a GitHub repo URL, the ArchLens API (`src/ArchLens.Api`) clones and
analyzes it, and the viewer draws the result. The viewer does no analysis itself: the C# analyzer decides what
exists and what calls what, and precomputes each Call Flow trace into the graph's `views`.

```bash
# terminal 1, from the repo root: the API on http://localhost:5080
dotnet run --project src/ArchLens.Api

# terminal 2
cd viewer
npm install
npm run dev      # http://localhost:5173, proxies /api to the API
npm test         # vitest, pure logic in src/lib
npm run build    # static site in dist/
```

## Flow

1. **Landing:** paste `github.com/owner/repo` (https:// and `.git` are fine) and press Enter. The link is
   checked in the browser first, so typos never reach the server.
2. **Progress:** `POST /api/analyses` starts a job. The page polls `GET /api/analyses/{id}` every 700 ms and
   shows Queued → Cloning → Analyzing → Done with the server's message. Cancel only stops polling; the
   server finishes the job and caches the result, so pasting the same repo again returns at once.
3. **Graph:** when the job is done, `GET /api/analyses/{id}/graph` loads the tabs (Overview, Data Entities, Call
   Flow, Score). "New analysis" returns to the landing screen.

`?repo=owner/name` in the address bar reruns that repo on refresh (served from the cache).

If the API isn't running, the page says so and shows the command to start it. The bundled examples
(`public/examples/`) and "Open a saved graph JSON" still work without it, and you can also drop a graph JSON
anywhere on the page.

Stack: React + TypeScript + Vite, `@xyflow/react` for the canvas, `elkjs` (layered algorithm) for layout, plain CSS
with color tokens (light and dark follow the system setting).
