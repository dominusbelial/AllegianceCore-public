// P543 — BLOOM (bright-pass threshold + separable Gaussian) — STEP-0 SHADER
// SOURCE (DOCUMENTATION ONLY). Cannot compile on this Linux box (mgfxc needs
// Wine — see docs/reports/P543_VFX_SPIKE.md step 0). This is the production
// replacement for the spike's shader-free (un-thresholded) ShaderFreeBloom.
//
// Pipeline (all via mgfxc-compiled .mgfxo, runtime `new Effect(gd, bytes)`):
//   1) BrightPass: threshold the scene, keep only pixels above `Threshold`,
//      write to a half-res RT.
//   2) BlurH: 9-tap Gaussian along +X into a second RT.
//   3) BlurV: 9-tap Gaussian along +Y (Direction=1) into the final bloom RT.
//   4) Composite: additive scene + bloom (no shader needed, or a composite
//      pass in the same technique).
// The spike proved the geometry with ShaderFreeBloom (¼+⅛ downsample +
// additive) at ~the same frame cost; the shader version adds a real threshold
// and a cleaner Gaussian.
//
// Compile:
//   DesktopGL: mgfxc Bloom.fx BloomGL.mgfxo /Profile:OpenGL
//   Android:   mgfxc Bloom.fx BloomAndroid.mgfxo /Profile:OpenGL
// Runtime load: new Effect(GraphicsDevice, File.ReadAllBytes("BloomGL.mgfxo"))

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float2 TexelSize;      // 1/sourceDim — step between texels
float Threshold = 0.8f; // bright-pass cutoff (0..1, on linear-ish RGB)
float BloomScale = 1.0f;
int Direction = 0;     // 0 = horizontal, 1 = vertical (Gaussian taps)

texture SceneTexture;
sampler SceneSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = Clamp; AddressV = Clamp;
};

struct VS_OUT
{
    float4 Pos : POSITION0;
    float2 UV : TEXCOORD0;
};

VS_OUT VS(in float4 pos : POSITION0, in float2 uv : TEXCOORD0)
{
    VS_OUT o;
    o.Pos = float4(pos.xy, 0, 1);   // fullscreen quad (NDC)
    o.UV = uv;
    return o;
}

static const float W[9] = { 0.09030, 0.07730, 0.05664, 0.03767, 0.02358, 0.01338, 0.00691, 0.00328, 0.00144 };

float4 PS_BrightPass(in VS_OUT i) : COLOR0
{
    float3 c = tex2D(SceneSampler, i.UV).rgb;
    float lum = dot(c, float3(0.299, 0.587, 0.114));
    // Soft knee: scale everything, kill sub-threshold.
    float bright = max(lum - Threshold, 0.0);
    c *= bright / max(lum, 1e-5);
    return float4(c * BloomScale, 1);
}

float4 PS_Blur(in VS_OUT i) : COLOR0
{
    float2 step = Direction == 0 ? float2(TexelSize.x, 0) : float2(0, TexelSize.y);
    float4 acc = tex2D(SceneSampler, i.UV) * W[0];
    for (int t = 1; t < 9; t++)
    {
        acc += tex2D(SceneSampler, i.UV + step * t) * W[t];
        acc += tex2D(SceneSampler, i.UV - step * t) * W[t];
    }
    return acc;
}

technique Bloom
{
    pass BrightPass
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_BrightPass();
    }
    pass Blur
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_Blur();
    }
}
