const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm');
const install=require('./p1075-frame-probe.cjs');
function fixture(){
 const queued=[],gl={FRAMEBUFFER_BINDING:1,RGBA:2,UNSIGNED_BYTE:3,drawingBufferWidth:100,drawingBufferHeight:100,
  color:[68,34,136,255],target:null,drawElements(){},drawArrays(){},getParameter(){return this.target},
  readPixels(x,y,w,h,format,type,output){output.set(this.color)}};
 class Canvas{constructor(){this.id='theCanvas'}getContext(){return gl}}
 const sandbox={HTMLCanvasElement:Canvas,Uint8Array,document:{getElementById:()=>({dataset:{state:'Flight'}})},
  requestAnimationFrame:callback=>queued.push(callback)};sandbox.window=sandbox;
 vm.runInNewContext('('+install.toString()+')()',sandbox);
 new sandbox.HTMLCanvasElement().getContext('webgl2');sandbox.p1075Frames.enabled=true;
 return {gl,probe:sandbox.p1075Frames,frame(draw=true){sandbox.requestAnimationFrame(()=>{if(draw)gl.drawElements()});queued.shift()(1)}};
}
test('Every drawn flight frame is sampled and the KNI discard colour is detected',()=>{
 const f=fixture();for(let i=0;i<17;i++)f.frame();
 assert.equal(f.probe.sampled,17);assert.equal(f.probe.discarded.length,17);
 f.gl.color=[69,34,136,255];f.frame();assert.equal(f.probe.discarded.length,18);
});
test('Normal scenes and RAF callbacks without a draw cannot produce a discard finding',()=>{
 const f=fixture();f.gl.color=[0,0,0,255];f.frame();f.frame(false);
 assert.equal(f.probe.sampled,1);assert.equal(f.probe.discarded.length,0);
 f.gl.color=[100,30,130,255];f.frame();assert.equal(f.probe.discarded.length,0);
});
test('The sampler never switches or reads an offscreen render target',()=>{
 const f=fixture();f.gl.target={};f.frame();assert.equal(f.probe.sampled,0);
 f.gl.target=null;f.frame();assert.equal(f.probe.sampled,1);
});
