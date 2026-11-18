// Google Brotli decoder from Microsoft.NETCore.App.Runtime.Mono.browser-wasm 10.0.15.
// MIT; covered by the shipped runtime THIRD-PARTY-NOTICES (Brotli section).
// Prepared asynchronously from hash-verified resident pack bytes; decode does no I/O.
const instances = [], exports = Object.create(null);
const maximumOutput = 4 * 1024 * 1024, arena = 20 * 1024 * 1024;
const memory = new WebAssembly.Memory({ initial: 1024, maximum: 1024 });
const table = new WebAssembly.Table({ initial: 64, element: 'anyfunc' });
const stack = new WebAssembly.Global({ value: 'i32', mutable: true }, 16 * 1024 * 1024);
let heap = arena, ready = false;
const env = new Proxy({
  __linear_memory: memory, __indirect_function_table: table, __stack_pointer: stack,
  malloc: size => {
    size >>>= 0;
    if (size > memory.buffer.byteLength - heap - 16) return 0;
    const pointer = heap; heap = (heap + size + 15) & ~15; return pointer;
  },
  free: () => {}, // One-shot decode owns an arena, reset in finally.
  memcpy: (d, s, n) => { new Uint8Array(memory.buffer).copyWithin(d, s, s + n); return d; },
  memmove: (d, s, n) => { new Uint8Array(memory.buffer).copyWithin(d, s, s + n); return d; },
  memset: (d, value, n) => { new Uint8Array(memory.buffer).fill(value, d, d + n); return d; }
}, { get: (target, key) => key in target ? target[key] : (...args) => {
  if (!exports[key]) throw new Error('Missing runtime decoder symbol: ' + String(key));
  return exports[key](...args);
} });
export function begin() {
  if (ready) return;
  instances.length = 0;
  for (const key of Object.keys(exports)) delete exports[key];
}
export async function addModule(bytes) {
  if (ready) return; // The same verified generation is reused when staging is retried.
  const { instance } = await WebAssembly.instantiate(bytes, { env });
  instances.push(instance); Object.assign(exports, instance.exports);
}
export function finish() {
  if (ready) return;
  // Generated relocation table in brotli/modules.json; only the two C allocator callbacks.
  if (instances.length !== 10) throw new Error('Incomplete runtime decoder pack');
  table.set(1, instances[3].exports.BrotliDefaultFreeFunc);
  table.set(2, instances[3].exports.BrotliDefaultAllocFunc);
  ready = true;
}
export function decode(input) {
  if (!ready) throw new Error('Runtime decoder is not staged');
  if (!(input instanceof Uint8Array) || input.length > 65531) throw new Error('Invalid briefing input');
  heap = arena; stack.value = 16 * 1024 * 1024;
  try {
    const source = env.malloc(input.length), size = env.malloc(4), target = env.malloc(maximumOutput);
    new Uint8Array(memory.buffer).set(input, source);
    const view = new DataView(memory.buffer); view.setUint32(size, maximumOutput, true);
    const result = exports.BrotliDecoderDecompress(input.length, source, size, target);
    const length = view.getUint32(size, true);
    if (result !== 1 || length > maximumOutput) throw new Error('Invalid or oversized mission briefing');
    return new Uint8Array(memory.buffer, target, length).slice();
  } finally { heap = arena; stack.value = 16 * 1024 * 1024; }
}
