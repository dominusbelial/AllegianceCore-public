// Installed before KNI creates its WebGL context. Read the presented framebuffer
// after every RAF that drew a flight frame; never switch or modify GPU targets.
module.exports = function installFrameProbe() {
  let gl, drawSerial = 0, lastFrame = -1;
  const original = HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = function(type, ...args) {
    const context = original.call(this, type, ...args);
    if ((type === 'webgl' || type === 'webgl2') && this.id === 'theCanvas' && context && gl !== context) {
      gl = context;
      for (const name of ['drawElements', 'drawArrays']) {
        const draw = gl[name].bind(gl);
        gl[name] = (...values) => { drawSerial++; return draw(...values); };
      }
    }
    return context;
  };
  window.p1075Frames = { sampled: 0, discarded: [], errors: [], enabled: false };
  const raf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = callback => raf(time => {
    callback(time);
    if (!p1075Frames.enabled || !gl || document.getElementById('online-panel')?.dataset.state !== 'Flight') return;
    try {
      const frame = drawSerial;
      if (frame === lastFrame) return;
      lastFrame = frame;
      // Default framebuffer is the actual presentation surface. Ignore a RAF
      // that did no game presentation (another callback may own an offscreen pass).
      if (gl.getParameter(gl.FRAMEBUFFER_BINDING) !== null) return;
      const pixel = new Uint8Array(4), samples = [];
      for (const x of [.2, .5, .8]) for (const y of [.25, .5, .75]) {
        gl.readPixels(Math.floor(gl.drawingBufferWidth*x), Math.floor(gl.drawingBufferHeight*y), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
        samples.push([...pixel]);
      }
      p1075Frames.sampled++;
      if (samples.every(([r,g,b]) => r>=66 && r<=71 && g>=32 && g<=36 && b>=133 && b<=139))
        p1075Frames.discarded.push({frame, time, samples});
    } catch(error) { p1075Frames.errors.push(String(error)); }
  });
};
