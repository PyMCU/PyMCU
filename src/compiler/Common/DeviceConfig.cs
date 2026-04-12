namespace PyMCU.Common;

public class DeviceConfig
{
    public string Chip { get; set; } = "";
    public string TargetChip { get; set; } = ""; // Source of Truth (CLI/TOML)
    public string DetectedChip { get; set; } = ""; // From source code (device_info)
    public string Arch { get; set; } = "";
    public ulong Frequency { get; set; }
    public int RamSize { get; set; } = 0;
    public int FlashSize { get; set; } = 0;
    public int EepromSize { get; set; } = 0;
    public Dictionary<string, string> Fuses { get; set; } = new();
    public int ResetVector { get; set; } = -1;
    public int InterruptVector { get; set; } = -1;
    public int InterruptVectorHigh { get; set; } = -1;
    public int InterruptVectorLow { get; set; } = -1;
}