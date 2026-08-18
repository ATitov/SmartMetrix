using SmartMetrix.E2ESimulator;

var dataset = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "dataset", "golden-v1.json");
var output = args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "artifacts", "e2e");
var report = await E2ESimulatorRunner.RunAsync(dataset, output);
Console.WriteLine($"SmartMetrix E2E: {(report.Passed ? "PASS" : "FAIL")}");
Console.WriteLine($"Dataset={report.DatasetVersion}; Model={report.ModelVersion}; Calibration={report.CalibrationVersion}; Transform={report.TransformVersion}");
Console.WriteLine($"D50 error={report.D50RelativeError:P2}; D80 error={report.D80RelativeError:P2}; coordinate error={report.CoordinateErrorMetres:F4} m");
Console.WriteLine($"Report: {Path.GetFullPath(Path.Combine(output, "report.json"))}");
return report.Passed ? 0 : 1;
