"""Offline ChArUco calibration. Camera extrinsics use camera A as the rig frame."""
import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import html
import json
from pathlib import Path
import sys
import urllib.error
import urllib.request
import uuid

import cv2 as cv
import numpy as np

CAMERAS = ("A", "B", "C")
PAIRS = ("AB", "AC", "BC")


def save_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def board_from(config):
    b = config["board"]
    if b.get("type") != "ChArUco" or b.get("dictionary") != "DICT_5X5_250":
        raise ValueError("This utility requires a ChArUco DICT_5X5_250 board.")
    if not (3 <= b["squaresX"] <= 20 and 3 <= b["squaresY"] <= 20):
        raise ValueError("Board dimensions must be between 3 and 20 squares.")
    if not np.isfinite([b["markerMetres"], b["squareMetres"]]).all() or not 0 < b["markerMetres"] < b["squareMetres"]:
        raise ValueError("Marker size must be positive and smaller than the square.")
    return cv.aruco.CharucoBoard((b["squaresX"], b["squaresY"]), b["squareMetres"],
                                b["markerMetres"], cv.aruco.getPredefinedDictionary(cv.aruco.DICT_5X5_250))


def init_session(args):
    root = Path(args.session).resolve()
    root.mkdir(parents=True, exist_ok=True)
    if (root / "session.json").exists():
        raise ValueError("Session already exists; create a new directory to change the board.")
    config = {"schemaVersion": 1, "rigId": args.rig_id,
              "board": {"type": "ChArUco", "dictionary": "DICT_5X5_250", "squaresX": args.columns,
                        "squaresY": args.rows, "squareMetres": args.square_mm / 1000,
                        "markerMetres": args.square_mm * .75 / 1000},
              "expectedBaselinesMetres": {"AB": .25, "BC": .75, "AC": 1.0},
              "baselineToleranceMetres": .02, "minimumViews": 15, "minimumCorners": 12,
              "maximumRmsPixels": 1.0, "rawImageSize": None}
    board = board_from(config)
    width, height = args.columns * 160, args.rows * 160
    image = board.generateImage((width, height), marginSize=0, borderBits=1)
    cv.imwrite(str(root / "board.png"), image)
    encoded = base64.b64encode((root / "board.png").read_bytes()).decode("ascii")
    # Physical dimensions belong to the board itself; print without fitting to a page.
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{args.columns * args.square_mm}mm" '
           f'height="{args.rows * args.square_mm}mm" viewBox="0 0 {width} {height}">'
           f'<image width="{width}" height="{height}" href="data:image/png;base64,{encoded}"/></svg>')
    (root / "board-print.svg").write_text(svg, encoding="utf-8")
    (root / "views").mkdir(exist_ok=True)
    save_json(root / "session.json", config)
    print(f"Session: {root}\nPrint board-print.svg at 100%; verify square = {args.square_mm:g} mm.")


def request_bytes(url, method="GET", body=None):
    req = urllib.request.Request(url, data=body, method=method,
                                 headers={"Content-Type": "application/json"} if body else {})
    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            return response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        raise ValueError(f"Service returned HTTP {error.code}; check service configuration.") from None
    except urllib.error.URLError:
        raise ValueError("Service unavailable; check its address and whether it is running.") from None


def capture(args):
    root = Path(args.session).resolve()
    read_json(root / "session.json")
    if not args.stationary_confirmed:
        raise ValueError("Hold the board and rig stationary and pass --stationary-confirmed.")
    view = root / "views" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:6])
    def fetch(camera):
        return camera, request_bytes(args.camera_service.rstrip("/") + f"/v1/test/cameras/{camera}/capture", "POST")
    with ThreadPoolExecutor(max_workers=3) as executor:
        frames = list(executor.map(fetch, CAMERAS))
    shapes = []
    for camera, (data, _) in frames:
        image = cv.imdecode(np.frombuffer(data, np.uint8), cv.IMREAD_GRAYSCALE)
        if image is None:
            raise ValueError(f"Camera {camera} returned an unreadable image.")
        shapes.append(image.shape)
    if len(set(shapes)) != 1:
        raise ValueError("All three cameras must use the same resolution.")
    view.mkdir(parents=True)
    metadata = {"stationaryConfirmed": True, "source": "CameraService RTSP test", "frames": {}}
    for camera, (data, headers) in frames:
        path = view / f"{camera}.pgm"
        path.write_bytes(data)
        metadata["frames"][camera] = {"sha256": digest(path), "headers": {
            key: value for key, value in headers.items() if key.lower() in ("x-received-at", "x-timestamp-source", "x-is-test-data")}}
    save_json(view / "capture.json", metadata)
    print(f"Saved stationary view: {view}")


