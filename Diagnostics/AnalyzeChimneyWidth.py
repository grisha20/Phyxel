"""Cross-section occupancy and carrier flux in the unchanged user furnace."""
import argparse,json,pathlib,csv
import numpy as np

p=argparse.ArgumentParser();p.add_argument('root');p.add_argument('--baseline');p.add_argument('--require-pass',action='store_true');a=p.parse_args();root=pathlib.Path(a.root)
baselines={r['case']:r for r in json.loads(pathlib.Path(a.baseline).read_text())} if a.baseline else {}
w,h=672,378
dtype=np.dtype([('id','<u4'),('mass','<f4'),('vx','<f4'),('vy','<f4'),('pressure','<f4'),
                ('active','<u4'),('rest','<u4'),('body','<u4'),('temp','<f4'),('life','<f4'),
                ('water','<f4'),('waterEnergy','<f4'),('oil','<f4')])
airtype=np.dtype([('pressure','<f4'),('vx','<f4'),('vy','<f4'),('blocked','<f4')])
results=[]
for directory in sorted(root.glob('*-*')):
    if not (directory/'runtime-palette.txt').exists():continue
    ids={line.split(':',1)[1].strip():int(line.split(':',1)[0]) for line in (directory/'runtime-palette.txt').read_text().splitlines()}
    gasids=[ids[name] for name in ['core:fire','core:smoke','core:co2']]
    fps=int(directory.name.rsplit('-',1)[1]);rows=[]
    files=[(frame,directory/f'grid-{frame}.bin',directory/f'air-{frame}.bin') for frame in range(40*fps,60*fps,5*fps)]
    files.append((60*fps,directory/'final-grid.bin',directory/'final-air.bin'))
    for frame,gp,ap in files:
        if not gp.exists():continue
        check=gp.stat().st_size==w*h*dtype.itemsize
        if not check:raise ValueError(f'{gp}: unexpected grid layout')
        grid=np.fromfile(gp,dtype=dtype).reshape(h,w)
        air=np.fromfile(ap,dtype=airtype).reshape((h+3)//4,(w+3)//4)
        band=grid[20:151,154:185]
        gas=(band['active']!=0)&np.isin(band['id'],gasids)
        mass=np.where(gas,band['mass'],0)
        columns=mass.sum(axis=0);total=float(columns.sum())
        quantiles=np.searchsorted(np.cumsum(columns),np.array([.05,.95])*total) if total>0 else np.array([0,0])
        wall=float(columns[:4].sum()+columns[-4:].sum())/max(1e-12,total)
        # Temporal histogram includes mass, not only full-cell counts; a tiny
        # CO2 packet must not count as an opaque column of dense smoke.
        section=air[5:38,39:46];up=np.maximum(0,-section['vy'])
        # Also report local cross-sections; an aggregate wide histogram alone
        # could hide a thin stream winding sideways along the sampled height.
        localwidths=[]
        for y in range(0,120,10):
            histogram=mass[y:y+10].sum(axis=0);m=float(histogram.sum())
            if m>0:
                q=np.searchsorted(np.cumsum(histogram),np.array([.05,.95])*m)
                localwidths.append(int(q[1]-q[0]+1))
        rows.append({'frame':frame,'occupiedColumns':int((columns>0).sum()),'width90':int(quantiles[1]-quantiles[0]+1),
                     'localWidth90Mean':float(np.mean(localwidths)) if localwidths else 0,
                     'gasMass':total,'wallMassFraction':wall,'upwardCarrierMean':float(up.mean()),
                     'signedCarrierMean':float(-section['vy'].mean()),'columnsMass':[float(v) for v in columns]})
    summary={'case':directory.name,'samples':rows,'meanWidth90':float(np.mean([r['width90'] for r in rows])),
             'meanWallMassFraction':float(np.mean([r['wallMassFraction'] for r in rows])),
             'meanUpwardCarrier':float(np.mean([r['upwardCarrierMean'] for r in rows])),
             'meanSignedCarrier':float(np.mean([r['signedCarrierMean'] for r in rows]))}
    summary['meanLocalWidth90']=float(np.mean([r['localWidth90Mean'] for r in rows]))
    baseline=baselines.get(directory.name.rsplit('-',1)[0]+'-60')
    if baseline:
        summary['carrierRatio']=summary['meanUpwardCarrier']/baseline['meanUpwardCarrier']
        summary['CW01Passed']=len(rows)==5 and summary['meanWidth90']>=22 and summary['carrierRatio']>=.9
    results.append(summary)
    print(json.dumps({k:v for k,v in summary.items() if k!='samples'},ensure_ascii=False))
(root/'chimney-width.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
if not results:raise SystemExit('No furnace snapshots')
if a.require_pass and (not baselines or any(not r.get('CW01Passed',False) for r in results)):
    raise SystemExit('CW01 failed or baseline missing')
