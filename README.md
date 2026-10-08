# SmartMetrix

Edge-платформа для оценки блочности и трещиноватости карьерного борта по трёхкамерной мультибазисной стереосистеме.

Документация актуализирована по рабочему коду 04.10.2026. Начните с
[указателя документации](src/docs/README.md) и [описания проекта](PROJECT_DESCRIPTION.md).

## Принятые решения

- .NET 10 LTS и ASP.NET Core для микросервисов.
- Три синхронные камеры: базисы 0,7 м, 0,8 м и 1,5 м; объективы 16 мм.
- Тяжёлые Arena SDK, OpenCV CUDA, TensorRT и VPI подключаются через узкие native-адаптеры.
- NATS JetStream для событий, PostgreSQL/PostGIS для метаданных, MinIO для кадров и облаков точек.

Контракт объектного хранилища, структура ключей и локальный MinIO описаны в [src/docs/storage.md](src/docs/storage.md).
- Пространственные результаты хранятся в локальной правой системе координат карьера; каждый объект содержит `coordinateSystemId` и версию преобразования.

## Структура

- [Веб-интерфейс и пять АРМ](src/docs/web-gui.md) — целевые экраны, роли, права и требования к GUI.
- [Формальные требования GUI](src/docs/gui-requirements.md), [адаптивные макеты](src/docs/gui-prototype/index.html) и [браузерные тесты](tools/gui-tests/README.md).
- [API пяти АРМ на ASP.NET Core](src/docs/workstation-api.md) — контракт v1, права, источники, настройка, миграция и ограничения.
- [Реализация задач ревью 30.09.2026](src/docs/review-implementation-2026-09-30.md) — изменения, миграция конфигурации и оставшаяся аппаратная приёмка.
- [Телеметрия экскаватора, replay циклов и сменный отчёт](src/docs/excavator-replay.md) — локальный сценарий без оборудования, JSONL-контракт и ограничения первой версии (#25, #28, #35).
- [Веб-интерфейс экскаватора](src/docs/excavator-web.md) — локальное демо, фазы смены, циклы, CSV и доступ по областям.

- `src/BuildingBlocks` — доменная модель и версионируемые контракты сообщений.
- `src/Services` — независимо развёртываемые сервисы.
- `StoneVision` — Python-сервис YOLO + MobileSAM (Git submodule); [подключение к конвейеру](src/docs/stonevision.md).
- `src/docs/architecture.md` — поток измерения и ответственность сервисов.
- `docker-compose.yml` — локальная инфраструктура разработки.

## Запуск

Команды выполняются из корня проекта. Требуется .NET SDK из `global.json`
(10.0.100 с `rollForward: latestPatch`). Docker нужен для контейнерной инфраструктуры,
PowerShell 7 — для локальных скриптов Windows.

```powershell
dotnet restore SmartMetrix.sln --locked-mode
dotnet build SmartMetrix.sln --no-restore --configuration Release
docker compose up -d
```

Корневой Compose запускает NATS, PostgreSQL/PostGIS, MinIO и StorageService.
Для полного прикладного контура выберите профиль в
[инструкции локального развёртывания](src/docs/local-deployment.md).
Запуск одного оркестратора не запускает остальные этапы измерения;
их адреса и конфигурация описаны в [HTTP-конвейере](src/docs/measurement-pipeline.md).

Для отдельной демонстрации телеметрии и сменных отчётов без оборудования:

```powershell
pwsh -File deploy/local/start-excavation-web.ps1 -NoBuild
```

Интерфейс: `http://127.0.0.1:5191/excavation/`. Скрипт создаёт синтетический replay;
учётная запись оператора и локальные пароли находятся в
`artifacts/excavation-web/credentials.local.json`. Остановка:
`pwsh -File deploy/local/stop-excavation-web.ps1`.
Контракт, API и ограничения описаны в [excavator-replay.md](src/docs/excavator-replay.md).

## Локальная проверка

Требования и критерии приёмки всех 14 сервисов, существующие проверки и открытые
разрывы описаны в [src/docs/service-requirements.md](src/docs/service-requirements.md).
Прогоны по ID требования, инфраструктурные проверки и сбор покрытия описаны в
[src/docs/requirements-testing.md](src/docs/requirements-testing.md).

Тестовый захват с IP-камер Hikvision через RTSP/FFmpeg описан в
[`src/docs/rtsp-camera.md`](src/docs/rtsp-camera.md).

Утилита калибровки A/B/C по шаблону ChArUco, создание карт ректификации и экспорт
черновика описаны в [`tools/camera-calibration/README.md`](tools/camera-calibration/README.md).

Рабочий HTTP-конвейер, настройка установки и сквозные испытания описаны в
[`src/docs/measurement-pipeline.md`](src/docs/measurement-pipeline.md).

Базовая проверка .NET выполняется из корня репозитория:

```powershell
dotnet restore SmartMetrix.sln --locked-mode
dotnet format SmartMetrix.sln --no-restore --verify-no-changes
dotnet build SmartMetrix.sln --no-restore --configuration Release
dotnet test SmartMetrix.sln --no-build --configuration Release
```

Для интеграционных тестов требуется отдельная инфраструктура и
`SMARTMETRIX_RUN_INTEGRATION_TESTS=true`; см. [tests/infrastructure/README.md](tests/infrastructure/README.md).
CI дополнительно проверяет GUI, RTK-станцию и Python-утилиту калибровки;
команды собраны в [указателе документации](src/docs/README.md).

При намеренном изменении NuGet-зависимостей сначала обновите lock-файлы командой
`dotnet restore SmartMetrix.sln --force-evaluate`, затем добавьте их в тот же pull request.
Правила ветвления и обязательные проверки описаны в
[`src/docs/contributing.md`](src/docs/contributing.md).

Каждый сервис предоставляет `GET /health`, `GET /ready` и `GET /info`. Для разработки доступны
детерминированные симуляторы камер и полного измерительного конвейера. Интеграционные границы Arena SDK
и GPU-реконструкции реализованы как отдельные native-адаптеры; для работы с реальным оборудованием их
необходимо собрать с установленными vendor SDK и включить соответствующий backend в конфигурации.
Подробности приведены в [`src/docs/camera.md`](src/docs/camera.md),
[`src/docs/depth-native.md`](src/docs/depth-native.md) и
[`src/docs/local-deployment.md`](src/docs/local-deployment.md).

Локальный профиль развёртывания использует демонстрационные ключи и пароли и слушает только loopback.
Эти значения нельзя переносить в edge или production: там секреты должны передаваться через переменные
окружения или внешний secret store.

## Общий bootstrap и наблюдаемость

Все микросервисы подключают `SmartMetrix.ServiceDefaults`, который предоставляет:

- liveness `/health`, readiness `/ready` и сведения о сервисе `/info`;
- Problem Details для необработанных ошибок;
- структурированный scope логов с `CorrelationId` и `MeasurementId`;
- OpenTelemetry для HTTP, `HttpClient`, runtime и обработчиков событий;
- проверку конфигурации при старте, единые HTTP-таймауты и graceful shutdown.

Поддерживаемые настройки:

| Настройка | Назначение | По умолчанию |
|---|---|---:|
| `SmartMetrix__ServiceName` | Имя ресурса OTel; автоматически берётся из assembly | имя приложения |
| `SmartMetrix__HttpClientTimeoutSeconds` | Таймаут исходящих HTTP-запросов | 30 |
| `SmartMetrix__ShutdownTimeoutSeconds` | Время graceful shutdown | 30 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Включает экспорт traces, metrics и logs по OTLP | отключён |

Корреляция принимается через `X-Correlation-ID`, контекст измерения — через `X-Measurement-ID`. Секреты задаются только через переменные окружения или внешний secret store и не должны добавляться в `appsettings*.json`.