def read_image(path, config):
    if path.suffix.lower() == ".raw":
        size = config.get("rawImageSize")
        if not size or len(size) != 2 or any(not isinstance(x, int) or x <= 0 for x in size):
            raise ValueError("For packed Mono8 RAW set rawImageSize=[width,height] in session.json.")
        pixels = np.frombuffer(path.read_bytes(), np.uint8)
        if pixels.size != size[0] * size[1]:
            raise ValueError(f"RAW size mismatch: {path.name}")
        return pixels.reshape(size[1], size[0])
    image = cv.imdecode(np.frombuffer(path.read_bytes(), np.uint8), cv.IMREAD_GRAYSCALE)
    if image is None:
        raise ValueError(f"Cannot decode {path}")
    return image


def detect_session(root, config, output):
    board = board_from(config)
    detector = cv.aruco.CharucoDetector(board)
    observations, inputs, rejected, seen = [], [], [], set()
    size = None
    for view in sorted((root / "views").iterdir()):
        if not view.is_dir():
            continue
        paths = {}
        for camera in CAMERAS:
            found = [p for p in view.iterdir() if p.stem.upper() == camera and p.suffix.lower() in
                     (".png", ".jpg", ".jpeg", ".pgm", ".tif", ".tiff", ".raw")]
            if len(found) != 1:
                raise ValueError(f"View {view.name} must have exactly one image for camera {camera}.")
            paths[camera] = found[0]
        hashes = tuple(digest(paths[c]) for c in CAMERAS)
        if hashes in seen:
            rejected.append({"view": view.name, "reason": "Duplicate triplet"})
            continue
        seen.add(hashes)
        observation = {"name": view.name, "cameras": {}}
        for camera in CAMERAS:
            path = paths[camera]
            image = read_image(path, config)
            current_size = (image.shape[1], image.shape[0])
            if size is not None and size != current_size:
                raise ValueError("Resolution changed between cameras/views. Do not resize images; use a new session.")
            size = current_size
            corners, ids, _, _ = detector.detectBoard(image)
            inputs.append({"path": str(path.relative_to(root)), "sha256": digest(path)})
            if ids is None or len(ids) < config["minimumCorners"] or board.checkCharucoCornersCollinear(ids):
                rejected.append({"view": view.name, "camera": camera, "reason": "Too few/non-spatial ChArUco corners"})
                continue
            observation["cameras"][camera] = {"ids": ids.flatten(), "points": corners.reshape(-1, 2)}
            preview = cv.cvtColor(image, cv.COLOR_GRAY2BGR)
            cv.aruco.drawDetectedCornersCharuco(preview, corners, ids)
            preview = cv.resize(preview, (min(960, size[0]), round(size[1] * min(960, size[0]) / size[0])))
            cv.imwrite(str(output / "detections" / f"{view.name}-{camera}.jpg"), preview)
        observations.append(observation)
    if size is None:
        raise ValueError("No images found in views/<view-id>/A.png,B.png,C.png.")
    return board, size, observations, inputs, rejected


