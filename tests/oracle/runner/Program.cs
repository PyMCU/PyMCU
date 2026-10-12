using System.Globalization;
using CH32VSharp.TestKit;

// Same contract as the AVR and ARM oracle runners: run a compiled firmware image until "END\n"
// shows up on the console UART (USART1, PD5), or a timeout elapses, and print whatever the UART
// captured (partial output included) so the Python harness can diff it against CPython.
//
// Usage: PyMCU.OracleRunner.CH32 <ch32v003> <firmware.bin> <max_ms>
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: PyMCU.OracleRunner.CH32 <ch32v003> <firmware.bin> <max_ms>");
    return 2;
}

if (args[0] != "ch32v003")
{
    Console.Error.WriteLine($"[runner] unknown chip '{args[0]}'");
    return 2;
}

var maxMs = double.Parse(args[2], CultureInfo.InvariantCulture);
var sim = Ch32Simulation.FromFile(args[1]);
try
{
    sim.RunUntilOutput("END\n", maxMs);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[runner] ch32v003 run raised: {ex}");
}
Console.Write(sim.UartText.Replace("\r\n", "\n"));
return 0;
