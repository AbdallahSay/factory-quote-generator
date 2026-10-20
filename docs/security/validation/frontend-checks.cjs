// Executes only extracted renderer functions with fabricated responses and inert DOM sinks.
// No browser, storage secrets, production requests, or payload execution.
const fs=require('node:fs'); const vm=require('node:vm'); const assert=require('node:assert/strict');
function extract(src,name){const start=src.indexOf(`        async function ${name}(`);assert(start>=0);const end=src.indexOf('\n        }',start);assert(end>start);return src.slice(start,end+10);}
async function check(file,name,data,expected){
 const sinks={};const context={CONFIG:{API_BASE_URL:'https://synthetic.invalid'},Auth:{getAuthHeader:()=>({})},console,Date,fetch:async()=>({ok:true,status:200,json:async()=>data}),document:{getElementById:id=>sinks[id]??=( {innerHTML:'',textContent:'',classList:{add(){},remove(){}}})},Swal:{fire(){}}};
 vm.createContext(context);vm.runInContext(extract(fs.readFileSync(file,'utf8'),name),context);await vm.runInContext(`${name}()`,context);assert(Object.values(sinks).some(s=>s.innerHTML.includes(expected)));console.log(`PASS ${file} ${name}: unescaped marker reaches innerHTML`);
}
(async()=>{
 const marker='<img src=x onerror="globalThis.__auditMarker=1">';
 for(const file of ['frontend/admin.html','backend/wwwroot/admin.html']){
  await check(file,'loadLogs',[{id:1,timestamp:'2026-10-20T00:00:00Z',action:marker,entityId:'Synthetic',user:null}],marker);
  await check(file,'loadQuotes',[{id:1,quoteNumber:'synthetic',clientName:marker,totalAmount:1,createdAt:'2026-10-20T00:00:00Z',telegramDeliveries:[]}],marker);
 }
 await check('frontend/admin.html','loadTelegramBotInfo',{isConfigured:true,botUsername:'synthetic',recentChats:[{chatId:123,firstName:marker}]},marker);
 const s={};const c={localStorage:{getItem:()=> 'synthetic'},sessionStorage:{getItem:()=>null},CONFIG:{API_BASE_URL:'https://synthetic.invalid'},console:{warn(){}},fetch:async()=>{throw new Error('synthetic offline')}};vm.createContext(c);vm.runInContext(fs.readFileSync('frontend/auth.js','utf8')+';globalThis.auditAuth=Auth',c);assert.equal(await c.auditAuth.verify('admin'),true);console.log('PASS Auth.verify returns true after network failure (UI only)');
})().catch(e=>{console.error(e);process.exitCode=1});
