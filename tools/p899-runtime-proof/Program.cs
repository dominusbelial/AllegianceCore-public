using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

// Bounded, paced .NET diagnostic. The optional seed synthesizes a historical
// counter boundary; it is NOT a reproduction of the Allegiance incident.
var bag = new ConcurrentBag<object>();
for (int i = 0; i < 3; i++) bag.Add(new object());
var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
object locals = bag.GetType().GetField("_locals", flags)!.GetValue(bag)!;
object queue = locals.GetType().GetProperty("Value")!.GetValue(locals)!;
var type = queue.GetType();
Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription} assembly={bag.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion}");
var method = type.GetMethod("LocalPush", flags)!;
var opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public)
    .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
    .ToDictionary(op => unchecked((ushort)op.Value));
byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
for (int at = 0; at < il.Length;)
{
    int offset = at;
    ushort code = il[at++];
    if (code == 0xfe) code = (ushort)(0xfe00 | il[at++]);
    var op = opcodes[code];
    int size = op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(il, at) * 4,
        _ => 4,
    };
    if (op.Name!.Contains("ovf")) Console.WriteLine($"LocalPush IL_{offset:x4}: {op.Name}");
    at += size;
}

if (args.Contains("--seed-counter-boundary"))
{
    // Three live elements with counters representing many cross-thread returns
    // and steals. Ring positions and counters agree on occupancy = three.
    int head = int.MaxValue - 4, tail = int.MaxValue - 1;
    type.GetField("_headIndex", flags)!.SetValue(queue, head);
    type.GetField("_tailIndex", flags)!.SetValue(queue, tail);
    type.GetField("_addTakeCount", flags)!.SetValue(queue, tail);
    type.GetField("_stealCount", flags)!.SetValue(queue, head);
    var array = (object[])type.GetField("_array", flags)!.GetValue(queue)!;
    Array.Clear(array);
    for (int i = head; i < tail; i++) array[i & 31] = new object();
    Console.WriteLine("SYNTHETIC historical-counter seed: occupancy=3; two paced pushes, one foreign steal");
    bag.Add(new object());
    Task.Run(() => bag.TryTake(out _)).GetAwaiter().GetResult();
    Thread.Sleep(10);
    try { bag.Add(new object()); Console.WriteLine("no overflow at seeded boundary"); }
    catch (OverflowException)
    {
        Console.WriteLine($"seeded overflow; addTake={type.GetField("_addTakeCount", flags)!.GetValue(queue)}; "
            + $"head={type.GetField("_headIndex", flags)!.GetValue(queue)}; tail={type.GetField("_tailIndex", flags)!.GetValue(queue)}");
    }
}
else
{
    using var work = new BlockingCollection<bool>();
    using var done = new AutoResetEvent(false);
    var thief = new Thread(() => { foreach (bool command in work.GetConsumingEnumerable()) { bag.TryTake(out _); done.Set(); } });
    thief.Start();
    for (int i = 0; i < 100; i++)
    {
        bag.Add(new object()); work.Add(true); done.WaitOne(); Thread.Sleep(10);
    }
    work.CompleteAdding(); thief.Join();
    Console.WriteLine($"real paced transfers=100 overflow=none retained={bag.Count}");
}
