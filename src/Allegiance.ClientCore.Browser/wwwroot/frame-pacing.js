// Browser presentation only. No clock, input cadence or simulation changes.
(() => {
  class FramePacing {
    constructor() { this.reset(); }
    reset() {
      this.previous = null; this.windowStart = null; this.gaps = []; this.scale = 1; this.slow = false;
      this.onTime = 0; this.overSince = null; this.underSince = null; this.lastChange = -Infinity; this.p95 = 0;
    }
    sample(now, hidden = false) {
      if (hidden) { this.previous = null; this.windowStart = null; this.gaps = []; this.overSince = this.underSince = null; this.onTime = 0; return; }
      if (this.previous === null) { this.previous = now; this.windowStart = now; return; }
      const gap = now - this.previous; this.previous = now;
      if (gap <= 0) return;
      if (gap > 100) { this.slow = true; this.onTime = 0; }
      else if (gap < 34) { if (++this.onTime >= 30) this.slow = false; }
      else this.onTime = 0;
      this.gaps.push({now, gap});
      while (this.gaps.length && this.gaps[0].now < now - 2000) this.gaps.shift();
      // Require a full two-second rolling window, then sustained pressure.
      if (now - this.windowStart < 2000) return;
      const sorted = this.gaps.map(x => x.gap).sort((a,b) => a-b);
      this.p95 = sorted[Math.ceil(sorted.length * .95) - 1];
      this.overSince = this.p95 > 40 ? this.overSince ?? now : null;
      this.underSince = this.p95 < 34 ? this.underSince ?? now : null;
      if (this.overSince !== null && now - this.overSince >= 2000 && now - this.lastChange >= 2000 && this.scale > .5) {
        this.scale = this.scale === 1 ? .75 : .5; this.changed(now);
      } else if (this.underSince !== null && now - this.underSince >= 5000 && now - this.lastChange >= 5000 && this.scale < 1) {
        this.scale = this.scale === .5 ? .75 : 1; this.changed(now);
      }
    }
    changed(now) { this.lastChange = now; this.overSince = this.underSince = null; }
    snapshot() { return {scale:this.scale,slow:this.slow,p95:this.p95,onTime:this.onTime}; }
  }
  window.FramePacing = FramePacing;
})();
