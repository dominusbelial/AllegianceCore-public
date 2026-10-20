// HD-2: optional modern material on unchanged native geometry/UVs.
// Sector key + blue back light; P530 readability floor for HD albedo only.
//
// HD-6: tuned experimental PBR so it KEEPS the native colour mood while adding
// believable reflections/highlights (docs/reports/HD6_PBR_TUNING.md).
//
// Architecture (changed from HD-2/HD-5 after diagnosing the pale/grey/silver
// report): the BASE lighting is computed bit-for-bit like the native classic
// path — sRGB-encoded albedo * (bidirectional ambient 0.25 + key colour*ndotl +
// blue back/alt colour*ndotl) — in GAMMA space, exactly the equation native's
// fixed-function lighting produces (device3d.cpp:792 bidirectional branch;
// clusterIGC.h:331 GetBidirectionalAmbientLevel / :321-324 GetLightColorAlt).
// This guarantees the PBR hull keeps the native colour mood (warm diffuse, strong
// blue rim) on every surface.
//
// On TOP of that gamma base we add a GGX specular with a WHITE dielectric
// clearcoat f0 (0.04) — real painted hull plating reads as warm coloured diffuse
// + white clearcoat glints — intensified by the (tuned) metallic map and shaped
// by the (tuned) roughness map. This is the intended modern addition; native has
// no specular at all (device3d.cpp:758 SPECULARENABLE FALSE).
//
// Working in gamma space (not linear HDR) is deliberate: it is what the classic
// path does, and it avoids the linear->gamma re-encode amplifying the coloured
// back light and washing warm hulls toward silver (the HD-6 diagnostic).
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif
float4x4 World, WorldViewProj, NormalMatrix;
float3 CameraPosition, KeyDirection, KeyColor, AltDirection, AltColor, Ambient;
float Opacity, CompressedMaps;
// HD-6 material/scene tuning uniforms (fed from HdMaterialRenderer.MaterialTuning).
float RoughnessScale, RoughnessBias, RoughnessMin, RoughnessMax;
float MetallicGain, MetallicDeadband, MetallicMin, MetallicMax;
float AmbientMetalFloor, Exposure, TonemapThreshold, TonemapSlope, EnvSheen;
float4 FlashPositions[4], FlashColors[4];
texture BaseMap, NormalMap, MetalMap, RoughMap, EmissionMap;
sampler BaseSampler = sampler_state { Texture=<BaseMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler NormalSampler = sampler_state { Texture=<NormalMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler MetalSampler = sampler_state { Texture=<MetalMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler RoughSampler = sampler_state { Texture=<RoughMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
sampler EmissionSampler = sampler_state { Texture=<EmissionMap>; MinFilter=Linear; MagFilter=Linear; MipFilter=Linear; AddressU=Clamp; AddressV=Clamp; };
struct VIN { float4 Position:POSITION0; float3 Normal:NORMAL0; float2 UV:TEXCOORD0; float4 Tangent:TANGENT0; };
struct VOUT { float4 Position:POSITION0; float3 WorldPosition:TEXCOORD0; float3 Normal:TEXCOORD1; float2 UV:TEXCOORD2; float4 Tangent:TEXCOORD3; };
VOUT VS(VIN i) {
    VOUT o; o.Position=mul(i.Position,WorldViewProj); o.WorldPosition=mul(i.Position,World).xyz;
    o.Normal=mul(float4(i.Normal,0),NormalMatrix).xyz;
    o.Tangent=float4(mul(float4(i.Tangent.xyz,0),World).xyz,i.Tangent.w);
    o.UV=i.UV; return o;
}
// White clearcoat GGX highlight (gamma space, 0.04 dielectric f0). `metal` raises
// the glint strength on plating; roughness shapes its tightness. Colour follows
// the incident light (warm key → warm glint, blue back → cool glint), so the
// highlight reinforces rather than fights the native mood.
float3 Highlight(float3 n,float3 v,float3 l,float3 color,float metal,float rough) {
    float3 h=normalize(v+l+float3(0.000001,0,0));
    float nl=saturate(dot(n,l)), nv=max(0.004,saturate(dot(n,v)));
    float nh=saturate(dot(n,h)), vh=saturate(dot(v,h));
    float a=rough*rough, a2=a*a, d=nh*nh*(a2-1)+1;
    float distribution=a2/max(0.000001,3.14159265*d*d);
    float k=(rough+1)*(rough+1)/8;
    float geometry=nv/(nv*(1-k)+k)*nl/(nl*(1-k)+k);
    float3 fresnel=.042+(1-.042)*pow(1-vh,5);
    float3 spec=distribution*geometry*fresnel/max(.006,4*nv*nl);
    return spec*color*nl*(1+metal*1.8);
}
// HD-6 hue-preserving tone map + exposure. Only the PEAK channel is compressed
// and every channel is scaled by the same factor, so highlights roll off toward
// white without desaturating the hull (the legacy raw clip desaturated). Below
// TonemapThreshold the curve is identity: native's saturated midtones survive.
float3 ToneMap(float3 c) {
    float3 lc = c * Exposure;
    float peak = max(lc.r, max(lc.g, lc.b));
    if (peak <= TonemapThreshold) return lc;
    float mapped = TonemapThreshold + (peak-TonemapThreshold)/(1+max(peak-TonemapThreshold,0)/TonemapSlope);
    return lc * (mapped/max(peak,.00001));
}
// Shared lighting. Returns lit albedo (gamma space, ready for ToneMap) and the
// vectors/vals the flash loop needs.
float3 AlbedoColor(VOUT i, out float3 n, out float3 v, out float3 albedoG, out float metal, out float rough)
{
    n=normalize(i.Normal);
    float3 t=normalize(i.Tangent.xyz-n*dot(n,i.Tangent.xyz));
    float3 b=cross(n,t)*i.Tangent.w;
    float3 map=tex2D(NormalSampler,i.UV).xyz*2-1;
    if (CompressedMaps > .5) map.z=sqrt(saturate(1-dot(map.xy,map.xy)));
    n=normalize(t*map.x+b*map.y+n*map.z);
    v=normalize(CameraPosition-i.WorldPosition);
    float3 albedoS=tex2D(BaseSampler,i.UV).rgb;
    // sRGB-encoded albedo in EVERY path: PNG samples are already encoded; the
    // compressed BC7 sRGB sampler decodes to linear, so re-encode. The base
    // lighting runs in this gamma space (== classic/native) to preserve colour.
    albedoG = CompressedMaps > .5 ? pow(max(albedoS,0),1/2.2) : albedoS;
    // ── HD-6 material tuning (HD-2's estimated maps were too rough / not
    //    metallic enough, making the hull grey and matte). Applied here, per
    //    material, from the pack manifest; no texture regeneration needed. ──
    float metalMap=tex2D(MetalSampler,i.UV).r;
    float roughMap=tex2D(RoughSampler,i.UV).r;
    metal=metalMap>=MetallicDeadband ? (metalMap-MetallicDeadband)*MetallicGain+MetallicDeadband : metalMap;
    metal=clamp(metal,MetallicMin,MetallicMax);
    rough=clamp(roughMap*RoughnessScale+RoughnessBias,RoughnessMin,RoughnessMax);
    // ── Scene lighting — EXACT native classic equation in gamma space ─────────
    // albedo * (bidirectional ambient 0.25 + key*nl + blue alt*nl2), flat on every
    // surface (AmbientMetalFloor=1). Coloured key = sector m_color; alt = constant
    // blue (0,.5,1) (WinTrek.cpp:3635; clusterIGC.h:321-324, :331). No headlight.
    float3 color=albedoG*Ambient*lerp(1,AmbientMetalFloor,metal);
    color+=albedoG*KeyColor*saturate(dot(n,-KeyDirection));
    color+=albedoG*AltColor*saturate(dot(n,-AltDirection));
    // HD-6: white clearcoat highlights (modern addition; native has none).
    color+=Highlight(n,v,-KeyDirection,KeyColor,metal,rough);
    color+=Highlight(n,v,-AltDirection,AltColor,metal,rough);
    // Subtle neutral-cool fresnel sheen on metal so plating reads reflective at
    // grazing without tinting the warm hull. EnvSheen=0 disables.
    if (EnvSheen > 0.001) {
        float fres=pow(1-saturate(dot(n,v)),3);
        float3 refl=reflect(-v,n);
        float hem=saturate(refl.y*0.5+0.5);
        color+=fres*lerp(float3(.05,.06,.08),AltColor*.4,hem)*metal*EnvSheen;
    }
    return color;
}

float4 PS(VOUT i):COLOR0 {
    float3 n,v,albedo; float metal,rough;
    float3 color=AlbedoColor(i,n,v,albedo,metal,rough);
    float3 emisS=tex2D(EmissionSampler,i.UV).rgb;
    float3 emisG = CompressedMaps > .5 ? pow(max(emisS,0),1/2.2) : emisS;
    color+=emisG;
    return float4(clamp(ToneMap(color),0,1)*Opacity,Opacity);
}

float4 PSFlash(VOUT i):COLOR0 {
    float3 n,v,albedo; float metal,rough;
    float3 color=AlbedoColor(i,n,v,albedo,metal,rough);
    // Unrolled four-light budget (SM3/MojoShader: no dynamic loop bound or
    // branch). Inactive slots carry range 0 / black colour, so their squared
    // falloff contributes ~nothing. Squared finite-range falloff.
    for (int j=0; j<4; j++) {
        float3 delta=FlashPositions[j].xyz-i.WorldPosition;
        float distance2=dot(delta,delta);
        float range=max(FlashPositions[j].w,1e-3);
        float fade=saturate(1-distance2/(range*range));
        float3 ldir=delta/max(sqrt(distance2),.001);
        float3 fcol=FlashColors[j].rgb*fade*fade;
        color+=albedo*fcol*saturate(dot(n,ldir));
        color+=Highlight(n,v,ldir,fcol,metal,rough);
    }
    float3 emisS=tex2D(EmissionSampler,i.UV).rgb;
    float3 emisG = CompressedMaps > .5 ? pow(max(emisS,0),1/2.2) : emisS;
    color+=emisG;
    return float4(clamp(ToneMap(color),0,1)*Opacity,Opacity);
}
technique NativePbr { pass Pass0 { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PS(); } }

technique EffectLitPbr { pass Pass0 { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PSFlash(); } }
