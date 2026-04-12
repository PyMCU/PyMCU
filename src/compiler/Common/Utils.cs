namespace PyMCU.Common;

public static class Utils
{
    public static string ReadSource(string pathStr)
    {
        if (!File.Exists(pathStr))
        {
            throw new Exception($"File not found: '{pathStr}'\nLooking in: {Path.GetFullPath(pathStr)}");
        }

        try
        {
            return File.ReadAllText(pathStr);
        }
        catch (Exception ex)
        {
            throw new Exception($"Permission denied or locked file: '{pathStr}'. Error: {ex.Message}");
        }
    }
}