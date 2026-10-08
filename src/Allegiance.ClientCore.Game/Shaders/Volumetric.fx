// P573/P579 — Technique A: raymarched volumetric explosion (impostor billboards).
//
// Each explosion sub-volume (the native per-decal staggered blobs) is a
// camera-facing billboard quad. The pixel shader intersects the view ray with
// the bounding sphere (Center, Radius), marches through it sampling animated
// ALU 3D value-noise fBm density shaped by a radial falloff, and shades via a
// temperature ramp (white-hot core -> orange -> red -> smoke) tinted by the
// native explosion type. Cheap self-shadow = the per-sample transmittance
// (optical depth along the view) plus a light-side bias.
//
// P579 changes vs the P573 spike:
//   * Sphere clip: each PS runs RaySphere first and `clip()`s fragments whose
//     ray misses the sphere — no square edges (the quad is a square, its
//     corners fall outside the inscribed sphere) AND ~21% fill saved (the
//     corners never reach the framebuffer).
//   * Coverage LOD (step counts inverted): BIG on-screen -> FEWER, longer
//     steps (Low 64); SMALL -> more (Ultra 510). Each level's dt is normalised
//     (1/step count) so total optical depth — and thus brightness — is uniform
//     across levels regardless of on-screen size.
//   * Brightness: a per-effect float param (set from code, default 10) boosts
//     the march to read additively over space (the spike's march was ~10x too
//     faint at game scale).
//
// Compatibility (verified by P573 feasibility): DesktopGL AND Android run this
// through MojoShader as ps_3_0 / vs_3_0. Static loops are NOT unrolled — they
// become hardware `rep` loops capped at 255 iterations each; higher step counts
// use NESTED loops (outer slices x inner 255), which MojoShader emits as nested
// reps. Four LOD techniques below trade step count against on-screen size; the
// CPU picks the technique per quad (coverage LOD + per-frame budget).
//
// NOTE ps_3_0: the pixel shader may NOT read the POSITION0 interpolant (only
// VPOS), so the world corner position is carried in a TEXCOORD instead.
//
//   scripts/build-shaders.sh   (=> Content/Shaders/Volumetric.ogl.mgfxo)

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldViewProj;
float4x4 WorldView;
float2 InvViewportSize;    // 0.5/VPx, 0.5/VPy
float Near;
float InvRange;
float3 EyePos;             // camera eye (world)
float3 LightDir;           // world unit dir for the cheap self-shadow bias
float Time;                // global seconds (drives churn/flow)
float Brightness;          // P579 — additive-brightness scale (set from code)

texture DepthTexture;
sampler DepthSampler = sampler_state
{
    Texture = <DepthTexture>;
    MinFilter = POINT; MagFilter = POINT; MipFilter = NONE;
    AddressU = Clamp; AddressV = Clamp;
};
float4 DecodeFloatRGBA(float4 rgba)
{ return dot(rgba, float4(1.0, 1.0/255.0, 1.0/65025.0, 1.0/16581375.0)); }

// ---------------------------------------------------------------------------
// ALU 3D value noise (GLES2-safe: no texture fetches, works everywhere).
// ---------------------------------------------------------------------------
float Hash3(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float Noise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i), n100 = Hash3(i + float3(1,0,0));
    float n010 = Hash3(i + float3(0,1,0)), n110 = Hash3(i + float3(1,1,0));
    float n001 = Hash3(i + float3(0,0,1)), n101 = Hash3(i + float3(1,0,1));
    float n011 = Hash3(i + float3(0,1,1)), n111 = Hash3(i + float3(1,1,1));
    return lerp(
        lerp(lerp(n000,n100,f.x), lerp(n010,n110,f.x), f.y),
        lerp(lerp(n001,n101,f.x), lerp(n011,n111,f.x), f.y),
        f.z);
}
// 4-octave fBm (feasibility-measured body ~120 ps_3_0 instructions/iteration).
float Fbm(float3 p)
{
    float sum = 0.0;
    float amp = 0.5;
    float freq = 1.618;
    float n1 = Noise(p*freq)*amp;            sum += n1; amp *= 0.5; freq *= 2.061;
    float n2 = Noise(p*freq)*amp;            sum += n2; amp *= 0.5; freq *= 2.067;
    float n3 = Noise(p*freq)*amp;            sum += n3; amp *= 0.5; freq *= 2.053;
    float n4 = Noise(p*freq)*amp;            sum += n4;
    return sum / (0.5+0.25+0.125+0.0625);
}

