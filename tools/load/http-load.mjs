import { performance } from 'node:perf_hooks';
import { writeFileSync, mkdirSync } from 'node:fs';
const base=process.argv[2]??'http://127.0.0.1:5110',count=Number(process.argv[3]??100);
if(!/^http:\/\/(127\.0\.0\.1|localhost):\d+$/.test(base))throw Error('This harness is restricted to a local test server');
const reports=[];
for(const endpoint of ['simulations','calculations'])for(const parallelism of [1,8,32]){
 let next=0,errors=0;const times=[],statuses={};const prefix=crypto.randomUUID();const started=performance.now();
 await Promise.all(Array.from({length:parallelism},async()=>{while(true){const i=next++;if(i>=count)return;const start=performance.now();try{const response=await fetch(base+'/api/'+endpoint,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestKey:prefix+'-'+i,modelId:1,institution:3404,placementId:'load-'+i,eventDate:'2026-09-15',ownership:'Private',institutionType:'דירה',supportLevel:4,monthlyFactor:1})});statuses[response.status]=(statuses[response.status]??0)+1;const data=await response.json();if(!response.ok||data.header?.total!==2577.006)errors++;}catch{errors++;}times.push(performance.now()-start);}}));
 times.sort((a,b)=>a-b);const seconds=(performance.now()-started)/1000;const result={endpoint,count,parallelism,seconds,requestsPerSecond:count/seconds,p95Ms:times[Math.min(times.length-1,Math.floor(times.length*.95))],errors,statuses};reports.push(result);console.log(JSON.stringify(result));
}
mkdirSync('test-results',{recursive:true});writeFileSync('test-results/http-load.json',JSON.stringify({timestamp:new Date().toISOString(),scope:'local HTTP + SQLite, single process; 5 component business seed; independent request IDs',reports},null,2));
if(reports.some(r=>r.errors))process.exitCode=1;
