using Snowcloak.API.Data.Enum;

namespace Snowcloak.Core.PlayerData;

public readonly record struct GroupPermissionView(
    GroupUserPermissions GroupUserPermissions,
    GroupPermissions GroupPermissions,
    GroupUserPermissions OwnGroupUserPermissions,
    GroupUserPermissions OtherGroupUserPermissions);
