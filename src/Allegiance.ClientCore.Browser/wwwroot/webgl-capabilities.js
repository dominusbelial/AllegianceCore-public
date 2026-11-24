// Query the actual KNI context, after its first draw. These calls never request permissions.
(() => {
  'use strict';
  window.allegianceWebGLCapabilities = gl => {
    const debug = gl.getExtension('WEBGL_debug_renderer_info');
    const highp = gl.getShaderPrecisionFormat(gl.FRAGMENT_SHADER, gl.HIGH_FLOAT);
    return {
      version: gl.getParameter(gl.VERSION),
      shadingLanguageVersion: gl.getParameter(gl.SHADING_LANGUAGE_VERSION),
      colorBufferFloat: !!gl.getExtension('EXT_color_buffer_float'),
      colorBufferHalfFloat: !!gl.getExtension('EXT_color_buffer_half_float'),
      textureFloatLinear: !!gl.getExtension('OES_texture_float_linear'),
      maxTextureUnits: gl.getParameter(gl.MAX_TEXTURE_IMAGE_UNITS),
      maxCombinedTextureUnits: gl.getParameter(gl.MAX_COMBINED_TEXTURE_IMAGE_UNITS),
      fragmentHighp: !!highp && highp.precision > 0,
      fragmentHighpPrecision: highp ? {precision: highp.precision, rangeMin: highp.rangeMin, rangeMax: highp.rangeMax} : null,
      renderer: gl.getParameter(gl.RENDERER),
      unmaskedRenderer: debug ? gl.getParameter(debug.UNMASKED_RENDERER_WEBGL) : null
    };
  };
})();
