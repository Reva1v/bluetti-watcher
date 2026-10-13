<div align="center">

# Bluetti Watcher

**Следит за станцией BLUETTI AC180P в фоне и защищает ПК при отключении света: eco-режим, а перед разрядом — гибернация.**

Один exe на Rust: раз в минуту читает станцию по Bluetooth LE, без окна и значка в трее; графики — по ярлыку.

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![Rust](https://img.shields.io/badge/Rust-2024-B7410E?logo=rust&logoColor=white)
![Station](https://img.shields.io/badge/BLUETTI-AC180P-FF6F1A)
![License](https://img.shields.io/badge/license-MIT-green)

</div>

---

## Возможности

| | |
|---|---|
| 🔋 **Чтение станции по Bluetooth** | Раз в минуту: заряд, вход и выход AC/DC, напряжение сети. Только чтение — настройки станции программа не меняет никогда. |
| 🌙 **Невидимо в фоне** | Без окна, консоли и трея; запускается при входе в Windows без прав администратора. В простое — около 0 % CPU и единицы мегабайт памяти. |
| 📈 **Дашборд по ярлыку** | Отдельное окно: заряд, сколько осталось работать, мощности, графики за 6 ч / 24 ч / 7 д / 30 д, отключения и события. |
| ⚡ **Eco при отключении света** | Через 3 минуты без сети: своя схема питания «Bluetti Eco» (CPU до 80 %, монитор гаснет через 3 мин, режим энергоэффективности) и лимит мощности видеокарты NVIDIA. На 20 % — глубокий eco. |
| 🛌 **Гибернация до разряда** | Уведомление на 7 %, гибернация на 5 % — только после свежего подтверждающего показания. При быстром разряде пороги поднимаются заранее. |
| 🎛 **Ручной режим** | Кнопки «Авто», «Eco», «Максимум» на дашборде. |

## Как это реагирует

| Когда | Что происходит |
|---|---|
| Сети нет 3 минуты | Начинается отключение, включается eco |
| Заряд 20 % | Глубокий eco: CPU до 50 %, GPU до минимального лимита, уведомление |
| Заряд 10 % | Уведомление «сохраните работу» |
| Заряд 7 % | Уведомление: ПК уйдёт в гибернацию примерно через N минут |
| Заряд 5 % | Гибернация, если свежее показание это подтверждает |
| Сеть вернулась на 3 минуты | Прежняя схема питания и лимит GPU |

Уровень срабатывает только после двух согласных показаний. Скачок заряда (сбой или перекалибровка станции) отвергается,
а если низкий заряд держится — гибернация ждёт 10 минут подтверждения. Все пороги настраиваются.

## Установка

Готовых сборок пока нет — собирается из исходников (см. ниже), затем:

```powershell
.\target\release\bluetti-watcher.exe scan | Out-Host                            # найти станцию: имя, адрес, сигнал
.\target\release\bluetti-watcher.exe install --elevated-setup | Out-Host        # установить (станция найдётся сама)
.\target\release\bluetti-watcher.exe install --mac AA:BB:CC:DD:EE:FF | Out-Host # или указать адрес явно
```

`| Out-Host` нужен потому, что exe собран без консоли: так PowerShell дождётся вывода команды.

`install` делает:
- копирует exe в `%LOCALAPPDATA%\bluetti-watcher\bin\` и пишет `config.toml` с адресом станции;
- создаёт схему питания «Bluetti Eco» (ваши схемы не меняются);
- регистрирует задачу планировщика `BluettiWatcher` — запуск при входе, без прав администратора, перезапуск при сбое;
- создаёт ярлыки «Bluetti Watcher» на Рабочем столе и в меню «Пуск»;
- запускает сервис.

`--elevated-setup` один раз запрашивает UAC: включает гибернацию Windows (`powercfg /h on`) и создаёт задачу
`BluettiWatcherGpu` для лимита мощности видеокарты. Без него eco работает без GPU, а дашборд предупредит, если
гибернация выключена.

## Использование

- **Дашборд** — ярлык «Bluetti Watcher». Закрытие окна сервис не останавливает. Если сервис не запущен, ярлык запустит его.
- **Состояние одной строкой** — `bluetti-watcher status | Out-Host`.
- **Проверка связи** — `bluetti-watcher read-once --trace | Out-Host` печатает поля и кадры протокола.
- **Выключатель гибернации** — пустой файл `%LOCALAPPDATA%\bluetti-watcher\no-hibernate`; проверяется на каждом цикле.
- **Пробный режим** — `dry_run = true` в `config.toml` (или `run --dry-run`): все действия над ПК только пишутся в лог.
- **Удаление** — `bluetti-watcher uninstall | Out-Host` возвращает схему питания и убирает задачи, ярлыки и схему
  «Bluetti Eco»; данные остаются. `--purge` удаляет и их.

## Настройка

`%LOCALAPPDATA%\bluetti-watcher\config.toml`. Обязателен только `mac`; остальное — значения по умолчанию:

```toml
mac = "AA:BB:CC:DD:EE:FF"
port = 8765                 # дашборд на 127.0.0.1
poll_sec = 60
dry_run = false

[station]
capacity_wh = 1440          # AC180P
efficiency = 0.85

[mains]
lost_sec = 180              # сколько ждать без сети до eco
back_sec = 180

[levels]
low = 20                    # глубокий eco
critical = 10
warn = 7
hibernate = 5

[eco]
power_scheme = true
cpu_max_pct = 80
deep_cpu_max_pct = 50
efficiency_overlay = true
monitor_off_sec = 180
gpu = true
gpu_pct = 70                # от номинала, не ниже минимального лимита карты
deep_gpu_pct = 0            # 0 = минимальный лимит
```

Полный список ключей и пределы — [contracts/cli-config.md](specs/001-rust-station-watcher/contracts/cli-config.md).

## Безопасность

- К станции уходят только команды чтения Modbus (0x03) и шаги защищённого соединения; тест проверяет каждый исходящий кадр.
- HTTP слушает только `127.0.0.1` и отклоняет запросы с чужим `Host`; смена режима требует заголовка, который другие
  сайты отправить не могут.
- Данные не покидают ПК: облака BLUETTI и внешних сервисов нет.

## Сборка из исходников

Нужны Windows 10/11 x64 с Bluetooth LE и [Rust](https://rustup.rs/) stable (1.97+).

```powershell
cargo build --release      # target\release\bluetti-watcher.exe
cargo test                 # протокол, решения, хранилище, API
```

## Архитектура

Сервис однопоточный (tokio current_thread): цикл чтения раз в минуту, решения — чистая функция без I/O,
действия над ПК — через трейты, которые в тестах подменяются фейками.

```mermaid
flowchart LR
    ST[AC180P] -- BLE GATT<br/>Modbus 0x03 --> BLE[ble<br/>WinRT]
    BLE --> SVC[service<br/>цикл 60 с]
    SVC --> DEC[decision<br/>сеть · уровни · гибернация]
    DEC --> SVC
    SVC --> PWR[power<br/>схема · GPU · гибернация]
    SVC --> DB[(SQLite<br/>station.db)]
    SVC --> HTTP[http<br/>127.0.0.1 · SSE]
    HTTP --> WIN[open<br/>окно WebView2]
```

| Слой | Технологии |
|---|---|
| Bluetooth и Windows API | `windows` (WinRT GATT, powrprof, Task Scheduler, Shell) |
| Протокол | Modbus RTU, шифрование BLUETTI IoT v2 на RustCrypto (`p256`, `aes`, `cbc`, `md-5`) |
| Хранилище | `rusqlite` (SQLite, bundled) |
| API и дашборд | `axum`, SSE, HTML/CSS/JS без фреймворков, графики uPlot |
| Окно | `wry` + `tao` (WebView2) |
| GPU и уведомления | `nvml-wrapper`, `tauri-winrt-notification` |

## Структура проекта

```
src/ble/           чтение станции: WinRT-транспорт, Modbus, шифрование, карта регистров AC180P, сканирование
src/decision/      решения: проверки показаний, сеть и отключения, уровни и гибернация
src/power/         eco: схема «Bluetti Eco», оверлей, лимит GPU, гибернация
src/http/          API, SSE и встроенный дашборд (web/)
src/service.rs     цикл сервиса
src/install.rs     install / uninstall / status
src/window.rs      окно дашборда
tests/             протокол против фейковой станции, сценарии решений
specs/             спецификация, план, задачи (Spec Kit)
design/            макеты дашборда
assets/            иконка
```

## Разработка

```powershell
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test                                   # 138 тестов
cargo test --lib -- --ignored overlay_spike  # проверка оверлея питания на этом ПК (обратимо)
```

Фичи ведутся по Spec Kit: [`specs/001-rust-station-watcher/`](specs/001-rust-station-watcher/) — спецификация,
план, исследование протокола, контракты API и сценарии проверки ([quickstart.md](specs/001-rust-station-watcher/quickstart.md)).

## Происхождение и лицензия

Переписано на Rust с [vitaliy-shatskiy/bluetti-watcher](https://github.com/vitaliy-shatskiy/bluetti-watcher)
(C# + Python): правила решений перенесены оттуда, облачный источник, модуль ДТЭК и флаг-файл убраны. Протокол
станции портирован с [bluetti-bt-lib](https://pypi.org/project/bluetti-bt-lib/). Лицензия — [MIT](LICENSE).

Проект независимый и не связан с BLUETTI.
