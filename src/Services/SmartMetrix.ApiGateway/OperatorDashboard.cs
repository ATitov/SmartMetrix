namespace SmartMetrix.ApiGateway;

public static class OperatorDashboard
{
    public const string Html = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SmartMetrix — состояние</title><style>
:root{color-scheme:dark;font:15px system-ui;background:#101820;color:#edf4f4}body{max-width:1200px;margin:auto;padding:24px}header{display:flex;justify-content:space-between;align-items:center}h1{margin:0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px}.card{background:#182630;border:1px solid #31444f;border-radius:12px;padding:16px;margin:16px 0}.Ready{color:#63d49a}.Degraded,.Unavailable{color:#ffbd59}.NotConfigured{color:#aeb8bd}button,input{padding:9px;border-radius:7px;border:1px solid #526772;background:#101820;color:inherit}button{cursor:pointer;background:#176b87}button.danger{background:#8b3434}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:9px;border-bottom:1px solid #31444f}small{color:#aeb8bd}.error{color:#ff8a80}dialog{background:#182630;color:inherit;border:1px solid #526772;border-radius:12px}
</style></head><body><header><div><h1>SmartMetrix</h1><small>Операторская панель</small></div><label>API key <input id="key" type="password"></label></header>
<section id="system" class="card">Загрузка…</section><section class="grid" id="components"></section>
<section class="card"><h2>Измерения</h2><button onclick="startMeasurement()">Ручной запуск</button><table><thead><tr><th>ID</th><th>Статус</th><th>Обновлено</th><th>Результат</th><th></th></tr></thead><tbody id="measurements"></tbody></table></section>
<dialog id="confirm"><h3>Подтвердите опасную команду</h3><p id="confirmText"></p><button id="confirmYes" class="danger">Подтвердить</button> <button onclick="confirm.close()">Отмена</button></dialog>
<script>
const key=document.querySelector('#key');key.value=sessionStorage.apiKey||'';key.onchange=()=>sessionStorage.apiKey=key.value;
async function api(path,options={}){options.headers={...options.headers,'X-API-Key':key.value,'Content-Type':'application/json'};const r=await fetch('/api/operator'+path,options);if(!r.ok)throw new Error((await r.text())||r.status);return r.status===204?null:r.json()}
function esc(v){const n=document.createElement('span');n.textContent=v??'—';return n.innerHTML}
async function refresh(){try{const [s,ms]=await Promise.all([api('/status'),api('/measurements')]);system.innerHTML=`<h2 class="${esc(s.state)}">${esc(s.state)}</h2><div>Активное измерение: ${esc(s.activeMeasurementId)}</div><small>${new Date(s.checkedAt).toLocaleString()}</small>`;components.innerHTML=s.components.map(c=>`<article class="card"><b>${esc(c.name)}</b><div class="${esc(c.state)}">${esc(c.state)}</div><small>${esc(c.detail)}</small></article>`).join('');measurements.innerHTML=ms.map(m=>`<tr><td><a href="#" onclick="details('${m.id}')">${esc(m.id)}</a></td><td>${esc(m.status)}</td><td>${new Date(m.updatedAt).toLocaleString()}</td><td>${m.d50??'—'} / ${m.d80??'—'}</td><td><button class="danger" onclick="danger('${m.id}','cancel',${m.version})">Отмена</button> <button onclick="danger('${m.id}','retry',${m.version})">Повтор</button></td></tr>`).join('')}catch(e){system.innerHTML=`<p class="error">${esc(e.message)}</p>`}}
async function details(id){try{const m=await api('/measurements/'+id);alert(JSON.stringify(m,null,2))}catch(e){alert(e.message)}}
async function startMeasurement(){const excavatorId=prompt('Экскаватор');if(!excavatorId)return;const coordinateSystemId=prompt('Система координат');if(!coordinateSystemId)return;await api('/measurements',{method:'POST',body:JSON.stringify({excavatorId,coordinateSystemId,reason:'Manual operator start'})});refresh()}
function danger(id,command,version){confirmText.textContent=`${command}: ${id}`;confirmYes.onclick=async()=>{confirm.close();await api(`/measurements/${id}/${command}`,{method:'POST',body:JSON.stringify({commandId:crypto.randomUUID(),expectedVersion:version,reason:'Confirmed by engineer',confirmed:true})});refresh()};confirm.showModal()}
refresh();setInterval(refresh,10000);
</script></body></html>
""";
}
