# MissingNo debut — 2026-10-11

MissingNo (`stealth/missingno`) is a free reasoning model on the Nous Portal: 262K
context, tool calling, structured outputs, reasoning always on (`reasoning_effort`
low…max, returned as `reasoning_content`). It is not on OpenRouter. It drove the v7
commander (`prompts/system-v7.md`, markdown state, plan executor) in every match.

## Harness changes it needed

- **User-Agent.** Nous's Cloudflare front answers requests without a User-Agent with
  a 403 HTML page; .NET HttpClient sends none. `LlmClient` now sets one.
- **Tool-call order.** MissingNo emits parallel tool calls in arbitrary order — it
  issued `attack_move` on group "ball" before the `set_group` that created it.
  `set_group` calls now run first.
- **Sandbag spam.** It bought 20 Sandbag Barriers in 70 s because the prompt said
  "idle production queue → start something" and listed walls. The prompt now exempts
  the Support queue.
- **Token-budget pacing.** The free key allows 720k tokens/min and 30.24M tokens/hour
  (fixed windows). Three lanes at ~25k-token prompts would have drained the hour in
  ~40 min and frozen mid-match. `LlmClient.UpdatePace` reads the `x-ratelimit-*`
  headers and delays turns so every lane on the key slows together; `historyTurns: 3`
  cut prompts to ~22k tokens. Result: zero 429s for the rest of the night.
- **False failover.** Three text-only "nothing to do" answers counted as provider
  failures and switched the lane to `stepfun/step-5-preview:free`, which answers every
  request with 400 "missing tags". Text answers are now live turns; no fallback model.
- **Poisoned history.** One tool call with empty arguments, replayed in history, made
  every later request a 400. Arguments are sanitized before replay, a 400 clears the
  rolling history, and 4xx responses are no longer retried.

## Results

All GDI on Scorched Earth, spawn 1 = top-left, spawn 8 = bottom-right. Two matches
ran in parallel at 2048² (≈90% game speed under llvmpipe; 2540² ran at ≈70%).

| Run | Spawn 1 | Spawn 8 | Winner | Game time |
| --- | --- | --- | --- | ---: |
| `20261011-062859-missingno-1v1` | MissingNo-Low | MissingNo-Medium | Low | 14:05 |
| `20261011-064546-missingno-1v1-reverse` | MissingNo-Medium | MissingNo-Low | Medium | 9:07 |
| `20261011-062907-jev-vs-missingno` | Jev v2 | MissingNo (low) | MissingNo | 18:54 |
| `20261011-065114-jev-vs-missingno-reverse` | MissingNo (low) | Jev v2 | MissingNo | 8:50 |
| `20261011-065703-missingno-mirror-low` | MissingNo-Low-A | MissingNo-Low-B | — (Nous outage) | stopped ~11:00 |

**Low vs medium effort: 1–1.** Spawn 1 won both, and so did the faster lane both
times — but which lane was faster flipped between games (Low 2.4 s vs Medium 7.6 s,
then Low 7.3 s vs Medium 3.1 s), driven by Nous latency, not the model: Low used fewer
reasoning tokens in both (median ~130 vs ~230–285). No effort effect is visible at n=2.

**MissingNo vs Jev: 2–0.** Jev trades units well (game 1: 40 killed for 21 lost) but
stays on one refinery and a handful of units; MissingNo out-earned it 44.1k–16.6k and
18.9k–10.5k, then destroyed every building without losing one.

| Game 1 (`062907`) | MissingNo | Jev v2 |
| --- | ---: | ---: |
| Decisions / mean latency | 218 / 2.7 s | 1,133 / 0.22 s |
| Earned | 44,100 | 16,568 |
| Units killed / lost | 21 / 40 | 40 / 21 |
| Buildings killed / lost | 10 / 0 | 0 / 10 |

At ~07:08 UTC the MissingNo endpoint began returning HTTP 500 to every request,
including trivial ones; the mirror match was stopped and the planned ladder (below)
held.

## Next: MissingNo vs a Jev alliance

Planned hill-climb: MissingNo at spawn 8 (team 1) against Jevs allied on team 2 at
spawns 1…N along the top edge, N = 2 → 3 → 4. Each harness version carries an
`evolution:` block in its spec (version, parent, tweak, challenge, lineage), which
the dashboard shows as a banner under the header. Multi-lane players are the likely
route past N = 2, bounded by the single Nous key's token budget.
