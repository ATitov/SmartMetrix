import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

import cv2 as cv
import numpy as np

import calibrate as tool


def synthetic_observations(config, count=18):
    board = tool.board_from(config)
    obj = board.getChessboardCorners()
    matrix = np.array([[1100., 0, 800], [0, 1080, 500], [0, 0, 1]])
    distortion = np.array([-.05, .01, .001, -.001, 0.])
    rng = np.random.default_rng(719)
    views = []
    for index in range(count):
        rotation = rng.uniform(-.55, .55, 3)
        translation = np.array([rng.uniform(-.15, .55), rng.uniform(-.6, .15), rng.uniform(1.7, 2.8)])
        cameras = {}
        for camera, x in zip(tool.CAMERAS, (0, .25, 1)):
            points = cv.projectPoints(obj, rotation, translation - np.array([x, 0, 0]), matrix, distortion)[0].reshape(-1, 2)
            cameras[camera] = {"ids": np.arange(len(obj), dtype=np.int32), "points": points}
        views.append({"name": str(index), "cameras": cameras})
    return board, views


class CalibrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "session"
        tool.init_session(SimpleNamespace(session=str(self.root), rig_id="test-rig", columns=10, rows=7, square_mm=80))
        self.config = tool.read_json(self.root / "session.json")

    def test_printed_board_is_detectable_and_physical_size_is_explicit(self):
        image = cv.imread(str(self.root / "board.png"), cv.IMREAD_GRAYSCALE)
        image = cv.copyMakeBorder(image, 50, 50, 50, 50, cv.BORDER_CONSTANT, value=255)
        corners, ids, _, _ = cv.aruco.CharucoDetector(tool.board_from(self.config)).detectBoard(image)
        self.assertEqual(54, len(ids))
        self.assertEqual(len(corners), len(ids))
        svg = (self.root / "board-print.svg").read_text()
        self.assertIn('width="800mm"', svg)
        self.assertIn('height="560mm"', svg)

    def test_recovers_known_camera_centres_and_rectifies_correspondences(self):
        board, observations = synthetic_observations(self.config)
        cameras, pairs, closure = tool.solve(self.config, board, (1600, 1000), observations)
        for camera, expected in zip(tool.CAMERAS, (0, .25, 1)):
            np.testing.assert_allclose(cameras[camera]["centreInRigMetres"], [expected, 0, 0], atol=2e-4)
            self.assertAlmostEqual(1100, cameras[camera]["matrix"][0][0], delta=.1)
        self.assertLess(closure["translationMetres"], 1e-4)
        self.assertLess(closure["rotationDegrees"], .01)
        for pair, baseline in (("AB", .25), ("AC", 1), ("BC", .75)):
            self.assertAlmostEqual(baseline, pairs[pair]["baselineMetres"], delta=2e-4)
            self.assertLess(max(x["medianEpipolarErrorPixels"] for x in pairs[pair]["views"]), .01)
        self.assertEqual([], tool.quality_checks(self.config, cameras, pairs, closure))
        pairs["AB"]["baselineMetres"] = .7
        self.assertTrue(tool.quality_checks(self.config, cameras, pairs, closure))

    def test_insufficient_views_cannot_produce_calibration(self):
        board, views = synthetic_observations(self.config, 3)
        with self.assertRaisesRegex(ValueError, "usable views"):
            tool.solve(self.config, board, (1600, 1000), views)

    def test_stationary_confirmation_required_before_network(self):
        with self.assertRaisesRegex(ValueError, "stationary"):
            tool.capture(SimpleNamespace(session=str(self.root), stationary_confirmed=False))

    def test_raw_dimensions_must_be_explicit_and_correct(self):
        path = self.root / "A.raw"
        path.write_bytes(bytes(range(12)))
        with self.assertRaisesRegex(ValueError, "rawImageSize"):
            tool.read_image(path, self.config)
        self.config["rawImageSize"] = [4, 3]
        self.assertEqual((3, 4), tool.read_image(path, self.config).shape)
        self.config["rawImageSize"] = [5, 3]
        with self.assertRaisesRegex(ValueError, "mismatch"):
            tool.read_image(path, self.config)

    def test_duplicate_views_do_not_inflate_observation_count(self):
        image = cv.imread(str(self.root / "board.png"), cv.IMREAD_GRAYSCALE)
        image = cv.copyMakeBorder(image, 50, 50, 50, 50, cv.BORDER_CONSTANT, value=255)
        for index in range(2):
            directory = self.root / "views" / str(index)
            directory.mkdir()
            for camera in tool.CAMERAS:
                cv.imwrite(str(directory / f"{camera}.png"), image)
        output = self.root / "output"
        (output / "detections").mkdir(parents=True)
        _, _, views, _, rejected = tool.detect_session(self.root, self.config, output)
        self.assertEqual(1, len(views))
        self.assertEqual("Duplicate triplet", rejected[0]["reason"])

    def test_failed_quality_cannot_be_exported(self):
        result = self.root / "result"
        result.mkdir()
        tool.save_json(result / "calibration.json", {"qualityPassed": False})
        with self.assertRaisesRegex(ValueError, "failed quality"):
            tool.export_draft(SimpleNamespace(result=str(result)))

    def test_end_to_end_rendered_images_maps_and_service_draft(self):
        board_image = cv.imread(str(self.root / "board.png"), cv.IMREAD_GRAYSCALE)
        matrix = np.array([[1100., 0, 800], [0, 1080, 500], [0, 0, 1]])
        world = np.array([[0, 0, 0], [.8, 0, 0], [.8, .56, 0], [0, .56, 0]], np.float32)
        source = np.array([[0, 0], [1599, 0], [1599, 1119], [0, 1119]], np.float32)
        rng = np.random.default_rng(29)
        for index in range(20):
            directory = self.root / "views" / str(index)
            directory.mkdir()
            rotation = rng.uniform(-.5, .5, 3)
            translation = np.array([rng.uniform(.05, .5), rng.uniform(-.5, .05), rng.uniform(1.8, 2.7)])
            for camera, x in zip(tool.CAMERAS, (0, .25, 1)):
                target = cv.projectPoints(world, rotation, translation - [x, 0, 0], matrix, None)[0].reshape(-1, 2)
                homography = cv.getPerspectiveTransform(source, target)
                image = cv.warpPerspective(board_image, homography, (1600, 1000), borderValue=255)
                cv.imwrite(str(directory / f"{camera}.png"), image)
        result = self.root / "result"
        self.assertEqual(0, tool.calculate(SimpleNamespace(session=str(self.root), output=str(result))))
        data = tool.read_json(result / "calibration.json")
        self.assertAlmostEqual(1, data["pairs"]["AC"]["baselineMetres"], delta=.02)
        self.assertTrue((result / "report.html").exists())
        tool.rectify(SimpleNamespace(result=str(result), pair="AB", left=str(self.root / "views/0/A.png"),
                                     right=str(self.root / "views/0/B.png"), output=str(self.root / "rectified")))
        self.assertEqual((1000, 1600), cv.imread(str(self.root / "rectified/left.png"), 0).shape)
        pose = self.root / "pose.json"
        tool.save_json(pose, {"rotation": np.eye(3).flatten().tolist(), "translation": [0, 0, 0]})
        tool.export_draft(SimpleNamespace(result=str(result), rig_to_platform=str(pose), service=None))
        draft = tool.read_json(result / "calibration-draft.json")
        self.assertEqual("test-rig", draft["rigId"])
        self.assertEqual(["A", "B", "C"], [c["cameraId"] for c in draft["cameras"]])
        self.assertAlmostEqual(.25, draft["geometry"]["abMetres"], delta=.02)
        rectification = draft["rectification"]
        self.assertEqual("A", rectification["referenceCameraId"])
        self.assertEqual(2, rectification["schemaVersion"])
        self.assertEqual(["AB", "AC", "BC"], [p["leftCameraId"] + p["rightCameraId"] for p in rectification["pairs"]])
        for pair in rectification["pairs"]:
            self.assertEqual(tool.digest(result / pair["mapFile"]), pair["sha256"])
            left = pair["leftCameraId"]
            np.testing.assert_allclose(np.array(pair["rectifiedToReferenceRotation"]).reshape(3, 3),
                                       np.array(data["cameras"][left]["rotationCameraToRig"]) @ np.array(data["pairs"][left + pair["rightCameraId"]]["R1"]).T)
            np.testing.assert_allclose(pair["rectifiedToReferenceTranslation"], data["cameras"][left]["centreInRigMetres"])
        self.assertEqual(data["cameras"]["A"]["distortion"], rectification["referenceProjection"]["distortion"])
        self.assertGreater(draft["cameras"]["ABC".index("C")]["translation"][0], .98)
        with (result / "maps-AB.npz").open("ab") as stream:
            stream.write(b"modified")
        with self.assertRaisesRegex(ValueError, "modified"):
            tool.pipeline_rectification(result, data)


if __name__ == "__main__":
    unittest.main()
