# Проверка требований

Каталог: [service-requirements.md](service-requirements.md). ID требований связаны
с конкретными тестами через `[Trait("Requirement", "CAM-01")]`. Метка означает
проверку указанного сценария, а не полную приёмку всех условий требования.

## Воспроизводимый прогон

```powershell
dotnet restore SmartMetrix.sln --locked-mode
dotnet format SmartMetrix.sln --no-restore --verify-no-changes
dotnet build SmartMetrix.sln --no-restore --configuration Release
dotnet test SmartMetrix.sln --no-build --configuration Release --collect:"XPlat Code Coverage" --logger "trx;LogFileName=requirements.trx" --results-directory TestResults/requirements
```

Для выбранного требования:

```powershell
dotnet test SmartMetrix.sln --no-build --configuration Release --filter "Requirement=CAM-01"
```

Пустая выборка не подтверждает требование: сначала проверить, что нужные тесты
обнаружены. Каталог явно отмечает ещё не покрытые сценарии.

Сводка всех сервисов по одному полученному файлу Cobertura:

```powershell
./tools/report-service-coverage.ps1 -CoveragePath TestResults/requirements/<run-id>/coverage.cobertura.xml
```

Отсутствующий в отчёте сервис обозначается `not measured`. Покрытие относится к
инструментированным сборкам тестового процесса. HTTP E2E запускает сервисы
дочерними процессами, поэтому его выполнение нельзя полностью оценить по этому
отчёту. Native C++, Python, JavaScript и deployment-скрипты в него не входят.

## Инфраструктура и вспомогательные инструменты

Для реальных проверок MinIO и NATS нужен работающий Docker:

```powershell
$env:SMARTMETRIX_RUN_INTEGRATION_TESTS = 'true'
dotnet test SmartMetrix.sln --no-build --configuration Release --filter "Category=Integration"
```

Без флага тесты явно помечаются skipped. С флагом отсутствие Docker приводит к
ошибке, а не фиктивному успеху. CI включает флаг и сохраняет TRX/Cobertura.

```powershell
node --test tools/rtk-base-station/test/station.test.mjs
python -m pip install -r tools/camera-calibration/requirements.txt
python -m unittest discover -s tools/camera-calibration -p 'test_*.py' -v
```

Python-зависимости устанавливаются в выделенное окружение. Для сравнения с .NET
предварительно собирается `tools/SmartMetrix.DepthComparison` в Release, как в CI.

## Работа с новым требованием

1. Записать наблюдаемое поведение, входы, ошибки и критерий приёмки с постоянным ID.
2. Добавить сценарии успеха, границ и существенных отказов; пометить соответствующие тесты ID.
3. Если тест выявляет дефект, сохранить воспроизведение и исправить причину.
4. Выполнить профильные проверки и общий прогон; обновить колонку «Проверка».
5. Для аппаратных, геодезических и эксплуатационных требований приложить отдельный отчёт.

Не вводить произвольный процент покрытия как замену функциональной приёмке.
Непокрытые требования остаются открытыми, даже если все имеющиеся тесты прошли.

## Результат локальной проверки 30.09.2026

- Locked restore, Release build и проверка форматирования прошли; сборка без предупреждений.
- .NET: 168 passed, 2 skipped, 0 failed; добавлено 40 случаев относительно исходных 130.
  Две инфраструктурные проверки раньше возвращались без выполнения и учитывались как passed.
- RTK: 6 passed. Docker отсутствует; MinIO/NATS локально не подтверждены.
- Python: тесты не загрузились из-за отсутствующего `cv2` в доступном runtime.
- Исправлен SYN-04: повтор после частичной записи spool теперь проходит; регрессионный тест сохранён.
- В одном предварительном HTTP E2E прогоне возник `Access to the path is denied`
  на этапе Georeferencing; повторный полный прогон прошёл. Причина единичного
  отказа файлового доступа не установлена, устойчивость к нему не подтверждена.

Локальные артефакты: `TestResults/requirements-verified/requirements.trx` и
`TestResults/requirements-verified/<run-id>/coverage.cobertura.xml` (не входят в Git).
CI обновлён, но его удалённый запуск в рамках этой работы не выполнялся.

| Сервис | Покрытие строк | Покрытие ветвей |
|---|---:|---:|
| ApiGateway | 23.8% | 39.4% |
| BlockAnalysisService | 89.2% | 81.8% |
| CalibrationService | 65.7% | 43.0% |
| CameraService | 48.8% | 55.4% |
| CloudSyncService | 64.2% | 61.2% |
| ControlPointService | 61.7% | 49.3% |
| DepthService | 80.0% | 74.2% |
| GeoreferenceService | 86.2% | 82.6% |
| LocalPositioningService | 72.4% | 58.3% |
| MeasurementOrchestrator | 8.8% | 2.9% |
| QualityService | 82.3% | 65.4% |
| SegmentationService | 79.3% | 74.8% |
| StorageService | 8.0% | 13.6% |
| TriggerService | 75.5% | 82.8% |

Это измерение тестового процесса с указанными выше ограничениями, а не процент
готовности сервисов. Полное покрытие кода и требований ещё не достигнуто.
