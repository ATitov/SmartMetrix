const mode = document.body.dataset.mode;
const apiKey = document.querySelector('#apiKey');
const state = { status: null, measurements: [], audit: [], lastSuccess: null, refreshing: false };
const labels = { Ready:'Готов', Degraded:'Ограниченно', Unavailable:'Недоступен', NotConfigured:'Не настроен', Requested:'Запрошено', Capturing:'Захват', QualityControl:'Контроль качества', Reconstructing:'Реконструкция глубины', Segmenting:'Сегментация', Persisting:'Сохранение', Analysing:'Анализ блоков', Georeferencing:'Геопривязка', Failed:'Ошибка', Rejected:'Отклонено', Completed:'Завершено' };

const esc = value => { const node = document.createElement('span'); node.textContent = value ?? '—'; return node.innerHTML; };
const date = value => value ? new Date(value).toLocaleString('ru-RU') : '—';
const label = value => labels[value] || value || 'Неизвестно';
const cssState = value => value === 'Ready' || value === 'Completed' ? 'state-ready' : value === 'Failed' || value === 'Rejected' || value === 'Cancelled' ? 'state-danger' : value === 'Degraded' || value === 'Unavailable' ? 'state-warning' : 'state-muted';
const isActive = value => ['Requested','Capturing','QualityControl','Reconstructing','Segmenting','Analysing','Georeferencing','Persisting'].includes(value);
const isProblem = value => ['Failed','Rejected','Degraded','Unavailable'].includes(value);