// Per-sub-volume vertex attributes (repeated across the quad's 4 verts).
struct VS_IN
{
    float4 Pos   : POSITION0;   // world corner position
    float3 Center: TEXCOORD0;   // sub-volume centre (world)
    float  Radius: TEXCOORD1;   // current radius (world)
    float3 Tint  : TEXCOORD2;   // native type tint (rgb)
    float  Age   : TEXCOORD3;   // seconds since this sub-volume's birth
    float  Life  : TEXCOORD4;   // total life seconds
    float  Seed  : TEXCOORD5;   // per-volume seed
};
struct VS_OUT
{
    float4 Pos      : POSITION0;
    float3 WorldPos : TEXCOORD0;
    float3 Center   : TEXCOORD1;
    float  Radius   : TEXCOORD2;
    float3 Tint     : TEXCOORD3;
    float  Age      : TEXCOORD4;
    float  Life     : TEXCOORD5;
    float  Seed     : TEXCOORD6;
    float  ViewDepth: TEXCOORD7; // positive view distance (for depth fade)
};
VS_OUT VS(in VS_IN i)
{
    VS_OUT o;
    o.Pos = mul(i.Pos, WorldViewProj);
    float4 vp = mul(i.Pos, WorldView);
    o.ViewDepth = -vp.z;
    o.WorldPos = i.Pos.xyz;
    o.Center = i.Center; o.Radius = i.Radius; o.Tint = i.Tint;
    o.Age = i.Age; o.Life = i.Life; o.Seed = i.Seed;
    return o;
}

// P579 — ray–sphere intersection in camera-relative space. Returns false when
// the ray misses (the caller then clip()s — no square edges, saved fill).
bool RaySphere(float3 ro, float3 rd, float3 center, float radius, out float t0, out float t1)
{
    float3 l = center - ro;
    float tca = dot(l, rd);
    float d2 = dot(l, l) - tca * tca;
    float rr = radius * radius;
    if (d2 > rr) { t0 = t1 = 0.0; return false; }
    float thc = sqrt(max(rr - d2, 0.0));
    float tA = tca - thc;
    float tB = tca + thc;
    if (tB < 0.0) { t0 = t1 = 0.0; return false; }
    t0 = max(tA, 0.0);
    t1 = tB;
    return true;
}

// Temperature ramp: white-hot core -> orange -> red -> smoke, cooled by 'cool'
// (0 hot .. 1 cold) and tinted by the native type tint.
float3 Ramp(float d, float cool, float3 tint)
{
    float3 core = float3(1.0, 0.975, 0.90);
    float3 hot  = float3(1.0, 0.58, 0.10);
    float3 red  = float3(0.62, 0.12, 0.018);
    float3 smoke= float3(0.078, 0.064, 0.056);
    float t = d;
    float3 c;
    if (t < 0.5) c = lerp(core, hot, t * 2.0);
    else if (t < 0.85) c = lerp(hot, red, (t - 0.5) / 0.35);
    else c = lerp(red, smoke, (t - 0.85) / 0.15);
    c = lerp(c, smoke, cool * 0.85);        // cool toward smoke with age
    c = lerp(c, c * tint, 0.55);            // native type tint (strong: mine reads violet)
    return c;
}

// Accumulate density from t0..t1 (intersection already computed by the caller).
// 'dt' is normalised to 1/step count so total optical depth is LOD-uniform.
// Returns float4(acc*4*Brightness, 1-trans) — alpha is the real volume
// transmittance (≈1 at the dense core), which the additive blend's SourceAlpha
// factor needs (a tiny alpha suppressed the whole output ~8×).
float4 MarchAcc(float3 ro, float3 rd, float3 center, float r, float age, float life,
                float3 tint, float seed, float dt, float t0, float t1, float span,
                int slices, int inner)
{
    float tn = saturate(age / max(0.045, life));   // life = FILM clock (P582b)
    // P582b — compress growth/cool to NATIVE FILM timing: reach full radius +
    // peak hot by 28% of the film (~0.42 s for a fighter's 1.5 s film), hold hot
    // through the peak, then cool to smoke across the rest of the film — instead
    // of the old whole-life ramp (peak only at life-end ~3.9 s) that made the
    // fireball "play too slowly and land out of sync". Mirrors VolumetricTiming.
    float growT = saturate(tn / 0.28);
    float grow = growT * growT * (3.0 - 2.0 * growT);
    float radCur = r * (0.18 + 0.82 * grow);
    float coolT = saturate((tn - 0.25) / 0.75);
    float cool = coolT * coolT * (3.0 - 2.0 * coolT);
    float churn = lerp(3.2, 0.8, tn) * Time;

    float3 acc = 0.0;
    float trans = 1.0;
    int steps = slices * inner;
    for (int si = 0; si < slices; si++)
    {
        for (int j = 0; j < inner; j++)
        {
            int idx = si * inner + j;
            float tt = t0 + (idx + 0.5) / steps * span;
            float3 p = ro + rd * tt;
            float3 lp = (p - center) / max(radCur, 1e-4);
            float rad = length(lp);
            float base = 1.0 - smoothstep(0.35, 1.0, rad);
            float n = Fbm(lp * 2.2
                          + float3(churn*0.031, churn*0.019, churn*0.025)
                          + float3(seed, seed*0.871, seed*1.643));
            float dens = max(0.0, base - (1.0 - n) * 0.42);
            dens *= smoothstep(1.04, 0.99, rad);
            if (dens > 0.006)
            {
                float3 col = Ramp(saturate(dens), cool, tint);
                float shade = 0.5 + 0.5 * saturate(dot(rd, LightDir) * 0.5 + 0.5);
                acc += col * (dens * trans * shade) * dt;
                trans *= exp(-dens * 3.2);
                if (trans < 0.016) break;
            }
            if (trans < 0.016) break;
        }
        if (trans < 0.016) break;
    }
    return float4(acc * 4.0 * Brightness, 1.0 - trans);
}

