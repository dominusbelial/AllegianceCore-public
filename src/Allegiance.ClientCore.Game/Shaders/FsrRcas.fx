// AMD FidelityFX FSR1, MIT, Copyright (c) 2021 Advanced Micro Devices, Inc.
// Port of ffx_fsr1.h at a21ffb8f6c13233ba336352bdff293894c706575. Full notice in NOTICE.
// SM3: point samples replace texture gathers; full precision reciprocal replaces bit approximations.
//
// P989 — the two *Graded* passes fold the HDR output transform (Tonemap.fx) into
// this pass: RCAS writes the display-referred image straight to the 8-bit final
// target, so the 4K HDR path does not need the extra full-resolution RGBA16F
// intermediate. The equations are the same ones Tonemap.fx uses (neutral
// shoulder / ACES fit + exposure + world saturation); the renderer only takes
// this path when it has been proven pixel-equivalent to the unfolded one.
float2 InputSize;
texture SceneTexture;
sampler SceneSampler = sampler_state { Texture = <SceneTexture>; MinFilter = POINT; MagFilter = POINT; MipFilter = NONE; AddressU = Clamp; AddressV = Clamp; };
struct VS_OUT { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
VS_OUT VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) { VS_OUT o; o.Pos=float4(pos.xy,0,1); o.UV=uv; return o; }
float3 Load(float2 p) { return tex2D(SceneSampler,(p+0.5)/InputSize).rgb; }
float Sharpness;
// Output transform uniforms (the same names/meaning as Tonemap.fx).
float Exposure = 1.0f;
float Saturation = 1.0f;
float Threshold = 0.8f;
float Slope = 0.5f;

float3 Rcas(VS_OUT v) {
 float2 p=floor(v.UV*InputSize);
 float3 b=Load(p+float2(0,-1)), d=Load(p+float2(-1,0)), e=Load(p), f=Load(p+float2(1,0)), h=Load(p+float2(0,1));
 float3 mn4=min(min(b,d),min(f,h)), mx4=max(max(b,d),max(f,h));
 float3 hitMin=min(mn4,e)/max(4*mx4,1e-8);
 float3 hitMax=(1-max(mx4,e))/min(4*mn4-4,-1e-8);
 float3 lobes=max(-hitMin,hitMax);
 float lobe=max(-0.1875,min(max(max(lobes.r,lobes.g),lobes.b),0))*Sharpness;
 return (lobe*(b+d+h+f)+e)/(4*lobe+1);
}

float4 PS(VS_OUT v) : COLOR0 { return float4(Rcas(v),1); }

float3 SrgbDecode(float3 c)
{
    float3 lo = c / 12.92;
    float3 hi = pow(max((c + 0.055) / 1.055, 0.0), 2.4);
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

float3 ApplySaturation(float3 c, float s)
{
    float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
    return lerp(float3(luma, luma, luma), c, s);
}

// Neutral: identity below Threshold, hue-preserving shoulder above it (Tonemap.fx
// PS_Neutral), then the display clamp.
float4 PS_NeutralGraded(VS_OUT v) : COLOR0
{
    float3 c = Rcas(v) * Exposure;
    float peak = max(c.r, max(c.g, c.b));
    if (peak > Threshold)
    {
        float excess = max(peak - Threshold, 0.0);
        float mapped = Threshold + excess / (1.0 + excess / Slope);
        c *= mapped / max(peak, 1e-5);
    }
    return float4(saturate(c), 1);
}

// Filmic: decode -> exposure -> world saturation -> ACES fit -> encode
// (Tonemap.fx PS_Aces).
float4 PS_AcesGraded(VS_OUT v) : COLOR0
{
    float3 lin = SrgbDecode(max(Rcas(v), 0.0)) * Exposure;
    if (Saturation != 1.0) lin = ApplySaturation(lin, Saturation);
    return float4(SrgbEncode(Aces(lin)), 1);
}

technique Fsr
{
    pass Rcas { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS(); }
    pass RcasNeutralGraded { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS_NeutralGraded(); }
    pass RcasAcesGraded { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS_AcesGraded(); }
}
