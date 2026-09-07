const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../web/wwwroot/app.js'), 'utf8').replace(/boot\(\)\.catch\(error=>showNotFound\(error.message\)\);/, '');
function harness(fetch) {
  const nodes=new Map();
  const node=()=>({innerHTML:'',textContent:'',hidden:false,value:'',querySelectorAll:()=>[],insertAdjacentHTML(_,html){this.innerHTML+=html;}});
  const context={fetch,AbortController,URLSearchParams,Date,console,window:{},location:{pathname:'/'},document:{querySelector(selector){if(!nodes.has(selector))nodes.set(selector,node());return nodes.get(selector);},querySelectorAll:()=>[],createElement(){const n=node();Object.defineProperty(n,'innerHTML',{get(){return n.textContent;},set(value){n.textContent=value;}});return n;}}};
  vm.createContext(context);vm.runInContext(source,context);return {context,nodes};
}
const ok=body=>Promise.resolve({ok:true,json:async()=>body});
(async()=>{
  for(const failed of [null,'stats','points','history','records']){
    const {context,nodes}=harness(url=>{
      if(failed&&url.includes('/'+failed))return Promise.reject(new Error('Panel unavailable'));
      if(url.endsWith('/points'))return ok({ranking:null});
      if(url.endsWith('/stats'))return ok({worldRecords:0,stageRecords:0,replays:0});
      if(url.includes('/records'))return ok({records:[],pagination:{total:0}});
      if(url.includes('/history'))return ok({history:[],pagination:{total:0}});
      return ok({playerName:'New surfer',steamId:'123',completions:0,mainRecords:0,bonusRecords:0,trackedTimeUs:0});
    });
    await vm.runInContext("showPlayerPage('123')",context);
    assert.match(nodes.get('#detailPage').innerHTML,/New surfer/);
    assert.doesNotMatch(nodes.get('#detailPage').innerHTML,/eyebrow">404/);
    if(!failed)assert.match(nodes.get('#detailPage').innerHTML,/Unranked/);
    if(failed==='records')assert.match(nodes.get('#playerRecords').innerHTML,/Panel unavailable/);
    if(failed==='history')assert.match(nodes.get('#historyRows').innerHTML,/Panel unavailable/);
  }
  const pending=[];
  const {context}=harness((url,{signal})=>new Promise((resolve,reject)=>{signal.addEventListener('abort',()=>reject(Object.assign(new Error('Aborted'),{name:'AbortError'})));pending.push({resolve,signal});}));
  const first=vm.runInContext("json('/api/maps/one/stages/1','detail-records').catch(e=>e.name)",context);
  const second=vm.runInContext("json('/api/maps/one/leaderboard','detail-records')",context);
  assert.equal(pending[0].signal.aborted,true);
  pending[1].resolve({ok:true,json:async()=>({latest:true})});
  assert.equal(await first,'AbortError');assert.equal((await second).latest,true);
  const navigation=vm.runInContext("json('/api/activity').catch(e=>e.name)",context);vm.runInContext('cancelRequests()',context);assert.equal(await navigation,'AbortError');
  console.log('PASS: unranked profile, four independent panel failures, superseded route request, navigation cancellation');
})().catch(error=>{console.error(error);process.exitCode=1;});
