using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.PlayerData;

public readonly record struct DirectPermissions(UserPermissions Own, UserPermissions Other);
