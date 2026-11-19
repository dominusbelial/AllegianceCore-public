// P1057 matched geometry/camera/resolution comparison. No npm dependencies.
// node tools/p1057-browser-aa.cjs --chromium <local executable> --scene <scene.json> --out <dir>
// This isolates browser default coverage from textures, post effects and KNI.
// Production KNI context attributes are also recorded by p1051-browser-flight.cjs.
const fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const arg=(key,fallback)=>{const i=process.argv.indexOf(key);return i<0?fallback:process.argv[i+1]};
const menuUrl=arg('--menu');
const scene=menuUrl?{width:1920,height:1080}:JSON.parse(fs.readFileSync(arg('--scene','.scratch/p1057/scene-before/scene.json')));
const output=path.resolve(arg('--out','.scratch/p1057/browser'));
fs.mkdirSync(output,{recursive:true});
async function main(){
 const child=spawn(arg('--chromium','/tmp/chromium'),['--headless','--no-sandbox','--disable-dev-shm-usage','--no-first-run','--no-default-browser-check','--disable-background-networking','--disable-component-update','--disable-sync','--disable-extensions','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader','--remote-debugging-port=0','--user-data-dir='+path.join(output,'profile'),'--proxy-server=http://127.0.0.1:9','--proxy-bypass-list=localhost;127.0.0.1','about:blank'],{stdio:['ignore','ignore','pipe']});
 const exited=new Promise(resolve=>child.once('exit',resolve));
 let socket;
 try{
  const endpoint=await new Promise((resolve,reject)=>{let text='';const timeout=setTimeout(()=>reject(Error('Chromium startup timeout')),20000);child.stderr.on('data',chunk=>{text+=chunk;const match=text.match(/DevTools listening on (ws:\/\/[^\s]+)/);if(match){clearTimeout(timeout);resolve(match[1])}});child.once('error',reject);child.once('exit',code=>reject(Error('Chromium exited '+code+' '+text.slice(-2000))))});
  const address=new URL(endpoint);const targets=await (await fetch('http://'+address.host+'/json/list')).json();
  socket=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true})});
  let serial=0;const pending=new Map();socket.addEventListener('message',event=>{const msg=JSON.parse(event.data);if(pending.has(msg.id)){const {resolve,reject}=pending.get(msg.id);pending.delete(msg.id);msg.error?reject(Error(JSON.stringify(msg.error))):resolve(msg.result)}});
  const send=(method,params={})=>new Promise((resolve,reject)=>{const id=++serial;pending.set(id,{resolve,reject});socket.send(JSON.stringify({id,method,params}))});
  await send('Emulation.setDeviceMetricsOverride',{width:scene.width,height:scene.height,deviceScaleFactor:1,mobile:false});
  if(menuUrl){
   const url=new URL(menuUrl);if(!['localhost','127.0.0.1'].includes(url.hostname))throw Error('Menu proof must be loopback');
   const errors=[];socket.addEventListener('message',event=>{const message=JSON.parse(event.data);if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails)});
   await send('Runtime.enable');await send('Page.enable');
   // Observe creation; asking a not-yet-initialized canvas for a context would
   // itself choose its type and could prevent KNI creating the intended one.
   await send('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{const original=HTMLCanvasElement.prototype.getContext;HTMLCanvasElement.prototype.getContext=function(...args){const gl=original.apply(this,args);if(this.id==='theCanvas'&&gl&&/webgl/.test(args[0])){window.__p1057GlContext=gl;window.__p1057Gl={requestedType:args[0],requestedAttributes:args[1]??null}}return gl}})()`});
   await send('Page.navigate',{url:menuUrl});
   const end=Date.now()+120000;let result;
   while(Date.now()<end){
    const response=await send('Runtime.evaluate',{expression:`(()=>{const gl=window.__p1057GlContext;if(!gl||gl.drawingBufferWidth<1000||!window.preview?.measurements?.firstFrameMs)return {pending:true,body:document.body.innerText};return {...window.__p1057Gl,attributes:gl.getContextAttributes(),samples:gl.getParameter(gl.SAMPLES),sampleBuffers:gl.getParameter(gl.SAMPLE_BUFFERS),drawingBufferWidth:gl.drawingBufferWidth,drawingBufferHeight:gl.drawingBufferHeight,devicePixelRatio}})()`,returnByValue:true});
    result=response.result?.value;if(result&&!result.pending)break;await new Promise(resolve=>setTimeout(resolve,250));
   }
   fs.writeFileSync(path.join(output,'production-context.json'),JSON.stringify({result,errors},null,2));
   if(!result||result.pending)throw Error('Production KNI context did not start: '+JSON.stringify({result,errors}));
   fs.writeFileSync(path.join(output,'production-menu.png'),Buffer.from((await send('Page.captureScreenshot',{format:'png'})).data,'base64'));
   console.log(JSON.stringify(result));return;
  }
  const results=[];
  for(const aa of [true,false,'kni']){
   function render(scene,aa){
    document.body.style.margin='0';const canvas=document.createElement('canvas');canvas.width=scene.width;canvas.height=scene.height;canvas.style.display='block';document.body.replaceChildren(canvas);
    const kni=aa==='kni';
    const gl=kni?canvas.getContext('webgl2',{depth:true,stencil:false,antialias:false,preserveDrawingBuffer:false}):aa?canvas.getContext('webgl'):canvas.getContext('webgl',{antialias:false});
    if(!gl)throw Error('WebGL unavailable');
    const shader=(type,source)=>{const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s};
    const program=gl.createProgram();gl.attachShader(program,shader(gl.VERTEX_SHADER,(kni?'#version 300 es\nin vec3 position;in vec3 color;uniform mat4 projection;out highp vec3 tint;':'attribute vec3 position;attribute vec3 color;uniform mat4 projection;varying highp vec3 tint;')+'void main(){gl_Position=projection*vec4(position,1.0);gl_Position.z=gl_Position.z*2.0-gl_Position.w;tint=color;}'));
    gl.attachShader(program,shader(gl.FRAGMENT_SHADER,kni?'#version 300 es\nprecision highp float;in highp vec3 tint;out vec4 outputColor;void main(){outputColor=vec4(tint,1.0);}':'precision highp float;varying highp vec3 tint;void main(){gl_FragColor=vec4(tint,1.0);}'));gl.linkProgram(program);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(program));gl.useProgram(program);
    gl.uniformMatrix4fv(gl.getUniformLocation(program,'projection'),false,new Float32Array(scene.projection));
    gl.viewport(0,0,scene.width,scene.height);gl.clearColor(2/255,4/255,8/255,1);gl.enable(gl.DEPTH_TEST);gl.depthFunc(gl.LEQUAL);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
    for(const object of scene.objects){const buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(object.flat()),gl.STATIC_DRAW);for(const [name,offset]of [['position',0],['color',12]]){const location=gl.getAttribLocation(program,name);gl.enableVertexAttribArray(location);gl.vertexAttribPointer(location,3,gl.FLOAT,false,24,offset)}gl.drawArrays(gl.TRIANGLES,0,object.length)}
    const result={attributes:gl.getContextAttributes(),samples:gl.getParameter(gl.SAMPLES),sampleBuffers:gl.getParameter(gl.SAMPLE_BUFFERS),drawingBufferWidth:gl.drawingBufferWidth,drawingBufferHeight:gl.drawingBufferHeight,devicePixelRatio,png:canvas.toDataURL('image/png')};
    if(gl.getError()!==gl.NO_ERROR)throw Error('WebGL error');return result;
   }
   const response=await send('Runtime.evaluate',{expression:'('+render.toString()+')('+JSON.stringify(scene)+','+JSON.stringify(aa)+')',returnByValue:true});
   if(response.exceptionDetails)throw Error(JSON.stringify(response.exceptionDetails));
   const {png,...result}=response.result.value;
   fs.writeFileSync(path.join(output,aa==='kni'?'browser-kni-policy.png':aa?'browser-default.png':'browser-off.png'),Buffer.from(png.split(',')[1],'base64'));
   results.push({requested:aa==='kni'?'WebGL2 with measured production KNI policy':aa?'getContext("webgl") without attributes':'antialias:false control',...result});
  }
  fs.writeFileSync(path.join(output,'context.json'),JSON.stringify({browser:await send('Browser.getVersion'),representativeGpuTiming:false,fixture:'same native meshes, vertex colours, camera and 1920x1080 drawing buffer as desktop; excludes textures and post',results},null,2));
  console.log(JSON.stringify(results));
 }finally{socket?.close();child.kill('SIGTERM');await exited;}
}
main().catch(error=>{console.error(error);process.exitCode=1});
