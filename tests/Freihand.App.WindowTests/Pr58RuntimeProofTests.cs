using System.Reflection;
using System.Reflection.Emit;
using System.Windows;

namespace Freihand.App;

public sealed class Pr58RuntimeProofTests
{
    [Fact]
    public void LoadedWpfShutsDownOnQueryAndQueuesItsActualShutdown()
    {
        var application = typeof(Application);
        Assert.Equal(10, application.Assembly.GetName().Version!.Major);
        var query = application.GetMethod("WmQueryEndSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shutdown = application.GetMethod("CriticalShutdown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Contains(Calls(query), method => method.Name == "Shutdown");
        Assert.Contains(Calls(shutdown), method => method.Name == "BeginInvoke" && method.DeclaringType?.FullName == "System.Windows.Threading.Dispatcher");
    }

    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var position = 0; position < bytes.Length;)
        {
            short value = bytes[position++];
            if (value == 0xfe) value = (short)(0xfe00 | bytes[position++]);
            var code = codes[value];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, position));
                if (called is not null) yield return called;
            }
            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, position),
                _ => 4,
            };
        }
    }
}
