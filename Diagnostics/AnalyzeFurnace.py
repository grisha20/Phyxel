"""Read furnace diagnostic snapshots; never modify the saved scene."""
import sys, pathlib, struct, json
root=pathlib.Path(sys.argv[1]); w,h=672,378
palette={int(line.split(':',1)[0]):line.split(':',1)[1].strip() for line in (root/'runtime-palette.txt').read_text().splitlines()}
frames=[(root/'final-grid.bin',root/'final-oxygen.bin')]
for gp,op in frames:
    cells=list(struct.iter_unpack('<I4f3I2f',gp.read_bytes()))
    stock=struct.unpack(f'<{w*h}f',op.read_bytes())
    names={v:k for k,v in palette.items()}
    summary={}
    for name in ['core:fire','core:smoke','core:co2']:
        ids=[i for i,c in enumerate(cells) if c[5] and c[0]==names[name]]
        summary[name]={'count':len(ids),'mass':sum(cells[i][1] for i in ids),'temperature':sum(cells[i][8] for i in ids)/max(1,len(ids))}
    front=[]
    for i,c in enumerate(cells):
        if not c[5] or c[0]!=names['core:coal']:continue
        donors=[n for n in [i-1,i+1,i-w,i+w] if 0<=n<w*h and (not cells[n][5] or palette[cells[n][0]] in ['core:fire','core:smoke','core:co2','core:steam'])]
        if donors:front.append((c[8],sum(min(stock[n],1) for n in donors)/len(donors),c[9]))
    summary['fuelSurface']={'count':len(front),'hot':sum(t>400 for t,o,l in front),'lit':sum(l>0 for t,o,l in front),'oxygenMean':sum(o for t,o,l in front)/max(1,len(front)),'starved':sum(o<=.2 for t,o,l in front)}
    chimney=[i for i,c in enumerate(cells) if c[5] and palette[c[0]] in ['core:fire','core:smoke'] and 154<=i%w<=184 and 20<=i//w<=230]
    summary['chimneySmokeFire']={'count':len(chimney),'wallFraction':sum(i%w<=156 or i%w>=182 for i in chimney)/max(1,len(chimney)),'meanX':sum(i%w for i in chimney)/max(1,len(chimney))}
    print(json.dumps(summary,indent=2))
