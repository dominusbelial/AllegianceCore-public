// HD-2: optional modern material on unchanged native geometry/UVs.
// Sector key + blue back light; P530 readability floor for HD albedo only.
// Base/emission are sRGB; tangent normal, metallic and roughness are linear data.
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif
float4x4 World, WorldViewProj, NormalMatrix;
float3 CameraPosition, KeyDirection, KeyColor, AltDirection, AltColor, Ambient;
float Opacity, CompressedMaps;
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
float3 Light(float3 n,float3 v,float3 l,float3 color,float3 albedo,float metal,float rough) {
    float3 h=normalize(v+l+float3(0.000001,0,0));
    float nl=saturate(dot(n,l)), nv=max(0.001,saturate(dot(n,v)));
    float nh=saturate(dot(n,h)), vh=saturate(dot(v,h));
    float a=rough*rough, a2=a*a, d=nh*nh*(a2-1)+1;
    float distribution=a2/max(0.000001,3.14159265*d*d);
    float k=(rough+1)*(rough+1)/8;
    float geometry=nv/(nv*(1-k)+k)*nl/(nl*(1-k)+k);
    float3 f0=lerp(float3(.04,.04,.04),albedo,metal);
    float3 fresnel=f0+(1-f0)*pow(1-vh,5);
    float3 spec=distribution*geometry*fresnel/max(.0001,4*nv*nl);
    return ((1-fresnel)*(1-metal)*albedo/3.14159265+spec)*color*nl*3.14159265;
}
float4 PS(VOUT i):COLOR0 {
    float3 n=normalize(i.Normal), t=normalize(i.Tangent.xyz-n*dot(n,i.Tangent.xyz));
    float3 b=cross(n,t)*i.Tangent.w;
    float3 map=tex2D(NormalSampler,i.UV).xyz*2-1;
    if (CompressedMaps > .5) map.z=sqrt(saturate(1-dot(map.xy,map.xy)));
    n=normalize(t*map.x+b*map.y+n*map.z);
    float3 albedo=tex2D(BaseSampler,i.UV).rgb;
    if (CompressedMaps < .5) albedo=pow(max(albedo,0),2.2);
    float metal=saturate(tex2D(MetalSampler,i.UV).r), rough=clamp(tex2D(RoughSampler,i.UV).r,.08,1);
    float3 v=normalize(CameraPosition-i.WorldPosition);
    // Ambient is the existing sector/floor, with a small conductor response in
    // lieu of an environment cubemap. No added camera/head light.
    float3 color=albedo*Ambient*lerp(1,.25,metal);
    color+=Light(n,v,-KeyDirection,KeyColor,albedo,metal,rough);
    color+=Light(n,v,-AltDirection,AltColor,albedo,metal,rough);
    float3 emission=tex2D(EmissionSampler,i.UV).rgb;
    if (CompressedMaps < .5) emission=pow(max(emission,0),2.2);
    color+=emission;
    return float4(pow(max(color,0),1/2.2)*Opacity,Opacity);
}
technique NativePbr { pass Pass0 { VertexShader=compile VS_SHADERMODEL VS(); PixelShader=compile PS_SHADERMODEL PS(); } }
