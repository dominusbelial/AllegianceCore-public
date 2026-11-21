const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
function fixture(menu=true){
 const calls=[],queue=[];let time=0;
 const canvas={width:800,height:480,parentElement:{getBoundingClientRect:()=>({width:800,height:480})}};
 const element=()=>({append(){},replaceChildren(){},addEventListener(){},getContext:()=>null});
 const app=element(),note={};
 const document={hidden:false,currentScript:{dataset:{}},fullscreenElement:null,
  getElementById:id=>id==='theCanvas'?canvas:id==='pacing-note'?note:app,createElement:element};
 const window={addEventListener(){},devicePixelRatio:1};if(menu)window.menuLayout=()=>{};
 const context={window,document,clearTimeout(){},cancelAnimationFrame(){},innerWidth:800,innerHeight:480,
  performance:{now:()=>time},requestAnimationFrame:callback=>{queue.push(callback);return queue.length}};
 vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/frame-pacing.js','utf8'),context);
 // The intentional capability refusal avoids any runtime/network boot; the
 // actual production startFrames closure is defined before that check.
 vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/preview.js','utf8'),context);
 window.preview.startFrames({invokeMethod:(name,...args)=>{calls.push({name,args,time});return name==='TickDotNet'}});
 return {calls,window,step(gap){time+=gap;queue.shift()(time)}};
}
test('real RAF bridge lowers effects once before changing the drawing buffer, then reverses recovery',()=>{
 const f=fixture();f.step(0);for(let i=0;i<82;i++)f.step(50);
 let effects=f.calls.filter(c=>c.name==='EffectLoad');assert.deepEqual(effects.map(c=>c.args[0]),[true,false]);
 assert.equal(f.calls.filter(c=>c.name==='PresentationSize').length,0);
 for(let i=0;i<50;i++)f.step(50);
 const firstResize=f.calls.find(c=>c.name==='PresentationSize');assert.ok(firstResize.time>effects[1].time);
 for(let i=0;i<1200;i++)f.step(16);
 effects=f.calls.filter(c=>c.name==='EffectLoad');assert.deepEqual(effects.map(c=>c.args[0]),[true,false,true]);
 assert.equal(f.window.preview.pacing().scale,1);
 const finalResize=f.calls.filter(c=>c.name==='PresentationSize').at(-1);assert.ok(effects[2].time>finalResize.time);
});
test('standalone scout preview does not invoke the menu-only effect-load API',()=>{
 const f=fixture(false);for(let i=0;i<100;i++)f.step(50);
 assert.equal(f.calls.filter(c=>c.name==='EffectLoad').length,0);
});
