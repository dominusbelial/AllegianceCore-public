// AMD FidelityFX FSR1, MIT, Copyright (c) 2021 Advanced Micro Devices, Inc.
// Port of ffx_fsr1.h at a21ffb8f6c13233ba336352bdff293894c706575. Full notice in NOTICE.
// SM3: point samples replace texture gathers; full precision reciprocal replaces bit approximations.
float2 InputSize;
texture SceneTexture;
sampler SceneSampler = sampler_state { Texture = <SceneTexture>; MinFilter = POINT; MagFilter = POINT; MipFilter = NONE; AddressU = Clamp; AddressV = Clamp; };
struct VS_OUT { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
VS_OUT VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) { VS_OUT o; o.Pos=float4(pos.xy,0,1); o.UV=uv; return o; }
float3 Load(float2 p) { return tex2D(SceneSampler,(p+0.5)/InputSize).rgb; }
float Sharpness;
float4 PS(VS_OUT v) : COLOR0 {
 float2 p=floor(v.UV*InputSize);
 float3 b=Load(p+float2(0,-1)), d=Load(p+float2(-1,0)), e=Load(p), f=Load(p+float2(1,0)), h=Load(p+float2(0,1));
 float3 mn4=min(min(b,d),min(f,h)), mx4=max(max(b,d),max(f,h));
 float3 hitMin=min(mn4,e)/max(4*mx4,1e-8);
 float3 hitMax=(1-max(mx4,e))/min(4*mn4-4,-1e-8);
 float3 lobes=max(-hitMin,hitMax);
 float lobe=max(-0.1875,min(max(max(lobes.r,lobes.g),lobes.b),0))*Sharpness;
 return float4((lobe*(b+d+h+f)+e)/(4*lobe+1),1);
}
technique Fsr { pass Rcas { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS(); } }
