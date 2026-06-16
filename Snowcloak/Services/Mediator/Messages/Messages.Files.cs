using Snowcloak.PlayerData.Handlers;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record PlayerUploadingMessage(GameObjectHandler Handler, bool IsUploading) : MessageBase;

public record DownloadLimitChangedMessage() : SameThreadMessage;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