def solve(config, board, size, observations):
    """Separated from image detection so known 3D synthetic rigs can verify conventions."""
    obj = board.getChessboardCorners()
    cameras, pairs = {}, {}
    criteria = (cv.TERM_CRITERIA_EPS + cv.TERM_CRITERIA_COUNT, 100, 1e-8)
    for camera in CAMERAS:
        views = [(v["name"], v["cameras"][camera]) for v in observations if camera in v["cameras"]]
        if len(views) < config["minimumViews"]:
            raise ValueError(f"Camera {camera}: only {len(views)} usable views; need {config['minimumViews']}.")
        object_points = [obj[v["ids"]].astype(np.float32) for _, v in views]
        image_points = [v["points"].astype(np.float32) for _, v in views]
        rms, matrix, distortion, rvecs, tvecs = cv.calibrateCamera(object_points, image_points, size, None, None, criteria=criteria)
        errors = []
        for (name, _), world, pixels, rv, tv in zip(views, object_points, image_points, rvecs, tvecs):
            projected = cv.projectPoints(world, rv, tv, matrix, distortion)[0].reshape(-1, 2)
            errors.append({"view": name, "rmsPixels": float(np.sqrt(np.mean(np.sum((projected - pixels) ** 2, axis=1))))})
        all_pixels = np.concatenate(image_points)
        coverage = (np.ptp(all_pixels, axis=0) / np.array(size)).tolist()
        cameras[camera] = {"rmsPixels": float(rms), "matrix": matrix.tolist(), "distortion": distortion.flatten().tolist(),
                           "views": errors, "coverageFractionXY": coverage}
    for pair in PAIRS:
        left, right = pair
        objects, a, b, names = [], [], [], []
        for view in observations:
            if left not in view["cameras"] or right not in view["cameras"]:
                continue
            vl, vr = view["cameras"][left], view["cameras"][right]
            ids, li, ri = np.intersect1d(vl["ids"], vr["ids"], return_indices=True)
            if len(ids) < config["minimumCorners"] or board.checkCharucoCornersCollinear(ids.reshape(-1, 1).astype(np.int32)):
                continue
            objects.append(obj[ids].astype(np.float32))
            a.append(vl["points"][li].astype(np.float32))
            b.append(vr["points"][ri].astype(np.float32))
            names.append(view["name"])
        if len(objects) < config["minimumViews"]:
            raise ValueError(f"Pair {pair}: only {len(objects)} shared views; need {config['minimumViews']}.")
        kl, dl = np.array(cameras[left]["matrix"]), np.array(cameras[left]["distortion"])
        kr, dr = np.array(cameras[right]["matrix"]), np.array(cameras[right]["distortion"])
        rms, _, _, _, _, rotation, translation, _, _ = cv.stereoCalibrate(
            objects, a, b, kl, dl, kr, dr, size, flags=cv.CALIB_FIX_INTRINSIC, criteria=criteria)
        r1, r2, p1, p2, q, roi1, roi2 = cv.stereoRectify(kl, dl, kr, dr, size, rotation, translation,
                                                      flags=cv.CALIB_ZERO_DISPARITY, alpha=0)
        axis = 1 if abs(p2[0, 3]) >= abs(p2[1, 3]) else 0
        alignment = []
        for name, pl, pr in zip(names, a, b):
            ul = cv.undistortPoints(pl.reshape(-1, 1, 2), kl, dl, R=r1, P=p1).reshape(-1, 2)
            ur = cv.undistortPoints(pr.reshape(-1, 1, 2), kr, dr, R=r2, P=p2).reshape(-1, 2)
            alignment.append({"view": name, "medianEpipolarErrorPixels": float(np.median(np.abs(ul[:, axis] - ur[:, axis])))})
        pairs[pair] = {"rmsPixels": float(rms), "baselineMetres": float(np.linalg.norm(translation)),
                       "rotationLeftToRight": rotation.tolist(), "translationLeftToRightMetres": translation.flatten().tolist(),
                       "R1": r1.tolist(), "R2": r2.tolist(), "P1": p1.tolist(), "P2": p2.tolist(), "Q": q.tolist(),
                       "roiLeft": list(roi1), "roiRight": list(roi2), "disparityAxis": "x" if axis == 1 else "y", "views": alignment}
    # Camera coordinates -> rig (camera A): X_A = R^T X_camera - R^T T.
    cameras["A"].update(rotationCameraToRig=np.eye(3).tolist(), centreInRigMetres=[0., 0., 0.])
    for camera, pair in (("B", "AB"), ("C", "AC")):
        r = np.array(pairs[pair]["rotationLeftToRight"])
        t = np.array(pairs[pair]["translationLeftToRightMetres"])
        cameras[camera].update(rotationCameraToRig=r.T.tolist(), centreInRigMetres=(-r.T @ t).tolist())
    r_ab, r_ac, r_bc = [np.array(pairs[p]["rotationLeftToRight"]) for p in PAIRS]
    t_ab, t_ac, t_bc = [np.array(pairs[p]["translationLeftToRightMetres"]) for p in PAIRS]
    rotation_closure = r_bc @ r_ab @ r_ac.T
    closure = {"translationMetres": float(np.linalg.norm(r_bc @ t_ab + t_bc - t_ac)),
               "rotationDegrees": float(np.degrees(np.arccos(np.clip((np.trace(rotation_closure) - 1) / 2, -1, 1))))}
    return cameras, pairs, closure


