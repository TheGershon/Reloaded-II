using ReverseMarkdown;

namespace Reloaded.Mod.Loader.Update.Providers.GameBanana;

/// <summary>
/// Provider that allows for searching of downloadable mods on GameBanana.
/// </summary>
public class GameBananaPackageProvider : IDownloadablePackageProvider
{
    private const string SourceName = "GameBanana";

    /// <summary>
    /// ID of the individual game.
    /// </summary>
    public int GameId { get; private set; }

    private string _dummyFolderForReleaseVerification = Path.GetTempPath();

    /// <summary/>
    public GameBananaPackageProvider(int gameId)
    {
        GameId = gameId;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IDownloadablePackage>> SearchAsync(string text, int skip = 0, int take = 50, CancellationToken token = default)
    {
        // TODO: Potential bug if no manager integrated mods are returned but there are still more items to take.
        // We ignore it for now but it's best revisited in the future.

        int page       = (skip / take) + 1;
        var gbApiItems = await GameBananaMod.GetByNameAsync(text, GameId, page, take);
        var results    = new ConcurrentBag<IDownloadablePackage>();

        if (gbApiItems == null)
            return results;

        var getExtraDataTasks = new Task[gbApiItems.Count];
        for (var x = 0; x < gbApiItems.Count; x++)
            getExtraDataTasks[x] = AddResultsForApiResult(gbApiItems[x], results);

        await Task.WhenAll(getExtraDataTasks);
        return results;
    }

    private async Task AddResultsForApiResult(GameBananaMod gbApiItem, ConcurrentBag<IDownloadablePackage> results)
    {
        if (!(await TryAddResultsFromReleaseMetadataAsync(gbApiItem, results)))
            AddResultsFromRawFiles(gbApiItem, results);
    }

    private static void AddResultsFromRawFiles(GameBananaMod gbApiItem, ConcurrentBag<IDownloadablePackage> results)
    {
        if (gbApiItem.ManagerIntegrations == null || gbApiItem.Files == null)
            return;

        // Check manager integrations.
        var hasMultipleReloadedFiles = CheckIfHasMultipleReloadedFiles(gbApiItem.ManagerIntegrations);
        foreach (var integratedFile in gbApiItem.ManagerIntegrations)
        {
            var fileId = integratedFile.Key;
            var integrations = integratedFile.Value;

            // Build items.
            foreach (var integration in integrations)
            {
                if (!integration.IsReloadedDownloadUrl().GetValueOrDefault())
                    continue;

                var url = new Uri(integration.GetReloadedDownloadUrl());
                var file = gbApiItem.Files.First(x => x.Id == fileId);
                string fileName;
                if (hasMultipleReloadedFiles)
                {
                    fileName = !string.IsNullOrEmpty(file.Description) ?
                        $"{gbApiItem.Name!}: {file.Description}" :
                        $"{gbApiItem.Name!}: {file.FileName}";
                }
                else
                {
                    fileName = $"{gbApiItem.Name!}";
                }

                var package = new WebDownloadablePackage(url, false)
                {
                    Name = fileName,
                    Description = HtmlUtilities.ConvertToPlainText(gbApiItem.Description),
                    MarkdownReadme = Singleton<Converter>.Instance.Convert(gbApiItem.Description)
                };

                GameBananaAddCommon(gbApiItem, file, package);
                results.Add(package);
            }
        }
    }

    /// <summary>
    /// Checks if the submission has multiple raw files that belong to Reloaded.
    /// </summary>
    /// <param name="managerIntegrations">List of manager integrations.</param>
    /// <returns>True or false.</returns>
    private static bool CheckIfHasMultipleReloadedFiles(Dictionary<string, GameBananaManagerIntegration[]> managerIntegrations)
    {
        int counter = 0;
        foreach (var integratedFile in managerIntegrations)
        {
            var integrations = integratedFile.Value;
            foreach (var integration in integrations)
            {
                if (!integration.IsReloadedDownloadUrl().GetValueOrDefault())
                    continue;
                
                counter++;
                if (counter > 1)
                    return true;
            }
        }

        return false;
    }

    private async Task<bool> TryAddResultsFromReleaseMetadataAsync(GameBananaMod gbApiItem, ConcurrentBag<IDownloadablePackage> results)
    {
        const string metadataExtension = ".json";
        const int maxFileSize = 512 * 1024; // 512KB. To prevent abuse of large JSON files.

        if (gbApiItem.Files == null)
            return false;

        int numAddedItems = 0;
        foreach (var file in gbApiItem.Files)
        {
            if (!file.FileName.EndsWith(metadataExtension) || file.FileSize > maxFileSize || string.IsNullOrEmpty(file.DownloadUrl))
                continue;

            // Try download metadata file.
            numAddedItems += await TryAddResultFromReleaseMetadataFile(results, file, gbApiItem);
        }

        return numAddedItems > 0;
    }

    private async Task<int> TryAddResultFromReleaseMetadataFile(ConcurrentBag<IDownloadablePackage> results, GameBananaModFile file, GameBananaMod item)
    {
        var metadata = await SharedHttpClient.CachedAndCompressed.GetByteArrayAsync(new Uri(file.DownloadUrl!));
        try
        {
            // Get metadata & filter potentially invalid file.
            var releaseMetadata = await Singleton<ReleaseMetadata>.Instance.ReadFromDataAsync(metadata);
            if (releaseMetadata.ExtraData == null || releaseMetadata.Releases.Count <= 0)
                return 0;

            // Get the highest version of release.
            var highestVersion = releaseMetadata.Releases.OrderByDescending(x => new NuGetVersion(x.Version)).First();
            var newestRelease = releaseMetadata.GetRelease(highestVersion.Version, new ReleaseMetadataVerificationInfo()
            {
                FolderPath = _dummyFolderForReleaseVerification
            });

            if (newestRelease == null)
                return 0;

            var url = GetDownloadUrlForFileName(newestRelease.FileName, item.Files!, out var modFile);
            if (string.IsNullOrEmpty(url))
                return 0;

            var package = new WebDownloadablePackage(new Uri(url), false)
            {
                Name = item.Name!,
                Description = HtmlUtilities.ConvertToPlainText(item.Description)
            };

            // Get better details from extra data.
            if (TryGetExtraData(releaseMetadata, out var extraData))
            {
                package.Id = !string.IsNullOrEmpty(extraData!.ModId) ? extraData.ModId : package.Name;
                package.Name = !string.IsNullOrEmpty(extraData.ModName) ? extraData.ModName : package.Name;
                package.Description = !string.IsNullOrEmpty(extraData.ModDescription)
                    ? extraData.ModDescription
                    : package.Name;
                package.MarkdownReadme = extraData.Readme;
                package.Changelog = extraData.Changelog;
            }

            // Set enhanced readme if possible.
            if (string.IsNullOrEmpty(package.MarkdownReadme))
                package.MarkdownReadme = Singleton<Converter>.Instance.Convert(item.Description);

            GameBananaAddCommon(item, modFile!, package);
            results.Add(package);
            return 1;
        }
        catch (Exception)
        {
            // Suppress
            return 0;
        }
    }

    private static string? GetDownloadUrlForFileName(string fileName, List<GameBananaModFile> files, out GameBananaModFile? file)
    {
        var expectedFileNames = GameBananaUtilities.GetFileNameStarts(fileName);
        file = default;
        foreach (var expectedFileName in expectedFileNames)
        {
            file = files.FirstOrDefault(x => x.FileName.StartsWith(expectedFileName, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.DownloadUrl;
        }

        return null;
    }

    private static bool TryGetExtraData(ReleaseMetadata releaseMetadata, out ReleaseMetadataExtraData? extraData)
    {
        extraData = default;
        try
        {
            extraData = releaseMetadata.GetExtraData<ReleaseMetadataExtraData>();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void GameBananaAddCommon(GameBananaMod modItem, GameBananaModFile file, WebDownloadablePackage package)
    {
        package.Source = SourceName;
        package.FileSize = file.FileSize.GetValueOrDefault();
        package.ProjectUri = new Uri(modItem.LinkToModPage);
        package.LikeCount = modItem.LikeCount;
        package.ViewCount = modItem.ViewCount;
        package.DownloadCount = modItem.DownloadCount;
        package.Published = file.DateAdded;
        GameBananaAddAuthors(modItem, package);
        GameBananaAddSubmitter(modItem, package);
        GameBananaAddImages(modItem, package);
    }

    private static void GameBananaAddImages(GameBananaMod file, WebDownloadablePackage package)
    {
        if (file.PreviewMedia?.Images == null)
            return;

        var gbImages = file.PreviewMedia.Images;
        if (gbImages.Length <= 0)
            return;

        var images = new DownloadableImage[gbImages.Length];
        var imagesSpan = new SpanList<DownloadableImage>(images);
        var thumbsSpan = new DownloadableImageThumbnail[GameBananaPreviewImage.MaxThumbnailCount];

        foreach (var gbImage in gbImages)
        {
            var baseUri = new Uri($"{gbImage.BaseUrl}/", UriKind.Absolute);
            var image = new DownloadableImage()
            {
                Uri = new Uri(baseUri, gbImage.File),
                Caption = gbImage.Caption
            };

            var thumbs = new SpanList<DownloadableImageThumbnail>(thumbsSpan);
            if (!string.IsNullOrEmpty(gbImage.FileWidth100))
                thumbs.Add(new DownloadableImageThumbnail(new Uri(baseUri, gbImage.FileWidth100), 100));

            if (!string.IsNullOrEmpty(gbImage.FileWidth220))
                thumbs.Add(new DownloadableImageThumbnail(new Uri(baseUri, gbImage.FileWidth220), 220));

            if (!string.IsNullOrEmpty(gbImage.FileWidth530))
                thumbs.Add(new DownloadableImageThumbnail(new Uri(baseUri, gbImage.FileWidth530), 530));

            if (thumbs.Length > 0)
                image.Thumbnails = thumbs.AsSpan.ToArray();

            imagesSpan.Add(image);
        }

        package.Images = images;
    }

    private static void GameBananaAddSubmitter(GameBananaMod result, WebDownloadablePackage package)
    {
        var gbSubmitter = result.Submitter;
        var pkgSubmitter = new Submitter
        {
            UserName = gbSubmitter.Name,
            JoinDate = gbSubmitter.JoinDate,
            ProfileUrl = new Uri(gbSubmitter.ProfileUrl)
        };

        if (gbSubmitter.AvatarUrl != null)
            pkgSubmitter.AvatarUrl = new Uri(gbSubmitter.AvatarUrl);

        package.Submitter = pkgSubmitter;
    }

    private static void GameBananaAddAuthors(GameBananaMod result, WebDownloadablePackage package)
    {
        if (result.Credits == null)
            return;

        var authors = new List<string>();
        foreach (var creditCategory in result.Credits)
        foreach (var credit in creditCategory.Value)
        {
            if (!string.IsNullOrEmpty(credit.Name))
                authors.Add(credit.Name);
        }

        package.Authors = string.Join(", ", authors);
    }
}