# CLI и конфигурация

## Команды `bluetti-watcher.exe`

Сборка с `windows_subsystem = "windows"`: окна консоли нет. Команды, которые печатают текст (`scan`, `read-once`,
`install`, `status`), подключаются к консоли родителя (`AttachConsole(ATTACH_PARENT_PROCESS)`).

| Команда | Что делает | Exit |
|---|---|---|
| `run [--dry-run]` | сервис: цикл чтения, решения, HTTP. Без аргументов = `run`. Второй экземпляр выходит сразу | 0 / 3 уже запущен |
| `open` | окно дашборда; если `/api/health` не отвечает 3 с — запускает задачу `BluettiWatcher` и ждёт до 10 с | 0 / 1 |
| `scan` | 10 с слушает рекламу BLE, печатает найденные BLUETTI: имя, адрес, RSSI | 0 / 1 нет адаптера |
| `read-once [--mac ..] [--trace]` | одно чтение, печать полей; `--trace` — каждый кадр hex (ключи не печатаются) | 0 / 1 нет ответа |
| `install [--mac AA:BB:..] [--port N] [--elevated-setup]` | без `--mac` — сканирует и берёт единственную найденную станцию (несколько — печатает список и выходит с 1); копирует exe в `%LOCALAPPDATA%\bluetti-watcher\bin`, пишет `config.toml`, создаёт схему «Bluetti Eco», задачу автозапуска, ярлыки (Рабочий стол, «Пуск» с AUMID), запускает сервис. `--elevated-setup` запрашивает UAC один раз: `powercfg /h on` и задача `BluettiWatcherGpu` | 0 / 1 |
| `uninstall [--purge]` | снимает eco, останавливает сервис, удаляет задачи, ярлыки, схему; `--purge` — и данные | 0 / 1 |
| `status` | одна строка: сервис, сеть, заряд, eco | 0 |
| `gpu-apply` | только из задачи `BluettiWatcherGpu` (элевировано): читает `gpu-request`, ставит лимит GPU | 0 / 1 / 2 нет NVIDIA |

## config.toml

`%LOCALAPPDATA%\bluetti-watcher\config.toml`. Отсутствующий ключ = значение по умолчанию. Обязателен только `mac`.

```toml
mac = "AA:BB:CC:DD:EE:FF"
port = 8765
poll_sec = 60
dry_run = false

[station]
capacity_wh = 1440          # AC180P (1440 Вт·ч, 1800 Вт)
efficiency = 0.85

[mains]
lost_sec = 180
back_sec = 180

[levels]
low = 20                    # глубокий eco + уведомление
critical = 10               # уведомление «сохраните работу»
warn = 7                    # предупреждение о гибернации
hibernate = 5
agree_sec = 90
hibernate_after_warn_sec = 180
rebase_confirm_sec = 600

[eco]
power_scheme = true         # схема «Bluetti Eco»
cpu_max_pct = 80
deep_cpu_max_pct = 50
efficiency_overlay = true   # режим «наилучшая энергоэффективность» Windows
monitor_off_sec = 180
gpu = true                  # действует, только если есть задача BluettiWatcherGpu
gpu_pct = 70                # от номинала, не ниже минимального лимита карты
deep_gpu_pct = 0            # 0 = минимальный лимит карты

[guards]
soc_rate_per_min = 5
soc_jump_floor = 3
max_gap_sec = 600
```

Неверное значение (вне диапазона, не тот тип) → сервис пишет ошибку в лог и событие и стартует со значением по
умолчанию для этого ключа; неверный или пустой `mac` → сервис не читает станцию, дашборд показывает причину.

## Файлы в `%LOCALAPPDATA%\bluetti-watcher`

`bin\bluetti-watcher.exe`, `config.toml`, `station.db`, `logs\service.log` (+ `.1`), `no-hibernate` (выключатель).
`%ProgramData%\bluetti-watcher\gpu-request` — одна строка `normal|eco|deep` (запись пользователю, чтение задаче).
