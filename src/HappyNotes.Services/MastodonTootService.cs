using CoreHtmlToImage;
using HappyNotes.Common;
using HappyNotes.Services.interfaces;
using Markdig;
using Mastonet;
using Mastonet.Entities;
using Microsoft.Extensions.Logging;

namespace HappyNotes.Services;

public class MastodonTootService(ILogger<MastodonTootService> logger
, IHttpClientFactory httpClientFactory) : IMastodonTootService
{
    private const int MaxImages = 4;

    private static string _GetStringTags(string longText) =>
        string.Join(' ', longText.GetTags().Where(t => !t.StartsWith("@")).Select(t => $"#{t}"));

    public async Task<Status> SendTootAsync(string instanceUrl, string accessToken, string text, bool isPrivate,
        bool isMarkdown = false, long noteId = 0, long userId = 0)
    {
        logger.LogInformation("Starting SendTootAsync for instance: {InstanceUrl}, textLength: {TextLength}, isPrivate: {IsPrivate}, isMarkdown: {IsMarkdown}",
            instanceUrl, text.Length, isPrivate, isMarkdown);

        try
        {
            // Remove note links (@123) from beginning and end before sending to Mastodon
            text = text.RemoveNoteLinks();
            var fullText = _GetFullText(text, isMarkdown);
            if (fullText.Length > Constants.MastodonTootLength)
            {
                logger.LogInformation("Text exceeds {CharLimit} characters, sending as photo for instance: {InstanceUrl}",
                    Constants.MastodonTootLength, instanceUrl);
                return await _SendLongTootAsPhotoAsync(instanceUrl, accessToken, fullText, isPrivate, isMarkdown);
            }

            var client = new MastodonClient(instanceUrl, accessToken);

            if (!isMarkdown)
            {
                logger.LogDebug("Publishing plain text status to {InstanceUrl}", instanceUrl);
                return await client.PublishStatus(fullText, isPrivate ? Visibility.Private : Visibility.Public);
            }

            logger.LogDebug("Processing markdown images for {InstanceUrl}", instanceUrl);
            var (processedText, mediaIds) = await ProcessMarkdownImagesAsync(client, fullText, noteId, userId);

            logger.LogDebug("Publishing markdown status with {MediaCount} media attachments to {InstanceUrl}",
                mediaIds.Count(), instanceUrl);

            return await client.PublishStatus(
                processedText,
                visibility: isPrivate ? Visibility.Private : Visibility.Public,
                mediaIds: mediaIds
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send toot to {InstanceUrl}. Error: {ErrorMessage}",
                instanceUrl, ex.Message);
            throw new Exception($"Failed to send toot to {instanceUrl}: {ex.Message}", ex);
        }
    }

    private async Task<Status> _SendLongTootAsPhotoAsync(string instanceUrl, string accessToken, string longText,
        bool isPrivate, bool isMarkdown)
    {
        logger.LogDebug("Converting long text to image for {InstanceUrl}, textLength: {TextLength}",
            instanceUrl, longText.Length);

        // Note: longText has already been sanitized by RemoveNoteLinks() in the caller
        var client = new MastodonClient(instanceUrl, accessToken);
        var filePath = Path.GetTempFileName();

        try
        {
            var media = await _UploadLongTextAsMedia(client, longText, isMarkdown);
            logger.LogDebug("Successfully uploaded long text as media to {InstanceUrl}, mediaId: {MediaId}",
                instanceUrl, media.Id);

            var status = await client.PublishStatus(
                _GetStringTags(longText),
                isPrivate ? Visibility.Private : Visibility.Public,
                mediaIds: [media.Id,]
            );

            logger.LogDebug("Successfully published long text as photo to {InstanceUrl}, statusId: {StatusId}",
                instanceUrl, status.Id);
            return status;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send long toot as photo to {InstanceUrl}. Error: {ErrorMessage}",
                instanceUrl, ex.Message);
            throw new Exception($"Failed to send long toot as photo to {instanceUrl}: {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    public async Task<Status> EditTootAsync(
        string instanceUrl,
        string accessToken,
        string tootId,
        string newText,
        bool isPrivate,
        bool isMarkdown = false,
        long noteId = 0,
        long userId = 0)
    {
        logger.LogInformation("Starting EditTootAsync for instance: {InstanceUrl}, tootId: {TootId}, textLength: {TextLength}, isPrivate: {IsPrivate}, isMarkdown: {IsMarkdown}",
            instanceUrl, tootId, newText.Length, isPrivate, isMarkdown);

        try
        {
            // Remove note links (@123) from beginning and end before editing on Mastodon
            newText = newText.RemoveNoteLinks();
            var fullText = _GetFullText(newText, isMarkdown);
            if (fullText.Length > Constants.MastodonTootLength)
            {
                logger.LogInformation("Text exceeds {CharLimit} characters, editing as photo for instance: {InstanceUrl}, tootId: {TootId}",
                    Constants.MastodonTootLength, instanceUrl, tootId);
                return await _EditLongTootAsPhotoAsync(instanceUrl, accessToken, tootId, fullText, isPrivate, isMarkdown);
            }

            var client = new MastodonClient(instanceUrl, accessToken);
            var visibility = isPrivate ? Visibility.Private : Visibility.Public;

            // Get original toot
            logger.LogDebug("Fetching original toot {TootId} from {InstanceUrl}", tootId, instanceUrl);
            var toot = await client.GetStatus(tootId);

            // Process markdown if enabled
            var processedText = fullText;
            IEnumerable<string> mediaIds = Array.Empty<string>();

            if (isMarkdown)
            {
                logger.LogDebug("Processing markdown images for edit on {InstanceUrl}", instanceUrl);
                (processedText, mediaIds) = await ProcessMarkdownImagesAsync(client, newText, noteId, userId);
            }

            // If visibility changed, delete and recreate
            if (toot.Visibility != visibility)
            {
                logger.LogInformation("Visibility changed for toot {TootId} on {InstanceUrl}, deleting and recreating",
                    tootId, instanceUrl);
                await client.DeleteStatus(tootId);
                return await client.PublishStatus(
                    processedText,
                    visibility: visibility,
                    mediaIds: mediaIds
                );
            }

            // Otherwise, update existing toot
            logger.LogDebug("Updating existing toot {TootId} on {InstanceUrl}", tootId, instanceUrl);
            var updatedStatus = await client.EditStatus(
                tootId,
                processedText,
                mediaIds: isMarkdown ? mediaIds : null // Only include mediaIds if markdown was processed
            );

            logger.LogDebug("Successfully edited toot {TootId} on {InstanceUrl}", tootId, instanceUrl);
            return updatedStatus;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to edit toot {TootId} on {InstanceUrl}. Error: {ErrorMessage}",
                tootId, instanceUrl, ex.Message);
            throw new Exception($"Failed to edit toot {tootId} on {instanceUrl}: {ex.Message}", ex);
        }
    }

    private static string _GetFullText(string text, bool isMarkdown)
    {
        if (isMarkdown && text.Length > Constants.MastodonTootLength)
        {
            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions() // This includes tables
                .Build();
            return Markdown.ToHtml(text, pipeline);
        }

        return text;
    }

    private static async Task<Attachment> _UploadLongTextAsMedia(MastodonClient client, string longText,
        bool isMarkdown)
    {
        var htmlContent = isMarkdown ? longText : longText.Replace("\n", "<br>");
        htmlContent =
            $"<html lang=\"en\">\n<head>\n<meta charset=\"UTF-8\">\n<link rel=\"stylesheet\" href=\"https://files.shukebeta.com/markdown.css?v2\" />\n</head>\n<body>\n{htmlContent}</body></html>";
        var converter = new HtmlConverter();
        var bytes = converter.FromHtmlString(htmlContent, width: 600);
        var memoryStream = new MemoryStream(bytes);
        var media = await client.UploadMedia(memoryStream, "long_text.jpg");
        return media;
    }

    public async Task DeleteTootAsync(string instanceUrl, string accessToken, string tootId)
    {
        logger.LogDebug("Starting DeleteTootAsync for instance: {InstanceUrl}, tootId: {TootId}",
            instanceUrl, tootId);

        try
        {
            var client = new MastodonClient(instanceUrl, accessToken);
            await client.DeleteStatus(tootId);
            logger.LogDebug("Successfully deleted toot {TootId} from {InstanceUrl}", tootId, instanceUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete toot {TootId} from {InstanceUrl}. Error: {ErrorMessage}",
                tootId, instanceUrl, ex.Message);
            throw new Exception($"Failed to delete toot {tootId} from {instanceUrl}: {ex.Message}", ex);
        }
    }

    private async Task<Status> _EditLongTootAsPhotoAsync(
        string instanceUrl,
        string accessToken,
        string tootId,
        string longText,
        bool isPrivate,
        bool isMarkdown)
    {
        logger.LogDebug("Editing long toot as photo for {InstanceUrl}, tootId: {TootId}, textLength: {TextLength}",
            instanceUrl, tootId, longText.Length);

        // Note: longText has already been sanitized by RemoveNoteLinks() in the caller
        try
        {
            var client = new MastodonClient(instanceUrl, accessToken);
            var toot = await client.GetStatus(tootId);
            var visibility = isPrivate ? Visibility.Private : Visibility.Public;
            var media = await _UploadLongTextAsMedia(client, longText, isMarkdown);

            logger.LogDebug("Successfully uploaded long text as media for edit on {InstanceUrl}, mediaId: {MediaId}",
                instanceUrl, media.Id);

            // If visibility changed, we need to delete and recreate
            if (toot.Visibility != visibility)
            {
                logger.LogInformation("Visibility changed for long toot {TootId} on {InstanceUrl}, deleting and recreating",
                    tootId, instanceUrl);
                await client.DeleteStatus(tootId);
                return await client.PublishStatus(
                    string.Empty,
                    visibility: visibility,
                    mediaIds: [media.Id]
                );
            }

            // Otherwise, edit the existing toot
            logger.LogDebug("Editing existing long toot {TootId} on {InstanceUrl}", tootId, instanceUrl);
            var editedStatus = await client.EditStatus(
                tootId,
                _GetStringTags(longText),
                mediaIds: [media.Id]
            );

            logger.LogDebug("Successfully edited long toot as photo {TootId} on {InstanceUrl}", tootId, instanceUrl);
            return editedStatus;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to edit long toot as photo {TootId} on {InstanceUrl}. Error: {ErrorMessage}",
                tootId, instanceUrl, ex.Message);
            throw new Exception($"Failed to edit long toot as photo {tootId} on {instanceUrl}: {ex.Message}", ex);
        }
    }

    private async Task<(string text, IEnumerable<string> mediaIds)> ProcessMarkdownImagesAsync(
        MastodonClient client,
        string markdownText,
        long noteId = 0,
        long userId = 0)
    {
        var imgInfos = markdownText.GetImgInfos();
        if (imgInfos.Count == 0)
        {
            return (markdownText, Array.Empty<string>());
        }

        var matches = imgInfos
            .Take(MaxImages)
            .ToList();

        var uploadTasks = new List<Task<(int index, Attachment attachment)>>();

        for (var index = 0; index < matches.Count; index++)
        {
            uploadTasks.Add(UploadImageAsync(client, matches[index].ImgUrl, matches[index].Alt, index));
        }

        var successfulUploads = new List<(int index, Attachment attachment)>();
        var failedIndexes = new HashSet<int>();

        try
        {
            var results = await Task.WhenAll(uploadTasks);
            // if all good, add to successfulUploads
            successfulUploads.AddRange(results);
        }
        catch
        {
            // collect successful results only from all tasks
            for (var i = 0; i < uploadTasks.Count; i++)
            {
                var task = uploadTasks[i];
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    successfulUploads.Add(await task);
                }
                else if (task.Status == TaskStatus.Faulted)
                {
                    failedIndexes.Add(i);
                    logger.LogError(task.Exception, $"Failed to upload image: {matches[i].ImgUrl}");
                }
            }
        }

        // Sort by original index to maintain order, but only keep the successful uploads
        var mediaIds = successfulUploads
            .OrderBy(r => r.index)
            .Select(r => r.attachment.Id)
            .ToList();

        if (successfulUploads.Count < matches.Count)
        {
            var failedUrls = failedIndexes.Select(i => matches[i].ImgUrl).ToList();
            logger.LogWarning(
                "Partial image upload failure for note {NoteId} user {UserId}: {FailedCount}/{TotalCount} images failed. FailedUrls: {FailedUrls}",
                noteId, userId, failedIndexes.Count, matches.Count, failedUrls);
        }

        // Replace markdown image syntax with reference text including alt text
        foreach (var task in successfulUploads)
        {
            var img = matches[task.index];
            var altText = img.Alt;
            altText = altText.Equals("image") ? string.Empty : altText;

            var imageText = !string.IsNullOrWhiteSpace(altText)
                ? $"image {task.index + 1}: {altText}"
                : $"image {task.index + 1}";
            // if there is only one picture
            if (successfulUploads.Count == 1)
            {
                imageText = !string.IsNullOrWhiteSpace(altText)
                    ? $"image: {altText}"
                    : string.Empty;
                if (markdownText.Trim().Equals(imageText))
                {
                    return (string.Empty, mediaIds);
                }
            }

            markdownText = markdownText.Replace(
                img.Match,
                imageText
            );
        }

        // Replace failed image uploads with "alt: url" format for readability
        foreach (var failedIndex in failedIndexes)
        {
            var img = matches[failedIndex];
            markdownText = MarkdownImageHelper.ReplaceFailedImage(
                markdownText,
                img.Match,
                img.Alt,
                img.ImgUrl
            );
        }

        return (markdownText.RemoveImageReference(), mediaIds);
    }

    private async Task<(int index, Attachment attachment)> UploadImageAsync(
        MastodonClient client,
        string imageUrl,
        string altText,
        int index)
    {
        const int maxRetries = 3;
        var retryDelay = TimeSpan.FromSeconds(1);

        logger.LogDebug("Starting image upload: {ImageUrl}, index: {Index}, altText: {AltText}",
            imageUrl, index, altText);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                logger.LogDebug("Image upload attempt {Attempt}/{MaxRetries} for {ImageUrl}",
                    attempt, maxRetries, imageUrl);

                using var httpClient = httpClientFactory.CreateClient();
                using var response = await httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead);

                // Validate response
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to fetch image. Status code: {response.StatusCode}");
                }

                // Verify content type
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("image/"))
                {
                    throw new Exception($"Invalid content type: {contentType}. Expected image/*");
                }

                // Extract filename from URL or use default
                var rawFilename = imageUrl.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Last()
                    .Split('?')[0]
                    .Split('#')[0];

                // Limit filename length to avoid issues with long filenames
                var filename = rawFilename.Length > 50 ? rawFilename[..50] : rawFilename;

                if (string.IsNullOrWhiteSpace(filename))
                {
                    filename = $"image_{index}.jpg";
                }

                logger.LogDebug("Uploading image {ImageUrl} with filename {Filename} and contentType {ContentType}",
                    imageUrl, filename, contentType);

                // Read the content as a stream
                await using var imageStream = await response.Content.ReadAsStreamAsync();

                // Upload with alt text if provided
                var attachment = !string.IsNullOrWhiteSpace(altText)
                    ? await client.UploadMedia(imageStream, filename, altText)
                    : await client.UploadMedia(imageStream, filename);

                logger.LogDebug("Successfully uploaded image {ImageUrl}, attachmentId: {AttachmentId}",
                    imageUrl, attachment.Id);
                return (index, attachment);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Image upload attempt {Attempt}/{MaxRetries} failed for {ImageUrl}: {ErrorMessage}",
                    attempt, maxRetries, imageUrl, ex.Message);

                // If this was our last retry, throw the exception
                if (attempt == maxRetries)
                {
                    logger.LogError(ex, "Failed to upload image {ImageUrl} after {MaxRetries} attempts",
                        imageUrl, maxRetries);
                    throw new Exception($"Failed to upload image {imageUrl} after {maxRetries} attempts: {ex.Message}",
                        ex);
                }

                // Wait before retrying, using exponential backoff
                logger.LogDebug("Waiting {RetryDelay}ms before retry {NextAttempt} for image {ImageUrl}",
                    retryDelay.TotalMilliseconds, attempt + 1, imageUrl);
                await Task.Delay(retryDelay);
                retryDelay *= 2; // Double the delay for next attempt
            }
        }

        // This shouldn't be reached due to the throw in the catch block, but compiler needs it
        throw new Exception($"Failed to upload image {imageUrl}");
    }
}
