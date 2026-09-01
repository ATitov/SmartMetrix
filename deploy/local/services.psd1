@{
    Infrastructure = @(
        @{ Name = 'nats'; Port = 4222; HealthPort = 8222 }
        @{ Name = 'minio'; Port = 9000; HealthPort = 9000 }
    )
    Services = @(
        @{ Name = 'storage'; Project = 'SmartMetrix.StorageService'; Port = 5105 }
        @{ Name = 'orchestrator'; Project = 'SmartMetrix.MeasurementOrchestrator'; Port = 5201 }
        @{ Name = 'camera'; Project = 'SmartMetrix.CameraService'; Port = 5202 }
        @{ Name = 'quality'; Project = 'SmartMetrix.QualityService'; Port = 5203 }
        @{ Name = 'depth'; Project = 'SmartMetrix.DepthService'; Port = 5204 }
        @{ Name = 'segmentation'; Project = 'SmartMetrix.SegmentationService'; Port = 5205 }
        @{ Name = 'trigger'; Project = 'SmartMetrix.TriggerService'; Port = 5207 }
        @{ Name = 'calibration'; Project = 'SmartMetrix.CalibrationService'; Port = 5208 }
        @{ Name = 'positioning'; Project = 'SmartMetrix.LocalPositioningService'; Port = 5209 }
        @{ Name = 'georeference'; Project = 'SmartMetrix.GeoreferenceService'; Port = 5210 }
        @{ Name = 'block-analysis'; Project = 'SmartMetrix.BlockAnalysisService'; Port = 5211 }
        @{ Name = 'control-points'; Project = 'SmartMetrix.ControlPointService'; Port = 5212 }
        @{ Name = 'cloud-sync'; Project = 'SmartMetrix.CloudSyncService'; Port = 5213 }
        @{ Name = 'api-gateway'; Project = 'SmartMetrix.ApiGateway'; Port = 5190 }
    )
}
