// DOM regression tests. No Chromium or external modules required.
const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
function fixture(source=fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/flight-input.js','utf8')){
 class Bus{
  constructor(kind=''){this.kind=kind;this.listeners=new Map()}
  addEventListener(name,handler){const a=this.listeners.get(name)||[];a.push(handler);this.listeners.set(name,a)}
  removeEventListener(name,handler){this.listeners.set(name,(this.listeners.get(name)||[]).filter(h=>h!==handler))}
  dispatch(name,data={}){const event={target:this,code:'',key:'',buttons:0,isTrusted:true,preventDefault(){this.prevented=true},...data};for(const f of this.listeners.get(name)||[])f(event);return event}
  closest(selector){if(this.kind==='button'&&/(^|,)button| button/.test(selector))return this;if(this.kind==='select'&&/select/.test(selector))return this;if(this.kind==='input'&&/input/.test(selector))return this;return null}
 }
 const window=new Bus(),document=new Bus(),canvas=new Bus(),button=new Bus('button'),input=new Bus('input'),select=new Bus('select'),calls=[];
 let locks=0,state='Flight';document.activeElement=canvas;document.hidden=false;
 document.getElementById=id=>id==='theCanvas'?canvas:{dataset:{state}};
 canvas.focus=()=>document.activeElement=canvas;
 canvas.requestPointerLock=()=>{locks++;return Promise.reject(Error('refused'))};
 document.exitPointerLock=()=>{document.pointerLockElement=null;document.dispatch('pointerlockchange')};
 document.documentElement={requestFullscreen:()=>Promise.resolve()};
 vm.runInNewContext(source,{window,document,navigator:{getGamepads:()=>[]},console:{info(){}},Set,Promise});
 const detach=window.installFlightInput({invokeMethod(name,...args){calls.push([name,...args]);return name==='MouseToggleKey'&&args[0].includes('KeyM')}});
 return {window,document,canvas,button,input,select,calls,detach,get locks(){return locks},set state(value){state=value},keys(){return Array.from(calls.findLast(c=>c[0]==='FlightKeys')?.[1]||[])}};
}

test('ordinary keys never capture or retry a previously refused pointer lock',async()=>{
 const f=fixture();f.window.dispatch('keydown',{code:'ArrowUp'});assert.equal(f.locks,0);
 f.canvas.dispatch('mousedown');assert.equal(f.locks,1);await Promise.resolve();
 f.window.dispatch('keydown',{code:'ArrowRight'});assert.equal(f.locks,1);assert.deepEqual(f.keys(),['ArrowUp','ArrowRight']);
 f.detach();
});
test('only the live mouse-flight toggle is a keyboard capture gesture',async()=>{
 const f=fixture();f.window.dispatch('keydown',{code:'KeyM'});assert.equal(f.locks,1);await Promise.resolve();
 f.window.dispatch('keydown',{code:'KeyM',repeat:true});assert.equal(f.locks,1);
 f.window.dispatch('keyup',{code:'KeyM'});f.window.dispatch('keydown',{code:'KeyM'});assert.equal(f.locks,2);f.detach();
});
test('granting pointer lock preserves held steering, losing it releases every key',()=>{
 const f=fixture();f.window.dispatch('keydown',{code:'ArrowLeft'});f.document.pointerLockElement=f.canvas;f.document.dispatch('pointerlockchange');
 assert.deepEqual(f.keys(),['ArrowLeft']);assert.ok(f.calls.some(c=>c[0]==='PointerCapture'&&c[1]===true));
 f.document.pointerLockElement=null;f.document.dispatch('pointerlockchange');assert.deepEqual(f.keys(),[]);
 assert.ok(f.calls.some(c=>c[0]==='ReleaseFlightInput'));f.detach();
});
test('a focused toolbar button cannot eat steering or accidentally fire itself with Space',()=>{
 const f=fixture();f.document.activeElement=f.button;
 assert.equal(f.window.dispatch('keydown',{code:'ArrowRight'}).prevented,true);assert.deepEqual(f.keys(),['ArrowRight']);assert.equal(f.document.activeElement,f.canvas);
 f.document.activeElement=f.button;assert.equal(f.window.dispatch('keydown',{code:'Space'}).prevented,true);assert.ok(f.keys().includes('Space'));f.detach();
});
test('mouse toolbar buttons preserve focus and completing buttons or selects returns to the canvas',()=>{
 const f=fixture();assert.equal(f.document.dispatch('mousedown',{target:f.button}).prevented,true);
 f.document.activeElement=f.button;f.document.dispatch('click',{target:f.button});assert.equal(f.document.activeElement,f.canvas);
 f.document.activeElement=f.select;f.document.dispatch('change',{target:f.select});assert.equal(f.document.activeElement,f.canvas);f.detach();
});
test('Shift Tab leaves flight, Tab/Enter reach and activate toolbar, Escape returns to flight',()=>{
 const f=fixture();assert.notEqual(f.window.dispatch('keydown',{code:'Tab',shiftKey:true}).prevented,true);
 f.document.activeElement=f.button;assert.notEqual(f.window.dispatch('keydown',{code:'Tab'}).prevented,true);
 assert.notEqual(f.window.dispatch('keydown',{code:'Enter'}).prevented,true);assert.deepEqual(f.keys(),[]);
 f.window.dispatch('keydown',{code:'Escape'});assert.equal(f.document.activeElement,f.canvas);
 f.window.dispatch('keydown',{code:'ArrowDown'});assert.deepEqual(f.keys(),['ArrowDown']);f.detach();
});
test('text fields keep printable keys, shortcuts, arrows, Space, Enter and Alt Enter',()=>{
 const f=fixture();f.window.dispatch('keydown',{code:'ArrowLeft'});f.document.activeElement=f.input;f.document.dispatch('focusin');assert.deepEqual(f.keys(),[]);
 for(const data of [{code:'KeyW'},{code:'ArrowUp'},{code:'Space'},{code:'Enter'},{code:'Enter',altKey:true},{code:'KeyT',ctrlKey:true}])assert.notEqual(f.window.dispatch('keydown',data).prevented,true);
 assert.deepEqual(f.keys(),[]);f.detach();
});
test('deliverable refresh/search/save/browser chords are cancelled only in game',()=>{
 const f=fixture();for(const code of ['F5','F11','F12','KeyR','KeyF','KeyS','KeyI','Backspace','Space'])assert.equal(f.window.dispatch('keydown',{code,ctrlKey:code.startsWith('Key')}).prevented,true);
 f.state='Servers';assert.notEqual(f.window.dispatch('keydown',{code:'KeyR',ctrlKey:true}).prevented,true);f.detach();
});
test('blur and Escape drop held keys without allowing future keys to recapture',()=>{
 const f=fixture();f.document.pointerLockElement=f.canvas;f.document.dispatch('pointerlockchange');f.window.dispatch('keydown',{code:'ArrowLeft'});
 f.window.dispatch('blur');assert.deepEqual(f.keys(),[]);assert.equal(f.document.pointerLockElement,null);
 f.window.dispatch('focus');f.window.dispatch('keydown',{code:'ArrowUp'});assert.equal(f.locks,0);f.detach();
});

