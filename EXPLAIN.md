# ArchLens: study pack

This is what you need to defend the project in an interview. Read it top to bottom once, then
use the "They'll ask" lines as flashcards.

## 30-second pitch

"ArchLens turns a C# repo into an architecture diagram. Roslyn, the C# compiler's own API, finds the facts:
which classes exist, what calls what, which entry points touch which tables, and which HTTP calls leave the
system. Every arrow keeps the file and line that proves it. An LLM step exists, but it can only relabel boxes
the rules were unsure about, never draw arrows. I ran it on Microsoft's eShop: 19 projects, about 1,000 boxes,
50 traced HTTP entry points, in about 3 seconds."

## The pipeline, file by file

| Step | File | One sentence |
|---|---|---|
| Load | `Loading/RepoLoader.cs` | One Roslyn compilation per `.csproj`, built in dependency order, with the .NET and ASP.NET framework assemblies as references. NuGet packages are never restored. |
| Collect | `Analysis/SignalCollector.cs` | One pass over every file that records facts (DbSets, DI registrations, routes, base URLs, migration tables) into `RepoIndex`. |
| Score entities | `Analysis/EntityScorer.cs` | Adds points per signal. 5 or more is data, 2 to 4 is unsure, under 2 is not data. |
| Classify | `Analysis/TypeClassifier.cs` | Picks one of the 10 box types by fixed rules, in a fixed order. |
| Build | `Analysis/GraphBuilder.cs` | Walks every method body, turns calls, data access and HTTP calls into arrows with evidence. |
| Enrich | `Enrichment/Enricher.cs` + `Cli/ClaudeLabelClient.cs` | Asks the LLM about unsure boxes only; the answer can change a box's type and purpose, nothing else. |
| Trace | `Tracing/CallFlowTracer.cs` | Depth-first walk from each entry point; stops at data, outside systems, or depth 8. |
| Score | `Scoring/ArchitectureScorer.cs`, `Scoring/Tarjan.cs` | 100 minus capped penalties from graph checks. |

## Decisions you must be able to defend

**1. No NuGet restore.**
- Say this: "Restoring and building every repo is slow and often fails. I compile the source only, so the
  repo's own calls resolve exactly, and package calls are matched by the receiver's type and the method name."
- Why not the alternative: MSBuildWorkspace needs a full restore and a working build, and many repos won't
  build on a random machine.
- They'll ask: "What do you lose?" Types that come out of package methods. `var x = await db.Items.FirstAsync()`
  has an unknown type, so a later `x.Name = ...` isn't seen as a write. It's in the README's limitations.

**2. Text keys, not symbol objects.**
- Say this: "The same class seen from two projects can be two different symbol objects in Roslyn, so I key
  everything by its display string, like `Shop.OrderService.GetOrders(int)`."
- They'll ask: "Overloads?" The key includes parameter types, and function IDs add them only when a name is
  overloaded.

**3. Interface calls.**
- Say this: "A call through `IOrderService` could land in any implementation. If DI registers one
  (`AddScoped<IOrderService, OrderService>`), I draw only that one. Otherwise I draw all of them at confidence
  0.5, shown dashed as 'maybe'."
- They'll ask: "Why not follow `IDisposable.Dispose()`?" Framework interfaces are implemented everywhere.
  Following them linked unrelated projects in eShop, so only interfaces the repo declares are followed. (There
  is a test.)

**4. MediatR.**
- Say this: "`mediator.Send(command)` is resolved at runtime by the command's type. The pattern rule finds the
  class implementing `IRequestHandler<ThatCommand, ...>`. That's what lets `POST api/orders` in eShop trace
  into the handler."
- They'll ask: "Why search base types too?" Without the MediatR package, Roslyn can't tell `IRequestHandler` is
  an interface. It's the first item in the base list, so Roslyn files it as the base class.

**5. The LLM never draws arrows.**
- Say this: "The enricher gets unsure boxes and returns a label. `Apply` only sets type, layer, purpose and
  confidence, and rejects any type not in the fixed enum. The JSON schema sent to Claude has the 10 types as an
  enum. A test checks the arrows are byte-identical before and after."
- They'll ask: "Did you run it on a real repo?" Say the truth: not yet (no key on the dev machine). The logic is
  tested with a fake client, and the live call is the next thing to do.

**6. Cycles per class, not per project.**
- Say this: "MSBuild refuses circular project references, so a project-level check always passes. Class-level
  cycles are the ones that actually happen."
- Algorithm: Tarjan's strongly connected components, one depth-first pass, O(nodes + edges).

**7. Penalties are capped per check.**
- Say this: "Without caps, 40 dead private methods would zero the score. Each check has a ceiling, so the number
  reflects several kinds of problem, not one loud one."
- They'll ask: "Why these numbers?" They're starting values from the design doc, not tuned. Say that.

**8. Serving arrows point caller -> callee.**
- ArchiMate draws Serving from provider to consumer. I store call direction because call flows read top down.
  It's a deliberate deviation, and it's listed in the README.

**9. The web API: queue + one worker + polling.**
- Say this: "POST returns right away with a job id; one background worker clones and analyzes jobs one at a
  time from an in-process queue, and the page polls the job every 700 ms. Before cloning I ask GitHub for the
  latest commit with `git ls-remote`, so an unchanged repo comes straight from the cache."
- Why not the alternative: Server-Sent Events push progress instead of polling, but polling is one fetch in a
  loop and a job only has four steps. Postgres (in the design doc) makes sense once there's more than one user.
- They'll ask: "Is it safe to clone whatever someone pastes?" Only `github.com/owner/repo` URLs are accepted,
  git runs with prompts off and a 3-minute timeout, and the clone is deleted after analysis. It's still a local
  tool; putting it on the internet would also need rate limits and a repo size cap.
- They'll ask: "Why one worker?" Roslyn holds a whole repo in memory. One at a time keeps memory predictable.

## Numbers and where each comes from

| Claim | Source |
|---|---|
| eShop: 19 projects, 1,064 boxes, 613 arrows, 50 entry points, 2.9 s | CLI output, `eval/dotnet_eShop.json`, commit `b4a4087` |
| 61 C# tests (incl. API), viewer tests | `dotnet test`, `npm test` |
| Precision | **Not claimed yet.** Grade `eval/eshop-edge-check.md` (30 held-out arrows) first |

## Bugs found by running on real repos (good interview stories)

1. eShop turns on `ImplicitUsings` in `Directory.Build.props`, which the loader didn't read, so `using` lines
   were missing and HTTP calls didn't resolve. Fix: read every `Directory.Build.props` up to the repo root.
2. `IDisposable.Dispose()` linked Catalog.API to WebApp. Fix: follow only the repo's own interfaces.
3. MAUI declares the same class once per platform folder, which gave duplicate IDs. Fix: first one wins.
4. `HooksRepository`, injected into a route handler, was scored as request data. Fix: a DI-registered type
   never counts as crossing the wire.
5. CleanArchitecture wraps `MapGet` with its arguments swapped. Fix: find the pattern and the handler by kind,
   not position.

## What you did NOT build (say it before they find it)

TypeScript support, the Infrastructure view, Postgres storage, SSE progress (it polls), PNG/SVG export,
and the CI score. Jobs and the cache are in memory.
