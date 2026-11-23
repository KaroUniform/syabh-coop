#!/usr/bin/env python3
"""Сводка замера -coopperftest: по фазам far/robots/back — fps клиента и сервера,
худший кадр, %CPU процессов (run/logs/cpu-<метка>.txt от сэмплера, если есть).
  tools/perf_summary.py <метка> [<метка> ...]"""
import re, sys, os
L = 'run/logs'
pat = re.compile(r'\[SYABH-COOP\] \[([\d.]+)\] perf (server|client) \[(\w+)\]: ([\d.]+) fps, avg ([\d.]+) ms, worst (\d+) ms, gc (\d+)')
ph = re.compile(r'\[SYABH-COOP\] \[([\d.]+)\] perf phase (\w+)')
def read(p):
    try: return open(p, encoding='utf-8', errors='replace').read().splitlines()
    except OSError: return []
for tag in sys.argv[1:]:
    rows = {}
    for side in ('server', 'a'):
        for l in read(f'{L}/{tag}-{side}.txt'):
            m = pat.search(l)
            if m and m.group(3) in ('far', 'robots', 'back'):
                rows.setdefault((m.group(2), m.group(3)), []).append((float(m.group(4)), int(m.group(6)), int(m.group(7))))
    phases = [(float(m.group(1)), m.group(2)) for m in map(ph.search, read(f'{L}/{tag}-server.txt')) if m]
    cpu = [tuple(map(float, l.split())) for l in read(f'{L}/cpu-{tag}.txt') if len(l.split()) == 3]
    print(f'== {tag}')
    for i, (t, name) in enumerate(phases):
        end = phases[i + 1][0] if i + 1 < len(phases) else t + 30
        # сэмплер стартует вместе с прогоном, сервер — на ~3 с позже; края фаз отбрасываем
        c = [x for x in cpu if t + 8 <= x[0] - 3 <= end - 3]
        line = f'  {name:7}'
        for side in ('client', 'server'):
            r = rows.get((side, name), [])
            if r: line += f'  {side}: {sum(x[0] for x in r)/len(r):6.1f} fps, worst {max(x[1] for x in r):4d} ms, gc {sum(x[2] for x in r)}'
        if c: line += f'  | CPU server {sum(x[1] for x in c)/len(c):5.1f}%  client {sum(x[2] for x in c)/len(c):5.1f}%  ({len(c)} проб)'
        print(line)
