# SmartMetrix

Edge-платформа для оценки блочности и трещиноватости карьерного борта по трёхкамерной мультибазисной стереосистеме.

## Принятые решения

- .NET 10 LTS и ASP.NET Core для микросервисов.
- Три синхронные камеры: базисы 0,7 м, 0,8 м и 1,5 м; объективы 16 мм.
- Тяжёлые Arena SDK, OpenCV CUDA, TensorRT и VPI подключаются через узкие native-адаптеры.
- NATS JetStream для событий, PostgreSQL/PostGIS для метаданных, MinIO для кадров и облаков точек.
- Пространственные результаты хранятся в локальной правой системе координат карьера; каждый объект содержит `coordinateSystemId` и версию преобразования.

## Структура

- `src/BuildingBlocks` — доменная модель и версионируемые контракты сообщений.
- `src/Services` — независимо развёртываемые сервисы.
- `docs/architecture.md` — поток измерения и ответственность сервисов.
- `docker-compose.yml` — локальная инфраструктура разработки.

## Запуск

```powershell
dotnet restore
dotnet build --no-restore
docker compose up -d
dotnet run --project src/Services/SmartMetrix.MeasurementOrchestrator
```

Каждый сервис предоставляет `GET /health`, `GET /ready` и `GET /info`. Аппаратные и ML-операции намеренно ещё не эмулируют реальные измерения: следующий этап — подключение SDK и запись интеграционных адаптеров.

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
