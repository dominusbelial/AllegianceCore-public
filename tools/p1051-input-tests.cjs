const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const flushAudio=()=>new Promise(setImmediate);
function fixture({delayedResume=false}={}){
 let pads=[], polls=0;const navigator={getGamepads(){polls++;if(pads instanceof Error)throw pads;return pads}};
 class Bus{constructor(){this.handlers=new Map()}addEventListener(n,f){let a=this.handlers.get(n)||[];a.push(f);this.handlers.set(n,a)}removeEventListener(n,f){this.handlers.set(n,(this.handlers.get(n)||[]).filter(x=>x!==f))}dispatch(n,data={}){const event={isTrusted:true,code:'',key:'',repeat:false,altKey:false,buttons:0,target:this,preventDefault(){this.prevented=true},...data};for(const f of this.handlers.get(n)||[])f(event);return event}closest(){return null}}
 const window=new Bus(),document=new Bus(),canvas=new Bus(),calls=[],order=[],audio=[];let lockCalls=0,fullCalls=0;
 class AudioContext{constructor(){this.state='running';this.resumeRequests=[];audio.push(this)}suspend(){this.state='suspended';return Promise.resolve()}resume(){if(delayedResume)return new Promise(resolve=>this.resumeRequests.push(()=>{this.state='running';resolve()}));this.state='running';return Promise.resolve()}}
 window.setInterval=()=>1;window.clearInterval=()=>{};
 window.AudioContext=AudioContext;document.hidden=false;document.activeElement=canvas;document.pointerLockElement=null;
 document.getElementById=id=>id==='theCanvas'?canvas:{dataset:{state:'Flight'}};
 canvas.focus=()=>document.activeElement=canvas;
 canvas.requestPointerLock=()=>{lockCalls++;return Promise.reject(Error('fixture refusal'))};
 document.documentElement=new Bus();
 canvas.requestFullscreen=()=>{throw Error('Canvas-only fullscreen hides the guest disclosure label')};
 document.documentElement.requestFullscreen=()=>{fullCalls++;order.push('fullscreen');document.fullscreenElement=document.documentElement;return Promise.resolve()};
 document.exitFullscreen=()=>{document.fullscreenElement=null;return Promise.resolve()};
 document.exitPointerLock=()=>{document.pointerLockElement=null;document.dispatch('pointerlockchange')};
 vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/flight-input.js','utf8'),{window,document,navigator,console:{info(){}},Set,Promise});
 const detach=window.installFlightInput({invokeMethod:(name,...args)=>{calls.push([name,...args]);order.push(name)}});
 return {window,document,canvas,calls,order,audio,detach,set pads(value){pads=value},get polls(){return polls},get lockCalls(){return lockCalls},get fullCalls(){return fullCalls}};
}
test('pointer refusal retries only on a deliberate canvas click and Escape releases it',async()=>{
 const f=fixture();f.canvas.dispatch('mousedown',{buttons:0});assert.equal(f.lockCalls,1);await Promise.resolve();assert.equal(f.lockCalls,1);
 f.window.dispatch('keyup',{code:'KeyW'});f.window.dispatch('keydown',{code:'ArrowUp',key:'ArrowUp'});assert.equal(f.lockCalls,1);
 f.canvas.dispatch('mousedown',{buttons:0});assert.equal(f.lockCalls,2);
 f.document.pointerLockElement=f.canvas;f.document.dispatch('pointerlockchange');f.window.dispatch('keydown',{code:'Escape',key:'Escape'});
 assert.equal(f.document.pointerLockElement,null);assert.ok(f.calls.some(c=>c[0]==='ReleaseFlightInput'));f.detach();
});
test('Alt+Enter requests fullscreen inside the real event and never during installation',()=>{
 const f=fixture();assert.equal(f.fullCalls,0);f.window.dispatch('keydown',{code:'Enter',key:'Enter',altKey:true});assert.equal(f.fullCalls,1);assert.equal(f.order.filter(name=>name!=='PlayerActivityTick')[0],'fullscreen');assert.equal(f.document.fullscreenElement,f.document.documentElement);
 f.window.dispatch('keydown',{code:'Enter',key:'Enter',altKey:true,repeat:true});assert.equal(f.fullCalls,1);f.detach();
});
test('toolbar fullscreen keeps the page chrome and the same control can exit',()=>{
 const f=fixture(),button={closest:selector=>selector==='[data-flight-fullscreen]'?{}:null};
 f.document.dispatch('click',{target:button});assert.equal(f.document.fullscreenElement,f.document.documentElement);assert.equal(f.fullCalls,1);
 f.document.dispatch('click',{target:button});assert.equal(f.document.fullscreenElement,null);assert.equal(f.fullCalls,1);f.detach();
});
test('blur and hidden tab clear every held key/button, exit capture and suspend audio',async()=>{
 const f=fixture(),audio=new f.window.AudioContext();await flushAudio();assert.equal(audio.state,'suspended');
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});await flushAudio();assert.equal(audio.state,'running');f.canvas.dispatch('mousedown',{buttons:1});
 f.document.pointerLockElement=f.canvas;f.window.dispatch('blur');assert.equal(f.document.pointerLockElement,null);await flushAudio();assert.equal(audio.state,'suspended');
 const release=f.calls.findLastIndex(c=>c[0]==='ReleaseFlightInput');assert.ok(release>=0);assert.deepEqual(Array.from(f.calls.slice(0,release).findLast(c=>c[0]==='FlightKeys')[1]),[]);
 f.document.hidden=true;f.document.dispatch('visibilitychange');await flushAudio();assert.equal(audio.state,'suspended');f.document.hidden=false;f.document.dispatch('visibilitychange');await flushAudio();assert.equal(audio.state,'suspended');f.window.dispatch('focus');await flushAudio();assert.equal(audio.state,'running');f.detach();
});
test('untrusted events never activate audio/pointer; canvas context menu and WebGL loss are contained',async()=>{
 const f=fixture(),audio=new f.window.AudioContext();f.window.dispatch('keydown',{code:'KeyW',key:'w',isTrusted:false});assert.equal(f.lockCalls,0);await flushAudio();assert.equal(audio.state,'suspended');
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

test('gamepad is polled once per frame, translates physical Y signs and ignores non-standard pads',()=>{
 const f=fixture(),pad={index:0,connected:true,mapping:'standard',axes:[.2,-.4,.6,-.8],buttons:[{pressed:true,value:1}]};f.pads=[pad];
 f.window.gameFlight.frame();assert.equal(f.polls,1);const call=f.calls.findLast(c=>c[0]==='FlightGamepad');assert.deepEqual(Array.from(call[1]),[.2,.4,.6,.8]);
 pad.mapping='';f.window.gameFlight.frame();assert.equal(f.polls,2);assert.equal(f.calls.at(-1)[0],'ReleaseFlightInput');
 const before=f.calls.filter(c=>c[0]==='FlightGamepad').length;f.window.gameFlight.frame();assert.equal(f.calls.filter(c=>c[0]==='FlightGamepad').length,before);f.detach();
});
test('held pad cannot reassert on blur/hidden/disconnect; resumed focus uses a fresh sample',()=>{
 const f=fixture(),pad={index:0,connected:true,mapping:'standard',axes:[0,0,1,1],buttons:[]};f.pads=[pad];f.window.gameFlight.frame();
 f.window.dispatch('blur');let before=f.calls.filter(c=>c[0]==='FlightGamepad').length;f.window.gameFlight.frame();assert.equal(f.calls.filter(c=>c[0]==='FlightGamepad').length,before);
 f.window.dispatch('focus');f.window.gameFlight.frame();assert.equal(f.calls.filter(c=>c[0]==='FlightGamepad').length,before+1);
 f.document.hidden=true;f.document.dispatch('visibilitychange');before=f.calls.filter(c=>c[0]==='FlightGamepad').length;f.window.gameFlight.frame();assert.equal(f.calls.filter(c=>c[0]==='FlightGamepad').length,before);
 f.document.hidden=false;f.window.gameFlight.frame();f.window.dispatch('gamepaddisconnected',{gamepad:pad});assert.equal(f.calls.at(-1)[0],'ReleaseFlightInput');f.detach();
});
test('pacing suspension cannot enable audio without gesture or override focus and visibility',async()=>{
 const f=fixture(),audio=new f.window.AudioContext();f.window.gameFlight.pacing(true);f.window.gameFlight.pacing(false);await flushAudio();assert.equal(audio.state,'suspended');
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});await flushAudio();assert.equal(audio.state,'running');f.window.gameFlight.pacing(true);await flushAudio();assert.equal(audio.state,'suspended');
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});await flushAudio();assert.equal(audio.state,'suspended');f.window.dispatch('blur');f.window.gameFlight.pacing(false);await flushAudio();assert.equal(audio.state,'suspended');
 f.window.dispatch('focus');await flushAudio();assert.equal(audio.state,'running');f.document.hidden=true;f.document.dispatch('visibilitychange');f.window.gameFlight.pacing(true);f.window.gameFlight.pacing(false);await flushAudio();assert.equal(audio.state,'suspended');f.detach();
});

