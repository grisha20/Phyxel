"""Read-only checks of the historical soft-fire stage, superseded by user preference.

The zero-white target is not a criterion for the current bright TPT fire display.
Use the preserved fire-smoke-visual artifacts to inspect that historical stage.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


def image_counts(path):
    rgb = np.array(Image.open(path).convert('RGB')).astype(np.int16)
    r, g, b = rgb.transpose(2, 0, 1)
    return {'white': int(((r >= 245) & (g >= 245) & (b >= 245)).sum()),
            'warm': int(((r >= 180) & (r > g) & (g > b)).sum())}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def analyze(before, after, smoke_before, smoke_after, performance_before, performance_after,
            fresh_baseline=None):
    checks = []
    for case in sorted(after.glob('*-open-*')):
        old = before / case.name
        if fresh_baseline and (fresh_baseline / case.name).exists():
            old = fresh_baseline / case.name
        baseline = image_counts(old / 'saved-furnace-60s.png')
        result = image_counts(case / 'saved-furnace-60s.png')
        checks.append({'case': case.name, 'reference': str(old), 'check': 'colour', 'baseline': baseline,
                       'result': result, 'passed': result['white'] <= baseline['white'] * .1
                       and result['warm'] >= baseline['warm'] * .8})
        snapshots = sorted(case.glob('*.bin'))
        mismatches = [p.name for p in snapshots if not (old / p.name).exists()
                      or digest(p) != digest(old / p.name)]
        checks.append({'case': case.name, 'check': 'physics-byte-equality',
                       'files': len(snapshots), 'mismatches': mismatches,
                       'passed': bool(snapshots) and not mismatches})
    raw_before = np.array(Image.open(smoke_before / 'effects-0/smoke-render.png'))
    raw_after = np.array(Image.open(smoke_after / 'effects-0/smoke-render.png'))
    checks.append({'check': 'raw-smoke-pixels', 'passed': bool(np.array_equal(raw_before, raw_after))})
    for mode in ('Simulation', 'Sandbox'):
        import csv
        with performance_before.open(newline='') as file:
            baseline = next(r for r in csv.DictReader(file) if r['Mode'] == mode)
        with performance_after.open(newline='') as file:
            result = next(r for r in csv.DictReader(file) if r['Mode'] == mode)
        ratio = float(result['RealFps']) / float(baseline['RealFps'])
        checks.append({'check': 'performance', 'mode': mode, 'fpsRatio': ratio,
                       'passed': ratio >= .9 and result['ClockPassed'] == 'True'})
    return {'checks': checks, 'passed': len(checks) >= 15 and all(c['passed'] for c in checks)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--fresh-baseline', type=Path)
    args = parser.parse_args()
    result = analyze(args.baseline / 'furnace', args.root / 'furnace',
                     args.root / 'baseline-smoke', args.root / 'smoke',
                     args.baseline / 'fire-performance/summary.csv',
                     args.root / 'performance/summary.csv', args.fresh_baseline)
    (args.root / 'acceptance.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))
    raise SystemExit(0 if result['passed'] else 1)
