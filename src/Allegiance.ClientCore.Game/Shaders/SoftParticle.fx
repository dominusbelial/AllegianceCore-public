// P547 — SOFT PARTICLES (linear-depth fade) for the ENHANCED smoke/fire path.
//
// Compile (both DesktopGL and Android use the OpenGL profile — MonoGame GL
// effects go through MojoShader, vs_3_0/ps_3_0 only):
//   scripts/build-shaders.sh   (or by hand: mgfxc SoftParticle.fx SoftParticle.ogl.mgfxo /Profile:OpenGL)
//
// Concept: at particle/geometry intersections a billboard quad's plane would
// normally slice through the surface and pop. Instead we read the scene's
// LINEAR DEPTH (rendered into its own RGBA8 RenderTarget2D by LinearDepth.fx —
// the backbuffer depth can't be sampled on GL) and fade the particle's alpha
// to zero where the opaque scene is CLOSER than the particle itself, melting
// it onto surfaces instead of cutting hard edges against hulls/rocks/stations.
//
// Units: both the scene depth and the particle depth are normalized to [0,1]
// via (viewDist - Near) * InvRange (InvRange = 1/(Far-Near)). The two are
// compared in the SAME normalized space, so SoftnessInv (1/softness) is also in
// normalized-depth units (see SoftParticleRenderer.DepthSoftnessNormalized).
//
// Effect parameters are set from CODE every frame (GL ignores parameter
// defaults — MonoGame docs): WorldView, WorldViewProj, InvViewportSize,
// Near, InvRange, SoftnessInv, Texture, DepthTexture.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldView;
float4x4 WorldViewProj;
// Inverse viewport size (0.5/VPx, 0.5/VPy) — converts VPOS pixels to UVs.
float2 InvViewportSize;
float Near;              // camera near plane
float InvRange;          // 1 / (Far - Near)
float SoftnessInv;       // 1 / softness (normalized-depth units): fade slope

texture Texture;
sampler TexSampler = sampler_state
{
    Texture = <Texture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = Clamp; AddressV = Clamp;
};

// The scene's LINEAR DEPTH, rendered into its own RGBA8 target (can't sample
// the backbuffer depth on OpenGL). Point sampling — we want per-texel depth.
texture DepthTexture;
sampler DepthSampler = sampler_state
{
    Texture = <DepthTexture>;
    MinFilter = POINT; MagFilter = POINT; MipFilter = NONE;
    AddressU = Clamp; AddressV = Clamp;
};

// Encode a float in [0,1] into RGBA8 (Valve's 32-bit float packing). Self-
// consistent with the encode in LinearDepth.fx so scene and particle depth
// round-trip through the same format.
float4 EncodeFloatRGBA(float v)
{
    float4 enc = float4(1.0, 255.0, 65025.0, 16581375.0) * v;
    enc = frac(enc);
    enc -= enc.yzww * float4(1.0/255.0, 1.0/255.0, 1.0/255.0, 0.0);
    return enc;
}
float DecodeFloatRGBA(float4 rgba)
{
    return dot(rgba, float4(1.0, 1.0/255.0, 1.0/65025.0, 1.0/16581375.0));
}

struct VS_OUT
{
    float4 Pos : POSITION0;
    float2 UV : TEXCOORD0;
    float4 Color : COLOR0;
    float ViewDepth : TEXCOORD1;   // particle depth: positive view distance
};

VS_OUT VS(in float3 pos : POSITION0, in float2 uv : TEXCOORD0, in float4 color : COLOR0)
{
    VS_OUT o;
    o.Pos = mul(float4(pos.xyz, 1), WorldViewProj);
    float4 vp = mul(float4(pos.xyz, 1), WorldView);
    o.ViewDepth = -vp.z;           // positive distance from the eye
    o.UV = uv;
    o.Color = color;
    return o;
}

float4 PS(in VS_OUT i, in float2 vpos : VPOS0) : COLOR0
{
    float2 duv = vpos * InvViewportSize;          // pixel -> [0,1]
    float sceneNorm = DecodeFloatRGBA(tex2D(DepthSampler, duv));
    float particleNorm = (i.ViewDepth - Near) * InvRange;
    // Fade to 0 where the scene is closer than the particle by more than
    // softness; smooth between (soft intersection). Saturate keeps it in [0,1].
    float fade = saturate((sceneNorm - particleNorm) * SoftnessInv);

    float4 tc = tex2D(TexSampler, i.UV);
    // Premultiplied output: vertex colour is already premultiplied (rgb =
    // tint·α); the texture is a straight-alpha mask. Scale both by fade to
    // keep premultiplied consistency under the (ONE, INVSRCALPHA) blend.
    float4 c;
    c.rgb = tc.rgb * i.Color.rgb * fade;
    c.a = tc.a * i.Color.a * fade;
    return c;
}

technique SoftParticle
{
    pass Pass0
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
