# Документация SmartMetrix

Обновлено 04.10.2026 по исходникам и конфигурации рабочего каталога. Документы
описывают текущую реализацию и отдельно указывают целевые требования и ограничения.
Отчёты с датами фиксируют отдельный этап работ и не заменяют актуальные инструкции.

## С чего начать

| Задача | Документ |
|---|---|
| Понять назначение, состав и ограничения | [Описание проекта](../../PROJECT_DESCRIPTION.md) |
| Собрать проект и выбрать запуск | [README проекта](../../README.md), [локальное развёртывание](local-deployment.md) |
| Проследить обработку измерения | [Архитектура](architecture.md), [HTTP-конвейер](measurement-pipeline.md) |
| Настроить интерфейс и доступ | [API пяти АРМ](workstation-api.md), [аутентификация и операторский API](operator-api.md) |
| Воспроизвести телеметрию и открыть сменный отчёт | [Replay экскаватора](excavator-replay.md) |
| Проверить требования и подготовить приёмку | [Требования сервисов](service-requirements.md), [проверки требований](requirements-testing.md), [E2E-пилот](e2e-pilot.md) |

## Сценарии запуска

Все команды выполняются из корня проекта после `dotnet restore SmartMetrix.sln --locked-mode`.
Скрипты с `-NoBuild` требуют предварительного Release build.

| Сценарий | Команда | Условия и результат |
|---|---|---|
| Контейнерная инфраструктура | `docker compose up -d` | Docker; NATS, PostgreSQL/PostGIS, MinIO и StorageService, без остальных 13 сервисов |
| Штатное локальное развёртывание | `pwsh -File deploy/local/deploy.ps1` | Публикация в `C:\DEPLOY\SmartMetrix`; затем `start.ps1`, `status.ps1`, `stop.ps1` в каталоге развёртывания |
| Запуск из рабочего каталога | `pwsh -File deploy/local/start-workspace.ps1` | 14 сервисов и MinIO/NATS; нужны инфраструктурные бинарники в `C:\DEPLOY\SmartMetrix\infrastructure`; данные в `data/workspace-run` |
| Стенд ревью | `pwsh -File deploy/local/start-review-stand.ps1` | Существующие PostgreSQL/NATS/MinIO и `artifacts/integration-stand/connections.local.json`; подробности в [review-stand.md](review-stand.md) |
| Веб-отчёты экскаватора | `pwsh -File deploy/local/start-excavation-web.ps1` | Синтетический replay и отдельный Gateway на порту 5191; остановка `stop-excavation-web.ps1` |
| Консольный replay | `dotnet run --project tools/SmartMetrix.ExcavatorReplay --configuration Release` | JSON/CSV и manifest в `artifacts/excavator-replay`, без камер и брокера |

Штатный Gateway использует порт 5190, отдельная демонстрация экскаватора — 5191.
Успешный `/health` подтверждает работу процесса; `/ready` проверяет его готовность.
Стенд с отключённым pipeline или недоступной камерой может отвечать 503 на `/ready`.
Локальные файлы с паролями и подключениями не входят в документацию.

## Сервисы и данные

| Область | Документы |
|---|---|
| Камеры, RTSP и калибровка | [Камеры](camera.md), [RTSP](rtsp-camera.md), [калибровка ChArUco](../../tools/camera-calibration/README.md) |
| Глубина и ректификация | [Глубина](depth.md), [native backend](depth-native.md), [ректификация](pipeline-rectification.md), [сравнение глубины](../../tools/camera-calibration/DEPTH-COMPARISON.md) |
| Сегментация и анализ | [Сегментация](segmentation.md), [API сегментации](segmentation-api.md), [StoneVision](stonevision.md), [анализ блоков](block-analysis.md) |
| Пространственная привязка | [Позиционирование](local-positioning.md), [опорные точки](control-points.md), [геопривязка](georeference.md), [RTK-станция](../../tools/rtk-base-station/README.md) |
| Хранение и обмен | [Хранилище](storage.md), [слой данных](data-layer.md), [события](messaging.md), [облачная синхронизация](cloud-sync.md) |
| Развёртывание | [Windows](local-deployment.md), [edge](edge-deployment.md) |
| Интерфейсы | [Целевые пять АРМ](web-gui.md), [требования GUI](gui-requirements.md), [макеты](gui-prototype/index.html), [браузерные проверки](../../tools/gui-tests/README.md) |

## Проверки

Базовая проверка .NET:

```powershell
dotnet restore SmartMetrix.sln --locked-mode
dotnet format SmartMetrix.sln --no-restore --verify-no-changes
dotnet build SmartMetrix.sln --no-restore --configuration Release
dotnet test SmartMetrix.sln --no-build --configuration Release
```

Интеграционный режим требует настройки из [инструкции инфраструктуры тестов](../../tests/infrastructure/README.md).
Без него базовый прогон не эквивалентен полному CI.

Браузерные проверки из `tools/gui-tests`:

```powershell
pnpm install --frozen-lockfile
pnpm exec playwright install chromium
pnpm test
```

`pnpm test` включает макеты, подключённые операторскую/инженерную панели и экран
экскаватора с тестовыми ответами API. Это не проверка реального оборудования.
Дополнительные команды из корня проекта:

```powershell
node --test tools/rtk-base-station/test/station.test.mjs
python -m pip install -r tools/camera-calibration/requirements.txt
dotnet build tools/SmartMetrix.DepthComparison --configuration Release
python -m unittest discover -s tools/camera-calibration -p 'test_*.py' -v
```

Полный состав CI задан в [ci.yml](../../.github/workflows/ci.yml).
Правила изменений и lock-файлов — в [contributing.md](contributing.md).

## Состояние и приёмка

В текущем коде доступны 14 сервисов, HTTP-конвейер, файловые и PostgreSQL-хранилища,
native-границы, API пяти ролей, подключённые операторская и инженерная панели,
а также просмотр сменных отчётов экскаватора. Макеты остальных АРМ и целевые
возможности описаны отдельно от реализованных операций API.

Измерение считается тестовым, если захват не подтверждает `adapter: Arena`,
либо захват или сегментация не содержат явного `isTestData: false`.
Для replay используется самостоятельный признак `isSynthetic`.
Эти признаки описывают происхождение данных и не подтверждают полевую точность.

Промышленная приёмка требует целевых камер, калибровки, проверенной модели,
аппаратных/GPU-бэкендов и сквозных полевых испытаний. Телеметрия экскаватора пока
не имеет прямого CAN/OEM-адаптера, измеренной массы и связи с картой породы.

История работ: [ревью интерфейса и backend](review-frontend-backend-2026-09-30.md),
[реализация ревью](review-implementation-2026-09-30.md),
[план сервисов](service-completion-plan-2026-09-30.md),
[задачи экскаватора от 04.10.2026](excavator-tickets-2026-10-04.md).
