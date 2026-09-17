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

These describe the original `policyVersion: 1` controller, which remains the
default so existing experiment specs retain their behavior.

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

## V2: spatial economy and combat questions

Set `jev: { policyVersion: 2 }` on a player to select the next harness. The native
model and order validation are shared with v1; `JevPolicyV2` wraps the unchanged
`JevPolicy` and replaces its combat questions. No LLM is involved.

- **Refineries:** a dedicated placement question compares the actual docking
  cell's walking distance to visible Tiberium patches, patch size and density,
  dock access, nearby existing refineries, and visible enemies. The engine derives
  the dock and free harvester from the mod rules. Candidate generation keeps the
  best routes to each patch before filling the remaining spatial coverage budget.
  Four-neighbor routes use explored terrain and visible building footprints,
  including the proposed refinery. They ignore moving traffic, terrain speeds,
  and diagonal shortcuts; they are estimates, not exact travel times. Patches
  contain currently visible resources only. The existing placement radius still
  limits expansion.
- **Economy:** explicit counts, power surplus, and free-harvester rules support
  investment and production questions. These ask about sustained military income,
  useful power, and credible capture tasks for engineers. Jev chooses whether and
  when to expand; there is no prescribed opening or refinery count.
- **Groups:** nearby recruits join assembling/defending groups of the same role.
  Deployed groups are sealed to new recruits while capacity permits. Frontline,
  artillery, air, capture, and support units receive separate role context. The
  eight-group bound still applies; overflow joins the nearest group, preferring
  the same role. Thus separation is not an unconditional guarantee at capacity.
- **Combat:** a persistent common operation gives each group a destination. Each
  group separately chooses a maneuver (assemble, reinforce, advance, engage,
  defend, withdraw, continue) and, when available, a local target. A target answer
  is executed only if the same batch selects engage. Both questions use the same
  observation; neither assumes the other's answer. Local health, weapon ranges,
  group spread, and nearby health-adjusted unit costs provide context. Unit cost
  is a rough force indicator, not a matchup simulation. There is no mandatory
  army size before attacking.
- **Campaign commitment:** Jev explicitly chooses whether to launch the preparing
  combat groups together, detach one scout, or leave decisions to the local
  groups. A launch claims those actors before local orders in that turn. A scout
  becomes a separate group so it cannot drag the preparing force along. Local
  combat decisions resume on subsequent observations. This avoids making every
  group wait independently for more reinforcements or more information.
- **Maintenance:** Jev can enable paid building repair or sell a power consumer
  during a shortage. Observed repair state and revalidation prevent repeated
  orders from toggling an active repair off.
- **Input budget:** rule metadata and patch quantities appear once in shared
  state. Local target lists contain at most 24 entries. V2 samples placement/target
  choices further if a single question plus state cannot fit a 48,000-character
  native batch. Larger turns split into sequential native batches sharing exactly
  the same observation; no answer is passed into another batch. All answers must
  validate before any orders are submitted, and the usual freshness check still
  applies. Raw exchanges live under each turn's `batches/`; `response.json`
  explicitly labels aggregation when several batches were needed and sums usage.
  This is an empirical budget, not an exact token count. A native
  `max_tokens_exceeded` response lowers the budget for the next fresh observation,
  down to 24,000 characters. Other non-retryable API failures retain normal behavior.

The comparison specs use a two-second command hold for v2 and the original
five-second hold for v1. Run them sequentially to swap starting positions:

```bash
dotnet orf/bin/Release/net10.0/orf.dll run --spec orf/specs/jev-v2-v1.yaml
dotnet orf/bin/Release/net10.0/orf.dll run --spec orf/specs/jev-v2-v1-reverse.yaml
python3 orf/analyze_jev.py runs/<first-run> runs/<reverse-run>
```

V2 also persists `agents/<slug>/tactics.json`. V1 receives its original observation
shape; only v2 gets the added spatial economy and repair-state fields. See the
[experiment report](experiments/2026-09-17-jev-v2.md) for diagnosis and results.

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
