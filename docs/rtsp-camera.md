# Тестовый захват с IP-камер по RTSP

Адаптер `Rtsp` получает один кадр через FFmpeg по RTSP/TCP, декодирует его в Mono8
без изменения разрешения. Поддерживаются отдельный снимок A/B/C и набор из трёх камер.
Каждый запрос открывает новое соединение; это инструмент неподвижного стенда, не непрерывная запись.
FFmpeg устанавливается отдельно и должен поддерживать RTSP и кодек камеры.

## Один снимок без других сервисов

Пример для Hikvision DS-2CD2T63G2-4LI2U. Основной поток обычно доступен по
`/Streaming/Channels/101`; фактический адрес и разрешение проверьте на своей камере.
Рекомендуемый начальный профиль: H.264 без H.264+, 3200×1800, одинаковые настройки,
дневной режим, стабильное освещение, отключённый WDR и умеренное шумоподавление.

В PowerShell из корня проекта:

```powershell
$env:Camera__Adapter = 'Rtsp'
$env:Camera__Rtsp__TestMode = 'true'
$env:Camera__Rtsp__FfmpegPath = 'C:\Tools\ffmpeg\bin\ffmpeg.exe'
$env:Camera__CaptureTimeoutMilliseconds = '20000'
$credential = Get-Credential -Message 'Учётная запись для чтения потока камеры A'
$rtspUser = [Uri]::EscapeDataString($credential.UserName)
$rtspPassword = [Uri]::EscapeDataString($credential.GetNetworkCredential().Password)
$cameraHost = Read-Host 'IP камеры A'
$env:Camera__Rtsp__CameraAUrl = "rtsp://${rtspUser}:${rtspPassword}@${cameraHost}:554/Streaming/Channels/101"
dotnet run --project src/Services/SmartMetrix.CameraService --no-launch-profile --urls http://127.0.0.1:5102
```

В другом терминале:

```powershell
Invoke-WebRequest -Method Post -Uri 'http://127.0.0.1:5102/v1/test/cameras/A/capture' -OutFile camera-a.pgm
```

Получится бинарный PGM: заголовок с фактическими размерами и пиксели grayscale.
Его можно открыть в поддерживающем PGM редакторе или преобразовать:

```powershell
& 'C:\Tools\ffmpeg\bin\ffmpeg.exe' -i camera-a.pgm camera-a.png
```

Для этого endpoint не нужны StorageService, калибровка, серийные номера и камеры B/C.
Заголовки ответа: `X-Is-Test-Data: true`, `X-Timestamp-Source: HostReceive`, `X-Received-At`.
Endpoint доступен напрямую в локальном CameraService; интеграция с панелью gateway пока не добавлена.

## Набор A/B/C с сохранением

До запуска сервиса аналогично задайте секретные переменные `Camera__Rtsp__CameraBUrl`
и `Camera__Rtsp__CameraCUrl`. Все три потока должны иметь одинаковые размеры.
Запустите StorageService с его хранилищем и задайте `Camera__StorageServiceUrl`, если он
отличается от `http://localhost:5105`. Затем:

```powershell
$captureId = [guid]::NewGuid()
$capture = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5102/v1/measurements/$captureId/capture" -ContentType 'application/json' -Body '{}'
$capture | ConvertTo-Json -Depth 8
```

Открытие трёх потоков происходит параллельно, но это не синхронизация экспозиций.
API возвращает ссылки на сохранённые packed Mono8 `.raw`, контрольные суммы, размеры
и время получения каждого кадра на хосте. Это декодированные изображения из сжатого
видеопотока, а не оригинальные RAW сенсора; provenance — `camera-service:rtsp-decoded-test`.
Повтор успешного запроса с тем же ID возвращает сохранённую квитанцию. После неудачного
или прерванного захвата используйте новый ID (существующий механизм `CaptureOutcomeUnknown`).

## Метки времени и ограничения

- RTSP включается только явно: `Adapter=Rtsp`, `Rtsp:TestMode=true`, `PixelFormat=Mono8`.
- `isTestData=true`; `hardwareTimestampNanoseconds=0` означает отсутствие аппаратного времени.
  `timestampSource=HostReceive`, `receivedAt` — время получения декодированного кадра.
  `timestampSkewNanoseconds=null` и `exposedAt=null`: одновременность и момент экспозиции неизвестны.
- Оркестратор сохраняет результат захвата, но прекращает обработку с `UnsynchronizedTestCapture`.
  Флаг `FramesAreRectified` не снимает это ограничение. Для измерений нужны отдельная
  калибровка/ректификация и согласованный режим позиционирования статического стенда.
- Таймаут ограничивает захват; отмена завершает дочерние процессы FFmpeg. Частичный набор не сохраняется.
  Число пикселей ограничено `Camera__Rtsp__MaximumPixels` (по умолчанию 12 000 000).
- При отсутствии FFmpeg возвращается `NotConfigured`, при отказе потока — `RtspCaptureFailed`,
  при таймауте — `CaptureTimeout`. Сырые ошибки FFmpeg не выводятся, так как могут содержать пароль.
- URL храните в окружении, не в Git. Он передаётся FFmpeg аргументом и может быть виден
  локальному администратору в списке процессов. Тестовый сервис запускайте на loopback.

Для принятой рамы: A—B = 0,25 м, B—C = 0,75 м, A—C = 1 м.
Геометрия не зашита в адаптер: её фактические значения нужно сохранить в калибровке.

Проверки без оборудования:

```powershell
dotnet test tests/SmartMetrix.ArchitectureTests/SmartMetrix.ArchitectureTests.csproj --configuration Release --filter FullyQualifiedName~RtspCameraTests
```

Для аппаратной проверки: снимок каждой камеры, набор A/B/C, неверный пароль,
отключение одной камеры, отмена запроса и повтор захвата. Проверьте фактические размеры,
отсутствие оставшихся процессов FFmpeg и отсутствие учётных данных в логах.

Справочники: [FFmpeg RTSP](https://ffmpeg.org/ffmpeg-protocols.html#rtsp),
[Hikvision datasheet](https://assets.hikvision.com/prd/public/all/doc/sm000064853/DS-2CD2T63G2-2LI2U_4LI2U_Datasheet_20250715.pdf).
