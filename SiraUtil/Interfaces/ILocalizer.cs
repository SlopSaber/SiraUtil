using BGLib.Polyglot;

namespace SiraUtil.Interfaces
{
    /// <summary>
    /// An interface to provide a link between a mod and the SiraLocalizer mod (custom localizations in game).
    /// </summary>
    public interface ILocalizer
    {

        /// <summary>
        /// Removes a localization asset from Polyglot.
        /// </summary>
        /// <param name="localizationAsset"></param>
        void RemoveLocalizationSheet(LocalizationAsset localizationAsset);

        /// <summary>
        /// Removes a localization asset from Polyglot.
        /// </summary>
        /// <param name="key">The name or source of the asset.</param>
        void RemoveLocalizationSheet(string key);

        /// <summary>
        /// Recalculate the supported languages table.
        /// </summary>
        void RecalculateLanguages();
    }
}