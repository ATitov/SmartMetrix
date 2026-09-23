let state, token, busy = false, online = false, toastTimer, candidateKey;
const $ = id => document.getElementById(id);
const text = (id, value) => $(id).textContent = value;
const format = c => c ? `${c.lat.toFixed(7)}°, ${c.lon.toFixed(7)}° · ${c.height.toFixed(3)} м` : 'Координаты ещё не подтверждены';
function toast(message) { text('toast', message); $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').hidden = true, 5000); }
function view(id) { document.querySelectorAll('.view').forEach(el => el.hidden = el.id !== id); document.querySelectorAll('[data-view]').forEach(el => { el.classList.toggle('selected', el.dataset.view === id); if (el.dataset.view === id) text('title', el.textContent.trim().slice(1).trim()); }); }
document.querySelectorAll('[data-view],[data-go]').forEach(el => el.onclick = () => view(el.dataset.view || el.dataset.go));
function node(tag, content, className) { const el = document.createElement(tag); if (content != null) el.textContent = content; if (className) el.className = className; return el; }
function button(label, fn) { const el = node('button', label, 'secondary'); el.onclick = fn; return el; }
function render(s) {
 state = s;
 const ready = s.coordinates && !s.relocating && !s.survey && !s.candidate && s.usb && s.signal;
 text('state', s.running ? 'Выдача активна' : s.relocating ? 'База переносится' : !s.usb ? 'Нет USB-соединения' : !s.signal ? 'Нет спутниковых данных' : 'Выдача остановлена');
 text('state-hint', s.running ? 'Демонстрационный поток · обновление каждую секунду' : ready ? 'Координаты подтверждены. База готова к запуску.' : 'Проверьте соединение и подтвердите координаты места установки.');
 $('status-dot').classList.toggle('active', s.running); text('toggle', s.running ? 'Остановить выдачу' : 'Запустить выдачу'); $('toggle').disabled = busy || !online || (!s.running && !ready);
 $('start-location').disabled = busy || !online || !ready || s.running;
 text('satellites', s.satellites); text('rate', (s.rate / 1000).toFixed(2)); text('clients', s.clients.length);
 $('coordinates').replaceChildren();
 for (const [label, value] of [['Широта', s.coordinates ? s.coordinates.lat.toFixed(7) + '°' : '—'], ['Долгота', s.coordinates ? s.coordinates.lon.toFixed(7) + '°' : '—'], ['Высота', s.coordinates ? s.coordinates.height.toFixed(3) + ' м' : '—']]) { const row = node('div', null, 'coordinate-row'); row.append(node('span', label), node('b', value)); $('coordinates').append(row); }
 text('usb-status', s.usb ? 'USB подключён · симулятор' : 'USB отключён'); text('endpoint', `${s.settings.mountpoint} · :${s.settings.port}`); text('rover-status', s.running ? '192.168.1.52 · демонстрация' : 'Ожидание выдачи');
 text('fault-usb', s.usb ? 'Отключить USB' : 'Восстановить USB'); text('fault-signal', s.signal ? 'Потерять спутники' : 'Восстановить спутники');
 text('location-state', s.relocating ? 'Перенос начат' : s.coordinates ? 'Координаты заданы' : 'Требуются координаты');
 $('survey-progress').hidden = !s.survey; if (s.survey) { $('progress').value = s.survey.progress; text('progress-label', `Усреднение: ${s.survey.progress}% · ${s.survey.duration} с`); }
 $('survey').disabled = busy || !online || s.running || !!s.survey || !s.usb || !s.signal;
 $('coordinate-form').querySelector('button').disabled = busy || !online || s.running;
 $('candidate-card').hidden = !s.candidate;
 const key = JSON.stringify(s.candidate); if (key !== candidateKey) { $('confirm-check').checked = false; candidateKey = key; }
 text('candidate-coordinates', format(s.candidate)); text('candidate-source', s.candidate?.source === 'simulated-average' ? 'Источник: демонстрационное усреднение. Это не реальные измерения.' : 'Источник: ручной ввод или сохранённое место.');
 $('confirm').disabled = busy || !online || !$('confirm-check').checked || !s.candidate || !s.usb || !s.signal;
 text('current-location', format(s.coordinates));
 const list = $('profile-list'); list.replaceChildren();
 if (!s.profiles.length) list.append(node('article', 'Нет сохранённых мест. Подтвердите координаты и сохраните первое место.'));
 for (const profile of s.profiles) { const card = node('article', null, 'profile'), details = node('div'), actions = node('div', null, 'actions'); details.append(node('h2', profile.name), node('p', format(profile.coordinates), 'muted')); const load = button('Выбрать', async () => { if (await act('load-profile', { id: profile.id })) view('location'); }); load.disabled = s.running || busy || !online; actions.append(load, button('Удалить', () => { if (confirm('Удалить сохранённое место «' + profile.name + '»?')) act('delete-profile', { id: profile.id }); })); card.append(details, actions); list.append(card); }
 const form = $('network-form'); if (!form.contains(document.activeElement)) { form.elements.mountpoint.value = s.settings.mountpoint; form.elements.port.value = s.settings.port; } form.querySelector('button').disabled = busy || !online || s.running;
 $('log-list').replaceChildren(...s.logs.map(log => { const row = node('div', null, 'log-row'); row.append(node('time', new Date(log.time).toLocaleString('ru-RU')), node('span', log.message)); return row; }));
 text('updated', new Date().toLocaleTimeString('ru-RU'));
}
async function poll() { if (busy) return; try { const res = await fetch('/api/status'); if (!res.ok) throw Error(); const data = await res.json(); token = data.token; if (!online) document.querySelectorAll('main button').forEach(el => el.disabled = false); online = true; $('connection').hidden = true; render(data); } catch { online = false; $('connection').hidden = false; document.querySelectorAll('main button').forEach(el => el.disabled = true); } }
async function act(action, data = {}) {
 if (busy || !online) return false; busy = true;
 try { const res = await fetch('/api/action', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-RTK-Token': token }, body: JSON.stringify({ action, data }) }); const body = await res.json(); if (!res.ok) throw Error(body.error); state = body; toast('Готово'); return true; } catch (error) { toast(error.message || 'Не удалось выполнить команду'); return false; } finally { busy = false; if (state) render(state); }
}
$('toggle').onclick = () => act(state.running ? 'stop' : 'start'); $('start-location').onclick = async () => { if (await act('start')) view('overview'); };
for (const id of ['relocate', 'relocate-2']) $(id).onclick = () => $('relocate-dialog').showModal();
$('cancel-relocate').onclick = () => $('relocate-dialog').close(); $('accept-relocate').onclick = async () => { if (await act('relocate')) { $('relocate-dialog').close(); view('location'); } };
$('coordinate-form').onsubmit = event => { event.preventDefault(); act('manual', Object.fromEntries(new FormData(event.target))); };
$('survey').onclick = () => act('survey', { duration: Number($('duration').value) }); $('cancel-survey').onclick = () => act('cancel-survey');
$('confirm-check').onchange = () => render(state); $('confirm').onclick = () => act('confirm');
$('profile-form').onsubmit = async event => { event.preventDefault(); if (await act('save-profile', Object.fromEntries(new FormData(event.target)))) event.target.reset(); };
$('network-form').onsubmit = event => { event.preventDefault(); act('settings', Object.fromEntries(new FormData(event.target))); };
$('fault-usb').onclick = () => act('fault', { kind: 'usb' }); $('fault-signal').onclick = () => act('fault', { kind: 'signal' });
poll(); setInterval(poll, 1000);