// Depth-soft intersection: fade where the opaque scene's linear depth is
// closer than this fragment (melts the volume onto hulls instead of slicing).
float DepthFade(VS_OUT i, float2 vpos)
{
    float2 duv = vpos * InvViewportSize;
    float scene = DecodeFloatRGBA(tex2D(DepthSampler, duv)).x;
    float vol = (i.ViewDepth - Near) * InvRange;
    // Same normalized-depth slope as SoftParticle.fx (SoftnessInv = 1/0.004,
    // LinearDepth.SoftnessNormalized); P573's 1/8 faded every volume to ~0.
    float softInv = 250.0;
    return saturate((scene - vol) * softInv);
}

// P579 coverage-LOD: Low(64) = biggest on-screen; Ultra(510) = smallest.
// dt normalised to 1/steps keeps brightness uniform across levels.
float4 PS_Low(VS_OUT i, in float2 vpos: VPOS0) : COLOR0
{
    float3 rd = normalize(i.WorldPos - EyePos);
    float t0, t1;
    if (!RaySphere(EyePos, rd, i.Center, i.Radius, t0, t1)) clip(-1);
    float span = max(t1 - t0, 1e-4);
    return MarchAcc(EyePos, rd, i.Center, i.Radius, i.Age, i.Life, i.Tint, i.Seed,
                    1.0/64.0, t0, t1, span, 1, 64) * DepthFade(i, vpos);
}
float4 PS_Med(VS_OUT i, in float2 vpos: VPOS0) : COLOR0
{
    float3 rd = normalize(i.WorldPos - EyePos);
    float t0, t1;
    if (!RaySphere(EyePos, rd, i.Center, i.Radius, t0, t1)) clip(-1);
    float span = max(t1 - t0, 1e-4);
    return MarchAcc(EyePos, rd, i.Center, i.Radius, i.Age, i.Life, i.Tint, i.Seed,
                    1.0/128.0, t0, t1, span, 1, 128) * DepthFade(i, vpos);
}
float4 PS_High(VS_OUT i, in float2 vpos: VPOS0) : COLOR0
{
    float3 rd = normalize(i.WorldPos - EyePos);
    float t0, t1;
    if (!RaySphere(EyePos, rd, i.Center, i.Radius, t0, t1)) clip(-1);
    float span = max(t1 - t0, 1e-4);
    return MarchAcc(EyePos, rd, i.Center, i.Radius, i.Age, i.Life, i.Tint, i.Seed,
                    1.0/255.0, t0, t1, span, 1, 255) * DepthFade(i, vpos);
}
float4 PS_Ultra(VS_OUT i, in float2 vpos: VPOS0) : COLOR0
{
    float3 rd = normalize(i.WorldPos - EyePos);
    float t0, t1;
    if (!RaySphere(EyePos, rd, i.Center, i.Radius, t0, t1)) clip(-1);
    float span = max(t1 - t0, 1e-4);
    return MarchAcc(EyePos, rd, i.Center, i.Radius, i.Age, i.Life, i.Tint, i.Seed,
                    1.0/510.0, t0, t1, span, 2, 255) * DepthFade(i, vpos);
}

technique RaymarchLow   { pass Pass0 { VertexShader = compile VS_SHADERMODEL VS(); PixelShader = compile PS_SHADERMODEL PS_Low(); } }
technique RaymarchMed   { pass Pass0 { VertexShader = compile VS_SHADERMODEL VS(); PixelShader = compile PS_SHADERMODEL PS_Med(); } }
technique RaymarchHigh  { pass Pass0 { VertexShader = compile VS_SHADERMODEL VS(); PixelShader = compile PS_SHADERMODEL PS_High(); } }
technique RaymarchUltra { pass Pass0 { VertexShader = compile VS_SHADERMODEL VS(); PixelShader = compile PS_SHADERMODEL PS_Ultra(); } }
