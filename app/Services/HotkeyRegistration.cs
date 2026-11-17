using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public interface IHotkeyBackend
{
    bool TryRegister(int id, RevertHotkey gesture, out int error);
    void Unregister(int id);
}

/// <summary>Register first, persist second, release old last. Failure preserves the working binding.</summary>
public sealed class HotkeyRegistration : IDisposable
{
    private readonly IHotkeyBackend _backend;
    public int? ActiveId { get; private set; }
    public RevertHotkey? ActiveGesture { get; private set; }
    public HotkeyRegistration(IHotkeyBackend backend) => _backend = backend;

    public void Change(RevertHotkey? gesture, Action persist)
    {
        if (gesture == ActiveGesture) { persist(); return; }
        int id = ActiveId == 0x4D53 ? 0x4D54 : 0x4D53;
        if (gesture is not null && !_backend.TryRegister(id, gesture, out int error))
            throw new InvalidOperationException($"Shortcut {gesture} is unavailable or reserved (Windows error {error}). Previous shortcut was retained.");
        try { persist(); }
        catch { if (gesture is not null) _backend.Unregister(id); throw; }
        if (ActiveId is int previous) _backend.Unregister(previous);
        ActiveId = gesture is null ? null : id;
        ActiveGesture = gesture;
    }

    public bool Matches(int id, uint modifiers, uint key) => ActiveId == id &&
        ActiveGesture is not null && ActiveGesture.Modifiers == modifiers && ActiveGesture.VirtualKey == key;

    public void Dispose()
    {
        if (ActiveId is int id) _backend.Unregister(id);
        ActiveId = null;
        ActiveGesture = null;
    }
}
