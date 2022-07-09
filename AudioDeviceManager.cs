using CSCore.CoreAudioAPI;

namespace NoSilence;

public class AudioDeviceManager
{
    public IEnumerable<MMDevice> GetAudioDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active);
    }

    public MMDevice GetDefaultDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }
}