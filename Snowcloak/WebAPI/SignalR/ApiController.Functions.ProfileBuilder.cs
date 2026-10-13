using Microsoft.AspNetCore.SignalR.Client;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Protocol;

namespace Snowcloak.WebAPI;

public partial class ApiController
{
    public bool SupportsProfileBuilder => IsConnected && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.ProfileBuilderV1) == true;
    public bool SupportsProfileCanvas => SupportsProfileBuilder && _connectionContext.Dto?.ServerCapabilities.HasFlag(HubCapability.ProfileCanvasV1) == true;
    public Task<List<ProfileRevisionSummaryDto>> CharacterProfileHistoryList() { CheckConnection(); return _snowHub!.InvokeAsync<List<ProfileRevisionSummaryDto>>(nameof(CharacterProfileHistoryList)); }
    public Task<CharacterProfileDocumentDto> CharacterProfileHistoryGet(long revision) { CheckConnection(); return _snowHub!.InvokeAsync<CharacterProfileDocumentDto>(nameof(CharacterProfileHistoryGet), revision); }
    public Task<CharacterProfileDto> CharacterProfileHistoryRestore(ProfileRevisionRestoreDto dto) { CheckConnection(); return _snowHub!.InvokeAsync<CharacterProfileDto>(nameof(CharacterProfileHistoryRestore), dto); }
    public Task<List<ProfileCatalogueEntryDto>> CharacterProfileCatalogueGet() { CheckConnection(); return _snowHub!.InvokeAsync<List<ProfileCatalogueEntryDto>>(nameof(CharacterProfileCatalogueGet)); }
}
