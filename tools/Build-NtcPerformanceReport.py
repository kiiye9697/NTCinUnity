"""Summarize independent Player trials; publish only allowlisted measurements, not local logs."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser()
parser.add_argument('--baseline', type=Path, required=True)
parser.add_argument('--optimized', type=Path)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
configs = [('Original', args.baseline, 'original'), ('Direct / retain originals', args.baseline, 'direct-retain'),
           ('Direct / release originals', args.baseline, 'direct-release')]
if args.optimized:
    configs.append(('Direct / deferred container + release', args.optimized, 'direct-release'))
records, raw, summaries = [], [], []
context = None
for label, directory, prefix in configs:
    reports = sorted(directory.glob(prefix + '-*/benchmark.json'))
    if len(reports) != 3:
        raise ValueError(f'{label}: expected 3 completed independent trials, found {len(reports)}')
    intervals = []
    for trial, path in enumerate(reports, 1):
        r = json.loads(path.read_text(encoding='utf-8-sig'))
        if len(r['modes']) != 1:
            raise ValueError(f'Use one measured mode per independent Player process: {path}')
        m = r['modes'][0]
        expected_mode = 'Original' if prefix == 'original' else 'InferenceOnSampleDirect'
        if m['mode'] != expected_mode:
            raise ValueError(f'Configuration does not match the recorded mode: {path}')
        current = {k: r[k] for k in ['unityVersion', 'graphicsDeviceName', 'graphicsDeviceType', 'width', 'height', 'cameraPosition', 'cameraRotationEuler']}
        current.update(warmupFrames=m['warmupFrames'], sampleFrames=m['sampleFrames'])
        if context is None:
            context = current
        if current != context or not r['framePerformanceValid']:
            raise ValueError(f'Inconsistent context or invalid frames: {path}')
        if r['renderDocCaptureAttempted']:
            raise ValueError(f'Captured run must not enter standalone timing results: {path}')
        if not m['completed'] or not m['gpuFencePassed'] or m['afterSamples']['failedGroups']:
            raise ValueError(f'Incomplete mode: {path}')
        p = m['afterSamples']['processMemory']
        if not p['gpuAvailable'] or not p['cpuAvailable']:
            raise ValueError(f'Process memory unavailable: {path}')
        record = dict(configuration=label, trial=trial, samples=m['sampleFrames'], p50_ms=m['frameIntervalMs']['p50'],
            p95_ms=m['frameIntervalMs']['p95'], private_bytes=p['privateBytes'], working_set_bytes=p['workingSetBytes'],
            gpu_local_bytes=p['localUsageBytes'], gpu_nonlocal_bytes=p['nonLocalUsageBytes'],
            texture_estimate_bytes=m['afterSamples']['memory']['estimatedTextureAndNtcBufferBytes'],
            direct_groups=m['afterSamples']['directMaterialGroups'], fallback_groups=m['afterSamples']['originalFallbackGroups'],
            originals_released=m['afterSamples']['originalsReleased'],
            report_sha256=hashlib.sha256(path.read_bytes()).hexdigest())
        records.append(record)
        with path.with_name('frames.csv').open(encoding='utf-8-sig', newline='') as f:
            frame_rows = list(csv.DictReader(f))
            if [int(row['sample']) for row in frame_rows] != list(range(m['sampleFrames'])):
                raise ValueError(f'Missing or duplicated frame samples: {path}')
            for row in frame_rows:
                value = float(row['frame_interval_ms'])
                if row['mode'] != m['mode'] or not np.isfinite(value) or value <= 0:
                    raise ValueError(f'Invalid frame sample: {path}')
                intervals.append(value)
                raw.append(dict(configuration=label, trial=trial, sample=int(row['sample']), frame_interval_ms=value))
    selected = records[-3:]
    summaries.append(dict(configuration=label, trials=3, sample_count=len(intervals),
        p50_ms=float(np.percentile(intervals,50)), p95_ms=float(np.percentile(intervals,95)),
        p99_ms=float(np.percentile(intervals,99)),
        gpu_local_mib=float(np.median([r['gpu_local_bytes'] for r in selected])) / 2**20,
        private_mib=float(np.median([r['private_bytes'] for r in selected])) / 2**20,
        working_set_mib=float(np.median([r['working_set_bytes'] for r in selected])) / 2**20))
for name, rows in [('performance-trials.csv', records), ('performance-frames.csv', raw), ('performance-summary.csv', summaries)]:
    with (args.output / name).open('w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0])); w.writeheader(); w.writerows(rows)
(args.output / 'performance-method.json').write_text(json.dumps(dict(context=context,
    frame_metric='Stopwatch interval between consecutive Player samples; includes scheduling and GPU backpressure; not GPU timestamp duration.',
    gpu_memory_metric='In-process DXGI local CurrentUsage, includes driver allocations and render targets.',
    cpu_memory_metric='PROCESS_MEMORY_COUNTERS_EX PrivateUsage and WorkingSetSize.',
    warmup_frames=context['warmupFrames'], samples_per_trial=context['sampleFrames'], capture_in_timing_runs=False,
    summary=summaries), ensure_ascii=False, indent=2), encoding='utf-8')
fig, axes = plt.subplots(1,3,figsize=(13,4.7))
labels=['Original', 'Direct\nretain', 'Direct\nrelease'] + (['Direct\ndeferred+release'] if args.optimized else [])
x=np.arange(len(summaries)); colors=['#7c8893','#ac956f','#4e8d99','#3c7077'][:len(x)]
for axis,key,title,unit in zip(axes,['p50_ms','gpu_local_mib','private_mib'],
    ['Frame interval (p50 / p95)','Process local video memory','Process private memory'], ['ms','MiB','MiB']):
    values=[r[key] for r in summaries]
    axis.bar(x,values,color=colors,width=.62)
    if key=='p50_ms':
        axis.scatter(x,[r['p95_ms'] for r in summaries],color='#333333',marker='_',s=120,label='p95')
        axis.legend(frameon=False)
    for i,value in enumerate(values): axis.annotate(f'{value:.1f}',(i,value),xytext=(0,4),textcoords='offset points',ha='center',fontsize=9)
    axis.set_xticks(x,labels,fontsize=8);axis.set_ylabel(unit);axis.set_title(title,fontsize=11)
    axis.spines[['top','right']].set_visible(False);axis.set_ylim(0,max(values)*1.25)
fig.suptitle(f"Unity Player · {context['width']}×{context['height']} · {context['graphicsDeviceName']} · 3 trials",fontsize=12)
fig.tight_layout()
for ext in ['svg','png']: fig.savefig(args.output/f'performance-comparison.{ext}',dpi=150)
print(json.dumps(summaries,ensure_ascii=False,indent=2))
