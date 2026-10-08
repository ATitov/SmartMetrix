# StoneVision в конвейере SmartMetrix

`MeasurementOrchestrator` → `SegmentationService` → StoneVision `/detect_json`.
HTTP-контракт сегментации и пути `mask.pgm` / `confidence.pgm` сохранены.
Backend `Deterministic` остаётся значением по умолчанию для тестов; для реальных
масок нужно явно включить `StoneVision`. При ошибке сервиса подмены симулятором нет.
Readiness `/ready` проверяет доступность StoneVision и `models_loaded`.

## Исходники и веса

`StoneVision/` подключён как Git submodule из
<https://github.com/StoneRecognition/StoneVision>. После клонирования SmartMetrix:

```powershell
git submodule update --init --recursive
```

Веса поставляются отдельно: `StoneVision/weights/best.pt` и
`StoneVision/weights/mobile_sam.pt`. Они, виртуальное окружение и результаты
инференса не входят в репозиторий SmartMetrix. Версию развернутой пары весов
нужно явно записывать в `Segmentation__ModelVersion`; она попадает в результат
измерения. Версия Git-кода не заменяет версию весов.

## Локальный запуск

Для рабочей станции с NVIDIA GPU, Docker и NVIDIA Container Toolkit:

```powershell
$env:STONEVISION_MODEL_VERSION = 'quarry-weights-v1' # фактическая версия ваших весов
docker compose -f docker-compose.yml -f compose.stonevision.yml up -d --build
```

Сервис сегментации доступен на `http://127.0.0.1:5205`, StoneVision — на
`http://127.0.0.1:5000`. Остальные сервисы конвейера запускаются по
[measurement-pipeline.md](measurement-pipeline.md). В процессе запуска оркестратора задайте
`Pipeline__RequestTimeoutSeconds=240` и `MeasurementWorkflow__StageTimeoutSeconds=900`,
чтобы его таймаут не прерывал инференс раньше HTTP-адаптера (180 секунд).

Можно использовать уже запущенный Python-сервис по инструкции
[StoneVision/RUN_NOTES.md](../../StoneVision/RUN_NOTES.md). В отдельном PowerShell:

```powershell
$env:Segmentation__Backend = 'StoneVision'
$env:Segmentation__StoneVisionBaseUrl = 'http://127.0.0.1:5000'
$env:Segmentation__ModelVersion = 'quarry-weights-v1'
$env:Segmentation__StorageBaseUrl = 'http://127.0.0.1:5105'
dotnet run --project src/Services/SmartMetrix.SegmentationService -- --urls http://127.0.0.1:5205
```

Для edge предусмотрен override `deploy/edge/compose.stonevision.yml`:
добавьте его после `deploy/edge/compose.yml` и задайте `STONEVISION_URL` и
`STONEVISION_MODEL_VERSION`. URL должен быть доступен из внутренней сети
`backend`: например, сервис в той же Docker-сети. Эта сеть изолирована, поэтому
внешний GPU-хост потребует отдельно настроенного сетевого доступа.
Dockerfile StoneVision рассчитан на CUDA 12.8; совместимость с Jetson/JetPack
этот override не предполагает и Python-контейнер на Jetson не собирает.

## Семантика результата и ограничения

- Весь RGB8-кадр передаётся как PPM без масштабирования и потерь. Текущий
  оркестратор передаёт кадр A, повторяя Mono8 в трёх каналах.
- COCO uncompressed RLE декодируется из порядка по столбцам в порядок по строкам,
  с сохранением отверстий. Проверяются размер, длины серий, категория и confidence.
- Камни получают класс `Rock=1`, остальные пиксели — `Background=0`.
  `Crack=2` не заполняется: модель не распознаёт трещины.
- Confidence камня — YOLO confidence; при пересечении масок берётся максимум.
  У необнаруженных пикселей confidence равен нулю (неизвестно). Среднее по всему
  кадру определяет `LowConfidence`, поэтому малая доля камней может давать флаг
  низкой уверенности даже при уверенных детекциях. SAM score хранится отдельно
  в исходном ответе и не подменяет confidence.
- При отсутствии SAM-маски у любой детекции этап завершается ошибкой. Bounding
  box не превращается в измерительную маску. Пустой список детекций допустим:
  пустая маска, нулевая уверенность, `LowConfidence=true` при обычном пороге.
- Исходный ответ сохраняется в `segmentation/stonevision.json`, включая отдельные
  экземпляры камней и оценки SAM. API также возвращает `instances` и
  `instanceMapUri`: оркестратор передаёт карту ID в BlockAnalysis, который
  не объединяет соседние пиксели с разными ID. Разрывы маски или глубины могут
  разделить один экземпляр на несколько 3D-компонент с одинаковым `sourceInstanceId`.
- У результата сегментации `IsTestData=false`; симулятор камеры по-прежнему
  помечает итоговое измерение как тестовое. Это не подтверждение метрологической
  точности: необходима проверка весов на целевой сцене и согласованности кадра A
  с координатной сеткой реконструкции.

Проверки адаптера без GPU: `dotnet test --filter FullyQualifiedName~StoneVisionTests`.

Контракт запросов, экземпляров, предупреждений и ошибок: [segmentation-api.md](segmentation-api.md).
