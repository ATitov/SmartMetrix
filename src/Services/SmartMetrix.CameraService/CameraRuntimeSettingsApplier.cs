using SmartMetrix.ServiceDefaults;

namespace SmartMetrix.CameraService;

public sealed class CameraRuntimeSettingsApplier(ICameraAdapter adapter) : IRuntimeSettingsApplier<CameraOptions>
{
    public void Apply(CameraOptions value)
    {
        if (adapter is not ArenaCameraAdapter arena) throw new InvalidOperationException("Runtime exposure changes require the Arena hardware backend.");
        arena.ApplyConfiguration(value);
    }
}