def quality_checks(config, cameras, pairs, closure):
    problems = []
    for name, result in {**cameras, **pairs}.items():
        if not np.isfinite(result["rmsPixels"]) or result["rmsPixels"] > config["maximumRmsPixels"]:
            problems.append(f"{name}: RMS exceeds {config['maximumRmsPixels']} px")
    for camera, result in cameras.items():
        if min(result["coverageFractionXY"]) < .35:
            problems.append(f"{camera}: insufficient image coverage (less than 35% on one axis)")
    for pair, result in pairs.items():
        if abs(result["baselineMetres"] - config["expectedBaselinesMetres"][pair]) > config["baselineToleranceMetres"]:
            problems.append(f"{pair}: measured baseline differs from the configured rig")
    if closure["translationMetres"] > config["baselineToleranceMetres"] or closure["rotationDegrees"] > 1:
        problems.append("Independent AB/AC/BC estimates disagree (pose loop closure)")
    return problems


def calculate(args):
    root, output = Path(args.session).resolve(), Path(args.output).resolve()
    config = read_json(root / "session.json")
    expected = np.array([config["expectedBaselinesMetres"][p] for p in PAIRS], dtype=float)
    if not np.isfinite(expected).all() or np.any(expected <= 0) or 2 * max(expected) > sum(expected) + 1e-9:
        raise ValueError("Expected baselines must be finite, positive and satisfy the triangle inequality.")
    if not str(config["rigId"]).strip():
        raise ValueError("rigId is required.")
    if config["minimumViews"] < 8 or config["minimumCorners"] < 6:
        raise ValueError("Require at least 8 views and 6 corners; recommended defaults are 15 and 12.")
    if any(not np.isfinite(config[k]) or config[k] <= 0 for k in ("maximumRmsPixels", "baselineToleranceMetres")):
        raise ValueError("Quality thresholds must be finite and positive.")
    output.mkdir(parents=True, exist_ok=False)
    (output / "detections").mkdir()
    board, size, observations, inputs, rejected = detect_session(root, config, output)
    save_json(output / "detection-report.json", {"inputs": inputs, "rejected": rejected,
              "views": [{"name": v["name"], "corners": {c: len(o["ids"]) for c, o in v["cameras"].items()}} for v in observations]})
    cameras, pairs, closure = solve(config, board, size, observations)
    problems = quality_checks(config, cameras, pairs, closure)
    for pair, result in pairs.items():
        left, right = pair
        maps = {}
        for side, camera, ri, pi in (("left", left, "R1", "P1"), ("right", right, "R2", "P2")):
            mx, my = cv.initUndistortRectifyMap(np.array(cameras[camera]["matrix"]), np.array(cameras[camera]["distortion"]),
                                               np.array(result[ri]), np.array(result[pi]), size, cv.CV_32FC1)
            maps[side + "X"], maps[side + "Y"] = mx, my
        np.savez_compressed(output / f"maps-{pair}.npz", **maps)
    for camera in CAMERAS:
        save_json(output / f"maps-{camera}.json", {"schemaVersion": 1, "cameraId": camera,
                  "pairs": [{"pair": p, "mapFile": f"maps-{p}.npz", "side": "left" if p[0] == camera else "right",
                             "sha256": digest(output / f"maps-{p}.npz")} for p in PAIRS if camera in p],
                  "note": "Pair-specific rectified grids. Reproject 3D results before fusion; these are not one common camera-A grid."})
    result = {"schemaVersion": 1, "rigId": config["rigId"], "createdAt": datetime.now(timezone.utc).isoformat(),
              "opencvVersion": cv.__version__, "imageSize": list(size), "coordinateConvention": "X right, Y down, Z forward; rig = original camera A; metres",
              "config": config, "inputs": inputs, "rejected": rejected, "cameras": cameras, "pairs": pairs,
              "closure": closure, "qualityPassed": not problems, "qualityProblems": problems,
              "limitations": ["Errors are fitted on the calibration set, not independent accuracy estimates.",
                              "Rig-to-platform pose is not measured by this utility.",
                              "Online depth uses pair maps and scatters points to original camera A pixels; validate accuracy on independent data."]}
    save_json(output / "calibration.json", result)
    rows = "".join(f"<tr><td>{name}</td><td>{item['rmsPixels']:.4f}</td><td>{item.get('baselineMetres', 0):.4f}</td></tr>"
                   for name, item in {**cameras, **pairs}.items())
    messages = "".join(f"<li>{html.escape(p)}</li>" for p in problems + result["limitations"])
    previews = "".join(f'<a href="detections/{html.escape(p.name)}"><img width="300" src="detections/{html.escape(p.name)}"></a>'
                       for p in sorted((output / "detections").glob("*.jpg")))
    (output / "report.html").write_text('<!doctype html><meta charset="utf-8"><title>Калибровка SmartMetrix</title>'
        '<style>body{font:16px system-ui;margin:40px;max-width:1200px}td,th{padding:10px;border:1px solid #ddd}img{margin:5px}</style>'
        f'<h1>Калибровка {html.escape(config["rigId"])}</h1><p>Проверки: {"пройдены" if not problems else "НЕ пройдены"}. '
        'Это черновик, не подтверждение точности измерений.</p><table><tr><th>Камера/пара</th><th>RMS, px</th><th>Базис, м (для пар)</th></tr>'
        + rows + '</table><ul>' + messages + '</ul><h2>Обнаруженные точки</h2>' + previews, encoding="utf-8")
    print(f"Report: {output / 'report.html'}\nQuality: {'PASS' if not problems else 'FAIL'}")
    return 0 if not problems else 2


