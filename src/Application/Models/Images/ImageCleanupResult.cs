namespace Application.Models.Images;

public sealed record ImageCleanupResult(
	int DeletedImageSets,
	int DeletedReceiptDirectories,
	int FailedEntries = 0);
