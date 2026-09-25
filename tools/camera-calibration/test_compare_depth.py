import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import cv2 as cv
import numpy as np

from compare_depth import compare, metrics, parser


class ComparisonTests(unittest.TestCase):
    def test_metrics_ignore_holes_and_use_roi(self):
        result = metrics(np.array([[5., 7., 99.]]), np.array([[1., 1., 0.]]),
                         np.ones((1, 3), dtype=bool), 6)
        self.assertAlmostEqual(result["coveragePercent"], 200 / 3)
        self.assertEqual(result["rmseMetres"], 1)
        self.assertEqual(result["biasMetres"], 0)
        empty = metrics(np.zeros((1, 1)), np.zeros((1, 1)), np.ones((1, 1), dtype=bool), 6)
        self.assertIsNone(empty["maeMetres"])

    def test_production_backend_report_on_known_plane(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            width, height = 128, 48
            image = np.random.default_rng(42).integers(0, 256, (height, width), dtype=np.uint8)
            for camera, shift in zip("ABC", [0, 4, 16]):
                cv.imwrite(str(root / f"{camera}.png"), np.roll(image, -shift, axis=1))
            x, y = np.meshgrid(np.arange(width, dtype=np.float32), np.arange(height, dtype=np.float32))
            pairs = []
            for name, baseline, offset in [("AB", .25, 0), ("AC", 1., 0), ("BC", .75, .25)]:
                path = root / f"{name}.npz"
                np.savez_compressed(path, leftX=x, leftY=y, rightX=x, rightY=y)
                pairs.append(dict(leftCameraId=name[0], rightCameraId=name[1], mapFile=path.name,
                                  sha256=hashlib.sha256(path.read_bytes()).hexdigest(), fx=80, fy=80, cx=64, cy=24,
                                  baselineMetres=baseline, rectifiedToReferenceRotation=np.eye(3).ravel().tolist(),
                                  rectifiedToReferenceTranslation=[offset, 0, 0]))
            rect = dict(schemaVersion=2, width=width, height=height, referenceCameraId="A", pairs=pairs,
                        referenceProjection=dict(fx=80, fy=80, cx=64, cy=24, distortion=[0]*5))
            (root / "calibration-draft.json").write_text(json.dumps(dict(rectification=rect)), encoding="utf-8")
            arguments = [str(root), "--output", str(root / "report"), "--no-build", "--max-disparity", "24",
                         "--roi", "30", "8", "70", "30", "--reference-depth", "5"]
            for camera in "ABC":
                arguments.extend([f"--{camera.lower()}", str(root / f"{camera}.png")])
            report = compare(parser().parse_args(arguments))
            self.assertEqual(len(report["runs"]), 5)
            for run in report["runs"]:
                self.assertGreater(run["metrics"]["coveragePercent"], 90)
                self.assertLess(run["metrics"]["maeMetres"], .05)
                self.assertEqual(len(run["checksums"]), len(run["mode"].split("_")))
                self.assertIsNotNone(cv.imread(str(root / "report" / f'{run["mode"]}.png')))
            self.assertTrue((root / "report/report.html").exists())
            with self.assertRaises(ValueError):
                compare(parser().parse_args(arguments))


if __name__ == "__main__":
    unittest.main()
