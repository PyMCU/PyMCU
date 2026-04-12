using PyMCU.Common;
using Xunit;

namespace PyMCU.UnitTests;

public class UtilsTests
{
    [Fact]
    public void ReadSource_ValidFile()
    {
        var path = Path.GetTempFileName();
        const string content = "def main():\n    return 0";
        File.WriteAllText(path, content);

        try
        {
            Assert.Equal(content, Utils.ReadSource(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadSource_InvalidFile()
    {
        Assert.Throws<Exception>(() => Utils.ReadSource("non_existent_file.py"));
    }
}
