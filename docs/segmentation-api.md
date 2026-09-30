# API сегментации

## Совместимость

`POST /v1/measurements/{measurementId}/segmentation` сохраняет прежние обязательные
поля запроса и ответа. В ответ добавлены поля схемы `schemaVersion: 2`;
старые клиенты могут продолжать использовать `event.maskUri`, `confidenceMapUri`,
`lowConfidence`, `classPixelCounts` и `isTestData`.
Обработка синхронная; HTTP-таймаут вызывающей стороны должен превышать таймаут
инференса и время записи артефактов. Асинхронных заданий в этом контракте нет.

## Запрос

Пример корректного минимального кадра 2×2 RGB8 (чёрный тестовый кадр):

```json
{
  "frame": {
    "width": 2,
    "height": 2,
    "channels": 3,
    "pixelFormat": "RGB8",
    "pixels": "AAAAAAAAAAAAAAAA",
    "cameraId": "A",
    "pixelGrid": "CameraAOriginal"
  },
  "detection": {
    "yoloConfidence": 0.5,
    "yoloIou": 0.45,
    "yoloImageSize": 1920,
    "yoloMaxDetections": 1000,
    "yoloTta": false,
    "maskMinimumArea": 100,
    "tileSize": 0,
    "tileOverlap": 0.25
  }
}
```

`pixels` — base64 плотно упакованных RGB-байтов по строкам, без заголовка файла.
Длина после декодирования — `width × height × 3`; максимум 30 миллионов пикселей.
Также действуют лимиты размера HTTP-запроса сервера и прокси: для больших кадров
их нужно настроить отдельно. `cameraId` и `pixelGrid` необязательны для прямых
вызовов и возвращаются без преобразования. Оркестратор всегда указывает `A` и
`CameraAOriginal` и проверяет их перед совмещением с глубиной.

`detection` необязателен; значения выше — значения по умолчанию. Поля могут
передаваться частично. Backend `Deterministic` отклоняет `detection` с HTTP 400,
чтобы параметры реальной модели не игнорировались молча.

| Параметр | Допустимые значения |
|---|---|
| `yoloConfidence`, `yoloIou` | Конечное число от 0 до 1 |
| `yoloImageSize` | 32–4096, кратно 32 |
| `yoloMaxDetections` | 1–10000 |
| `maskMinimumArea` | 1–30000000 пикселей |
| `tileSize` | 0 (отключён) или 256–4096 |
| `tileOverlap` | Конечное число от 0 до 0.5 |

StoneVision дополнительно ограничивает число тайлов до 100; для больших кадров
увеличьте `tileSize`. Фактически отправленные параметры возвращаются в `detection`.

## Дополнительные поля ответа

| Поле | Содержимое |
|---|---|
| `image` | `width`, `height`, `pixelFormat`, `cameraId`, `pixelGrid` |
| `provenance` | `backend`, `modelVersion`, `backendVersion`, `weightsVersion` |
| `processingMilliseconds` | Время обработки запроса, включая инференс и запись артефактов |
| `detection` | Параметры, отправленные в StoneVision; null для тестового backend |
| `instances` | Список экземпляров камней |
| `instanceMapUri` | JSON-карта принадлежности пикселей экземплярам |
| `rawResultUri` | Исходный JSON-ответ StoneVision |
| `warnings` | Список объектов `{code, message}` |

Версии являются метками конфигурации развертывания, а не автоматически
вычисленными хешами. `modelVersion` берётся из `Segmentation__ModelVersion`,
`backendVersion` — из `Segmentation__StoneVisionVersion`, `weightsVersion` — из
`Segmentation__WeightsVersion`. Неизвестные версии остаются null.

Каждый элемент `instances` содержит:

```json
{
  "instanceId": 7,
  "boundingBox": {"x": 10, "y": 20, "width": 30, "height": 40},
  "areaPixels": 850,
  "maskUri": "s3://bucket/measurement/segmentation/instances/7.json",
  "yoloConfidence": 0.91,
  "samScore": 0.87
}
```

`instanceId` — положительный ID аннотации StoneVision, уникальный внутри ответа;
это не ID отслеживания камня между кадрами. Bounding box и площадь вычисляются
по исходной маске, а не по прямоугольнику YOLO. Начало координат — левый верхний
угол, единицы — пиксели. `samScore` может быть null и не заменяет YOLO confidence.

`maskUri` указывает на JSON `{size: [height, width], counts: [...]}` с COCO
uncompressed RLE: обход по столбцам, первая серия — фон; отверстия сохраняются.
Маски разных экземпляров могут пересекаться.

`instanceMapUri` указывает на `segmentation/instances.json`:

```json
{
  "schemaVersion": 1,
  "width": 2,
  "height": 2,
  "cameraId": "A",
  "pixelGrid": "CameraAOriginal",
  "labels": [7, 7, 0, 9]
}
```

`labels` — целые ID по строкам, 0 обозначает фон. При пересечении выбирается
экземпляр с большей YOLO confidence, при равенстве — с меньшим ID. Исходные
маски и площади не обрезаются. Полностью перекрытый экземпляр остаётся в
`instances`, но может отсутствовать на карте для 3D-анализа.

BlockAnalysis принимает необязательный массив `instanceLabels`, возвращает
`sourceInstanceId` для каждой 3D-компоненты и ссылку на карту в `sourceArtifacts`.
Без массива сохраняется старый алгоритм. С массивом разные ID не объединяются;
разрывы глубины и геометрические пороги всё ещё могут разделить один ID на
несколько компонент. Это не гарантирует соответствие один экземпляр — один блок.

## Предупреждения и ошибки

- `NoDetections`: пустой список детекций — успешный ответ, пустые маски и экземпляры.
- `LowConfidence`: средняя уверенность по всему кадру ниже порога. Confidence
  необнаруженных пикселей равен 0, поэтому это зависит и от доли камней в кадре.
- `OverlappingInstances`: пересечения разрешены по правилу выше.
- `TestBackend`: результат детерминированного симулятора; экземпляров нет,
  `instanceMapUri` и `rawResultUri` равны null, `isTestData=true`.

Ошибки возвращаются в формате Problem Details:

| HTTP | `title` | Причина |
|---|---|---|
| 400 | `InvalidSegmentationRequest` | Неверный кадр, параметры или неподдерживаемый режим |
| 502 | `InvalidSegmentationResponse` | Невалидный JSON/COCO/RLE, несовпадение размеров, повтор ID, отсутствие SAM-маски |
| 502 | `SegmentationDependencyFailure` | HTTP-ошибка StoneVision или хранилища |
| 504 | `SegmentationDependencyTimeout` | Таймаут запроса к зависимости |

Ошибки JSON-привязки запроса также возвращают HTTP 400 средствами ASP.NET Core;
они могут иметь другой формат. При отсутствии SAM-маски у любой детекции запрос
по-прежнему завершается ошибкой: bounding box не используется для измерений.
Проверка всего ответа выполняется до записи масок. Ошибка хранилища может оставить
часть артефактов; успешный результат API при этом не возвращается.

## Возможности и готовность

`GET /v1/segmentation/capabilities` возвращает backend, версии, поддерживаемые
режимы, формат кадра, предельное число пикселей и параметры по умолчанию.
`GET /health` — liveness; `GET /ready` — готовность, включая загрузку моделей
StoneVision. Реальное распознавание трещин не поддерживается.
