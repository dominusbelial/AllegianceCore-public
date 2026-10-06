// P547 — LINEAR DEPTH PASS (encoded to RGBA8). Renders the opaque scene
// geometry (ships, asteroids, stations, models) into an RGBA8 RenderTarget2D
// holding each pixel's LINEAR view-space depth, normalized to [0,1] and packed
// into 32 bits (Valve float packing) so it round-trips precisely and works on
// GLES/Android (SurfaceFormat.Color is universally supported; Single/HalfSingle
// targets are not guaranteed on GL).
//
// The SoftParticle.fx pixel shader samples this target (DecodeFloatRGBA) and
// compares it to each particle's own depth to fade particles at geometry
// intersections.
//
// Compile: scripts/build-shaders.sh (OpenGL profile, vs_3_0/ps_3_0 via MojoShader).
// Parameters set from code every frame: WorldView, WorldViewProj, Near, InvRange.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldView;
float4x4 WorldViewProj;
float Near;        // camera near plane (for normalization)
float InvRange;    // 1 / (Far - Near)

float4 EncodeFloatRGBA(float v)
{
    float4 enc = float4(1.0, 255.0, 65025.0, 16581375.0) * v;
    enc = frac(enc);
    enc -= enc.yzww * float4(1.0/255.0, 1.0/255.0, 1.0/255.0, 0.0);
    return enc;
}

struct VS_OUT
{
    float4 Pos : POSITION0;
    float ViewDepth : TEXCOORD0;   // positive view distance from the eye
};

VS_OUT VS(in float3 pos : POSITION0)
{
    VS_OUT o;
    o.Pos = mul(float4(pos.xyz, 1), WorldViewProj);
    float4 vp = mul(float4(pos.xyz, 1), WorldView);
    o.ViewDepth = -vp.z;
    return o;
}

float4 PS(in VS_OUT i) : COLOR0
{
    float norm = saturate((i.ViewDepth - Near) * InvRange);
    return EncodeFloatRGBA(norm);
}

technique LinearDepth
{
    pass Pass0
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
