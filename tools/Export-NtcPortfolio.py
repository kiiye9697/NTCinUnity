"""Prepare public NTC portfolio media and measured data from local review artifacts."""
import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import zipfile

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image
import imageio_ffmpeg


def main(args):
    root=Path(__file__).resolve().parent.parent
    out=args.output.resolve();out.mkdir(parents=True,exist_ok=True)
    report=json.loads((args.review/'review-data.json').read_text(encoding='utf-8'))
    container=runpy.run_path(str(root/'tools/Build-NtcReview.py'))['container']
    def write_json(name,value):
        (out/name).parent.mkdir(parents=True,exist_ok=True)
        (out/name).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
    def write_csv(name,rows):
        with (out/name).open('w',newline='',encoding='utf-8') as f:
            writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    def figure(fig,name):
        for ext in ['svg','png']:fig.savefig(out/f'{name}.{ext}',dpi=140,bbox_inches='tight',facecolor='white')
        plt.close(fig)
    plt.rcParams.update({'font.size':10,'svg.fonttype':'none'})

    # Same pixels as the local channel PNGs; WebP lossless reduces transfer size.
    for m in report['materials']:
        for c in m['channels']:
            for kind in ['input','output','diff']:
                p=Path(c[kind]);target=out/p.with_suffix('.webp');target.parent.mkdir(parents=True,exist_ok=True)
                if not target.exists():Image.open(args.review/p).save(target,lossless=True,method=4)
                c[kind]=p.with_suffix('.webp').as_posix()
                preview=Path('previews')/p.with_suffix('.webp')
                (out/preview).parent.mkdir(parents=True,exist_ok=True)
                image=Image.open(target);image.thumbnail((640,640),Image.Resampling.LANCZOS)
                image.save(out/preview,quality=84,method=5)
                c[kind+'Preview']=preview.as_posix()
        m['latentPreviews']=[]
        for p in m['latentImages']+[m['weights']]:
            target=out/p;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(args.review/p,target)
            if p in m['latentImages']:
                preview=Path('previews')/Path(p).with_suffix('.webp');(out/preview).parent.mkdir(parents=True,exist_ok=True)
                image=Image.open(target);image.thumbnail((320,320),Image.Resampling.LANCZOS);image.save(out/preview,quality=84,method=5)
                m['latentPreviews'].append(preview.as_posix())
    for name in ['original','ntc-direct']:
        p=out/'media'/f'{name}.webp';p.parent.mkdir(exist_ok=True)
        Image.open(args.review/'media'/f'{name}.png').resize((1280,720),Image.Resampling.LANCZOS).save(p,quality=88,method=5)
    Image.open(args.review/'media'/'ntc-direct.png').resize((960,540),Image.Resampling.LANCZOS).save(out/'cover.webp',quality=86,method=5)
    for name in ['Original','NTC-Direct']:
        if (out/'media'/f'{name}.mp4').exists():continue
        subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(),'-hide_banner','-loglevel','error','-y','-i',str(args.review/'media'/f'{name}.mp4'),'-vf','scale=1280:720','-c:v','libx264','-crf','22','-preset','fast','-an','-movflags','+faststart',str(out/'media'/f'{name}.mp4')],check=True)
    for path in (args.review/'figures').glob('*'):
        if path.suffix in ['.svg','.png']:
            target=out/'figures'/path.name;target.parent.mkdir(exist_ok=True);shutil.copy2(path,target)
    report['preview']='640px WebP quality 84 display thumbnails; full channel images preserve the original PNG pixels losslessly. Quality metrics use the full channel images.'
    write_json('review-data.json',report)

    # Historical logs: no inferred association with the current 24 material bindings.
    training=[]
    for label,file in [('SponzaBricks','compress_sponza.log'),('MetalPlates013','compress_metalplates.log')]:
        blob=(root/'nvidia-backend'/file).read_bytes()
        text=blob.decode('utf-16' if blob.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig')
        rows=[dict(asset=label,step=int(step),milliseconds_per_step=float(ms),psnr_db=float(psnr),normalized_mse_from_psnr=10**(-float(psnr)/10)) for step,ms,psnr in re.findall(r'Training: (\d+) steps, ([\d.]+) ms/step, intermediate PSNR: ([\d.]+) dB',text)]
        if len(rows)!=100:raise ValueError(f'Expected 100 logged checkpoints: {file}')
        training.extend(rows)
    write_csv('training-curves.csv',training)
    fig,axes=plt.subplots(1,2,figsize=(12,4))
    for label in ['SponzaBricks','MetalPlates013']:
        rows=[r for r in training if r['asset']==label]
        axes[0].plot([r['step'] for r in rows],[r['psnr_db'] for r in rows],label=label)
        axes[1].semilogy([r['step'] for r in rows],[r['normalized_mse_from_psnr'] for r in rows],label=label)
    axes[0].set_ylabel('Logged training PSNR (dB)');axes[1].set_ylabel('Normalized MSE = 10^(-PSNR/10)')
    for axis in axes:axis.set_xlabel('Training step');axis.legend();axis.grid(alpha=.2)
    fig.suptitle('Historical training records · 100,000 steps · loss reconstructed from rounded PSNR')
    figure(fig,'training-curves')

    # Interpolation reference on the actual input/reconstructed channel pixels.
    probes=[];fig,axes=plt.subplots(1,3,figsize=(15,4))
    for axis,m in zip(axes,report['materials']):
        c=next(c for c in m['channels'] if c['name']=='Color')
        source=np.asarray(Image.open(out/c['input']),dtype=np.float64)/255
        decoded=np.asarray(Image.open(out/c['output']),dtype=np.float64)/255
        rng=np.random.default_rng(20261127);h,w=source.shape[:2]
        x=rng.integers(0,w-1,2048);y=rng.integers(0,h-1,2048);fy=.37
        curves=[[],[]]
        for fx in np.linspace(0,1,65):
            def bilinear(a):return ((1-fx)*(1-fy)*a[y,x]+fx*(1-fy)*a[y,x+1]+(1-fx)*fy*a[y+1,x]+fx*fy*a[y+1,x+1])
            reference=bilinear(source)
            nearest=decoded[y,x+(fx>=.5)];filtered=bilinear(decoded)
            n=float(np.mean((nearest-reference)**2));b=float(np.mean((filtered-reference)**2))
            curves[0].append(n);curves[1].append(b)
            probes.append(dict(asset=m['name'],fraction_x=float(fx),fraction_y=fy,point_mse=n,bilinear_mse=b,samples=2048))
        for values,label in zip(curves,['NTC Point','NTC Bilinear']):axis.plot(np.linspace(0,1,65),values,label=label)
        axis.set_title(m['name']);axis.set_xlabel('Sub-texel x position');axis.set_ylabel('MSE vs original bilinear');axis.legend();axis.grid(alpha=.2)
    fig.suptitle('Offline texture-space interpolation · 8-bit channel domain · 2,048 fixed locations · mip 0')
    figure(fig,'interpolation-loss');write_csv('interpolation-loss.csv',probes)

    inventory=[]
    bindings=root/'unity-showcase/Assets/NtcBindings'
    for asset in sorted(bindings.glob('*.asset')):
        text=asset.read_text();relative=re.search(r'^  streamingPath: (.+)$',text,re.M).group(1)
        path=root/'unity-showcase/Assets/StreamingAssets'/relative
        doc,raw,_=container(path)
        material_guid=re.search(r'editorSourceMaterial:.*?guid: (\w+)',text).group(1)
        material_name=''
        for meta in (root/'unity-showcase/Assets/ClassicSponza/Art/Sponza/Materials').glob('*.mat.meta'):
            if 'guid: '+material_guid in meta.read_text():material_name=meta.name[:-9];break
        unsupported=re.search(r'^  directUnsupportedReason: ?(.*)$',text,re.M).group(1)
        inventory.append(dict(material=material_name,stableId=asset.stem,fileBytes=len(raw),sha256=hashlib.sha256(raw).hexdigest(),width=doc['width'],height=doc['height'],mips=doc['numColorMips'],textures=doc['textures'],latentShape=doc['latentShape'],latentLevels=doc['latents'],mlp=doc['mlpVersions'][0],directUnsupportedReason=unsupported,samplerYaml=text.split('  samplers:\n',1)[-1]))
    write_json('material-inventory.json',inventory)
    parameters=dict(bakeToolDefaults=dict(bitsPerPixel=16,trainingSteps=100000,metalnessLossScale=24,smoothnessLossScale=16,generateMips=True,gdeflate='off'),sdkDefaults=dict(networkLearningRate=.005,gridLearningRate=.1,kPixelsPerBatch=64,randomSeed=0,stableTraining=False),historicalRuns=[dict(asset=label,steps=100000,loggedFinalPsnrDb=next(r['psnr_db'] for r in reversed(training) if r['asset']==label),learningRates='not recorded',seed='not recorded',lossWeights='not recorded') for label in ['SponzaBricks','MetalPlates013']],sources=['unity-plugin/Editor/NtcBakeWindow.cs','nvidia-backend/libraries/RTXNTC-Library/include/libntc/ntc.h','nvidia-backend/compress_sponza.log','nvidia-backend/compress_metalplates.log'],interpolation=dict(seed=20261127,samples=2048,phases=65,mip=0,domain='UNORM8 stored RGB / 255',reference='Original Color, bilinear; resized to NTC dimensions as documented by review data'))
    write_json('training-parameters.json',parameters)
    shutil.copy2(root/'build/psnr-real.csv',out/'historical-metalplates-quality.csv')
    with zipfile.ZipFile(out/'experiment-data.zip','w',zipfile.ZIP_DEFLATED) as archive:
        for path in list(out.glob('*.json'))+list(out.glob('*.csv'))+list((out/'figures').glob('*')):
            archive.write(path,path.relative_to(out))
    # Only public experiment data are exported; private notes and raw machine logs stay local.
    print(f'Exported {len(inventory)} binding records, {len(training)} training checkpoints, {len(probes)} interpolation measurements to {out}',flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--review',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    main(parser.parse_args())
