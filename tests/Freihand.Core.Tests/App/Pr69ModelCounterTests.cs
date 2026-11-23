using Freihand.Adapters.Dictation;

namespace Freihand.Core.Tests.App;

public sealed class Pr69ModelCounterTests
{
    [Theory]
    [InlineData(true, "ggml-large-v3-turbo-q5_0.bin", 1500)]
    [InlineData(false, "ggml-small.bin", 850)]
    public void AManualModelChangeCannotInheritTheWindowOfTheOtherModel(bool business, string file, int expected)
    {
        var directory = Directory.CreateTempSubdirectory("freihand-pr69-");
        var path = Path.Combine(directory.FullName, "einstellungen.txt");
        try
        {
            var defaults = business
                ? PrototypeSettings.BusinessDefaults(directory.FullName, directory.FullName)
                : PrototypeSettings.Defaults(directory.FullName);
            PrototypeSettings.LoadOrCreate(path, defaults);
            PrototypeSettings.SetValue(path, "modell", Path.Combine(directory.FullName, file));

            var configured = PrototypeSettings.LoadOrCreate(path, defaults);
            Assert.Equal(file, Path.GetFileName(configured.Model));
            Assert.Equal(expected, configured.AudioContext);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }
}