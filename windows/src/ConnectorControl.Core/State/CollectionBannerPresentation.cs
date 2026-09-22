namespace ConnectorControl.Core.State;

/// <summary>
/// One <see cref="CollectionBanner"/>, in words: the sentence and the button title that both the
/// flyout's banner slot and the Collections window's banner strip show. Shared so the two
/// surfaces cannot word the same news differently, and so only one place knows which collection
/// a banner is about.
///
/// The sentences are <see cref="AppState"/>'s constants and the button titles
/// <see cref="FlyoutModel"/>'s — where the string catalog already keys them, so nothing here
/// owns wording of its own.
///
/// Mirror: Sources/ConnectorControlState/CollectionBannerPresentation.swift
/// </summary>
internal static class CollectionBannerPresentation
{
    /// <summary>
    /// Which collection the banner is about. A window's strip stays quiet until this is the
    /// collection it is showing: the flyout's slot speaks for whichever collection has news, but
    /// a window that is looking at one collection must not answer for another.
    /// </summary>
    internal static string Collection(CollectionBanner banner) => banner switch
    {
        CollectionBanner.UpdateAvailable update => update.Collection,
        CollectionBanner.Locate locate => locate.Collection,
        CollectionBanner.PublishFailed failed => failed.Collection,
        // Unreachable: the record hierarchy's constructor is private, so these are all of them.
        // Throwing rather than answering blandly, because a strip that claims to have news and
        // then shows nothing is harder to notice than a crash.
        _ => throw new ArgumentOutOfRangeException(nameof(banner)),
    };

    internal static string Text(CollectionBanner banner, AppState state) => banner switch
    {
        CollectionBanner.UpdateAvailable update => AppState.CollectionUpdateBanner(update.Collection, update.Summary),
        CollectionBanner.Locate locate => AppState.CollectionLocateBanner(locate.Collection),
        // The folder is the binding's, not the banner's: publishing is what sets the error, so
        // the collection that failed always has one.
        CollectionBanner.PublishFailed failed => AppState.CollectionPublishFailedBanner(
            failed.Collection,
            state.CollectionsCache.Published.TryGetValue(failed.Collection, out var binding) ? binding.Folder : "",
            failed.Message),
        _ => throw new ArgumentOutOfRangeException(nameof(banner)),
    };

    internal static string Button(CollectionBanner banner) => banner switch
    {
        CollectionBanner.UpdateAvailable => FlyoutModel.ReviewAndApplyButton,
        CollectionBanner.Locate locate => FlyoutModel.LocateButton(locate.FileName),
        CollectionBanner.PublishFailed => FlyoutModel.ChooseFolderButton,
        _ => throw new ArgumentOutOfRangeException(nameof(banner)),
    };

    /// <summary>
    /// A second button, for the one banner that has two answers: point the collection at another
    /// folder, or stop publishing it. The wording is the Collections window's action link, since
    /// this is the same command reached from somewhere else. Null for the other two banners.
    /// </summary>
    internal static string? SecondaryButton(CollectionBanner banner) =>
        banner is CollectionBanner.PublishFailed ? CollectionsModel.StopPublishingAction : null;
}
