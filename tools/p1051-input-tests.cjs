const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
function fixture(){
 class Bus{constructor(){this.handlers=new Map()}addEventListener(n,f){let a=this.handlers.get(n)||[];a.push(f);this.handlers.set(n,a)}removeEventListener(n,f){this.handlers.set(n,(this.handlers.get(n)||[]).filter(x=>x!==f))}dispatch(n,data={}){const event={isTrusted:true,code:'',key:'',repeat:false,altKey:false,buttons:0,target:this,preventDefault(){this.prevented=true},...data};for(const f of this.handlers.get(n)||[])f(event);return event}closest(){return null}}
 const window=new Bus(),document=new Bus(),canvas=new Bus(),calls=[],order=[],audio=[];let lockCalls=0,fullCalls=0;
 class AudioContext{constructor(){this.state='running';audio.push(this)}suspend(){this.state='suspended';return Promise.resolve()}resume(){this.state='running';return Promise.resolve()}}
 window.AudioContext=AudioContext;document.hidden=false;document.activeElement=canvas;document.pointerLockElement=null;
 document.getElementById=id=>id==='theCanvas'?canvas:{dataset:{state:'Flight'}};
 canvas.focus=()=>document.activeElement=canvas;
 canvas.requestPointerLock=()=>{lockCalls++;return Promise.reject(Error('fixture refusal'))};
 canvas.requestFullscreen=()=>{fullCalls++;order.push('fullscreen');document.fullscreenElement=canvas;return Promise.resolve()};
 document.exitFullscreen=()=>{document.fullscreenElement=null;return Promise.resolve()};
 document.exitPointerLock=()=>{document.pointerLockElement=null;document.dispatch('pointerlockchange')};
 vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/flight-input.js','utf8'),{window,document,console:{info(){}},Set,Promise});
 const detach=window.installFlightInput({invokeMethod:(name,...args)=>{calls.push([name,...args]);order.push(name)}});
 return {window,document,canvas,calls,order,audio,detach,get lockCalls(){return lockCalls},get fullCalls(){return fullCalls}};
}
test('pointer refusal retries only on a later fresh gesture and Escape releases it',async()=>{
 const f=fixture();f.canvas.dispatch('mousedown',{buttons:0});assert.equal(f.lockCalls,1);await Promise.resolve();assert.equal(f.lockCalls,1);
 f.window.dispatch('keyup',{code:'KeyW'});f.window.dispatch('keydown',{code:'ArrowUp',key:'ArrowUp'});assert.equal(f.lockCalls,2);
 f.document.pointerLockElement=f.canvas;f.document.dispatch('pointerlockchange');f.window.dispatch('keydown',{code:'Escape',key:'Escape'});
 assert.equal(f.document.pointerLockElement,null);assert.ok(f.calls.some(c=>c[0]==='ReleaseFlightInput'));f.detach();
});
test('Alt+Enter requests fullscreen inside the real event and never during installation',()=>{
 const f=fixture();assert.equal(f.fullCalls,0);f.window.dispatch('keydown',{code:'Enter',key:'Enter',altKey:true});assert.equal(f.fullCalls,1);assert.equal(f.order[0],'fullscreen');
 f.window.dispatch('keydown',{code:'Enter',key:'Enter',altKey:true,repeat:true});assert.equal(f.fullCalls,1);f.detach();
});
test('blur and hidden tab clear every held key/button, exit capture and suspend audio',()=>{
 const f=fixture(),audio=new f.window.AudioContext();assert.equal(audio.state,'suspended');
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});assert.equal(audio.state,'running');f.canvas.dispatch('mousedown',{buttons:1});
 f.document.pointerLockElement=f.canvas;f.window.dispatch('blur');assert.equal(f.document.pointerLockElement,null);assert.equal(audio.state,'suspended');
 const release=f.calls.findLastIndex(c=>c[0]==='ReleaseFlightInput');assert.ok(release>=0);assert.deepEqual(Array.from(f.calls.slice(0,release).findLast(c=>c[0]==='FlightKeys')[1]),[]);
 f.document.hidden=true;f.document.dispatch('visibilitychange');assert.equal(audio.state,'suspended');f.document.hidden=false;f.document.dispatch('visibilitychange');assert.equal(audio.state,'running');f.detach();
});
test('untrusted events never activate audio/pointer; canvas context menu and WebGL loss are contained',()=>{
 const f=fixture(),audio=new f.window.AudioContext();f.window.dispatch('keydown',{code:'KeyW',key:'w',isTrusted:false});assert.equal(f.lockCalls,0);assert.equal(audio.state,'suspended');
 assert.equal(f.canvas.dispatch('contextmenu').prevented,true);assert.equal(f.canvas.dispatch('webglcontextlost').prevented,true);assert.ok(f.calls.some(c=>c[0]==='ContextLost'));
 f.detach();const count=f.calls.length;f.window.dispatch('keydown',{code:'KeyW',key:'w'});assert.equal(f.calls.length,count);
});

test('relative steering reaches the input layer only under actual canvas capture',()=>{
 const f=fixture();f.canvas.dispatch('mousemove',{movementX:30,movementY:12});assert.ok(!f.calls.some(c=>c[0]==='PointerMotion'));
 f.document.pointerLockElement=f.canvas;f.document.dispatch('pointerlockchange');f.canvas.dispatch('mousemove',{movementX:30,movementY:12});
 assert.deepEqual(f.calls.find(c=>c[0]==='PointerMotion'),['PointerMotion',30,12]);
 f.document.pointerLockElement=null;f.document.dispatch('pointerlockchange');const before=f.calls.filter(c=>c[0]==='PointerMotion').length;
 f.canvas.dispatch('mousemove',{movementX:90,movementY:30});assert.equal(f.calls.filter(c=>c[0]==='PointerMotion').length,before);f.detach();
});
