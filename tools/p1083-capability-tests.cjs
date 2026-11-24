const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const context = {window:{}};
vm.runInNewContext(fs.readFileSync('src/Allegiance.ClientCore.Browser/wwwroot/webgl-capabilities.js', 'utf8'), context);
const selected = process.argv[2];
assert.ok(!selected || ['no-extensions','extensions'].includes(selected), 'optional case: no-extensions | extensions');
const cases = selected ? [selected === 'extensions'] : [false, true];
for (const enabled of cases) {
  const requested = [];
  const gl = {
    VERSION:1, SHADING_LANGUAGE_VERSION:2, MAX_TEXTURE_IMAGE_UNITS:3, MAX_COMBINED_TEXTURE_IMAGE_UNITS:4,
    RENDERER:5, FRAGMENT_SHADER:6, HIGH_FLOAT:7,
    getParameter: value => ({1:'WebGL 2.0',2:'WebGL GLSL ES 3.00',3:16,4:32,5:'masked',8:'unmasked'})[value],
    getExtension: name => { requested.push(name); return enabled ? (name === 'WEBGL_debug_renderer_info' ? {UNMASKED_RENDERER_WEBGL:8} : {}) : null; },
    getShaderPrecisionFormat: (stage, type) => { assert.equal(stage,6); assert.equal(type,7); return enabled ? {precision:23,rangeMin:127,rangeMax:127} : {precision:0,rangeMin:0,rangeMax:0}; }
  };
  const result = context.window.allegianceWebGLCapabilities(gl);
  assert.equal(result.version, 'WebGL 2.0'); assert.equal(result.maxTextureUnits, 16); assert.equal(result.maxCombinedTextureUnits, 32);
  for (const key of ['colorBufferFloat','colorBufferHalfFloat','textureFloatLinear','fragmentHighp']) assert.equal(result[key],enabled);
  assert.equal(result.renderer,'masked'); assert.equal(result.unmaskedRenderer,enabled?'unmasked':null);
  assert.equal(requested.length,4); // No permission APIs or extra contexts.
}
console.log('P1083_CAPABILITIES_PASS',cases.length,'cases',selected || 'all');
