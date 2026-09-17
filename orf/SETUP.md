# orf setup (Anchorage box)

## One-time system setup (needs sudo — Kyle runs these)

```sh
sudo apt install -y xvfb
```

That's the only system package missing. ffmpeg and .NET 10 are already installed.

## API keys (once, under your user)

Keys live in a private file sourced by your shell — never in the repo (it's public).

```sh
touch ~/.orf-secrets && chmod 600 ~/.orf-secrets
```

Edit `~/.orf-secrets` (e.g. `nano ~/.orf-secrets`) to contain:

```sh
export DEEPINFRA_API_KEY="di-..."
export NOUS_API_KEY="sk-..."
export TYPESAFE_API_KEY="<your TypeSafe key>" # for Jev controllers
```

Then add this line to the end of `~/.bashrc` so every terminal session picks them up:

```sh
[ -f ~/.orf-secrets ] && . ~/.orf-secrets
```

New terminals get the keys automatically; for an already-open shell run
`source ~/.orf-secrets`. Verify with `echo ${DEEPINFRA_API_KEY:+set}`.

## Running a match

```sh
dotnet run --project orf -- run --spec orf/specs/first-ffa.yaml
```

Dashboard: http://localhost:5199 (or the port in the spec). Everything about the run —
states, orders, prompts, responses, replay — lands in `runs/<runId>/` (gitignored).

For the native Jev controller and Jev-versus-Jev experiment, see [JEV.md](JEV.md).

## Remote access while traveling

Options considered (2026-08):

- **ngrok** (quickest): `ngrok http 5199 --basic-auth "kyle:<password>"`. Free tier gives
  a random URL per start and one tunnel; a paid plan gives a stable domain. Fine for now —
  the dashboard is read-mostly, but ALWAYS set the basic auth flag since the URL is public.
- **Tailscale** (better long-term): private mesh VPN, no public exposure at all — the
  dashboard is reachable at `http://anchorage:5199` from your MacBook once both machines
  are on the tailnet. No auth story needed, survives restarts, and later lets `orf` on this
  box and the 128 GB machine talk to each other directly. Recommended once you have
  10 minutes to install it on both machines.
- **Cloudflare Tunnel**: free stable hostname, but wants a domain you own and more setup.

Recommendation: ngrok today, Tailscale when convenient.
