// LOOK-1: thresholded bloom; hue-preserving shoulder on added highlights.
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
texture BloomTexture;
sampler BloomSampler = sampler_state { Texture=<BloomTexture>; MinFilter=LINEAR; MagFilter=LINEAR; MipFilter=LINEAR; AddressU=Clamp; AddressV=Clamp; };
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
    float lum = max(c.r, max(c.g, c.b));
    // Quadratic knee is continuous at the HDR threshold. Peak-channel
    // extraction retains orange fire and blue shields instead of favouring white.
    float knee=.15;
    float soft=clamp(lum-Threshold+knee,0,2*knee);
    float bright=max(lum-Threshold,soft*soft/(4*knee));
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
    return acc / .53070;
}

float4 PS_Composite(in VS_OUT i) : COLOR0
{
    float3 scene=tex2D(SceneSampler,i.UV).rgb;
    float3 glow=tex2D(BloomSampler,i.UV).rgb;
    // Reserve headroom per channel. A dark, saturated nebula gets a small
    // coloured halo; an already bright core receives no extra white clipping.
    return float4(scene + (1-scene)*(1-exp(-glow*.65)),1);
}

technique Bloom
{
    pass BrightPass
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_BrightPass();
    }
    pass Composite { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PS_Composite(); }
    pass Blur
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_Blur();
    }
}
