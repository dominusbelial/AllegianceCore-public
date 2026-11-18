// Exercise the production runtime decoder, not a reimplementation. Entirely local.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict'), zlib = require('node:zlib');
async function main() {
  const root = path.resolve('src/Allegiance.ClientCore.Browser/wwwroot');
  const decoder = await import('data:text/javascript;base64,' + fs.readFileSync(path.join(root,'brotli.js')).toString('base64'));
  decoder.begin();
  const manifest = JSON.parse(fs.readFileSync(path.join(root,'brotli/modules.json')));
  for (const module of manifest.modules) await decoder.addModule(fs.readFileSync(path.join(root,'brotli',module)));
  decoder.finish();
  let cases = 0, checks = 0, random = 1051;
  function bytes(length) { return Buffer.from(Array.from({length}, () => { random = (Math.imul(random,1664525)+1013904223)>>>0; return random>>>24; })); }
  for (const source of [Buffer.alloc(0),Buffer.from('briefing é'),bytes(32768),Buffer.alloc(1024*1024,65),Buffer.alloc(4*1024*1024,66)])
    for (const quality of [0,1,4,11]) {
      const compressed = zlib.brotliCompressSync(source,{params:{[zlib.constants.BROTLI_PARAM_QUALITY]:quality}});
      assert.deepEqual(Buffer.from(decoder.decode(compressed)),source); checks++; cases++;
    }
  for (const invalid of [Buffer.alloc(0),Buffer.from([255,255,255]),bytes(17),zlib.brotliCompressSync(bytes(4000)).subarray(0,12),zlib.brotliCompressSync(Buffer.alloc(4*1024*1024+1,65)),Buffer.alloc(65532)]) {
    assert.throws(()=>decoder.decode(invalid)); cases++; checks++;
    // A rejected/trapped frame must not corrupt the arena/stack for the next message.
    const source=Buffer.from('healthy after rejection');assert.deepEqual(Buffer.from(decoder.decode(zlib.brotliCompressSync(source))),source);checks++;
  }
  const evidence=[];
  if (process.argv[2]) for (const file of fs.readdirSync(process.argv[2]).filter(n=>/^briefing-.*\.bin$/.test(n))) {
    const message=fs.readFileSync(path.join(process.argv[2],file));
    assert.equal(message.readUInt16LE(0),message.length);assert.equal(message.readUInt16LE(2),996);
    const desktop=fs.readFileSync(path.join(process.argv[2],file.replace(/\.bin$/,'.desktop.json'))),decoded=Buffer.from(decoder.decode(message.subarray(4)));
    assert.deepEqual(zlib.brotliDecompressSync(message.subarray(4)),desktop);
    assert.deepEqual(decoded,desktop);checks++;cases++;evidence.push({file,compressedBytes:message.length-4,decodedBytes:decoded.length,byteEqual:true});
  }
  console.log(JSON.stringify({passed:true,cases,checks,realBriefings:evidence}));
}
main().catch(error=>{console.error(error);process.exitCode=1;});