def rectify(args):
    result_path = Path(args.result).resolve()
    result = read_json(result_path / "calibration.json")
    config = result["config"]
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    with np.load(result_path / f"maps-{args.pair}.npz", allow_pickle=False) as maps:
        for side, path in (("left", args.left), ("right", args.right)):
            image = read_image(Path(path), config)
            if list(image.shape[::-1]) != result["imageSize"]:
                raise ValueError("Image dimensions differ from calibration.")
            corrected = cv.remap(image, maps[side + "X"], maps[side + "Y"], cv.INTER_LINEAR)
            cv.imwrite(str(output / f"{side}.png"), corrected)
    print(f"Rectified pair {args.pair}: {output}")


def export_draft(args):
    root = Path(args.result).resolve()
    result = read_json(root / "calibration.json")
    if not result["qualityPassed"]:
        raise ValueError("Calibration failed quality checks; collect better views and recalculate.")
    pose = read_json(args.rig_to_platform)
    rotation = np.array(pose["rotation"], dtype=float).reshape(3, 3)
    translation = np.array(pose["translation"], dtype=float)
    if translation.shape != (3,) or not np.isfinite(translation).all() or not np.isfinite(rotation).all() or not np.allclose(rotation.T @ rotation, np.eye(3), atol=1e-5) or not np.isclose(np.linalg.det(rotation), 1, atol=1e-5):
        raise ValueError("rig-to-platform must contain a proper rotation (9 numbers) and translation in metres (3 numbers).")
    cameras = []
    for camera, item in result["cameras"].items():
        k = item["matrix"]
        cameras.append({"cameraId": camera, "intrinsics": {"fx": k[0][0], "fy": k[1][1], "cx": k[0][2], "cy": k[1][2],
                         "width": result["imageSize"][0], "height": result["imageSize"][1]},
                        "distortion": item["distortion"], "rotation": np.array(item["rotationCameraToRig"]).flatten().tolist(),
                        "translation": item["centreInRigMetres"], "rectificationMapUri": (root / f"maps-{camera}.json").as_uri()})
    payload = {"rigId": result["rigId"], "cameras": cameras,
               "geometry": {p.lower() + "Metres": result["pairs"][p]["baselineMetres"] for p in PAIRS},
               "rigToPlatform": {"rotation": rotation.flatten().tolist(), "translation": translation.tolist()},
               "reprojectionErrorPixels": max(x["rmsPixels"] for x in [*result["cameras"].values(), *result["pairs"].values()])}
    payload["rectification"] = pipeline_rectification(root, result)
    path = root / "calibration-draft.json"
    if path.exists() and read_json(path) != payload:
        raise ValueError("A different calibration-draft.json already exists; use a new result directory.")
    if not path.exists():
        save_json(path, payload)
    if args.service:
        if (root / "service-draft.json").exists():
            raise ValueError("A service registration receipt already exists; no duplicate draft was sent.")
        data, _ = request_bytes(args.service.rstrip("/") + "/api/calibrations/", "POST", json.dumps(payload).encode())
        save_json(root / "service-draft.json", json.loads(data))
    print(f"Draft: {path}. Not activated. Set Depth:CalibrationDirectory to {root} on the depth host and Pipeline:FramesAreRectified=false.")