async function api(path, options = {}) {
  const base = path.startsWith('/auth/') || path.startsWith('/admin/') ? '/api' : '/api/operator';
  const headers = { ...options.headers, 'Content-Type':'application/json' };
  if (apiKey?.value) headers['X-API-Key'] = apiKey.value;
  if (!apiKey?.value && options.method && !['GET','HEAD'].includes(options.method.toUpperCase())) {
    const csrf = await fetch('/api/auth/csrf', { credentials:'same-origin' });
    if (!csrf.ok) throw new Error('Не удалось получить токен сессии');
    headers['X-CSRF-Token'] = (await csrf.json()).token;
  }
  const response = await fetch(base + path, { ...options, credentials:'same-origin', headers });
  if (response.status === 401) {
    location.assign(`/login/?return=${encodeURIComponent(location.pathname)}`);
    throw new Error('Требуется вход в систему');
  }
  if (!response.ok) throw new Error((await response.text()) || `HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}

async function loadIdentity() {
  const identity = await api('/auth/me');
  const currentUser = document.querySelector('#currentUser');
  if (currentUser) currentUser.textContent = `${identity.displayName} · ${identity.role}`;
  const administration = document.querySelector('#userAdministration');
  if (administration) {
    administration.hidden = identity.role !== 'administrator';
    if (!administration.hidden) await loadUsers();
  }
}

async function loadUsers() {
  const users = await api('/admin/users');
  const body = document.querySelector('#users');
  if (!body) return;
  body.innerHTML = users.map(user => `<tr><td><b>${esc(user.username)}</b><br><small>${esc(user.displayName)}</small></td><td>${esc(user.role)}</td><td>${user.enabled ? 'Активен' : 'Отключен'}${user.lockedUntil ? '<br><small>Заблокирован</small>' : ''}</td><td><button class="button" data-user-toggle="${esc(user.username)}" data-enabled="${!user.enabled}">${user.enabled ? 'Отключить' : 'Включить'}</button><button class="button" data-user-password="${esc(user.username)}">Сменить пароль</button></td></tr>`).join('');
}

function toast(message, isError = false) {
  const root = document.querySelector('#toast');
  root.textContent = message;
  root.className = `toast show${isError ? ' error-toast' : ''}`;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => root.className = 'toast', 3500);
}

function renderStatus(status) {
  const root = document.querySelector('#systemState');
  root.innerHTML = `<div class="status-line ${cssState(status.state)}"><span class="dot"></span><b>${esc(label(status.state))}</b></div><div class="metric">${status.configured ? 'Система доступна' : 'Требуется настройка'}</div><div class="muted">Проверено ${date(status.checkedAt)}</div>`;
  document.querySelector('#activeMeasurement').textContent = status.activeMeasurementId || 'Нет активного измерения';
  document.querySelector('#components').innerHTML = (status.components || []).map(component => `<article class="card component"><div class="caption">${esc(component.kind)}</div><h3>${esc(component.name)}</h3><div class="status-line ${cssState(component.state)}"><span class="dot"></span>${esc(label(component.state))}</div><p class="muted">${esc(component.detail || 'Диагностика без замечаний')}</p></article>`).join('');
  if (mode === 'engineer') renderConfiguration(status.components || []);
}

function renderKpis() {
  const components = state.status?.components || [];
  const items = mode === 'engineer'
    ? [['Компонентов готово', components.filter(x => x.state === 'Ready').length], ['Требуют внимания', components.filter(x => x.state !== 'Ready').length], ['Ошибок измерений', state.measurements.filter(x => isProblem(x.status)).length], ['Записей аудита', state.audit.length]]
    : [['Всего измерений', state.measurements.length], ['В работе', state.measurements.filter(x => isActive(x.status)).length], ['Завершено', state.measurements.filter(x => x.status === 'Completed').length], ['Требуют внимания', state.measurements.filter(x => isProblem(x.status)).length]];
  document.querySelector('#kpis').innerHTML = items.map(([name,value]) => `<article class="card kpi"><div class="caption">${name}</div><div class="kpi-value">${value}</div></article>`).join('');
}

function filteredMeasurements() {
  const query = document.querySelector('#measurementSearch').value.trim().toLowerCase();
  const status = document.querySelector('#statusFilter').value;
  return state.measurements.filter(item => (!status || item.status === status) && (!query || `${item.id} ${item.status}`.toLowerCase().includes(query)));
}

function populateStatuses() {
  const select = document.querySelector('#statusFilter');
  const selected = select.value;
  const values = [...new Set(state.measurements.map(item => item.status).filter(Boolean))].sort();
  select.innerHTML = '<option value="">Все</option>' + values.map(value => `<option value="${esc(value)}">${esc(label(value))}</option>`).join('');
  if (values.includes(selected)) select.value = selected;
}

function renderMeasurements() {
  const items = filteredMeasurements();
  document.querySelector('#resultCount').textContent = `Показано ${items.length} из ${state.measurements.length}`;
  const body = document.querySelector('#measurements');
  body.innerHTML = items.length ? items.map(item => {
    const actions = mode !== 'engineer' ? '' : `${['Failed','Rejected'].includes(item.status) ? `<button class="button" data-retry="${esc(item.id)}" data-version="${item.version ?? 0}">Повторить</button>` : ''} ${isActive(item.status) ? `<button class="button danger" data-cancel="${esc(item.id)}" data-version="${item.version ?? 0}">Отменить</button>` : ''}`;
    const result = item.d50 == null && item.d80 == null ? '<span class="muted">Нет результата</span>' : `${item.d50 ?? '—'} / ${item.d80 ?? '—'}`;
    return `<tr><td><button class="link" data-detail="${esc(item.id)}">${esc(String(item.id).slice(0,8))}…</button><br><small class="muted">${esc(item.excavatorId)}</small></td><td><span class="pill ${cssState(item.status)}">${esc(label(item.status))}</span></td><td>${date(item.updatedAt)}</td><td>${result}</td><td>${item.confidence == null ? '—' : Math.round(item.confidence * 100) + '%'}</td><td>${actions}</td></tr>`;
  }).join('') : '<tr><td colspan="6" class="empty">Измерений пока нет. Создайте первое измерение кнопкой выше.</td></tr>';
}

function renderAudit() {
  const root = document.querySelector('#audit');
  const filter = document.querySelector('#auditFilter')?.value || '';
  const rows = state.audit.filter(item => !filter || (filter === 'ok' ? item.succeeded : !item.succeeded)).slice().reverse().slice(0, 30);
  document.querySelector('#auditCount').textContent = `Показано ${rows.length} из ${state.audit.length}`;
  root.innerHTML = rows.length ? rows.map(item => `<div class="audit-item"><span class="muted">${date(item.occurredAt)}</span><span><b>${esc(item.action)}</b><br><small>${esc(item.actor)} · ${esc(item.reason)}</small></span><span class="${item.succeeded ? 'state-ready' : 'state-danger'}">${item.succeeded ? 'OK' : 'Ошибка'}</span></div>`).join('') : '<div class="empty">Записей по выбранному фильтру нет</div>';
}

function renderConfiguration(components) {
  const root = document.querySelector('#configChecks');
  const checks = components.map(component => ({ name: component.name, detail: component.kind, ready: component.state === 'Ready' }));
  checks.push({ name:'Инженерный API-доступ', detail:'Ключ принят шлюзом', ready:Boolean(apiKey.value) });
  root.innerHTML = checks.map(check => `<div class="config-row"><span class="status-line ${check.ready ? 'state-ready' : 'state-warning'}"><span class="dot"></span></span><div><b>${esc(check.name)}</b><small>${esc(check.detail)} · ${check.ready ? 'готово' : 'проверьте настройку'}</small></div></div>`).join('');
}

function renderResultVisual(item) {
  const root = document.querySelector('#resultVisual');
  if (!root) return;
  if (item.d50 == null) { root.innerHTML = '<div class="empty">Расчётный результат для этого измерения отсутствует</div>'; return; }
  const values = [['D10',item.d10],['D20',item.d20],['D50',item.d50],['D80',item.d80],['D90',item.d90],['D95',item.d95]];
  const shownValues = values.filter(([,value]) => value != null);
  const maximum = Math.max(...values.map(([,value]) => value || 0), 1);
  root.innerHTML = `<div class="result-grid"><div class="result-cell"><span class="muted">Блоков</span><b>${item.blockCount ?? '—'}</b></div><div class="result-cell"><span class="muted">Confidence</span><b>${item.confidence == null ? '—' : Math.round(item.confidence*100)+'%'}</b></div><div class="result-cell"><span class="muted">Покрытие</span><b>${item.coverage == null ? '—' : Math.round(item.coverage*100)+'%'}</b></div><div class="result-cell"><span class="muted">Негабарит</span><b>${item.oversizeFraction == null ? '—' : Math.round(item.oversizeFraction*100)+'%'}</b></div></div><div class="histogram">${shownValues.map(([name,value]) => `<div class="hist-bar" style="height:${Math.max(5,(value||0)/maximum*100)}%"><span>${value ?? '—'}</span><small>${name}</small></div>`).join('')}</div>${item.isTestData ? '<div class="warning">Тестовые данные: не использовать для производственных решений.</div>' : ''}`;
}

function download(name, content, type) {
  const url = URL.createObjectURL(new Blob([content], { type }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); URL.revokeObjectURL(url);
}

function renderPipeline(item) {
  const root = document.querySelector('#pipelineStages');
  if (!root) return;
  const names = { calibration:'Калибровка', capture:'Кадры', pose:'Положение при съёмке', quality:'Контроль качества', depth:'Глубина', segmentation:'Сегментация', analysis:'Анализ блоков', georeference:'Геопривязка', result:'Итоговый результат' };
  root.innerHTML = `${item.failureReason ? `<p class="warning">${esc(item.failureReason)}</p>` : ''}${item.pipeline ? `<h3>Этапы обработки</h3><p class="muted">${item.pipeline.activeStage ? 'Текущий этап: '+esc(names[item.pipeline.activeStage] || item.pipeline.activeStage) : 'Сохранённые результаты'}</p>${(item.pipeline.stages || []).map(stage => `<div class="config-row"><span>${esc(names[stage.name] || stage.name)} · ${date(stage.completedAt)}</span><button class="button" data-stage="${esc(stage.name)}" data-measurement="${esc(item.id)}">Скачать JSON</button></div>`).join('')}` : ''}`;
}

document.addEventListener('click', async event => {
  const button = event.target.closest('[data-stage]');
  if (!button) return;
  button.disabled = true;
  try {
    const value = await api(`/measurements/${button.dataset.measurement}/stages/${button.dataset.stage}`);
    download(`${button.dataset.measurement}-${button.dataset.stage}.json`, JSON.stringify(value, null, 2), 'application/json');
  } catch (error) { toast(error.message, true); } finally { button.disabled = false; }
});

async function loadEngineeringTools() {
  if (mode !== 'engineer') return;
  try {
    const [system, config] = await Promise.all([api('/engineering/system'), api('/engineering/config')]);
    const drive = system.drives?.[0];
    document.querySelector('#systemMetrics').innerHTML = [['Компьютер',system.machine],['ОС',system.os],['Процессоры',system.processors],['Память процесса',Math.round(system.processMemoryBytes/1048576)+' МБ'],['Диск',drive ? `${drive.usedPercent}% занято` : '—'],['.NET',system.runtime]].map(([name,value]) => `<div class="config-row"><div><b>${esc(name)}</b><small>${esc(value)}</small></div></div>`).join('');
    document.querySelector('#runtimeConfig').innerHTML = Object.entries(config).map(([name,value]) => `<label>${esc(name)}<input data-config="${esc(name)}" value="${esc(value)}"></label>`).join('');
  } catch (error) { toast('Диагностика: '+error.message, true); }
}

async function loadLogs() {
  const root = document.querySelector('#serviceLogs'); if (!root) return;
  try {
    const service = encodeURIComponent(document.querySelector('#logService').value.trim());
    const level = encodeURIComponent(document.querySelector('#logLevel').value);
    const rows = await api(`/engineering/logs?service=${service}&level=${level}&take=150`);
    root.innerHTML = rows.length ? rows.map(row => `<div class="log-row"><span>${date(row.timestamp)}</span><span class="${cssState(row.level==='Error'||row.level==='Critical'?'Failed':'Ready')}">${esc(row.level)}</span><span>${esc(row.service)}</span><span>${esc(row.message)}</span></div>`).join('') : '<div class="empty">Записей по фильтру нет</div>';
  } catch (error) { root.innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}

function updateFreshness() {
  const root = document.querySelector('#freshness');
  if (!state.lastSuccess) { root.textContent = 'Ожидание данных'; root.className = 'freshness stale'; return; }
  const seconds = Math.max(0, Math.round((Date.now() - state.lastSuccess) / 1000));
  root.textContent = seconds < 2 ? 'Данные обновлены сейчас' : `Обновлено ${seconds} сек назад`;
  root.className = `freshness${seconds > 30 ? ' stale' : ''}`;
}

async function refresh() {
  if (state.refreshing) return;
  state.refreshing = true;
  const button = document.querySelector('#refresh');
  const banner = document.querySelector('#error');
  button.disabled = true;
  banner.hidden = true;
  try {
    const requests = [api('/status'), api('/measurements')];
    if (mode === 'engineer') requests.push(api('/audit'));
    const [status, measurements, audit = []] = await Promise.all(requests);
    state.status = status;
    state.measurements = Array.isArray(measurements) ? measurements : [];
    state.audit = Array.isArray(audit) ? audit : [];
    state.lastSuccess = Date.now();
    renderStatus(status);
    populateStatuses();
    renderMeasurements();
    if (mode === 'engineer') renderAudit();
    renderKpis();
  } catch (error) {
    banner.hidden = false;
    banner.textContent = apiKey.value ? `Не удалось получить данные: ${error.message}` : 'Введите API-ключ для загрузки данных';
  } finally {
    state.refreshing = false;
    button.disabled = false;
    updateFreshness();
  }
}

document.querySelector('#refresh').addEventListener('click', refresh);
document.querySelector('#logout')?.addEventListener('click', async () => {
  await api('/auth/logout', { method:'POST' });
  location.assign('/login/');
});
document.querySelector('#userForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  try {
    await api('/admin/users', { method:'POST', body:JSON.stringify(Object.fromEntries(new FormData(event.target))) });
    event.target.reset(); toast('Пользователь создан'); await loadUsers();
  } catch (error) { toast(error.message, true); }
});
document.querySelector('#users')?.addEventListener('click', async event => {
  const toggle=event.target.closest('[data-user-toggle]'); const reset=event.target.closest('[data-user-password]');
  try {
    if(toggle) await api(`/admin/users/${encodeURIComponent(toggle.dataset.userToggle)}/enabled/${toggle.dataset.enabled}`,{method:'PUT'});
    if(reset){const password=prompt(`Новый пароль для ${reset.dataset.userPassword} (не менее 12 символов)`);if(!password)return;await api(`/admin/users/${encodeURIComponent(reset.dataset.userPassword)}/password`,{method:'PUT',body:JSON.stringify({password})});}
    await loadUsers();
  } catch(error){toast(error.message,true);}
});
document.querySelector('#measurementSearch').addEventListener('input', renderMeasurements);
document.querySelector('#statusFilter').addEventListener('change', renderMeasurements);
document.querySelector('#auditFilter')?.addEventListener('change', renderAudit);
document.querySelector('#start')?.addEventListener('click', () => document.querySelector('#startDialog').showModal());
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => document.querySelector(`#${button.dataset.close}`).close()));

document.querySelector('#startForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = event.submitter;
  submit.disabled = true;
  try {
    const data = new FormData(event.target);
    event.target.dataset.commandId ||= crypto.randomUUID();
    await api('/measurements', { method:'POST', body:JSON.stringify({ commandId:event.target.dataset.commandId, excavatorId:data.get('excavatorId'), coordinateSystemId:data.get('coordinateSystemId'), reason:data.get('reason') }) });
    document.querySelector('#startDialog').close();
    event.target.reset();
    delete event.target.dataset.commandId;
    toast('Измерение запущено');
    await refresh();
  } catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
});

document.addEventListener('click', async event => {
  const target = event.target.closest('[data-detail],[data-retry],[data-cancel]');
  if (!target) return;
  if (target.dataset.detail) {
    try {
      const value = await api(`/measurements/${target.dataset.detail}`);
      renderResultVisual(value); renderPipeline(value);
      document.querySelector('#detailJson').textContent = JSON.stringify(value, null, 2);
      document.querySelector('#detailDialog').showModal();
    } catch (error) { toast(error.message, true); }
    return;
  }
  const form = document.querySelector('#commandForm');
  const command = target.dataset.retry ? 'retry' : 'cancel';
  const id = target.dataset.retry || target.dataset.cancel;
  form.elements.id.value = id;
  form.elements.command.value = command;
  form.elements.version.value = target.dataset.version;
  form.elements.reason.value = '';
  document.querySelector('#commandTitle').textContent = command === 'retry' ? 'Повторить измерение' : 'Отменить измерение';
  document.querySelector('#commandTarget').textContent = `Измерение ${id}`;
  document.querySelector('#commandDialog').showModal();
});

document.querySelector('#commandForm')?.addEventListener('submit', async event => {
  event.preventDefault();
  const submit = event.submitter;
  const data = new FormData(event.target);
  submit.disabled = true;
  try {
    await api(`/measurements/${data.get('id')}/${data.get('command')}`, { method:'POST', body:JSON.stringify({ commandId:crypto.randomUUID(), expectedVersion:Number(data.get('version')), reason:data.get('reason'), confirmed:true }) });
    document.querySelector('#commandDialog').close();
    toast('Команда выполнена и записана в аудит');
    await refresh();
  } catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
});

document.querySelector('#exportDiagnostics')?.addEventListener('click', () => {
  if (!state.status) { toast('Сначала загрузите данные', true); return; }
  const snapshot = { generatedAt:new Date().toISOString(), system:state.status, measurements:state.measurements, audit:state.audit };
  const blob = new Blob([JSON.stringify(snapshot, null, 2)], { type:'application/json' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `smartmetrix-diagnostics-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
  toast('Диагностический снимок подготовлен');
});

document.querySelector('#exportCsv')?.addEventListener('click', () => {
  const columns = ['id','excavatorId','status','updatedAt','d10','d20','d50','d80','d90','d95','isTestData','confidence','blockCount','oversizeFraction','coverage'];
  const csv = '\ufeff' + [columns.join(';'), ...state.measurements.map(item => columns.map(name => String(item[name] ?? '').replaceAll(';',',')).join(';'))].join('\r\n');
  download(`smartmetrix-report-${new Date().toISOString().slice(0,10)}.csv`, csv, 'text/csv;charset=utf-8');
});
document.querySelector('#printReport')?.addEventListener('click', () => window.print());
document.querySelector('#refreshSystem')?.addEventListener('click', loadEngineeringTools);
document.querySelector('#loadLogs')?.addEventListener('click', loadLogs);
document.querySelector('#saveConfig')?.addEventListener('click', async () => {
  const values = Object.fromEntries([...document.querySelectorAll('[data-config]')].map(input => [input.dataset.config,input.value]));
  try { await api('/engineering/config',{method:'PUT',body:JSON.stringify(values)}); toast('Конфигурация сохранена'); } catch(error) { toast(error.message,true); }
});

async function boot() {
  await loadIdentity();
  await refresh();
  if (mode === 'engineer') { await loadEngineeringTools(); await loadLogs(); }
}
boot().catch(error => toast(error.message, true));
setInterval(refresh, 10000);
setInterval(updateFreshness, 1000);