test('a refused Gamepad API releases an active pad without stopping keyboard flight',()=>{
 const f=fixture();f.pads=[{index:0,connected:true,mapping:'standard',axes:[0,0,1,0],buttons:[]}];f.window.gameFlight.frame();
 f.pads=Error('permissions policy');assert.doesNotThrow(()=>f.window.gameFlight.frame());assert.equal(f.calls.at(-1)[0],'ReleaseFlightInput');
 f.window.dispatch('keydown',{code:'ArrowUp',key:'ArrowUp'});assert.deepEqual(Array.from(f.calls.at(-1)[1]),['ArrowUp']);f.detach();
});

for(const boundary of ['hidden','blur','pacing','detach'])test('pending audio resume cannot outlive '+boundary,async()=>{
 const f=fixture({delayedResume:true}),audio=new f.window.AudioContext();await flushAudio();
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});assert.equal(audio.resumeRequests.length,1);assert.equal(audio.state,'suspended');
 if(boundary==='hidden'){f.document.hidden=true;f.document.dispatch('visibilitychange')}
 else if(boundary==='blur')f.window.dispatch('blur');
 else if(boundary==='pacing')f.window.gameFlight.pacing(true);
 else f.detach();
 // Real WebAudio ordering: a suspend on the currently suspended context is
 // immediately satisfied, while this preceding resume completes later.
 audio.resumeRequests.shift()();await flushAudio();assert.equal(audio.state,'suspended');
 assert.equal(audio.resumeRequests.length,0);if(boundary!=='detach')f.detach();
});
test('audio changes coalesce while an operation is pending and follow the latest state',async()=>{
 const f=fixture({delayedResume:true}),audio=new f.window.AudioContext();await flushAudio();
 f.window.dispatch('keydown',{code:'KeyW',key:'w'});
 for(let i=0;i<100;i++){f.window.dispatch('blur');f.window.dispatch('focus')}
 f.document.hidden=true;f.document.dispatch('visibilitychange');
 assert.equal(audio.resumeRequests.length,1);audio.resumeRequests.shift()();await flushAudio();assert.equal(audio.state,'suspended');
 f.document.hidden=false;f.document.dispatch('visibilitychange');assert.equal(audio.resumeRequests.length,1);
 audio.resumeRequests.shift()();await flushAudio();assert.equal(audio.state,'running');f.detach();
});
