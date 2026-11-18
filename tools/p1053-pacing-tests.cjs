const {test} = require('node:test'), assert = require('node:assert/strict'), fs = require('node:fs'), vm = require('node:vm');
function guard() {
  const window = {};
  vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/frame-pacing.js','utf8'), {window});
  return new window.FramePacing();
}
function frames(p, start, interval, count) { for(let i=1;i<=count;i++) p.sample(start+i*interval); return start+interval*count; }
test('audio gap threshold is strict; thirty consecutive sub-34ms frames recover', () => {
  const p=guard();p.sample(0);p.sample(100);assert.equal(p.slow,false);p.sample(201);assert.equal(p.slow,true);
  let now=frames(p,201,16,29);assert.equal(p.slow,true);p.sample(now+=34);now=frames(p,now,16,29);assert.equal(p.slow,true);p.sample(now+16);assert.equal(p.slow,false);
});
test('sustained rolling p95 steps down twice and only five-second low p95 recovers', () => {
  const p=guard();p.sample(0);let now=frames(p,0,50,80);assert.equal(p.scale,.75);
  now=frames(p,now,50,42);assert.equal(p.scale,.5);now=frames(p,now,16,300);assert.equal(p.scale,.5);
  now=frames(p,now,16,150);assert.equal(p.scale,.75);now=frames(p,now,16,320);assert.equal(p.scale,1);
});
test('an isolated stall cannot lower scale; background gaps cannot authorize recovery', () => {
  const p=guard();p.sample(0);let now=frames(p,0,16,150);p.sample(now+=101);now=frames(p,now,16,150);assert.equal(p.scale,1);
  p.sample(now+10000,true);p.sample(now+20000);assert.equal(p.slow,false);assert.equal(p.gaps.length,0);
});
test('34-100ms work resets audio recovery without causing a new late-gap mute', () => {
  const p=guard();p.sample(0);let now=frames(p,0,45,200);assert.equal(p.slow,false);assert.equal(p.scale,.5);
  p.sample(now+=101);now=frames(p,now,45,100);assert.equal(p.slow,true);assert.equal(p.onTime,0);
});

test('very sparse frames still lower resolution instead of starving the rolling window',()=>{
 const p=guard();p.sample(0);let now=frames(p,0,1100,4);assert.equal(p.scale,.75);now=frames(p,now,1100,4);assert.equal(p.scale,.5);assert.equal(p.slow,true);
});
