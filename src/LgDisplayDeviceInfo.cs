using PepperDash.Essentials.Core.DeviceInfo;

namespace Epi.Display.Lg
{
    /// <summary>An LG display's identity, with the model telemetry reads by name.</summary>
    public class LgDisplayDeviceInfo : DeviceInfo
    {
        /// <summary>The model the display publishes over UPnP (e.g. 55UR340C9UD); null until it has been read.</summary>
        public string Model { get; set; }
    }
}
