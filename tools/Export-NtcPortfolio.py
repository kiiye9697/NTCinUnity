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


def archive_data(out):
    paths = [p for p in out.iterdir() if p.suffix in ['.json', '.csv', '.svg', '.png']]
    for folder in ['figures', 'performance']:
        paths += [p for p in (out/folder).rglob('*') if p.suffix in ['.json', '.csv', '.svg', '.png']]
    with zipfile.ZipFile(out/'experiment-data.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(paths):
            entry = zipfile.ZipInfo(path.relative_to(out).as_posix(), date_time=(1980,1,1,0,0,0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, path.read_bytes())


def training_figure(out):
    with (out/'training-curves.csv').open(encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f))
    fig, axes = plt.subplots(1, 2, figsize=(12, 4))
    for label in dict.fromkeys(r['asset'] for r in rows):
        selected = [r for r in rows if r['asset'] == label]
        x = [int(r['step']) for r in selected]
        axes[0].plot(x, [float(r['psnr_db']) for r in selected], label='NTC / '+label)
        axes[1].semilogy(x, [float(r['normalized_mse_from_psnr']) for r in selected], label='NTC / '+label)
    axes[0].set_ylabel('Logged PSNR vs each Original (dB)')
    axes[1].set_ylabel('MSE reconstructed from rounded PSNR')
    for axis in axes:
        axis.set_xlabel('Training step'); axis.legend(); axis.grid(alpha=.2)
    fig.suptitle('Historical NTC training: SponzaBricks / MetalPlates013')
    fig.text(.5, -.025, 'Original / no NTC is the reference: MSE = 0, PSNR = +inf. It has no training steps.', ha='center', fontsize=9)
    for ext in ['svg', 'png']:
        fig.savefig(out/f'training-curves.{ext}', dpi=140, bbox_inches='tight', facecolor='white', metadata={'Date':None} if ext == 'svg' else None)
    plt.close(fig)


def comparison_curves(out, report):
    inventory = json.loads((out/'material-inventory.json').read_text(encoding='utf-8'))
    probes, correspondence, channel_rows = [], [], []
    fig, axes = plt.subplots(1, len(report['materials']), figsize=(15, 4), squeeze=False)
    fig_quality, quality_axes = plt.subplots(1, len(report['materials']), figsize=(15, 4), squeeze=False)
    def save(fig, path):
        path.parent.mkdir(exist_ok=True)
        for ext in ['svg', 'png']:
            fig.savefig(path.with_suffix('.'+ext), dpi=140, bbox_inches='tight', facecolor='white', metadata={'Date':None} if ext == 'svg' else None)
    for index, material in enumerate(report['materials']):
        name = material['name']; entry = next(m for m in inventory if m['material'] == name)
        recorded_hash = material.get('ntcSha256')
        if recorded_hash is None:
            legacy = Path(__file__).resolve().parent.parent/'unity-showcase/Assets/NtcPlugin/NtcData'/f'{name}.ntc'
            recorded_hash = hashlib.sha256(legacy.read_bytes()).hexdigest()
        if recorded_hash != entry['sha256']:
            raise ValueError('Review model differs from the current scene binding: '+name)
        material['ntcSha256'] = recorded_hash
        material['stableId'] = entry['stableId']
        channel = next(c for c in material['channels'] if c['name'] == 'Color')
        source = np.asarray(Image.open(out/channel['input']), dtype=np.float64)/255
        decoded = np.asarray(Image.open(out/channel['output']), dtype=np.float64)/255
        if source.shape != decoded.shape:
            raise ValueError('Comparison images must have matching dimensions: '+name)
        rng = np.random.default_rng(20261127); h, w = source.shape[:2]
        x = rng.integers(0, w-1, 2048); y = rng.integers(0, h-1, 2048); fy = .37
        rows = []
        for fx in np.linspace(0, 1, 65):
            def bilinear(a):
                return ((1-fx)*(1-fy)*a[y,x] + fx*(1-fy)*a[y,x+1]
                    + (1-fx)*fy*a[y+1,x] + fx*fy*a[y+1,x+1])
            reference = bilinear(source); original_point = source[y,x+(fx >= .5)]
            ntc_point = decoded[y,x+(fx >= .5)]; ntc_bilinear = bilinear(decoded)
            row = dict(asset=name, channel='Color', mip=0, fraction_x=float(fx), fraction_y=fy,
                original_point_mse=float(np.mean((original_point-reference)**2)), original_bilinear_mse=0.0,
                point_mse=float(np.mean((ntc_point-reference)**2)), bilinear_mse=float(np.mean((ntc_bilinear-reference)**2)),
                matched_point_mse=float(np.mean((ntc_point-original_point)**2)), samples=2048)
            rows.append(row); probes.append(row)
        def plot(axis):
            for field, label, color, style in [
                ('original_point_mse','Original / no NTC / Point','#ad734c','--'),
                ('point_mse','NTC / Point','#ad734c','-'),
                ('bilinear_mse','NTC / Bilinear','#367f8b','-'),
                ('original_bilinear_mse','Original / no NTC / Bilinear','#737373',':')]:
                axis.plot([r['fraction_x'] for r in rows], [r[field] for r in rows], label=label, color=color, linestyle=style)
            axis.set_title(name+' / Color / mip 0'); axis.set_xlabel('Sub-texel x position')
            axis.set_ylabel('MSE vs Original Bilinear'); axis.legend(fontsize=8); axis.grid(alpha=.2)
        plot(axes[0,index])
        single, pair = plt.subplots(1, 2, figsize=(12, 4))
        plot(pair[0])
        for field, label, color in [('matched_point_mse','NTC Point vs Original Point','#ad734c'),
                                   ('bilinear_mse','NTC Bilinear vs Original Bilinear','#367f8b')]:
            pair[1].plot([r['fraction_x'] for r in rows], [r[field] for r in rows], label=label, color=color)
        pair[1].axhline(0, color='#737373', linestyle=':', label='Original vs itself = 0')
        pair[1].set_title('Matched-filter reconstruction error'); pair[1].set_ylabel('MSE')
        pair[1].set_xlabel('Sub-texel x position'); pair[1].legend(fontsize=8); pair[1].grid(alpha=.2)
        single.suptitle(name+' / source textures: Original (no NTC) and NTC / same sample positions')
        save(single, out/'figures'/f'{name}-sampling-comparison'); plt.close(single)
        errors = []
        for c in material['channels']:
            a = np.asarray(Image.open(out/c['input']), dtype=np.float64)/255
            b = np.asarray(Image.open(out/c['output']), dtype=np.float64)/255
            mse = float(np.mean((a-b)**2)); errors.append(mse)
            channel_rows.append(dict(asset=name, channel=c['name'], original_mse=0.0, ntc_mse=mse,
                reference=c['input'], reconstructed=c['output']))
        qa = quality_axes[0,index]; ticks = np.arange(len(errors))
        qa.bar(ticks, errors, color='#367f8b', label='NTC reconstruction')
        qa.axhline(0, color='#737373', linestyle=':', label='Original / no NTC = 0')
        qa.set_xticks(ticks, [c['name'] for c in material['channels']], rotation=25)
        qa.set_title(name+' / mip 0'); qa.set_ylabel('MSE vs same Original channel'); qa.legend(fontsize=8)
        correspondence.append(dict(id=f'{name}/Color/mip0', material=name, channel='Color', mip=0,
            stableId=entry['stableId'], ntcSha256=entry['sha256'], sourceFile=channel['source'], sourceSize=channel['sourceSize'],
            comparisonSize=channel['outputSize'], original=channel['input'], ntc=channel['output'],
            originalPreview=channel['inputPreview'], ntcPreview=channel['outputPreview'],
            curve=f'figures/{name}-sampling-comparison.svg', seed=20261127, samples=2048, phases=65,
            domain='UNORM8 stored RGB / 255', originalReferenceMse=0,
            referenceKind='Source image without NTC, resized to model dimensions; Unity imported textures are the separate scene A/B reference.',
            originalPixelSha256=hashlib.sha256(np.asarray(Image.open(out/channel['input'])).tobytes()).hexdigest(),
            ntcPixelSha256=hashlib.sha256(np.asarray(Image.open(out/channel['output'])).tobytes()).hexdigest()))
    fig.suptitle('Original source (no NTC) and NTC sampling: same positions, mip 0, stored RGB domain')
    save(fig, out/'interpolation-loss'); plt.close(fig)
    fig_quality.suptitle('Channel reconstruction against Original source (no NTC); Original self-error = 0')
    fig_quality.tight_layout(); save(fig_quality, out/'figures/channel-mse'); plt.close(fig_quality)
    for file, records in [('interpolation-loss.csv', probes), ('channel-mse.csv', channel_rows)]:
        with (out/file).open('w', newline='', encoding='utf-8') as f:
            writer = csv.DictWriter(f, fieldnames=list(records[0])); writer.writeheader(); writer.writerows(records)
    (out/'sampling-correspondence.json').write_text(json.dumps(correspondence, ensure_ascii=False, indent=2), encoding='utf-8')
    return probes


def main(args):
    root=Path(__file__).resolve().parent.parent
    plt.rcParams.update({'font.size':10, 'svg.fonttype':'none', 'svg.hashsalt':'ntc-source-comparisons-v1'})
    out=args.output.resolve();out.mkdir(parents=True,exist_ok=True)
    if args.refresh_comparisons:
        report = json.loads((out/'review-data.json').read_text(encoding='utf-8'))
        comparison_curves(out, report); training_figure(out)
        (out/'review-data.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        params = json.loads((out/'training-parameters.json').read_text(encoding='utf-8'))
        params['interpolation'].update(comparisonManifest='sampling-correspondence.json',
            reference='Original source Color / Bilinear, resized to NTC dimensions',
            baselines=['Original source / Point', 'Original source / Bilinear'],
            matchedFilters=['NTC Point vs Original Point', 'NTC Bilinear vs Original Bilinear'])
        (out/'training-parameters.json').write_text(json.dumps(params, ensure_ascii=False, indent=2), encoding='utf-8')
        archive_data(out)
        print('Refreshed matched Original / no NTC comparisons for '+str(len(report['materials']))+' materials.')
        return
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
    training_figure(out)

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
    probes = comparison_curves(out, report)
    parameters=dict(bakeToolDefaults=dict(bitsPerPixel=16,trainingSteps=100000,metalnessLossScale=24,smoothnessLossScale=16,generateMips=True,gdeflate='off'),sdkDefaults=dict(networkLearningRate=.005,gridLearningRate=.1,kPixelsPerBatch=64,randomSeed=0,stableTraining=False),historicalRuns=[dict(asset=label,steps=100000,loggedFinalPsnrDb=next(r['psnr_db'] for r in reversed(training) if r['asset']==label),learningRates='not recorded',seed='not recorded',lossWeights='not recorded') for label in ['SponzaBricks','MetalPlates013']],sources=['unity-plugin/Editor/NtcBakeWindow.cs','nvidia-backend/libraries/RTXNTC-Library/include/libntc/ntc.h','nvidia-backend/compress_sponza.log','nvidia-backend/compress_metalplates.log'],interpolation=dict(seed=20261127,samples=2048,phases=65,mip=0,domain='UNORM8 stored RGB / 255',reference='Original Color, bilinear; resized to NTC dimensions as documented by review data'))
    parameters['interpolation'].update(comparisonManifest='sampling-correspondence.json', reference='Original source Color / Bilinear, resized to NTC dimensions', baselines=['Original source / Point', 'Original source / Bilinear'], matchedFilters=['NTC Point vs Original Point', 'NTC Bilinear vs Original Bilinear'])
    write_json('training-parameters.json',parameters)
    shutil.copy2(root/'build/psnr-real.csv',out/'historical-metalplates-quality.csv')
    archive_data(out)
    # Only public experiment data are exported; private notes and raw machine logs stay local.
    print(f'Exported {len(inventory)} binding records, {len(training)} training checkpoints, {len(probes)} interpolation measurements to {out}',flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--review',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--refresh-comparisons', action='store_true', help='Refresh plots and mapping from existing paired exports; no GPU work.')
    options = parser.parse_args()
    if not options.refresh_comparisons and options.review is None: parser.error('--review is required for a full export')
    main(options)
