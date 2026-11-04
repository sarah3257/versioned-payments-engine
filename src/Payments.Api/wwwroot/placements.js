let selectedPlacement=null, placementRows=[], placementSearchTimer, placementSearchSequence=0;
const money=value=>new Intl.NumberFormat('he-IL',{minimumFractionDigits:2,maximumFractionDigits:3}).format(value);
async function initNewPlacement(){
 const [institutions,catalog]=await Promise.all([api('institutions'),api('catalog')]);
 const selectedInstitution=$('placementInstitution').value,selectedModel=$('placementModel').value;
 $('placementInstitution').innerHTML=institutions.filter(i=>i.isActive).map(i=>`<option value="${i.id}">${esc(i.name)} (${i.id})</option>`).join('');
 $('placementModel').innerHTML=[...new Set(catalog.models.map(m=>m.modelId))].map(id=>`<option value="${id}">מודל ${id}</option>`).join('');
 if(selectedInstitution)$('placementInstitution').value=selectedInstitution;if(selectedModel)$('placementModel').value=selectedModel;
 if(!$('placementId').value)$('placementId').value='placement-'+crypto.randomUUID().slice(0,8);
 if(!$('creationRequestKey').value)$('creationRequestKey').value='create-'+crypto.randomUUID();
}
async function createNewPlacement(event){
 event.preventDefault();if($('createPlacement').disabled)return;
 const body={requestKey:$('creationRequestKey').value.trim(),placement:{id:$('placementId').value.trim(),modelId:Number($('placementModel').value),institution:Number($('placementInstitution').value),from:$('placementFrom').value,to:$('placementTo').value,supportLevel:Number($('placementSupport').value),institutionType:$('placementType').value}};
 const controls=[...$('placementForm').querySelectorAll('input,select,button')];controls.forEach(x=>x.disabled=true);
 $('placementStatus').textContent='בודק את ההשמה ומחשב את חודשי התקופה. הנתונים נשמרים יחד רק אם כל החישובים מצליחים…';
 try{
  const r=await api('placements/create-and-calculate',body);
  $('placementStatus').textContent=`${r.repeated?'הבקשה כבר בוצעה; לא נוצרו כפילויות.':'ההשמה נוצרה והחישובים נשמרו.'} ${r.months.length} חודשים · חושב עד ${r.calculationEnd}`;
  $('creationResult').innerHTML=`<div class="badge">השמה ${esc(r.placement.id)} · ${esc(r.placement.institutionName)} · מזהה בקשה ${esc(body.requestKey)}</div><table><thead><tr><th>חודש</th><th>גרסת מודל</th><th>סכום</th></tr></thead><tbody>${r.months.map(x=>`<tr><td>${esc(x.month.slice(0,7))}</td><td>${x.modelVersion}</td><td>${money(x.total)} ₪</td></tr>`).join('')}</tbody></table><button id="showCreatedPlacement">פתיחת ההשמה והטרנזקציות</button> <button id="anotherPlacement" class="secondary">השמה חדשה נוספת</button>`;
  $('showCreatedPlacement').onclick=async()=>{await openView('placements');$('placementSearch').value=r.placement.id;await loadPlacements();await showPlacement(r.placement.id);};
  $('anotherPlacement').onclick=()=>{$('placementId').value='';$('creationRequestKey').value='';$('creationResult').innerHTML='';$('placementStatus').textContent='';controls.forEach(x=>x.disabled=false);initNewPlacement().catch(e=>$('placementStatus').textContent=e.message);};
 }catch(e){$('placementStatus').textContent=e.message+' ניתן לנסות שוב עם אותם מזהי השמה ובקשה; בקשה שהושלמה לא תחושב פעמיים.';controls.forEach(x=>x.disabled=false);}
}
async function loadPlacements(){
 const sequence=++placementSearchSequence,q=$('placementSearch').value.trim();
 $('placementListStatus').textContent='טוען השמות…';
 try{
  const r=await api('placements/search?q='+encodeURIComponent(q));if(sequence!==placementSearchSequence)return;
  placementRows=r.items;$('placementListStatus').textContent=r.total?`${r.total} השמות${r.total>100?' · מוצגות 100 הראשונות, צמצמו את החיפוש':''}`:'לא נמצאו השמות מתאימות';
  $('placementRows').innerHTML=r.items.length?'<table><thead><tr><th>מזהה השמה</th><th>מסגרת</th><th>מודל</th><th>מתאריך</th><th>עד תאריך</th><th>פעולה</th></tr></thead><tbody>'+r.items.map(p=>`<tr><td><strong>${esc(p.id)}</strong></td><td>${esc(p.institutionName)} (${p.institution})</td><td>${p.modelId}</td><td>${p.from}</td><td>${p.to}</td><td><button data-open-placement="${esc(p.id)}">טרנזקציות והיסטוריה</button></td></tr>`).join('')+'</tbody></table>':'';
  document.querySelectorAll('[data-open-placement]').forEach(b=>b.onclick=()=>showPlacement(b.dataset.openPlacement));
  if(selectedPlacement)await showPlacement(selectedPlacement);
 }catch(e){if(sequence===placementSearchSequence)$('placementListStatus').textContent=e.message;throw e;}
}
function transactionDetails(h){return `<details><summary>פירוט ${h.details.length} שורות רכיב</summary><div class="table-scroll"><table><tr><th>רכיב</th><th>גרסה</th><th>פריט</th><th>סכום</th><th>נימוק</th></tr>${h.details.map(d=>`<tr><td>${d.componentId}</td><td>${d.componentVersion}</td><td>${esc(d.description)}</td><td>${money(d.amount)}</td><td>${esc(d.explanation)}</td></tr>`).join('')}</table></div></details>`;}
async function showPlacement(id){
 selectedPlacement=id;$('placementDetail').hidden=false;$('placementDetail').innerHTML='<p role="status">טוען טרנזקציות להשמה…</p>';
 try{
  const r=await api('placements/'+encodeURIComponent(id)+'/transactions');if(selectedPlacement!==id)return;
  const p=r.placement,months=[...new Set(r.transactions.map(x=>x.header.paymentMonth))].sort();
  $('placementDetail').innerHTML=`<div class="record-heading"><div><span class="eyebrow">תיק השמה</span><h3>${esc(p.id)}</h3><p>${esc(p.institutionName)} · מודל ${p.modelId} · ${p.from} עד ${p.to}</p></div><button id="refreshPlacementRecord" class="secondary">↻ רענון טרנזקציות</button></div><p>מזהה בקשת יצירה: <bdi>${esc(p.creationRequestKey||'השמת הדגמה / רשומה קודמת')}</bdi></p><p>לכל חודש מוצגת התוצאה התקפה. פתחו את ההיסטוריה כדי לראות גרסאות קודמות, ביטולים ושינוי בעקבות פרסום מודל. הסכום התקף אינו סכום כל הרשומות ההיסטוריות.</p>${months.length?months.map(month=>{
   const rows=r.transactions.filter(x=>x.header.paymentMonth===month).sort((a,b)=>String(a.createdAt).localeCompare(String(b.createdAt))||Number(a.header.entryType==='Reversal')-Number(b.header.entryType==='Reversal'));
   const current=rows.find(x=>x.isCurrent),h=current?.header;
   return `<article class="month-card"><div class="record-heading"><h3>${esc(month.slice(0,7))}</h3>${h?`<span class="badge">גרסת מודל ${h.modelVersion} · ${money(h.total)} ₪ · ${esc(paymentText(h.paymentStatus))}</span>`:'<span>אין תוצאה תקפה מקושרת</span>'}</div>${h?transactionDetails(h):''}<details><summary>היסטוריית החודש · ${rows.length} רשומות${rows.some(x=>x.batchToken)?' · עודכן בפרסום מודל':''}</summary><table><thead><tr><th>מצב</th><th>גרסת מודל</th><th>סכום</th><th>תשלום</th><th>מקור השינוי</th><th>מזהה בקשה</th></tr></thead><tbody>${rows.map(x=>`<tr><td>${x.isCurrent?'תקפה':x.header.entryType==='Reversal'?'ביטול במינוס':'היסטורית'}</td><td>${x.header.modelVersion}</td><td>${money(x.header.total)} ₪</td><td>${esc(paymentText(x.header.paymentStatus))}</td><td>${x.batchToken?'פרסום מודל למסגרת שנבחרה':'חישוב השמה'}</td><td><bdi>${esc(x.requestKey)}</bdi></td></tr><tr><td colspan="6">${transactionDetails(x.header)}</td></tr>`).join('')}</tbody></table></details></article>`;
  }).join(''):'<p class="notice">עדיין אין טרנזקציות שמורות להשמה הזאת.</p>'}`;
  $('refreshPlacementRecord').onclick=()=>{clearReads();return showPlacement(id);};
 }catch(e){if(selectedPlacement===id)$('placementDetail').textContent=e.message;}
}
