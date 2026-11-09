// P984 — the ONE output transform of the linear HDR scene path.
//
// The 3-D scene renders into a floating-point target (RGBA16F) and this
// fullscreen pass is the single place where a curve + the display transfer are
// applied, immediately before the HUD/overlay pass (which is never graded).
//
// Two passes, one technique:
//   Neutral — the hue-preserving peak shoulder that the PBR shader
//             (NativePbr.fx's ToneMap) used to run inside itself, moved here so
//             it runs exactly once for the whole scene, then saturate(). Below
//             the onset it is the identity, which keeps today's picture.
//   Aces    — Narkowicz's public ACES filmic fit (not FS2 Open code), for
//             comparison: decode the stored display-encoded value back to linear,
//             apply the fit, encode once.
//
// Storage domain: the float scene target holds display-encoded (sRGB) RGB with
// headroom (values may exceed 1.0). Neutral therefore needs no decode/encode
// round trip — decoding then re-encoding the same value would be the identity —
// while Aces decodes into the linear domain it is defined in.
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float Exposure = 1.0f;    // unity: no auto-exposure in this slice
float Threshold = 0.8f;   // neutral shoulder onset (HD-6's TonemapThreshold)
float Slope = 0.5f;       // neutral shoulder roll-off (HD-6's TonemapSlope)

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

// Exact sRGB transfer functions (IEC 61966-2-1), the transfer the ACES path
// works through; the neutral path needs neither.
float3 SrgbDecode(float3 c)
{
    float3 lo = c / 12.92;
    float3 hi = pow((c + 0.055) / 1.055, 2.4);
    return lerp(hi, lo, step(c, 0.04045));
}

float3 SrgbEncode(float3 c)
{
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(hi, lo, step(c, 0.0031308));
}

float3 Aces(float3 x)
{
    x = max(x, 0.0);
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

// Neutral: identity below Threshold, hue-preserving shoulder above it (same
// equation as NativePbr.fx's old ToneMap), then the display clamp.
float4 PS_Neutral(in VS_OUT i) : COLOR0
{
    float3 c = tex2D(SceneSampler, i.UV).rgb * Exposure;
    float peak = max(c.r, max(c.g, c.b));
    if (peak > Threshold)
    {
        float excess = max(peak - Threshold, 0.0);
        float mapped = Threshold + excess / (1.0 + excess / Slope);
        c *= mapped / max(peak, 1e-5);
    }
    return float4(saturate(c), 1);
}

// Aces: decode -> filmic fit -> encode (one transfer pair, linear domain in
// between). Exposure is applied in linear space, where it belongs.
float4 PS_Aces(in VS_OUT i) : COLOR0
{
    float3 lin = SrgbDecode(max(tex2D(SceneSampler, i.UV).rgb * Exposure, 0.0));
    return float4(SrgbEncode(Aces(lin)), 1);
}

technique Output
{
    pass Neutral
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_Neutral();
    }
    pass Aces
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS_Aces();
    }
}
