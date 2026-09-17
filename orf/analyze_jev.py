#!/usr/bin/env python3
"""Summarize recorded Jev matches without model calls or observer information as input.

Usage: python3 orf/analyze_jev.py runs/<run> [runs/<other-run> ...]
Writes analysis.json in each run. Purchases/commands count submissions, while unit
and group measurements use the observations recorded with those decisions.
"""
import collections
import json
import math
import pathlib
import statistics
import sys


def read(path, default=None):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return default


def distance(a, b):
    return math.dist(a, b)


def analyze(run):
    result = read(run / 'result.json', {})
    match = read(run / 'match.json', {})
    summary = {'run': run.name, 'finished': bool(result),
               'durationSeconds': result.get('second'),
               'winners': [p['slug'] for p in result.get('players', []) if p['winState'] == 'Won'],
               'players': {}}
    for agent in sorted((run / 'agents').iterdir()):
        if not (agent / 'turns').is_dir():
            continue
        orders, purchases, first_seen = collections.Counter(), collections.Counter(), {}
        models, rejections = set(), collections.Counter()
        advance_sizes, solo_advances, attack_sizes, spans, latency = [], 0, [], [], []
        peak_harvesters = peak_army = low_power = 0
        timeline, seen_seconds, refinery_sites = [], set(), []
        for turn in sorted((agent / 'turns').iterdir()):
            state, request = read(turn / 'state.json'), read(turn / 'request.json')
            if not state or not request:
                continue
            trace, response = read(turn / 'decisions.json', {}), read(turn / 'response.json', {})
            if response.get('model'):
                models.add(response['model'])
            if response:
                latency.append(max(0, (turn / 'response.json').stat().st_mtime - (turn / 'request.json').stat().st_mtime) * 1000)
            second, catalog = state['second'], state.get('ruleCatalog', {})
            units = {u['id']: u for u in state.get('units', [])}
            buildings = state.get('buildings', [])
            for actor in list(units.values()) + buildings:
                first_seen.setdefault(actor['name'], second)
            armed = [u for u in units.values() if catalog.get(u['name'], {}).get('weapons')]
            harvesters = sum(catalog.get(u['name'], {}).get('isHarvester', u['name'] == 'Harvester') for u in units.values())
            army = sum(catalog.get(u['name'], {}).get('cost', 0) for u in armed)
            peak_harvesters, peak_army = max(peak_harvesters, harvesters), max(peak_army, army)
            groups = request.get('state', {}).get('tactics', {}).get('groups')
            memberships = [g['members'] for g in groups.values()] if groups is not None else request.get('state', {}).get('memory', {}).get('squads', {}).values()
            current_spans = []
            for members in memberships:
                positions = [units[i]['cell'] for i in members if i in units]
                if len(positions) > 1:
                    current_spans.append(max(distance(a, b) for a in positions for b in positions))
            if second not in seen_seconds:
                seen_seconds.add(second)
                spans.extend(current_spans)
                low_power += state['you'].get('powerProvided', 0) < state['you'].get('powerDrained', 0)
                if second % 15 == 0:
                    timeline.append({'second': second, 'cash': state['you']['cash'], 'incomePerMinute': state['you'].get('incomePerMinute', 0),
                                     'harvesters': harvesters, 'armedUnits': len(armed), 'armyValue': army,
                                     'refineries': sum(b['name'] == 'Tiberium Refinery' for b in buildings),
                                     'largestGroupSpan': round(max(current_spans, default=0), 1)})
            enemy_locations = [e['cell'] for e in state.get('enemySpawns', []) + state.get('lastKnownEnemyBuildings', [])]
            for order in trace.get('orders', []):
                orders[order['type']] += 1
                if order['type'] == 'start_production':
                    purchases[order['item']] += order.get('count', 1)
                if order['type'] in ('attack', 'attack_move'):
                    attack_sizes.append(len(order.get('actorIds', [])))
                if order['type'] == 'attack_move' and order.get('cell') and enemy_locations:
                    members = [units[i] for i in order.get('actorIds', []) if i in units]
                    if members:
                        center = [statistics.mean(u['cell'][axis] for u in members) for axis in (0, 1)]
                        if distance(center, order['cell']) >= 25 and min(distance(order['cell'], e) for e in enemy_locations) <= 20:
                            advance_sizes.append(len(members))
                            solo_advances += len(members) == 1 and sum(distance(center, u['cell']) <= 10 for u in armed) <= 1
                if order['type'] == 'place_building' and order['item'] == 'Tiberium Refinery':
                    chosen = next((q['criteria'].get('c' + '_'.join(map(str, order['cell'])))
                                   for key, q in request.get('questions', {}).items() if key.startswith('placement')), None)
                    refinery_sites.append({'second': second, 'cell': order['cell'], 'candidate': chosen})
        for path in (run / 'orders' / agent.name / 'results').glob('*.json'):
            for entry in read(path, {}).get('results', []):
                if entry.get('status') == 'rejected':
                    rejections[entry.get('reason', 'unknown')] += 1
        status = read(agent / 'status.json', {})
        errors = (agent / 'errors.log').read_text().splitlines() if (agent / 'errors.log').exists() else []
        summary['players'][agent.name] = {
            'policyVersion': status.get('policyVersion', next((p.get('jevPolicyVersion', 1) for p in match.get('players', []) if p['slug'] == agent.name), 1)),
            'models': sorted(models), 'turns': status.get('turn'), 'errors': errors, 'engineRejections': dict(rejections),
            'costEstimateUsd': status.get('totalCostUsd'), 'medianApiMilliseconds': round(statistics.median(latency), 1) if latency else None,
            'observations': len(seen_seconds), 'lowPowerObservations': low_power, 'firstSeenSecond': first_seen,
            'purchasesSubmitted': dict(purchases), 'ordersSubmitted': dict(orders), 'attackCommandSizes': dict(collections.Counter(attack_sizes)),
            'distantAdvanceCount': len(advance_sizes), 'isolatedSingleUnitAdvances': solo_advances,
            'medianDistantAdvanceSize': statistics.median(advance_sizes) if advance_sizes else None,
            'medianGroupSpanCells': round(statistics.median(spans), 1) if spans else None,
            'peakHarvesters': peak_harvesters, 'peakArmyValue': peak_army, 'refinerySites': refinery_sites, 'timeline': timeline
        }
    return summary


def main():
    for name in sys.argv[1:]:
        run = pathlib.Path(name)
        summary = analyze(run)
        (run / 'analysis.json').write_text(json.dumps(summary, indent=2) + '\n')
        seconds = summary['durationSeconds']
        duration = f'{seconds // 60}:{seconds % 60:02}' if seconds is not None else 'running'
        print(f"{run.name}: {duration}; winners: {', '.join(summary['winners']) or 'pending'}")
        for slug, p in summary['players'].items():
            print(f"  {slug}: v{p['policyVersion']}; peak harvesters {p['peakHarvesters']}; peak army ${p['peakArmyValue']}; "
                  f"median distant advance {p['medianDistantAdvanceSize']} units; isolated advances {p['isolatedSingleUnitAdvances']}/{p['distantAdvanceCount']}; "
                  f"median group span {p['medianGroupSpanCells']} cells; errors {len(p['errors'])}; rejections {sum(p['engineRejections'].values())}")


if __name__ == '__main__':
    main()
