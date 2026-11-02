using D2AbilityTracker;

internal static class InputSenderTests
{
    internal static void Run()
    {
        var binding = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.Control | InputModifiers.Shift);
        var (press, release) = InputBindingSender.CreateEvents(binding, key => key == 0x47 ? 0x22u : key, key => key == 0x10);
        Assert(press.Length == 2 && release.Length == 2, "Physically held modifiers must not be injected or released.");
        Assert(press.All(input => input.Data.Keyboard.VirtualKey == 0 && (input.Data.Keyboard.Flags & 8) != 0) &&
            press[1].Data.Keyboard.ScanCode == 0x22 && release[0].Data.Keyboard.ScanCode == 0x22 &&
            release.All(input => (input.Data.Keyboard.Flags & 2) != 0),
            "Keyboard events must use scan codes, releasing the main key before modifiers.");
        var extended = InputBindingSender.CreateEvents(new InputBinding(InputDevice.Keyboard, 0x27, InputModifiers.None),
            _ => 0xe04d, _ => false);
        Assert(extended.Press[0].Data.Keyboard.Flags == 9 && extended.Release[0].Data.Keyboard.Flags == 11 &&
            extended.Press[0].Data.Keyboard.ScanCode == 0x4d, "Extended scan codes must keep their extended-key flag.");
        var mouse = InputBindingSender.CreateEvents(new InputBinding(InputDevice.Mouse, MouseInputCode.X1, InputModifiers.None),
            key => key, _ => false);
        Assert(mouse.Press.Single().Data.Mouse.Flags == 0x80 && mouse.Release.Single().Data.Mouse.Flags == 0x100 &&
            mouse.Press[0].Data.Mouse.MouseData == 1, "Mouse button taps must preserve X-button identity across separate edges.");
        var wheel = InputBindingSender.CreateEvents(new InputBinding(InputDevice.Mouse, MouseInputCode.WheelDown, InputModifiers.None),
            key => key, _ => false);
        Assert(wheel.Release.Length == 0 && wheel.Press[0].Data.Mouse.MouseData == unchecked((uint)-120),
            "Wheel bindings must remain single impulses, not invent release events.");
        var nativeKey = InputBindingSender.CreateEvents(new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.None),
            key => InputNativeMethods.MapVirtualKey(key, 4), _ => false);
        Assert(nativeKey.Press[0].Data.Keyboard.ScanCode != 0, "Windows must resolve the recorded key to a usable scan code.");
        var heldRejected = false;
        try { InputBindingSender.CreateEvents(binding, key => key, _ => true); }
        catch (InvalidOperationException) { heldRejected = true; }
        Assert(heldRejected, "Already-held ability keys must not be spuriously released by an automatic tap.");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<InputBindingSender.InputEvent[]>();
        var holdMilliseconds = 0;
        var pending = InputBindingSender.SendTapAsync(press, release, sent.Add, milliseconds =>
        {
            holdMilliseconds = milliseconds;
            return gate.Task;
        }, 1750);
        Assert(!pending.IsCompleted && sent.Count == 1 && ReferenceEquals(sent[0], press) && holdMilliseconds == 1750,
            "A tap must leave the key down across game frames without blocking the capture worker.");
        gate.SetResult();
        pending.GetAwaiter().GetResult();
        Assert(sent.Count == 2 && ReferenceEquals(sent[1], release), "The hold must end with a separate release batch.");
        sent.Clear();
        try
        {
            InputBindingSender.SendTapAsync(press, release, events =>
            {
                sent.Add(events);
                if (ReferenceEquals(events, press)) throw new InvalidOperationException("Simulated partial send.");
            }, _ => Task.CompletedTask).GetAwaiter().GetResult();
            throw new Exception("A failed press must not report success.");
        }
        catch (InvalidOperationException)
        {
            Assert(sent.Count == 2 && ReferenceEquals(sent[1], release), "Partial sends must still attempt key-up cleanup.");
        }
        sent.Clear();
        try
        {
            InputBindingSender.SendTapAsync(press, release, sent.Add,
                _ => Task.FromException(new InvalidOperationException("Simulated delay failure."))).GetAwaiter().GetResult();
            throw new Exception("A failed hold must not report success.");
        }
        catch (InvalidOperationException)
        {
            Assert(sent.Count == 2 && ReferenceEquals(sent[1], release), "A failed hold must still release injected input.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
