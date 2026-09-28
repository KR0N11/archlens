# ArchLens viewer

Static React app that draws an ArchLens graph JSON. It does no analysis: the C# analyzer decides
what exists and what calls what, and precomputes each Call Flow trace into `views`.

```bash
npm install
npm run dev      # http://localhost:5173
npm test         # vitest, pure logic in src/lib
npm run build    # static site in dist/
```

Examples are listed in `public/examples/index.json` (`[{ "name", "file" }]`); the first one opens on start.
You can also drop any graph JSON onto the page.

Stack: React + TypeScript + Vite, `@xyflow/react` for the canvas, `elkjs` (layered algorithm) for layout.
