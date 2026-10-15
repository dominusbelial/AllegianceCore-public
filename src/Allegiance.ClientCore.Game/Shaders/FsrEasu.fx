// AMD FidelityFX FSR1, MIT, Copyright (c) 2021 Advanced Micro Devices, Inc.
// Port of ffx_fsr1.h at a21ffb8f6c13233ba336352bdff293894c706575. Full notice in NOTICE.
// SM3: point samples replace texture gathers; full precision reciprocal replaces bit approximations.
float2 InputSize;
texture SceneTexture;
sampler SceneSampler = sampler_state { Texture = <SceneTexture>; MinFilter = POINT; MagFilter = POINT; MipFilter = NONE; AddressU = Clamp; AddressV = Clamp; };
struct VS_OUT { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
VS_OUT VS(float4 pos : POSITION0, float2 uv : TEXCOORD0) { VS_OUT o; o.Pos=float4(pos.xy,0,1); o.UV=uv; return o; }
float3 Load(float2 p) { return tex2D(SceneSampler,(p+0.5)/InputSize).rgb; }
float SafeRcp(float x) { return 1.0/max(x,1e-8); }
 void FsrEasuTapF(
 inout float3 aC, // Accumulated color, with negative lobe.
 inout float aW, // Accumulated weight.
 float2 off, // Pixel offset from resolve position to tap.
 float2 dir, // Gradient direction.
 float2 len, // Length.
 float lob, // Negative lobe strength.
 float clp, // Clipping point.
 float3 c){ // Tap color.
  // Rotate offset by direction.
  float2 v;
  v.x=(off.x*( dir.x))+(off.y*dir.y);
  v.y=(off.x*(-dir.y))+(off.y*dir.x);
  // Anisotropy.
  v*=len;
  // Compute distance^2.
  float d2=v.x*v.x+v.y*v.y;
  // Limit to the window as at corner, 2 taps can easily be outside.
  d2=min(d2,clp);
  // Approximation of lancos2 without sin() or rcp(), or sqrt() to get x.
  //  (25/16 * (2/5 * x^2 - 1)^2 - (25/16 - 1)) * (1/4 * x^2 - 1)^2
  //  |_______________________________________|   |_______________|
  //                   base                             window
  // The general form of the 'base' is,
  //  (a*(b*x^2-1)^2-(a-1))
  // Where 'a=1/(2*b-b^2)' and 'b' moves around the negative lobe.
  float wB=(2.0/5.0)*d2+(-1.0);
  float wA=lob*d2+(-1.0);
  wB*=wB;
  wA*=wA;
  wB=(25.0/16.0)*wB+(-(25.0/16.0-1.0));
  float w=wB*wA;
  // Do weighted average.
  aC+=c*w;aW+=w;}
//------------------------------------------------------------------------------------------------------------------------------
 // Accumulate direction and length.
 void FsrEasuSetF(
 inout float2 dir,
 inout float len,
 float2 pp,
 bool biS,bool biT,bool biU,bool biV,
 float lA,float lB,float lC,float lD,float lE){
  // Compute bilinear weight, branches factor out as predicates are compiler time immediates.
  //  s t
  //  u v
  float w = (0.0);
  if(biS)w=((1.0)-pp.x)*((1.0)-pp.y);
  if(biT)w=           pp.x *((1.0)-pp.y);
  if(biU)w=((1.0)-pp.x)*           pp.y ;
  if(biV)w=           pp.x *           pp.y ;
  // Direction is the '+' diff.
  //    a
  //  b c d
  //    e
  // Then takes magnitude from abs average of both sides of 'c'.
  // Length converts gradient reversal to 0, smoothly to non-reversal at 1, shaped, then adding horz and vert terms.
  float dc=lD-lC;
  float cb=lC-lB;
  float lenX=max(abs(dc),abs(cb));
  lenX=SafeRcp(lenX);
  float dirX=lD-lB;
  dir.x+=dirX*w;
  lenX=saturate(abs(dirX)*lenX);
  lenX*=lenX;
  len+=lenX*w;
  // Repeat for the y axis.
  float ec=lE-lC;
  float ca=lC-lA;
  float lenY=max(abs(ec),abs(ca));
  lenY=SafeRcp(lenY);
  float dirY=lE-lA;
  dir.y+=dirY*w;
  lenY=saturate(abs(dirY)*lenY);
  lenY*=lenY;
  len+=lenY*w;}
//------------------------------------------------------------------------------------------------------------------------------
float4 PS(VS_OUT v) : COLOR0 {
 float2 pp=v.UV*InputSize-0.5;
 float2 fp=floor(pp); pp-=fp;
 float3 b=Load(fp+float2(0,-1)); float bL=b.b*0.5+(b.r*0.5+b.g);
 float3 c=Load(fp+float2(1,-1)); float cL=c.b*0.5+(c.r*0.5+c.g);
 float3 e=Load(fp+float2(-1,0)); float eL=e.b*0.5+(e.r*0.5+e.g);
 float3 f=Load(fp+float2(0,0)); float fL=f.b*0.5+(f.r*0.5+f.g);
 float3 g=Load(fp+float2(1,0)); float gL=g.b*0.5+(g.r*0.5+g.g);
 float3 h=Load(fp+float2(2,0)); float hL=h.b*0.5+(h.r*0.5+h.g);
 float3 i=Load(fp+float2(-1,1)); float iL=i.b*0.5+(i.r*0.5+i.g);
 float3 j=Load(fp+float2(0,1)); float jL=j.b*0.5+(j.r*0.5+j.g);
 float3 k=Load(fp+float2(1,1)); float kL=k.b*0.5+(k.r*0.5+k.g);
 float3 l=Load(fp+float2(2,1)); float lL=l.b*0.5+(l.r*0.5+l.g);
 float3 n=Load(fp+float2(0,2)); float nL=n.b*0.5+(n.r*0.5+n.g);
 float3 o=Load(fp+float2(1,2)); float oL=o.b*0.5+(o.r*0.5+o.g);
 float2 dir=0; float len=0;
 FsrEasuSetF(dir,len,pp,true,false,false,false,bL,eL,fL,gL,jL);
 FsrEasuSetF(dir,len,pp,false,true,false,false,cL,fL,gL,hL,kL);
 FsrEasuSetF(dir,len,pp,false,false,true,false,fL,iL,jL,kL,nL);
 FsrEasuSetF(dir,len,pp,false,false,false,true,gL,jL,kL,lL,oL);
 float dirR=dot(dir,dir);
 if(dirR<1.0/32768.0) dir=float2(1,0); else dir*=rsqrt(dirR);
 len*=0.5; len*=len;
 float stretch=dot(dir,dir)/max(abs(dir.x),abs(dir.y));
 float2 len2=float2(1+(stretch-1)*len,1-0.5*len);
 float lob=0.5+((1.0/4.0-0.04)-0.5)*len;
 float clp=1/lob;
 float3 min4=min(min(f,g),min(j,k)); float3 max4=max(max(f,g),max(j,k));
 float3 acc=0; float weight=0;
 FsrEasuTapF(acc,weight,float2(0,-1)-pp,dir,len2,lob,clp,b);
 FsrEasuTapF(acc,weight,float2(1,-1)-pp,dir,len2,lob,clp,c);
 FsrEasuTapF(acc,weight,float2(-1,1)-pp,dir,len2,lob,clp,i);
 FsrEasuTapF(acc,weight,float2(0,1)-pp,dir,len2,lob,clp,j);
 FsrEasuTapF(acc,weight,float2(0,0)-pp,dir,len2,lob,clp,f);
 FsrEasuTapF(acc,weight,float2(-1,0)-pp,dir,len2,lob,clp,e);
 FsrEasuTapF(acc,weight,float2(1,1)-pp,dir,len2,lob,clp,k);
 FsrEasuTapF(acc,weight,float2(2,1)-pp,dir,len2,lob,clp,l);
 FsrEasuTapF(acc,weight,float2(2,0)-pp,dir,len2,lob,clp,h);
 FsrEasuTapF(acc,weight,float2(1,0)-pp,dir,len2,lob,clp,g);
 FsrEasuTapF(acc,weight,float2(1,2)-pp,dir,len2,lob,clp,o);
 FsrEasuTapF(acc,weight,float2(0,2)-pp,dir,len2,lob,clp,n);
 return float4(min(max4,max(min4,acc/weight)),1);
}
technique Fsr { pass Easu { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS(); } }
