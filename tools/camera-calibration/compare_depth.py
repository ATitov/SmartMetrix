"""Offline comparison using the production .NET CPU stereo/reprojection code."""
import argparse
import base64
import hashlib
import html
import json
from pathlib import Path
import subprocess
import tempfile

import cv2 as cv
import numpy as np


def metrics(depth, confidence, roi, reference=None):
    valid = np.isfinite(depth) & (depth > 0) & (confidence > 0) & roi
    z = depth[valid].astype(float)
    result = {"validPixels": int(valid.sum()), "coveragePercent": 100 * float(valid.sum()) / int(roi.sum()),
              "medianDepthMetres": float(np.median(z)) if z.size else None,
              "meanConfidence": float(confidence[valid].mean()) if z.size else None}
    if reference is not None:
        error = z - reference
        result.update(biasMetres=float(error.mean()) if z.size else None,
                      maeMetres=float(np.abs(error).mean()) if z.size else None,
                      rmseMetres=float(np.sqrt(np.mean(error ** 2))) if z.size else None,
                      p95AbsoluteErrorMetres=float(np.percentile(np.abs(error), 95)) if z.size else None)
    return result


def compare(args):
    root = Path(args.calibration).resolve()
    draft_path = root / "calibration-draft.json"
    draft = json.loads(draft_path.read_text(encoding="utf-8"))
    rect = draft["rectification"]
    if rect["schemaVersion"] != 2:
        raise ValueError("Re-export calibration with schemaVersion=2 first.")
    width, height = rect["width"], rect["height"]
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("Use a new output directory to preserve previous comparisons.")
    if not 0 < args.near < args.far or args.max_disparity < 1:
        raise ValueError("Require 0 < near < far and max-disparity >= 1.")
    if args.reference_depth is not None and (not np.isfinite(args.reference_depth) or args.reference_depth <= 0 or args.roi is None):
        raise ValueError("A positive reference depth requires --roi x y width height on a fronto-parallel plane.")
    roi = np.ones((height, width), dtype=bool)
    if args.roi:
        x, y, w, h = args.roi
        if min(x, y) < 0 or min(w, h) <= 0 or x + w > width or y + h > height:
            raise ValueError("ROI must lie inside the original A frame.")
        roi[:] = False
        roi[y:y+h, x:x+w] = True
    frames, hashes, images = [], {}, {}
    for camera in "ABC":
        path = Path(getattr(args, camera.lower())).resolve()
        data = path.read_bytes()
        image = cv.imdecode(np.frombuffer(data, np.uint8), cv.IMREAD_UNCHANGED)
        if image is None or image.dtype != np.uint8:
            raise ValueError(f"{camera}: require an 8-bit image.")
        if image.ndim == 3:
            image = cv.cvtColor(image, cv.COLOR_BGR2GRAY if image.shape[2] == 3 else cv.COLOR_BGRA2GRAY)
        if image.shape != (height, width):
            raise ValueError(f"{camera}: dimensions must match calibration; resizing is not allowed.")
        images[camera] = image
        frames.append(dict(cameraId=camera, width=width, height=height,
                           pixels=base64.b64encode(image.tobytes()).decode("ascii")))
        hashes[camera] = dict(path=str(path), sha256=hashlib.sha256(data).hexdigest())
    identity = np.eye(3).ravel().tolist()
    # Force reference-A coordinates so Z always means optical depth in A, independent of rig pose.
    pairs = [dict(leftCameraId=p["leftCameraId"], rightCameraId=p["rightCameraId"],
                  baselineMetres=p["baselineMetres"], fx=p["fx"], fy=p["fy"], cx=p["cx"], cy=p["cy"],
                  rotation=identity, translation=[0, 0, 0], rectificationMapUri="unused") for p in rect["pairs"]]
    request = dict(frames=frames, calibration=dict(schemaVersion=1, calibrationId="offline-comparison",
                   cameraRigCoordinateSystemId="CameraA", pairs=pairs, rectification=rect))
    options = dict(nearDistanceMetres=args.near, farDistanceMetres=args.far, maximumDisparity=args.max_disparity)
    project = Path(__file__).resolve().parents[1] / "SmartMetrix.DepthComparison/SmartMetrix.DepthComparison.csproj"
    if not args.no_build:
        subprocess.run(["dotnet", "build", str(project), "-c", "Release"], check=True)
    dll = project.parent / "bin/Release/net10.0/SmartMetrix.DepthComparison.dll"
    if not dll.exists():
        raise ValueError("Build the comparison project in Release first.")
    output.mkdir(parents=True)
    with tempfile.TemporaryDirectory() as temp:
        request_path, options_path = Path(temp) / "request.json", Path(temp) / "options.json"
        request_path.write_text(json.dumps(request), encoding="utf-8")
        options_path.write_text(json.dumps(options), encoding="utf-8")
        subprocess.run(["dotnet", str(dll), str(request_path), str(options_path), str(root), str(output)], check=True)
    report = json.loads((output / "runs.json").read_text(encoding="utf-8"))
    report.update(inputs=hashes, calibrationSha256=hashlib.sha256(draft_path.read_bytes()).hexdigest(),
                  roi=args.roi, referenceDepthMetres=args.reference_depth, pixelGrid="CameraAOriginal")
    masks, cards = {}, []
    for run in report["runs"]:
        mode = run["mode"]
        raw = np.fromfile(output / f"{mode}.bin", dtype="<f4").reshape(height, width, 2)
        depth, confidence = raw[:, :, 0], raw[:, :, 1]
        valid = (confidence > 0) & np.isfinite(depth) & (depth > 0)
        masks[mode] = valid & roi
        run["metrics"] = metrics(depth, confidence, roi, args.reference_depth)
        np.savez_compressed(output / f"{mode}.npz", depthMetres=depth, confidence=confidence, valid=valid)
        scale = np.clip((depth - args.near) / (args.far - args.near), 0, 1)
        color = cv.applyColorMap((scale * 255).astype(np.uint8), cv.COLORMAP_TURBO)
        color[~valid] = 0
        cv.imwrite(str(output / f"{mode}.png"), color)
        cv.imwrite(str(output / f"{mode}-confidence.png"), (np.clip(confidence, 0, 1) * 255).astype(np.uint8))
        cells = "".join(f"<tr><td>{html.escape(k)}</td><td>{'—' if v is None else f'{v:.5g}'}</td></tr>" for k, v in run["metrics"].items())
        cards.append(f'<section><h2>{mode}</h2><p>{run["elapsedMilliseconds"]:.1f} ms</p><img src="{mode}.png" alt="Depth {mode}"><details><summary>Confidence</summary><img src="{mode}-confidence.png" alt="Confidence"></details><table>{cells}</table></section>')
    before, after = masks["AB_AC"], masks["AB_AC_BC"]
    report["bcContribution"] = dict(addedValidPixels=int((after & ~before).sum()), lostValidPixels=int((before & ~after).sum()),
                                    coverageGainPercentagePoints=100 * float(after.sum() - before.sum()) / int(roi.sum()))
    cv.imwrite(str(output / "A.png"), images["A"])
    (output / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    (output / "report.html").write_text(f'''<!doctype html><html lang="ru"><meta charset="utf-8"><title>Сравнение глубины</title>
<style>body{{font:16px system-ui;margin:24px;background:#eef2f5;color:#182030}}main{{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:20px}}section{{background:white;padding:16px;border-radius:12px}}img{{width:100%;image-rendering:pixelated}}td{{padding:4px}}.source{{max-width:600px}}</style>
<h1>Сравнение стереопар</h1><p>Исходная сетка A. Глубина Z вдоль оптической оси A, метры.
Общая шкала: {args.near:g} м (синий) — {args.far:g} м (красный); чёрный — нет данных.</p>
<p>CPU рабочего DepthService. Время включает загрузку карт, remap, stereo и слияние; исключает запуск процесса и запись файлов.
Один замер после прогрева AB: это диагностическое сравнение, не стабильный бенчмарк.</p>
<p>ROI: {html.escape(str(args.roi or 'весь кадр'))}. Эталон Z: {html.escape(str(args.reference_depth))} м.
Без эталона заполнение и confidence не доказывают точность; разброс по сцене не является оценкой шума.</p>
<p>Вклад BC: {html.escape(json.dumps(report['bcContribution']))}</p><img class="source" src="A.png" alt="Исходная A">
<main>{''.join(cards)}</main><p><a href="report.json">Метрики, настройки и SHA-256</a>. NPZ: depthMetres, confidence, valid.</p></html>''', encoding="utf-8")
    print(output / "report.html")
    return report


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("calibration", help="Directory with calibration-draft.json and NPZ maps")
    for camera in "abc":
        p.add_argument(f"--{camera}", required=True, help=f"Original image from camera {camera.upper()}")
    p.add_argument("--output", required=True)
    p.add_argument("--near", type=float, default=1)
    p.add_argument("--far", type=float, default=20)
    p.add_argument("--max-disparity", type=int, default=96)
    p.add_argument("--roi", nargs=4, type=int, metavar=("X", "Y", "WIDTH", "HEIGHT"))
    p.add_argument("--reference-depth", type=float, help="Known constant Z in metres, only for a fronto-parallel plane in ROI")
    p.add_argument("--no-build", action="store_true")
    return p


if __name__ == "__main__":
    compare(parser().parse_args())
