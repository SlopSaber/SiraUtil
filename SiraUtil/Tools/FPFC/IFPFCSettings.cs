using System;
using System.ComponentModel;

namespace SiraUtil.Tools.FPFC
{
    /// <summary>
    /// The legacy settings contract used by mods with optional SiraUtil FPFC integration.
    /// </summary>
    public interface IFPFCSettings : INotifyPropertyChanged
    {
        /// <summary>
        /// The camera field of view when no VR headset is active.
        /// </summary>
        float FOV { get; }

        /// <summary>
        /// Whether the controller is enabled.
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// The movement sensitivity.
        /// </summary>
        float MoveSensitivity { get; }

        /// <summary>
        /// The mouse sensitivity.
        /// </summary>
        float MouseSensitivity { get; }

        /// <summary>
        /// Whether to invert the default FPFC state when changed.
        /// </summary>
        bool Ignore { get; }

        /// <summary>
        /// Whether to retain the camera pose when leaving FPFC.
        /// </summary>
        bool LockViewOnDisable { get; }

        /// <summary>
        /// Whether to limit rendering to the display refresh rate.
        /// </summary>
        bool LimitFrameRate { get; }

        /// <summary>
        /// The requested vertical synchronization count.
        /// </summary>
        int VSyncCount { get; }

        /// <summary>
        /// Raised when the settings change.
        /// </summary>
        [Obsolete("Use the PropertyChanged event instead.")]
        event Action<IFPFCSettings> Changed;
    }
}
