import json, struct, re, hashlib
from pathlib import Path
from PIL import Image,ImageChops,ImageStat
import argparse
parser=argparse.ArgumentParser(description="Read-only audit of native rows, HD inputs and deployed DDS basecolor")
parser.add_argument('--art',type=Path,required=True)
parser.add_argument('--pack-repo',type=Path,required=True)
parser.add_argument('--out',type=Path,required=True)
args=parser.parse_args()
art,hd=args.art,args.pack_repo
def decode(path):
 d=path.read_bytes(); pos=24
 counts=struct.unpack_from('<6I',d)[2:]
 def skip():
  nonlocal pos
  pos=(d.index(b'\0',pos)+4)&~3
 for _ in range(counts[0]): skip()
 for _ in range(counts[1]): pos+=4; skip()
 for _ in range(counts[2]): skip()
 pos+=16
 w,h,p,b=struct.unpack_from('<4i',d,pos); pos+=36
 assert b==16
 out=bytearray(w*h*4)
 for y in range(h):
  for x in range(w):
   v=struct.unpack_from('<H',d,pos+y*p+x*2)[0]; i=(y*w+x)*4
   out[i:i+4]=bytes((round((v>>11)/31*255), round(((v>>5)&63)/63*255),round((v&31)/31*255),255))
 return Image.frombytes('RGBA',(w,h),bytes(out))
inputs={}
for path in (hd/'inputs').glob('*/native.json'):
 doc=json.loads(path.read_text()); stem=doc.get('Texture'); png=path.with_name('native.png')
 if stem and png.exists(): inputs.setdefault(stem,[]).append(png)
manifest=json.loads((hd/'pack/allegiance-hd-v1/manifest.json').read_text())
rows=[]
for stem in manifest['textures']:
 src=art/(stem+'.mdl'); d=src.read_bytes(); binary=d[:4]==bytes.fromhex('00dfbade')
 if binary: native=decode(src)
 else:
  m=re.search(rb'(?:ImportImageFromFile|ImageFromFile)\("([^"]+)"',d)
  if not m: rows.append({'stem':stem,'error':'no import'}); continue
  native=Image.open(art/m[1].decode()).convert('RGBA')
 candidates=inputs.get(stem,[])
 # ImportedImage native.json retains Texture as well, but also accept stem directory.
 p=hd/'inputs'/stem/'native.png'
 if p.exists() and p not in candidates: candidates.append(p)
 row={'stem':stem,'source':'binary' if binary else 'image','inputCount':len(candidates),'inputOrders':[]}
 for p in candidates:
  im=Image.open(p).convert('RGBA'); a=im.tobytes(); raw=native.tobytes(); flip=native.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes()
  row['inputOrders'].append({'input':p.parent.name,'order':'file' if a==raw else 'flipped' if a==flip else 'different','sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
 # Compare deployed compressed basecolor to source rows; resized maps have detail differences.
 packed=Image.open(hd/'pack/allegiance-hd-v1/textures'/stem/'basecolor.dds').convert('RGB').resize((32,32))
 ref=native.convert('RGB').resize((32,32)); rev=ref.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
 row['deployedMaeFile']=round(sum(ImageStat.Stat(ImageChops.difference(packed,ref)).mean)/3,4)
 row['deployedMaeFlipped']=round(sum(ImageStat.Stat(ImageChops.difference(packed,rev)).mean)/3,4)
 rows.append(row)
 print(stem, row['source'], [x['order'] for x in row['inputOrders']],row['deployedMaeFile'],row['deployedMaeFlipped'],flush=True)
args.out.write_text(json.dumps(rows,indent=2)+'\n')
