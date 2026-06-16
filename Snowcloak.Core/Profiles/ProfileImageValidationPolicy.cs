using Snowcloak.Core.IO;

namespace Snowcloak.Core.Profiles;

public enum ProfileImageKind
{
    Portrait,
    Header,
}

public enum ProfileImageValidationFailure
{
    None,
    Empty,
    InvalidPng,
    TooManyPixels,
    TooLarge,
}

public readonly record struct ProfileImageValidationResult(
    bool IsValid,
    ProfileImageValidationFailure Failure,
    int Width,
    int Height,
    int MaxBytes,
    int MaxPixels)
{
    public static ProfileImageValidationResult Valid(int width, int height, int maxBytes, int maxPixels)
        => new(true, ProfileImageValidationFailure.None, width, height, maxBytes, maxPixels);

    public static ProfileImageValidationResult Invalid(ProfileImageValidationFailure failure, int maxBytes, int maxPixels)
        => new(false, failure, 0, 0, maxBytes, maxPixels);
}

public static class ProfileImageValidationPolicy
{
    public const int MaxPortraitUploadBytes = 4 * 1024 * 1024;
    public const int MaxHeaderUploadBytes = 8 * 1024 * 1024;
    public const int MaxUploadSourcePixels = 16_777_216;

    public static ProfileImageValidationResult ValidateUpload(byte[] bytes, ProfileImageKind kind)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var maxBytes = GetMaxBytes(kind);
        if (bytes.Length == 0)
        {
            return ProfileImageValidationResult.Invalid(ProfileImageValidationFailure.Empty, maxBytes, MaxUploadSourcePixels);
        }

        using var stream = new MemoryStream(bytes);
        var dimensions = PngHeaderReader.TryExtractDimensions(stream);
        if (dimensions == PngHeaderReader.InvalidSize)
        {
            return ProfileImageValidationResult.Invalid(ProfileImageValidationFailure.InvalidPng, maxBytes, MaxUploadSourcePixels);
        }

        if ((long)dimensions.Width * dimensions.Height > MaxUploadSourcePixels)
        {
            return new ProfileImageValidationResult(
                false,
                ProfileImageValidationFailure.TooManyPixels,
                dimensions.Width,
                dimensions.Height,
                maxBytes,
                MaxUploadSourcePixels);
        }

        return bytes.Length > maxBytes
            ? new ProfileImageValidationResult(
                false,
                ProfileImageValidationFailure.TooLarge,
                dimensions.Width,
                dimensions.Height,
                maxBytes,
                MaxUploadSourcePixels)
            : ProfileImageValidationResult.Valid(dimensions.Width, dimensions.Height, maxBytes, MaxUploadSourcePixels);
    }

    public static int GetMaxBytes(ProfileImageKind kind)
        => kind == ProfileImageKind.Header ? MaxHeaderUploadBytes : MaxPortraitUploadBytes;
}
