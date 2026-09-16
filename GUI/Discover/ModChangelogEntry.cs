using System;

namespace CKAN.GUI
{
    /// <summary>
    /// A single version's changelog information for a mod, sourced either from the
    /// local CKAN registry or from the mod's public GitHub releases.
    /// </summary>
    public sealed class ModChangelogEntry
    {
        public string     Version     { get; }
        public DateTime?  ReleaseDate { get; }
        public bool       IsInstalled { get; }
        public bool       IsLatest    { get; }
        public bool       IsSelected  { get; }
        public string?    Title       { get; }
        public string?    Notes       { get; }
        public string     Source      { get; }
        public string?    Url         { get; }

        public ModChangelogEntry(string    version,
                                 DateTime? releaseDate,
                                 bool      isInstalled,
                                 bool      isLatest,
                                 bool      isSelected,
                                 string?   title,
                                 string?   notes,
                                 string    source,
                                 string?   url)
        {
            if (version == null)
            {
                throw new ArgumentNullException(nameof(version));
            }
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            Version     = version;
            ReleaseDate = releaseDate;
            IsInstalled = isInstalled;
            IsLatest    = isLatest;
            IsSelected  = isSelected;
            Title       = title;
            Notes       = notes;
            Source      = source;
            Url         = url;
        }
    }
}
