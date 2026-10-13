# Tasks: 002 — График ДТЭК

- [ ] D001 `src/config.rs`: секция `[dtek]` (`enabled` false, `region` "kyiv", `queues` [], `address` "", `url` None) с проверкой имён очередей `GPV\d{1,2}\.\d{1,2}`; тесты
- [ ] D002 [P] `src/dtek/model.rs` + `parse.rs` + фикстура `tests/fixtures/kyiv-sample.json`: разбор и пределы FR-002; тесты
- [ ] D003 [P] `src/dtek/windows.rs`: окна, текущее состояние, `until`; тесты
- [ ] D004 [P] `src/dtek/queue.rs`: выбор очереди US2; тесты
- [ ] D005 `src/dtek/fetch.rs`: WinRT HttpClient, ETag, 4 МБ, без редиректов; ignored-тест с реальным запросом
- [ ] D006 `src/dtek/mod.rs`: `DtekView` по контракту plan.md, `view()`; тест формы
- [ ] D007 `src/service.rs` + `src/main.rs`: задача обновления раз в 30 мин, кэш в `meta`, `watch` → HTTP, наблюдение отключений для `queue`, события `dtek-failed/ok`
- [ ] D008 `src/http/mod.rs`: `GET /api/dtek`, `event: dtek` в SSE; тесты
- [ ] D009 `src/http/web/*`: карточка «График ДТЭК» перед «Отключениями» по plan.md
- [ ] D010 Конфиг владельца: `[dtek] enabled = true`, адрес, 5 кандидатов; переустановка; сверка сетки с сайтом ДТЭК (SC-001)
