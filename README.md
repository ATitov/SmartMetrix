# SmartMetrix

Edge-платформа для оценки блочности и трещиноватости карьерного борта по трёхкамерной мультибазисной стереосистеме.

## Принятые решения

- .NET 10 LTS и ASP.NET Core для микросервисов.
- Три синхронные камеры: базисы 0,7 м, 0,8 м и 1,5 м; объективы 16 мм.
- Тяжёлые Arena SDK, OpenCV CUDA, TensorRT и VPI подключаются через узкие native-адаптеры.
- NATS JetStream для событий, PostgreSQL/PostGIS для метаданных, MinIO для кадров и облаков точек.

Контракт объектного хранилища, структура ключей и локальный MinIO описаны в [docs/storage.md](docs/storage.md).
- Пространственные результаты хранятся в локальной правой системе координат карьера; каждый объект содержит `coordinateSystemId` и версию преобразования.

## Структура

- [Веб-интерфейс и пять АРМ](docs/web-gui.md) — целевые экраны, роли, права и требования к GUI.
- [Формальные требования GUI](docs/gui-requirements.md), [адаптивные макеты](docs/gui-prototype/index.html) и [браузерные тесты](tools/gui-tests/README.md).
- [API пяти АРМ на ASP.NET Core](docs/workstation-api.md) — контракт v1, права, источники, настройка, миграция и ограничения.
- [Реализация задач ревью 30.09.2026](docs/review-implementation-2026-09-30.md) — изменения, миграция конфигурации и оставшаяся аппаратная приёмка.

- `src/BuildingBlocks` — доменная модель и версионируемые контракты сообщений.
- `src/Services` — независимо развёртываемые сервисы.
- `StoneVision` — Python-сервис YOLO + MobileSAM (Git submodule); [подключение к конвейеру](docs/stonevision.md).
- `docs/architecture.md` — поток измерения и ответственность сервисов.
- `docker-compose.yml` — локальная инфраструктура разработки.

## Запуск

```powershell
dotnet restore
dotnet build --no-restore
docker compose up -d
dotnet run --project src/Services/SmartMetrix.MeasurementOrchestrator
```

## Локальная проверка

Требования и критерии приёмки всех 14 сервисов, существующие проверки и открытые
разрывы описаны в [docs/service-requirements.md](docs/service-requirements.md).
Прогоны по ID требования, инфраструктурные проверки и сбор покрытия описаны в
[docs/requirements-testing.md](docs/requirements-testing.md).

Тестовый захват с IP-камер Hikvision через RTSP/FFmpeg описан в
[`docs/rtsp-camera.md`](docs/rtsp-camera.md).

Утилита калибровки A/B/C по шаблону ChArUco, создание карт ректификации и экспорт
черновика описаны в [`tools/camera-calibration/README.md`](tools/camera-calibration/README.md).

Рабочий HTTP-конвейер, настройка установки и сквозные испытания описаны в
[`docs/measurement-pipeline.md`](docs/measurement-pipeline.md).

Полная проверка, эквивалентная CI, выполняется из корня репозитория:

```powershell
dotnet restore SmartMetrix.sln --locked-mode
dotnet format SmartMetrix.sln --no-restore --verify-no-changes
dotnet build SmartMetrix.sln --no-restore --configuration Release
dotnet test SmartMetrix.sln --no-build --configuration Release
```

При намеренном изменении NuGet-зависимостей сначала обновите lock-файлы командой
`dotnet restore SmartMetrix.sln --force-evaluate`, затем добавьте их в тот же pull request.
Правила ветвления и обязательные проверки описаны в
[`docs/contributing.md`](docs/contributing.md).

Каждый сервис предоставляет `GET /health`, `GET /ready` и `GET /info`. Для разработки доступны
детерминированные симуляторы камер и полного измерительного конвейера. Интеграционные границы Arena SDK
и GPU-реконструкции реализованы как отдельные native-адаптеры; для работы с реальным оборудованием их
необходимо собрать с установленными vendor SDK и включить соответствующий backend в конфигурации.
Подробности приведены в [`docs/camera.md`](docs/camera.md),
[`docs/depth-native.md`](docs/depth-native.md) и
[`docs/local-deployment.md`](docs/local-deployment.md).

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