function menuFixture(){
 const calls=[],handlers=new Map(),canvas={closest:()=>null};let state='Menu';
 const document={activeElement:canvas,hasFocus:()=>true,getElementById:()=>({dataset:{get state(){return state}}}),addEventListener(){},removeEventListener(){}};
 const window={installFlightInput:()=>()=>{},addEventListener:(name,fn)=>handlers.set(name,fn),removeEventListener(){}};
 const instance={invokeMethod(name,...args){calls.push([name,...args]);if(name==='Metrics')return {screen:'Main'};if(name==='ReferenceKey')return args[0].includes('F1')||args[0].includes('F6')||args[0].includes('F2')&&args[0].includes('ShiftLeft')}};
 vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/menu.js','utf8'),{window,document,queueMicrotask:fn=>fn()});window.menuText(instance);
 return {window,document,calls,set state(value){state=value},key(data){const e={code:'',key:'',preventDefault(){this.prevented=true},...data};handlers.get('keydown')(e);return e}};
}
test('intro reference and screenshot keys cancel browser defaults using the live map',()=>{
 const f=menuFixture();assert.equal(f.key({code:'F1',key:'F1'}).prevented,true);
 assert.equal(f.key({code:'F6',key:'F6'}).prevented,true);
 assert.equal(f.key({code:'F2',key:'F2',shiftKey:true}).prevented,true);
 assert.deepEqual(Array.from(f.calls.findLast(c=>c[0]==='ReferenceKey')[1]),['F2','ShiftLeft']);
 assert.ok(!f.calls.some(c=>c[0]==='TextInput'));
});
test('the menu text adapter leaves session input to the flight owner',()=>{
 const f=menuFixture();for(const state of ['Flight','Hangar']){f.state=state;assert.notEqual(f.key({code:'KeyW',key:'w'}).prevented,true)}
 assert.ok(!f.calls.some(c=>c[0]==='TextInput'||c[0]==='ReferenceKey'));
});
test('menu text fields retain their keys and canvas text still reaches the menu',()=>{
 const f=menuFixture();f.document.activeElement={closest:()=>({})};assert.notEqual(f.key({code:'F1',key:'F1'}).prevented,true);
 f.document.activeElement={closest:()=>null};assert.equal(f.key({code:'KeyW',key:'w'}).prevented,true);
 assert.deepEqual(f.calls.findLast(c=>c[0]==='TextInput'),['TextInput','w']);
});