def pipeline_rectification(root, result):
    pairs = []
    for name in ("AB", "AC", "BC"):
        pair = result["pairs"][name]
        p1, p2 = np.array(pair["P1"]), np.array(pair["P2"])
        if pair["disparityAxis"] != "x" or p2[0, 3] >= 0 or not np.allclose(p1[:, :3], p2[:, :3], atol=1e-6):
            raise ValueError("Online pipeline requires horizontal positive-disparity pairs with equal rectified intrinsics (AB, AC, BC).")
        fx, fy, cx, cy = float(p1[0, 0]), float(p1[1, 1]), float(p1[0, 2]), float(p1[1, 2])
        baseline = float(-p2[0, 3] / fx)
        if abs(baseline - pair["baselineMetres"]) > 1e-6:
            raise ValueError("Rectified projection baseline differs from the solved geometry.")
        map_file = root / f"maps-{name}.npz"
        sha = digest(map_file)
        manifest = read_json(root / f"maps-{name[0]}.json")
        if not any(entry["pair"] == name and entry["sha256"] == sha for entry in manifest["pairs"]):
            raise ValueError("Rectification map was modified after calibration; recalculate in a new directory.")
        left_camera = result["cameras"][name[0]]
        rotation_to_a = np.array(left_camera["rotationCameraToRig"]) @ np.array(pair["R1"]).T
        pairs.append({"leftCameraId": name[0], "rightCameraId": name[1], "mapFile": map_file.name, "sha256": sha,
                      "fx": fx, "fy": fy, "cx": cx, "cy": cy, "baselineMetres": baseline,
                      "rectifiedToReferenceRotation": rotation_to_a.flatten().tolist(),
                      "rectifiedToReferenceTranslation": left_camera["centreInRigMetres"]})
    reference = result["cameras"]["A"]
    k = reference["matrix"]
    if len(reference["distortion"]) != 5:
        raise ValueError("Online projection requires the five-coefficient OpenCV distortion model.")
    return {"schemaVersion": 2, "width": result["imageSize"][0], "height": result["imageSize"][1],
            "referenceCameraId": "A", "pairs": pairs,
            "referenceProjection": {"fx": k[0][0], "fy": k[1][1], "cx": k[0][2], "cy": k[1][2], "distortion": reference["distortion"]}}


def main():
    parser = argparse.ArgumentParser(description="SmartMetrix: offline A/B/C ChArUco calibration (metres).")
    commands = parser.add_subparsers(dest="command", required=True)
    init = commands.add_parser("init", help="Create session and printable ChArUco board")
    init.add_argument("session")
    init.add_argument("--rig-id", default="hikvision-test-rig")
    init.add_argument("--columns", type=int, default=10)
    init.add_argument("--rows", type=int, default=7)
    init.add_argument("--square-mm", type=float, default=80)
    init.set_defaults(action=init_session)
    cap = commands.add_parser("capture", help="Capture stationary A/B/C through RTSP CameraService")
    cap.add_argument("session")
    cap.add_argument("--camera-service", default="http://127.0.0.1:5102")
    cap.add_argument("--stationary-confirmed", action="store_true")
    cap.set_defaults(action=capture)
    calc = commands.add_parser("calibrate", help="Detect corners, solve intrinsics/extrinsics and create reports/maps")
    calc.add_argument("session")
    calc.add_argument("--output", required=True)
    calc.set_defaults(action=calculate)
    rect = commands.add_parser("rectify", help="Apply one pair's maps to saved images")
    rect.add_argument("result")
    rect.add_argument("--pair", choices=PAIRS, required=True)
    rect.add_argument("--left", required=True)
    rect.add_argument("--right", required=True)
    rect.add_argument("--output", required=True)
    rect.set_defaults(action=rectify)
    export = commands.add_parser("export-draft", help="Export CalibrationService payload; optionally register a draft")
    export.add_argument("result")
    export.add_argument("--rig-to-platform", required=True)
    export.add_argument("--service")
    export.set_defaults(action=export_draft)
    args = parser.parse_args()
    try:
        return args.action(args) or 0
    except (ValueError, OSError, KeyError, cv.error) as error:
        print(f"Calibration error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
