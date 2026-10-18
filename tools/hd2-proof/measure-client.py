#!/usr/bin/env python3
"""Synthetic-only client matrix; needs Pillow and an already built DesktopGL head."""
from pathlib import Path
from PIL import Image
import argparse,json,hashlib,subprocess,os,math,csv,statistics,tempfile
parser=argparse.ArgumentParser(description="HD-2: P643/P687 client GPU matrix, synthetic maps on native wc_icfig")
parser.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[2])
parser.add_argument('--sdl-driver',default='x11' if os.environ.get('DISPLAY') else 'offscreen')
parser.add_argument('--art',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
options=parser.parse_args();root=options.root.resolve();scratch=Path(tempfile.mkdtemp(prefix='hd2-client-'))
pack=scratch/'pack';maps=pack/'models/wc_icfig';maps.mkdir(parents=True,exist_ok=True)
entry={'native':{'Model':'wc_icfig','Texture':'fig02bmp',
    'ModelSha256':hashlib.sha256((options.art/'wc_icfig.mdl').read_bytes()).hexdigest(),
    'TextureSha256':hashlib.sha256((options.art/'fig02bmp.mdl').read_bytes()).hexdigest()}}
(pack/'manifest.json').write_text(json.dumps({'schema':1,'models':{'wc_icfig':entry}}))
for map in ['basecolor','normal','metallic','roughness','emission']:
 im=Image.new('RGB',(256,256));p=im.load()
 for y in range(256):
  for x in range(256):
   if map=='basecolor':p[x,y]=(40,50,55) if x%32<2 or y%32<2 else ((160,85,40) if (x//32+y//32)%2==0 else (70,115,135))
   elif map=='normal':
    nx,ny,nz=.25*math.sin(x*.4),.2*math.cos(y*.4),1;length=math.sqrt(nx*nx+ny*ny+nz*nz);p[x,y]=tuple(round((v/length*.5+.5)*255) for v in [nx,ny,nz])
   elif map=='metallic':p[x,y]=(40,40,40)
   elif map=='roughness':p[x,y]=(128,128,128)
   else:p[x,y]=(0,120,160) if x%64<5 and y%64<5 else (0,0,0)
 im.save(maps/(map+'.png'))
out=options.output.resolve();out.mkdir(parents=True,exist_ok=True);env=os.environ.copy();env.update(ALLEGIANCE_ARTWORK_PATH=str(options.art.resolve()),SDL_VIDEODRIVER=options.sdl_driver,ALSOFT_DRIVERS='null',ALLEGIANCE_NOVSYNC='1',ALLEGIANCE_HD_PACK_PATH=str(pack),ALLEGIANCE_CLIENT_PERF='1',ALLEGIANCE_CLIENT_PERF_SECONDS='15')
results=[]
for width,height in [(1920,1080),(2560,1440)]:
 for fsr in [False,True]:
  for hd in [False,True]:
   tag=f'client-{height}-'+('fsr' if fsr else 'native')+'-'+('hd' if hd else 'off')
   settings=scratch/(tag+'.json');settings.write_text(json.dumps({'HdTextures':hd,'Upscaling':'FSR1' if fsr else 'Off','Preset':'Quality'}))
   csvpath=scratch/(tag+'.csv');env.update(ALLEGIANCE_GRAPHICS_SETTINGS=str(settings),ALLEGIANCE_CLIENT_PERF_CSV=str(csvpath))
   env['ALLEGIANCE_CLIENT_PERF_CAPTURE']=str(out/(tag+'.png'))
   cmd=['dotnet',str(root/'src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.dll'),'--inspect','--inspect-hull','414','--inspect-native-only','--window',f'{width}x{height}','--bloom','on' if fsr else 'off']
   with open(scratch/(tag+'.log'),'w') as log:p=subprocess.run(cmd,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=120)
   data=list(csv.DictReader(csvpath.open())) if csvpath.exists() else []
   # Frame IDs retain the P643/P687 delayed correlation. Discard 2 s from
   # start + capture tail; report NaN coverage rather than inventing GPU time.
   elapsed=0;rows=[]
   for row in data:
    elapsed+=float(row['frame_ms'])
    if elapsed>=2000 and elapsed<=12500:rows.append(row)
   result={'tag':tag,'width':width,'height':height,'hd':hd,'fsrAndBloom':fsr,'exitCode':p.returncode,'frames':len(data),'measuredFrames':len(rows)}
   for key in ['scene_gpu_ms','post_gpu_ms','draw_submit_ms','frame_ms','draw_calls']:
    values=[float(r[key]) for r in rows if math.isfinite(float(r[key]))]
    result[key]={'samples':len(values),'median':statistics.median(values) if values else None,'p95':sorted(values)[int(.95*(len(values)-1))] if values else None}
   print(json.dumps(result),flush=True);results.append(result);(out/'client-matrix.json').write_text(json.dumps(results,indent=2))
# A concrete same-hull, side-by-side inspector screenshot using its new CLI control.
env.update(ALLEGIANCE_CLIENT_PERF_SECONDS='6',ALLEGIANCE_CLIENT_PERF_CAPTURE=str(out/'client-inspector-native-hd.png'),ALLEGIANCE_GRAPHICS_SETTINGS=str(scratch/'client-1080-native-hd.json'),ALLEGIANCE_CLIENT_PERF_CSV=str(scratch/'client-compare.csv'))
with open(scratch/'client-compare.log','w') as log:
 p=subprocess.run(['dotnet',str(root/'src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Allegiance.ClientCore.MonoGame.dll'),'--inspect-hd','--inspect-hull','414','--window','1920x1080'],env=env,stdout=log,stderr=subprocess.STDOUT,timeout=120)
print('CLIENT_MATRIX_DONE',p.returncode,flush=True)

print("Raw CSVs, settings, synthetic pack and logs:",scratch,flush=True)
