"""Measure exact saved furnace fuel geometry and heating; read-only artifacts."""
import argparse, csv, json, pathlib, re
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('root', type=pathlib.Path)
parser.add_argument('--expected-count', type=int, default=6)
parser.add_argument('--baseline', type=pathlib.Path,
                    default=pathlib.Path('artifacts/fire-smoke-visual-20261003/furnace'))
args = parser.parse_args()
dtype = np.dtype([('material','<u4'),('mass','<f4'),('velocity','<f4',(2,)),
                  ('pressure','<f4'),('active','<u4'),('rest','<u4'),('body','<u4'),
                  ('temperature','<f4'),('lifetime','<f4')])
assert dtype.itemsize == 40
results = []
for directory in sorted(args.root.glob('*-open-*')):
    fps = int(directory.name.rsplit('-',1)[1])
    palette = {line.split(':',1)[1].strip(): int(line.split(':',1)[0])
               for line in (directory/'runtime-palette.txt').read_text().splitlines()}
    snapshots = [(int(p.stem.split('-')[1]),p) for p in directory.glob('grid-*.bin')]
    snapshots.append((60*fps,directory/'final-grid.bin'))
    samples=[]
    for frame,path in sorted(snapshots):
        if frame>60*fps: continue
        cells=np.fromfile(path,dtype=dtype)
        ids=np.flatnonzero((cells['active']!=0)&(cells['material']==palette['core:coal']))
        samples.append({'seconds':frame/fps,'cells':len(ids),
                        'left':int((ids%672<367).sum()),'right':int((ids%672>=367).sum()),
                        'mass':float(cells['mass'][ids].sum(dtype=np.float64))})
    first=next((s for s in samples if s['cells']<7097),None)
    final=samples[-1]
    with (directory/'saved-furnace.csv').open() as stream:
        rows=list(csv.DictReader(stream))
    at60=next(r for r in rows if int(r['frame'])==60*fps)
    # Compare equal FPS and equal simulated time. The prior bright-fire stage
    # changed render only; its 60-FPS physics matches this complete old matrix.
    with (args.baseline/directory.name/'saved-furnace.csv').open() as stream:
        before=next(r for r in csv.DictReader(stream) if int(r['frame'])==60*fps)
    heat_ratio=(float(at60['farFloorT'])-20)/(float(before['farFloorT'])-20)
    report=(directory/'report.txt').read_text()
    speed=float(re.search(r'speed95=([\d.]+)',report)[1])
    limit=30 if directory.name.startswith('sandbox') else 60
    passed=(first is not None and first['seconds']<limit and
            first['right']<3599 and first['left']>=3498 and
            7097-final['cells']>=25 and heat_ratio>=.8 and speed<=4 and
            'lateFlow=True' in report)
    results.append({'case':directory.name,'firstReduction':first,'at60':final,
                    'farFloorT':float(at60['farFloorT']), 'baselineFarFloorT':float(before['farFloorT']),
                    'heatRatio':heat_ratio,'speed95':speed,'passed':bool(passed),'samples':samples})
timing = {}
for mode in ['sandbox','simulation']:
    times=[r['firstReduction']['seconds'] for r in results if r['case'].startswith(mode) and r['firstReduction']]
    timing[mode]=max(times)-min(times) if times else None
overall=(len(results)==args.expected_count and all(r['passed'] for r in results) and
         all(t is not None and t<=2 for t in timing.values()))
output={'passed':overall,'firstReductionTimingSpread':timing,'results':results}
(args.root/'coal-analysis.json').write_text(json.dumps(output,indent=2)+'\n')
for r in results:
    print(f"{r['case']} first={r['firstReduction']} at60={r['at60']} heatRatio={r['heatRatio']:.3f} speed95={r['speed95']:.3f} PASS={r['passed']}")
print(f'COAL_ACCEPTANCE passed={overall} timingSpread={timing}')
raise SystemExit(0 if overall else 1)
