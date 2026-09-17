# Pure Jev experiment

`jev-duel.yaml` runs two `jev-latest` controllers in a GDI mirror on Scorched Earth,
at spawns 1 and 8. All model decisions use TypeSafe's native System One endpoint;
there is no LLM driver, advisor, fallback, scripted opening, or automatic MCV/building
management. The existing `llm` engine bot type is simply the external order bridge.

## Run

```bash
source ~/.orf-secrets  # exports TYPESAFE_API_KEY; never commit this file
make
dotnet build orf/Orf.csproj -c Release
dotnet orf/bin/Release/net10.0/orf.dll run --spec orf/specs/jev-duel.yaml
```

The dashboard normally binds port 5199 (the runner probes upward if occupied).
Click a Jev player for a full-screen input/output view that follows new turns
automatically. It shows native API latency and call cadence, with tabs for the
complete state, questions, raw request/response, and submitted orders. Use Pause
to hold the current turn, or Turn history to inspect an earlier one. Back to live
resumes following; the Map button returns to the match without resetting map zoom.
Question details include all candidates, probabilities, and execution decisions;
the Orders tab distinguishes current submissions from earlier engine feedback.
The ordinary video/audio stream, observer renderer, hub, replay archive, and stop
command remain available. Use SSH forwarding or authenticated access for remote viewing.

```bash
dotnet orf/bin/Release/net10.0/orf.dll stop <runId>
```

`jev-smoke.yaml` pairs Jev with the existing scripted test player. For one API
evaluation against a saved observation, without launching a game:

```bash
dotnet orf/bin/Release/net10.0/orf.dll agent-turn \
  --spec orf/specs/jev-smoke.yaml --slug jev \
  --state runs/<runId>/state/jev.json --outdir runs/jev-offline
```

## Decision architecture

Each fresh player observation produces a single request containing independent
questions. Jev selects a short-term focus, a persistent capital objective, purchases
per available queue, placements, squad commands, and harvester/MCV tasks. A capital
objective is either one additional structure or unlocking an unavailable unit.
The goal is reconciled against observed buildings and buildability. Its prerequisite
chain marks useful construction candidates; Jev still selects each purchase.
Jev also decides whether to reserve money toward the next available goal step.

Objectives selected in one request become context on the next observation. Questions
within a batch never depend on each other's answers. Queue urgency scores share the
same 0–4 rubric; the scheduler considers higher-urgency purchases first, subtracting
actual costs and reservations. Ties use question ID order. Non-spending actions run
first. One actor or queue can receive at most one selected command per pass.

Only player state is model input. `game.json` and the unrestricted `actors.json`
observer stream are not inputs. Jev slots receive `ruleCatalog`: actual mod metadata
for palette items and owned/visible unit types, including costs, power, descriptions,
current prerequisites, weapon ranges, and worker/deployment capabilities. Base
weapon statistics are information, not a combat simulator or a prediction of victory.

No prose is generated as an explanation. Persistent memory records focus, the next
capital objective, stable squad memberships, submitted assignments, and factual
engine feedback. Choice confidence is distribution concentration, not fight-win
probability. We do not impose an arbitrary confidence cutoff that makes unsure
players stop playing.

## Explicit scaffolding and limitations

- Combat units are grouped by stable actor ID, with 12 members per squad and eight
  squads by default. Overflow joins the smallest squad so units are not silently
  omitted. Jev can send a single member scouting or give the squad a complete
  attack/advance/guard/withdraw directive. Group membership is code, not a learned decision.
- Candidate destinations include visible enemies (nearest 24), remembered buildings
  and enemy starts (up to 24 distinct cells), home, and up to 12 own buildings.
  Placement candidates uniformly cover the exported legal grid (up to 128), with
  currently visible footprints so dynamic blockers cannot reveal units through fog.
  The current engine grid covers a radius of 12 around the base centroid, which
  limits distant expansion placement. MCV destinations are simple offsets from
  explored resource centroids; there is no terrain-aware route planner yet.
- Production offers all affordable items in the queue in quantities 1 or 3 for
  units and 1 for structures. Structures are placed before another is queued;
  units can have one waiting batch. There is no fixed tech or army composition.
- Tactical assignments have a five-second minimum hold; unchanged active orders
  are retained. This limits oscillation but also limits emergency reaction speed.
- Visible capturable targets can receive a single capable squad member; conditional
  capture eligibility is still decided by the engine.
- Transport loading, superweapon activation, repair/sell/cancel, fine aiming, and stances
  are not part of this first candidate surface. Low-level pathfinding and automatic
  targeting are still ordinary OpenRA behavior.
- Goal options come from current queues' buildable and exported locked entries;
  goals behind production queues that do not yet exist are not directly selectable.
- The comparison is between complete controllers, not an isolated model benchmark.
  Candidate limits, grouping, reservations, and prompts influence the result.

## Timing and reliability

The baseline exports every 25 world ticks (one game second) and issues at most one
request per player at a time, with a one-second start-to-start minimum interval.
The engine scans inboxes every ten ticks. A native request has a five-second wall
deadline; responses more than 75 game ticks behind the latest observation are
discarded. Orders are revalidated against the newest available state before writing.

HTTP failures use bounded exponential backoff, respecting Retry-After, then evaluate
a fresh observation. Authentication and request-validation failures stop the player
loop and appear in status/errors. There is no retry of an old action response and no
fallback to an LLM. Accepted engine acknowledgements reserve spending for two more
observation intervals because acknowledgements precede actual execution. Engine
rejection and observed completion remain distinct.

Optional per-player `jev:` fields are defined in `JevSpec` in `Spec.cs`. Keep the
one-second observation baseline for initial comparisons. Raising API frequency alone
does not improve the age of the exported observation. `stateIntervalTicks` is a match
setting; faster exports require profiling before using many simultaneous players.

## Artifacts and validation

Each run saves its `spec.yaml`. Each turn records `state.json`, native `request.json`/`response.json`, prior
`results.json`, `orders.json`, and `decisions.json`. Player `memory.json` and
`status.json` record current commitments and telemetry. Input/output usage is taken
from TypeSafe's response. Dashboard cost is an **estimate**, using a configurable
input price of $0.042/million tokens and zero output cost as published at integration
time. Errors and retries may incur unreported usage. High-frequency artifacts grow;
this first experiment retains full traces for diagnosis.

```bash
dotnet test orf.Tests/Orf.Tests.csproj -c Release
make check
./utility.sh cnc --check-yaml
```

Tests cover candidate legality, shared budgets, stale responses, ownership/visibility
changes, command persistence, pending spending, protocol failures, and native HTTP
behavior. Validate live with `jev-smoke.yaml` before the duel. For comparisons, swap
spawns and retain the same model route, configuration, and observation cadence.
`jev-latest` is a moving alias; the account currently advertises that alias and
`jev-preview`, so an immutable model version is not assumed.

Primary references:
- https://docs.typesafe.ai/api
- https://docs.typesafe.ai/primitives
- https://docs.typesafe.ai/primitives/choice
- https://docs.typesafe.ai/confidence
- https://typesafe.ai/blog/introducing-system-one-models-and-jev
