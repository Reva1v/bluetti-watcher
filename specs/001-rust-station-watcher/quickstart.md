# Quickstart: проверка 001-rust-station-watcher

Требуется: Windows 11 x64 с BLE, Rust stable (1.97+), станция AC180P рядом. Команды — из корня репозитория.

## 0. Сборка и тесты

```powershell
cargo fmt --check; cargo clippy --all-targets -- -D warnings; cargo test
cargo build --release      # target\release\bluetti-watcher.exe
```

Ожидание: все тесты зелёные, включая протокол (CRC, кадры, шифрование с фейковой станцией) и решения
(сценарии из spec.md, US3/US4).

## 1. Связь со станцией (риск R1, первым делом)

```powershell
.\target\release\bluetti-watcher.exe scan                          # находит AC180P…, печатает адрес
.\target\release\bluetti-watcher.exe read-once --mac <адрес> --trace
```

Ожидание: `encryption: yes|no`, затем `soc`, `ac_in_w`, `ac_out_w`, `dc_in_w`, `dc_out_w`, `ac_in_v`, совпадающие с
экраном станции. Результат (модель, прошивка, шифрование, успех) записать в spec.md → Assumptions.

## 2. Установка и фон (US1, US2)

```powershell
.\target\release\bluetti-watcher.exe install --mac <адрес> --elevated-setup
```

Ожидание: один запрос UAC; ярлыки «Bluetti Watcher» на Рабочем столе и в «Пуске»; задача `BluettiWatcher` в
планировщике в состоянии «Выполняется»; нет окна и значка в трее; `powercfg /a` показывает гибернацию; через 5 мин
`bluetti-watcher status` показывает заряд.

Ярлык → окно дашборда за ≤ 3 с, заряд не старше 2 мин, графики 6ч/24ч/7д/30д. Закрыть окно → `status` по-прежнему
показывает свежие показания.

## 3. Eco (US3) — с реальной станцией

1. Выдернуть станцию из розетки. Через ~3 мин: дашборд показывает отсчёт `ecoAt`, затем «Отключение» и «Eco».
   `powercfg /getactivescheme` → «Bluetti Eco»; если GPU настроен — `nvidia-smi -q -d POWER` показывает сниженный лимит.
2. Нажать «Максимум» → прежняя схема, eco снят; «Авто» → eco снова.
3. Вернуть питание. Через ~3 мин: прежняя схема и лимит GPU, событие «Свет вернулся».

## 4. Гибернация (US4) — без риска

```powershell
cargo test decision::scenarios      # симуляция разряда 25 % → 4 %, ложные скачки, rebase
```

На реальной станции: положить пустой файл `%LOCALAPPDATA%\bluetti-watcher\no-hibernate`, довести отключение до 5 %
(или временно поднять пороги в `config.toml`: `warn = 60`, `hibernate = 58` вместе с `dry_run = true`) и убедиться в
уведомлении-предупреждении и событии `hibernate-refused` / записи в логе вместо гибернации. Затем убрать выключатель и
`dry_run` — реальная гибернация проверяется по желанию владельца.

## 5. Удаление

```powershell
bluetti-watcher uninstall          # данные остаются; --purge — удалить и их
```

Ожидание: задач, ярлыков и схемы «Bluetti Eco» нет, активна прежняя схема питания.
