import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source=fs.readFileSync(new URL('../../src/Payments.Api/wwwroot/app.js',import.meta.url),'utf8');
let calls=0, release;
const context=vm.createContext({setTimeout,clearTimeout,AbortController,Map,Date,JSON,Error,
 document:{body:{classList:{toggle(){}}}},
 fetch:async()=>{calls++;return {ok:true,json:async()=>({revision:calls})};}});
vm.runInContext(source.slice(source.indexOf('const readCache'),source.indexOf('const helps'))+'\nconst viewTimes=new Map();globalThis.client={api,clearReads};',context);
const {api,clearReads}=context.client;
await Promise.all([api('catalog'),api('catalog')]);assert.equal(calls,1,'concurrent reads share one request');
await api('catalog');assert.equal(calls,1,'recent read uses cache');
await api('payment',{});await api('catalog');assert.equal(calls,3,'write invalidates cached reads');
clearReads();context.fetch=async()=>{calls++;return new Promise(resolve=>{release=()=>resolve({ok:true,json:async()=>({old:true})});});};
const old=api('catalog');clearReads();release();await old;
context.fetch=async()=>{calls++;return {ok:true,json:async()=>({fresh:true})};};
assert.equal((await api('catalog')).fresh,true,'read started before invalidation cannot repopulate cache');
clearReads();context.fetch=async()=>{calls++;return {ok:false,json:async()=>({error:'failure'})};};
await assert.rejects(api('broken'));await assert.rejects(api('broken'));
console.log('PASS: request deduplication, cache reuse, write invalidation, stale-read race, failure retry');
