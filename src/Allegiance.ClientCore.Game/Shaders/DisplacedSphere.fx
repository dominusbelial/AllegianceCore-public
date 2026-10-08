// P573 — Technique B: displaced-sphere fireball (SM3.0 reference:
// Steven Craeynest, "Easy volumetric explosion in Unity3D", 2013 — a dense
// icosphere displaced along its normals by noise, coloured from a 1D ramp by
// displacement, faded by clip-threshold erosion so it stays OPAQUE and writes
// depth (no alpha blending, no sorting).
//
// MonoGame form (DesktopGL + Android through MojoShader ps_3_0/vs_3_0):
//   - displacement is ALU 3D value-noise in the VERTEX shader (verified GLES2-
//     safe; no tex2Dlod/Texture3D dependence — see the P573 feasibility report).
//   - each sub-volume is its own sphere; radius grows over life (fast-then-ease),
//     the surface churns via time-advected noise, colour ramps from the type tint
//     and erodes via ClipThreshold as it ages.
//
//   scripts/build-shaders.sh   (=> Content/Shaders/DisplacedSphere.ogl.mgfxo)

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldViewProj;
float3 Center;
float  Radius;          // peak radius (radiusExplosion)
float  Age;
float  Life;
float  Seed;
float  Time;            // global seconds (churn)
float3 Tint;            // native type tint
float  DispStrength;    // displacement amplitude (fraction of radius)
float  Frequency;       // noise frequency (per world unit)
float  ClipThreshold;   // erosion threshold 0..1 (rises as it ages)

float Hash3(float3 p){ p=frac(p*0.3183099+0.1); p*=17.0; return frac(p.x*p.y*p.z*(p.x+p.y+p.z)); }
float Noise(float3 p)
{
    float3 i=floor(p); float3 f=frac(p); f=f*f*(3.0-2.0*f);
    float n000=Hash3(i),n100=Hash3(i+float3(1,0,0)),n010=Hash3(i+float3(0,1,0)),n110=Hash3(i+float3(1,1,0));
    float n001=Hash3(i+float3(0,0,1)),n101=Hash3(i+float3(1,0,1)),n011=Hash3(i+float3(0,1,1)),n111=Hash3(i+float3(1,1,1));
    return lerp(lerp(lerp(n000,n100,f.x),lerp(n010,n110,f.x),f.y),lerp(lerp(n001,n101,f.x),lerp(n011,n111,f.x),f.y),f.z);
}
float Fbm(float3 p)
{
    float s=0.0,a=0.5,f=1.619;
    s+=Noise(p*f)*a; a*=0.5; f*=2.073;
    s+=Noise(p*f)*a; a*=0.5; f*=2.069;
    s+=Noise(p*f)*a; a*=0.5; f*=2.061;
    s+=Noise(p*f)*a;
    return s/(0.5+0.25+0.125+0.0625);
}

// 1D ramp by displacement: low = dark smoke, high = hot core; tinted by the type.
float3 Ramp(float d, float3 tint)
{
    float3 core=float3(1.0,0.972,0.905);
    float3 hot =float3(1.0,0.575,0.095);
    float3 red =float3(0.615,0.11,0.018);
    float3 smoke=float3(0.072,0.058,0.050);
    float3 c = d<0.45 ? lerp(core,hot,saturate(d/0.45))
            : d<0.85 ? lerp(hot,red,saturate((d-0.45)/0.4))
            : lerp(red,smoke,saturate((d-0.85)/0.15));
    c = lerp(c, c*tint, 0.55);
    return c;
}

struct VS_IN { float4 Pos:POSITION0; float3 Nrm:NORMAL0; };
struct VS_OUT { float4 Pos:POSITION0; float4 Col:COLOR0; float Disp:TEXCOORD0; };

VS_OUT VS(in VS_IN i)
{
    VS_OUT o;
    float tn = saturate(Age / max(0.042, Life));   // Life = FILM clock (P582b)
    // P582b — compress growth/cool to native film timing (peak by 28% of the
    // film, cool across the film), mirroring VolumetricTiming. Old code ramped
    // across the full ~3.9 s fire+smoke-tail life — the "too slow, out of sync"
    // fireball. P582's erosion (CPU side) still uses the full life separately.
    float growT = saturate(tn / 0.28);
    float grow = growT*growT*(3.0-2.0*growT);
    float r = Radius * (0.16 + 0.84*grow);
    float coolT = saturate((tn - 0.25)/0.75);
    float cool = coolT*coolT*(3.0-2.0*coolT);       // 0 hot .. 1 cold across film

    float3 n = normalize(i.Nrm);
    float3 wp = Center + i.Pos.xyz * r;
    float churn = lerp(2.6, 0.5, tn) * Time;
    float nd = Fbm(wp * Frequency + float3(churn, churn*0.707, churn*0.894)
                   + float3(Seed, Seed*1.613, Seed*2.917));
    float disp = smoothstep(0.0, 1.0, nd);
    // Displace along the normal both ways; erode toward the core as it ages.
    o.Disp = saturate(disp * (1.0 - cool*0.35));
    float3 outPos = wp + n * (disp - 0.5) * 2.0 * DispStrength * r;
    o.Col = float4(Ramp(o.Disp, Tint), 1.0);
    o.Pos = mul(float4(outPos, 1.0), WorldViewProj);
    return o;
}

float4 PS(in VS_OUT i) : COLOR0
{
    clip(i.Disp - ClipThreshold);   // erosion: opaque fragments vanish -> no alpha
    return float4(i.Col.rgb, 1.0);
}

technique DisplacedSphere
{
    pass Pass0
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
