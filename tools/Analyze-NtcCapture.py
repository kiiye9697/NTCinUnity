"""Run with qrenderdoc --python Analyze-NtcCapture.py. Set NTC_CAPTURE_DIR first.

Replay timings diagnose individual draw/dispatch costs; they are not Player frame times.
"""
import csv
import json
import os
from pathlib import Path
import traceback
import renderdoc as rd

root = Path(os.environ['NTC_CAPTURE_DIR'])
def progress(message):
    with (root / 'analysis-progress.txt').open('a', encoding='utf-8') as output:
        output.write(message + '\n')
progress('script started')
try:
    for path in sorted(root.glob('*.rdc')):
        capture = rd.OpenCaptureFile()
        controller = None
        try:
            progress('opening ' + str(path))
            status = capture.OpenFile(str(path), '', None)
            if status != rd.ResultCode.Succeeded:
                raise RuntimeError(str(status))
            progress('creating replay device')
            status, controller = capture.OpenCapture(rd.ReplayOptions(), None)
            if status != rd.ResultCode.Succeeded:
                raise RuntimeError(str(status))
            actions = {}
            def visit(nodes, parents):
                for action in nodes:
                    label = action.GetName(controller.GetStructuredFile())
                    actions[action.eventId] = (action, ' / '.join(parents + [label]))
                    visit(action.children, parents + [label])
            visit(controller.GetRootActions(), [])
            action_rows = [dict(event=e, action=label, flags=str(a.flags)) for e, (a, label) in actions.items()]
            path.with_suffix('.actions.json').write_text(json.dumps(action_rows, indent=2), encoding='utf-8')
            progress('fetching event GPU durations')
            supported = rd.GPUCounter.EventGPUDuration in controller.EnumerateCounters()
            samples = controller.FetchCounters([rd.GPUCounter.EventGPUDuration]) if supported else []
            progress('writing results')
            rows = []
            for sample in samples:
                action, label = actions.get(sample.eventId, (None, ''))
                if action is None:
                    continue
                rows.append(dict(event=sample.eventId, milliseconds=sample.value.d * 1000, action=label))
            rows.sort(key=lambda r: r['milliseconds'], reverse=True)
            with path.with_suffix('.gpu-events.csv').open('w', newline='', encoding='utf-8') as output:
                writer = csv.DictWriter(output, fieldnames=['event', 'milliseconds', 'action'])
                writer.writeheader(); writer.writerows(rows)
            texture_rows = [dict(id=str(t.resourceId), width=t.width, height=t.height, mips=t.mips,
                layers=t.arraysize, format=t.format.Name(), bytes=t.byteSize) for t in controller.GetTextures()]
            path.with_suffix('.textures.json').write_text(json.dumps(texture_rows, indent=2), encoding='utf-8')
            shader_events = rows or [r for r in action_rows if actions[r['event']][0].flags & (rd.ActionFlags.Drawcall | rd.ActionFlags.Dispatch)]
            written_shaders = set()
            for row in shader_events:
                controller.SetFrameEvent(row['event'], True)
                pipeline = controller.GetPipelineState()
                stage = rd.ShaderStage.Compute if actions[row['event']][0].flags & rd.ActionFlags.Dispatch else rd.ShaderStage.Pixel
                reflection = pipeline.GetShaderReflection(stage)
                shader_id = str(pipeline.GetShader(stage))
                if reflection and shader_id not in written_shaders:
                    text = controller.DisassembleShader(pipeline.GetGraphicsPipelineObject(), reflection, '')
                    path.with_suffix('.event-%d.shader.txt' % row['event']).write_text(text, encoding='utf-8')
                    written_shaders.add(shader_id)
                    if len(written_shaders) >= 8:
                        break
            path.with_suffix('.summary.json').write_text(json.dumps(dict(
                capture=path.name, actionCount=len(actions), counterResultCount=len(samples), gpuDurationsAvailable=bool(rows),
                gpuDurationCounterSupported=supported, shaderReflectionCount=len(written_shaders),
                drawCount=sum(bool(a.flags & rd.ActionFlags.Drawcall) for a, _ in actions.values()),
                dispatchCount=sum(bool(a.flags & rd.ActionFlags.Dispatch) for a, _ in actions.values()),
                replayEventTotalMs=sum(r['milliseconds'] for r in rows) if rows else None,
                note='RenderDoc replay event durations; instrumentation and replay differ from standalone Player.',
                topEvents=rows[:20]), indent=2), encoding='utf-8')
        finally:
            if controller:
                controller.Shutdown()
            capture.Shutdown()
except Exception:
    (root / 'analysis-error.txt').write_text(traceback.format_exc(), encoding='utf-8')
raise SystemExit
