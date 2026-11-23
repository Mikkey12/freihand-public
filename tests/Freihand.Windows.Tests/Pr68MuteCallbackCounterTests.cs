using System.Reflection;
using System.Runtime.CompilerServices;
using Freihand.Windows.Audio;
using NAudio.CoreAudioApi;

namespace Freihand.Windows.Tests;

public sealed class Pr68MuteCallbackCounterTests
{
    [Fact]
    public async Task EndpointCallbackReturnsEvenWhileAStatusSubscriberIsBusy()
    {
        var type = typeof(WasapiAudio).GetNestedType("WasapiCaptureBackend", BindingFlags.NonPublic)!;
        var backend = RuntimeHelpers.GetUninitializedObject(type);
        var method = type.GetMethod("OnVolumeNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        EventHandler<bool> subscriber = (_, muted) =>
        {
            Assert.True(muted);
            entered.TrySetResult();
            release.Wait();
            finished.Set();
        };
        type.GetField("_muteChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(backend, subscriber);
        var data = new AudioVolumeNotificationData(Guid.Empty, true, 1, [], Guid.Empty);
        var nativeCallback = Task.Run(() => method.Invoke(backend, [data]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await nativeCallback.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.Set();
            await nativeCallback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}