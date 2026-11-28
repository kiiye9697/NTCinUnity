"""Build an offline review page from real scene captures and NTC containers.

Requires Pillow, numpy and matplotlib. Art stays in the selected local output directory.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import shutil
import struct
import subprocess

import numpy as np
from PIL import Image


def export_figures(output, materials):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    from matplotlib.patches import FancyBboxPatch
    folder = output / 'figures'
    folder.mkdir(exist_ok=True)
    plt.rcParams.update({'font.size': 10, 'svg.fonttype': 'none'})
    def save(figure, name):
        for suffix in ['png', 'svg']:
            figure.savefig(folder / (name+'.'+suffix), dpi=160, bbox_inches='tight', facecolor='white')
        plt.close(figure)
    positions = np.arange(len(materials))
    for name, fields, labels, title in [
        ('compressed-size', ['rawBytes', 'ntcBytes'], ['UNORM8 channels + mips', '.ntc file'], 'Logical channel payload vs complete NTC file'),
        ('latent-layout', ['latentExpandedBytes', 'latentPackedBytes'], ['RGBA32 expanded features', 'BGRA4 packed features'], 'Latent format capacity (same quantized features)')]:
        figure, axis = plt.subplots(figsize=(9, 4))
        for i, field in enumerate(fields):
            values = [m[field]/1048576 for m in materials]
            bars = axis.bar(positions+(i-.5)*.35, values, .35, label=labels[i], color=['#405a6c', '#a5b9c5'][i])
            axis.bar_label(bars, fmt='%.2f', padding=3)
        axis.set_xticks(positions, [m['name'] for m in materials])
        axis.set_ylabel('MiB'); axis.set_title(title); axis.legend(); axis.set_ylim(0, axis.get_ylim()[1]*1.2)
        save(figure, name)
    figure, axis = plt.subplots(figsize=(10, 4))
    channel_names = [c['name'] for c in materials[0]['channels']]
    for i, m in enumerate(materials):
        values = [next((c['psnr'] for c in m['channels'] if c['name']==name), None) for name in channel_names]
        bars = axis.bar(np.arange(len(values))+(i-1)*.24, [v if v is not None else 0 for v in values], .24, label=m['name'])
        axis.bar_label(bars, labels=[f'{v:.1f}' if v is not None else 'exact / n/a' for v in values], padding=3, fontsize=8)
    axis.set_xticks(np.arange(len(channel_names)), channel_names); axis.set_ylabel('PSNR (dB)'); axis.set_title('8-bit channel reconstruction; reference resized to NTC dimensions')
    axis.legend(); axis.set_ylim(0, axis.get_ylim()[1]*1.25); save(figure, 'channel-quality')
    for m in materials:
        figure, axes = plt.subplots(1, 5, figsize=(15, 3.1))
        images = [m['channels'][0]['input'], None, m['latentImages'][0], m['channels'][0]['output'], 'media/ntc-direct.png']
        titles = ['PBR inputs', '.ntc container', 'Features + INT8 weights', 'UV / LOD + MLP → PBR', 'Unity URP Lit']
        for i, (axis, path, title) in enumerate(zip(axes, images, titles)):
            axis.set_axis_off(); axis.set_title(title, fontsize=11)
            if path: axis.imshow(Image.open(output/path))
            else:
                axis.add_patch(FancyBboxPatch((.23,.16),.54,.66,boxstyle='round,pad=0.03',fill=False,transform=axis.transAxes))
                axis.text(.5,.55,'.ntc',ha='center',transform=axis.transAxes,fontsize=18)
                axis.text(.5,.36,f"{m['ntcBytes']/1048576:.2f} MiB",ha='center',transform=axis.transAxes)
            if i==2:
                inset = axis.inset_axes([.58,.02,.4,.3])
                inset.imshow(Image.open(output/m['weights']), cmap='gray')
                inset.set_axis_off()
            if i<4: axis.annotate('→',xy=(1.08,.5),xycoords='axes fraction',annotation_clip=False,fontsize=20)
        figure.suptitle(m['name']+' — actual inputs, latent data, weights and scene output', y=1.02)
        save(figure, m['name']+'-pipeline')


def container(path):
    data = path.read_bytes()
    magic, version, offset, size, binary, length = struct.unpack_from('<4sIQQQQ', data)
    if magic != b'NTEX' or version != 0x100 or binary + length > len(data):
        raise ValueError(f'Invalid container: {path}')
    return json.loads(data[offset:offset+size].rstrip(b'\0 \r\n')), data, binary


def source_image(path, channel):
    image = Image.open(path)
    # Photoshop TIFFs can mark their fourth sample as unspecified instead of alpha.
    if channel == 'A' and 'A' not in image.getbands() and image.mode == 'RGB' and image.tile and image.tile[0][3][0] in ('RGBX', 'RGBXX'):
        image._mode = 'RGBA'
        image.tile = [(t[0], t[1], t[2], ('RGBA', t[3][1], t[3][2])) for t in image.tile]
    if channel == 'RGB':
        return image.convert('RGB')
    if image.mode == 'L' and channel in 'RGB':
        return image.convert('RGB')
    if channel not in image.getbands():
        raise ValueError(f'{path} has no {channel} sample')
    return image.getchannel(channel).convert('RGB')


def material_binding(assets, name):
    materials = list((assets/'ClassicSponza/Art').rglob(name+'.mat'))
    if len(materials) != 1:
        raise ValueError('Expected one source material named '+name)
    material = materials[0]
    guid = re.search(r'^guid: (\w+)', material.with_suffix('.mat.meta').read_text(encoding='utf-8'), re.M).group(1)
    matches = []
    for binding in (assets/'NtcBindings').glob('*.asset'):
        text = binding.read_text(encoding='utf-8')
        source = re.search(r'editorSourceMaterial:.*?guid: (\w+)', text)
        if source and source.group(1) == guid:
            relative = re.search(r'^  streamingPath: (.+)$', text, re.M).group(1).strip('"')
            matches.append((assets/'StreamingAssets'/relative, binding.stem))
    if len(matches) != 1:
        raise ValueError('Expected one GUID texture-set binding for '+name+'; prepare the scene bindings first.')
    return material, matches[0][0], matches[0][1]


def build(args):
    assets = args.project.resolve() / 'Assets'
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    media = output / 'media'
    media.mkdir(exist_ok=True)
    for name in ['Original.mp4', 'NTC-Direct.mp4', 'original.png', 'ntc-direct.png']:
        shutil.copy2(args.recording / name, media / name)

    guid_paths = {}
    for path in (assets / 'ClassicSponza/Art').rglob('*.meta'):
        match = re.search(r'^guid: (\w+)', path.read_text(errors='replace'), re.M)
        if match:
            guid_paths[match[1]] = path.with_suffix('')
    names = args.materials.split(',')
    materials = []
    for name in names:
        material_path, ntc, stable_id = material_binding(assets, name)
        doc, data, binary = container(ntc)
        folder = output / name
        folder.mkdir(exist_ok=True)
        decoded = folder / 'decoded'
        decoded.mkdir(exist_ok=True)
        result = subprocess.run([str(args.cli.resolve()), str(ntc), '-D', '-i', str(decoded), '--bcFormat', 'none', '--imageFormat', 'PNG', '--no-coopVec'], capture_output=True, text=True)
        (folder / 'decode.log').write_text(result.stdout + result.stderr, encoding='utf-8')
        if result.returncode:
            raise RuntimeError(f'Decode failed: {name}. See {folder / "decode.log"}')
        text = material_path.read_text(encoding='utf-8')
        properties = dict(re.findall(r'- (_\w+):\s*\n\s*m_Texture: \{fileID: \d+, guid: (\w+), type: \d+\}', text))
        conventions = [('Color', '_BaseMap', 'RGB'), ('Normal', '_BumpMap', 'RGB'), ('AO', '_OcclusionMap', 'G'), ('Metalness', '_MetallicGlossMap', 'R'), ('Smoothness', '_MetallicGlossMap', 'A')]
        channels = []
        source_files = set()
        for channel, prop, swizzle in conventions:
            if prop not in properties or not (decoded / (channel + '.png')).is_file():
                continue
            path = guid_paths[properties[prop]]
            original = source_image(path, swizzle)
            source_files.add(path)
            reconstructed = Image.open(decoded / (channel + '.png')).convert('RGB')
            source_size = original.size
            reference = original.resize(reconstructed.size, Image.Resampling.BILINEAR)
            delta = np.abs(np.asarray(reference, dtype=np.float32) - np.asarray(reconstructed, dtype=np.float32))
            mse = float(np.mean(delta ** 2))
            psnr = 10 * math.log10(255**2 / mse) if mse > 0 else None
            # Keep full size PNGs for inspection and an explicit 8x difference image.
            reference.save(folder / (channel + '-input.png'))
            reconstructed.save(folder / (channel + '-output.png'))
            Image.fromarray(np.minimum(delta * 8, 255).astype(np.uint8)).save(folder / (channel + '-diff.png'))
            channels.append(dict(name=channel, psnr=psnr, source=path.name, sourceSize=source_size, outputSize=reconstructed.size, input=f'{name}/{channel}-input.png', output=f'{name}/{channel}-output.png', diff=f'{name}/{channel}-diff.png'))

        def view_bytes(index):
            v = doc['views'][index]
            if v.get('compression', 'None').lower() != 'none':
                raise ValueError('Latent visualization requires uncompressed buffer views')
            return data[binary+v['offset']:binary+v['offset']+v['storedSize']]

        first = doc['latents'][0]
        latent_images = []
        for i, index in enumerate(first['layerViews']):
            values = np.frombuffer(view_bytes(index), dtype='<u2').reshape(first['height'], first['width'])
            rgb = np.stack([(values >> shift & 15) * 17 for shift in (0, 4, 8)], axis=-1).astype(np.uint8)
            Image.fromarray(rgb).save(folder / f'latent-{i}.png')
            latent_images.append(f'{name}/latent-{i}.png')
        layer = doc['mlpVersions'][0]['layers'][0]
        weights = np.frombuffer(view_bytes(layer['weightView']), dtype=np.int8).reshape(layer['outputChannels'], layer['inputChannels'])
        Image.fromarray((weights.astype(np.int16)+128).astype(np.uint8)).resize((384, 256), Image.Resampling.NEAREST).save(folder / 'weights.png')
        latent_texels = sum(x['width']*x['height']*len(x['layerViews']) for x in doc['latents'])
        # UNORM8 channel payload at the container resolution, including declared mips.
        logical_channels = sum(t['numChannels'] for t in doc['textures'])
        raw = sum(max(1, doc['width'] >> i)*max(1, doc['height'] >> i)*logical_channels for i in range(doc.get('numColorMips', 1)))
        materials.append(dict(name=name, stableId=stable_id, ntcSha256=hashlib.sha256(data).hexdigest(), width=doc['width'], height=doc['height'], channels=channels, ntcBytes=len(data), rawBytes=raw, sourceFileBytes=sum(p.stat().st_size for p in source_files), latentPackedBytes=latent_texels*2, latentExpandedBytes=latent_texels*4, latentImages=latent_images, weights=f'{name}/weights.png', layers=[f"{x['inputChannels']} → {x['outputChannels']}" for x in doc['mlpVersions'][0]['layers']]))
        print(f'{name}: {len(channels)} channels, {len(data)} bytes', flush=True)
    frame_match = re.search(r'(\d+) frames each', (args.recording / 'recording.txt').read_text())
    if not frame_match:
        raise ValueError('recording.txt must identify the number of frames in each pass')
    report = dict(frames=int(frame_match[1]), fps=30, materials=materials, source='Current .ntc decoded with the NVIDIA CLI; original channels extracted from material GUID references.', quality='PSNR in 8-bit stored channel space. Inputs bilinearly resized only when dimensions differ. Difference preview is absolute error ×8.', size='Raw = UNORM8 logical channels at NTC resolution with all declared mips. NTC = full container file. These are payload/file sizes, not VRAM or source-image file compression ratios.')
    (output / 'review-data.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    export_figures(output, materials)
    template = Path(__file__).with_name('ntc-review.html').read_text(encoding='utf-8')
    payload = json.dumps(report, ensure_ascii=False).replace('<', '\\u003c')
    (output / 'index.html').write_text(template.replace('__REVIEW_DATA__', payload), encoding='utf-8')
    shutil.copy2(Path(__file__).with_name('Serve-NtcReview.py'), output / 'serve.py')
    (output / 'Open-Review.cmd').write_text('@echo off\r\nstart "" "%~dp0index.html"\r\n', encoding='ascii')
    print(f'Review page: {output / "index.html"}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parent.parent
    parser.add_argument('--project', type=Path, default=root / 'unity-showcase')
    parser.add_argument('--cli', type=Path, default=root / 'nvidia-backend/bin/windows-x64/ntc-cli.exe')
    parser.add_argument('--recording', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--materials', default='Curtain_Red,Floor,Column_A')
    build(parser.parse_args())
