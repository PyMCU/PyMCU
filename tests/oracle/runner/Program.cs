using System.Globalization;
using Avr8Sharp.TestKit.Boards;

var sim = new ArduinoUnoSimulation();
sim.WithHex(Console.In.ReadToEnd());
var maxMs = double.Parse(args[0], CultureInfo.InvariantCulture);
try
{
    sim.RunUntilSerial(sim.Serial, "END\n", maxMs);
}
catch (TimeoutException) when (sim.Serial.Text.Length > 0)
{
    // Preserve partial output so the oracle reports the first differing line.
}
Console.Write(sim.Serial.Text.Replace("\r\n", "\n"));
