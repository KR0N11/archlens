# Evaluation files

| File | What it is |
|---|---|
| `dotnet_eShop.json`, `dotnet-architecture_eShopOnWeb.json`, `jasontaylordev_CleanArchitecture.json` | Analyzer output for each repo at the commit named inside the file |
| `eshop-edge-sample.md` | Development sample (seed 1, 20 arrows). Earlier seed-1 samples found bugs that were fixed, so this sheet is not a fair measure |
| `eshop-edge-check.md` | Held-out sample (seed 42, 30 arrows), generated after the last fix and not looked at. **This is the one to grade** |

## How to grade the held-out sheet

For each row, open the evidence link and mark it correct only if that line really makes the call, data access
or HTTP request shown. An arrow with confidence 0.5 ("maybe", one of several implementations of an interface)
counts as correct when the target really is one of the implementations.

Precision = correct / 30. Write the number and the date here:

- Held-out precision (seed 42): _not graded yet_

## What happened in development (for honesty)

- Seed 1, first run: 20/20 evidence lines made the call shown. Three were interface calls with two possible
  targets (a mock and the real service), drawn at confidence 0.5.
- After the bug fixes found by running on the three repos (`Directory.Build.props`, framework interfaces,
  MediatR, conventional routes), a second seed-1 run found 1 wrong arrow in 20. `HooksRepository` was scored as
  request data because it is injected into a route handler. Fixed: a DI-registered type never counts as crossing
  the wire.
- Recall (arrows that should exist but are missing) has not been measured. The known causes are listed under
  "Known limitations" in the main README.
