/// One `CollectionBanner`, in words: the sentence and the button title that both the popover's
/// banner slot and the Collections window's banner strip show. Shared so the two surfaces cannot
/// word the same news differently, and so only one place knows which collection a banner is
/// about.
///
/// The sentences are `AppState`'s statics and the button titles `PopoverModel`'s — where the
/// string catalog already keys them, so nothing here owns wording of its own.
///
/// Mirror: windows/src/ConnectorControl.Core/State/CollectionBannerPresentation.cs
@MainActor
enum CollectionBannerPresentation {
    /// Which collection the banner is about. A window's strip stays quiet until this is the
    /// collection it is showing: the popover's slot speaks for whichever collection has news,
    /// but a window that is looking at one collection must not answer for another.
    static func collection(of banner: CollectionBanner) -> String {
        switch banner {
        case .updateAvailable(let collection, _): return collection
        case .locate(let collection, _): return collection
        case .publishFailed(let collection, _): return collection
        }
    }

    static func text(_ banner: CollectionBanner, _ state: AppState) -> String {
        switch banner {
        case .updateAvailable(let collection, let summary):
            return AppState.collectionUpdateBanner(collection, summary)
        case .locate(let collection, _):
            return AppState.collectionLocateBanner(collection)
        case .publishFailed(let collection, let message):
            // The folder is the binding's, not the banner's: publishing is what sets the error,
            // so the collection that failed always has one.
            return AppState.collectionPublishFailedBanner(
                collection, state.collectionsCache.published[collection]?.folder ?? "", message)
        }
    }

    static func button(_ banner: CollectionBanner) -> String {
        switch banner {
        case .updateAvailable: return PopoverModel.reviewAndApplyButton
        case .locate(_, let fileName): return PopoverModel.locateButton(fileName)
        case .publishFailed: return PopoverModel.chooseFolderButton
        }
    }

    /// A second button, for the one banner that has two answers: point the collection at another
    /// folder, or stop publishing it. The wording is the Collections window's action link, since
    /// this is the same command reached from somewhere else. nil for the other two banners.
    static func secondaryButton(_ banner: CollectionBanner) -> String? {
        guard case .publishFailed = banner else { return nil }
        return CollectionsModel.stopPublishingAction
    }
}
