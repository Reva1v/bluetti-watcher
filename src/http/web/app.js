// Дашборд Bluetti Watcher: /api/now + SSE /api/stream, история на uPlot, смена режима.
'use strict';
(() => {
  const $ = (id) => document.getElementById(id);
  const MONO = "'JetBrains Mono', 'Cascadia Mono', Consolas, monospace";
  const C = { green: '#3DD68C', yellow: '#F5C518', red: '#FF4D4D', blue: '#5AA9FF', orange: '#FF6F1A', muted: '#8E9AA3' };
  const RANGES = { '6h': [21600, '6 часов'], '24h': [86400, '24 часа'], '7d': [604800, '7 дней'], '30d': [2592000, '30 дней'] };
  // Значения по умолчанию до первого ответа; дальше — из `station` в /api/now.
  let CAPACITY_WH = 1440;
  let EFFICIENCY = 0.85;
  let HIBERNATE_SOC = 5;
  let LOW_SOC = 20;
  const SRC = { rate: 'По скорости разряда', learned: 'По обученному КПД', capacity: 'По ёмкости станции' };

  const S = {
    now: null, outages: [], events: [], range: '6h', hist: null, histAt: 0, xr: [0, 1],
    pending: null, pendingAt: 0, modeErr: '', conn: true,
  };

  // ---------- formatting ----------
  const pad = (n) => String(n).padStart(2, '0');
  const hm = (d) => pad(d.getHours()) + ':' + pad(d.getMinutes());
  const dm = (d) => pad(d.getDate()) + '.' + pad(d.getMonth() + 1);
  function dayLabel(d) {
    const a = new Date(d); a.setHours(0, 0, 0, 0);
    const b = new Date(); b.setHours(0, 0, 0, 0);
    const diff = Math.round((b - a) / 864e5);
    return diff === 0 ? 'Сегодня' : diff === 1 ? 'Вчера' : dm(d);
  }
  const when = (d) => dayLabel(d) + ' ' + hm(d);
  function plural(n, f) {
    const a = n % 100, b = n % 10;
    return f[a > 10 && a < 20 ? 2 : b === 1 ? 0 : b >= 2 && b <= 4 ? 1 : 2];
  }
  const clockDur = (min) => { min = Math.max(0, Math.floor(min)); return Math.floor(min / 60) + ':' + pad(min % 60); };
  function wordDur(min) {
    min = Math.max(0, Math.floor(min));
    const h = Math.floor(min / 60), m = min % 60;
    return h ? h + ' ч ' + m + ' мин' : m + ' мин';
  }
  function ago(sec) {
    sec = Math.max(0, Math.floor(sec));
    if (sec < 60) return sec + ' с назад';
    if (sec < 3600) return Math.floor(sec / 60) + ' мин назад';
    return Math.floor(sec / 3600) + ' ч назад';
  }
  function hoursParts(h) {
    const min = Math.round(h * 60), hh = Math.floor(min / 60);
    return hh ? [hh + ' ч ' + pad(min % 60), 'мин'] : [String(min), 'мин'];
  }
  const socColor = (s) => (s >= 50 ? C.green : s >= LOW_SOC ? C.yellow : C.red);
  const fmtW = (v) => (v == null ? '—' : String(Math.round(v)));
  const sum = (a, b) => (a == null && b == null ? null : (a || 0) + (b || 0));

  // ---------- DOM helpers ----------
  function setText(id, t) { const e = $(id); if (e.textContent !== t) e.textContent = t; }
  function show(id, on) { const e = $(id); if (e.hidden === on) e.hidden = !on; }
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  // ---------- live state ----------
  function render() {
    const n = S.now;
    show('b-conn', !S.conn);
    const dot = $('st-dot');
    if (!n) { dot.className = 'dot'; return; }
    if (n.station) {
      CAPACITY_WH = n.station.capacityWh;
      EFFICIENCY = n.station.efficiency;
      HIBERNATE_SOC = n.station.hibernate;
      LOW_SOC = n.station.low;
      setText('st-sub', 'AC180P' + (n.station.mac ? ' · ' + n.station.mac : ''));
    }
    const nowMs = Date.now();
    document.body.classList.toggle('is-stale', n.stale);
    show('dry', n.dryRun);
    show('b-hib', n.hibernateAvailable === false);
    show('b-kill', !!n.killSwitch);

    // header + stale banner
    const ts = n.ts ? new Date(n.ts) : null;
    dot.className = 'dot ' + (!S.conn || n.stale ? 'bad' : 'ok');
    if (ts) {
      const sec = (nowMs - ts) / 1000;
      setText('st-text', (n.stale ? 'Последнее показание в ' : 'Показание в ') + hm(ts) + ' · ' + ago(sec));
      tbChip(n);
    } else {
      setText('st-text', 'Показаний ещё нет');
    }
    show('b-stale', n.stale && S.conn);
    if (n.stale) {
      const why = ' Частая причина: к станции подключён телефон с приложением BLUETTI.';
      if (ts) {
        const m = Math.max(1, Math.floor((nowMs - ts) / 60000));
        setText('b-stale-text', 'Станция не отвечает ' + m + ' ' + plural(m, ['минуту', 'минуты', 'минут']) +
          '. Цифры ниже — последние известные.' + why);
      } else {
        setText('b-stale-text', 'Станция ещё не ответила.' + why);
      }
    }

    // charge card
    const soc = n.soc;
    const col = soc == null ? C.muted : socColor(soc);
    setText('soc', soc == null ? '—' : String(soc));
    $('soc').style.color = col;
    const bar = $('socbar');
    bar.style.width = (soc || 0) + '%';
    bar.style.background = col;
    $('c-charge').classList.toggle('alert', n.mains === 'off' && !n.stale);

    const pill = $('pill');
    let pt = '', pc = 'pill';
    if (n.outage) {
      pt = 'Отключение · ' + wordDur((nowMs - new Date(n.outage.since)) / 60000);
      pc += ' out';
    } else if (n.mains === 'off') {
      pt = 'Сети нет'; pc += ' out';
    } else if (n.mains === 'on') {
      pt = (n.acInW || 0) > 0 && soc != null && soc < 100 ? 'Сеть есть · заряжается' : 'Сеть есть';
      pc += ' mains';
    } else {
      pt = 'Сеть: неизвестно';
    }
    if (pill.className !== pc) pill.className = pc;
    setText('pill', pt);
    show('pill', true);

    let note = '';
    if (n.outage) {
      const since = new Date(n.outage.since);
      note = 'С ' + hm(since) + (n.outage.socStart != null ? ', было ' + n.outage.socStart + ' %' : '') + '. Работает от батареи.';
    } else if (n.mains === 'off') {
      note = 'Сеть пропала. Работает от батареи.';
    } else {
      const last = S.outages.find((o) => o.end);
      if (last) note = 'Последнее отключение: ' + dayLabel(new Date(last.start)) + ', ' + wordDur(last.durationMin) + '.';
    }
    if (n.levels && n.levels.critical) note += ' Критический заряд.';
    else if (n.levels && n.levels.low) note += ' Низкий заряд.';
    setText('soc-note', note.trim());

    renderTimeLeft(n, soc, nowMs);
    renderMode(n, nowMs);

    // tiles
    tile('t-acin', n.acInW, 'in');
    tile('t-acout', n.acOutW, 'out');
    tile('t-dcin', n.dcInW, 'in');
    tile('t-dcout', n.dcOutW, 'out');
    const v = n.acInV;
    setText('t-v', v == null ? '—' : v > 0 ? v.toFixed(1) : '0');
    $('t-v').parentNode.className = 'tv live' + (v > 0 ? ' on' : '');

    // last error
    show('last-err', !!n.lastError);
    if (n.lastError) {
      setText('last-err', 'Последняя ошибка, ' + when(new Date(n.lastError.ts)) + ': ' + n.lastError.text);
    }
  }

  function tile(id, w, kind) {
    setText(id, fmtW(w));
    const cls = 'tv live' + (w > 0 ? ' ' + kind : '');
    const p = $(id).parentNode;
    if (p.className !== cls) p.className = cls;
  }

  function renderTimeLeft(n, soc, nowMs) {
    const load = sum(n.acOutW, n.dcOutW);
    const onMains = n.mains === 'on';
    setText('tl-title', onMains ? 'Если свет пропадёт' : 'Осталось');
    let big = '—', unit = '', sub = '', m1 = '', m2 = '', cls = 'tl live';
    const avg = n.avgLoadW;
    const avgText = avg != null
      ? 'при средней нагрузке ' + avg + ' Вт' + (n.avgLoadScope === 'outage' ? ' с начала отключения' : ' за всё время')
      : '';
    if (n.hoursLeft != null) {
      [big, unit] = hoursParts(n.hoursLeft);
      sub = avgText || (load != null ? 'при нагрузке ' + load + ' Вт' : '');
      m1 = SRC[n.runtimeSource] || '';
      if (soc != null && soc > HIBERNATE_SOC && !onMains) {
        const at = new Date(nowMs + (n.hoursLeft * (soc - HIBERNATE_SOC)) / soc * 3600e3);
        m2 = 'Гибернация на ' + HIBERNATE_SOC + ' % — примерно в ' + hm(at);
      }
    } else if (onMains && n.hoursIfOutage != null) {
      [big, unit] = hoursParts(n.hoursIfOutage);
      big = '≈ ' + big; cls += ' est';
      sub = avgText + ', по ёмкости ' + CAPACITY_WH + ' Вт·ч';
      if (load != null) m1 = 'Сейчас ' + load + ' Вт';
    } else if (load === 0) {
      big = 'нет нагрузки'; cls += ' small';
    } else if (onMains && soc != null && load > 0) {
      [big, unit] = hoursParts((CAPACITY_WH * soc / 100 * EFFICIENCY) / load);
      big = '≈ ' + big; cls += ' est';
      sub = 'при нынешней нагрузке ' + load + ' Вт, по ёмкости ' + CAPACITY_WH + ' Вт·ч';
    }
    const b = $('tl-big');
    if (b.className !== cls) b.className = cls;
    setText('tl-big', big);
    setText('tl-unit', unit);
    setText('tl-sub', sub);
    setText('tl-m1', m1);
    setText('tl-m2', m2);
    show('tl-foot', !!(m1 || m2));
  }

  function renderMode(n, nowMs) {
    const active = S.pending || n.manual;
    for (const b of document.querySelectorAll('.seg button')) {
      const on = String(b.dataset.mode === active);
      if (b.getAttribute('aria-pressed') !== on) b.setAttribute('aria-pressed', on);
    }
    let title, detail;
    if (active === 'eco') {
      title = 'Eco выбран вручную';
      detail = 'Держится до следующего изменения сети или до «Авто».';
    } else if (active === 'max') {
      title = 'Максимум: eco снят';
      detail = 'Прежняя схема питания до следующего изменения сети или «Авто». Гибернация на ' + HIBERNATE_SOC + ' % всё равно сработает.';
    } else if (n.eco === 'deep') {
      title = 'Глубокий eco включён';
      detail = 'Заряд низкий: максимальная экономия до возвращения света.';
    } else if (n.eco === 'eco') {
      title = 'Eco включён автоматически';
      detail = 'Схема «Bluetti Eco»: CPU и GPU ограничены, монитор гаснет раньше. На ' + LOW_SOC + ' % включится глубокий eco.';
    } else if (n.ecoAt) {
      const left = Math.max(0, Math.round((new Date(n.ecoAt) - nowMs) / 1000));
      title = 'Eco через ' + Math.floor(left / 60) + ':' + pad(left % 60);
      detail = 'Свет пропал. Если не вернётся, включится eco.';
    } else {
      title = 'Обычный режим';
      detail = 'Eco включится автоматически после пропадания света.';
    }
    if (n.hibernateDelayedUntil) detail += ' Гибернация отложена до ' + hm(new Date(n.hibernateDelayedUntil)) + '.';
    setText('mode-title', title);
    setText('mode-detail', detail);
    show('mode-err', !!S.modeErr);
    setText('mode-err', S.modeErr);
  }

  async function setMode(mode) {
    S.pending = mode; S.pendingAt = Date.now(); S.modeErr = '';
    render();
    try {
      const r = await fetch('/api/mode', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Bluetti-Watcher': '1' },
        body: JSON.stringify({ mode }),
      });
      if (!r.ok) {
        const t = await r.text().catch(() => '');
        throw new Error(t || 'HTTP ' + r.status);
      }
    } catch (e) {
      S.pending = null;
      S.modeErr = 'Не удалось переключить режим: ' + (e instanceof TypeError ? 'нет связи с сервисом' : e.message);
      render();
    }
  }

  // ---------- lists ----------
  function renderOutages() {
    const body = $('out-body');
    const rows = S.outages.map((o) => {
      const tr = document.createElement('tr');
      const socEnd = o.end ? o.socEnd : (S.now && S.now.soc);
      const socTxt = o.socStart == null && socEnd == null ? '—' : (o.socStart ?? '?') + ' → ' + (socEnd ?? '?') + ' %';
      const cells = [
        [when(new Date(o.start)), ''],
        [o.end ? clockDur(o.durationMin) : 'идёт ' + clockDur((Date.now() - new Date(o.start)) / 60000), 'm'],
        [socTxt, 'm'],
        [o.eco ? 'да' : 'нет', 'c'],
        [o.hibernated ? 'да' : o.end ? 'нет' : '—', 'c'],
      ];
      for (const [t, c] of cells) tr.appendChild(el('td', c, t));
      return tr;
    });
    body.replaceChildren(...rows);
    show('out-empty', !rows.length);
  }

  function evClass(k) {
    if (/failed|refused|critical/.test(k)) return 'ev-bad';
    if (/^(outage-start|eco-on|eco-deep|level-low|hibernate)/.test(k)) return 'ev-warn';
    return '';
  }

  function renderEvents() {
    const items = S.events.map((e) => {
      const d = new Date(e.ts);
      const li = document.createElement('li');
      const t = el('time', '', dayLabel(d) === 'Сегодня' ? hm(d) : when(d).replace('Сегодня ', ''));
      t.dateTime = e.ts;
      li.append(t, el('span', evClass(e.kind), e.text));
      return li;
    });
    $('ev-list').replaceChildren(...items);
    show('ev-empty', !items.length);
  }

  // ---------- history charts ----------
  let charts = null;

  function fmtTick(v) {
    const d = new Date(v * 1000);
    return S.range !== '6h' && S.range !== '24h' && d.getHours() === 0 && d.getMinutes() === 0 ? dm(d) : hm(d);
  }

  function drawBands(u) {
    const out = (S.hist && S.hist.outages) || [];
    if (!out.length) return;
    const { left, top, width, height } = u.bbox;
    const ctx = u.ctx, nowS = Date.now() / 1000;
    ctx.save();
    ctx.fillStyle = '#40210D';
    ctx.globalAlpha = 0.75;
    for (const o of out) {
      const x0 = Math.max(left, u.valToPos(o.start, 'x', true));
      const x1 = Math.min(left + width, u.valToPos(o.end ?? nowS, 'x', true));
      if (x1 > x0) ctx.fillRect(x0, top, x1 - x0, height);
    }
    ctx.restore();
  }

  function drawLevels(u) {
    const { left, width } = u.bbox, ctx = u.ctx, px = uPlot.pxRatio;
    ctx.save();
    ctx.strokeStyle = '#5C2A24';
    ctx.lineWidth = px;
    ctx.setLineDash([4 * px, 4 * px]);
    for (const v of [LOW_SOC, HIBERNATE_SOC]) {
      const y = Math.round(u.valToPos(v, 'y', true)) + 0.5;
      ctx.beginPath(); ctx.moveTo(left, y); ctx.lineTo(left + width, y); ctx.stroke();
    }
    ctx.restore();
  }

  function readout(u) {
    const i = u.cursor.idx;
    if (i == null || !charts) { setText('readout', ''); return; }
    const d0 = charts[0].data, d1 = charts[1].data;
    const t = d0[0][i];
    if (t == null || d0[1][i] == null) { setText('readout', ''); return; }
    const d = new Date(t * 1000);
    setText('readout', (S.range === '6h' || S.range === '24h' ? hm(d) : dm(d) + ' ' + hm(d)) + ' · ' +
      d0[1][i] + ' % · вход ' + fmtW(d1[1][i]) + ' Вт · нагрузка ' + fmtW(d1[2][i]) + ' Вт');
  }

  function chartOpts(kind, width, height) {
    const axis = (extra) => Object.assign({
      stroke: C.muted, font: '12px ' + MONO, ticks: { show: false },
      grid: { stroke: '#2E2E2E', width: 1 },
    }, extra);
    const line = (stroke) => ({ stroke, width: 2.5, spanGaps: false, points: { show: false } });
    const soc = kind === 'soc';
    return {
      width, height,
      legend: { show: false },
      cursor: { sync: { key: 'bw' }, drag: { x: false, y: false }, points: { size: 7 } },
      scales: {
        x: { time: true, range: () => S.xr },
        y: soc ? { range: [0, 100] } : { range: (u, min, max) => [0, Math.max(50, Math.ceil(((max || 0) * 1.15) / 50) * 50)] },
      },
      axes: [
        axis({ space: 64, values: (u, sp) => sp.map(fmtTick), grid: { show: false } }),
        axis({ size: 54, space: 30, values: (u, sp) => sp.map((v) => v + (soc ? ' %' : '')) }),
      ],
      series: soc ? [{}, line(C.orange)] : [{}, line(C.blue), line(C.orange)],
      hooks: { drawClear: [drawBands], draw: soc ? [drawLevels] : [], setCursor: [readout] },
    };
  }

  function chartWidth() { return Math.max(200, $('ch-soc').clientWidth); }

  function drawHistory() {
    const h = S.hist;
    const x = [], soc = [], pin = [], pout = [];
    let prev = null;
    for (const p of h.points) {
      if (prev != null && p.t - prev > 3 * h.step) { x.push(prev + h.step); soc.push(null); pin.push(null); pout.push(null); }
      x.push(p.t); soc.push(p.soc); pin.push(p.in); pout.push(p.out);
      prev = p.t;
    }
    const nowS = Date.now() / 1000;
    S.xr = [nowS - RANGES[S.range][0], nowS];
    show('hist-empty', !h.points.length);
    if (!charts) {
      const w = chartWidth();
      charts = [
        new uPlot(chartOpts('soc', w, 200), [x, soc], $('ch-soc')),
        new uPlot(chartOpts('pow', w, 150), [x, pin, pout], $('ch-pow')),
      ];
      let raf = 0;
      new ResizeObserver(() => {
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(() => {
          const w2 = chartWidth();
          if (charts[0].width !== w2) { charts[0].setSize({ width: w2, height: 200 }); charts[1].setSize({ width: w2, height: 150 }); }
        });
      }).observe($('ch-soc'));
    } else {
      charts[0].setData([x, soc]);
      charts[1].setData([x, pin, pout]);
    }
  }

  async function loadHistory() {
    const range = S.range;
    try {
      const h = await getJSON('/api/history?range=' + range);
      if (range !== S.range) return;
      S.hist = h; S.histAt = Date.now();
      drawHistory();
    } catch (e) { /* связь потеряна — баннер покажет поток */ }
  }

  function maybeRefreshHistory() {
    if (S.hist && document.visibilityState === 'visible' && Date.now() - S.histAt > Math.max(60, S.hist.step) * 1000) loadHistory();
  }

  function pickRange(range) {
    if (range === S.range) return;
    S.range = range;
    for (const b of document.querySelectorAll('.hist .tabs button')) b.setAttribute('aria-pressed', String(b.dataset.range === range));
    const name = RANGES[range][1];
    $('ch-soc').setAttribute('aria-label', 'График заряда за ' + name);
    $('ch-pow').setAttribute('aria-label', 'График мощности за ' + name + ': вход и нагрузка');
    loadHistory();
  }

  // ---------- график ДТЭК (/api/dtek, SSE `dtek`) ----------
  const D = { v: null, day: 0, minute: '' };
  const BOLT = 'M13.5 2 L5 13.5 H11 L10 22 L19 10 H13 Z';
  const CELL = {
    yes: ['', 'свет есть'], no: ['no', 'света нет'], first: ['first', 'света нет первые полчаса'],
    second: ['second', 'света нет вторые полчаса'], maybe: ['maybe', 'возможно отключение'],
    mfirst: ['mfirst', 'возможно отключение в первые полчаса'], msecond: ['msecond', 'возможно отключение во вторые полчаса'],
  };
  const OFF_HALVES = { no: [1, 1], first: [1, 0], second: [0, 1] };
  const ymd = (d) => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
  function parseDay(s) { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d); }
  const queueName = (q) => String(q || '').replace(/^GPV/, '');

  function boltSvg(crossed) {
    const ns = 'http://www.w3.org/2000/svg';
    const s = document.createElementNS(ns, 'svg');
    s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('width', '15');
    s.setAttribute('height', '15');
    s.setAttribute('aria-hidden', 'true');
    const p = document.createElementNS(ns, 'path');
    p.setAttribute('d', BOLT);
    p.setAttribute('fill', 'currentColor');
    s.appendChild(p);
    if (crossed) {
      const l = document.createElementNS(ns, 'path');
      l.setAttribute('d', 'M3.5 3.5 L20.5 20.5');
      l.setAttribute('stroke', 'currentColor');
      l.setAttribute('stroke-width', '2.2');
      l.setAttribute('stroke-linecap', 'round');
      s.appendChild(l);
    }
    return s;
  }

  // Дни ответа, у которых есть дата сегодня/завтра по часам ПК (подписи считаем сами — ответ мог устареть).
  function dtekDays() {
    const v = D.v, today = ymd(new Date());
    const t = new Date(); t.setDate(t.getDate() + 1);
    const tomorrow = ymd(t);
    const by = new Map((v.days || []).map((d) => [d.date, d]));
    return [[today, 'Сегодня'], [tomorrow, 'Завтра']].map(([date, label]) => ({ date, label, cells: (by.get(date) || {}).cells || null }));
  }

  // Состояние «сейчас» по получасам сегодня и завтра: окна подряд — одно, `maybe` светом не считается.
  function dtekNow(days) {
    const slots = [];
    for (const d of days) {
      if (!d.cells) break;
      const base = parseDay(d.date).getTime();
      d.cells.forEach((c, h) => {
        const off = OFF_HALVES[c] || [0, 0];
        const known = c in CELL;
        for (let k = 0; k < 2; k++) {
          const t = new Date(base); t.setHours(h, k * 30, 0, 0);
          slots.push({ t, off: !!off[k], known, today: d === days[0] });
        }
      });
    }
    const nowMs = Date.now();
    const i = slots.findIndex((s, j) => s.t <= nowMs && (j + 1 >= slots.length || slots[j + 1].t > nowMs));
    if (i < 0 || !slots[i].today || nowMs - slots[i].t > 1800e3) return { cls: '', text: 'Расписания нет' };
    const cur = slots[i];
    if (!cur.known) return { cls: '', text: 'Нет данных на этот час' };
    let j = i;
    while (j < slots.length && slots[j].known && slots[j].off === cur.off) j++;
    const end = j < slots.length ? slots[j].t : null;
    const endTxt = end ? (ymd(end) === ymd(cur.t) ? hm(end) : 'завтра ' + hm(end)) : '';
    if (cur.off) return { cls: 'out', text: end ? 'Света нет до ' + endTxt : 'Света нет' };
    if (end) return { cls: 'on', text: slots[j].known ? 'Свет есть до ' + endTxt : 'Свет есть' };
    const anyToday = slots.some((s) => s.today && s.off);
    return { cls: 'on', text: anyToday ? 'Сегодня больше без отключений' : 'Сегодня без отключений' };
  }

  function renderDtek() {
    const v = D.v;
    const on = !!(v && v.enabled);
    show('dtek', on);
    if (!on) return;
    const now = new Date();
    D.minute = ymd(now) + ' ' + hm(now);
    const days = dtekDays();
    if (!days[D.day].cells) D.day = 0;
    setText('dtek-addr', v.address || '');

    const st = dtekNow(days);
    const pill = $('dtek-pill'), pc = 'pill dtek-pill' + (st.cls ? ' ' + st.cls : '');
    if (pill.className !== pc) pill.className = pc;
    setText('dtek-pill-text', st.text);

    document.querySelectorAll('#dtek-tabs button').forEach((b, k) => {
      const d = days[k];
      const txt = d.label + ' ' + dm(parseDay(d.date)) + (d.cells ? '' : ' · ещё нет');
      if (b.textContent !== txt) b.textContent = txt;
      b.disabled = !d.cells;
      b.setAttribute('aria-pressed', String(k === D.day));
    });

    const day = days[D.day];
    show('dtek-none', !day.cells);
    show('dtek-grid', !!day.cells);
    if (day.cells) {
      const curH = D.day === 0 ? now.getHours() : -1;
      const items = day.cells.map((c, h) => {
        const known = CELL[c];
        const [cls, what] = known || ['unknown', 'нет данных'];
        const e = el('div', 'dc' + (cls ? ' ' + cls : '') + (h === curH ? ' cur' : ''));
        e.setAttribute('role', 'img');
        e.setAttribute('aria-label', pad(h) + ':00–' + pad((h + 1) % 24) + ':00: ' + what + (h === curH ? ', текущий час' : ''));
        e.title = what;
        e.append(known ? boltSvg(!!OFF_HALVES[c]) : el('i', 'dc-q', '?'), el('span', '', pad(h) + ':00'));
        return e;
      });
      $('dtek-grid').replaceChildren(...items);
    }

    const parts = [];
    const up = /^(\d\d\.\d\d)\.\d{4}\s+(\d\d:\d\d)/.exec(v.update || '');
    if (up) parts.push('ДТЭК обновил ' + up[1] + ' ' + up[2]);
    else if (v.update) parts.push('ДТЭК обновил ' + v.update);
    if (v.collected) {
      const c = new Date(v.collected);
      parts.push('собрано ' + (ymd(c) === ymd(now) ? hm(c) : dm(c) + ' ' + hm(c)));
    }
    const cand = (v.candidates || []).map(queueName);
    if (v.queueStatus === 'same' && cand.length > 1) parts.push('очереди ' + cand.join(', ') + ' (совпадают)');
    else if (v.queue) parts.push('очередь ' + queueName(v.queue) + (v.queueStatus === 'guess' ? ' (уточняется)' : ''));
    setText('dtek-meta', parts.join(' · '));
    show('dtek-stale', !!v.stale);
    show('dtek-err', !!v.error);
    if (v.error) setText('dtek-err', 'Ошибка обновления: ' + v.error);
  }

  // Раз в минуту из 1-секундного тикера: текущий час и плашка.
  function tickDtek() {
    if (!D.v || !D.v.enabled) return;
    const now = new Date();
    if (ymd(now) + ' ' + hm(now) !== D.minute) renderDtek();
  }

  function setDtek(v) { D.v = v; renderDtek(); }
  async function loadDtek() {
    try { setDtek(await getJSON('/api/dtek')); } catch (e) { /* нет эндпоинта или связи — карточка остаётся как была */ }
  }

  // ---------- network ----------
  async function getJSON(url) {
    const r = await fetch(url, { cache: 'no-store' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.json();
  }

  async function loadOutages() {
    try { S.outages = await getJSON('/api/outages?limit=20'); renderOutages(); render(); } catch (e) { /* ignore */ }
  }
  async function loadEvents() {
    try { S.events = await getJSON('/api/events?limit=30'); renderEvents(); } catch (e) { /* ignore */ }
  }

  function setNow(v) {
    S.now = v;
    if (S.pending && (v.manual === S.pending || Date.now() - S.pendingAt > 120e3)) S.pending = null;
    render();
    if (S.outages.length && !S.outages[0].end) renderOutages();
  }

  function setConn(ok) {
    if (S.conn === ok) return;
    S.conn = ok;
    render();
  }

  let es = null, backoff = 1000, retry = 0;
  function connect() {
    es = new EventSource('/api/stream');
    es.onopen = () => {
      backoff = 1000;
      if (!S.conn) { setConn(true); loadOutages(); loadEvents(); loadHistory(); loadDtek(); }
    };
    es.addEventListener('now', (e) => { setConn(true); setNow(JSON.parse(e.data)); maybeRefreshHistory(); });
    es.addEventListener('dtek', (e) => setDtek(JSON.parse(e.data)));
    es.addEventListener('event', (e) => {
      const ev = JSON.parse(e.data);
      S.events.unshift(ev);
      if (S.events.length > 30) S.events.length = 30;
      renderEvents();
      if (ev.kind.startsWith('outage-')) { loadOutages(); loadHistory(); }
    });
    es.onerror = () => {
      es.close();
      setConn(false);
      clearTimeout(retry);
      retry = setTimeout(connect, backoff);
      backoff = Math.min(backoff * 2, 30000);
    };
  }

  // ---------- 1 s ticker, only while visible ----------
  let timer = 0;
  function tick() { render(); tickDtek(); }
  function syncTimer() {
    const visible = document.visibilityState === 'visible';
    if (visible && !timer) { timer = setInterval(tick, 1000); tick(); maybeRefreshHistory(); }
    else if (!visible && timer) { clearInterval(timer); timer = 0; }
  }

  // ---------- окно приложения (WebView2): своя верхняя панель ----------
  function tbChip(n) {
    const chip = $('tb-chip');
    if (!chip || !document.body.classList.contains('app')) return;
    const parts = [];
    if (n.soc != null) parts.push(n.soc + ' %');
    parts.push(n.mains === 'on' ? 'сеть есть' : n.mains === 'off' ? 'от батареи' : 'сеть неизвестна');
    if (n.eco === 'eco') parts.push('eco');
    if (n.eco === 'deep') parts.push('глубокий eco');
    setText('tb-chip', parts.join(' · '));
    chip.classList.toggle('on-battery', n.mains === 'off');
    show('tb-chip', true);
  }
  // `?frame` — показать панель и в обычном браузере (скриншоты для README); кнопки там ничего не делают.
  const inApp = window.ipc && typeof window.ipc.postMessage === 'function';
  if (inApp || new URLSearchParams(location.search).has('frame')) {
    const send = (m) => { if (inApp) window.ipc.postMessage(m); };
    document.body.classList.add('app');
    show('titlebar', true);
    const drag = $('tb-drag');
    drag.addEventListener('mousedown', (e) => { if (e.button === 0 && e.detail === 1) send('drag'); });
    drag.addEventListener('dblclick', () => send('max'));
    $('tb-min').addEventListener('click', () => send('min'));
    $('tb-max').addEventListener('click', () => send('max'));
    $('tb-close').addEventListener('click', () => send('close'));
    // Окно сообщает, развернуто ли оно: меняем значок и подпись кнопки.
    window.__setMaximized = (on) => {
      $('tb-max-icon').toggleAttribute('hidden', on);
      $('tb-restore-icon').toggleAttribute('hidden', !on);
      $('tb-max').setAttribute('aria-label', on ? 'Восстановить' : 'Развернуть');
    };
  }

  // ---------- start ----------
  for (const b of document.querySelectorAll('.seg button')) b.addEventListener('click', () => setMode(b.dataset.mode));
  for (const b of document.querySelectorAll('.hist .tabs button')) b.addEventListener('click', () => pickRange(b.dataset.range));
  for (const b of document.querySelectorAll('#dtek-tabs button')) {
    b.addEventListener('click', () => { D.day = Number(b.dataset.day); renderDtek(); });
  }
  document.addEventListener('visibilitychange', syncTimer);

  getJSON('/api/now').then(setNow, () => setConn(false));
  loadOutages();
  loadEvents();
  loadHistory();
  loadDtek();
  setInterval(loadDtek, 300e3);
  connect();
  syncTimer();
})();
