using System.Reflection;
using PiSignage.Control;
using PiSignage.Signage;

namespace signage_core.Tests;

public class SignageWindowTargetTests
{
    [Fact]
    public void Restored_unpaired_target_cannot_remain_checked_and_shows_pair_state()
    {
        var choice = Choice(
            context: null,
            wasRequested: true,
            isChecked: true);

        Assert.False(Bool(choice, "Checked"));
        Assert.False(Bool(choice, "CanControl"));
        Assert.True(Bool(choice, "PairingRequiredSelection"));
        Assert.Contains(
            "Pair this Pi",
            String(choice, "DisplayLabel"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Paired_target_remains_selectable()
    {
        var choice = Choice(
            Context(),
            wasRequested: true,
            isChecked: true);

        Assert.True(Bool(choice, "Checked"));
        Assert.True(Bool(choice, "CanControl"));
        Assert.False(Bool(choice, "PairingRequiredSelection"));
        Assert.Equal("Front TV", String(choice, "DisplayLabel"));
    }

    static object Choice(
        ControlContext? context,
        bool wasRequested,
        bool isChecked)
    {
        var type = typeof(SignageWindow).GetNestedType(
            "TvChoice",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TvChoice type not found.");
        var choice = Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("TvChoice could not be created.");
        Set(choice, "Device", new SavedDevice
        {
            DeviceId = "device-id",
            Name = "Front TV",
        });
        Set(choice, "ControlContext", context);
        Set(choice, "WasRequested", wasRequested);
        Set(choice, "Checked", isChecked);
        return choice;
    }

    static bool Bool(object target, string property) =>
        (bool)Get(target, property);

    static string String(object target, string property) =>
        (string)Get(target, property);

    static object Get(object target, string property) =>
        target.GetType().GetProperty(property)!.GetValue(target)!;

    static void Set(object target, string property, object? value)
    {
        var targetProperty = target.GetType().GetProperty(property)
            ?? throw new Xunit.Sdk.XunitException(
                $"Expected TvChoice.{property} to exist.");
        targetProperty.SetValue(target, value);
    }

    static ControlContext Context()
    {
        long counter = 0;
        return new ControlContext(
            "device-id",
            "test-controller",
            Enumerable.Repeat((byte)1, 32).ToArray(),
            () => Interlocked.Increment(ref counter));
    }
}
