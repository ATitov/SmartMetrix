# Реализация задач ревью от 30.09.2026

Изменения подготовлены в ветке `codex/review-remediation` поверх `feature/stonevision-integration` (PR #23). Номера ниже соответствуют порядку в
`tools/review-tickets-2026-09-30.json`. GitHub issues не были созданы: интеграция
отказала в записи (403). Этот отчёт не означает аппаратную или полевую приёмку.

## Статус 14 пунктов

| № | Задача | Реализовано | Оставшаяся приёмка |
|---|---|---|---|
| 1 | Scoped GUI | Загрузка областей и ролей, переключение области, scoped чтение и команды, CSRF, отказ без доступной области. | Программная часть завершена; браузерные сценарии и существующие проверки авторизации API. |
| 2 | Действующие настройки | Приватный GET/PUT `/v1/configuration` в пяти сервисах, whitelist, валидация, optimistic revision, сохранение и откат; шлюз и GUI показывают ответы сервисов. Фиктивные Defaults удалены. | Применение экспозиции к физической Arena-камере; откат при отказе устройства. |
| 3 | Точность калибровки | Версионированная Accuracy в CalibrationPayload, проверка confidence и PSD ковариации; данные идут в анализ, геопривязку и manifest. Без Accuracy — CalibrationAccuracyUnknown. | Измерить реальные оценки на установке и создать новую версию калибровки. |
| 4 | Размеры и объём | PCA вместо размахов мировых осей, явная модель visible-surface-pca-ellipsoid-proxy-v2. Для плоских и частично видимых поверхностей нет объёма и объёмных процентилей. | Проверка на эталонных объектах; оценка полного физического объёма по одной видимой поверхности не реализована. |
| 5 | Trigger → Orchestrator | Durable JetStream consumer, rig/excavator identity, срок действия и схема события, идемпотентный запуск по EventId/MeasurementId, ack после сохранения. | Прогон с реальным NATS, рестартом и повторной доставкой прошёл; повтор события не создаёт второе измерение. |
| 6 | Границы блоков | Точки контура выделенной области из организованного облака передаются и преобразуются в Georeference. BoundaryKind сохраняется. | Программная часть завершена. Это набор точек видимого контура, не полигон и не замкнутая поверхность. |
| 7 | Quality persistence | Неизменяемый JSON на processing run, atomic rename и flush, fingerprint входа; одинаковый replay возвращает сохранённый результат, другой вход конфликтует. | Программная часть завершена; хранение требует постоянного тома. |
| 8 | Production/test | Явный DeploymentProfile; Production отвергает Simulator/Rtsp, Deterministic и demo workflow. Edge compose включает Production и реальный StoneVision backend. | Программная часть завершена. |
| 9 | Метрики качества | Все коэффициенты и пороги сцен конфигурируются и валидируются; в результате полные профили, версии, имя метода, FieldValidated=false. | Размеченные кадры, calibration/validation split, метрики ложных отказов/пропусков; эвристика загрязнения остаётся эвристикой. |
| 10 | Профиль анализа | Пороги confidence, partial factor, минимальная толщина и границы классов вынесены в options/runtime settings; параметры и версия присутствуют в provenance. | Программная часть завершена; подбор значений на данных объекта. Число классов меняется через конфигурацию при старте. |
| 11 | Native Arena/CUDA | Добавлены ABI probes и readiness: диагностический stub, отсутствующий SDK/GPU или несовместимая библиотека не показываются готовыми. Arena readiness открывает настроенные камеры. | Нужны Arena SDK, CUDA OpenCV, целевой Jetson/камеры, сборка и нагрузочная аппаратная проверка. Native код здесь не скомпилирован. |
| 12 | Reverse disparity | Отдельный отрицательный диапазон reverse matcher, проверка abs(d + reverse_d), границ и конфигурации. Добавлен CTest известного сдвига/окклюзии. | Запуск CTest на CUDA, сравнение с CPU и benchmark. Native код здесь не проверен исполнением. |
| 13 | Источники позиции | TCP JSON-lines bridge: bounded frames, source/clock/schema validation, covariance, reconnect, freshness readiness, provenance. | Прямые протоколы конкретных GNSS/IMU/энкодера/тахеометра не реализованы; нужны модели устройств, протоколы и проверка синхронизации времени. |
| 14 | Трещины | Подключаемый HTTP inference, проверка пиксельной сетки/версии/hash весов, readiness, сырая маска и provenance, объединение классов; crack fraction=null без модели. | Нужны обученная модель, веса, реальный сервер и размеченный набор для оценки; обучение и полевая приёмка не выполнены. |

## Настройки и миграция

Сервисы: `camera`, `depth`, `segmentation`, `quality`, `analysis`. Шлюз:
`GET /api/v1/scopes/{scopeId}/engineer/config`,
`PUT /api/v1/scopes/{scopeId}/engineer/config/{service}`. Запрос PUT:

```json
{"expectedRevision":"initial","values":{"Metrics:ShadowThreshold":"40"}}
```

PUT требует роли engineer, доступа к области и CSRF для cookie-сессии. Частные
API сервисов должны оставаться в закрытой сети. Шлюз не меняет backend, адреса,
модель, секреты или набор устройств через форму runtime settings.

Файл каждого сервиса: `data/runtime-settings.json`. Он перекрывает указанные
редактируемые параметры конфигурации, включая переменные окружения, при старте.
Для возврата к deployment-конфигурации остановить сервис, архивировать/убрать
этот файл и запустить сервис. GET показывает bound options со штатными defaults.
Изменение создаёт новую revision; устаревшая revision даёт 409, невалидное
значение — 422, ошибка применения/отката backend — 503. При такой ошибке
проверить `/ready`; GUI не сообщает об успешном применении.

Камера применяет экспозицию через пересоздание контекста Arena под общей с capture
блокировкой. Остальные сервисы используют один snapshot настроек на запрос.
Применение нескольких сервисов последовательно: общей распределённой транзакции
нет; при отказе уже применённые сервисы сохраняют свои настройки, GUI перечитывает
фактическое состояние. Один экземпляр сервиса должен владеть своим файлом runtime
settings; общий том для нескольких процессов не поддержан.

Старые калибровки читаются с прежним checksum, однако без Accuracy анализ нового
измерения завершается ошибкой. Создать новую версию с реально оценёнными
`methodVersion`, `confidence` и 36 элементами `rigToPlatformCovariance` в формате
6×6 row-major, порядок `[x,y,z,rx,ry,rz]`, метры/радианы. Нулевая матрица в
симуляторе не подставляется автоматически; тесты используют явно синтетические
оценки. Не переносить синтетические значения в промышленную калибровку.

`D10/D50/D80/D95Millimetres` теперь nullable. Клиенты должны показывать неизвестный
результат при `volumeDistributionAvailable=false`, учитывать eligible/excluded
counts и модель объёма. Положительный объём — эллипсоидальная оценка наблюдаемой
поверхности. `boundaryKind=visible-surface-samples` запрещает трактовать порядок
элементов как порядок вершин GeoJSON Polygon. Без crack inference поля
`crackPixelCount` и `crackImageFraction` равны null.

Quality результаты хранятся в `data/quality-results/{runId:N}.json`. Автоматического
удаления нет: файлы нужны для replay и аудита. Retention согласовать вместе с
политикой артефактов; резервная копия edge data root уже включает новые каталоги.
Edge bootstrap создаёт отдельные writable тома quality/depth/segmentation/analysis.

DeploymentProfile принимает `Local`, `Test`, `Production`; по умолчанию Local.
`ASPNETCORE_ENVIRONMENT` сам по себе профиль не выбирает. Для промышленной
установки использовать edge compose или явно задать
`SmartMetrix__DeploymentProfile=Production`. Для симулятора — Local/Test.
Edge env теперь также требует rig/excavator/coordinate-system и StoneVision
URL/model-version. Эти идентификаторы должны соответствовать регистрации
калибровки и трансформации. Для scoped GUI настроить Workstations:Scopes и Services
по существующему контракту WorkstationScope, затем выдать пользователю ScopeIds
и необходимые роли через admin API. Права administrator не дают роль engineer
автоматически. При отсутствии scope права администрирования остаются доступны.

## Контракт позиционирования

Настройка `PositioningStreams`: Sources,
ExposureClockId, MaximumSilenceSeconds, ReconnectDelaySeconds. Каждый source:
Host, Port, ExcavatorId, SourceType, SourceId, ClockId. SourceType: 0=TotalStation,
1=Imu, 2=Encoder, 3=Gnss. Bridge отдаёт UTF-8 JSON, одна запись на строку:

```json
{"schemaVersion":1,"clockId":"camera-clock","sample":{"sourceType":3,"sourceId":"gnss-1","hardwareTimestampNanoseconds":123456789,"positionMetres":{"x":1,"y":2,"z":3},"orientation":{"x":0,"y":0,"z":0,"w":1},"covariance":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]}}
```

Это пример формата с синтетической covariance. Производственный bridge обязан
передавать реальную оценку ошибок и переводить timestamp в аппаратную шкалу
экспозиции. Совпадение ClockId проверяется; преобразование GPS/UTC/PTP времени
адаптер не выполняет. Лимит строки 8192 байта; quaternion нормализован, covariance
симметрична и PSD. Нужны хотя бы position и orientation среди источников для
получения полной позы. Подключение восстанавливается автоматически; отсутствие
свежих данных настроенного bridge делает readiness unhealthy.

## Контракт модели трещин

Задать `Segmentation:CrackBaseUrl`, `CrackModelVersion`, `CrackWeightsSha256`
(64 hex); backend должен быть StoneVision. `GET health`:

```json
{"ready":true,"modelVersion":"cracks-v1","weightsSha256":"<64 hex>"}
```

`POST v1/cracks` принимает `{processingRunId,frame}` с существующим контрактом
SegmentationFrame (RGB8, исходный CameraA pixel grid). Ответ:

```json
{"schemaVersion":1,"width":3,"height":2,"cameraId":"A","pixelGrid":"CameraAOriginal","mask":"AQAAAAAA","confidence":"5gAAAAAA","modelVersion":"cracks-v1","weightsSha256":"<64 hex>"}
```

Mask/Confidence — base64 byte arrays в row-major, длина width×height; mask содержит
0/1, confidence 0..255. Размеры, camera/grid, версия и hash должны совпасть.
Несовместимый ответ отклоняется до записи артефактов. Пиксели трещин имеют класс 2
и нулевую stone instance label. Исходные StoneVision instance masks сохраняются;
они отличаются от объединённой карты в местах трещин. Реализация модели и веса
не входят в этот контракт.

## Аппаратная проверка

На целевой системе с установленными SDK:

```sh
cmake -S src/Services/SmartMetrix.CameraService/native -B artifacts/native-arena -DSMARTMETRIX_WITH_ARENA=ON -DARENA_SDK_ROOT=/opt/arena
cmake --build artifacts/native-arena --config Release
cmake -S src/Services/SmartMetrix.DepthService/native -B artifacts/native-stereo -DSMARTMETRIX_WITH_OPENCV_CUDA=ON -DBUILD_TESTING=ON
cmake --build artifacts/native-stereo --config Release
ctest --test-dir artifacts/native-stereo -C Release --output-on-failure
```

Сохранить stdout CTest (median disparity, coverage, rejected occlusion), версии
SDK/OpenCV/CUDA и hash библиотек. Затем проверить `/ready` сервисов с настоящими
serial numbers, захват трёх кадров, временной skew, повторные захваты, отсутствие
роста памяти, сравнение CPU/CUDA и задержки на целевом разрешении. Stub-сборка
остаётся только диагностическим вариантом: ABI probe возвращает 0.

## Проверки в рабочей копии

Результаты финального прогона записаны в соседнем
`review-implementation-status-2026-09-30.json`. Браузерные тесты рабочих страниц
используют настоящие файлы GUI и HTTP fixtures контрактов, а тесты конвейера —
исполняемые .NET сервисы с HTTP и локальным хранилищем артефактов. Это не тест
реального оборудования и не полевая оценка точности. Инфраструктурные тесты
PostgreSQL/NATS/MinIO выполнены с реальными локальными сервисами, без пропусков.


## Повторное ревью и интеграционная проверка

Исправлены гонка двух snapshots настроек DepthService, переключение области GUI
во время применения настроек и запоздалые ответы конфигурации другой области.
Некорректные null frames/pixels Quality отклоняются до fingerprint; длина массива
включена в fingerprint. Crack HTTP response читается с ResponseHeadersRead до
проверки лимита. Production также валидирует фактический Segmentation backend,
когда ключ отсутствует в конфигурации. PostgreSQL-тест плоской поверхности теперь
проверяет неизвестный D50, а не выдуманный объём.
Чтение и запись JSON измерений синхронизированы общим per-measurement gate:
параллельное чтение больше не пересекается с заменой файла на Windows.
Добавлен stress test чтения состояния, списка и unfinished во время 200 замен.

Проверки: 284 .NET теста без пропусков, 46 браузерных тестов, Release build,
locked restore, dotnet format и git diff --check. Аппаратная приёмка и сборка
native SDK остаются отдельными проверками.

Локальный стенд использует PostgreSQL 18 на 127.0.0.1:5432 и отдельную БД
`smartmetrix_review_20260930_f4433d63`. Каждый PostgreSQL-тест создаёт и удаляет
свою БД `smartmetrix_test_<guid>`. NATS 2.11.0 слушает 127.0.0.1:14222,
monitoring/health — http://127.0.0.1:18222/healthz. MinIO слушает
http://127.0.0.1:19000, console — http://127.0.0.1:19001.
NATS streams и MinIO buckets имеют уникальные имена; fixture удаляет только
ресурсы, созданные соответствующим тестом.

Docker на этой машине отсутствует. NATS получен из официального release и
проверен SHA256; MinIO собран Go 1.24.6 из официального тега
`RELEASE.2025-07-23T15-54-02Z`, commit
`7ced9663e6a791fef9dc6be798ff24cda9c730ac`, поскольку Windows download недоступен.
Бинарники, данные, PID, логи и подключения находятся в игнорируемом
`artifacts/integration-stand/`. Пароли не включены в репозиторий.

Повторный прогон при работающем стенде:

```powershell
./tools/run-integration-tests.ps1 -NoBuild
```

Скрипт читает локальный `connections.local.json` и восстанавливает переменные
окружения после прогона. Для другого стенда передать `-ConnectionsFile` с JSON
полями `postgres`, `nats`, `minio`, `minioAccessKey`, `minioSecretKey`.
TRX сохраняется в `artifacts/integration-stand/test-results/full-integration.trx`.
При отсутствии внешних URL CI использует изолированные Testcontainers.
CI разрешён также для PR в `feature/stonevision-integration`; результаты
локальных проверок не означают успешный удалённый CI.
