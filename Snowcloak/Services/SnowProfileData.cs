using System;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;

namespace Snowcloak.Services;

public record SnowProfileData(UserData? User, bool IsFlagged, bool IsNSFW, string Base64ProfilePicture, string Description, ProfileVisibility Visibility)
    {
        public Lazy<byte[]> ImageData { get; } = new Lazy<byte[]>(() =>
        {
            if (string.IsNullOrEmpty(Base64ProfilePicture)) return Array.Empty<byte>();
            return Convert.FromBase64String(Base64ProfilePicture);
        });
    }
